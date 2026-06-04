using System;
using System.Collections.Generic;
using Server;

namespace Server.Custom.LLMNpc
{
    // Disk-backed memory for the EXISTING vanilla NPCs voiced by LLMAmbientSpeech.
    //
    // Those NPCs aren't LLMTalkingMobile subclasses, so they can't serialize their
    // own rolled identity or per-player relationships the way the custom NPCs do.
    // This singleton persistence Item carries both, keyed by mobile Serial (stable
    // across reboots), so a vanilla blacksmith keeps the same character and still
    // remembers who has been kind to them after a server restart.
    //
    // Standard ServUO persistence-singleton pattern (cf. EthicsPersistence): a
    // Movable=false Item that never enters the world map; its Serialize/Deserialize
    // ride the normal world save. The maps are static so callers reach them without
    // holding the instance.
    public class LLMAmbientMemory : Item
    {
        private static LLMAmbientMemory m_Instance;

        // npcSerial -> rolled identity
        private static readonly Dictionary<int, NpcIdentity> m_Identities =
            new Dictionary<int, NpcIdentity>();

        // npcSerial -> (playerSerial -> relationship)
        private static readonly Dictionary<int, Dictionary<int, NpcRelationship>> m_Relationships =
            new Dictionary<int, Dictionary<int, NpcRelationship>>();

        // npcSerial -> current errand (driven by ErrandDirector)
        private static readonly Dictionary<int, Errand> m_Errands =
            new Dictionary<int, Errand>();

        // npcSerial -> recent completed-errand deeds (newest last; capped). P3 will
        // embed these into the npc_journal Qdrant collection.
        private static readonly Dictionary<int, List<string>> m_Journals =
            new Dictionary<int, List<string>>();

        private const int JournalCap = 8;

        public static LLMAmbientMemory Instance { get { return m_Instance; } }

        public override string DefaultName { get { return "LLM Ambient Memory - Internal"; } }

        [Constructable]
        public LLMAmbientMemory()
            : base(1)
        {
            Movable = false;

            if (m_Instance == null || m_Instance.Deleted)
                m_Instance = this;
            else
                base.Delete();
        }

        public LLMAmbientMemory(Serial serial)
            : base(serial)
        {
            m_Instance = this;
        }

        // Creates the singleton on first boot if no save carried one. Safe to call
        // after world load — a deserialized instance has already claimed m_Instance.
        public static void EnsureExists()
        {
            if (m_Instance == null || m_Instance.Deleted)
                new LLMAmbientMemory();
        }

        public override void Delete()
        {
            // Singleton: never deleted out from under the live memory maps.
        }

        // ---- identity ---------------------------------------------------------

        public static bool TryGetIdentity(int npcSerial, out NpcIdentity id)
        {
            return m_Identities.TryGetValue(npcSerial, out id);
        }

        public static void SetIdentity(int npcSerial, NpcIdentity id)
        {
            if (id != null)
                m_Identities[npcSerial] = id;
        }

        // ---- relationships ----------------------------------------------------

        public static NpcRelationship GetRelationship(int npcSerial, int playerSerial)
        {
            Dictionary<int, NpcRelationship> inner;
            if (!m_Relationships.TryGetValue(npcSerial, out inner))
                return null;

            NpcRelationship rel;
            if (inner.TryGetValue(playerSerial, out rel))
                return rel;

            return null;
        }

        public static NpcRelationship GetOrCreateRelationship(int npcSerial, int playerSerial)
        {
            Dictionary<int, NpcRelationship> inner;
            if (!m_Relationships.TryGetValue(npcSerial, out inner))
            {
                inner = new Dictionary<int, NpcRelationship>();
                m_Relationships[npcSerial] = inner;
            }

            NpcRelationship rel;
            if (!inner.TryGetValue(playerSerial, out rel))
            {
                rel = new NpcRelationship();
                inner[playerSerial] = rel;
            }

            return rel;
        }

        // ---- errands ----------------------------------------------------------

        public static bool TryGetErrand(int npcSerial, out Errand errand)
        {
            return m_Errands.TryGetValue(npcSerial, out errand);
        }

        // Returns the NPC's errand record, creating an idle one on first sight. New
        // records get a randomized first-decision time so a crowd of NPCs doesn't
        // all set off the moment a player walks into town.
        public static Errand GetOrCreateErrand(int npcSerial, DateTime now)
        {
            Errand e;
            if (m_Errands.TryGetValue(npcSerial, out e))
                return e;

            e = new Errand();
            e.NextDecisionUtc = now.AddSeconds(Utility.RandomMinMax(30, 240));
            m_Errands[npcSerial] = e;
            return e;
        }

        // ---- journal ----------------------------------------------------------

        public static void AppendJournal(int npcSerial, string deed)
        {
            if (string.IsNullOrEmpty(deed))
                return;

            List<string> log;
            if (!m_Journals.TryGetValue(npcSerial, out log))
            {
                log = new List<string>();
                m_Journals[npcSerial] = log;
            }

            log.Add(deed);

            while (log.Count > JournalCap)
                log.RemoveAt(0);

            // Embed + persist the deed to the Qdrant journal collection (bg thread,
            // gated by JournalEnabled, fail-open). This is the single choke point for
            // "a new deed happened", so it captures both local errands and journeys.
            LLMRag.StoreJournal(npcSerial, deed);
        }

        // Serials of NPCs persisted mid-journey. After a reboot the director re-seeds
        // its journey set from this so an NPC saved abroad (with no player nearby to
        // tick it) still recalls home when its stay elapses.
        public static List<int> GetJourneyingSerials()
        {
            List<int> result = new List<int>();

            foreach (KeyValuePair<int, Errand> kv in m_Errands)
                if (kv.Value != null && kv.Value.Active && kv.Value.Journey)
                    result.Add(kv.Key);

            return result;
        }

        // Counts errand records and how many are mid-errand (for [ErrandStatus).
        public static void ErrandStats(out int total, out int active)
        {
            total = m_Errands.Count;
            active = 0;

            foreach (KeyValuePair<int, Errand> kv in m_Errands)
                if (kv.Value != null && kv.Value.Active)
                    active++;
        }

        public static List<string> GetJournal(int npcSerial)
        {
            List<string> log;
            if (m_Journals.TryGetValue(npcSerial, out log))
                return log;

            return new List<string>();
        }

        // The NPC's most recent completed deed, or "" if it has done nothing yet.
        public static string LastJournal(int npcSerial)
        {
            List<string> log;
            if (m_Journals.TryGetValue(npcSerial, out log) && log.Count > 0)
                return log[log.Count - 1];

            return "";
        }

        // Removes memory for NPC serials that no longer resolve to a live mobile, so
        // recycled/decayed spawns don't accumulate dead entries across reboots. Run
        // once after world load (Initialize), when all mobiles are present.
        public static void Prune()
        {
            List<int> dead = new List<int>();

            foreach (int s in m_Identities.Keys)
                if (World.FindMobile((Serial)s) == null)
                    dead.Add(s);

            for (int i = 0; i < dead.Count; i++)
                m_Identities.Remove(dead[i]);

            dead.Clear();

            foreach (int s in m_Relationships.Keys)
                if (World.FindMobile((Serial)s) == null)
                    dead.Add(s);

            for (int i = 0; i < dead.Count; i++)
                m_Relationships.Remove(dead[i]);

            dead.Clear();

            foreach (int s in m_Errands.Keys)
                if (World.FindMobile((Serial)s) == null)
                    dead.Add(s);

            for (int i = 0; i < dead.Count; i++)
                m_Errands.Remove(dead[i]);

            dead.Clear();

            foreach (int s in m_Journals.Keys)
                if (World.FindMobile((Serial)s) == null)
                    dead.Add(s);

            for (int i = 0; i < dead.Count; i++)
                m_Journals.Remove(dead[i]);
        }

        // ---- persistence ------------------------------------------------------

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write((int)1); // version (1 adds errands + journals)

            writer.Write(m_Identities.Count);
            foreach (KeyValuePair<int, NpcIdentity> kv in m_Identities)
            {
                writer.Write(kv.Key);
                kv.Value.Serialize(writer);
            }

            writer.Write(m_Relationships.Count);
            foreach (KeyValuePair<int, Dictionary<int, NpcRelationship>> outer in m_Relationships)
            {
                writer.Write(outer.Key);
                writer.Write(outer.Value.Count);

                foreach (KeyValuePair<int, NpcRelationship> inner in outer.Value)
                {
                    writer.Write(inner.Key);
                    inner.Value.Serialize(writer);
                }
            }

            // v1: current errands
            writer.Write(m_Errands.Count);
            foreach (KeyValuePair<int, Errand> kv in m_Errands)
            {
                writer.Write(kv.Key);
                kv.Value.Serialize(writer);
            }

            // v1: completed-deed journals
            writer.Write(m_Journals.Count);
            foreach (KeyValuePair<int, List<string>> kv in m_Journals)
            {
                writer.Write(kv.Key);
                writer.Write(kv.Value.Count);

                for (int i = 0; i < kv.Value.Count; i++)
                    writer.Write(kv.Value[i] == null ? "" : kv.Value[i]);
            }
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            int version = reader.ReadInt();

            m_Identities.Clear();
            int idCount = reader.ReadInt();
            for (int i = 0; i < idCount; i++)
            {
                int npcSerial = reader.ReadInt();
                NpcIdentity id = new NpcIdentity();
                id.Deserialize(reader);
                m_Identities[npcSerial] = id;
            }

            m_Relationships.Clear();
            int relCount = reader.ReadInt();
            for (int i = 0; i < relCount; i++)
            {
                int npcSerial = reader.ReadInt();
                int innerCount = reader.ReadInt();

                Dictionary<int, NpcRelationship> map = new Dictionary<int, NpcRelationship>();
                for (int j = 0; j < innerCount; j++)
                {
                    int playerSerial = reader.ReadInt();
                    NpcRelationship rel = new NpcRelationship();
                    rel.Deserialize(reader);
                    map[playerSerial] = rel;
                }

                m_Relationships[npcSerial] = map;
            }

            m_Errands.Clear();
            m_Journals.Clear();

            // v0 saves carried neither errands nor journals; leave both empty.
            if (version >= 1)
            {
                int errCount = reader.ReadInt();
                for (int i = 0; i < errCount; i++)
                {
                    int npcSerial = reader.ReadInt();
                    Errand e = new Errand();
                    e.Deserialize(reader);
                    m_Errands[npcSerial] = e;
                }

                int jCount = reader.ReadInt();
                for (int i = 0; i < jCount; i++)
                {
                    int npcSerial = reader.ReadInt();
                    int lines = reader.ReadInt();

                    List<string> log = new List<string>();
                    for (int j = 0; j < lines; j++)
                        log.Add(reader.ReadString());

                    m_Journals[npcSerial] = log;
                }
            }
        }
    }
}
