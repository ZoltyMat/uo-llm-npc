using System;
using System.Collections.Generic;
using System.Text;
using Server;

namespace Server.Custom.LLMNpc
{
    // A persistent, per-instance identity for an LLM NPC: where they hold their
    // post, where they hail from, their temperament, a short history, how they
    // speak, a private drive, and a current mood. Generated once from
    // vocation-flavored pools so two NPCs of the same trade in different towns
    // are genuinely distinct, then serialized with the mobile so the character
    // stays stable across reboots.
    public class NpcIdentity
    {
        public bool Assigned;
        public string Town;        // where they hold their post (drives location color)
        public string Origin;      // where they hail from (may differ from Town)
        public string Personality; // a couple of temperament traits
        public string Backstory;   // a sentence or two of history
        public string SpeechStyle; // how they talk
        public string Motivation;  // private drive / preoccupation (their "thought")
        public string Mood;        // current mood; drifts over time
        public string Archetype;   // BG3-derived voice archetype key (drives style RAG)

        public NpcIdentity()
        {
        }

        // ---- generation ------------------------------------------------------

        public static NpcIdentity Generate(string vocation, string town, bool female)
        {
            NpcIdentity id = new NpcIdentity();

            id.Assigned = true;
            id.Town = string.IsNullOrEmpty(town) ? "Britannia" : town;
            id.Origin = RollOrigin(id.Town);
            id.Personality = RollPersonality();
            id.SpeechStyle = Pick(SpeechStylePool(vocation));
            id.Mood = Pick(m_Moods);
            id.Backstory = SafeFormat(Pick(BackstoryPool(vocation)), id.Town, id.Origin);
            id.Motivation = SafeFormat(Pick(MotivationPool(vocation)), id.Town, id.Origin);
            id.Archetype = Pick(ArchetypePool(vocation));

            return id;
        }

        // Occasionally nudges the mood so repeated visitors notice the NPC isn't
        // a fixed recording.
        public void DriftMood()
        {
            if (Utility.RandomDouble() < 0.15)
                Mood = Pick(m_Moods);
        }

        private static string RollOrigin(string town)
        {
            // Most folk are locals; some are transplants from another town.
            if (Utility.RandomDouble() < 0.6)
                return town;

            return BritanniaGeography.RandomCityExcept(town);
        }

        private static string RollPersonality()
        {
            int a = Utility.Random(m_Traits.Length);
            int b = Utility.Random(m_Traits.Length);

            int guard = 0;
            while (b == a && guard++ < 8)
                b = Utility.Random(m_Traits.Length);

            if (a == b)
                return m_Traits[a];

            return m_Traits[a] + " and " + m_Traits[b];
        }

        private static string Pick(string[] arr)
        {
            if (arr == null || arr.Length == 0)
                return "";

            return arr[Utility.Random(arr.Length)];
        }

        // {0} = town, {1} = origin. Falls back to the raw template on a bad format.
        private static string SafeFormat(string tmpl, string town, string origin)
        {
            try
            {
                return string.Format(tmpl, town, origin);
            }
            catch (FormatException)
            {
                return tmpl;
            }
        }

        private static string[] BackstoryPool(string vocation)
        {
            switch (Norm(vocation))
            {
                case "banker": return m_BankerBack;
                case "blacksmith": return m_SmithBack;
                case "tavernkeeper": return m_TavernBack;
                case "villager": return m_VillagerBack;
                case "raider": return m_RaiderBack;
                case "lich": return m_LichBack;
                case "ogre": return m_OgreBack;
                case "lizardman": return m_LizardBack;
                case "ratman": return m_RatBack;
                case "gargoyle": return m_GargoyleBack;
                case "daemon": return m_DaemonBack;
                case "overseer": return m_OverseerBack;
                default: return m_GenericBack;
            }
        }

        private static string[] MotivationPool(string vocation)
        {
            switch (Norm(vocation))
            {
                case "banker": return m_BankerMot;
                case "blacksmith": return m_SmithMot;
                case "tavernkeeper": return m_TavernMot;
                case "villager": return m_VillagerMot;
                case "raider": return m_RaiderMot;
                case "lich": return m_LichMot;
                case "ogre": return m_OgreMot;
                case "lizardman": return m_LizardMot;
                case "ratman": return m_RatMot;
                case "gargoyle": return m_GargoyleMot;
                case "daemon": return m_DaemonMot;
                case "overseer": return m_OverseerMot;
                default: return m_GenericMot;
            }
        }

        // Maps a vocation to a small set of BG3-derived voice archetypes that fit
        // the trade. One is rolled per NPC and used to filter the bg3_style RAG
        // collection so the LLM gets cadence/wit exemplars in keeping with the
        // character — a gruff smith pulls from blunt/martial voices, a barkeep
        // from warm/comic ones, and so on.
        private static string[] ArchetypePool(string vocation)
        {
            switch (Norm(vocation))
            {
                case "banker": return m_BankerArch;
                case "blacksmith": return m_SmithArch;
                case "tavernkeeper": return m_TavernArch;
                case "villager": return m_VillagerArch;
                case "raider": return m_RaiderArch;
                case "lich": return m_LichArch;
                case "ogre": return m_OgreArch;
                case "lizardman": return m_LizardArch;
                case "ratman": return m_RatArch;
                case "gargoyle": return m_GargoyleArch;
                case "daemon": return m_DaemonArch;
                case "overseer": return m_OverseerArch;
                default: return m_GenericArch;
            }
        }

        // Most NPCs draw their cadence from the shared speech-style pool. The
        // Overseer is a deliberately distinct, meta-adjacent voice, so it gets its
        // own pool — a harried-but-composed register the generic styles can't hit.
        private static string[] SpeechStylePool(string vocation)
        {
            switch (Norm(vocation))
            {
                case "overseer": return m_OverseerSpeech;
                case "lich": return m_LichSpeech;
                case "gargoyle": return m_GargoyleSpeech;
                case "daemon": return m_DaemonSpeech;
                case "ogre":
                case "lizardman":
                case "ratman":
                case "raider": return m_BrutishSpeech;
                default: return m_SpeechStyles;
            }
        }

        private static string Norm(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();
        }

        // ---- pools -----------------------------------------------------------

        private static readonly string[] m_Traits = new string[]
        {
            "shrewd", "plainspoken", "suspicious of strangers", "warm and talkative",
            "gruff", "secretly generous", "proud", "weary of the world",
            "quick to anger", "patient", "superstitious", "ambitious",
            "cautious", "jovial", "stern", "curious", "tight-fisted",
            "honest to a fault", "sly", "devout"
        };

        private static readonly string[] m_SpeechStyles = new string[]
        {
            "clipped and formal", "rambling, fond of old proverbs",
            "blunt, wasting no words", "florid, with courtly flourishes",
            "peppered with nautical turns of phrase", "soft-spoken and deliberate",
            "brash and loud", "dry and sardonic",
            "kindly, calling everyone 'friend'", "guarded, answering questions with questions"
        };

        private static readonly string[] m_Moods = new string[]
        {
            "content", "irritable today", "cheerful", "tired and short-tempered",
            "wary", "melancholy", "in unusually good spirits", "distracted",
            "on edge", "calm"
        };

        private static readonly string[] m_BankerBack = new string[]
        {
            "You have kept the vaults of {0} for over twenty years and trust no lock you did not set yourself.",
            "You came to {0} from {1} after a rival ruined your family's trade, and you guard every coin as though it were your last.",
            "You inherited the counting-house from your late father and mean to make it the richest in {0}.",
            "You served as a ledger-clerk in {1} before the guild gave you the keys to the bank of {0}."
        };

        private static readonly string[] m_SmithBack = new string[]
        {
            "You learned the hammer in {1} and set up your forge in {0} when the old smith died.",
            "Your arms have known the anvil since boyhood; the militia of {0} carries blades you made.",
            "A burn took two fingers years ago, yet none in {0} shape steel truer than you.",
            "You wandered as a tinker out of {1} before the forge in {0} finally gave you a roof."
        };

        private static readonly string[] m_TavernBack = new string[]
        {
            "You took over the tavern in {0} when its last keeper drank himself to ruin, and you mean to run it better.",
            "Sailors and pilgrims pass through {0}, and you have heard every rumor in the realm twice over.",
            "You were a cook aboard a galleon out of {1} until you tired of the sea and settled in {0}.",
            "Your tavern has stood three generations in {0}, and you know every family's secrets."
        };

        private static readonly string[] m_VillagerBack = new string[]
        {
            "You have tilled the fields outside {0} all your life and rarely venture past its walls.",
            "You came to {0} from {1} seeking quieter days after hard years.",
            "Your cottage near {0} burned one winter, and the town's kindness is why you still call it home.",
            "You mind a small flock and a smaller plot just beyond {0}, content with little."
        };

        private static readonly string[] m_RaiderBack = new string[]
        {
            "Your warband was driven from its caves near {0}, and you nurse a grudge against all who walk upright.",
            "You have raided the roads about {0} for seasons beyond counting and fear no blade.",
            "A human host slew your chieftain near {0}; you mean to take a hundred of theirs in kind."
        };

        private static readonly string[] m_GenericBack = new string[]
        {
            "You have made your living in {0} for many years and know its streets well.",
            "You drifted to {0} from {1} long ago and never found reason to leave.",
            "Few in {0} know your full story, and you prefer it so."
        };

        private static readonly string[] m_BankerMot = new string[]
        {
            "saving quietly to buy back your family's old estate",
            "convinced a thief has been skimming the vault, and watching everyone",
            "hoping to win a seat on the town council of {0}",
            "weary of greedy nobles and dreaming of retirement by the sea"
        };

        private static readonly string[] m_SmithMot = new string[]
        {
            "determined to forge a blade worthy of a king",
            "behind on a large order for the {0} guard and anxious about it",
            "proud of an apprentice you hope will one day surpass you",
            "saving for ore from the mines of Minoc to try a new alloy"
        };

        private static readonly string[] m_TavernMot = new string[]
        {
            "trying to learn who has been spreading lies about your ale",
            "sweet on a traveler who promised to return to {0} but never did",
            "scheming to drive the rival tavern across {0} out of business",
            "quietly fencing news to whoever pays best for it"
        };

        private static readonly string[] m_VillagerMot = new string[]
        {
            "worried the harvest near {0} will fail again this year",
            "saving copper by copper to send a child to study in Moonglow",
            "certain you saw something strange in the woods and afraid to speak of it",
            "longing for the simpler days before the troubles came to {0}"
        };

        private static readonly string[] m_RaiderMot = new string[]
        {
            "hungry for a fight and itching to test the stranger before you",
            "guarding a stolen hoard you will not let any near",
            "spoiling to prove yourself the fiercest of your band"
        };

        private static readonly string[] m_GenericMot = new string[]
        {
            "missing someone who left {0} long ago",
            "quietly worried about coin",
            "hoping for a quiet season with no trouble"
        };

        // BG3-derived voice archetypes (keys must match the `archetype` payload
        // field seeded into the bg3_style Qdrant collection). Each vocation draws
        // from a handful of voices that suit the trade.
        private static readonly string[] m_BankerArch = new string[]
        {
            "guarded_dry", "erudite_charming", "wry_veteran", "cold_commanding"
        };

        private static readonly string[] m_SmithArch = new string[]
        {
            "blunt_martial", "warm_fierce", "wry_veteran", "calm_grounded"
        };

        private static readonly string[] m_TavernArch = new string[]
        {
            "warm_fierce", "bombastic_comic", "noble_theatrical", "wry_veteran"
        };

        private static readonly string[] m_VillagerArch = new string[]
        {
            "calm_grounded", "warm_fierce", "guarded_dry", "wry_veteran"
        };

        private static readonly string[] m_RaiderArch = new string[]
        {
            "blunt_martial", "cold_commanding", "bombastic_comic", "warm_fierce"
        };

        private static readonly string[] m_GenericArch = new string[]
        {
            "warm_fierce", "guarded_dry", "wry_veteran", "calm_grounded",
            "erudite_charming", "noble_theatrical"
        };

        // ---- language-speaking monsters --------------------------------------
        // Wild speakers (orcs, ogres, lizardmen, ratmen, liches, gargoyles,
        // daemons). Their "town" is the nearest region BritanniaGeography resolves,
        // used loosely as the lair's locale ({0}) rather than a civic post.

        // Brutish, broken cadence shared by the dumber/feral brutes (ogre,
        // lizardman, ratman, orc raider). Grandiose/ancient/infernal speakers get
        // their own pools below.
        private static readonly string[] m_BrutishSpeech = new string[]
        {
            "broken and guttural, mangling the common tongue into a few heavy words",
            "snarling and clipped, more threat than sentence",
            "crude and simple, thick with grunts and short blunt words",
            "harsh and halting, the speech of a thing unused to talking"
        };

        private static readonly string[] m_LichSpeech = new string[]
        {
            "grandiose and imperious, every word a pronouncement from on high",
            "cold, slow, and absolute, savoring the weight of your own decrees",
            "lofty and contemptuous, naming lesser things for what they are",
            "ceremonious and deathless, as one who has all of eternity to speak"
        };

        private static readonly string[] m_GargoyleSpeech = new string[]
        {
            "cold, formal, and precise, every syllable carved from stone",
            "measured and ancient, weary of explaining the obvious to the short-lived",
            "haughty and exact, sparing of warmth and shorter still of patience"
        };

        private static readonly string[] m_DaemonSpeech = new string[]
        {
            "silken and eloquent, each cruelty wrapped in honeyed courtesy",
            "smooth, lordly, and mocking, tempting even as you belittle",
            "velvet-voiced and patient, a predator who need never raise its tone"
        };

        private static readonly string[] m_LichBack = new string[]
        {
            "You raised your tomb in the dead lands beyond {0} an age ago, and the kingdoms that feared you are themselves long dust.",
            "You were a sorcerer-king of {1} before you tore your own soul free of death, and you have not forgotten a crown is owed you.",
            "Mortal empires have risen and rotted outside your crypt near {0} while you merely waited, deathless and unbothered.",
            "You drank the secret of undeath dry where wiser fools feared to sip, and now no power in {0} may command you."
        };

        private static readonly string[] m_OgreBack = new string[]
        {
            "You smash and eat in the hills near {0}, and little things run when your shadow falls.",
            "You took this club off a dead thing seasons ago near {0}, and it is the best thing you own.",
            "Big folk chased your kin from the caves by {0}, so now you take what you want from the roads.",
            "You are the biggest, hungriest thing in the hills about {0}, and you mean to stay that way."
        };

        private static readonly string[] m_LizardBack = new string[]
        {
            "You have guarded the same stretch of marsh near {0} since you hatched, and warm-bloods are not welcome in it.",
            "Your nest-kin were speared by hunters out of {0}, and you trust nothing that walks upright.",
            "The fens about {0} are yours by right of tooth and water, and you suffer no trespass.",
            "You drifted to the wetlands near {0} from the deeper swamps of {1} when the waters there went foul."
        };

        private static readonly string[] m_RatBack = new string[]
        {
            "You run the warren beneath {0} and skim a little from everything that passes through it.",
            "You slunk out of the sewers of {1} to the under-tunnels of {0}, always one alley ahead of trouble.",
            "You have lived long for your kind by being quick, sly, and never the last one in a fight near {0}.",
            "Every scrap and secret in the under-ways of {0} crosses your path sooner or later, and you trade in both."
        };

        private static readonly string[] m_GargoyleBack = new string[]
        {
            "You kept your stone vigil over the heights near {0} while the very stones of human towns were quarried and raised.",
            "Your people built wonders before {0} had a name, and you remember each one the short-lived have forgotten.",
            "You were old when the first walls of {0} were laid, and you expect to be older still when they fall.",
            "You withdrew to the cliffs above {0} when the younger races spread, preferring stone and silence to their noise."
        };

        private static readonly string[] m_DaemonBack = new string[]
        {
            "You were summoned to the lands near {0} by a fool who could not hold you, and the lands have suffered for it since.",
            "You have whispered ruin into a hundred ambitious ears around {0}, and collected on every bargain.",
            "The pits know your name; the mortals near {0} are only beginning to learn it.",
            "You slipped the bounds of your summoning circle outside {0} long ago, and walk free as it pleases you."
        };

        private static readonly string[] m_LichMot = new string[]
        {
            "certain this mortal, like all the rest, exists only to serve, kneel, or be unmade",
            "amused that so brief a creature dares stand in your presence at all",
            "weighing whether this one is worth raising as a servant or simply ending",
            "savoring the slow, absolute certainty of your dominion over all that lives"
        };

        private static readonly string[] m_OgreMot = new string[]
        {
            "hungry, and wondering if the little thing in front of you is food",
            "wanting to smash something and not yet sure why you have not",
            "guarding a pile of shiny junk you are very proud of",
            "trying to remember what you were doing before this thing started talking"
        };

        private static readonly string[] m_LizardMot = new string[]
        {
            "ready to drive the warm-blood off your waters the moment it steps wrong",
            "guarding a clutch of eggs hidden deep in the reeds",
            "wary that this one brings hunters and spears behind it",
            "hungry and weighing whether the intruder is threat or prey"
        };

        private static readonly string[] m_RatMot = new string[]
        {
            "already sizing up what this one carries and how to get it",
            "looking for an angle, a weakness, or a deal in your favor",
            "ready to flatter, lie, or flee the instant it serves you",
            "nursing a grudge and a scheme against a bigger rat up the tunnel"
        };

        private static readonly string[] m_GargoyleMot = new string[]
        {
            "quietly contemptuous of how much these brief creatures rush and fret",
            "guarding an old secret of stone the younger races would only ruin",
            "weary of being disturbed and wishing the mortal would simply leave",
            "measuring whether this one is worth the breath of a reply at all"
        };

        private static readonly string[] m_DaemonMot = new string[]
        {
            "already shaping the bargain that will cost this mortal far more than they think",
            "savoring the fear you can smell rising off the soul before you",
            "tempting them toward the first small sin that opens the door to the rest",
            "amused at how easily the proud and the desperate alike are led"
        };

        private static readonly string[] m_LichArch = new string[]
        {
            "cold_commanding", "noble_theatrical", "erudite_charming", "bombastic_comic"
        };

        private static readonly string[] m_OgreArch = new string[]
        {
            "blunt_martial", "bombastic_comic"
        };

        private static readonly string[] m_LizardArch = new string[]
        {
            "blunt_martial", "guarded_dry", "cold_commanding"
        };

        private static readonly string[] m_RatArch = new string[]
        {
            "roguish_vain", "wry_veteran", "guarded_dry", "bombastic_comic"
        };

        private static readonly string[] m_GargoyleArch = new string[]
        {
            "cold_commanding", "erudite_charming", "guarded_dry", "calm_grounded"
        };

        private static readonly string[] m_DaemonArch = new string[]
        {
            "cold_commanding", "noble_theatrical", "erudite_charming", "roguish_vain"
        };

        // ---- the Overseer: the GM-avatar persona -----------------------------
        // A meta-adjacent figure who tends the realm from somewhere behind it,
        // quietly overwhelmed but keeping a calm, confident front. The flavor is
        // administrative strain ("ten other fires to put out"), never the modern
        // world — so it stays inside the "never break character" guardrail while
        // reading as the harried hand that keeps Britannia running.
        private static readonly string[] m_OverseerSpeech = new string[]
        {
            "warm but harried, forever half-turned toward somewhere else",
            "breezily reassuring, smoothing over every crack before it shows",
            "calm and unbothered on the surface, the faintest strain beneath",
            "diplomatic and deflecting, answering what it can and gliding past the rest"
        };

        private static readonly string[] m_OverseerBack = new string[]
        {
            "You have tended this realm longer than you care to count, and the work has only grown heavier with the years.",
            "No one appointed you to keep the world turning; the weight simply settled on your shoulders and never lifted.",
            "You remember when holding Britannia together was a smaller task than it has somehow become.",
            "You keep more of this realm running than anyone walking it will ever guess, and you are very tired."
        };

        private static readonly string[] m_OverseerMot = new string[]
        {
            "terrified that one of the thousand things you are holding together will finally slip",
            "running on the very last of your patience and hiding it well",
            "quietly certain you are not equal to all that has been asked of you, and determined no one find out",
            "longing, just once, for someone to ask how you yourself are faring"
        };

        // Existing seeded bg3_style archetypes that fit a composed, harried
        // authority — keeps the style RAG returning sensible cadence exemplars.
        private static readonly string[] m_OverseerArch = new string[]
        {
            "wry_veteran", "guarded_dry", "erudite_charming", "calm_grounded"
        };

        // ---- serialization ---------------------------------------------------

        public void Serialize(GenericWriter writer)
        {
            writer.Write((int)1); // identity version

            writer.Write(Assigned);
            writer.Write(Town == null ? "" : Town);
            writer.Write(Origin == null ? "" : Origin);
            writer.Write(Personality == null ? "" : Personality);
            writer.Write(Backstory == null ? "" : Backstory);
            writer.Write(SpeechStyle == null ? "" : SpeechStyle);
            writer.Write(Motivation == null ? "" : Motivation);
            writer.Write(Mood == null ? "" : Mood);
            writer.Write(Archetype == null ? "" : Archetype); // v1
        }

        public void Deserialize(GenericReader reader)
        {
            int v = reader.ReadInt();

            Assigned = reader.ReadBool();
            Town = reader.ReadString();
            Origin = reader.ReadString();
            Personality = reader.ReadString();
            Backstory = reader.ReadString();
            SpeechStyle = reader.ReadString();
            Motivation = reader.ReadString();
            Mood = reader.ReadString();

            if (v >= 1)
                Archetype = reader.ReadString();
            else
                Archetype = "";
        }
    }

    // What an NPC remembers about one specific player. Serialized with the NPC,
    // so the banker in Britain still knows your name and your manners after a
    // server reboot.
    public class NpcRelationship
    {
        public int Conversations;   // distinct sessions (gaps > 15 min start a new one)
        public int Lines;           // total utterances heard from this player
        public DateTime FirstMet;
        public DateTime LastMet;
        public int Disposition;     // -100 (loathing) .. 100 (devoted)
        public List<string> Topics; // recent salient things the player said

        public NpcRelationship()
        {
            Topics = new List<string>();
            FirstMet = DateTime.UtcNow;
            LastMet = DateTime.UtcNow;
        }

        // ---- behavior (shared by the custom NPCs and the ambient vanilla NPCs) --

        // Folds one heard utterance into this relationship: counts the line, opens
        // a new conversation after a gap, nudges disposition by sentiment, and
        // remembers a salient topic.
        public void Note(string text)
        {
            DateTime now = DateTime.UtcNow;

            if (Lines == 0)
            {
                FirstMet = now;
                Conversations = 1;
            }
            else if ((now - LastMet) > TimeSpan.FromMinutes(15))
            {
                Conversations++;
            }

            Lines++;
            LastMet = now;

            Disposition += Sentiment(text);
            if (Disposition > 100)
                Disposition = 100;
            else if (Disposition < -100)
                Disposition = -100;

            AddTopic(text);
        }

        // Prompt-ready recap of what this NPC remembers about the player, reflecting
        // state as it stood before the current utterance was folded in.
        public string Recap()
        {
            if (Conversations <= 0)
                return "You have never met them before.";

            StringBuilder sb = new StringBuilder();
            sb.Append("You have spoken with them ");
            sb.Append(Conversations == 1 ? "once before" : (Conversations + " times before"));
            sb.Append(". ");
            sb.Append(DispositionPhrase(Disposition));

            string topics = TopicSummary();
            if (!string.IsNullOrEmpty(topics))
            {
                sb.Append(" You recall them speaking of: ");
                sb.Append(topics);
                sb.Append(".");
            }

            return sb.ToString();
        }

        private static readonly string[] m_Kind = new string[]
        {
            "thank", "please", "friend", "hello", "greetings", "well met",
            "bless", "good day", "kind", "help you"
        };

        private static readonly string[] m_Cruel = new string[]
        {
            "fool", "idiot", "stupid", "hate", "kill you", "you die", "thief",
            "liar", "coward", "curse you", "shut up", "worthless"
        };

        private static int Sentiment(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            string t = text.ToLowerInvariant();
            int score = 0;

            for (int i = 0; i < m_Kind.Length; i++)
                if (t.IndexOf(m_Kind[i], StringComparison.Ordinal) >= 0)
                    score += 2;

            for (int i = 0; i < m_Cruel.Length; i++)
                if (t.IndexOf(m_Cruel[i], StringComparison.Ordinal) >= 0)
                    score -= 3;

            if (score > 6)
                score = 6;
            else if (score < -9)
                score = -9;

            return score;
        }

        private void AddTopic(string text)
        {
            if (Topics == null)
                Topics = new List<string>();

            if (string.IsNullOrEmpty(text))
                return;

            string t = text.Trim();
            if (t.Length < 6) // skip terse grunts / bare greetings
                return;

            if (t.Length > 60)
            {
                t = t.Substring(0, 60);
                int sp = t.LastIndexOf(' ');
                if (sp > 30)
                    t = t.Substring(0, sp);
                t = t.TrimEnd() + "...";
            }

            for (int i = 0; i < Topics.Count; i++)
                if (Topics[i].Equals(t, StringComparison.OrdinalIgnoreCase))
                    return; // already remembered

            Topics.Add(t);

            while (Topics.Count > 4)
                Topics.RemoveAt(0);
        }

        private static string DispositionPhrase(int d)
        {
            if (d <= -40)
                return "You distrust and dislike them.";
            if (d <= -10)
                return "You are wary of them.";
            if (d < 10)
                return "You regard them with ordinary courtesy.";
            if (d < 40)
                return "You rather like them.";
            return "You trust them and are fond of them.";
        }

        public string TopicSummary()
        {
            if (Topics == null || Topics.Count == 0)
                return "";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Topics.Count; i++)
            {
                if (i > 0)
                    sb.Append("; ");

                sb.Append('"');
                sb.Append(Topics[i]);
                sb.Append('"');
            }

            return sb.ToString();
        }

        public void Serialize(GenericWriter writer)
        {
            writer.Write((int)0); // relationship version

            writer.Write(Conversations);
            writer.Write(Lines);
            writer.Write(FirstMet);
            writer.Write(LastMet);
            writer.Write(Disposition);

            int n = Topics == null ? 0 : Topics.Count;
            writer.Write(n);

            for (int i = 0; i < n; i++)
                writer.Write(Topics[i] == null ? "" : Topics[i]);
        }

        public void Deserialize(GenericReader reader)
        {
            int v = reader.ReadInt();

            Conversations = reader.ReadInt();
            Lines = reader.ReadInt();
            FirstMet = reader.ReadDateTime();
            LastMet = reader.ReadDateTime();
            Disposition = reader.ReadInt();

            int n = reader.ReadInt();
            Topics = new List<string>();

            for (int i = 0; i < n; i++)
                Topics.Add(reader.ReadString());
        }
    }
}
