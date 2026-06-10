using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // One piece of town talk. Origin is the town where it FIRST arose; a rumor
    // carried abroad by a journeying NPC keeps its Origin, so a Vesper board can
    // hold "word from Britain" items a traveler brought in.
    public class Rumor
    {
        public string Text;
        public string Origin;
        public DateTime BornUtc;

        public Rumor()
        {
        }

        public Rumor(string text, string origin, DateTime born)
        {
            Text = text;
            Origin = origin;
            BornUtc = born;
        }

        public void Serialize(GenericWriter writer)
        {
            writer.Write((int)0); // rumor version

            writer.Write(Text == null ? "" : Text);
            writer.Write(Origin == null ? "" : Origin);
            writer.Write(BornUtc);
        }

        public void Deserialize(GenericReader reader)
        {
            reader.ReadInt();

            Text = reader.ReadString();
            Origin = reader.ReadString();
            BornUtc = reader.ReadDateTime();
        }
    }

    // P11/P12: a per-town rumor board that makes information actually MOVE
    // through the world, with zero extra LLM calls — rumors are deterministic
    // strings that ride the prompts of systems that already exist.
    //
    // Sources (writes):
    //   - a player saying something salient near an NPC (chance-gated)
    //   - a player dying near a town (EventSink.PlayerDeath)
    //   - a player felling a NOTABLE creature (EventSink.CreatureDeath, fame-gated)
    //   - an NPC being banished by the Overseer (P9 anomaly becomes town legend)
    //   - a journeying NPC arriving somewhere ("X of Britain was seen about town")
    //
    // Spread: when an NPC journeys between towns (P2), it carries the freshest
    // rumors of each board to the other — so what a player tells a smith in
    // Britain can, hours later, come back to them from a tavernkeeper in Vesper.
    //
    // Surfacing (reads): the chat prompts (ambient + custom NPCs) and the P6
    // chatter opener each get a small "talk of the town" block. Boards are
    // capped, rumors expire, and everything persists with LLMAmbientMemory.
    public static class TownGossip
    {
        // At most this many rumors per town; oldest fall off first.
        private const int BoardCap = 12;

        // A rumor older than this is no longer repeated and gets pruned.
        private static readonly TimeSpan RumorTtl = TimeSpan.FromHours(20.0);

        // How many rumors ride a chat prompt, and how many a journeyer carries
        // in each direction between two town boards.
        private const int PromptMax = 2;
        private const int CarryMax = 2;

        // Minimum salient length before a player utterance can become a rumor.
        private const int MinPlayerLine = 20;

        // Creatures below this fame aren't worth talking about. Dragons, liches,
        // daemons and their like clear it; ghouls and mongbats never do.
        private const int NotableFame = 10000;

        // lowercased town -> newest-last rumor list
        private static readonly Dictionary<string, List<Rumor>> m_Boards =
            new Dictionary<string, List<Rumor>>();

        public static void Initialize()
        {
            EventSink.PlayerDeath += EventSink_PlayerDeath;
            EventSink.CreatureDeath += EventSink_CreatureDeath;
        }

        // ---- writes ------------------------------------------------------------

        public static void Add(string town, string text)
        {
            if (string.IsNullOrEmpty(town) || string.IsNullOrEmpty(text))
                return;

            AddRumor(town, new Rumor(text, town, DateTime.UtcNow));
        }

        private static void AddRumor(string town, Rumor r)
        {
            List<Rumor> board = BoardFor(town, true);

            Expire(board, DateTime.UtcNow);

            // The same story doesn't get told onto one board twice.
            for (int i = 0; i < board.Count; i++)
                if (board[i].Text.Equals(r.Text, StringComparison.OrdinalIgnoreCase))
                    return;

            board.Add(r);

            while (board.Count > BoardCap)
                board.RemoveAt(0);
        }

        // A salient player utterance overheard by an NPC may enter the town's
        // talk. Chance-gated so only the occasional line sticks; short greetings
        // never qualify.
        public static void MaybeAddPlayerRumor(Mobile npc, Mobile player, string text)
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.GossipEnabled || npc == null || player == null)
                    return;

                if (string.IsNullOrEmpty(text) || text.Trim().Length < MinPlayerLine)
                    return;

                if (Utility.RandomDouble() >= LLMConfig.GossipChance)
                    return;

                string snippet = Snip(text, 60);
                string who = string.IsNullOrEmpty(player.Name) ? "a traveler" : player.Name;

                Add(BritanniaGeography.TownOf(npc),
                    who + " was heard speaking of \"" + snippet + "\"");
            }
            catch (Exception ex)
            {
                LLMClient.Log("GOSSIP-PLAYER-ERROR " + ex.Message);
            }
        }

        // The P9 crisis ends with the Overseer unmaking an NPC in front of
        // witnesses. The town remembers.
        public static void AddBanishRumor(Mobile npc)
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.GossipEnabled || npc == null || string.IsNullOrEmpty(npc.Name))
                    return;

                string town = BritanniaGeography.TownOf(npc);

                Add(town, npc.Name + " vanished bodily from " + town +
                    " before witnesses, and none can say where");
            }
            catch (Exception ex)
            {
                LLMClient.Log("GOSSIP-BANISH-ERROR " + ex.Message);
            }
        }

        private static void EventSink_PlayerDeath(PlayerDeathEventArgs e)
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.GossipEnabled)
                    return;

                Mobile m = e.Mobile;
                if (m == null || !m.Player || string.IsNullOrEmpty(m.Name))
                    return;

                if (m.Map == null || m.Map == Map.Internal)
                    return;

                string town = BritanniaGeography.TownOf(m);

                Mobile killer = e.Killer;
                if (killer != null && killer != m && !string.IsNullOrEmpty(killer.Name))
                    Add(town, "word is that " + m.Name + " was struck down by " +
                        killer.Name + " near " + town);
                else
                    Add(town, "word is that " + m.Name + " met a grim end near " + town);
            }
            catch (Exception ex)
            {
                LLMClient.Log("GOSSIP-DEATH-ERROR " + ex.Message);
            }
        }

        private static void EventSink_CreatureDeath(CreatureDeathEventArgs e)
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.GossipEnabled)
                    return;

                BaseCreature bc = e.Creature as BaseCreature;
                if (bc == null || bc.Controlled || bc.Summoned || bc.Fame < NotableFame)
                    return;

                if (bc.Map == null || bc.Map == Map.Internal)
                    return;

                // The slayer must trace to a player — directly, or through a pet.
                Mobile killer = e.Killer;
                BaseCreature pet = killer as BaseCreature;
                if (pet != null && pet.Controlled && pet.ControlMaster != null)
                    killer = pet.ControlMaster;

                if (killer == null || !killer.Player || string.IsNullOrEmpty(killer.Name))
                    return;

                string kind = LLMAmbientSpeech.InferCreatureKind(bc);
                string town = BritanniaGeography.TownOf(bc);

                Add(town, killer.Name + " slew a " + kind + " near " + town +
                    ", or so the talk goes");
            }
            catch (Exception ex)
            {
                LLMClient.Log("GOSSIP-KILL-ERROR " + ex.Message);
            }
        }

        // ---- spread (journeying NPCs as carriers) ------------------------------

        // The journeyer just recalled INTO `city` from `fromTown`: it brings its
        // home town's freshest talk with it, and its arrival is itself noticed.
        public static void OnJourneyArrive(BaseCreature npc, string fromTown, string city)
        {
            LLMConfig.EnsureLoaded();

            if (!LLMConfig.GossipEnabled || npc == null)
                return;

            Carry(fromTown, city);

            if (!string.IsNullOrEmpty(npc.Name))
            {
                string voc = LLMAmbientSpeech.InferVocation(npc);
                if (string.IsNullOrEmpty(voc))
                    voc = "traveler";

                AddRumor(city, new Rumor(npc.Name + ", a " + voc + " out of " + fromTown +
                    ", has been seen about town", city, DateTime.UtcNow));
            }
        }

        // The journeyer is recalling home from `city`: it carries that city's
        // talk back with it.
        public static void OnJourneyReturn(string city, string homeTown)
        {
            LLMConfig.EnsureLoaded();

            if (!LLMConfig.GossipEnabled)
                return;

            Carry(city, homeTown);
        }

        // Copies the freshest few rumors of one board onto another, keeping each
        // rumor's original Origin so it reads as "word from <Origin>" abroad.
        private static void Carry(string fromTown, string toTown)
        {
            if (string.IsNullOrEmpty(fromTown) || string.IsNullOrEmpty(toTown))
                return;

            if (fromTown.Equals(toTown, StringComparison.OrdinalIgnoreCase))
                return;

            List<Rumor> src = BoardFor(fromTown, false);
            if (src == null || src.Count == 0)
                return;

            Expire(src, DateTime.UtcNow);

            int carried = 0;
            for (int i = src.Count - 1; i >= 0 && carried < CarryMax; i--)
            {
                Rumor r = src[i];

                // Never re-import a town's own news back onto its board.
                if (r.Origin.Equals(toTown, StringComparison.OrdinalIgnoreCase))
                    continue;

                AddRumor(toTown, new Rumor(r.Text, r.Origin, r.BornUtc));
                carried++;
            }
        }

        // ---- reads -------------------------------------------------------------

        // A prompt-ready block of the freshest talk in this town, or "" if the
        // board is empty. Appended to chat/chatter system prompts.
        public static string PromptBlock(string town)
        {
            LLMConfig.EnsureLoaded();

            if (!LLMConfig.GossipEnabled)
                return "";

            List<string> items = Freshest(town, PromptMax);
            if (items.Count == 0)
                return "";

            StringBuilder sb = new StringBuilder();
            sb.Append(" Lately there is talk about ");
            sb.Append(town);
            sb.Append(": ");

            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append("; also, ");
                sb.Append(items[i]);
            }

            sb.Append(". If conversation turns to news, rumors, or recent doings, you may pass this gossip along naturally — never recite it as a list.");

            return sb.ToString();
        }

        // The single freshest piece of talk, for seeding an NPC-to-NPC exchange.
        public static string PickOne(string town)
        {
            LLMConfig.EnsureLoaded();

            if (!LLMConfig.GossipEnabled)
                return "";

            List<string> items = Freshest(town, 1);
            return items.Count > 0 ? items[0] : "";
        }

        private static List<string> Freshest(string town, int max)
        {
            List<string> result = new List<string>();

            List<Rumor> board = BoardFor(town, false);
            if (board == null)
                return result;

            Expire(board, DateTime.UtcNow);

            for (int i = board.Count - 1; i >= 0 && result.Count < max; i--)
            {
                Rumor r = board[i];

                if (r.Origin.Equals(town, StringComparison.OrdinalIgnoreCase))
                    result.Add(r.Text);
                else
                    result.Add("word from " + r.Origin + " has it that " + r.Text);
            }

            return result;
        }

        // GM-facing dump of one town's board, one line per rumor with its age.
        public static List<string> StatusLines(string town)
        {
            List<string> lines = new List<string>();

            List<Rumor> board = BoardFor(town, false);
            if (board == null || board.Count == 0)
            {
                lines.Add("(no talk in " + town + ")");
                return lines;
            }

            Expire(board, DateTime.UtcNow);

            DateTime now = DateTime.UtcNow;
            for (int i = board.Count - 1; i >= 0; i--)
            {
                Rumor r = board[i];
                int mins = (int)(now - r.BornUtc).TotalMinutes;
                lines.Add("[" + mins + "m, from " + r.Origin + "] " + r.Text);
            }

            return lines;
        }

        // ---- P13: townsfolk notice players -------------------------------------

        // Background-frequency observation: shard-wide spacing plus a long
        // per-player cooldown, then a coin flip — so a player hears about
        // themselves now and then, not every visit.
        private static readonly TimeSpan ObserveGlobalCooldown = TimeSpan.FromSeconds(120.0);
        private static readonly TimeSpan ObservePlayerCooldown = TimeSpan.FromMinutes(40.0);
        private const double ObserveChance = 0.3;

        private static DateTime m_NextObserveUtc = DateTime.MinValue;
        private static readonly Dictionary<int, DateTime> m_NextPlayerObserveUtc =
            new Dictionary<int, DateTime>();

        // A townsperson standing near a player takes notice of them — their
        // blade, their bearing, their reputation — and the impression enters the
        // town's talk. Deterministic templates only; the LLM rephrases naturally
        // when the rumor later rides a chat prompt. Driven off the ErrandDirector
        // heartbeat's per-player scan, so it costs nothing extra.
        public static void MaybeObservePlayer(Mobile player, BaseCreature observer, DateTime now)
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.GossipEnabled || player == null || observer == null)
                    return;

                // Staff walk unseen; the town does not gossip about its keepers.
                if (player.AccessLevel != AccessLevel.Player || string.IsNullOrEmpty(player.Name))
                    return;

                if (now < m_NextObserveUtc)
                    return;

                DateTime nextForPlayer;
                if (m_NextPlayerObserveUtc.TryGetValue(player.Serial.Value, out nextForPlayer) &&
                    now < nextForPlayer)
                    return;

                if (Utility.RandomDouble() >= ObserveChance)
                    return;

                // Reserve both windows up front so a no-aspect player doesn't get
                // re-examined every heartbeat.
                m_NextObserveUtc = now.Add(ObserveGlobalCooldown);
                m_NextPlayerObserveUtc[player.Serial.Value] = now.Add(ObservePlayerCooldown);

                string text = BuildObservation(player);
                if (string.IsNullOrEmpty(text))
                    return;

                Add(BritanniaGeography.TownOf(observer), text);
            }
            catch (Exception ex)
            {
                LLMClient.Log("GOSSIP-OBSERVE-ERROR " + ex.Message);
            }
        }

        // GM test hook: bypass every gate and observe the player right now,
        // boarding the rumor in the observer's town. Returns the text, or "".
        public static string ForceObservation(Mobile player, Mobile observer)
        {
            string text = BuildObservation(player);

            if (!string.IsNullOrEmpty(text))
                Add(BritanniaGeography.TownOf(observer), text);

            return text;
        }

        // One impression, picked at random among whatever aspects apply: a
        // grandmastered skill, reputation (good or ill), fame, the weapon in
        // hand, or heavy plate. Karma thresholds mirror the notoriety system's
        // sense of "known good" and "known trouble".
        private static string BuildObservation(Mobile player)
        {
            string name = player.Name;
            List<string> candidates = new List<string>();

            // Grandmastered skill — folk gush about mastery.
            string skill = BestGrandmasterSkill(player);
            if (skill != null)
            {
                candidates.Add(Pick(new string[]
                {
                    name + " is called a grandmaster of " + skill + ", and folk speak of it in awe",
                    "they say none in the realm can match " + name + " at " + skill,
                    name + "'s skill at " + skill + " is the stuff of tavern boasts"
                }));
            }

            if (player.Karma <= -2500)
            {
                candidates.Add(Pick(new string[]
                {
                    name + " is no friend of honest folk — keep your purse close",
                    "there are dark whispers about " + name + ", and none of them kind",
                    "decent folk cross the street when " + name + " comes walking"
                }));
            }
            else if (player.Karma >= 5000)
            {
                candidates.Add(Pick(new string[]
                {
                    name + " is spoken of as a soul of true virtue",
                    "they say the realm is safer wherever " + name + " walks",
                    name + "'s good deeds are told and retold in the square"
                }));
            }

            if (player.Fame >= 10000 && player.Karma > -2500)
            {
                candidates.Add(Pick(new string[]
                {
                    name + "'s name travels ahead of them these days",
                    "even the children know stories about " + name
                }));
            }

            // The weapon in hand.
            Item held = player.FindItemOnLayer(Layer.OneHanded);
            if (held == null || !(held is BaseWeapon))
                held = player.FindItemOnLayer(Layer.TwoHanded);

            BaseWeapon weapon = held as BaseWeapon;
            if (weapon != null)
            {
                string kind = WeaponKind(weapon);

                candidates.Add(Pick(new string[]
                {
                    name + " looks awful quick with that " + kind,
                    "I'd not care to cross " + name + " while they carry that " + kind,
                    "did you see the " + kind + " " + name + " goes about with?"
                }));
            }
            else
            {
                // No weapon drawn — maybe the armor speaks instead.
                Item chest = player.FindItemOnLayer(Layer.InnerTorso);
                if (chest != null && chest.GetType().Name.IndexOf("Plate", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    candidates.Add(Pick(new string[]
                    {
                        name + " goes about armored like a walking fortress",
                        "you could hear " + name + "'s plate clanking a street away"
                    }));
                }
            }

            if (candidates.Count == 0)
                return "";

            return candidates[Utility.Random(candidates.Count)];
        }

        // The player's highest skill at or past grandmaster (100.0), as a
        // lowercase friendly name, or null if they have none.
        private static string BestGrandmasterSkill(Mobile player)
        {
            if (player.Skills == null)
                return null;

            Skill best = null;

            for (int i = 0; i < player.Skills.Length; i++)
            {
                Skill s = player.Skills[i];

                if (s == null || s.Base < 100.0)
                    continue;

                if (best == null || s.Base > best.Base)
                    best = s;
            }

            if (best == null)
                return null;

            return best.Info.Name.ToLowerInvariant();
        }

        // "Katana" -> "katana", "VikingSword" -> "viking sword" — same humanizing
        // trick the monster prompts use for type names.
        private static string WeaponKind(BaseWeapon weapon)
        {
            string n = weapon.GetType().Name;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(n.Length + 4);

            for (int i = 0; i < n.Length; i++)
            {
                char c = n[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(n[i - 1]))
                    sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString().Trim();
        }

        private static string Pick(string[] pool)
        {
            return pool[Utility.Random(pool.Length)];
        }

        // ---- internals ---------------------------------------------------------

        private static List<Rumor> BoardFor(string town, bool create)
        {
            string key = town.Trim().ToLowerInvariant();

            List<Rumor> board;
            if (m_Boards.TryGetValue(key, out board))
                return board;

            if (!create)
                return null;

            board = new List<Rumor>();
            m_Boards[key] = board;
            return board;
        }

        private static void Expire(List<Rumor> board, DateTime now)
        {
            for (int i = board.Count - 1; i >= 0; i--)
                if ((now - board[i].BornUtc) > RumorTtl)
                    board.RemoveAt(i);
        }

        private static string Snip(string s, int max)
        {
            s = s.Trim();

            if (s.Length <= max)
                return s;

            s = s.Substring(0, max);
            int sp = s.LastIndexOf(' ');
            if (sp > max / 2)
                s = s.Substring(0, sp);

            return s.TrimEnd() + "...";
        }

        // ---- persistence (rides LLMAmbientMemory's save, v2) -------------------

        public static void Serialize(GenericWriter writer)
        {
            writer.Write(m_Boards.Count);

            foreach (KeyValuePair<string, List<Rumor>> kv in m_Boards)
            {
                writer.Write(kv.Key);
                writer.Write(kv.Value.Count);

                for (int i = 0; i < kv.Value.Count; i++)
                    kv.Value[i].Serialize(writer);
            }
        }

        public static void Deserialize(GenericReader reader)
        {
            m_Boards.Clear();

            int towns = reader.ReadInt();
            for (int i = 0; i < towns; i++)
            {
                string key = reader.ReadString();
                int count = reader.ReadInt();

                List<Rumor> board = new List<Rumor>();
                for (int j = 0; j < count; j++)
                {
                    Rumor r = new Rumor();
                    r.Deserialize(reader);
                    board.Add(r);
                }

                m_Boards[key] = board;
            }
        }
    }
}
