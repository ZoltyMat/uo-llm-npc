using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Server.Custom.LLMNpc
{
    // Retrieval-augmented lore lookup against the cluster Qdrant collection.
    //
    // Two blocking http calls: embed the query (Ollama native /api/embed), then
    // vector-search the lore collection (Qdrant REST). MUST run on a background
    // thread — it is only ever called from inside LLMClient's request thread,
    // never from the game loop. Fail-open everywhere: any error returns "" so the
    // NPC still answers, just without grounded lore.
    public static class LLMRag
    {
        private static bool m_TlsConfigured;

        // Embeds the query, searches the lore collection, and returns a formatted
        // block of the top matches (one per line), or "" if nothing usable.
        public static string Retrieve(string query)
        {
            return Retrieve(query, null);
        }

        // Region-aware overload: when region is a town name, the search is filtered
        // to snippets tagged with that region OR `general` (realm-wide lore), so a
        // Minoc NPC draws on Minoc + realm lore but not Moonglow trivia. A null or
        // empty region searches all lore unfiltered.
        public static string Retrieve(string query, string region)
        {
            try
            {
                if (string.IsNullOrEmpty(query))
                    return "";

                double[] vec = Embed(query);
                if (vec == null || vec.Length == 0)
                    return "";

                Dictionary<string, object> filter = BuildRegionFilter(region);

                List<string> hits = Search(vec, LLMConfig.RagCollection,
                    LLMConfig.RagTopK, LLMConfig.RagMinScore, filter);
                if (hits == null || hits.Count == 0)
                    return "";

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0)
                        sb.Append("\n");
                    sb.Append("- ");
                    sb.Append(hits[i]);
                }

                LLMClient.Log("RAG hits=" + hits.Count +
                    " region=" + (string.IsNullOrEmpty(region) ? "*" : region) +
                    " q=" + Truncate(query, 60));
                return sb.ToString();
            }
            catch (Exception e)
            {
                LLMClient.Log("RAG-ERROR " + e.GetType().Name + ": " + e.Message);
                return "";
            }
        }

        // Embeds the utterance and pulls a few archetype-matched voice exemplars
        // from the bg3_style collection — cadence/wit examples, setting stripped.
        // Returns a formatted block (one quoted line each) or "" on any miss/error.
        // Same fail-open contract as Retrieve; bg thread only.
        public static string RetrieveStyle(string archetype, string query)
        {
            try
            {
                if (string.IsNullOrEmpty(archetype) || string.IsNullOrEmpty(query))
                    return "";

                double[] vec = Embed(query);
                if (vec == null || vec.Length == 0)
                    return "";

                List<object> must = new List<object>();
                must.Add(MatchCond("archetype", archetype));

                Dictionary<string, object> filter = new Dictionary<string, object>();
                filter["must"] = must;

                List<string> hits = Search(vec, LLMConfig.StyleCollection,
                    LLMConfig.StyleTopK, LLMConfig.StyleMinScore, filter);
                if (hits == null || hits.Count == 0)
                    return "";

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0)
                        sb.Append("\n");
                    sb.Append("- \"");
                    sb.Append(hits[i]);
                    sb.Append("\"");
                }

                LLMClient.Log("STYLE hits=" + hits.Count + " arch=" + archetype +
                    " q=" + Truncate(query, 60));
                return sb.ToString();
            }
            catch (Exception e)
            {
                LLMClient.Log("STYLE-ERROR " + e.GetType().Name + ": " + e.Message);
                return "";
            }
        }

        // WRITE path. Embeds a completed deed and upserts it to the per-NPC journal
        // collection, keyed by the NPC's serial (stored as a string payload so the
        // same keyword MatchCond used for retrieval applies). Called from the GAME
        // LOOP (ErrandDirector, via LLMAmbientMemory.AppendJournal), so it spawns its
        // own background thread for the blocking embed + Qdrant calls. Fail-open:
        // any error is logged and swallowed; a lost journal entry never affects play.
        public static void StoreJournal(int npcSerial, string deed)
        {
            if (!LLMConfig.JournalEnabled || string.IsNullOrEmpty(deed))
                return;

            string serialStr = npcSerial.ToString();
            string text = deed;

            Thread t = new Thread(delegate()
            {
                try
                {
                    double[] vec = Embed(text);
                    if (vec == null || vec.Length == 0)
                        return;

                    Upsert(serialStr, text, vec, false);
                    LLMClient.Log("JOURNAL-STORE npc=" + serialStr + " deed=" + Truncate(text, 60));
                }
                catch (Exception e)
                {
                    LLMClient.Log("JOURNAL-STORE-ERROR " + e.GetType().Name + ": " + e.Message);
                }
            });

            t.IsBackground = true;
            t.Start();
        }

        // DIAGNOSTIC. Runs the full journal WRITE+READ round-trip synchronously and
        // returns a single human-readable status line (no game-loop access; call from
        // a background thread). Bypasses the JournalEnabled gate on purpose so it can
        // tell apart "disabled by config" from "write actually fails" — it reports the
        // live JournalEnabled value, the embed dim, the upsert outcome (incl. any
        // caught exception type+message), and a same-vector read-back count.
        public static string JournalSelfTest(int npcSerial)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Enabled=").Append(LLMConfig.JournalEnabled);
            sb.Append(" Coll=").Append(LLMConfig.JournalCollection);
            sb.Append(" Url=").Append(LLMConfig.RagUrl);

            string serialStr = npcSerial.ToString();
            string deed = "SELFTEST deed " + Guid.NewGuid().ToString("N").Substring(0, 8);

            double[] vec;
            try
            {
                vec = Embed(deed);
            }
            catch (Exception ex)
            {
                return sb.Append(" | EMBED THREW ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).ToString();
            }
            if (vec == null || vec.Length == 0)
                return sb.Append(" | EMBED NULL/empty").ToString();
            sb.Append(" | embedDim=").Append(vec.Length);

            try
            {
                Upsert(serialStr, deed, vec, true);
                sb.Append(" | UPSERT OK");
            }
            catch (Exception ex)
            {
                return sb.Append(" | UPSERT THREW ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).ToString();
            }

            try
            {
                List<object> must = new List<object>();
                must.Add(MatchCond("npc", serialStr));
                Dictionary<string, object> filter = new Dictionary<string, object>();
                filter["must"] = must;

                List<string> hits = Search(vec, LLMConfig.JournalCollection, 5, 0.0, filter);
                sb.Append(" | READBACK hits=").Append(hits == null ? 0 : hits.Count);
            }
            catch (Exception ex)
            {
                sb.Append(" | READBACK THREW ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            }

            return sb.ToString();
        }

        // READ path. Embeds the player's utterance and pulls the NPC's own most
        // relevant recent deeds (filtered to this NPC's serial) so it can recall
        // what it has been up to. Returns a formatted block or "" on any miss/error.
        // Same fail-open contract as Retrieve; bg thread only (called from inside
        // LLMClient's request thread).
        public static string RetrieveJournal(int npcSerial, string query)
        {
            try
            {
                if (string.IsNullOrEmpty(query))
                    return "";

                double[] vec = Embed(query);
                if (vec == null || vec.Length == 0)
                    return "";

                List<object> must = new List<object>();
                must.Add(MatchCond("npc", npcSerial.ToString()));

                Dictionary<string, object> filter = new Dictionary<string, object>();
                filter["must"] = must;

                List<string> hits = Search(vec, LLMConfig.JournalCollection,
                    LLMConfig.JournalTopK, LLMConfig.JournalMinScore, filter);
                if (hits == null || hits.Count == 0)
                    return "";

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0)
                        sb.Append("\n");
                    sb.Append("- ");
                    sb.Append(hits[i]);
                }

                LLMClient.Log("JOURNAL hits=" + hits.Count + " npc=" + npcSerial +
                    " q=" + Truncate(query, 60));
                return sb.ToString();
            }
            catch (Exception e)
            {
                LLMClient.Log("JOURNAL-ERROR " + e.GetType().Name + ": " + e.Message);
                return "";
            }
        }

        // Upserts a single journal point (UUID id, npc/text/ts payload) into the
        // journal collection via Qdrant's PUT points endpoint. bg thread only.
        private static void Upsert(string npcSerial, string text, double[] vec, bool wait)
        {
            string endpoint = LLMConfig.RagUrl.TrimEnd('/') + "/collections/" +
                LLMConfig.JournalCollection + "/points";
            if (wait)
                endpoint += "?wait=true";

            ConfigureTls(endpoint);

            List<object> vecList = new List<object>(vec.Length);
            for (int i = 0; i < vec.Length; i++)
                vecList.Add(vec[i]);

            long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            Dictionary<string, object> pl = new Dictionary<string, object>();
            pl["npc"] = npcSerial;
            pl["text"] = text;
            pl["ts"] = ts;

            Dictionary<string, object> point = new Dictionary<string, object>();
            point["id"] = Guid.NewGuid().ToString();
            point["vector"] = vecList;
            point["payload"] = pl;

            List<object> points = new List<object>();
            points.Add(point);

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["points"] = points;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            byte[] payload = Encoding.UTF8.GetBytes(ser.Serialize(body));

            Send("PUT", endpoint, payload, LLMConfig.RagApiKey);
        }

        private static double[] Embed(string text)
        {
            string endpoint = LLMConfig.RagEmbedUrl.TrimEnd('/') + "/api/embed";

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = LLMConfig.RagEmbedModel;
            body["input"] = text;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            byte[] payload = Encoding.UTF8.GetBytes(ser.Serialize(body));

            string respText = Post(endpoint, payload, null);

            object parsed = ser.DeserializeObject(respText);
            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null)
                return null;

            object embObj;
            if (!root.TryGetValue("embeddings", out embObj))
                return null;

            object[] embArr = embObj as object[];
            if (embArr == null || embArr.Length == 0)
                return null;

            object[] first = embArr[0] as object[];
            if (first == null)
                return null;

            double[] vec = new double[first.Length];
            for (int i = 0; i < first.Length; i++)
                vec[i] = Convert.ToDouble(first[i]);

            return vec;
        }

        // Builds a single Qdrant keyword-match condition: { key, match: { value } }.
        private static Dictionary<string, object> MatchCond(string key, string value)
        {
            Dictionary<string, object> matchVal = new Dictionary<string, object>();
            matchVal["value"] = value;

            Dictionary<string, object> cond = new Dictionary<string, object>();
            cond["key"] = key;
            cond["match"] = matchVal;
            return cond;
        }

        // region == <town> OR region == general. A null/empty region returns no
        // filter (all lore is searched). Matching is exact — region values are
        // stored verbatim, see uo-lore/README.md.
        private static Dictionary<string, object> BuildRegionFilter(string region)
        {
            if (string.IsNullOrEmpty(region))
                return null;

            List<object> should = new List<object>();
            should.Add(MatchCond("region", region));
            if (!region.Equals("general", StringComparison.OrdinalIgnoreCase))
                should.Add(MatchCond("region", "general"));

            Dictionary<string, object> filter = new Dictionary<string, object>();
            filter["should"] = should;
            return filter;
        }

        // Vector-searches one Qdrant collection and returns the `text` payload of
        // each kept hit. filter, when non-null, is passed straight through as the
        // Qdrant `filter` clause (a `must` for the style collection, a `should`
        // region clause for lore).
        private static List<string> Search(double[] vec, string collection, int topK, double minScore, Dictionary<string, object> filter)
        {
            string endpoint = LLMConfig.RagUrl.TrimEnd('/') + "/collections/" +
                collection + "/points/search";

            ConfigureTls(endpoint);

            List<object> vecList = new List<object>(vec.Length);
            for (int i = 0; i < vec.Length; i++)
                vecList.Add(vec[i]);

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["vector"] = vecList;
            body["limit"] = topK;
            body["with_payload"] = true;

            if (filter != null)
                body["filter"] = filter;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            byte[] payload = Encoding.UTF8.GetBytes(ser.Serialize(body));

            string apiKey = LLMConfig.RagApiKey;
            string respText = Post(endpoint, payload, apiKey);

            object parsed = ser.DeserializeObject(respText);
            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null)
                return null;

            object resObj;
            if (!root.TryGetValue("result", out resObj))
                return null;

            object[] arr = resObj as object[];
            if (arr == null)
                return null;

            List<string> hits = new List<string>();

            for (int i = 0; i < arr.Length; i++)
            {
                Dictionary<string, object> hit = arr[i] as Dictionary<string, object>;
                if (hit == null)
                    continue;

                double score = 0.0;
                object scoreObj;
                if (hit.TryGetValue("score", out scoreObj) && scoreObj != null)
                    score = Convert.ToDouble(scoreObj);

                if (score < minScore)
                    continue;

                object payloadObj;
                if (!hit.TryGetValue("payload", out payloadObj))
                    continue;

                Dictionary<string, object> pl = payloadObj as Dictionary<string, object>;
                if (pl == null)
                    continue;

                object textObj;
                if (pl.TryGetValue("text", out textObj) && textObj != null)
                {
                    string t = textObj.ToString().Trim();
                    if (t.Length > 0)
                        hits.Add(t);
                }
            }

            return hits;
        }

        private static string Post(string endpoint, byte[] payload, string apiKey)
        {
            return Send("POST", endpoint, payload, apiKey);
        }

        private static string Send(string method, string endpoint, byte[] payload, string apiKey)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = method;
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.Timeout = LLMConfig.RagTimeoutMs;
            req.ReadWriteTimeout = LLMConfig.RagTimeoutMs;
            req.ContentLength = payload.Length;
            req.KeepAlive = false;

            if (!string.IsNullOrEmpty(apiKey))
                req.Headers["api-key"] = apiKey;

            using (Stream rs = req.GetRequestStream())
            {
                rs.Write(payload, 0, payload.Length);
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void ConfigureTls(string endpoint)
        {
            if (m_TlsConfigured)
                return;

            m_TlsConfigured = true;

            if (!endpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase))
                return;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (LLMConfig.InsecureTls)
            {
                ServicePointManager.ServerCertificateValidationCallback =
                    delegate(object s, System.Security.Cryptography.X509Certificates.X509Certificate c, System.Security.Cryptography.X509Certificates.X509Chain ch, SslPolicyErrors er) { return true; };
            }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            if (s.Length <= n)
                return s;

            return s.Substring(0, n);
        }
    }
}
