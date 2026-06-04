using System.Collections.Generic;

namespace Server.Custom.LLMNpc
{
    // Short-term per-pair conversation memory.
    // Keyed by (npc serial, player serial). Accessed only on the game thread
    // (dispatch happens on-thread; results are marshaled back via Timer.DelayCall),
    // so no locking is needed here.
    public static class LLMConversation
    {
        private static readonly Dictionary<string, List<LLMMessage>> m_History = new Dictionary<string, List<LLMMessage>>();

        private static string Key(int npcSerial, int playerSerial)
        {
            return npcSerial + ":" + playerSerial;
        }

        // Returns a copy of the stored turns, oldest first.
        public static List<LLMMessage> Get(int npcSerial, int playerSerial)
        {
            List<LLMMessage> stored;

            if (m_History.TryGetValue(Key(npcSerial, playerSerial), out stored))
                return new List<LLMMessage>(stored);

            return new List<LLMMessage>();
        }

        public static void Record(int npcSerial, int playerSerial, string role, string content)
        {
            if (string.IsNullOrEmpty(content))
                return;

            string key = Key(npcSerial, playerSerial);

            List<LLMMessage> stored;
            if (!m_History.TryGetValue(key, out stored))
            {
                stored = new List<LLMMessage>();
                m_History[key] = stored;
            }

            stored.Add(new LLMMessage(role, content));

            int max = LLMConfig.MaxMemoryTurns * 2;
            if (max < 2)
                max = 2;

            while (stored.Count > max)
                stored.RemoveAt(0);
        }

        public static void Clear(int npcSerial, int playerSerial)
        {
            m_History.Remove(Key(npcSerial, playerSerial));
        }

        // Drops every conversation involving this NPC (e.g. on deletion).
        public static void ClearNpc(int npcSerial)
        {
            string prefix = npcSerial + ":";
            List<string> dead = new List<string>();

            foreach (string k in m_History.Keys)
            {
                if (k.StartsWith(prefix))
                    dead.Add(k);
            }

            for (int i = 0; i < dead.Count; i++)
                m_History.Remove(dead[i]);
        }
    }
}
