using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // One scheduled activity in an NPC's day, pinned to a game hour.
    public class DayLeg
    {
        public int Hour;         // game hour 0-23 the leg becomes due
        public string Activity;  // errand | tavern | bank | market | stroll
        public string Purpose;   // in-world label, flows into e.Kind
        public bool Done;

        public DayLeg(int hour, string activity, string purpose)
        {
            Hour = hour;
            Activity = activity;
            Purpose = purpose;
        }
    }

    // One NPC's plan for one game day (a UO day is ~2 real hours, so players in
    // a session actually see morning turn to evening and routines repeat).
    public class DayPlan
    {
        public int Day;            // absolute game-day stamp this plan covers
        public string Intention;   // optional LLM-written private aim for the day
        public List<DayLeg> Legs;

        public DayPlan(int day)
        {
            Day = day;
            Legs = new List<DayLeg>();
        }
    }

    // P10: daily routines. Instead of only rolling random errands, an NPC gets a
    // vocation-shaped plan for each game day — a morning task, a midday meal at
    // the ACTUAL tavern (legs anchor to real NPCs found near the post, so the
    // smith walks to where the tavernkeeper actually stands), an afternoon call
    // at the bank or market, an evening stroll. The existing errand state machine
    // executes every leg; this class only decides WHAT and WHEN.
    //
    // Plans are runtime-only and regenerate each game day (or after a reboot) —
    // they are cheap, and losing one costs nothing. Random errands/journeys still
    // fill the gaps between legs, so towns stay lively off-schedule too.
    //
    // The LLM's only role is optional flavor: at plan creation a chance-gated,
    // fail-open call names a small private "intention" for the day, which rides
    // the chat prompt ("Today you have a mind for...") and the journal. The
    // schedule itself is deterministic — the LLM is never in the movement loop.
    public static class DailyRoutine
    {
        // A leg fires if the current game hour is within this many hours past its
        // start (a watched NPC ticks every heartbeat, so it usually lands within
        // seconds; the window forgives an NPC nobody was near at the time).
        private const int LegWindowHours = 2;

        // POIs must sit between these distances from the post — closer is not
        // worth walking to, farther is another district entirely.
        private const int PoiMinDist = 4;
        private const int PoiMaxDist = 96;

        private static readonly Dictionary<int, DayPlan> m_Plans =
            new Dictionary<int, DayPlan>();

        // ---- the director's hook ----------------------------------------------

        // Called from ErrandDirector.MaybeStart (idle NPC, decision window open),
        // BEFORE the random journey/errand rolls. Returns true if a routine leg
        // was started, in which case the random rolls are skipped this window.
        public static bool TryStartLeg(BaseCreature npc, Errand e, DateTime now, MobilityClass cls)
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.RoutineEnabled || cls == MobilityClass.Stationary)
                    return false;

                if (npc == null || npc.Deleted || npc.Map == null || npc.Map == Map.Internal)
                    return false;

                int hours, minutes, totalMinutes;
                Clock.GetTime(npc.Map, npc.X, npc.Y, out hours, out minutes, out totalMinutes);

                int day = totalMinutes / 1440;

                DayPlan plan = PlanFor(npc, cls, day);
                if (plan == null || plan.Legs.Count == 0)
                    return false;

                for (int i = 0; i < plan.Legs.Count; i++)
                {
                    DayLeg leg = plan.Legs[i];

                    if (leg.Done || hours < leg.Hour || hours > leg.Hour + LegWindowHours)
                        continue;

                    // Due. Consume it whether or not it starts cleanly, so a
                    // failed POI lookup doesn't retry every window all day.
                    leg.Done = true;

                    return StartLeg(npc, e, now, leg);
                }
            }
            catch (Exception ex)
            {
                LLMClient.Log("ROUTINE-ERROR " + ex.Message);
            }

            return false;
        }

        private static bool StartLeg(BaseCreature npc, Errand e, DateTime now, DayLeg leg)
        {
            Point3D post = npc.Home != Point3D.Zero ? npc.Home : npc.Location;

            Point3D dest;
            int dwellMin = 20, dwellMax = 60;

            switch (leg.Activity)
            {
                case "tavern":
                    if (!FindPoi(npc, post, "tavernkeeper", out dest))
                        return false; // no tavern in this town — skip the leg
                    dwellMin = 90; dwellMax = 240;
                    break;

                case "bank":
                    if (!FindPoi(npc, post, "banker", out dest))
                        return false;
                    dwellMin = 30; dwellMax = 90;
                    break;

                case "market":
                    if (!FindVendorPoi(npc, post, out dest))
                        return false;
                    dwellMin = 45; dwellMax = 120;
                    break;

                default: // errand / stroll — anywhere reachable will do
                    dest = ErrandPolicy.PickLocalDestination(npc.Map, post);
                    if (dest == post)
                        return false;
                    break;
            }

            return ErrandDirector.StartRoutineErrand(npc, e, now, dest, leg.Purpose, dwellMin, dwellMax);
        }

        // ---- plan generation ----------------------------------------------------

        private static DayPlan PlanFor(BaseCreature npc, MobilityClass cls, int day)
        {
            int serial = npc.Serial.Value;

            DayPlan plan;
            if (m_Plans.TryGetValue(serial, out plan) && plan.Day == day)
                return plan;

            plan = Generate(npc, cls, day);
            m_Plans[serial] = plan;

            MaybeRollIntention(npc, plan);

            return plan;
        }

        // A vocation-shaped day. Hours are jittered and legs are chance-gated so
        // the same NPC's days differ, and a street of NPCs doesn't move in lockstep.
        private static DayPlan Generate(BaseCreature npc, MobilityClass cls, int day)
        {
            DayPlan plan = new DayPlan(day);

            string voc = LLMAmbientSpeech.InferVocation(npc);

            if (cls == MobilityClass.LocalOnly)
            {
                // Shopkeepers mind the shop: a midday bite out, maybe one errand.
                if (voc != "tavernkeeper" && Utility.RandomDouble() < 0.7)
                    plan.Legs.Add(new DayLeg(Jitter(12), "tavern",
                        "slipping out for a midday bite at the tavern"));

                if (Utility.RandomDouble() < 0.5)
                    plan.Legs.Add(new DayLeg(Jitter(17), "errand",
                        ErrandPolicy.RollLocalErrandKind(voc)));
            }
            else
            {
                // Roamers keep a fuller day.
                if (Utility.RandomDouble() < 0.7)
                    plan.Legs.Add(new DayLeg(Jitter(9), "errand",
                        ErrandPolicy.RollLocalErrandKind(voc)));

                // The tavernkeeper IS the tavern; they go marketing instead.
                if (voc == "tavernkeeper")
                {
                    if (Utility.RandomDouble() < 0.8)
                        plan.Legs.Add(new DayLeg(Jitter(12), "market",
                            "buying bread and meat for the common room"));
                }
                else if (Utility.RandomDouble() < 0.8)
                {
                    plan.Legs.Add(new DayLeg(Jitter(12), "tavern",
                        "taking the midday meal at the tavern"));
                }

                if (Utility.RandomDouble() < 0.6)
                {
                    if (Utility.RandomDouble() < 0.5)
                        plan.Legs.Add(new DayLeg(Jitter(16), "bank",
                            "seeing the banker about a small matter"));
                    else
                        plan.Legs.Add(new DayLeg(Jitter(16), "market",
                            "browsing the market stalls"));
                }

                if (Utility.RandomDouble() < 0.5)
                    plan.Legs.Add(new DayLeg(Jitter(19), "stroll",
                        "taking the evening air about town"));
            }

            return plan;
        }

        private static int Jitter(int hour)
        {
            int h = hour + Utility.RandomMinMax(-1, 1);

            if (h < 0)
                h = 0;
            else if (h > 23)
                h = 23;

            return h;
        }

        // ---- POIs: real places, found by who actually stands there --------------

        // The nearest NPC of the wanted vocation within range of the post — so
        // "the tavern" is wherever the tavernkeeper actually keeps it. Returns a
        // standable tile beside them.
        private static bool FindPoi(BaseCreature npc, Point3D post, string vocation, out Point3D dest)
        {
            dest = Point3D.Zero;

            Mobile best = null;
            double bestDist = double.MaxValue;

            IPooledEnumerable eable = npc.Map.GetMobilesInRange(post, PoiMaxDist);

            foreach (Mobile m in eable)
            {
                if (m == npc || m.Deleted || m.Player || !m.Alive || !m.Body.IsHuman)
                    continue;

                if (LLMAmbientSpeech.InferVocation(m) != vocation)
                    continue;

                double dist = npc.GetDistanceToSqrt(m);
                if (dist < PoiMinDist)
                    continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = m;
                }
            }

            eable.Free();

            if (best == null)
                return false;

            return StandableNear(npc.Map, best.Location, out dest);
        }

        // Any other vendor's pitch serves as "the market".
        private static bool FindVendorPoi(BaseCreature npc, Point3D post, out Point3D dest)
        {
            dest = Point3D.Zero;

            Mobile best = null;
            double bestDist = double.MaxValue;

            IPooledEnumerable eable = npc.Map.GetMobilesInRange(post, PoiMaxDist);

            foreach (Mobile m in eable)
            {
                if (m == npc || m.Deleted || m.Player || !m.Alive)
                    continue;

                if (!(m is BaseVendor))
                    continue;

                double dist = npc.GetDistanceToSqrt(m);
                if (dist < PoiMinDist)
                    continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = m;
                }
            }

            eable.Free();

            if (best == null)
                return false;

            return StandableNear(npc.Map, best.Location, out dest);
        }

        // A spot a tile or two off the target the NPC can actually stand on, so
        // it sidles up beside the keeper rather than into them.
        private static bool StandableNear(Map map, Point3D at, out Point3D dest)
        {
            dest = Point3D.Zero;

            for (int i = 0; i < 9; i++)
            {
                int x = at.X + Utility.RandomMinMax(-2, 2);
                int y = at.Y + Utility.RandomMinMax(-2, 2);

                int z = map.GetAverageZ(x, y);
                if (map.CanSpawnMobile(x, y, z))
                {
                    dest = new Point3D(x, y, z);
                    return true;
                }

                if (map.CanSpawnMobile(x, y, at.Z))
                {
                    dest = new Point3D(x, y, at.Z);
                    return true;
                }
            }

            return false;
        }

        // ---- optional LLM flavor: the dawn intention ----------------------------

        // One chance-gated, fail-open call per NPC per game day that names a small
        // private aim ("mending the fence behind the cottage"). It rides the chat
        // prompt and the journal; a miss simply means a day with no stated aim.
        private static void MaybeRollIntention(BaseCreature npc, DayPlan plan)
        {
            if (!LLMConfig.Enabled || Utility.RandomDouble() >= LLMConfig.RoutineLlmChance)
                return;

            NpcIdentity id = LLMAmbientSpeech.EnsureIdentity(npc);
            string voc = LLMAmbientSpeech.InferVocation(npc);
            string town = BritanniaGeography.TownOf(npc);
            string name = string.IsNullOrEmpty(npc.Name) ? "an unnamed townsperson" : npc.Name;

            System.Text.StringBuilder sys = new System.Text.StringBuilder();
            sys.Append("You name a single small private intention for the day for a character in the medieval fantasy world of Ultima Online (no modern words). ");
            sys.Append("Reply with ONLY the intention: a short phrase of at most ten words, lowercase, no name, no quotation marks, no trailing period. ");
            sys.Append("It must read naturally after the words \"a mind for\".");

            System.Text.StringBuilder usr = new System.Text.StringBuilder();
            usr.Append("Character: ").Append(name);
            if (!string.IsNullOrEmpty(voc))
                usr.Append(", a ").Append(voc);
            usr.Append(" of ").Append(town).Append(". ");

            if (id != null)
            {
                if (!string.IsNullOrEmpty(id.Personality))
                    usr.Append("Temperament: ").Append(id.Personality).Append(". ");
                if (!string.IsNullOrEmpty(id.Mood))
                    usr.Append("Mood: ").Append(id.Mood).Append(". ");
                if (!string.IsNullOrEmpty(id.Motivation))
                    usr.Append("Preoccupation: ").Append(id.Motivation).Append(". ");
            }

            usr.Append("Give the intention only.");

            List<LLMMessage> msgs = new List<LLMMessage>();
            msgs.Add(new LLMMessage("user", usr.ToString()));

            int serial = npc.Serial.Value;
            int day = plan.Day;
            string ftown = town;

            LLMClient.TryDispatch("routine:" + serial, sys.ToString(), msgs, "", "", "", 0, delegate(bool ok, string reply)
            {
                if (!ok || string.IsNullOrEmpty(reply))
                    return;

                DayPlan cur;
                if (!m_Plans.TryGetValue(serial, out cur) || cur.Day != day)
                    return; // a new day started while the model thought

                string clean = CleanIntention(reply);
                if (clean.Length == 0)
                    return;

                cur.Intention = clean;

                LLMAmbientMemory.AppendJournal(serial, "rose with a mind for " + clean + " (" + ftown + ")");
            });
        }

        private static string CleanIntention(string s)
        {
            s = s.Trim();

            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2).Trim();

            s = s.TrimEnd('.', '!', '?', ' ', '"', '\'', ',');

            if (s.Length > 60)
            {
                s = s.Substring(0, 60);
                int sp = s.LastIndexOf(' ');
                if (sp > 30)
                    s = s.Substring(0, sp);
                s = s.TrimEnd();
            }

            if (s.Length > 0 && char.IsUpper(s[0]))
                s = char.ToLowerInvariant(s[0]) + s.Substring(1);

            return s;
        }

        // ---- surfacing -----------------------------------------------------------

        // The NPC's current stated aim for today, or "" — appended to chat prompts.
        public static string IntentionOf(int npcSerial)
        {
            DayPlan plan;
            if (m_Plans.TryGetValue(npcSerial, out plan) && !string.IsNullOrEmpty(plan.Intention))
                return plan.Intention;

            return "";
        }

        // ---- GM helpers ----------------------------------------------------------

        public static List<string> PlanLines(BaseCreature npc)
        {
            List<string> lines = new List<string>();

            int hours, minutes, totalMinutes;
            Clock.GetTime(npc.Map, npc.X, npc.Y, out hours, out minutes, out totalMinutes);

            DayPlan plan;
            if (!m_Plans.TryGetValue(npc.Serial.Value, out plan))
            {
                lines.Add("no plan yet (game hour " + hours + ", day " + (totalMinutes / 1440) + ")");
                return lines;
            }

            lines.Add("day " + plan.Day + " (now hour " + hours + ")" +
                (string.IsNullOrEmpty(plan.Intention) ? "" : " — a mind for " + plan.Intention));

            if (plan.Legs.Count == 0)
                lines.Add("(an empty day — no legs rolled)");

            for (int i = 0; i < plan.Legs.Count; i++)
            {
                DayLeg leg = plan.Legs[i];
                lines.Add((leg.Done ? "[done] " : "[ " + leg.Hour + "h ] ") +
                    leg.Activity + ": " + leg.Purpose);
            }

            return lines;
        }

        // Forces the next undone leg due RIGHT NOW (GM test): re-pins it to the
        // current game hour and opens the NPC's decision window.
        public static bool ForceNextLeg(BaseCreature npc)
        {
            int hours, minutes, totalMinutes;
            Clock.GetTime(npc.Map, npc.X, npc.Y, out hours, out minutes, out totalMinutes);

            DayPlan plan;
            if (!m_Plans.TryGetValue(npc.Serial.Value, out plan))
                return false;

            for (int i = 0; i < plan.Legs.Count; i++)
            {
                if (plan.Legs[i].Done)
                    continue;

                plan.Legs[i].Hour = hours;

                Errand e = LLMAmbientMemory.GetOrCreateErrand(npc.Serial.Value, DateTime.UtcNow);
                e.NextDecisionUtc = DateTime.MinValue;

                return true;
            }

            return false;
        }
    }
}
