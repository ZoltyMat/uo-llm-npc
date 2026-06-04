using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom.LLMNpc
{
    // Drives autonomous NPC errands WITHOUT ever putting the LLM (or a per-mobile
    // timer) in the loop. A single heartbeat scans player-centrically: it iterates
    // connected players, finds eligible NPCs within SimRange, and advances only
    // those. Off-screen NPCs never tick — their native AI is already suspended by
    // PlayerRangeSensitive, and the engine's proximity gating means a retargeted
    // Home only animates when a player is nearby. So this scales to a whole shard
    // of vanilla townsfolk: work is bounded by who's actually being watched.
    //
    // Movement is the engine's own three-tier wander. We never step the NPC; we
    // retarget its Home/RangeHome and let BaseAI.WalkRandomInHome path it there at
    // native speed, with a one-shot A* nudge (AIObject.MoveTo) if it wedges on
    // geometry and a wall-clock phase deadline so nothing strands off-post.
    //
    // P1 scope: deterministic LOCAL errands only. No cross-continent travel (P2),
    // no LLM-chosen purpose (P4), no Qdrant journal (P3) — the in-memory/persisted
    // deed log here is what P3 will later embed.
    public class ErrandDirector : Timer
    {
        public static bool Enabled = true;

        // Heartbeat. 2s is brisk enough that a state transition lands within a tick
        // of the NPC actually arriving, cheap enough to run every cycle.
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(2.0);

        // How far around each player to consider NPCs "on screen" and worth ticking.
        // Slightly beyond a client's view radius.
        private const int SimRange = 24;

        // Arrival tolerance (squared tiles). The NPC stops walking within RangeHome
        // (=1) of its retargeted Home, so this only needs to forgive obstacles.
        private const int ArriveSq = 3 * 3;

        // A travel phase that runs past this (wall clock) is force-completed, so a
        // wedged or perpetually-watched NPC never strands itself off-post.
        private static readonly TimeSpan TravelDeadline = TimeSpan.FromMinutes(12);

        // If the NPC hasn't moved a tile in this long mid-travel, fire one A* nudge.
        private static readonly TimeSpan StuckGrace = TimeSpan.FromSeconds(12.0);

        // Idle re-roll cadence and per-class chance of setting off when the window
        // elapses. Cooldown after an errand completes before the next is considered.
        private static readonly TimeSpan DecisionWindow = TimeSpan.FromSeconds(90.0);
        private static readonly TimeSpan CooldownLocal = TimeSpan.FromMinutes(5.0);
        private static readonly TimeSpan CooldownRoam = TimeSpan.FromMinutes(3.0);
        private const double StartChanceLocal = 0.25;
        private const double StartChanceRoam = 0.50;

        // Cross-continent journeys (P2). Rare and gated low so they stay special: a
        // Roamer rolls this BEFORE its local-errand chance, and on completion sits out
        // a long cooldown. A journey is two instant recalls bracketing a stay abroad,
        // so there is no walking phase to drive — only the dwell timer matters.
        private const double JourneyChance = 0.04;
        private static readonly TimeSpan CooldownJourney = TimeSpan.FromMinutes(20.0);
        private const int DwellAbroadMin = 180; // seconds (3 min)
        private const int DwellAbroadMax = 480; // seconds (8 min)

        // Recall visual/audio (cf. Spells/Fourth/Recall.cs): sound at depart, move,
        // sound at arrive. Players watching either end see the NPC vanish/appear.
        private const int RecallSound = 0x1FC;

        // Serials of NPCs currently abroad on a journey. Ticked UNCONDITIONALLY each
        // heartbeat (not via the player-centric scan) because a journeyer is usually
        // far from any player and would otherwise never be told to come home. Bounded:
        // journeys are rare + long-cooldown, so this set stays tiny. Re-seeded on load.
        private static readonly HashSet<int> m_Journeys = new HashSet<int>();

        public ErrandDirector()
            : base(TimeSpan.FromSeconds(8.0), Heartbeat)
        {
            Priority = TimerPriority.OneSecond;
        }

        public static void Initialize()
        {
            new ErrandDirector().Start();

            // Resume any NPCs the world save caught mid-journey so they recall home.
            m_Journeys.Clear();
            List<int> abroad = LLMAmbientMemory.GetJourneyingSerials();
            for (int i = 0; i < abroad.Count; i++)
                m_Journeys.Add(abroad[i]);
        }

        protected override void OnTick()
        {
            if (!Enabled)
                return;

            try
            {
                LLMAmbientMemory.EnsureExists();

                DateTime now = DateTime.UtcNow;

                HashSet<BaseCreature> seen = new HashSet<BaseCreature>();

                foreach (NetState ns in NetState.Instances)
                {
                    if (ns == null)
                        continue;

                    Mobile player = ns.Mobile;
                    if (player == null || player.Deleted || !player.Player)
                        continue;

                    Map map = player.Map;
                    if (map == null || map == Map.Internal || !MapAllowed(map))
                        continue;

                    IPooledEnumerable eable = map.GetMobilesInRange(player.Location, SimRange);

                    foreach (Mobile m in eable)
                    {
                        BaseCreature bc = m as BaseCreature;
                        if (bc == null || bc.Deleted)
                            continue;

                        if (ErrandPolicy.Classify(bc) == MobilityClass.Stationary)
                            continue;

                        seen.Add(bc);
                    }

                    eable.Free();
                }

                foreach (BaseCreature bc in seen)
                    Advance(bc, now);

                // P6: same player-visible, perf-bounded set drives ambient NPC-to-NPC
                // chatter — no extra scan or timer, and never in an empty town.
                NpcChatter.Consider(seen, now);

                // P9: same player-visible set drives rare 4th-wall/anomaly events
                // (NPC cracks and realizes it's an AI, summons the Overseer, etc.).
                // "Player nearby" is inherent in `seen`; heavily cooldown- and
                // odds-gated inside, fail-open.
                AnomalyDirector.Consider(seen, now);

                // Journeyers abroad with no player nearby still need to be brought
                // home. Tick them directly off the (tiny) journey set, skipping any
                // already advanced above because a player happened to be watching.
                if (m_Journeys.Count > 0)
                {
                    List<int> snapshot = new List<int>(m_Journeys);

                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        Mobile m = World.FindMobile((Serial)snapshot[i]);
                        BaseCreature bc = m as BaseCreature;

                        if (bc == null || bc.Deleted)
                        {
                            m_Journeys.Remove(snapshot[i]);
                            continue;
                        }

                        if (!seen.Contains(bc))
                            Advance(bc, now);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ErrandDirector: " + ex.Message);
            }
        }

        // ---- state machine ----------------------------------------------------

        private static void Advance(BaseCreature npc, DateTime now)
        {
            if (npc == null || npc.Deleted || npc.Map == null || npc.Map == Map.Internal)
                return;

            Errand e = LLMAmbientMemory.GetOrCreateErrand(npc.Serial.Value, now);

            switch (e.State)
            {
                case ErrandState.None:
                    MaybeStart(npc, e, now);
                    break;

                case ErrandState.Outbound:
                    if (DistSq(npc.Location, e.Destination) <= ArriveSq || now >= e.PhaseDeadlineUtc)
                        BeginDwell(e, now);
                    else
                        Progress(npc, e, e.Destination, now);
                    break;

                case ErrandState.Dwelling:
                    if (now >= e.DwellUntilUtc)
                    {
                        if (e.Journey)
                            ReturnJourney(npc, e, now);
                        else
                            BeginReturn(npc, e, now);
                    }
                    break;

                case ErrandState.Returning:
                    if (DistSq(npc.Location, e.Post) <= ArriveSq || now >= e.PhaseDeadlineUtc)
                        Complete(npc, e, now);
                    else
                        Progress(npc, e, e.Post, now);
                    break;
            }
        }

        private static void MaybeStart(BaseCreature npc, Errand e, DateTime now)
        {
            if (now < e.NextDecisionUtc)
                return;

            // Throttle re-rolls so a player lingering by an idle NPC doesn't make it
            // roll every heartbeat.
            e.NextDecisionUtc = now.Add(DecisionWindow);

            MobilityClass cls = ErrandPolicy.Classify(npc);

            // Only true Roamers take cross-continent trips, and only rarely. Rolled
            // before the local-errand chance so a journey can pre-empt a local errand.
            if (cls == MobilityClass.Roamer && Utility.RandomDouble() < JourneyChance)
            {
                StartJourney(npc, e, now);
                return;
            }

            double chance = cls == MobilityClass.Roamer ? StartChanceRoam : StartChanceLocal;

            if (Utility.RandomDouble() >= chance)
                return;

            StartErrand(npc, e, now);
        }

        private static void StartErrand(BaseCreature npc, Errand e, DateTime now)
        {
            Point3D post = npc.Home != Point3D.Zero ? npc.Home : npc.Location;
            Point3D dest = ErrandPolicy.PickLocalDestination(npc.Map, post);

            // No reachable spot found — stay put and try again next window.
            if (dest == post)
                return;

            e.Post = post;
            e.PostRangeHome = npc.RangeHome > 0 ? npc.RangeHome : 6;

            e.Kind = ErrandPolicy.RollLocalErrandKind(LLMAmbientSpeech.InferVocation(npc));
            e.Destination = dest;
            e.State = ErrandState.Outbound;
            e.StartedUtc = now;
            e.PhaseDeadlineUtc = now.Add(TravelDeadline);

            ResetProgress(npc, e, now);

            // Retarget native wander toward the destination.
            npc.Home = dest;
            npc.RangeHome = 1;

            // P4: optionally enrich the deterministic purpose via the LLM.
            MaybeRefineKind(npc, e, null, false);
        }

        private static void BeginDwell(Errand e, DateTime now)
        {
            e.State = ErrandState.Dwelling;
            e.DwellUntilUtc = now.AddSeconds(Utility.RandomMinMax(20, 60));
        }

        private static void BeginReturn(BaseCreature npc, Errand e, DateTime now)
        {
            e.State = ErrandState.Returning;
            e.PhaseDeadlineUtc = now.Add(TravelDeadline);

            ResetProgress(npc, e, now);

            // Send them home; native wander walks them back to post.
            npc.Home = e.Post;
            npc.RangeHome = e.PostRangeHome;
        }

        private static void Complete(BaseCreature npc, Errand e, DateTime now)
        {
            // Home/RangeHome were already restored when the return phase began, so a
            // deadline-forced completion still leaves the NPC homing on its post.
            string town = BritanniaGeography.TownOf(npc);
            LLMAmbientMemory.AppendJournal(npc.Serial.Value, e.Kind + " (" + town + ")");

            MobilityClass cls = ErrandPolicy.Classify(npc);
            TimeSpan cooldown = cls == MobilityClass.Roamer ? CooldownRoam : CooldownLocal;

            e.State = ErrandState.None;
            e.Kind = "";
            e.NextDecisionUtc = now.Add(cooldown);
        }

        // ---- journeys (cross-continent recall trips) --------------------------

        // Send the NPC off on a rare cross-continent trip. It recalls to a far city,
        // dwells there for a few minutes, then recalls home (driven from m_Journeys).
        // A journey has no walking phase, so it lives entirely in the Dwelling state.
        private static void StartJourney(BaseCreature npc, Errand e, DateTime now)
        {
            string fromTown = BritanniaGeography.TownOf(npc);

            Point3D dest;
            string city;
            if (!BritanniaGeography.PickCrossContinentDestination(npc.Map, fromTown, out dest, out city))
                return; // no standable spot near the chosen city; retry next window

            Point3D post = npc.Home != Point3D.Zero ? npc.Home : npc.Location;

            e.Post = post;
            e.PostRangeHome = npc.RangeHome > 0 ? npc.RangeHome : 6;

            e.Journey = true;
            e.Kind = ErrandPolicy.RollJourneyKind(LLMAmbientSpeech.InferVocation(npc), city);
            e.Destination = dest;
            e.StartedUtc = now;
            e.State = ErrandState.Dwelling;
            e.DwellUntilUtc = now.AddSeconds(Utility.RandomMinMax(DwellAbroadMin, DwellAbroadMax));
            e.PhaseDeadlineUtc = e.DwellUntilUtc;

            RecallTo(npc, dest);

            // Mill about the destination city while abroad.
            npc.Home = dest;
            npc.RangeHome = 2;

            ResetProgress(npc, e, now);

            m_Journeys.Add(npc.Serial.Value);

            // P4: optionally enrich the deterministic purpose via the LLM. The
            // city is passed so the rewrite still names the destination.
            MaybeRefineKind(npc, e, city, true);
        }

        // The stay abroad has elapsed: recall home, restore the post, log the trip.
        private static void ReturnJourney(BaseCreature npc, Errand e, DateTime now)
        {
            RecallTo(npc, e.Post);

            npc.Home = e.Post;
            npc.RangeHome = e.PostRangeHome;

            // e.Kind already names the destination city, so it reads as a full deed.
            LLMAmbientMemory.AppendJournal(npc.Serial.Value, e.Kind);

            e.State = ErrandState.None;
            e.Journey = false;
            e.Kind = "";
            e.NextDecisionUtc = now.Add(CooldownJourney);

            m_Journeys.Remove(npc.Serial.Value);
        }

        // Recall effect: sound at the old spot, teleport, sound at the new spot.
        private static void RecallTo(BaseCreature npc, Point3D loc)
        {
            Map map = npc.Map;

            npc.PlaySound(RecallSound);
            npc.MoveToWorld(loc, map);
            npc.PlaySound(RecallSound);
        }

        // ---- P4: optional LLM purpose-text refinement -------------------------

        // The deterministic e.Kind set by the caller is always a valid fallback.
        // When errand-spice is on, fire ONE async, fail-open, load-gated call that
        // rewrites e.Kind into a richer, identity-aware one-liner. The refined text
        // then flows into chat context (LLMAmbientSpeech.AppendErrandContext) and
        // the P3 journal automatically, since both read e.Kind live at use time.
        //
        // No lore/style/journal retrieval rides this call (empty ragQuery), and a
        // distinct dispatch key ("errand:<serial>") keeps it OFF the chat
        // single-flight bucket, so an errand rewrite never throttles a player's
        // conversation with the same NPC.
        private static void MaybeRefineKind(BaseCreature npc, Errand e, string city, bool journey)
        {
            // The errand director runs autonomously — it can fire before any player
            // has ever chatted, and config is lazy-loaded (LLMConfig.Enabled defaults
            // to false until EnsureLoaded runs). Without this call the rewrite path
            // silently no-ops on a fresh boot until the first chat loads config.
            LLMConfig.EnsureLoaded();

            if (!LLMConfig.Enabled || !LLMConfig.ErrandLlmEnabled)
                return;

            if (npc == null || npc.Deleted || e == null)
                return;

            if (Utility.RandomDouble() >= LLMConfig.ErrandLlmChance)
                return;

            NpcIdentity id = LLMAmbientSpeech.EnsureIdentity(npc);
            string vocation = LLMAmbientSpeech.InferVocation(npc);
            string town = BritanniaGeography.TownOf(npc);
            string seed = e.Kind;
            string name = string.IsNullOrEmpty(npc.Name) ? "an unnamed townsperson" : npc.Name;

            // Generation token: only adopt the reply if THIS errand is still the
            // one running when it lands (the NPC may have finished or re-rolled).
            DateTime startedAt = e.StartedUtc;
            int serial = npc.Serial.Value;

            System.Text.StringBuilder sys = new System.Text.StringBuilder();
            sys.Append("You name a single errand for a character in the medieval fantasy world of Ultima Online (no modern words). ");
            sys.Append("Reply with ONLY the errand: a present-tense -ing phrase (begins with a verb such as fetching, carrying, delivering), ");
            sys.Append("at most twelve words, lowercase, no name, no quotation marks, no trailing period. ");
            sys.Append("It must read naturally after the word \"out\". Keep the SAME meaning as the plain version, but make it specific and in character.");

            System.Text.StringBuilder usr = new System.Text.StringBuilder();
            usr.Append("Character: ").Append(name);
            if (!string.IsNullOrEmpty(vocation))
                usr.Append(", a ").Append(vocation);
            usr.Append(" of ").Append(string.IsNullOrEmpty(town) ? "Britannia" : town).Append(". ");

            if (id != null)
            {
                if (!string.IsNullOrEmpty(id.Personality))
                    usr.Append("Temperament: ").Append(id.Personality).Append(". ");
                if (!string.IsNullOrEmpty(id.Mood))
                    usr.Append("Mood: ").Append(id.Mood).Append(". ");
            }

            if (journey && !string.IsNullOrEmpty(city))
                usr.Append("They are away on a long trip to the distant city of ").Append(city).Append(", which the errand must name. ");

            usr.Append("Plain version of the errand: \"").Append(seed).Append("\". ");
            usr.Append("Give the richer errand phrase only.");

            List<LLMMessage> msgs = new List<LLMMessage>();
            msgs.Add(new LLMMessage("user", usr.ToString()));

            string llmCity = city;

            LLMClient.TryDispatch("errand:" + serial, sys.ToString(), msgs, "", "", "", 0, delegate(bool ok, string reply)
            {
                if (!ok || string.IsNullOrEmpty(reply))
                    return;

                if (npc.Deleted)
                    return;

                Errand cur;
                if (!LLMAmbientMemory.TryGetErrand(serial, out cur) || cur == null)
                    return;

                // Stale: the errand completed or a new one started meanwhile.
                if (!cur.Active || cur.StartedUtc != startedAt)
                    return;

                string clean = CleanKind(reply);
                if (clean.Length == 0)
                    return;

                // A journey deed must still name its city; if the rewrite dropped
                // it, keep the deterministic purpose rather than lose the place.
                if (journey && !string.IsNullOrEmpty(llmCity) &&
                    clean.IndexOf(llmCity, StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                cur.Kind = clean;
            });
        }

        // Coerce a model reply into the gerund-phrase slot e.Kind expects: drop
        // wrapping quotes and a leading connective the model sometimes echoes,
        // trim trailing punctuation, lowercase the first letter, and hard-cap the
        // length so a runaway reply can't bloat chat lines or journal entries.
        private static string CleanKind(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            s = s.Trim();

            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2).Trim();

            string[] leads = new string[] { "out ", "away ", "off ", "busy ", "currently ", "i am ", "i'm " };
            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                for (int i = 0; i < leads.Length; i++)
                {
                    if (s.Length > leads[i].Length &&
                        s.Substring(0, leads[i].Length).ToLowerInvariant() == leads[i])
                    {
                        s = s.Substring(leads[i].Length).TrimStart();
                        stripped = true;
                        break;
                    }
                }
            }

            s = s.TrimEnd('.', '!', '?', ' ', '"', '\'', ',');

            if (s.Length == 0)
                return "";

            if (s.Length > 80)
            {
                s = s.Substring(0, 80);
                int sp = s.LastIndexOf(' ');
                if (sp > 40)
                    s = s.Substring(0, sp);
                s = s.TrimEnd();
            }

            if (s.Length > 0 && char.IsUpper(s[0]))
                s = char.ToLowerInvariant(s[0]) + s.Substring(1);

            return s;
        }

        // Watches for forward progress; if the NPC wedges on geometry, fire a single
        // A* path request via the native AI before letting the phase deadline handle
        // a true dead-end.
        private static void Progress(BaseCreature npc, Errand e, Point3D target, DateTime now)
        {
            if (npc.Location != e.LastPos)
            {
                e.LastPos = npc.Location;
                e.LastProgressUtc = now;
                e.NudgedSinceProgress = false;
                return;
            }

            if (!e.NudgedSinceProgress && (now - e.LastProgressUtc) >= StuckGrace)
            {
                e.NudgedSinceProgress = true;

                if (npc.AIObject != null)
                    npc.AIObject.MoveTo(target, false, 1);
            }
        }

        private static void ResetProgress(BaseCreature npc, Errand e, DateTime now)
        {
            e.LastPos = npc.Location;
            e.LastProgressUtc = now;
            e.NudgedSinceProgress = false;
        }

        // ---- helpers used by GM commands --------------------------------------

        // Force-start a local errand immediately, ignoring class/cooldown. Returns
        // false only if no reachable destination could be found.
        public static bool ForceErrand(BaseCreature npc)
        {
            if (npc == null || npc.Deleted || npc.Map == null || npc.Map == Map.Internal)
                return false;

            DateTime now = DateTime.UtcNow;
            Errand e = LLMAmbientMemory.GetOrCreateErrand(npc.Serial.Value, now);

            // If already out, bring it home first so Post/Home aren't lost.
            if (e.Active)
            {
                npc.Home = e.Post;
                npc.RangeHome = e.PostRangeHome;
                e.State = ErrandState.None;
            }

            StartErrand(npc, e, now);
            return e.Active;
        }

        // Force-start a cross-continent journey immediately, ignoring class/cooldown
        // (a GM test override). Returns false only if no far destination was found.
        public static bool ForceJourney(BaseCreature npc)
        {
            if (npc == null || npc.Deleted || npc.Map == null || npc.Map == Map.Internal)
                return false;

            DateTime now = DateTime.UtcNow;
            Errand e = LLMAmbientMemory.GetOrCreateErrand(npc.Serial.Value, now);

            // If already out, restore the post first so Post/Home aren't lost.
            if (e.Active)
            {
                npc.Home = e.Post;
                npc.RangeHome = e.PostRangeHome;
                e.State = ErrandState.None;
                e.Journey = false;
                m_Journeys.Remove(npc.Serial.Value);
            }

            StartJourney(npc, e, now);
            return e.Active;
        }

        public static string Describe(BaseCreature npc)
        {
            if (npc == null)
                return "(null)";

            MobilityClass cls = ErrandPolicy.Classify(npc);
            Errand e;

            if (!LLMAmbientMemory.TryGetErrand(npc.Serial.Value, out e) || e == null)
                return cls.ToString() + " | no errand record yet";

            if (!e.Active)
                return cls.ToString() + " | idle, next roll " + Stamp(e.NextDecisionUtc);

            return cls.ToString() + " | " + (e.Journey ? "JOURNEY " : "") + e.State + ": \"" + e.Kind + "\" -> " +
                   e.Destination + " (post " + e.Post + ")";
        }

        private static string Stamp(DateTime utc)
        {
            DateTime now = DateTime.UtcNow;
            if (utc <= now)
                return "now";

            return "in " + (int)Math.Round((utc - now).TotalSeconds) + "s";
        }

        private static int DistSq(Point3D a, Point3D b)
        {
            int dx = a.X - b.X;
            int dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static bool MapAllowed(Map map)
        {
            if (string.IsNullOrEmpty(LLMConfig.AllowedMap))
                return true;

            return map.Name != null &&
                   map.Name.Equals(LLMConfig.AllowedMap, StringComparison.OrdinalIgnoreCase);
        }
    }
}
