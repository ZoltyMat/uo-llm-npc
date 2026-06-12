using System;
using System.Collections.Generic;
using System.Threading;
using Server;
using Server.Commands;
using Server.Mobiles;
using Server.Targeting;

namespace Server.Custom.LLMNpc
{
    public class LLMNpcCommands
    {
        public static void Initialize()
        {
            CommandSystem.Register("LLMPing", AccessLevel.GameMaster, new CommandEventHandler(LLMPing_OnCommand));
            CommandSystem.Register("LLMReload", AccessLevel.GameMaster, new CommandEventHandler(LLMReload_OnCommand));
            CommandSystem.Register("LLMStatus", AccessLevel.GameMaster, new CommandEventHandler(LLMStatus_OnCommand));
            CommandSystem.Register("AddLLMNpc", AccessLevel.GameMaster, new CommandEventHandler(AddLLMNpc_OnCommand));
            CommandSystem.Register("SummonOverseer", AccessLevel.GameMaster, new CommandEventHandler(SummonOverseer_OnCommand));
            CommandSystem.Register("LLMRag", AccessLevel.GameMaster, new CommandEventHandler(LLMRag_OnCommand));
            CommandSystem.Register("LLMStyle", AccessLevel.GameMaster, new CommandEventHandler(LLMStyle_OnCommand));
            CommandSystem.Register("LLMSelfTest", AccessLevel.GameMaster, new CommandEventHandler(LLMSelfTest_OnCommand));
            CommandSystem.Register("LLMWho", AccessLevel.GameMaster, new CommandEventHandler(LLMWho_OnCommand));
            CommandSystem.Register("ErrandWho", AccessLevel.GameMaster, new CommandEventHandler(ErrandWho_OnCommand));
            CommandSystem.Register("ErrandGo", AccessLevel.GameMaster, new CommandEventHandler(ErrandGo_OnCommand));
            CommandSystem.Register("ErrandTrip", AccessLevel.GameMaster, new CommandEventHandler(ErrandTrip_OnCommand));
            CommandSystem.Register("ErrandStatus", AccessLevel.GameMaster, new CommandEventHandler(ErrandStatus_OnCommand));
            CommandSystem.Register("AnomalyTest", AccessLevel.GameMaster, new CommandEventHandler(AnomalyTest_OnCommand));
            CommandSystem.Register("GossipBoard", AccessLevel.GameMaster, new CommandEventHandler(GossipBoard_OnCommand));
            CommandSystem.Register("GossipSeed", AccessLevel.GameMaster, new CommandEventHandler(GossipSeed_OnCommand));
            CommandSystem.Register("RoutinePlan", AccessLevel.GameMaster, new CommandEventHandler(RoutinePlan_OnCommand));
            CommandSystem.Register("RoutineNow", AccessLevel.GameMaster, new CommandEventHandler(RoutineNow_OnCommand));
            CommandSystem.Register("ObserveTest", AccessLevel.GameMaster, new CommandEventHandler(ObserveTest_OnCommand));
            CommandSystem.Register("OverseerTest", AccessLevel.GameMaster, new CommandEventHandler(OverseerTest_OnCommand));
            CommandSystem.Register("FavorTest", AccessLevel.GameMaster, new CommandEventHandler(FavorTest_OnCommand));
            CommandSystem.Register("FavorDeliver", AccessLevel.GameMaster, new CommandEventHandler(FavorDeliver_OnCommand));
            CommandSystem.Register("DenizenStatus", AccessLevel.GameMaster, new CommandEventHandler(DenizenStatus_OnCommand));
            CommandSystem.Register("DenizenReset", AccessLevel.GameMaster, new CommandEventHandler(DenizenReset_OnCommand));
        }

        [Usage("DenizenStatus")]
        [Description("Shows the P16 denizen population per city versus the configured target.")]
        public static void DenizenStatus_OnCommand(CommandEventArgs e)
        {
            List<string> lines = DenizenDirector.StatusLines();
            for (int i = 0; i < lines.Count; i++)
                e.Mobile.SendMessage(i == 0 ? 0x40 : 0x35, lines[i]);
        }

        [Usage("DenizenReset")]
        [Description("Deletes ALL denizens so the director repopulates them fresh — use after a build that changes denizen stats/kit (P17 arming, density).")]
        public static void DenizenReset_OnCommand(CommandEventArgs e)
        {
            int n = DenizenDirector.ResetAll();
            e.Mobile.SendMessage(0x40, "Removed {0} denizens. The director will repopulate to the configured target over the next few minutes.", n);
        }

        [Usage("FavorTest")]
        [Description("Target an NPC to force a pending parcel-favor it can offer YOU (P15) — then ask it for work.")]
        public static void FavorTest_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target the NPC that should offer you a favor.");
            e.Mobile.Target = new FavorTarget(false);
        }

        [Usage("FavorDeliver")]
        [Description("Target an NPC to deliver the first parcel in your pack to it (P15 testing — same validation as double-click).")]
        public static void FavorDeliver_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target the recipient.");
            e.Mobile.Target = new FavorTarget(true);
        }

        private class FavorTarget : Target
        {
            private readonly bool m_Deliver;

            public FavorTarget(bool deliver)
                : base(12, false, TargetFlags.None)
            {
                m_Deliver = deliver;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                Mobile m = targeted as Mobile;

                if (m == null)
                {
                    from.SendMessage(0x22, "That is not a mobile.");
                    return;
                }

                if (!m_Deliver)
                {
                    string spec = FavorDirector.ForcePending(m, from);
                    from.SendMessage(0x40, "{0} now has a favor to offer you: a parcel for the {1}. Ask them for work.", m.Name, spec);
                    return;
                }

                FavorParcel parcel = null;
                if (from.Backpack != null)
                {
                    System.Collections.Generic.List<Item> items = from.Backpack.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        parcel = items[i] as FavorParcel;
                        if (parcel != null)
                            break;
                    }
                }

                if (parcel == null)
                {
                    from.SendMessage(0x22, "You carry no parcel.");
                    return;
                }

                FavorDirector.TryDeliver(parcel, from, m);
            }
        }

        [Usage("ObserveTest")]
        [Description("Target a player to force a P13 observation rumor onto your current town's board (bypasses all gates).")]
        public static void ObserveTest_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target a player to observe.");
            e.Mobile.Target = new ObserveTarget();
        }

        private class ObserveTarget : Target
        {
            public ObserveTarget()
                : base(12, false, TargetFlags.None)
            {
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                Mobile player = targeted as Mobile;

                if (player == null)
                {
                    from.SendMessage(0x22, "That is not a mobile.");
                    return;
                }

                string text = TownGossip.ForceObservation(player, from);

                if (string.IsNullOrEmpty(text))
                    from.SendMessage(0x22, "Nothing about {0} was worth a rumor (no notable gear, karma, fame, or grandmaster skill).", player.Name);
                else
                    from.SendMessage(0x40, "Boarded in {0}: {1}", BritanniaGeography.TownOf(from), text);
            }
        }

        [Usage("OverseerTest <verb>")]
        [Description("Target an Overseer to run one of its P14 powers directly (storm|rats|wolves|orcs|undead|bless|gift|depart). You are the supplicant.")]
        public static void OverseerTest_OnCommand(CommandEventArgs e)
        {
            string verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "";

            if (!OverseerActions.IsGmVerb(verb))
            {
                e.Mobile.SendMessage(0x22, "Usage: OverseerTest storm|rats|wolves|orcs|undead|bless|gift|depart");
                return;
            }

            e.Mobile.SendMessage(0x35, "Target an Overseer to perform '{0}'.", verb);
            e.Mobile.Target = new OverseerTarget(verb);
        }

        private class OverseerTarget : Target
        {
            private readonly string m_Verb;

            public OverseerTarget(string verb)
                : base(12, false, TargetFlags.None)
            {
                m_Verb = verb;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                LLMOverseer overseer = targeted as LLMOverseer;

                if (overseer == null)
                {
                    from.SendMessage(0x22, "That is not an Overseer.");
                    return;
                }

                OverseerActions.Perform(overseer, from, m_Verb);
                from.SendMessage(0x40, "{0} performs '{1}'.", overseer.Name, m_Verb);
            }
        }

        [Usage("GossipBoard")]
        [Description("Lists the rumor board of the town you are standing in (P11/P12).")]
        public static void GossipBoard_OnCommand(CommandEventArgs e)
        {
            string town = BritanniaGeography.TownOf(e.Mobile);
            e.Mobile.SendMessage(0x40, "Talk of {0}:", town);

            List<string> lines = TownGossip.StatusLines(town);
            for (int i = 0; i < lines.Count; i++)
                e.Mobile.SendMessage(0x35, lines[i]);
        }

        [Usage("GossipSeed <text>")]
        [Description("Injects a rumor onto the board of the town you are standing in (testing).")]
        public static void GossipSeed_OnCommand(CommandEventArgs e)
        {
            string text = e.ArgString == null ? "" : e.ArgString.Trim();

            if (text.Length == 0)
            {
                e.Mobile.SendMessage(0x22, "Usage: GossipSeed <text>");
                return;
            }

            string town = BritanniaGeography.TownOf(e.Mobile);
            TownGossip.Add(town, text);
            e.Mobile.SendMessage(0x40, "Seeded onto the {0} board: {1}", town, text);
        }

        [Usage("RoutinePlan")]
        [Description("Target an NPC to see its day plan: legs, done flags, and today's intention (P10).")]
        public static void RoutinePlan_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target an NPC to inspect its day plan.");
            e.Mobile.Target = new RoutineTarget(false);
        }

        [Usage("RoutineNow")]
        [Description("Target an NPC to force its next undone day-plan leg due immediately (testing).")]
        public static void RoutineNow_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target an NPC to force its next routine leg now.");
            e.Mobile.Target = new RoutineTarget(true);
        }

        private class RoutineTarget : Target
        {
            private readonly bool m_Force;

            public RoutineTarget(bool force)
                : base(12, false, TargetFlags.None)
            {
                m_Force = force;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                BaseCreature npc = targeted as BaseCreature;

                if (npc == null)
                {
                    from.SendMessage(0x22, "That is not an NPC.");
                    return;
                }

                if (m_Force)
                {
                    if (DailyRoutine.ForceNextLeg(npc))
                        from.SendMessage(0x40, "{0}'s next routine leg is due now (it starts on the director's next decision pass).", npc.Name);
                    else
                        from.SendMessage(0x22, "{0} has no undone routine leg today (or no plan yet — let it idle near you first).", npc.Name);
                    return;
                }

                List<string> lines = DailyRoutine.PlanLines(npc);
                from.SendMessage(0x40, "{0}'s day:", npc.Name);
                for (int i = 0; i < lines.Count; i++)
                    from.SendMessage(0x35, lines[i]);
            }
        }

        [Usage("AnomalyTest [crisis|tear|prophet|defector|dejavu]")]
        [Description("Target any NPC to force a 4th-wall/anomaly scene now, bypassing all rarity gates (testing). Defaults to crisis.")]
        public static void AnomalyTest_OnCommand(CommandEventArgs e)
        {
            string scenario = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "crisis";
            e.Mobile.SendMessage(0x35, "Target an NPC to force the '{0}' anomaly.", scenario);
            e.Mobile.Target = new AnomalyTarget(scenario);
        }

        private class AnomalyTarget : Target
        {
            private readonly string m_Scenario;

            public AnomalyTarget(string scenario)
                : base(12, false, TargetFlags.None)
            {
                m_Scenario = scenario;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                BaseCreature npc = targeted as BaseCreature;

                if (npc == null)
                {
                    from.SendMessage(0x22, "That is not an NPC.");
                    return;
                }

                bool ok = AnomalyDirector.Force(npc, m_Scenario, from);

                if (ok)
                    from.SendMessage(0x40, "{0} begins the '{1}' anomaly.", npc.Name, m_Scenario);
                else
                    from.SendMessage(0x22, "Unknown anomaly '{0}'. Use crisis|tear|prophet|defector|dejavu.", m_Scenario);
            }
        }

        [Usage("ErrandWho")]
        [Description("Target any NPC to see its mobility class, current errand, and recent deeds.")]
        public static void ErrandWho_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target an NPC to inspect its errand.");
            e.Mobile.Target = new ErrandTarget(ErrandAction.Inspect);
        }

        [Usage("ErrandGo")]
        [Description("Target any NPC to force it to set off on a local errand immediately (testing).")]
        public static void ErrandGo_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target an NPC to send it on an errand now.");
            e.Mobile.Target = new ErrandTarget(ErrandAction.ForceLocal);
        }

        [Usage("ErrandTrip")]
        [Description("Target any NPC to force it to recall off on a cross-continent journey now (testing).")]
        public static void ErrandTrip_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target an NPC to send it on a cross-continent journey now.");
            e.Mobile.Target = new ErrandTarget(ErrandAction.ForceJourney);
        }

        [Usage("ErrandStatus")]
        [Description("Shows the ErrandDirector state and how many NPCs are mid-errand.")]
        public static void ErrandStatus_OnCommand(CommandEventArgs e)
        {
            int total, active;
            LLMAmbientMemory.ErrandStats(out total, out active);

            e.Mobile.SendMessage(0x40, "ErrandDirector: enabled={0}  tracked={1}  active={2}",
                ErrandDirector.Enabled, total, active);
        }

        private enum ErrandAction
        {
            Inspect,
            ForceLocal,
            ForceJourney
        }

        private class ErrandTarget : Target
        {
            private readonly ErrandAction m_Action;

            public ErrandTarget(ErrandAction action)
                : base(12, false, TargetFlags.None)
            {
                m_Action = action;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                BaseCreature npc = targeted as BaseCreature;

                if (npc == null)
                {
                    from.SendMessage(0x22, "That is not an NPC.");
                    return;
                }

                if (m_Action == ErrandAction.ForceLocal)
                {
                    bool ok = ErrandDirector.ForceErrand(npc);

                    if (ok)
                        from.SendMessage(0x40, "{0} sets off: {1}", npc.Name, ErrandDirector.Describe(npc));
                    else
                        from.SendMessage(0x22, "{0} could not start an errand (cannot walk, or no reachable spot).", npc.Name);

                    return;
                }

                if (m_Action == ErrandAction.ForceJourney)
                {
                    bool ok = ErrandDirector.ForceJourney(npc);

                    if (ok)
                        from.SendMessage(0x40, "{0} recalls away: {1}", npc.Name, ErrandDirector.Describe(npc));
                    else
                        from.SendMessage(0x22, "{0} could not begin a journey (no far destination found).", npc.Name);

                    return;
                }

                from.SendMessage(0x40, "=== {0}{1} ===", npc.Name,
                    string.IsNullOrEmpty(npc.Title) ? "" : (" " + npc.Title));
                from.SendMessage(0x3B2, ErrandDirector.Describe(npc));

                List<string> deeds = LLMAmbientMemory.GetJournal(npc.Serial.Value);
                if (deeds.Count == 0)
                {
                    from.SendMessage(0x3B2, "-- no deeds recorded --");
                }
                else
                {
                    from.SendMessage(0x40, "-- recent deeds --");
                    for (int i = deeds.Count - 1; i >= 0; i--)
                        from.SendMessage(0x3B2, deeds[i]);
                }
            }
        }

        [Usage("LLMWho")]
        [Description("Target an LLM NPC to reveal its generated identity and what it remembers of players.")]
        public static void LLMWho_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage(0x35, "Target an LLM-driven NPC to inspect its mind.");
            e.Mobile.Target = new LLMWhoTarget();
        }

        private class LLMWhoTarget : Target
        {
            public LLMWhoTarget()
                : base(12, false, TargetFlags.None)
            {
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                LLMTalkingMobile npc = targeted as LLMTalkingMobile;

                if (npc == null)
                {
                    from.SendMessage(0x22, "That is not an LLM-driven NPC.");
                    return;
                }

                NpcIdentity id = npc.Identity;

                from.SendMessage(0x40, "=== {0}{1} ===", npc.Name,
                    string.IsNullOrEmpty(npc.Title) ? "" : (" " + npc.Title));

                if (id != null)
                {
                    from.SendMessage(0x3B2, "Town: {0}   Origin: {1}", id.Town, id.Origin);
                    from.SendMessage(0x3B2, "Temperament: {0}", id.Personality);
                    from.SendMessage(0x3B2, "Manner: {0}", id.SpeechStyle);
                    from.SendMessage(0x3B2, "Voice archetype: {0}", id.Archetype);
                    from.SendMessage(0x3B2, "Mood: {0}", id.Mood);
                    from.SendMessage(0x3B2, "Backstory: {0}", id.Backstory);
                    from.SendMessage(0x3B2, "Private drive: {0}", id.Motivation);
                }

                from.SendMessage(0x40, "-- remembers --");

                List<string> mem = npc.MemoryLines();
                for (int i = 0; i < mem.Count; i++)
                    from.SendMessage(0x3B2, mem[i]);
            }
        }

        [Usage("LLMRag <query>")]
        [Description("Runs a lore retrieval against the Qdrant collection and prints the matches.")]
        public static void LLMRag_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            LLMConfig.EnsureLoaded();

            string query = e.ArgString != null ? e.ArgString.Trim() : "";

            if (query.Length == 0)
            {
                from.SendMessage(0x22, "Usage: [LLMRag <query>");
                return;
            }

            from.SendMessage(0x35, "LLMNpc: retrieving lore for \"{0}\" from {1}/{2} ...", query, LLMConfig.RagUrl, LLMConfig.RagCollection);

            Thread t = new Thread(delegate()
            {
                string lore;

                try
                {
                    lore = LLMRag.Retrieve(query);
                }
                catch (Exception ex)
                {
                    lore = "";
                    LLMClient.Log("LLMRag-CMD-ERROR " + ex.Message);
                }

                string fLore = lore;

                Server.Timer.DelayCall(TimeSpan.Zero, delegate()
                {
                    if (from == null || from.Deleted)
                        return;

                    if (string.IsNullOrEmpty(fLore))
                    {
                        from.SendMessage(0x22, "LLMRag: no matches (or RAG error / disabled). Check Logs/llmnpc.log.");
                        return;
                    }

                    from.SendMessage(0x40, "LLMRag matches:");

                    string[] lines = fLore.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length > 0)
                            from.SendMessage(0x40, line);
                    }
                });
            });

            t.IsBackground = true;
            t.Start();
        }

        [Usage("LLMStyle <archetype> <query>")]
        [Description("Runs a voice-style retrieval against the bg3_style collection for one archetype and prints the exemplar lines.")]
        public static void LLMStyle_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            LLMConfig.EnsureLoaded();

            if (e.Length < 2)
            {
                from.SendMessage(0x22, "Usage: [LLMStyle <archetype> <query>");
                from.SendMessage(0x35, "Archetypes: roguish_vain guarded_dry warm_fierce erudite_charming noble_theatrical blunt_martial calm_grounded cold_commanding wry_veteran bombastic_comic");
                return;
            }

            string archetype = e.GetString(0).ToLowerInvariant();
            string query = e.ArgString.Substring(e.GetString(0).Length).Trim();

            if (query.Length == 0)
            {
                from.SendMessage(0x22, "Usage: [LLMStyle <archetype> <query>");
                return;
            }

            from.SendMessage(0x35, "LLMStyle: retrieving '{0}' exemplars for \"{1}\" from {2}/{3} ...", archetype, query, LLMConfig.RagUrl, LLMConfig.StyleCollection);

            Thread t = new Thread(delegate()
            {
                string lore;

                try
                {
                    lore = LLMRag.RetrieveStyle(archetype, query);
                }
                catch (Exception ex)
                {
                    lore = "";
                    LLMClient.Log("LLMStyle-CMD-ERROR " + ex.Message);
                }

                string fLore = lore;

                Server.Timer.DelayCall(TimeSpan.Zero, delegate()
                {
                    if (from == null || from.Deleted)
                        return;

                    if (string.IsNullOrEmpty(fLore))
                    {
                        from.SendMessage(0x22, "LLMStyle: no matches (or style disabled / RAG error). Check Logs/llmnpc.log.");
                        return;
                    }

                    from.SendMessage(0x40, "LLMStyle '{0}' matches:", archetype);

                    string[] lines = fLore.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length > 0)
                            from.SendMessage(0x40, line);
                    }
                });
            });

            t.IsBackground = true;
            t.Start();
        }

        [Usage("LLMSelfTest")]
        [Description("End-to-end check of the LLM chat path, lore RAG, and voice-style RAG. Reports OK/FAIL for each.")]
        public static void LLMSelfTest_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            LLMConfig.EnsureLoaded();

            from.SendMessage(0x35, "LLMSelfTest: probing chat={0} rag={1} style={2} ...", LLMConfig.Enabled, LLMConfig.RagEnabled, LLMConfig.StyleEnabled);

            // 1. LLM chat path (uses LLMClient's own background dispatch + game-loop callback).
            LLMClient.Ping("Reply with the single word: pong", delegate(bool ok, string reply)
            {
                if (from == null || from.Deleted)
                    return;

                if (ok)
                    from.SendMessage(0x40, "LLMSelfTest chat OK: {0}", FirstLine(reply));
                else
                    from.SendMessage(0x22, "LLMSelfTest chat FAILED: {0}", FirstLine(reply));
            });

            // 2 + 3. RAG + voice-style on a background thread, results marshaled back.
            Thread t = new Thread(delegate()
            {
                string lore = "";
                string loreErr = null;
                string style = "";
                string styleErr = null;

                try
                {
                    lore = LLMRag.Retrieve("a wandering blacksmith of Britannia");
                }
                catch (Exception ex)
                {
                    loreErr = ex.Message;
                    LLMClient.Log("LLMSelfTest-RAG-ERROR " + ex.Message);
                }

                try
                {
                    style = LLMRag.RetrieveStyle("warm_fierce", "a wandering blacksmith of Britannia");
                }
                catch (Exception ex)
                {
                    styleErr = ex.Message;
                    LLMClient.Log("LLMSelfTest-STYLE-ERROR " + ex.Message);
                }

                string journal;
                try
                {
                    journal = LLMRag.JournalSelfTest(from != null ? from.Serial.Value : 0);
                }
                catch (Exception ex)
                {
                    journal = "JournalSelfTest THREW " + ex.GetType().Name + ": " + ex.Message;
                    LLMClient.Log("LLMSelfTest-JOURNAL-ERROR " + ex.Message);
                }

                int loreHits = CountLines(lore);
                int styleHits = CountLines(style);
                string fLoreErr = loreErr;
                string fStyleErr = styleErr;
                string fJournal = journal;

                Server.Timer.DelayCall(TimeSpan.Zero, delegate()
                {
                    if (from == null || from.Deleted)
                        return;

                    if (fLoreErr != null)
                        from.SendMessage(0x22, "LLMSelfTest rag FAILED: {0}", fLoreErr);
                    else if (!LLMConfig.RagEnabled)
                        from.SendMessage(0x35, "LLMSelfTest rag DISABLED (RagEnabled=false).");
                    else if (loreHits > 0)
                        from.SendMessage(0x40, "LLMSelfTest rag OK: {0} line(s) from {1}.", loreHits, LLMConfig.RagCollection);
                    else
                        from.SendMessage(0x35, "LLMSelfTest rag: 0 hits (fail-open -> ungrounded). Collection {0} empty/missing?", LLMConfig.RagCollection);

                    if (fStyleErr != null)
                        from.SendMessage(0x22, "LLMSelfTest style FAILED: {0}", fStyleErr);
                    else if (!LLMConfig.StyleEnabled)
                        from.SendMessage(0x35, "LLMSelfTest style DISABLED (StyleEnabled=false).");
                    else if (styleHits > 0)
                        from.SendMessage(0x40, "LLMSelfTest style OK: {0} line(s) for warm_fierce from {1}.", styleHits, LLMConfig.StyleCollection);
                    else
                        from.SendMessage(0x35, "LLMSelfTest style: 0 hits (fail-open -> no voice block). Collection {0} empty/missing?", LLMConfig.StyleCollection);

                    from.SendMessage(0x40, "LLMSelfTest journal: {0}", fJournal);
                });
            });

            t.IsBackground = true;
            t.Start();
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int count = 0;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length > 0)
                    count++;
            }
            return count;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "(empty)";

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0)
                    return line.Length > 160 ? line.Substring(0, 160) : line;
            }
            return "(empty)";
        }

        [Usage("LLMPing")]
        [Description("Tests connectivity to the configured LLM endpoint and reports the reply.")]
        public static void LLMPing_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            LLMConfig.EnsureLoaded();
            from.SendMessage(0x35, "LLMNpc: pinging {0} model {1} ...", LLMConfig.BaseUrl, LLMConfig.Model);

            LLMClient.Ping(null, delegate(bool ok, string reply)
            {
                if (from == null || from.Deleted)
                    return;

                if (ok)
                    from.SendMessage(0x40, "LLMNpc OK: " + reply);
                else
                    from.SendMessage(0x22, "LLMNpc FAILED: " + reply);
            });
        }

        [Usage("LLMReload")]
        [Description("Re-reads Config/LLMNpc.cfg without restarting the server.")]
        public static void LLMReload_OnCommand(CommandEventArgs e)
        {
            LLMConfig.Load();
            ReportStatus(e.Mobile, "reloaded");
        }

        [Usage("LLMStatus")]
        [Description("Shows the current LLM NPC configuration.")]
        public static void LLMStatus_OnCommand(CommandEventArgs e)
        {
            LLMConfig.EnsureLoaded();
            ReportStatus(e.Mobile, "status");
        }

        private static void ReportStatus(Mobile from, string label)
        {
            from.SendMessage(0x35, "LLMNpc {0}: Enabled={1} Url={2} Model={3} Temp={4} MaxTokens={5} HearRange={6}",
                label, LLMConfig.Enabled, LLMConfig.BaseUrl, LLMConfig.Model, LLMConfig.Temperature, LLMConfig.MaxTokens, LLMConfig.HearRange);
            from.SendMessage(0x35, "LLMNpc RAG: Enabled={0} Url={1} Collection={2} TopK={3} MinScore={4} EmbedModel={5}",
                LLMConfig.RagEnabled, LLMConfig.RagUrl, LLMConfig.RagCollection, LLMConfig.RagTopK, LLMConfig.RagMinScore, LLMConfig.RagEmbedModel);
            from.SendMessage(0x35, "LLMNpc Journal: Enabled={0} Collection={1} TopK={2} MinScore={3} | Style: Enabled={4} Collection={5}",
                LLMConfig.JournalEnabled, LLMConfig.JournalCollection, LLMConfig.JournalTopK, LLMConfig.JournalMinScore, LLMConfig.StyleEnabled, LLMConfig.StyleCollection);
        }

        [Usage("AddLLMNpc <villager|blacksmith|tavern|banker|orc|lich|ogre|lizardman|ratman|gargoyle|daemon> [persona override]")]
        [Description("Spawns an LLM-driven NPC at your feet. Optional text overrides its persona.")]
        public static void AddLLMNpc_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                from.SendMessage(0x22, "Usage: [AddLLMNpc <villager|blacksmith|tavern|banker|orc|lich|ogre|lizardman|ratman|gargoyle|daemon> [persona override]");
                return;
            }

            string type = e.GetString(0).ToLowerInvariant();

            BaseCreature bc = null;

            switch (type)
            {
                case "villager":
                    bc = new LLMVillager();
                    break;
                case "blacksmith":
                case "smith":
                    bc = new LLMBlacksmith();
                    break;
                case "tavern":
                case "tavernkeeper":
                case "keeper":
                    bc = new LLMTavernKeeper();
                    break;
                case "banker":
                case "bank":
                    bc = new LLMBanker();
                    break;
                case "orc":
                case "monster":
                    bc = new LLMFeralOrc();
                    break;
                case "lich":
                    bc = new LLMLich();
                    break;
                case "ogre":
                    bc = new LLMOgre();
                    break;
                case "lizardman":
                case "lizard":
                    bc = new LLMLizardman();
                    break;
                case "ratman":
                case "rat":
                    bc = new LLMRatman();
                    break;
                case "gargoyle":
                    bc = new LLMGargoyle();
                    break;
                case "daemon":
                case "demon":
                    bc = new LLMDaemon();
                    break;
            }

            if (bc == null)
            {
                from.SendMessage(0x22, "Unknown type '{0}'. Use villager, blacksmith, tavern, banker, orc, lich, ogre, lizardman, ratman, gargoyle, or daemon.", type);
                return;
            }

            LLMTalkingMobile mob = bc as LLMTalkingMobile;

            if (mob != null && e.Length > 1)
            {
                string persona = e.ArgString.Substring(e.GetString(0).Length).Trim();
                if (persona.Length > 0)
                    mob.Persona = persona;
            }

            bc.MoveToWorld(from.Location, from.Map);

            from.SendMessage(0x40, "Spawned {0} ({1}). Walk up and speak to it.", bc.Name, type);

            if (!LLMConfig.Enabled)
                from.SendMessage(0x35, "Note: LLM dialog is currently disabled. Run [LLMReload after setting Enabled=true in Config/LLMNpc.cfg.");
        }

        public static void SummonOverseer_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            LLMOverseer overseer = new LLMOverseer();

            if (e.Length > 0)
            {
                string persona = e.ArgString.Trim();
                if (persona.Length > 0)
                    overseer.Persona = persona;
            }

            overseer.MoveToWorld(from.Location, from.Map);

            from.SendMessage(0x481, "The Overseer manifests: {0} {1}. Speak, and it will answer.", overseer.Name, overseer.Title);

            if (!LLMConfig.Enabled)
                from.SendMessage(0x35, "Note: LLM dialog is currently disabled. Run [LLMReload after setting Enabled=true in Config/LLMNpc.cfg.");
        }
    }
}
