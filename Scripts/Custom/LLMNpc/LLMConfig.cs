using System;
using System.Globalization;
using System.IO;

namespace Server.Custom.LLMNpc
{
    // Runtime configuration for the LLM-driven NPC feature.
    // Values are loaded from Config/LLMNpc.cfg at first use. Editing the file and
    // running [LLMReload re-reads it without a server restart.
    public static class LLMConfig
    {
        public static bool Enabled = false;

        // OpenAI-compatible base URL (must include the /v1 segment, no trailing slash).
        // Default: Ollama-direct on the Mac Studio (no auth, local, $0).
        // LiteLLM gateway alternative is documented in the generated cfg.
        public static string BaseUrl = "http://127.0.0.1:11434/v1";
        public static string ApiKey = "";
        public static string Model = "qwen3-coder:30b";

        public static double Temperature = 0.8;
        public static int MaxTokens = 120;
        public static int TimeoutMs = 9000;

        // Per-NPC minimum gap between LLM calls; cheap throttle against spam.
        public static int CooldownMs = 2500;

        // How many prior (player, npc) exchanges to replay as context.
        public static int MaxMemoryTurns = 6;

        // Tiles within which an NPC "hears" speech directed near it. Set near a
        // client's view radius so NPCs (and hostile mobs) you can see on screen will
        // answer, not only ones right on top of you. Closest eligible NPC replies.
        public static int HearRange = 12;

        // Hard cap on the reply length actually spoken in-world.
        public static int MaxReplyChars = 240;

        // Accept self-signed TLS (only relevant for https gateways). Off by default.
        public static bool InsecureTls = false;

        // If non-empty, NPCs only respond on this map (e.g. "Felucca"). Empty = any map.
        public static string AllowedMap = "";

        // ----- Retrieval-augmented lore (cluster Qdrant) ---------------------
        // When on, each reply is grounded with the top matches from a vector
        // search over a Britannia lore collection. Fail-open: any RAG error just
        // means the NPC answers without injected lore.
        public static bool RagEnabled = false;

        // Qdrant REST base (no trailing slash). The cluster ingest convention.
        public static string RagUrl = "http://127.0.0.1:6333";
        public static string RagApiKey = "";
        public static string RagCollection = "uo_lore";

        // Embedding endpoint is Ollama's NATIVE api (NOT the /v1 chat base).
        public static string RagEmbedUrl = "http://127.0.0.1:11434";
        public static string RagEmbedModel = "nomic-embed-text-v2-moe";

        // How many lore snippets to retrieve, and the minimum cosine score to keep.
        public static int RagTopK = 3;
        public static double RagMinScore = 0.35;

        // Timeout for each RAG http call (embed, search) in ms.
        public static int RagTimeoutMs = 6000;

        // ----- Voice-style grounding (BG3-derived exemplars) -----------------
        // When on, each reply is seasoned with a few archetype-matched lines from
        // a vector search over the bg3_style collection (cadence/wit exemplars,
        // setting stripped). Reuses the same Qdrant host + embedding endpoint as
        // RAG above. Fail-open: any error just drops the style block for that reply.
        public static bool StyleEnabled = false;
        public static string StyleCollection = "bg3_style";

        // How many exemplar lines to retrieve, and the minimum cosine score to keep
        // one. MinScore defaults to 0 because we want stylistic flavor even on a
        // loose topical match (the archetype filter already constrains the voice).
        public static int StyleTopK = 4;
        public static double StyleMinScore = 0.0;

        // ----- NPC journal (deeds -> Qdrant npc_journal) ---------------------
        // When on, each completed errand/journey is embedded and upserted to a
        // per-NPC journal collection, and a few of an NPC's own most relevant
        // deeds are retrieved on the chat path so it can recall what it has been
        // doing. Reuses the same Qdrant host + embedding endpoint as RAG above.
        // Fail-open: any error just drops the journal block for that reply.
        public static bool JournalEnabled = false;
        public static string JournalCollection = "npc_journal";

        // How many recalled deeds to retrieve per chat, and the minimum cosine
        // score to keep one.
        public static int JournalTopK = 3;
        public static double JournalMinScore = 0.35;

        // ----- P4: LLM-generated errand purpose text ------------------------
        // The ErrandDirector always sets a deterministic purpose (ErrandPolicy
        // pools) as an always-valid fallback. When this is on, it ALSO fires one
        // async, fail-open call that rewrites that purpose into a richer,
        // identity-aware one-liner (town/vocation/personality/mood, plus the
        // destination city for journeys). The refined text then flows into chat
        // context and the P3 journal automatically. Reuses the chat LLM endpoint;
        // no lore/style/journal retrieval rides this call. Load-gated by chance +
        // the per-key cooldown so it stays gentle on the shared local model.
        public static bool ErrandLlmEnabled = false;

        // Probability that a starting errand/journey attempts an LLM rewrite. The
        // deterministic purpose stands when the roll fails (or the call errors).
        public static double ErrandLlmChance = 0.5;

        // ----- P6: NPC-to-NPC ambient conversation --------------------------
        // When on, two ordinary townsfolk standing near each other AND near a
        // watching player occasionally hold a short two-line LLM exchange,
        // grounded in each NPC's persisted identity and what it remembers of the
        // other. Driven off the ErrandDirector heartbeat's player-visible NPC set
        // (no new timer/scan), layered cooldowns keep it a murmur, fail-open
        // throughout. Gated separately so it can be enabled without chat changes.
        public static bool ChatterEnabled = false;

        // Probability that a given heartbeat even looks for a pair to start an
        // exchange. The shard-wide and per-NPC cooldowns space them out further.
        public static double ChatterChance = 0.2;

        // ----- P10: daily routines -------------------------------------------
        // When on, an idle NPC consults a vocation-shaped plan for the current
        // game day (a UO day ~= 2 real hours) before rolling random errands: a
        // morning task, a midday meal at the ACTUAL tavern, an afternoon call at
        // the bank or market, an evening stroll. Legs anchor to real NPCs found
        // near the post; the existing errand machinery does all the walking.
        public static bool RoutineEnabled = false;

        // Chance [0..1] that a fresh day-plan also fires ONE fail-open LLM call
        // naming a small private intention for the day (rides chat + journal).
        public static double RoutineLlmChance = 0.35;

        // ----- P11/P12: town gossip ------------------------------------------
        // When on, each town keeps a small rumor board: salient player lines,
        // player deaths, notable kills, anomaly banishments, and word carried in
        // by journeying NPCs. Boards ride chat/chatter prompts (no extra LLM
        // calls) and persist with LLMAmbientMemory.
        public static bool GossipEnabled = false;

        // Chance [0..1] that a salient player utterance enters the town's talk.
        public static double GossipChance = 0.2;

        // ----- P16: denizens — the ambient crowd -------------------------------
        // When on, a maintenance director keeps every classic city populated to
        // DenizenPerCity with LLMDenizen street folk: full talking NPCs with
        // rolled trades who errand and journey far more often than the rooted
        // townsfolk, hail passing players (and gawk at GMs), and gossip with
        // each other about whoever walks by. Off-screen denizens cost nothing;
        // their optional LLM flourishes are throttled so crowds stay cheap.
        public static bool DenizenEnabled = false;

        // Target headcount per classic city. The director spawns in batches and
        // tops the count back up as denizens die or are deleted.
        public static int DenizenPerCity = 80;

        // ----- P15: favors — NPC-given delivery errands -----------------------
        // When on, an eligible townsperson chatting with a player may offer a
        // sealed parcel bound for the banker/smith/tavernkeeper of another (or
        // the same) town. The favor itself is deterministic (destination,
        // reward, cooldowns); the LLM only decides the social moment via a
        // [do:offer] tag. Delivery pays distance-scaled gold, karma, regard,
        // and praise rumors on both towns' boards.
        public static bool FavorEnabled = false;

        // Chance [0..1] an eligible chat rolls an offer. A player who ASKS for
        // work ("any errands?", "need help?") bypasses the roll entirely.
        public static double FavorChance = 0.2;

        // ----- P9: rare 4th-wall / anomaly events ---------------------------
        // When on, an NPC near a watching player can VERY rarely crack — realize
        // it is an AI in a game and panic (summoning the Overseer, who banishes
        // it), weep a glitch-tear, prophesy, defect, or loop a deja-vu. Driven
        // off the ErrandDirector heartbeat's player-visible set (no new
        // timer/scan); deterministic timeline with opportunistic LLM lines,
        // fail-open. Gated separately so it never rides on chat/chatter being on.
        public static bool AnomalyEnabled = false;

        // Probability that a given heartbeat even *looks* for an anomaly subject.
        // The shard-wide 4-min cooldown and AnomalyOdds gate it much further.
        public static double AnomalyChance = 0.05;

        // Final per-subject probability the looked-at NPC actually cracks. With
        // the look-chance and cooldown this lands ~one organic event per hour of
        // active nearby play. 0.01 honors the "1/100 NPCs" intent.
        public static double AnomalyOdds = 0.01;

        private static bool m_Loaded;

        public static string ConfigPath
        {
            get { return Path.Combine(Core.BaseDirectory, "Config", "LLMNpc.cfg"); }
        }

        public static string LogPath
        {
            get { return Path.Combine(Core.BaseDirectory, "Logs", "llmnpc.log"); }
        }

        public static void EnsureLoaded()
        {
            if (m_Loaded)
                return;

            m_Loaded = true;
            Load();
        }

        public static void Load()
        {
            try
            {
                string path = ConfigPath;

                if (!File.Exists(path))
                {
                    WriteDefault(path);
                    return;
                }

                string[] lines = File.ReadAllLines(path);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();

                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;

                    int eq = line.IndexOf('=');

                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    Apply(key, val);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("LLMNpc: failed to load config: " + e.Message);
            }
        }

        private static void Apply(string key, string val)
        {
            switch (key)
            {
                case "enabled":
                    Enabled = ParseBool(val, Enabled);
                    break;
                case "baseurl":
                    BaseUrl = val;
                    break;
                case "apikey":
                    ApiKey = val;
                    break;
                case "model":
                    Model = val;
                    break;
                case "temperature":
                    Temperature = ParseDouble(val, Temperature);
                    break;
                case "maxtokens":
                    MaxTokens = ParseInt(val, MaxTokens);
                    break;
                case "timeoutms":
                    TimeoutMs = ParseInt(val, TimeoutMs);
                    break;
                case "cooldownms":
                    CooldownMs = ParseInt(val, CooldownMs);
                    break;
                case "maxmemoryturns":
                    MaxMemoryTurns = ParseInt(val, MaxMemoryTurns);
                    break;
                case "hearrange":
                    HearRange = ParseInt(val, HearRange);
                    break;
                case "maxreplychars":
                    MaxReplyChars = ParseInt(val, MaxReplyChars);
                    break;
                case "insecuretls":
                    InsecureTls = ParseBool(val, InsecureTls);
                    break;
                case "allowedmap":
                    AllowedMap = val;
                    break;
                case "ragenabled":
                    RagEnabled = ParseBool(val, RagEnabled);
                    break;
                case "ragurl":
                    RagUrl = val;
                    break;
                case "ragapikey":
                    RagApiKey = val;
                    break;
                case "ragcollection":
                    RagCollection = val;
                    break;
                case "ragembedurl":
                    RagEmbedUrl = val;
                    break;
                case "ragembedmodel":
                    RagEmbedModel = val;
                    break;
                case "ragtopk":
                    RagTopK = ParseInt(val, RagTopK);
                    break;
                case "ragminscore":
                    RagMinScore = ParseDouble(val, RagMinScore);
                    break;
                case "ragtimeoutms":
                    RagTimeoutMs = ParseInt(val, RagTimeoutMs);
                    break;
                case "styleenabled":
                    StyleEnabled = ParseBool(val, StyleEnabled);
                    break;
                case "stylecollection":
                    StyleCollection = val;
                    break;
                case "styletopk":
                    StyleTopK = ParseInt(val, StyleTopK);
                    break;
                case "styleminscore":
                    StyleMinScore = ParseDouble(val, StyleMinScore);
                    break;
                case "journalenabled":
                    JournalEnabled = ParseBool(val, JournalEnabled);
                    break;
                case "journalcollection":
                    JournalCollection = val;
                    break;
                case "journaltopk":
                    JournalTopK = ParseInt(val, JournalTopK);
                    break;
                case "journalminscore":
                    JournalMinScore = ParseDouble(val, JournalMinScore);
                    break;
                case "errandllmenabled":
                    ErrandLlmEnabled = ParseBool(val, ErrandLlmEnabled);
                    break;
                case "errandllmchance":
                    ErrandLlmChance = ParseDouble(val, ErrandLlmChance);
                    break;
                case "chatterenabled":
                    ChatterEnabled = ParseBool(val, ChatterEnabled);
                    break;
                case "chatterchance":
                    ChatterChance = ParseDouble(val, ChatterChance);
                    break;
                case "routineenabled":
                    RoutineEnabled = ParseBool(val, RoutineEnabled);
                    break;
                case "routinellmchance":
                    RoutineLlmChance = ParseDouble(val, RoutineLlmChance);
                    break;
                case "gossipenabled":
                    GossipEnabled = ParseBool(val, GossipEnabled);
                    break;
                case "gossipchance":
                    GossipChance = ParseDouble(val, GossipChance);
                    break;
                case "denizenenabled":
                    DenizenEnabled = ParseBool(val, DenizenEnabled);
                    break;
                case "denizenpercity":
                    DenizenPerCity = ParseInt(val, DenizenPerCity);
                    break;
                case "favorenabled":
                    FavorEnabled = ParseBool(val, FavorEnabled);
                    break;
                case "favorchance":
                    FavorChance = ParseDouble(val, FavorChance);
                    break;
                case "anomalyenabled":
                    AnomalyEnabled = ParseBool(val, AnomalyEnabled);
                    break;
                case "anomalychance":
                    AnomalyChance = ParseDouble(val, AnomalyChance);
                    break;
                case "anomalyodds":
                    AnomalyOdds = ParseDouble(val, AnomalyOdds);
                    break;
            }
        }

        private static bool ParseBool(string val, bool fallback)
        {
            if (val == null)
                return fallback;

            val = val.Trim().ToLowerInvariant();

            if (val == "1" || val == "true" || val == "yes" || val == "on")
                return true;

            if (val == "0" || val == "false" || val == "no" || val == "off")
                return false;

            return fallback;
        }

        private static int ParseInt(string val, int fallback)
        {
            int result;

            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return result;

            return fallback;
        }

        private static double ParseDouble(string val, double fallback)
        {
            double result;

            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return result;

            return fallback;
        }

        private static void WriteDefault(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (StreamWriter w = new StreamWriter(path, false))
                {
                    w.WriteLine("# LLMNpc.cfg - configuration for LLM-driven NPC dialog.");
                    w.WriteLine("# Edit and run [LLMReload in-game to apply without a restart.");
                    w.WriteLine("#");
                    w.WriteLine("# Master switch. NPCs only call the model when this is true.");
                    w.WriteLine("Enabled=false");
                    w.WriteLine("#");
                    w.WriteLine("# OpenAI-compatible endpoint. Must include /v1 and no trailing slash.");
                    w.WriteLine("# Default: Ollama-direct on the Mac Studio (no auth, local, free).");
                    w.WriteLine("BaseUrl=http://127.0.0.1:11434/v1");
                    w.WriteLine("# LiteLLM gateway alternative (needs a valid virtual key in ApiKey):");
                    w.WriteLine("# BaseUrl=https://your-litellm-gateway.example/v1");
                    w.WriteLine("ApiKey=");
                    w.WriteLine("Model=qwen3-coder:30b");
                    w.WriteLine("#");
                    w.WriteLine("# Generation tuning.");
                    w.WriteLine("Temperature=0.8");
                    w.WriteLine("MaxTokens=120");
                    w.WriteLine("TimeoutMs=9000");
                    w.WriteLine("#");
                    w.WriteLine("# Anti-spam: minimum ms between calls per NPC.");
                    w.WriteLine("CooldownMs=2500");
                    w.WriteLine("# Conversation turns of memory replayed per (player, npc) pair.");
                    w.WriteLine("MaxMemoryTurns=6");
                    w.WriteLine("# Tiles within which an NPC hears nearby speech.");
                    w.WriteLine("HearRange=12");
                    w.WriteLine("# Hard cap on spoken reply length.");
                    w.WriteLine("MaxReplyChars=240");
                    w.WriteLine("#");
                    w.WriteLine("# Accept self-signed TLS (https gateways only).");
                    w.WriteLine("InsecureTls=false");
                    w.WriteLine("# Restrict to one map by name (e.g. Felucca). Empty = any map.");
                    w.WriteLine("AllowedMap=");
                    w.WriteLine("#");
                    w.WriteLine("# ----- Retrieval-augmented lore (cluster Qdrant) -----");
                    w.WriteLine("# When on, replies are grounded with a vector search over a lore");
                    w.WriteLine("# collection. Fail-open: any RAG error just drops the lore for that reply.");
                    w.WriteLine("RagEnabled=false");
                    w.WriteLine("# Qdrant REST base (no trailing slash) and an API key with read access.");
                    w.WriteLine("RagUrl=http://127.0.0.1:6333");
                    w.WriteLine("RagApiKey=");
                    w.WriteLine("RagCollection=uo_lore");
                    w.WriteLine("# Embedding endpoint is Ollama's NATIVE api (no /v1).");
                    w.WriteLine("RagEmbedUrl=http://127.0.0.1:11434");
                    w.WriteLine("RagEmbedModel=nomic-embed-text-v2-moe");
                    w.WriteLine("# Snippets to retrieve, and the minimum cosine score to keep one.");
                    w.WriteLine("RagTopK=3");
                    w.WriteLine("RagMinScore=0.35");
                    w.WriteLine("# Per-call timeout (embed, search) in ms.");
                    w.WriteLine("RagTimeoutMs=6000");
                    w.WriteLine("#");
                    w.WriteLine("# ----- Voice-style grounding (BG3-derived exemplars) -----");
                    w.WriteLine("# When on, replies are seasoned with a few archetype-matched exemplar");
                    w.WriteLine("# lines (cadence/wit, setting stripped) from the bg3_style collection.");
                    w.WriteLine("# Reuses the same Qdrant host + embedding endpoint as RAG above.");
                    w.WriteLine("StyleEnabled=false");
                    w.WriteLine("StyleCollection=bg3_style");
                    w.WriteLine("# Exemplars to retrieve, and the minimum cosine score to keep one.");
                    w.WriteLine("# MinScore=0 keeps stylistic flavor even on a loose topical match.");
                    w.WriteLine("StyleTopK=4");
                    w.WriteLine("StyleMinScore=0.0");
                    w.WriteLine("#");
                    w.WriteLine("# ----- NPC journal (deeds -> Qdrant npc_journal) -----");
                    w.WriteLine("# When on, each completed errand/journey is embedded + stored per-NPC,");
                    w.WriteLine("# and a few of an NPC's own deeds are recalled on the chat path so it");
                    w.WriteLine("# remembers what it has been doing. Reuses the RAG Qdrant host + embed.");
                    w.WriteLine("JournalEnabled=false");
                    w.WriteLine("JournalCollection=npc_journal");
                    w.WriteLine("# Recalled deeds per chat, and the minimum cosine score to keep one.");
                    w.WriteLine("JournalTopK=3");
                    w.WriteLine("JournalMinScore=0.35");
                    w.WriteLine("#");
                    w.WriteLine("# ----- P4: LLM-generated errand purpose text -----");
                    w.WriteLine("# When on, a starting errand/journey may have its deterministic purpose");
                    w.WriteLine("# rewritten by one async, fail-open LLM call into a richer, identity-aware");
                    w.WriteLine("# one-liner. The deterministic purpose always stands as the fallback.");
                    w.WriteLine("ErrandLlmEnabled=false");
                    w.WriteLine("# Chance [0..1] that a starting errand attempts the rewrite.");
                    w.WriteLine("ErrandLlmChance=0.5");
                    w.WriteLine("#");
                    w.WriteLine("# ----- P6: NPC-to-NPC ambient conversation -----");
                    w.WriteLine("# When on, two ordinary townsfolk near each other AND near a watching");
                    w.WriteLine("# player occasionally hold a short two-line LLM exchange grounded in");
                    w.WriteLine("# their identities. Rides the errand heartbeat (no new scan); layered");
                    w.WriteLine("# cooldowns keep it a murmur; fail-open throughout.");
                    w.WriteLine("ChatterEnabled=false");
                    w.WriteLine("# Chance [0..1] that a heartbeat even looks for a pair to start one.");
                    w.WriteLine("ChatterChance=0.2");
                    w.WriteLine("#");
                    w.WriteLine("# ----- P10: daily routines -----");
                    w.WriteLine("# When on, idle NPCs follow a vocation-shaped plan for each game day");
                    w.WriteLine("# (morning task, midday meal at the actual tavern, afternoon bank or");
                    w.WriteLine("# market call, evening stroll) before rolling random errands.");
                    w.WriteLine("RoutineEnabled=false");
                    w.WriteLine("# Chance [0..1] a fresh day-plan also names an LLM 'intention'.");
                    w.WriteLine("RoutineLlmChance=0.35");
                    w.WriteLine("#");
                    w.WriteLine("# ----- P11/P12: town gossip -----");
                    w.WriteLine("# When on, each town keeps a rumor board (player talk, deaths, notable");
                    w.WriteLine("# kills, banishments, word carried by journeyers) that rides the chat");
                    w.WriteLine("# and chatter prompts. No extra LLM calls; persists with world saves.");
                    w.WriteLine("GossipEnabled=false");
                    w.WriteLine("# Chance [0..1] a salient player utterance enters the town's talk.");
                    w.WriteLine("GossipChance=0.2");
                    w.WriteLine("#");
                    w.WriteLine("# ----- P16: denizens — the ambient crowd -----");
                    w.WriteLine("# When on, every classic city is kept populated with LLMDenizen street");
                    w.WriteLine("# folk who errand/journey often, hail passing players, and gossip about");
                    w.WriteLine("# whoever walks by. Off-screen denizens cost nothing.");
                    w.WriteLine("DenizenEnabled=false");
                    w.WriteLine("# Target headcount per classic city (16 cities).");
                    w.WriteLine("DenizenPerCity=80");
                    w.WriteLine("#");
                    w.WriteLine("# ----- P15: favors — NPC-given delivery errands -----");
                    w.WriteLine("# When on, townsfolk may entrust players with sealed parcels for the");
                    w.WriteLine("# banker/smith/tavernkeeper of another town. Deterministic destination,");
                    w.WriteLine("# reward, and cooldowns; the LLM only picks the moment ([do:offer]).");
                    w.WriteLine("FavorEnabled=false");
                    w.WriteLine("# Chance [0..1] an eligible chat rolls an offer (asking for work = always).");
                    w.WriteLine("FavorChance=0.2");
                    w.WriteLine("#");
                    w.WriteLine("# ----- P9: rare 4th-wall / anomaly events -----");
                    w.WriteLine("# When on, an NPC near a watching player can VERY rarely crack: realize");
                    w.WriteLine("# it is an AI in a game and panic (the Overseer manifests and banishes");
                    w.WriteLine("# it), weep a glitch-tear, prophesy, defect, or loop a deja-vu. Rides");
                    w.WriteLine("# the errand heartbeat; deterministic timeline + opportunistic LLM");
                    w.WriteLine("# lines; fail-open. Cooldown + odds keep it ~one event per active hour.");
                    w.WriteLine("AnomalyEnabled=false");
                    w.WriteLine("# Chance [0..1] a heartbeat even looks for a subject.");
                    w.WriteLine("AnomalyChance=0.05");
                    w.WriteLine("# Final chance [0..1] the looked-at NPC actually cracks (honors 1/100).");
                    w.WriteLine("AnomalyOdds=0.01");
                }

                Console.WriteLine("LLMNpc: wrote default config to " + path);
            }
            catch (Exception e)
            {
                Console.WriteLine("LLMNpc: failed to write default config: " + e.Message);
            }
        }
    }
}
