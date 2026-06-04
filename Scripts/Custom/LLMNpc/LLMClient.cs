using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Server.Custom.LLMNpc
{
    // One chat message in OpenAI format.
    public class LLMMessage
    {
        public string Role;
        public string Content;

        public LLMMessage(string role, string content)
        {
            this.Role = role;
            this.Content = content;
        }
    }

    // Delivered on the MAIN game thread.
    public delegate void LLMResultCallback(bool success, string reply);

    // Fire-and-forget OpenAI-compatible chat client.
    // HTTP runs on a background thread; the result is marshaled back onto the
    // single-threaded game loop via Timer.DelayCall before the callback fires.
    public static class LLMClient
    {
        private static readonly object m_Sync = new object();
        private static readonly Dictionary<string, long> m_LastCall = new Dictionary<string, long>();
        private static readonly HashSet<string> m_InFlight = new HashSet<string>();

        private static readonly object m_LogSync = new object();
        private static bool m_TlsConfigured;

        private static readonly Regex m_Think = new Regex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex m_Ws = new Regex(@"\s+");

        // Returns true if a request was dispatched; false if disabled or throttled.
        // ragQuery, when non-empty and RagEnabled, is embedded + vector-searched on
        // the background thread to ground the reply in retrieved lore. ragRegion,
        // when non-empty, scopes that lore search to the NPC's town + realm-wide
        // lore (see LLMRag.Retrieve). styleArchetype, when non-empty and
        // StyleEnabled, pulls archetype-matched voice exemplars from the bg3_style
        // collection and injects them as cadence anchors. journalKey, when nonzero
        // and JournalEnabled, retrieves that NPC's own most relevant recent deeds
        // (filtered to its serial) from the npc_journal collection so it can recall
        // what it has been doing.
        public static bool TryDispatch(string key, string systemPrompt, List<LLMMessage> messages, string ragQuery, string ragRegion, string styleArchetype, int journalKey, LLMResultCallback callback)
        {
            LLMConfig.EnsureLoaded();

            if (!LLMConfig.Enabled)
                return false;

            if (key == null)
                key = "";

            long now = NowMs();

            lock (m_Sync)
            {
                if (m_InFlight.Contains(key))
                    return false;

                long last;
                if (m_LastCall.TryGetValue(key, out last) && (now - last) < LLMConfig.CooldownMs)
                    return false;

                m_LastCall[key] = now;
                m_InFlight.Add(key);
            }

            string url = LLMConfig.BaseUrl;
            string apiKey = LLMConfig.ApiKey;
            string model = LLMConfig.Model;
            double temp = LLMConfig.Temperature;
            int maxTokens = LLMConfig.MaxTokens;
            int timeout = LLMConfig.TimeoutMs;
            bool insecure = LLMConfig.InsecureTls;

            Thread t = new Thread(delegate()
            {
                bool ok = false;
                string reply = "";

                try
                {
                    string sysPrompt = systemPrompt;

                    if (LLMConfig.RagEnabled && !string.IsNullOrEmpty(ragQuery))
                    {
                        string lore = LLMRag.Retrieve(ragQuery, ragRegion);
                        if (!string.IsNullOrEmpty(lore))
                            sysPrompt = sysPrompt +
                                "\n\nLore of Britannia you may know (background only; weave in what fits, never quote or list it):\n" + lore;
                    }

                    if (LLMConfig.StyleEnabled && !string.IsNullOrEmpty(styleArchetype) && !string.IsNullOrEmpty(ragQuery))
                    {
                        string style = LLMRag.RetrieveStyle(styleArchetype, ragQuery);
                        if (!string.IsNullOrEmpty(style))
                            sysPrompt = sysPrompt +
                                "\n\nVoice exemplars - emulate the CADENCE, wit, and attitude of these lines, never their content or setting:\n" + style;
                    }

                    if (LLMConfig.JournalEnabled && journalKey != 0 && !string.IsNullOrEmpty(ragQuery))
                    {
                        string deeds = LLMRag.RetrieveJournal(journalKey, ragQuery);
                        if (!string.IsNullOrEmpty(deeds))
                            sysPrompt = sysPrompt +
                                "\n\nDeeds from your own recent memory (mention only if the traveler's words touch on them; never recite or list):\n" + deeds;
                    }

                    reply = DoRequest(url, apiKey, model, temp, maxTokens, timeout, insecure, sysPrompt, messages);
                    reply = Sanitize(reply);
                    ok = reply.Length > 0;
                }
                catch (Exception e)
                {
                    Log("ERROR key=" + key + " " + e.GetType().Name + ": " + e.Message);
                    ok = false;
                    reply = "";
                }

                bool fOk = ok;
                string fReply = reply;

                // Marshal back onto the game loop.
                Server.Timer.DelayCall(TimeSpan.Zero, delegate()
                {
                    lock (m_Sync)
                    {
                        m_InFlight.Remove(key);
                    }

                    try
                    {
                        if (callback != null)
                            callback(fOk, fReply);
                    }
                    catch (Exception e)
                    {
                        Log("CALLBACK-ERROR key=" + key + " " + e.Message);
                    }
                });
            });

            t.IsBackground = true;
            t.Start();
            return true;
        }

        // Connectivity self-test. Bypasses the Enabled flag and cooldown so a GM
        // can verify the endpoint before turning the feature on. Result is delivered
        // on the main thread.
        public static void Ping(string userText, LLMResultCallback callback)
        {
            LLMConfig.EnsureLoaded();

            string url = LLMConfig.BaseUrl;
            string apiKey = LLMConfig.ApiKey;
            string model = LLMConfig.Model;
            double temp = LLMConfig.Temperature;
            int maxTokens = LLMConfig.MaxTokens;
            int timeout = LLMConfig.TimeoutMs;
            bool insecure = LLMConfig.InsecureTls;

            List<LLMMessage> messages = new List<LLMMessage>();
            messages.Add(new LLMMessage("user", string.IsNullOrEmpty(userText) ? "Say a short greeting as a medieval villager." : userText));

            Thread t = new Thread(delegate()
            {
                bool ok = false;
                string reply = "";

                try
                {
                    reply = DoRequest(url, apiKey, model, temp, maxTokens, timeout, insecure,
                        "You are a villager in the medieval world of Ultima Online. Stay in character. One short sentence.", messages);
                    reply = Sanitize(reply);
                    ok = reply.Length > 0;
                }
                catch (Exception e)
                {
                    reply = e.GetType().Name + ": " + e.Message;
                    Log("PING-ERROR " + reply);
                    ok = false;
                }

                bool fOk = ok;
                string fReply = reply;

                Server.Timer.DelayCall(TimeSpan.Zero, delegate()
                {
                    try
                    {
                        if (callback != null)
                            callback(fOk, fReply);
                    }
                    catch (Exception e)
                    {
                        Log("PING-CALLBACK-ERROR " + e.Message);
                    }
                });
            });

            t.IsBackground = true;
            t.Start();
        }

        private static string DoRequest(string baseUrl, string apiKey, string model, double temp, int maxTokens, int timeout, bool insecure, string systemPrompt, List<LLMMessage> messages)
        {
            string endpoint = baseUrl.TrimEnd('/') + "/chat/completions";

            if (insecure && !m_TlsConfigured)
            {
                m_TlsConfigured = true;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                ServicePointManager.ServerCertificateValidationCallback =
                    delegate(object s, System.Security.Cryptography.X509Certificates.X509Certificate c, System.Security.Cryptography.X509Certificates.X509Chain ch, SslPolicyErrors e) { return true; };
            }

            List<object> msgList = new List<object>();

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                Dictionary<string, object> sys = new Dictionary<string, object>();
                sys["role"] = "system";
                sys["content"] = systemPrompt;
                msgList.Add(sys);
            }

            if (messages != null)
            {
                for (int i = 0; i < messages.Count; i++)
                {
                    LLMMessage m = messages[i];
                    if (m == null || string.IsNullOrEmpty(m.Content))
                        continue;

                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["role"] = m.Role;
                    d["content"] = m.Content;
                    msgList.Add(d);
                }
            }

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = model;
            body["temperature"] = temp;
            body["max_tokens"] = maxTokens;
            body["stream"] = false;
            // Thinking models (e.g. gemma4-e4b/Gemma 3n) otherwise spend the
            // whole token budget on chain-of-thought returned in reasoning_content
            // and leave content empty (finish_reason=length). NPC one-liners never
            // want CoT; litellm maps this to the provider's no-think flag.
            body["reasoning_effort"] = "none";
            body["messages"] = msgList;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 4 * 1024 * 1024;
            string json = ser.Serialize(body);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.Timeout = timeout;
            req.ReadWriteTimeout = timeout;
            req.ContentLength = payload.Length;
            req.KeepAlive = false;

            if (!string.IsNullOrEmpty(apiKey))
                req.Headers["Authorization"] = "Bearer " + apiKey;

            using (Stream rs = req.GetRequestStream())
            {
                rs.Write(payload, 0, payload.Length);
            }

            string respText;

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                respText = reader.ReadToEnd();
            }

            string content = ExtractContent(ser, respText);
            Log("OK model=" + model + " in=" + Truncate(LastUser(messages), 80) + " out=" + Truncate(content, 120));
            return content;
        }

        private static string ExtractContent(JavaScriptSerializer ser, string respText)
        {
            object parsed = ser.DeserializeObject(respText);

            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null)
                return "";

            object choicesObj;
            if (!root.TryGetValue("choices", out choicesObj))
                return "";

            object[] choices = choicesObj as object[];
            if (choices == null || choices.Length == 0)
                return "";

            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null)
                return "";

            object messageObj;
            if (first.TryGetValue("message", out messageObj))
            {
                Dictionary<string, object> message = messageObj as Dictionary<string, object>;
                if (message != null)
                {
                    object contentObj;
                    if (message.TryGetValue("content", out contentObj) && contentObj != null)
                        return contentObj.ToString();
                }
            }

            // Fallback for text-completion shaped responses.
            object textObj;
            if (first.TryGetValue("text", out textObj) && textObj != null)
                return textObj.ToString();

            return "";
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            s = m_Think.Replace(s, " ");
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            s = m_Ws.Replace(s, " ").Trim();

            // Strip a single layer of wrapping quotes.
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2).Trim();

            if (s.Length > LLMConfig.MaxReplyChars)
            {
                s = s.Substring(0, LLMConfig.MaxReplyChars);
                int lastSpace = s.LastIndexOf(' ');
                if (lastSpace > LLMConfig.MaxReplyChars - 40 && lastSpace > 0)
                    s = s.Substring(0, lastSpace);
                s = s.TrimEnd() + "...";
            }

            return s;
        }

        private static string LastUser(List<LLMMessage> messages)
        {
            if (messages == null)
                return "";

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] != null && messages[i].Role == "user")
                    return messages[i].Content;
            }

            return "";
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            if (s.Length <= n)
                return s;

            return s.Substring(0, n);
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        public static void Log(string line)
        {
            try
            {
                string path = LLMConfig.LogPath;
                string dir = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                lock (m_LogSync)
                {
                    File.AppendAllText(path, stamp + "  " + line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never crash the caller.
            }
        }
    }
}
