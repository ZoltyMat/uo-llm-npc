using System;
using Server;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // How far an NPC is permitted to roam. Set per-creature from its type so the
    // banker stays at the counter (players must be able to bank) and the smith only
    // steps out briefly, while ordinary townsfolk may take real trips.
    public enum MobilityClass
    {
        Stationary = 0, // bankers, escortables, controlled, non-human — never errands
        LocalOnly = 1,  // shopkeepers — brief local errands, always return to post
        Roamer = 2      // human townsfolk — local errands now; cross-continent in P2
    }

    // Deterministic errand selection: classifies an NPC's mobility and rolls a
    // profession-appropriate LOCAL errand (label + nearby destination). No LLM,
    // no cross-continent travel — that is P2/P4. Kept separate from the director
    // so the policy can be tuned or table-driven without touching the state machine.
    public static class ErrandPolicy
    {
        // Classification order matters: Banker derives from BaseVendor, so it must
        // be caught before the BaseVendor->LocalOnly rule.
        public static MobilityClass Classify(BaseCreature bc)
        {
            if (bc == null || bc.Deleted)
                return MobilityClass.Stationary;

            if (bc.Controlled || bc.Summoned)
                return MobilityClass.Stationary;

            // The Overseer (GM-avatar) manifests and vanishes; it never runs errands.
            if (bc is LLMOverseer)
                return MobilityClass.Stationary;

            // P1 scope: only the human townsfolk who already have voices/identities.
            if (!bc.Body.IsHuman)
                return MobilityClass.Stationary;

            if (bc is Banker)
                return MobilityClass.Stationary; // players must be able to bank

            if (bc is BaseEscortable)
                return MobilityClass.Stationary; // don't fight quest escort logic

            if (bc is BaseVendor)
                return MobilityClass.LocalOnly; // shopkeepers step out briefly, then return

            return MobilityClass.Roamer;
        }

        // A short, in-world errand label tied to the NPC's trade. Drives both the
        // "tell me your errand" chat context and the journal entry.
        public static string RollLocalErrandKind(string vocation)
        {
            string[] pool = KindPool(vocation);
            if (pool == null || pool.Length == 0)
                return "tending to a small task in town";

            return pool[Utility.Random(pool.Length)];
        }

        // A reachable point 8-25 tiles from the NPC's post. Tries several random
        // bearings and only accepts spots a mobile can actually stand on, so the
        // NPC doesn't set off toward a wall or the sea. Falls back to the post.
        public static Point3D PickLocalDestination(Map map, Point3D origin)
        {
            if (map == null || map == Map.Internal)
                return origin;

            for (int i = 0; i < 12; i++)
            {
                int dist = Utility.RandomMinMax(8, 25);
                double ang = Utility.RandomDouble() * Math.PI * 2.0;

                int x = origin.X + (int)Math.Round(Math.Cos(ang) * dist);
                int y = origin.Y + (int)Math.Round(Math.Sin(ang) * dist);

                int z = map.GetAverageZ(x, y);
                if (map.CanSpawnMobile(x, y, z))
                    return new Point3D(x, y, z);

                if (map.CanSpawnMobile(x, y, origin.Z))
                    return new Point3D(x, y, origin.Z);
            }

            return origin;
        }

        // A profession-relevant reason for a cross-continent trip, with the
        // destination city woven in. The pool strings carry a {0} placeholder for
        // the city so the same reason reads naturally for any destination.
        public static string RollJourneyKind(string vocation, string city)
        {
            string[] pool = JourneyPool(vocation);
            string template = (pool == null || pool.Length == 0)
                ? "traveling to {0} on an errand of some import"
                : pool[Utility.Random(pool.Length)];

            return string.Format(template, string.IsNullOrEmpty(city) ? "a distant city" : city);
        }

        private static string[] KindPool(string vocation)
        {
            switch (Norm(vocation))
            {
                case "banker": return m_BankerKinds;
                case "blacksmith": return m_SmithKinds;
                case "tavernkeeper": return m_TavernKinds;
                case "villager": return m_VillagerKinds;
                default: return m_GenericKinds;
            }
        }

        private static string[] JourneyPool(string vocation)
        {
            switch (Norm(vocation))
            {
                case "banker": return m_BankerJourneys;
                case "blacksmith": return m_SmithJourneys;
                case "tavernkeeper": return m_TavernJourneys;
                case "villager": return m_VillagerJourneys;
                default: return m_GenericJourneys;
            }
        }

        private static string Norm(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();
        }

        private static readonly string[] m_BankerKinds = new string[]
        {
            "carrying a sealed ledger to a colleague",
            "settling accounts with a merchant across town",
            "meeting a creditor at the market"
        };

        private static readonly string[] m_SmithKinds = new string[]
        {
            "fetching ore for the forge",
            "delivering a finished blade to its buyer",
            "buying charcoal at the market"
        };

        private static readonly string[] m_TavernKinds = new string[]
        {
            "rolling a fresh cask up from the cellar",
            "buying bread and meat for the common room",
            "chasing down a supplier about the ale"
        };

        private static readonly string[] m_VillagerKinds = new string[]
        {
            "carrying goods to the market",
            "calling on a neighbor",
            "drawing water from the town well",
            "bringing the day's harvest to a buyer"
        };

        private static readonly string[] m_GenericKinds = new string[]
        {
            "running an errand across town",
            "off to meet someone",
            "tending to a small task in town"
        };

        // Cross-continent reasons. {0} is filled with the destination city.
        private static readonly string[] m_BankerJourneys = new string[]
        {
            "carrying sealed notes to the bank in {0}",
            "auditing a colleague's ledgers in {0}",
            "collecting a long-overdue debt from a merchant in {0}"
        };

        private static readonly string[] m_SmithJourneys = new string[]
        {
            "delivering a commissioned blade to a buyer in {0}",
            "seeking rare ore from the smiths of {0}",
            "answering a guildmaster's summons in {0}"
        };

        private static readonly string[] m_TavernJourneys = new string[]
        {
            "bargaining for a shipment of wine bound for {0}",
            "visiting kin who keep a tavern in {0}",
            "chasing a debtor who fled to {0}"
        };

        private static readonly string[] m_VillagerJourneys = new string[]
        {
            "visiting family in {0}",
            "carrying the season's goods to the market in {0}",
            "making a pilgrimage to the shrine near {0}",
            "answering a letter from an old friend in {0}"
        };

        private static readonly string[] m_GenericJourneys = new string[]
        {
            "traveling to {0} on an errand of some import",
            "called away to {0} on a private matter",
            "bound for {0} to see to old business"
        };
    }
}
