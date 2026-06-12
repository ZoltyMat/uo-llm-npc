using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom.LLMNpc
{
    // Denizen combat class — rolled once, drives AI type, stats, skills, kit.
    public enum DenizenClass
    {
        Warrior = 0, // melee weapon + bandages
        Healer = 1,  // melee + heavy Healing/Anatomy + lots of bandages
        Mage = 2     // spellbook + Magery/EvalInt
    }

    // P16/P17: denizens — the crowd. Ordinary folk of no fixed shop or post who
    // exist to make Britannia feel inhabited: they roam the streets, run errands
    // and cross-realm journeys, hail travelers (and gawk at GMs), and gossip with
    // each other about who passes.
    //
    // They are full LLMTalkingMobiles, so every prior system applies for free:
    // identities, relationships, chat, chatter, routines, journals, gossip
    // carrying, favors, anomalies. P17 makes them DENIZENS OF A DANGEROUS WORLD:
    //   - armed and combat-capable — each rolls a class (warrior / healer /
    //     mage) with a real weapon or spellbook and level-3-to-5 stats & skills.
    //     FightMode.Aggressor: they DEFEND when attacked, never start fights, so
    //     a wandering monster (or a foolish player) gets a real fight, but the
    //     town doesn't turn on itself.
    //   - always moving — PlayerRangeSensitive is false so their AI keeps ticking
    //     even when no player is near, and the DenizenDirector drifts each one's
    //     roam-anchor across the city on a jittered cadence, so they steadily
    //     walk somewhere (not mill in place) and the whole realm keeps turning.
    //
    // Crowd-rate throttles on the optional LLM flourishes (see the LLMDenizen
    // branches in ErrandDirector/DailyRoutine) keep a packed street cheap, and
    // off-loop work still only happens near a player.
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
        private DenizenClass m_Class;

        // Runtime-only roam state (driven by ErrandDirector.DenizenRoam): the point
        // being walked toward (Zero = pick a fresh one), plus stuck-detection so a
        // denizen wedged on a building gets an A* nudge around it.
        public Point3D RoamTarget;
        public Point3D LastRoamPos;
        public DateTime RoamProgressUtc;

        // Denizens move at a steady clip regardless of stamina — the engine's
        // default would inflate their step delay toward the 0.5s cap as stamina
        // dipped from constant walking, which read as a tired shuffle.
        public override bool ReduceSpeedWithDamage { get { return false; } }

        [CommandProperty(AccessLevel.GameMaster)]
        public string HomeCity { get { return m_HomeCity; } set { m_HomeCity = value; } }

        [CommandProperty(AccessLevel.GameMaster)]
        public DenizenClass DenizenKind { get { return m_Class; } }

        // Always-active: the AI keeps thinking (and the denizen keeps roaming)
        // whether or not a player is nearby, so the world stays in motion.
        public override bool PlayerRangeSensitive { get { return false; } }

        [Constructable]
        public LLMDenizen()
            : this(RollClass())
        {
        }

        // Private chaining ctor: the class is rolled FIRST so it can pick the AI
        // type for base() and then drive the kit in the body.
        private LLMDenizen(DenizenClass kind)
            : base(AIFor(kind), FightMode.Aggressor, 8, 1, 0.2, 0.35)
        {
            m_Class = kind;
            SpeechHue = 0x3B2;

            Female = Utility.RandomBool();
            Body = Female ? 0x191 : 0x190;
            Name = NameList.RandomName(Female ? "female" : "male");

            m_Trade = m_Trades[Utility.Random(m_Trades.Length)];
            Title = "the " + m_Trade;

            // Level 3-to-5 mortals: competent, killable. A lone weak monster is a
            // fair fight; anything serious will end them.
            if (kind == DenizenClass.Mage)
            {
                SetStr(45, 65);
                SetDex(45, 60);
                SetInt(60, 80);
            }
            else
            {
                SetStr(60, 80);
                SetDex(50, 70);
                SetInt(25, 45);
            }

            SetHits(35, 60);

            SetDamage(3, 7);
            SetDamageType(ResistanceType.Physical, 100);
            SetResistance(ResistanceType.Physical, 5, 15);

            Fame = Utility.RandomMinMax(200, 1500);
            Karma = Utility.RandomMinMax(200, 1500); // honest folk — and valid prey for monsters

            EquipClothes();
            ArmAndSkill(kind);

            Utility.AssignRandomHair(this);
        }

        public LLMDenizen(Serial serial)
            : base(serial)
        {
        }

        private static DenizenClass RollClass()
        {
            double r = Utility.RandomDouble();
            if (r < 0.30)
                return DenizenClass.Mage;
            if (r < 0.50)
                return DenizenClass.Healer;
            return DenizenClass.Warrior;
        }

        private static AIType AIFor(DenizenClass kind)
        {
            return kind == DenizenClass.Mage ? AIType.AI_Mage : AIType.AI_Melee;
        }

        private void EquipClothes()
        {
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

            // A little light protection for the fighters — they look the part and
            // shrug off a glancing blow, but it is no plate harness.
            if (m_Class != DenizenClass.Mage && Utility.RandomDouble() < 0.5)
            {
                AddItem(new LeatherChest());
                if (Utility.RandomBool())
                    AddItem(new LeatherGloves());
            }
        }

        // Weapon (or spellbook) + the skills that go with the class. Skill bands
        // sit around 35-65: a few seasons of training, not a grandmaster.
        private void ArmAndSkill(DenizenClass kind)
        {
            SetSkill(SkillName.MagicResist, 25.0, 45.0);

            switch (kind)
            {
                case DenizenClass.Mage:
                    AddItem(new Spellbook());
                    if (Utility.RandomBool())
                        AddItem(new Dagger()); // a backup blade
                    SetSkill(SkillName.Magery, 45.0, 62.0);
                    SetSkill(SkillName.EvalInt, 35.0, 55.0);
                    SetSkill(SkillName.Wrestling, 20.0, 40.0);
                    SetSkill(SkillName.Meditation, 30.0, 50.0);
                    SetMana(40);
                    break;

                case DenizenClass.Healer:
                    AddMeleeWeapon();
                    SetSkill(SkillName.Healing, 50.0, 70.0);
                    SetSkill(SkillName.Anatomy, 40.0, 60.0);
                    SetSkill(SkillName.Tactics, 30.0, 50.0);
                    SetSkill(SkillName.Macing, 30.0, 50.0);
                    PackItem(new Bandage(Utility.RandomMinMax(10, 20)));
                    break;

                default: // Warrior
                    AddMeleeWeapon();
                    SetSkill(SkillName.Tactics, 40.0, 58.0);
                    SetSkill(SkillName.Anatomy, 25.0, 45.0);
                    SetSkill(SkillName.Healing, 25.0, 45.0);
                    SetSkill(SkillName.Swords, 38.0, 58.0);
                    SetSkill(SkillName.Macing, 38.0, 58.0);
                    SetSkill(SkillName.Fencing, 38.0, 58.0);
                    SetSkill(SkillName.Wrestling, 20.0, 40.0);
                    PackItem(new Bandage(Utility.RandomMinMax(5, 12)));
                    break;
            }
        }

        private void AddMeleeWeapon()
        {
            switch (Utility.Random(7))
            {
                case 0: AddItem(new Dagger()); break;
                case 1: AddItem(new Mace()); break;
                case 2: AddItem(new Club()); break;
                case 3: AddItem(new QuarterStaff()); break;
                case 4: AddItem(new ShortSpear()); break;
                case 5: AddItem(new ShepherdsCrook()); break;
                default: AddItem(new Longsword()); break;
            }
        }

        public override string DefaultPersona
        {
            get
            {
                return "one of the common folk of " +
                       (string.IsNullOrEmpty(m_HomeCity) ? "Britannia" : m_HomeCity) +
                       " — a " + (string.IsNullOrEmpty(m_Trade) ? "laborer" : m_Trade) +
                       " forever about some small business in the streets, who knows how to "
                       + (m_Class == DenizenClass.Mage ? "call on a spell" : "swing the blade at their belt")
                       + " should trouble find them, with opinions on everything that passes";
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

            writer.Write((int)1); // denizen version (1 adds combat class)

            writer.Write(m_Trade == null ? "" : m_Trade);
            writer.Write(m_HomeCity == null ? "" : m_HomeCity);
            writer.Write((int)m_Class);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            int v = reader.ReadInt();

            m_Trade = reader.ReadString();
            m_HomeCity = reader.ReadString();

            if (v >= 1)
                m_Class = (DenizenClass)reader.ReadInt();
        }
    }

    // Keeps every city populated to its target headcount, and runs the
    // street-greeting lane (denizens hailing players off the errand heartbeat).
    public class DenizenDirector : Timer
    {
        // How many denizens to place per maintenance tick — spreads the initial
        // 200-per-city fill over a few minutes instead of spiking one save.
        private const int SpawnBatch = 80;

        // How far from a city center a denizen may make its home. Widened from 50:
        // 200 homes packed into a 50-tile disk left the center wall-to-wall (NPCs
        // constantly blocking each other, which forced the lockstep stall-recovery
        // path) while the outskirts sat empty. 90 tiles roughly thirds the density,
        // so native staggered movement flows and the whole city feels inhabited
        // rather than just the square. (Applies to newly-spawned/topped-up denizens.)
        private const int HomeRadius = 90;

        // Population top-up cadence. The actual roaming is driven per-heartbeat by
        // ErrandDirector.DenizenRoam (A* pathing for the denizens a player can see);
        // this director only keeps the roster full and runs the greeting lane.
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10.0);

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
                d.RangeHome = 2;

                return true;
            }

            return false;
        }

        // GM hook: delete every denizen so the director repopulates from scratch
        // (used after a build that changes how denizens are made — they carry
        // their old stats/kit in the world save otherwise). Returns the count.
        public static int ResetAll()
        {
            List<Mobile> doomed = new List<Mobile>();

            foreach (Mobile m in World.Mobiles.Values)
                if (m is LLMDenizen && !m.Deleted)
                    doomed.Add(m);

            for (int i = 0; i < doomed.Count; i++)
                doomed[i].Delete();

            return doomed.Count;
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
