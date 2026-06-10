using System;
using Server;

namespace Server.Custom.LLMNpc
{
    // Phases of a single errand. The ErrandDirector advances these
    // deterministically; the LLM is NEVER consulted to run an errand.
    public enum ErrandState
    {
        None = 0,      // idle at post (native WalkRandomInHome)
        Outbound = 1,  // walking to Destination
        Dwelling = 2,  // milling about Destination until DwellUntilUtc
        Returning = 3  // walking back to Post
    }

    // One NPC's current errand: a self-contained, serializable record kept in
    // LLMAmbientMemory keyed by NPC serial. Movement is driven by retargeting the
    // creature's native Home/RangeHome (not by stepping it ourselves), which keeps
    // the engine's PlayerRangeSensitive proximity gating intact — travel only
    // animates when a player is nearby.
    //
    // All timestamps are UTC. The travel-phase deadline lets the director
    // force-complete an errand that has dragged on (stuck on geometry, or a player
    // lingered for an unrealistic time), so no NPC strands itself off-post.
    public class Errand
    {
        public ErrandState State;
        public string Kind;          // short in-world label, e.g. "fetching ore from the mines"
        public Point3D Destination;  // where the NPC is headed this phase (local for P1)
        public Point3D Post;         // the NPC's home/post, restored when the errand ends
        public int PostRangeHome;    // the NPC's original RangeHome, restored with Post

        // True while this errand is a rare cross-continent trip (P2). Journeys skip
        // the walking phases entirely: the NPC recalls to a far city, dwells there,
        // then recalls home — so a journey lives only in the Dwelling state.
        public bool Journey;

        // The named destination city of a journey (e.g. "Vesper"), so the return
        // leg knows which town's gossip board the NPC carries home (P11).
        public string JourneyCity;

        // Dwell window override in seconds for routine legs (P10) — a tavern
        // lunch lingers longer than a market stop. 0 means the default 20-60s.
        // Runtime-only: a reboot mid-errand just falls back to the default.
        public int DwellMinSec;
        public int DwellMaxSec;

        public DateTime StartedUtc;       // when the current errand began
        public DateTime DwellUntilUtc;    // while Dwelling, when to start heading back
        public DateTime PhaseDeadlineUtc; // hard cap on a travel phase (force-complete past this)
        public DateTime NextDecisionUtc;  // earliest time to roll the NEXT errand (cooldown / stagger)

        // Runtime-only stuck detection; rebuilt on load, never serialized.
        public Point3D LastPos;
        public DateTime LastProgressUtc;
        public bool NudgedSinceProgress;

        public Errand()
        {
            State = ErrandState.None;
            Kind = "";
            PostRangeHome = 6;
            LastProgressUtc = DateTime.UtcNow;
        }

        public bool Active { get { return State != ErrandState.None; } }

        public void Serialize(GenericWriter writer)
        {
            writer.Write((int)2); // errand version (1 adds Journey; 2 adds JourneyCity)

            writer.Write((int)State);
            writer.Write(Kind == null ? "" : Kind);

            writer.Write(Destination);
            writer.Write(Post);
            writer.Write(PostRangeHome);

            writer.Write(StartedUtc);
            writer.Write(DwellUntilUtc);
            writer.Write(PhaseDeadlineUtc);
            writer.Write(NextDecisionUtc);

            writer.Write(Journey);

            writer.Write(JourneyCity == null ? "" : JourneyCity); // v2
        }

        public void Deserialize(GenericReader reader)
        {
            int v = reader.ReadInt();

            State = (ErrandState)reader.ReadInt();
            Kind = reader.ReadString();

            Destination = reader.ReadPoint3D();
            Post = reader.ReadPoint3D();
            PostRangeHome = reader.ReadInt();

            StartedUtc = reader.ReadDateTime();
            DwellUntilUtc = reader.ReadDateTime();
            PhaseDeadlineUtc = reader.ReadDateTime();
            NextDecisionUtc = reader.ReadDateTime();

            // v0 saves (the deployed P1 image) carried no Journey flag.
            Journey = (v >= 1) && reader.ReadBool();

            JourneyCity = (v >= 2) ? reader.ReadString() : "";

            // Runtime-only fields are not persisted; start the stuck clock fresh.
            LastPos = Point3D.Zero;
            LastProgressUtc = DateTime.UtcNow;
            NudgedSinceProgress = false;
        }
    }
}
