using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Items;
using Server.Misc;
using Server.Mobiles;
using Server.Targeting;

namespace Server.Custom.LLMNpc
{
    // P15: favors — the errand system inverted. NPCs run their own errands;
    // now a townsperson who cannot leave their post may entrust a passing
    // player with a sealed parcel for the banker (or smith, or tavernkeeper)
    // of another town — or across their own.
    //
    // Division of labor, as everywhere in this codebase: the favor itself is
    // DETERMINISTIC — FavorDirector rolls the destination, reward, and
    // eligibility, and registers a short-lived "pending" offer; the LLM only
    // decides whether the social moment is right, by ending its reply with
    // [do:offer] (the prompt block tells it the errand exists). A player who
    // ASKS for work makes the offer certain; a model that never emits the tag
    // just lets the pending offer lapse. The parcel is a real item that
    // carries its own quest spec, so nothing else needs serializing.
    //
    // Completion threads back through everything else built here: gold scaled
    // by distance, a karma award, a disposition bump with the giver, journal
    // deeds for giver and receiver, and PRAISE RUMORS on both towns' boards —
    // doing favors is how a player builds the reputation that P13 townsfolk
    // then gossip about.
    public static class FavorDirector
    {
        // Extra verb the chat-path extractor recognizes alongside the cosmetic
        // set (only ever honored when a matching pending offer exists).
        public static readonly string[] OfferVerbs = new string[] { "offer" };

        // How long a rolled offer stays open for the LLM/player to land it.
        private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5.0);

        // Background frequency: per-player and per-NPC spacing, plus a roll —
        // except that asking for work ("any work?", "need a hand?") makes an
        // eligible NPC offer every time.
        private static readonly TimeSpan PlayerCooldown = TimeSpan.FromMinutes(20.0);
        private static readonly TimeSpan NpcCooldown = TimeSpan.FromMinutes(10.0);

        // A player carries at most this many undelivered parcels.
        private const int MaxParcels = 2;

        private static readonly string[] m_WorkWords = new string[]
        {
            "work", "task", "errand", "favor", "favour", "job", "quest", "help you", "deliver"
        };

        // Destination vocations — present in every classic town, banker most
        // reliably of all (hence the bias).
        private static readonly string[] m_TargetVocs = new string[]
        {
            "banker", "banker", "blacksmith", "tavernkeeper"
        };

        private class Pending
        {
            public int PlayerSerial;
            public string TargetVoc;
            public string TargetTown;
            public int Gold;
            public DateTime ExpiresUtc;
        }

        private static readonly Dictionary<int, Pending> m_Pending = new Dictionary<int, Pending>();
        private static readonly Dictionary<int, DateTime> m_NextPlayerUtc = new Dictionary<int, DateTime>();
        private static readonly Dictionary<int, DateTime> m_NextNpcUtc = new Dictionary<int, DateTime>();

        // ---- offer lifecycle ---------------------------------------------------

        // Called from the chat paths BEFORE the prompt is built, so a fresh
        // pending offer can ride this very exchange. Gated hard; fail-quiet.
        public static void MaybeCreatePending(Mobile npc, Mobile player, string text)
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.Enabled || !LLMConfig.FavorEnabled)
                    return;

                if (npc == null || player == null || npc.Deleted || player.Deleted)
                    return;

                BaseCreature bc = npc as BaseCreature;
                if (bc == null || !bc.Body.IsHuman || bc.Karma < 0 ||
                    bc.Controlled || bc.Summoned || bc is LLMOverseer)
                    return;

                DateTime now = DateTime.UtcNow;

                Pending existing;
                if (m_Pending.TryGetValue(npc.Serial.Value, out existing) && now < existing.ExpiresUtc)
                    return; // this NPC already has an open offer

                DateTime next;
                if (m_NextNpcUtc.TryGetValue(npc.Serial.Value, out next) && now < next)
                    return;

                if (m_NextPlayerUtc.TryGetValue(player.Serial.Value, out next) && now < next)
                    return;

                if (CountParcels(player) >= MaxParcels)
                    return;

                // Asking for work always earns an offer; otherwise a quiet roll.
                if (!MentionsWork(text) && Utility.RandomDouble() >= LLMConfig.FavorChance)
                    return;

                CreatePending(npc, player, now);
            }
            catch (Exception ex)
            {
                LLMClient.Log("FAVOR-PENDING-ERROR " + ex.Message);
            }
        }

        // GM test hook: force a pending offer for this player, skipping every
        // gate. The NPC offers on its next reply (ask it for work).
        public static string ForcePending(Mobile npc, Mobile player)
        {
            Pending p = CreatePending(npc, player, DateTime.UtcNow);
            return p.TargetVoc + " of " + p.TargetTown + " (~" + p.Gold + "gp)";
        }

        private static Pending CreatePending(Mobile npc, Mobile player, DateTime now)
        {
            string giverTown = BritanniaGeography.TownOf(npc);

            // Half the favors stay local; half cross the realm.
            string targetTown = Utility.RandomBool()
                ? giverTown
                : BritanniaGeography.RandomCityExcept(giverTown);

            Pending p = new Pending();
            p.PlayerSerial = player.Serial.Value;
            p.TargetVoc = m_TargetVocs[Utility.Random(m_TargetVocs.Length)];
            p.TargetTown = targetTown;
            p.Gold = RewardFor(npc, targetTown, giverTown);
            p.ExpiresUtc = now.Add(PendingLifetime);

            m_Pending[npc.Serial.Value] = p;
            m_NextNpcUtc[npc.Serial.Value] = now.Add(NpcCooldown);
            m_NextPlayerUtc[player.Serial.Value] = now.Add(PlayerCooldown);

            return p;
        }

        // Distance-scaled: a parcel across town pays pocket change, one across
        // the realm pays for the road.
        private static int RewardFor(Mobile npc, string targetTown, string giverTown)
        {
            if (targetTown.Equals(giverTown, StringComparison.OrdinalIgnoreCase))
                return Utility.RandomMinMax(80, 120);

            Point3D center;
            if (!BritanniaGeography.TryGetCityCenter(targetTown, out center))
                return 200;

            int dx = npc.X - center.X;
            int dy = npc.Y - center.Y;
            int dist = (int)Math.Sqrt((double)dx * dx + (double)dy * dy);

            int gold = 150 + dist / 6;
            if (gold > 600)
                gold = 600;

            return gold;
        }

        // Appended to the chat prompt when this NPC holds an open offer for
        // this player — tells the model the errand exists and how to extend it.
        public static string PromptBlock(int npcSerial, int playerSerial)
        {
            Pending p;
            if (!m_Pending.TryGetValue(npcSerial, out p))
                return "";

            if (p.PlayerSerial != playerSerial || DateTime.UtcNow >= p.ExpiresUtc)
                return "";

            StringBuilder sb = new StringBuilder();
            sb.Append(" You have been meaning to send a sealed parcel to the ");
            sb.Append(p.TargetVoc);
            sb.Append(" of ");
            sb.Append(p.TargetTown);
            sb.Append(", but you cannot leave your post. If the traveler seems willing, trustworthy, or asks for work, offer them the errand — name the destination and a reward of about ");
            sb.Append(p.Gold);
            sb.Append(" gold on delivery — and end your reply with [do:offer]. If they seem hostile or dismissive, do not offer, and do not use the tag.");

            return sb.ToString();
        }

        // Consumes an [do:offer] tag. Returns true when the verb was "offer"
        // (whether or not it was honored), so cosmetic dispatch is skipped.
        public static bool TryPerformOffer(Mobile npc, Mobile player, string verb)
        {
            if (verb != "offer")
                return false;

            try
            {
                Pending p;
                if (npc == null || player == null ||
                    !m_Pending.TryGetValue(npc.Serial.Value, out p) ||
                    p.PlayerSerial != player.Serial.Value ||
                    DateTime.UtcNow >= p.ExpiresUtc)
                    return true; // hallucinated/stale tag — swallow it

                m_Pending.Remove(npc.Serial.Value);

                if (CountParcels(player) >= MaxParcels)
                    return true;

                string giverTown = BritanniaGeography.TownOf(npc);

                FavorParcel parcel = new FavorParcel(
                    npc.Serial.Value,
                    string.IsNullOrEmpty(npc.Name) ? "a townsperson" : npc.Name,
                    giverTown, p.TargetVoc, p.TargetTown, p.Gold);

                player.AddToBackpack(parcel);

                npc.Emote("*hands over a sealed parcel*");
                player.SendMessage(0x3F, "{0} entrusts you with a sealed parcel for the {1} of {2}. ({3} gold promised on delivery — double-click the parcel beside its recipient.)",
                    npc.Name, p.TargetVoc, p.TargetTown, p.Gold);

                LLMAmbientMemory.AppendJournal(npc.Serial.Value,
                    "entrusted a parcel bound for " + p.TargetTown + " to a traveler named " +
                    (string.IsNullOrEmpty(player.Name) ? "a stranger" : player.Name) + " (" + giverTown + ")");

                LLMClient.Log("FAVOR-OFFER npc=" + npc.Serial.Value + " player=" + player.Serial.Value +
                    " -> " + p.TargetVoc + "@" + p.TargetTown + " gold=" + p.Gold);
            }
            catch (Exception ex)
            {
                LLMClient.Log("FAVOR-OFFER-ERROR " + ex.Message);
            }

            return true;
        }

        // ---- delivery -----------------------------------------------------------

        // Validates a delivery attempt and, on success, pays out and threads
        // the favor back through karma, memory, and the gossip boards.
        public static void TryDeliver(FavorParcel parcel, Mobile player, Mobile target)
        {
            if (parcel == null || parcel.Deleted || player == null || target == null)
                return;

            BaseCreature bc = target as BaseCreature;

            if (bc == null || !bc.Alive || bc.Controlled || bc.Summoned)
            {
                player.SendMessage(0x22, "That is no one to leave a parcel with.");
                return;
            }

            string voc = LLMAmbientSpeech.InferVocation(target);
            string town = BritanniaGeography.TownOf(target);

            if (!voc.Equals(parcel.TargetVoc, StringComparison.OrdinalIgnoreCase) ||
                !town.Equals(parcel.TargetTown, StringComparison.OrdinalIgnoreCase))
            {
                player.SendMessage(0x22, "The parcel is addressed to the {0} of {1}.",
                    parcel.TargetVoc, parcel.TargetTown);
                return;
            }

            if (!player.InRange(target.Location, 4))
            {
                player.SendMessage(0x22, "You are too far away to hand it over.");
                return;
            }

            // Paid in full, in coin and in standing.
            player.AddToBackpack(new Gold(parcel.RewardGold));
            Titles.AwardKarma(player, 25, true);

            target.Say("My thanks, traveler — this was long awaited.");
            target.PlaySound(0x3D);

            string playerName = string.IsNullOrEmpty(player.Name) ? "a stranger" : player.Name;

            // The recipient now thinks a little better of them, and remembers.
            NpcRelationship rel = LLMAmbientMemory.GetOrCreateRelationship(
                target.Serial.Value, player.Serial.Value);
            BumpDisposition(rel, 12);

            LLMAmbientMemory.AppendJournal(target.Serial.Value,
                "received a parcel from " + parcel.GiverName + " of " + parcel.GiverTown +
                ", carried by " + playerName + " (" + town + ")");

            // The giver's regard rises too, even from afar.
            Mobile giver = World.FindMobile((Serial)parcel.GiverSerial);
            if (giver != null && !giver.Deleted)
            {
                LLMTalkingMobile talker = giver as LLMTalkingMobile;
                if (talker != null)
                    talker.RecordFavor(player);
                else
                    BumpDisposition(LLMAmbientMemory.GetOrCreateRelationship(
                        giver.Serial.Value, player.Serial.Value), 15);
            }

            // P11/P13 payoff: the deed enters BOTH towns' talk as praise.
            TownGossip.Add(town, playerName + " carried a parcel all the way from " +
                parcel.GiverTown + " and saw it safely delivered");
            TownGossip.Add(parcel.GiverTown, parcel.GiverName + "'s parcel reached " +
                parcel.TargetTown + " safely — " + playerName + " saw to it");

            LLMClient.Log("FAVOR-DONE player=" + player.Serial.Value + " from=" + parcel.GiverTown +
                " to=" + parcel.TargetVoc + "@" + parcel.TargetTown + " gold=" + parcel.RewardGold);

            parcel.Delete();
        }

        private static void BumpDisposition(NpcRelationship rel, int amount)
        {
            rel.Disposition += amount;
            if (rel.Disposition > 100)
                rel.Disposition = 100;
        }

        // ---- helpers ------------------------------------------------------------

        private static int CountParcels(Mobile player)
        {
            if (player.Backpack == null)
                return 0;

            int n = 0;
            List<Item> items = player.Backpack.Items;

            for (int i = 0; i < items.Count; i++)
                if (items[i] is FavorParcel)
                    n++;

            return n;
        }

        private static bool MentionsWork(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string t = text.ToLowerInvariant();

            for (int i = 0; i < m_WorkWords.Length; i++)
                if (t.IndexOf(m_WorkWords[i], StringComparison.Ordinal) >= 0)
                    return true;

            return false;
        }
    }

    // The parcel itself: a blessed item that carries its whole quest spec, so
    // favors survive logout/reboot with no registry. Double-click it beside the
    // addressee to deliver.
    public class FavorParcel : Item
    {
        private int m_GiverSerial;
        private string m_GiverName;
        private string m_GiverTown;
        private string m_TargetVoc;
        private string m_TargetTown;
        private int m_RewardGold;

        public int GiverSerial { get { return m_GiverSerial; } }
        public string GiverName { get { return m_GiverName; } }
        public string GiverTown { get { return m_GiverTown; } }
        public string TargetVoc { get { return m_TargetVoc; } }
        public string TargetTown { get { return m_TargetTown; } }
        public int RewardGold { get { return m_RewardGold; } }

        public FavorParcel(int giverSerial, string giverName, string giverTown,
            string targetVoc, string targetTown, int rewardGold)
            : base(0x2DF3) // small wrapped package
        {
            m_GiverSerial = giverSerial;
            m_GiverName = giverName;
            m_GiverTown = giverTown;
            m_TargetVoc = targetVoc;
            m_TargetTown = targetTown;
            m_RewardGold = rewardGold;

            Name = "a sealed parcel for the " + targetVoc + " of " + targetTown;
            Hue = 0x481;          // the realm's violet — a favor is half the Overseer's business anyway
            LootType = LootType.Blessed; // griefers shouldn't be able to cost you the errand
            Weight = 1.0;
        }

        public FavorParcel(Serial serial)
            : base(serial)
        {
        }

        public override void OnDoubleClick(Mobile from)
        {
            if (!IsChildOf(from.Backpack))
            {
                from.SendMessage(0x22, "The parcel must be in your pack to deliver it.");
                return;
            }

            from.SendMessage(0x3F, "Deliver the parcel to whom? (It is addressed to the {0} of {1}.)",
                m_TargetVoc, m_TargetTown);
            from.Target = new DeliverTarget(this);
        }

        private class DeliverTarget : Target
        {
            private readonly FavorParcel m_Parcel;

            public DeliverTarget(FavorParcel parcel)
                : base(6, false, TargetFlags.None)
            {
                m_Parcel = parcel;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                Mobile m = targeted as Mobile;

                if (m == null)
                {
                    from.SendMessage(0x22, "That is no one to leave a parcel with.");
                    return;
                }

                FavorDirector.TryDeliver(m_Parcel, from, m);
            }
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write((int)0); // version

            writer.Write(m_GiverSerial);
            writer.Write(m_GiverName == null ? "" : m_GiverName);
            writer.Write(m_GiverTown == null ? "" : m_GiverTown);
            writer.Write(m_TargetVoc == null ? "" : m_TargetVoc);
            writer.Write(m_TargetTown == null ? "" : m_TargetTown);
            writer.Write(m_RewardGold);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            reader.ReadInt();

            m_GiverSerial = reader.ReadInt();
            m_GiverName = reader.ReadString();
            m_GiverTown = reader.ReadString();
            m_TargetVoc = reader.ReadString();
            m_TargetTown = reader.ReadString();
            m_RewardGold = reader.ReadInt();
        }
    }
}
