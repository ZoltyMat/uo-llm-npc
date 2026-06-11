using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom.LLMNpc
{
    // P16: denizens — the crowd. Ordinary folk of no fixed shop or post who
    // exist purely to make Britannia feel inhabited: they walk with purpose,
    // run errands and cross-realm journeys far more often than the rooted
    // townsfolk, hail travelers in the street (and gawk at GMs), and gossip
    // with each other about whoever is passing.
    //
    // They are full LLMTalkingMobiles, so every system built so far applies
    // for free: identities, relationships, chat, chatter, routines, journals,
    // gossip carrying, favors, anomalies. What's new is QUANTITY — hundreds
    // per city — which the architecture already affords (off-screen NPCs cost
    // nothing; only player-visible ones tick), plus crowd-rate throttles on
    // the optional LLM flourishes so a packed street never stampedes the model
    // (see the LLMDenizen branches in ErrandDirector/DailyRoutine).
    public class LLMDenizen : LLMTalkingMobile
    {
        private static readonly string[] m_Trades = new string[]
        {
            "fisherman", "farmer", "beggar", "sailor", "peddler", "laborer",
            "courier", "scribe", "pilgrim", "dockhand", "minstrel", "herbalist",
            "washerwoman", "rat-catcher", "lamplighter", "porter"
        };

        private string m_Trade;
        private string m_HomeCity;

        [CommandProperty(AccessLevel.GameMaster)]
        public string HomeCity { get { return m_HomeCity; } set { m_HomeCity = value; } }

        [Constructable]
        public LLMDenizen()
            : base(AIType.AI_Animal, FightMode.None, 10, 1, 0.2, 0.4)
        {
            SpeechHue = 0x3B2;

            Female = Utility.RandomBool();
            Body = Female ? 0x191 : 0x190;
            Name = NameList.RandomName(Female ? "female" : "male");

            m_Trade = m_Trades[Utility.Random(m_Trades.Length)];
            Title = "the " + m_Trade;

            InitStats(55, 55, 25);

            // Street clothes, no two alike.
            if (Female && Utility.RandomBool())
            {
                AddItem(new PlainDress(Utility.RandomDyedHue()));
            }
            else
            {
                AddItem(Utility.RandomBool()
                    ? (Item)new Shirt(Utility.RandomDyedHue())
                    : (Item)new FancyShirt(Utility.RandomDyedHue()));
                AddItem(Utility.RandomBool()
                    ? (Item)new LongPants(Utility.RandomNeutralHue())
                    : (Item)new ShortPants(Utility.RandomNeutralHue()));
            }

            switch (Utility.Random(3))
            {
                case 0: AddItem(new Shoes(Utility.RandomNeutralHue())); break;
                case 1: AddItem(new Sandals(Utility.RandomNeutralHue())); break;
                default: AddItem(new Boots(Utility.RandomNeutralHue())); break;
            }

            if (Utility.RandomDouble() < 0.25)
                AddItem(new HalfApron(Utility.RandomDyedHue()));

            Utility.AssignRandomHair(this);
        }

        public LLMDenizen(Serial serial)
            : base(serial)
        {
        }

        public override string DefaultPersona
        {
            get
            {
                return "one of the common folk of " +
                       (string.IsNullOrEmpty(m_HomeCity) ? "Britannia" : m_HomeCity) +
                       " — a " + (string.IsNullOrEmpty(m_Trade) ? "laborer" : m_Trade) +
                       " forever about some small business in the streets, with opinions on everything that passes";
            }
        }

        public override string Vocation
        {
            get { return string.IsNullOrEmpty(m_Trade) ? "villager" : m_Trade; }
        }

        public override string SceneHint
        {
            get { return "You are out amid the day's bustle, on your way somewhere as always."; }
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write((int)0); // denizen version

            writer.Write(m_Trade == null ? "" : m_Trade);
            writer.Write(m_HomeCity == null ? "" : m_HomeCity);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            reader.ReadInt();

            m_Trade = reader.ReadString();
            m_HomeCity = reader.ReadString();
        }
    }

    // Keeps every city populated to its target headcount, and runs the
    // street-greeting lane (denizens hailing players off the errand heartbeat).
    public class DenizenDirector : Timer
    {
        // How many denizens to place per maintenance tick — spreads the initial
        // 200-per-city fill over a few minutes instead of spiking one save.
        private const int SpawnBatch = 80;

        // How far from a city center a denizen may make its home.
        private const int HomeRadius = 50;

        // Population is re-counted (a full World.Mobiles pass — cheap, ~ms)
        // every tick until full, then settles to top-up duty.
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(25.0);

        // ---- street greetings (off the ErrandDirector heartbeat) --------------
        // A denizen near a passing player may call out: shard-spaced, long
        // per-denizen and per-player cooldowns, then a coin flip — frequent
        // enough that streets feel alive, spaced enough that it stays charming.
        private static readonly TimeSpan GreetGlobalCooldown = TimeSpan.FromSeconds(75.0);
        private static readonly TimeSpan GreetDenizenCooldown = TimeSpan.FromMinutes(8.0);
        private static readonly TimeSpan GreetPlayerCooldown = TimeSpan.FromSeconds(150.0);
        private const double GreetChance = 0.3;
        private const int GreetRange = 8;

        private static DateTime m_NextGreetUtc = DateTime.MinValue;
        private static readonly Dictionary<int, DateTime> m_NextDenizenGreetUtc =
            new Dictionary<int, DateTime>();
        private static readonly Dictionary<int, DateTime> m_NextPlayerGreetUtc =
            new Dictionary<int, DateTime>();

        public DenizenDirector()
            : base(TimeSpan.FromSeconds(15.0), Tick)
        {
            Priority = TimerPriority.FiveSeconds;
        }

        public static void Initialize()
        {
            new DenizenDirector().Start();
        }

        protected override void OnTick()
        {
            try
            {
                LLMConfig.EnsureLoaded();

                if (!LLMConfig.DenizenEnabled || LLMConfig.DenizenPerCity <= 0)
                    return;

                Map map = TargetMap();
                if (map == null)
                    return;

                Dictionary<string, int> counts = CountByCity();
                string[] cities = BritanniaGeography.CityNames();

                int budget = SpawnBatch;

                for (int i = 0; i < cities.Length && budget > 0; i++)
                {
                    int have;
                    counts.TryGetValue(cities[i].ToLowerInvariant(), out have);

                    int need = LLMConfig.DenizenPerCity - have;

                    for (int j = 0; j < need && budget > 0; j++)
                    {
                        if (SpawnOne(map, cities[i]))
                            budget--;
                        else
                            break; // no standable spot found this pass; try next tick
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("DenizenDirector: " + ex.Message);
            }
        }

        private static Map TargetMap()
        {
            if (!string.IsNullOrEmpty(LLMConfig.AllowedMap))
            {
                for (int i = 0; i < Map.AllMaps.Count; i++)
                {
                    Map m = Map.AllMaps[i];
                    if (m != null && m.Name != null &&
                        m.Name.Equals(LLMConfig.AllowedMap, StringComparison.OrdinalIgnoreCase))
                        return m;
                }
            }

            return Map.Felucca;
        }

        private static Dictionary<string, int> CountByCity()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();

            foreach (Mobile m in World.Mobiles.Values)
            {
                LLMDenizen d = m as LLMDenizen;
                if (d == null || d.Deleted)
                    continue;

                string key = (d.HomeCity == null ? "" : d.HomeCity).ToLowerInvariant();

                int n;
                counts.TryGetValue(key, out n);
                counts[key] = n + 1;
            }

            return counts;
        }

        private static bool SpawnOne(Map map, string city)
        {
            Point3D center;
            if (!BritanniaGeography.TryGetCityCenter(city, out center))
                return false;

            for (int i = 0; i < 20; i++)
            {
                int x = center.X + Utility.RandomMinMax(-HomeRadius, HomeRadius);
                int y = center.Y + Utility.RandomMinMax(-HomeRadius, HomeRadius);
                int z = map.GetAverageZ(x, y);

                if (!map.CanSpawnMobile(x, y, z))
                    continue;

                LLMDenizen d = new LLMDenizen();
                d.HomeCity = city;
                d.MoveToWorld(new Point3D(x, y, z), map);
                d.Home = d.Location;
                d.RangeHome = 8;

                return true;
            }

            return false;
        }

        // GM-facing population report.
        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();

            Dictionary<string, int> counts = CountByCity();
            string[] cities = BritanniaGeography.CityNames();
            int total = 0;

            for (int i = 0; i < cities.Length; i++)
            {
                int n;
                counts.TryGetValue(cities[i].ToLowerInvariant(), out n);
                total += n;
                lines.Add(cities[i] + ": " + n + " / " + LLMConfig.DenizenPerCity);
            }

            lines.Insert(0, "denizens: " + total + " total (enabled=" + LLMConfig.DenizenEnabled + ")");
            return lines;
        }

        // ---- street greetings ---------------------------------------------------

        // Called per-player from the ErrandDirector heartbeat with one nearby
        // denizen candidate. All the gates live here; fail-open.
        public static void MaybeGreet(Mobile player, LLMDenizen denizen, DateTime now)
        {
            try
            {
                if (!LLMConfig.Enabled || !LLMConfig.DenizenEnabled)
                    return;

                if (player == null || denizen == null || denizen.Deleted || !denizen.Alive)
                    return;

                if (!player.InRange(denizen.Location, GreetRange))
                    return;

                if (now < m_NextGreetUtc)
                    return;

                DateTime next;
                if (m_NextDenizenGreetUtc.TryGetValue(denizen.Serial.Value, out next) && now < next)
                    return;

                if (m_NextPlayerGreetUtc.TryGetValue(player.Serial.Value, out next) && now < next)
                    return;

                if (Utility.RandomDouble() >= GreetChance)
                    return;

                m_NextGreetUtc = now.Add(GreetGlobalCooldown);
                m_NextDenizenGreetUtc[denizen.Serial.Value] = now.Add(GreetDenizenCooldown);
                m_NextPlayerGreetUtc[player.Serial.Value] = now.Add(GreetPlayerCooldown);

                Greet(denizen, player);
            }
            catch (Exception ex)
            {
                LLMClient.Log("GREET-ERROR " + ex.Message);
            }
        }

        private static void Greet(LLMDenizen denizen, Mobile player)
        {
            NpcIdentity id = LLMAmbientSpeech.EnsureIdentity(denizen);

            StringBuilder sb = new StringBuilder();

            sb.Append("You are ");
            sb.Append(string.IsNullOrEmpty(denizen.Name) ? "a townsperson" : denizen.Name);

            if (!string.IsNullOrEmpty(denizen.Title))
            {
                sb.Append(" ");
                sb.Append(denizen.Title);
            }

            sb.Append(", ");
            sb.Append(denizen.DefaultPersona);
            sb.Append(". You live in the medieval fantasy realm of Britannia, the world of Ultima Online. ");

            if (id != null)
            {
                if (!string.IsNullOrEmpty(id.Personality))
                {
                    sb.Append("Your temperament is ");
                    sb.Append(id.Personality);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.Mood))
                {
                    sb.Append("Right now you are ");
                    sb.Append(id.Mood);
                    sb.Append(". ");
                }
            }

            sb.Append("A traveler named ");
            sb.Append(string.IsNullOrEmpty(player.Name) ? "a stranger" : player.Name);
            sb.Append(" passes you in the street. ");

            if (player.AccessLevel > AccessLevel.Player)
            {
                sb.Append("And this is no ordinary traveler — one of the realm's own unseen keepers walks the street in plain sight. Let your awe, reverence, or nervousness show, but stay entirely in character; never name what they are in plain terms, only that they are touched by powers beyond you. ");
            }
            else
            {
                string hint = TownGossip.ObservationHint(player);
                if (!string.IsNullOrEmpty(hint))
                {
                    sb.Append(hint);
                    sb.Append(" ");
                }
            }

            sb.Append("Call out a brief greeting or remark to them as they pass — ONE short sentence, in a medieval, in-world tone. ");
            sb.Append("Speak only your own words: no name label, no quotation marks, no narration. ");
            sb.Append("Never break character, never mention being an AI or a computer, never mention the modern world or that this is a game.");

            sb.Append(NpcActions.PromptInstruction());

            List<LLMMessage> msgs = new List<LLMMessage>();
            msgs.Add(new LLMMessage("user", "They are walking past you right now."));

            LLMDenizen d = denizen;

            LLMClient.TryDispatch("greet:" + denizen.Serial.Value, sb.ToString(), msgs, "", "", "", 0,
                delegate(bool ok, string reply)
            {
                if (!ok || string.IsNullOrEmpty(reply))
                    return;

                if (d == null || d.Deleted || !d.Alive)
                    return;

                string verb;
                string spoken = NpcActions.Extract(reply, out verb);

                if (spoken.Length > 0)
                    d.Say(spoken);

                NpcActions.Perform(d, verb);
            });
        }
    }
}
