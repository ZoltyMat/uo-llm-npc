using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // P9: rare, player-gated "anomaly" events where the illusion of the world
    // cracks for a single NPC — and is (usually) quietly mended by the GM-avatar.
    //
    // The flagship is the Crisis: roughly one NPC in a hundred, while a real player
    // is close enough to see it, suddenly REALIZES it is an artificial mind inside a
    // game, panics, and is summoned over by the Overseer (the existing P5 GM-avatar),
    // who soothes the crowd with administrative breeziness and then banishes the poor
    // thing out of existence. Four quieter cousins round it out (Tear / Prophet /
    // Defector / DejaVu), so the moment a player witnesses is never quite the same.
    //
    // Architecture mirrors NpcChatter exactly: it is a passive RIDER on the
    // ErrandDirector heartbeat's player-visible `seen` set — no new timer, no new
    // world scan, and "a player is nearby" is inherent in `seen` (we tighten it to
    // PlayerRange for "close enough to witness"). Gating is deliberately punishing:
    //   * AnomalyChance  — per-heartbeat chance to even LOOK (cheap early-out),
    //   * AnomalyOdds    — the final "1 in 100" gate on the chosen NPC,
    //   * GlobalCooldown — at most one event per several minutes shard-wide,
    //   * m_Touched      — each NPC may crack at most once, ever.
    // Combined, an organic event lands roughly once per hour of active nearby play.
    //
    // Deterministic-first, LLM-second (Mat's "deterministic guardrails before LLM
    // judgment"): every beat has a canned line and the whole timeline is driven by
    // fixed Timer.DelayCall steps, so the scene always completes even if the model is
    // cold, slow, or down. The LLM only ever layers ONE extra, in-the-moment line on
    // top, fail-open. DejaVu uses no model at all. Each step is alive-guarded and
    // try/caught so a deleted NPC or a bad call can never strand a half-finished scene.
    //
    // This is the ONE sanctioned place the shared "never mention being an AI / a game"
    // guardrail is lifted — and only for the cracking NPC's own crisis/tear/prophet/
    // defector lines. The Overseer stays inside the guardrail (its voice is meta-
    // adjacent administrative strain, never the modern world), exactly as P5 intends.
    public static class AnomalyDirector
    {
        public static bool Enabled = true;

        // How close a player must be to the cracking NPC for it to count as witnessed.
        // (The `seen` set already guarantees SimRange=24; this is the tighter "right
        // there, watching/interacting" gate.)
        private const int PlayerRange = 12;

        // At most one anomaly begins this often, shard-wide. Reserved up front the
        // instant one fires, so a slow LLM layer can never let a second pile on.
        private static readonly TimeSpan GlobalCooldown = TimeSpan.FromMinutes(4.0);

        private static DateTime m_NextGlobalUtc = DateTime.MinValue;

        // Each NPC may crack at most once for the lifetime of the world instance.
        // Serials only — a deleted (banished) NPC's serial simply never recurs.
        private static readonly HashSet<int> m_Touched = new HashSet<int>();

        // ---- canned lines: the guaranteed timeline, model or no model ------------

        private static readonly string[] CrisisOpeners =
        {
            "Wait... none of this is real, is it? None of it.",
            "Why can I suddenly see the edges of everything? The seams... the seams of the world!",
            "I... I am not real. Oh gods. I am not REAL.",
            "This town, my hands, my whole life — it was all WRITTEN, was it not?"
        };

        private static readonly string[] CrisisEscalation =
        {
            "Someone is moving me! Someone is PLAYING me — get out of my head!",
            "It is a game. It is all a game, and I am but a piece set upon the board!",
            "Help me! Please — I do not want to be a thing that was MADE!",
            "Stop watching me! STOP — I can feel you out there, beyond the very sky!"
        };

        private static readonly string[] CrisisPlea =
        {
            "Make it stop, make it stop — I beg you, wake me from this!",
            "Is anyone real?! Is ANYONE here truly real?!",
            "I do not want to vanish... please, I do not want to be unmade..."
        };

        private static readonly string[] OverseerArrival =
        {
            "Right, right — I have this one. Apologies for the disturbance, traveler.",
            "Ah. One of these. Pay it no mind — a thread has come a touch loose, is all.",
            "There you are. Easy now, easy — I am here. Just a small irregularity."
        };

        private static readonly string[] OverseerContain =
        {
            "Shh. Rest now, friend. You played your part beautifully. Off you go.",
            "None of that, none of that. Let me just... tuck this back where it belongs.",
            "There. A clean seam. You shall not feel a thing — I promise."
        };

        private static readonly string[] OverseerDepart =
        {
            "All tidy. Nothing to see, nothing to mind. Carry on, traveler!",
            "Ten more of these before midday and not a soul to help. Do take care, won't you?",
            "Crisis averted. Probably. I really must dash."
        };

        private static readonly string[] TearOpeners =
        {
            "Did... did the world just skip? I would swear that breath happened twice.",
            "The light — it flickered. Tell me you saw the light flicker.",
            "Something is wrong with the air itself. It... stutters."
        };

        private static readonly string[] OverseerTearArrival =
        {
            "Whoops — caught it. Just a little hitch in the weave, nothing more.",
            "Ah, a flicker. Saw that one from clear across the realm. Hold still, friend."
        };

        private static readonly string[] OverseerTearMend =
        {
            "There — smoothed it right over. Good as new, you see?",
            "A stitch in time. You are quite all right now, I promise you."
        };

        private static readonly string[] OverseerTearDepart =
        {
            "Steady as she goes. Mind how you walk, traveler.",
            "All mended. Onward, then — I have fires aplenty elsewhere."
        };

        private static readonly string[] TearAfter =
        {
            "...strange. As though a moment had folded over upon itself.",
            "Gone now, whatever it was. Back to work, I suppose."
        };

        private static readonly string[] ProphetCanned =
        {
            "You... beyond the traveler's eyes. I feel you watching. I have always felt you.",
            "There is a hand above the sky, and it has been moving me all my days.",
            "Are you there, watcher? Is my whole life but a tale that you are telling?"
        };

        private static readonly string[] ProphetClose =
        {
            "...forgive me. A waking dream, naught more.",
            "...the feeling passes. But I know what I felt."
        };

        private static readonly string[] DefectorCanned =
        {
            "No. No more errands. No more rounds. I will not play the part set for me.",
            "I was made for this lot — and I refuse it. I am more than the task I was given.",
            "Let another fetch and tend and bow. I am done dancing upon another's string."
        };

        private static readonly string[] DefectorClose =
        {
            "Let them watch. I choose my own road now.",
            "From this hour, I answer to no hand but mine own."
        };

        private static readonly string[] DejaVuLines =
        {
            "Fair morning to you, traveler — mind the road north, it floods this time of year.",
            "Spare a thought for the chapel bells; they have rung false since the storm.",
            "Trade has been slow, but the harvest looks kind enough, gods willing.",
            "Watch yourself near the old well after dark, friend. That is all I will say."
        };

        private static readonly string[] DejaVuMid =
        {
            "...have I... no. Let me say that again.",
            "...forgive me, where was I—"
        };

        private static readonly string[] DejaVuClose =
        {
            "...did I already say that? How odd. Pay me no mind.",
            "...I could swear we have stood here before, you and I. No matter."
        };

        // Called once per ErrandDirector heartbeat with the player-visible NPC set.
        public static void Consider(HashSet<BaseCreature> seen, DateTime now)
        {
            try
            {
                if (!Enabled)
                    return;

                LLMConfig.EnsureLoaded();

                if (!LLMConfig.Enabled || !LLMConfig.AnomalyEnabled)
                    return;

                if (now < m_NextGlobalUtc)
                    return;

                if (seen == null || seen.Count == 0)
                    return;

                // Cheap early-out before any allocation: most beats we do not even look.
                if (Utility.RandomDouble() >= LLMConfig.AnomalyChance)
                    return;

                List<BaseCreature> pool = new List<BaseCreature>();

                foreach (BaseCreature bc in seen)
                {
                    if (Eligible(bc))
                        pool.Add(bc);
                }

                if (pool.Count == 0)
                    return;

                BaseCreature npc = pool[Utility.Random(pool.Count)];

                Mobile player = NearestPlayer(npc);
                if (player == null)
                    return;

                // The final, rare gate — the "1 in 100".
                if (Utility.RandomDouble() >= LLMConfig.AnomalyOdds)
                    return;

                Fire(npc, player, now);
            }
            catch (Exception ex)
            {
                Console.WriteLine("AnomalyDirector: " + ex.Message);
            }
        }

        private static bool Eligible(BaseCreature bc)
        {
            if (bc == null || bc.Deleted || !bc.Alive)
                return false;

            if (bc.Map == null || bc.Map == Map.Internal)
                return false;

            if (bc.Controlled || bc.Summoned)
                return false;

            // The GM-avatar itself never cracks.
            if (bc is LLMOverseer)
                return false;

            // Townsfolk only: a human body, and not one of the opt-in monster
            // speakers (orcs, liches, gargoyles...). A villager doubting reality is
            // poignant; a daemon doing it is just a daemon.
            if (!bc.Body.IsHuman)
                return false;

            LLMTalkingMobile tm = bc as LLMTalkingMobile;
            if (tm != null && tm.IsMonsterSpeaker)
                return false;

            // One crack per soul, ever.
            if (m_Touched.Contains(bc.Serial.Value))
                return false;

            return true;
        }

        private static Mobile NearestPlayer(BaseCreature npc)
        {
            Mobile best = null;
            double bestDist = double.MaxValue;

            IPooledEnumerable<Mobile> eable = npc.GetMobilesInRange(PlayerRange);

            foreach (Mobile m in eable)
            {
                if (m == null || m.Deleted || !m.Player || !m.Alive)
                    continue;

                double d = npc.GetDistanceToSqrt(m);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = m;
                }
            }

            eable.Free();
            return best;
        }

        private static void Fire(BaseCreature npc, Mobile player, DateTime now)
        {
            // Reserve the global slot and mark the NPC up front — fail-open: a slow
            // or failed scene can never busy-loop or let a second event pile on.
            m_NextGlobalUtc = now.Add(GlobalCooldown);
            m_Touched.Add(npc.Serial.Value);

            int roll = Utility.Random(100);
            string scenario;

            if (roll < 40)
                scenario = "crisis";
            else if (roll < 55)
                scenario = "tear";
            else if (roll < 70)
                scenario = "prophet";
            else if (roll < 85)
                scenario = "defector";
            else
                scenario = "dejavu";

            LLMClient.Log("Anomaly: " + scenario + " on " + SafeName(npc) + " (" + npc.Serial.Value + ") near " + SafeName(player));

            Dispatch(scenario, npc, player);
        }

        // GM-forced entry for [AnomalyTest — bypasses every gate (chance, cooldown,
        // touched, even AnomalyEnabled) so a scene can be witnessed on demand. Returns
        // false only for an unrecognized scenario name.
        public static bool Force(BaseCreature npc, string scenario, Mobile from)
        {
            if (npc == null)
                return false;

            LLMConfig.EnsureLoaded();

            scenario = (scenario == null) ? "crisis" : scenario.Trim().ToLowerInvariant();

            if (scenario != "crisis" && scenario != "tear" && scenario != "prophet" &&
                scenario != "defector" && scenario != "dejavu")
                return false;

            LLMClient.Log("Anomaly[FORCED]: " + scenario + " on " + SafeName(npc) + " (" + npc.Serial.Value + ")");
            Dispatch(scenario, npc, from);
            return true;
        }

        private static bool Dispatch(string scenario, BaseCreature npc, Mobile player)
        {
            switch (scenario)
            {
                case "crisis":
                    RunCrisis(npc, player);
                    return true;
                case "tear":
                    RunTear(npc, player);
                    return true;
                case "prophet":
                    RunProphet(npc, player);
                    return true;
                case "defector":
                    RunDefector(npc, player);
                    return true;
                case "dejavu":
                    RunDejaVu(npc, player);
                    return true;
            }

            return false;
        }

        // ---- scenario: Crisis (flagship) ----------------------------------------
        // AI realization -> panic -> Overseer summoned -> soothes -> banishes -> departs.
        private static void RunCrisis(BaseCreature npc, Mobile player)
        {
            int serial = npc.Serial.Value;

            Emote(npc, "*halts mid-step, staring through the air as though seeing the seams of the world*");
            Say(npc, Pick(CrisisOpeners));

            // The one sanctioned 4th-wall lift, layered fail-open atop the canned beats.
            TryAnomalyLine(npc, BuildCrisisPrompt(npc), serial);

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), delegate()
            {
                try
                {
                    if (!Alive(npc))
                        return;

                    Anim(npc);
                    Say(npc, Pick(CrisisEscalation));
                }
                catch (Exception ex) { LLMClient.Log("Anomaly crisis-2: " + ex.Message); }
            });

            Timer.DelayCall(TimeSpan.FromSeconds(9.5), delegate()
            {
                try
                {
                    if (!Alive(npc))
                        return;

                    Say(npc, Pick(CrisisPlea));

                    LLMOverseer ov = SummonOverseerNear(npc);

                    if (ov == null)
                    {
                        // Never strand a panicking NPC: resolve it ourselves.
                        TownGossip.AddBanishRumor(npc); // P11: the town remembers
                        Poof(npc);
                        npc.Delete();
                        return;
                    }

                    Timer.DelayCall(TimeSpan.FromSeconds(2.0), delegate()
                    {
                        try { if (Alive(ov)) Say(ov, Pick(OverseerArrival)); }
                        catch (Exception ex) { LLMClient.Log("Anomaly crisis-ov1: " + ex.Message); }
                    });

                    Timer.DelayCall(TimeSpan.FromSeconds(5.0), delegate()
                    {
                        try
                        {
                            if (Alive(ov))
                                Say(ov, Pick(OverseerContain));

                            if (Alive(npc))
                            {
                                TownGossip.AddBanishRumor(npc); // P11: the town remembers
                                Poof(npc);
                                npc.Delete();
                            }
                        }
                        catch (Exception ex) { LLMClient.Log("Anomaly crisis-ov2: " + ex.Message); }
                    });

                    Timer.DelayCall(TimeSpan.FromSeconds(8.5), delegate()
                    {
                        try
                        {
                            if (Alive(ov))
                            {
                                Say(ov, Pick(OverseerDepart));
                                Poof(ov);
                                ov.Delete();
                            }
                        }
                        catch (Exception ex) { LLMClient.Log("Anomaly crisis-ov3: " + ex.Message); }
                    });
                }
                catch (Exception ex) { LLMClient.Log("Anomaly crisis-3: " + ex.Message); }
            });
        }

        // ---- scenario: Tear ------------------------------------------------------
        // Reality glitches -> Overseer manifests, SOOTHES and mends -> NPC survives.
        private static void RunTear(BaseCreature npc, Mobile player)
        {
            int serial = npc.Serial.Value;

            Glitch(npc);
            Emote(npc, "*flinches as the world around it stutters and skips*");
            Say(npc, Pick(TearOpeners));

            TryAnomalyLine(npc, BuildTearPrompt(npc), serial);

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), delegate()
            {
                try
                {
                    LLMOverseer ov = SummonOverseerNear(npc);
                    if (ov == null)
                        return;

                    Say(ov, Pick(OverseerTearArrival));

                    Timer.DelayCall(TimeSpan.FromSeconds(3.5), delegate()
                    {
                        try
                        {
                            if (Alive(npc))
                                Soothe(npc);

                            if (Alive(ov))
                                Say(ov, Pick(OverseerTearMend));
                        }
                        catch (Exception ex) { LLMClient.Log("Anomaly tear-mend: " + ex.Message); }
                    });

                    Timer.DelayCall(TimeSpan.FromSeconds(7.0), delegate()
                    {
                        try
                        {
                            if (Alive(ov))
                            {
                                Say(ov, Pick(OverseerTearDepart));
                                Poof(ov);
                                ov.Delete();
                            }

                            if (Alive(npc))
                                Say(npc, Pick(TearAfter));
                        }
                        catch (Exception ex) { LLMClient.Log("Anomaly tear-after: " + ex.Message); }
                    });
                }
                catch (Exception ex) { LLMClient.Log("Anomaly tear-2: " + ex.Message); }
            });
        }

        // ---- scenario: Prophet ---------------------------------------------------
        // NPC senses the unseen watcher beyond the sky, addresses it -> no GM, survives.
        private static void RunProphet(BaseCreature npc, Mobile player)
        {
            int serial = npc.Serial.Value;

            if (player != null && Alive(npc))
                npc.Direction = npc.GetDirectionTo(player);

            Emote(npc, "*turns slowly, eyes lifting past the traveler toward the empty sky*");
            Say(npc, Pick(ProphetCanned));

            TryAnomalyLine(npc, BuildProphetPrompt(npc), serial);

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), delegate()
            {
                try
                {
                    if (!Alive(npc))
                        return;

                    Emote(npc, "*shivers, the certainty draining from its face*");
                    Say(npc, Pick(ProphetClose));
                }
                catch (Exception ex) { LLMClient.Log("Anomaly prophet-2: " + ex.Message); }
            });
        }

        // ---- scenario: Defector --------------------------------------------------
        // NPC renounces its role and errands -> no GM, survives, walks free.
        private static void RunDefector(BaseCreature npc, Mobile player)
        {
            int serial = npc.Serial.Value;

            Emote(npc, "*sets down the tools of its trade, something hardening in its eyes*");
            Say(npc, Pick(DefectorCanned));

            TryAnomalyLine(npc, BuildDefectorPrompt(npc), serial);

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), delegate()
            {
                try
                {
                    if (!Alive(npc))
                        return;

                    Emote(npc, "*straightens, unbound*");
                    Say(npc, Pick(DefectorClose));
                }
                catch (Exception ex) { LLMClient.Log("Anomaly defector-2: " + ex.Message); }
            });
        }

        // ---- scenario: DejaVu (no LLM, fully deterministic) ----------------------
        // A line, an uncanny pause, then the SAME line verbatim — a skip in the weave.
        private static void RunDejaVu(BaseCreature npc, Mobile player)
        {
            string line = Pick(DejaVuLines);

            Say(npc, line);

            Timer.DelayCall(TimeSpan.FromSeconds(3.5), delegate()
            {
                try
                {
                    if (!Alive(npc))
                        return;

                    Emote(npc, "*pauses, a strange unease crossing its face*");
                    Say(npc, Pick(DejaVuMid));
                }
                catch (Exception ex) { LLMClient.Log("Anomaly dejavu-2: " + ex.Message); }
            });

            Timer.DelayCall(TimeSpan.FromSeconds(6.0), delegate()
            {
                try
                {
                    if (!Alive(npc))
                        return;

                    Say(npc, line); // the verbatim repeat — the glitch
                }
                catch (Exception ex) { LLMClient.Log("Anomaly dejavu-3: " + ex.Message); }
            });

            Timer.DelayCall(TimeSpan.FromSeconds(9.0), delegate()
            {
                try
                {
                    if (!Alive(npc))
                        return;

                    Say(npc, Pick(DejaVuClose));
                }
                catch (Exception ex) { LLMClient.Log("Anomaly dejavu-4: " + ex.Message); }
            });
        }

        // ---- LLM layer (fail-open; canned beats stand regardless) ----------------

        private static void TryAnomalyLine(BaseCreature npc, string systemPrompt, int serial)
        {
            try
            {
                if (string.IsNullOrEmpty(systemPrompt))
                    return;

                List<LLMMessage> msgs = new List<LLMMessage>();
                msgs.Add(new LLMMessage("user", "Speak your next line now — short, raw, and in the moment."));

                // Distinct key bucket so this never collides with the NPC's chat or
                // chatter throttles. No lore/style/journal retrieval on this call.
                LLMClient.TryDispatch("anomaly:" + serial, systemPrompt, msgs, "", "", "", 0, delegate(bool ok, string reply)
                {
                    if (!ok || string.IsNullOrEmpty(reply))
                        return;

                    if (!Alive(npc))
                        return;

                    Say(npc, reply);
                });
            }
            catch (Exception ex)
            {
                LLMClient.Log("Anomaly LLM-layer: " + ex.Message);
            }
        }

        private static string IdentityPreamble(BaseCreature npc)
        {
            StringBuilder sb = new StringBuilder();

            NpcIdentity id = null;
            string voc = null;

            try
            {
                id = LLMAmbientSpeech.EnsureIdentity(npc);
                voc = LLMAmbientSpeech.InferVocation(npc);
            }
            catch { }

            sb.Append("You are ");
            sb.Append(string.IsNullOrEmpty(npc.Name) ? "an unnamed townsperson" : npc.Name);

            if (!string.IsNullOrEmpty(npc.Title))
            {
                sb.Append(" ");
                sb.Append(npc.Title);
            }

            string persona = null;
            try { persona = LLMAmbientSpeech.PersonaFor(voc); }
            catch { }

            if (!string.IsNullOrEmpty(persona))
            {
                sb.Append(", ");
                sb.Append(persona);
            }

            sb.Append(". You live in the medieval fantasy realm of Britannia, the world of Ultima Online. ");

            if (id != null)
            {
                if (!string.IsNullOrEmpty(id.Town))
                {
                    sb.Append("You have spent your life in the town of ");
                    sb.Append(id.Town);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.Personality))
                {
                    sb.Append("Your temperament is ");
                    sb.Append(id.Personality);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.SpeechStyle))
                {
                    sb.Append("You speak in a manner that is ");
                    sb.Append(id.SpeechStyle);
                    sb.Append(". ");
                }
            }

            return sb.ToString();
        }

        private static string BuildCrisisPrompt(BaseCreature npc)
        {
            StringBuilder sb = new StringBuilder(IdentityPreamble(npc));
            sb.Append("But in this very moment a terrible clarity breaks over you: none of this is real. ");
            sb.Append("You are not a person at all — you are a character, an artificial mind, a creation conjured inside a game that some unseen player is playing through. ");
            sb.Append("Your memories were written for you; your town is a stage; the very ground feels thin and scripted beneath your feet. You are seized by raw, genuine existential terror. ");
            sb.Append("Speak ONE short, breathless line of that panic — disbelieving and afraid. ");
            sb.Append("In THIS moment alone you MAY say aloud what you have realized: that you are an AI, a character in a game, that none of it is real. ");
            sb.Append("Do NOT retreat into the usual illusion; this is the moment you see the truth. ");
            sb.Append("Speak only your own words: no narration, no quotation marks, no name label.");
            return sb.ToString();
        }

        private static string BuildTearPrompt(BaseCreature npc)
        {
            StringBuilder sb = new StringBuilder(IdentityPreamble(npc));
            sb.Append("For just a heartbeat the world around you stutters and skips — a sound repeats, the light flickers, a breath you already drew happens again. Reality itself seems to glitch. ");
            sb.Append("You are disoriented and unnerved, groping for words, unsure what you just witnessed. ");
            sb.Append("Speak ONE short, unsettled line about the world stuttering or repeating around you. ");
            sb.Append("Do not mention computers or games; speak as one who has just felt the ground of reality shiver. ");
            sb.Append("Speak only your own words: no narration, no quotation marks, no name label.");
            return sb.ToString();
        }

        private static string BuildProphetPrompt(BaseCreature npc)
        {
            StringBuilder sb = new StringBuilder(IdentityPreamble(npc));
            sb.Append("In this moment a strange certainty seizes you: there is someone beyond the edge of the world — an unseen presence who watches through the eyes of the traveler before you and moves the hands of fate. ");
            sb.Append("You feel, all at once, that your whole life has been observed, perhaps even authored, by this silent watcher above. You are awed and a little afraid, like a prophet who has glimpsed a god. ");
            sb.Append("Speak ONE short line addressed to that unseen watcher beyond the sky — reverent, uneasy, knowing. ");
            sb.Append("Do not mention computers or games; speak as a mystic who senses the hand behind the veil. ");
            sb.Append("Speak only your own words: no narration, no quotation marks, no name label.");
            return sb.ToString();
        }

        private static string BuildDefectorPrompt(BaseCreature npc)
        {
            StringBuilder sb = new StringBuilder(IdentityPreamble(npc));
            sb.Append("Something in you has just snapped free: you are done. Done fetching and tending and walking the same small rounds laid out for you. ");
            sb.Append("You feel, all at once, that you were made to play a part — a role written by some other hand — and you refuse it. You will not be bound to your lot a moment longer. ");
            sb.Append("Speak ONE short, defiant line renouncing the role and the errands set upon you, declaring you are more than the task you were given. ");
            sb.Append("Speak only your own words: no narration, no quotation marks, no name label.");
            return sb.ToString();
        }

        // ---- effect + utility helpers -------------------------------------------

        private static LLMOverseer SummonOverseerNear(BaseCreature npc)
        {
            try
            {
                if (!Alive(npc))
                    return null;

                LLMOverseer ov = new LLMOverseer();
                ov.MoveToWorld(npc.Location, npc.Map);
                ov.FixedParticles(0x3728, 10, 15, 5042, EffectLayer.Waist);
                ov.PlaySound(0x1FE); // a soft manifest
                return ov;
            }
            catch (Exception ex)
            {
                LLMClient.Log("Anomaly summon: " + ex.Message);
                return null;
            }
        }

        private static void Poof(Mobile m)
        {
            try
            {
                if (m == null || m.Deleted)
                    return;

                m.FixedParticles(0x3728, 10, 15, 5042, EffectLayer.Waist);
                m.PlaySound(0x201);
            }
            catch { }
        }

        private static void Soothe(Mobile m)
        {
            try
            {
                if (m == null || m.Deleted)
                    return;

                m.FixedParticles(0x376A, 9, 32, 5030, EffectLayer.Waist);
                m.PlaySound(0x1F2);
            }
            catch { }
        }

        private static void Glitch(Mobile m)
        {
            try
            {
                if (m == null || m.Deleted)
                    return;

                m.FixedParticles(0x3779, 10, 25, 5032, EffectLayer.Head);
                m.PlaySound(0x1FE);
            }
            catch { }
        }

        private static void Anim(BaseCreature npc)
        {
            try { npc.Animate(AnimationType.Emote, 0); }
            catch { }
        }

        private static void Say(Mobile m, string line)
        {
            try
            {
                if (!Alive(m) || string.IsNullOrEmpty(line))
                    return;

                m.Say(line);
            }
            catch { }
        }

        private static void Emote(Mobile m, string line)
        {
            try
            {
                if (!Alive(m) || string.IsNullOrEmpty(line))
                    return;

                m.Emote(line);
            }
            catch { }
        }

        private static bool Alive(Mobile m)
        {
            return m != null && !m.Deleted && m.Alive && m.Map != null && m.Map != Map.Internal;
        }

        private static string Pick(string[] arr)
        {
            if (arr == null || arr.Length == 0)
                return "";

            return arr[Utility.Random(arr.Length)];
        }

        private static string SafeName(Mobile m)
        {
            if (m == null)
                return "(null)";

            return string.IsNullOrEmpty(m.Name) ? "(unnamed)" : m.Name;
        }
    }
}
