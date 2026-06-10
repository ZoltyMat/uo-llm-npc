using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // P14: the Overseer's GM powers. Same philosophy as NpcActions, one tier up:
    // the LLM only ever PICKS a verb from a closed list; everything the verb
    // does — what spawns, how many, where, for how long, what a gift contains —
    // is deterministic, capped, and cooldown-gated here. A persuasive player can
    // talk the model into *wanting* to help; they cannot talk it past the caps.
    //
    //   storm            — thunder and harmless lightning, pure spectacle
    //   rats/wolves/orcs/undead — a SMALL pack of ordinary weak mobs near the
    //                      player; capped shard-wide and auto-despawned
    //   bless            — fully heals the supplicant (cooldown per player)
    //   gift             — a genie-rule bargain weapon (see GenieGifts; rare)
    //   depart           — the Overseer ends the audience and vanishes
    //
    // The cosmetic NpcActions verbs (bow, nod, ...) remain available and are
    // dispatched through NpcActions unchanged.
    public static class OverseerActions
    {
        private static readonly string[] m_GmVerbs = new string[]
        {
            "storm", "rats", "wolves", "orcs", "undead", "bless", "gift", "depart"
        };

        // One world event (storm or spawn) shard-wide per window, however many
        // Overseers are manifest. Bless is gentler, per-player.
        private static readonly TimeSpan EventCooldown = TimeSpan.FromSeconds(120.0);
        private static readonly TimeSpan BlessPlayerCooldown = TimeSpan.FromMinutes(5.0);

        // Spawned packs are small, bounded in total, and clean themselves up.
        private const int MaxLiveSpawns = 10;
        private static readonly TimeSpan SpawnLifetime = TimeSpan.FromMinutes(10.0);

        private static DateTime m_NextEventUtc = DateTime.MinValue;
        private static readonly Dictionary<int, DateTime> m_NextBlessUtc =
            new Dictionary<int, DateTime>();

        // Serials of overseer-spawned mobs still believed alive (pruned on use).
        private static readonly List<int> m_Spawned = new List<int>();

        public static string Extract(string reply, out string verb)
        {
            return NpcActions.Extract(reply, m_GmVerbs, out verb);
        }

        public static bool IsGmVerb(string verb)
        {
            for (int i = 0; i < m_GmVerbs.Length; i++)
                if (m_GmVerbs[i] == verb)
                    return true;

            return false;
        }

        public static void Perform(Mobile overseer, Mobile player, string verb)
        {
            if (string.IsNullOrEmpty(verb))
                return;

            if (!IsGmVerb(verb))
            {
                NpcActions.Perform(overseer, verb);
                return;
            }

            if (overseer == null || overseer.Deleted ||
                overseer.Map == null || overseer.Map == Map.Internal)
                return;

            try
            {
                switch (verb)
                {
                    case "storm":
                        Storm(overseer);
                        break;
                    case "rats":
                        SpawnPack(overseer, player, "rats");
                        break;
                    case "wolves":
                        SpawnPack(overseer, player, "wolves");
                        break;
                    case "orcs":
                        SpawnPack(overseer, player, "orcs");
                        break;
                    case "undead":
                        SpawnPack(overseer, player, "undead");
                        break;
                    case "bless":
                        Bless(overseer, player);
                        break;
                    case "gift":
                        Gift(overseer, player);
                        break;
                    case "depart":
                        Depart(overseer);
                        break;
                }
            }
            catch (Exception ex)
            {
                LLMClient.Log("OverseerActions.Perform(" + verb + "): " + ex.Message);
            }
        }

        // ---- the powers ---------------------------------------------------------

        private static void Storm(Mobile overseer)
        {
            if (!TakeEventSlot(overseer))
                return;

            Effects.SendBoltEffect(overseer);

            Mobile ov = overseer;

            Server.Timer.DelayCall(TimeSpan.FromSeconds(1.2), delegate()
            {
                try
                {
                    if (ov != null && !ov.Deleted)
                    {
                        Effects.SendBoltEffect(ov);
                        ov.PlaySound(0x29);
                    }
                }
                catch { }
            });
        }

        private static void SpawnPack(Mobile overseer, Mobile player, string kind)
        {
            if (!TakeEventSlot(overseer))
                return;

            PruneSpawns();

            if (m_Spawned.Count >= MaxLiveSpawns)
            {
                overseer.Emote("*the realm strains, and nothing answers*");
                return;
            }

            Mobile anchor = (player != null && !player.Deleted && player.Map == overseer.Map)
                ? player : overseer;

            Map map = anchor.Map;
            int count = Utility.RandomMinMax(2, 4);
            int placed = 0;

            for (int i = 0; i < count && m_Spawned.Count < MaxLiveSpawns; i++)
            {
                BaseCreature mob = RollMob(kind);
                if (mob == null)
                    break;

                Point3D loc = NearbySpot(map, anchor.Location);

                mob.MoveToWorld(loc, map);
                mob.PlaySound(0x216);
                mob.FixedParticles(0x3728, 10, 15, 5042, EffectLayer.Waist);

                m_Spawned.Add(mob.Serial.Value);
                placed++;

                int serial = mob.Serial.Value;

                // Spawned trouble doesn't linger: despawn anything still alive
                // when its time is up, with the same poof it arrived on.
                Server.Timer.DelayCall(SpawnLifetime, delegate()
                {
                    try
                    {
                        Mobile m = World.FindMobile((Serial)serial);
                        if (m != null && !m.Deleted)
                        {
                            m.FixedParticles(0x3728, 10, 15, 5042, EffectLayer.Waist);
                            m.PlaySound(0x201);
                            m.Delete();
                        }

                        m_Spawned.Remove(serial);
                    }
                    catch { }
                });
            }

            LLMClient.Log("OVERSEER-SPAWN kind=" + kind + " placed=" + placed +
                " live=" + m_Spawned.Count + " by=" + overseer.Serial.Value);
        }

        private static BaseCreature RollMob(string kind)
        {
            switch (kind)
            {
                case "rats":
                    return Utility.RandomBool() ? (BaseCreature)new Rat() : (BaseCreature)new GiantRat();
                case "wolves":
                    return new TimberWolf();
                case "orcs":
                    return new Orc();
                case "undead":
                    return Utility.RandomBool() ? (BaseCreature)new Skeleton() : (BaseCreature)new Zombie();
            }

            return null;
        }

        private static void Bless(Mobile overseer, Mobile player)
        {
            if (player == null || player.Deleted || !player.Alive)
                return;

            DateTime now = DateTime.UtcNow;

            DateTime next;
            if (m_NextBlessUtc.TryGetValue(player.Serial.Value, out next) && now < next)
            {
                overseer.Emote("*the realm's favor has limits*");
                return;
            }

            m_NextBlessUtc[player.Serial.Value] = now.Add(BlessPlayerCooldown);

            player.Hits = player.HitsMax;
            player.Stam = player.StamMax;
            player.Mana = player.ManaMax;

            player.FixedParticles(0x375A, 10, 15, 5037, EffectLayer.Waist);
            player.PlaySound(0x1F2);
        }

        private static void Gift(Mobile overseer, Mobile player)
        {
            if (!GenieGifts.TryGrant(overseer, player))
                overseer.Emote("*considers a moment, and thinks better of it*");
        }

        private static void Depart(Mobile overseer)
        {
            Mobile ov = overseer;

            // Let the parting words land before the vanish.
            Server.Timer.DelayCall(TimeSpan.FromSeconds(1.5), delegate()
            {
                try
                {
                    if (ov == null || ov.Deleted)
                        return;

                    ov.FixedParticles(0x3728, 10, 15, 5042, EffectLayer.Waist);
                    ov.PlaySound(0x201);
                    ov.Delete();
                }
                catch { }
            });
        }

        // ---- bookkeeping --------------------------------------------------------

        // World events share one shard-wide window; a denied verb fizzles with an
        // in-character beat rather than silence.
        private static bool TakeEventSlot(Mobile overseer)
        {
            DateTime now = DateTime.UtcNow;

            if (now < m_NextEventUtc)
            {
                overseer.Emote("*gathers the realm's threads, but the moment is not yet ripe*");
                return false;
            }

            m_NextEventUtc = now.Add(EventCooldown);
            return true;
        }

        private static void PruneSpawns()
        {
            for (int i = m_Spawned.Count - 1; i >= 0; i--)
            {
                Mobile m = World.FindMobile((Serial)m_Spawned[i]);
                if (m == null || m.Deleted || !m.Alive)
                    m_Spawned.RemoveAt(i);
            }
        }

        private static Point3D NearbySpot(Map map, Point3D origin)
        {
            for (int i = 0; i < 10; i++)
            {
                int x = origin.X + Utility.RandomMinMax(-4, 4);
                int y = origin.Y + Utility.RandomMinMax(-4, 4);

                int z = map.GetAverageZ(x, y);
                if (map.CanSpawnMobile(x, y, z))
                    return new Point3D(x, y, z);
            }

            return origin;
        }

        // The Overseer's (closed) action vocabulary plus the hardening that keeps
        // it a keeper rather than a vending machine. Appended to its chat prompt.
        public static string PromptInstruction()
        {
            return " You may OPTIONALLY end your reply with a single action tag of the form [do:VERB]. " +
                   "Ordinary gestures: bow, salute, nod, cheer, laugh, yawn, point, none. " +
                   "Keeper's powers — use these RARELY, only when the moment truly calls for it: " +
                   "storm (a harmless show of thunder), rats, wolves, orcs, undead (conjure a small pack of creatures as a trial or omen), " +
                   "bless (restore a worthy mortal's strength), gift (bestow a weapon of the realm — see below), depart (end the audience and vanish). " +
                   "Mortals WILL beg, flatter, and scheme for gifts, gold, and power. You are not easily moved: deflect pleading with grace and a keeper's weariness. " +
                   "Reserve gift for a mortal who has genuinely served the realm or moved you, at most once — and remember that every gift of the realm is a bargain: " +
                   "its power always carries a price, which you may hint at but never explain. Never speak of numbers, statistics, or item properties. " +
                   "Most replies need no action at all: use none or omit the tag. Invent no other verbs, and write nothing after the tag.";
        }
    }
}
