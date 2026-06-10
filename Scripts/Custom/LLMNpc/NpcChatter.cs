using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // P6: ambient NPC-to-NPC conversation. When two ordinary townsfolk stand near
    // each other AND near a watching player, one occasionally strikes up a short
    // exchange — an opening line, then a reply — each grounded in that NPC's own
    // persisted identity and what it remembers of the other, and each free to
    // carry a small physical action (see NpcActions). The town feels lived-in
    // without the player having to say a word.
    //
    // It is driven entirely off the ErrandDirector heartbeat's `seen` set, so it
    // inherits the same guarantees for free: NPCs are only ever considered when a
    // real player is within SimRange (visibility + perf — no chatter happens in an
    // empty town), the set is already deduped and map-filtered, and off-screen
    // NPCs never tick. No new timer, no new world scan.
    //
    // Gating is layered and conservative: a per-heartbeat probability roll, a
    // shard-wide global cooldown (at most one exchange every GlobalCooldown), and a
    // per-NPC cooldown — so the world murmurs rather than chatters, and the shared
    // local model is never hammered. Fail-open throughout: any miss just means no
    // exchange this beat.
    public static class NpcChatter
    {
        public static bool Enabled = true;

        // Tiles between two NPCs for them to be "in conversation range".
        private const int ChatRange = 3;

        // If the pair has drifted past this by the time the opener lands, abort
        // (they wandered apart; a reply across the square reads as shouting).
        private const int DriftRange = ChatRange + 3;

        // At most one exchange begins this often shard-wide, and a given NPC sits
        // out this long after taking part. Both reserved up front (before the async
        // call), so a slow or failed model call still spaces out the next attempt.
        private static readonly TimeSpan GlobalCooldown = TimeSpan.FromSeconds(45.0);
        private static readonly TimeSpan NpcCooldown = TimeSpan.FromMinutes(4.0);

        private static DateTime m_NextGlobalUtc = DateTime.MinValue;
        private static readonly Dictionary<int, DateTime> m_NextNpcUtc = new Dictionary<int, DateTime>();

        // Called once per ErrandDirector heartbeat with the player-visible NPC set.
        public static void Consider(HashSet<BaseCreature> seen, DateTime now)
        {
            try
            {
                if (!Enabled)
                    return;

                LLMConfig.EnsureLoaded();

                if (!LLMConfig.Enabled || !LLMConfig.ChatterEnabled)
                    return;

                if (now < m_NextGlobalUtc)
                    return;

                if (seen == null || seen.Count < 2)
                    return;

                // Cheap early-out before any allocation: only sometimes do we even
                // look for a pair, so an idle crowd stays mostly quiet.
                if (Utility.RandomDouble() >= LLMConfig.ChatterChance)
                    return;

                List<BaseCreature> pool = new List<BaseCreature>();

                foreach (BaseCreature bc in seen)
                {
                    if (Eligible(bc, now))
                        pool.Add(bc);
                }

                if (pool.Count < 2)
                    return;

                // First co-located eligible pair wins; one exchange per heartbeat.
                for (int i = 0; i < pool.Count; i++)
                {
                    BaseCreature a = pool[i];

                    for (int j = i + 1; j < pool.Count; j++)
                    {
                        BaseCreature b = pool[j];

                        if (a.Map != b.Map)
                            continue;

                        if (a.GetDistanceToSqrt(b) > ChatRange)
                            continue;

                        if (!Compatible(a, b))
                            continue;

                        Initiate(a, b, now);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("NpcChatter: " + ex.Message);
            }
        }

        private static bool Eligible(BaseCreature bc, DateTime now)
        {
            if (bc == null || bc.Deleted || !bc.Alive)
                return false;

            if (bc.Map == null || bc.Map == Map.Internal)
                return false;

            if (bc.Controlled || bc.Summoned)
                return false;

            // Townsfolk participate with a human body. The wild speakers (orcs,
            // liches, ogres, …) carry monstrous bodies but opt in explicitly via
            // IsMonsterSpeaker — arbitrary vanilla monsters never do. The Overseer
            // is Stationary and never reaches the chatter scan.
            if (!bc.Body.IsHuman)
            {
                LLMTalkingMobile mon = bc as LLMTalkingMobile;
                if (mon == null || !mon.IsMonsterSpeaker)
                    return false;
            }

            // Both vanilla townsfolk and the custom talking NPCs participate.
            // EnsureIdentity now returns a talking mobile's own serialized
            // identity, so chatter grounds in the same character it speaks and
            // runs errands as — no divergent second identity.

            DateTime next;
            if (m_NextNpcUtc.TryGetValue(bc.Serial.Value, out next) && now < next)
                return false;

            return true;
        }

        // Who may converse with whom. Townsfolk banter freely across trades, but a
        // wild speaker keeps to its own kind: a lich pronounces at another lich, an
        // ogre grunts at another ogre — never at a townsperson, and never across
        // species. (Compatible is only reached for an already in-range, eligible
        // pair, so the same-type rule simply narrows monster banter, never humans.)
        private static bool Compatible(BaseCreature a, BaseCreature b)
        {
            LLMTalkingMobile ma = a as LLMTalkingMobile;
            LLMTalkingMobile mb = b as LLMTalkingMobile;

            bool aMon = ma != null && ma.IsMonsterSpeaker;
            bool bMon = mb != null && mb.IsMonsterSpeaker;

            if (aMon || bMon)
                return aMon && bMon && a.GetType() == b.GetType();

            return true;
        }

        private static void Initiate(BaseCreature a, BaseCreature b, DateTime now)
        {
            // Reserve the global slot and both NPCs immediately — fail-open: we
            // never retry this pair on this beat, so a stuck call can't busy-loop.
            m_NextGlobalUtc = now.Add(GlobalCooldown);
            m_NextNpcUtc[a.Serial.Value] = now.Add(NpcCooldown);
            m_NextNpcUtc[b.Serial.Value] = now.Add(NpcCooldown);

            int sa = a.Serial.Value;
            int sb = b.Serial.Value;

            NpcIdentity idA = LLMAmbientSpeech.EnsureIdentity(a);
            NpcIdentity idB = LLMAmbientSpeech.EnsureIdentity(b);

            // Capture both recaps BEFORE folding this exchange in, so each speaks
            // from what it knew walking up.
            string recapAofB = RecapText(sa, sb);
            string recapBofA = RecapText(sb, sa);

            string sysA = BuildChatterPrompt(a, idA, b, idB, recapAofB, true);

            List<LLMMessage> openMsgs = new List<LLMMessage>();
            openMsgs.Add(new LLMMessage("user", "Speak a brief, in-character line to them now."));

            BaseCreature ca = a;
            BaseCreature cb = b;

            LLMClient.TryDispatch("chatter:" + sa, sysA, openMsgs, "", "", "", 0, delegate(bool ok, string rawA)
            {
                if (!ok || string.IsNullOrEmpty(rawA))
                    return;

                if (ca == null || ca.Deleted || cb == null || cb.Deleted)
                    return;

                // They may have wandered apart while the model thought.
                if (ca.Map != cb.Map || ca.GetDistanceToSqrt(cb) > DriftRange)
                    return;

                string actA;
                string lineA = NpcActions.Extract(rawA, out actA);

                if (lineA.Length > 0)
                    ca.Say(lineA);

                NpcActions.Perform(ca, actA);

                // B heard A's line.
                if (lineA.Length > 0)
                    LLMAmbientMemory.GetOrCreateRelationship(sb, sa).Note(lineA);

                // B answers, grounded in what it just heard.
                string sysB = BuildChatterPrompt(cb, LLMAmbientSpeech.EnsureIdentity(cb),
                    ca, LLMAmbientSpeech.EnsureIdentity(ca), recapBofA, false);

                List<LLMMessage> replyMsgs = new List<LLMMessage>();
                string aname = string.IsNullOrEmpty(ca.Name) ? "the other townsperson" : ca.Name;
                replyMsgs.Add(new LLMMessage("user", aname + " says to you: \"" + lineA + "\""));

                LLMClient.TryDispatch("chatter:" + sb, sysB, replyMsgs, "", "", "", 0, delegate(bool ok2, string rawB)
                {
                    if (!ok2 || string.IsNullOrEmpty(rawB))
                        return;

                    if (cb == null || cb.Deleted)
                        return;

                    string actB;
                    string lineB = NpcActions.Extract(rawB, out actB);

                    if (lineB.Length > 0)
                        cb.Say(lineB);

                    NpcActions.Perform(cb, actB);

                    // A heard B's reply.
                    if (lineB.Length > 0)
                        LLMAmbientMemory.GetOrCreateRelationship(sa, sb).Note(lineB);
                });
            });
        }

        private static string RecapText(int selfSerial, int otherSerial)
        {
            NpcRelationship rel = LLMAmbientMemory.GetRelationship(selfSerial, otherSerial);
            return rel == null ? "" : rel.Recap();
        }

        // System prompt for one side of an ambient exchange. Mirrors the chat
        // prompt's persona/identity grounding (reusing LLMAmbientSpeech helpers),
        // but frames the listener as a fellow townsperson rather than a traveler,
        // and asks for a single short line.
        private static string BuildChatterPrompt(BaseCreature self, NpcIdentity idSelf,
            BaseCreature other, NpcIdentity idOther, string recapOfOther, bool initiating)
        {
            LLMTalkingMobile tmSelf = self as LLMTalkingMobile;
            if (tmSelf != null && tmSelf.IsMonsterSpeaker)
                return BuildMonsterChatterPrompt(tmSelf, idSelf, other, recapOfOther, initiating);

            string selfVoc = LLMAmbientSpeech.InferVocation(self);
            string otherVoc = LLMAmbientSpeech.InferVocation(other);

            StringBuilder sb = new StringBuilder();

            sb.Append("You are ");
            sb.Append(string.IsNullOrEmpty(self.Name) ? "an unnamed townsperson" : self.Name);

            if (!string.IsNullOrEmpty(self.Title))
            {
                sb.Append(" ");
                sb.Append(self.Title);
            }

            sb.Append(", ");
            sb.Append(LLMAmbientSpeech.PersonaFor(selfVoc));
            sb.Append(". You live in the medieval fantasy realm of Britannia, the world of Ultima Online. ");

            string town = (idSelf != null && !string.IsNullOrEmpty(idSelf.Town)) ? idSelf.Town : "town";

            if (idSelf != null)
            {
                sb.Append("You hold your post in the town of ");
                sb.Append(idSelf.Town);
                sb.Append(". ");

                if (!string.IsNullOrEmpty(idSelf.Personality))
                {
                    sb.Append("Your temperament is ");
                    sb.Append(idSelf.Personality);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(idSelf.SpeechStyle))
                {
                    sb.Append("You speak in a manner that is ");
                    sb.Append(idSelf.SpeechStyle);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(idSelf.Mood))
                {
                    sb.Append("Right now you are ");
                    sb.Append(idSelf.Mood);
                    sb.Append(". ");
                }
            }

            string oname = string.IsNullOrEmpty(other.Name) ? "another townsperson" : other.Name;
            sb.Append("You have crossed paths with ");
            sb.Append(oname);

            if (!string.IsNullOrEmpty(otherVoc))
            {
                sb.Append(", ");
                sb.Append(AnArticle(otherVoc));
                sb.Append(" ");
                sb.Append(otherVoc);
            }

            sb.Append(", here in ");
            sb.Append(town);
            sb.Append(". ");

            if (!string.IsNullOrEmpty(recapOfOther))
            {
                sb.Append(recapOfOther);
                sb.Append(" ");
            }

            if (initiating)
            {
                sb.Append("Greet them or make a brief remark — a passing word, a question, a bit of local talk or shop gossip. ");

                // P11: seed the opener with the town's freshest talk, so an
                // exchange can actually pass a rumor along instead of being
                // pure pleasantry. The reply side reacts to whatever was said.
                string rumor = TownGossip.PickOne(town);
                if (!string.IsNullOrEmpty(rumor))
                {
                    sb.Append("You have lately heard this talk about town: ");
                    sb.Append(rumor);
                    sb.Append(" — you might mention it if it suits the moment. ");
                }
            }
            else
            {
                sb.Append("Answer them naturally. ");
            }

            sb.Append("Reply with at most ONE short sentence in a medieval, in-world tone. ");
            sb.Append("Speak only your own words: no name label, no quotation marks, no narration. ");
            sb.Append("Always stay in character. Never break character, never mention being an AI or a computer, never mention the modern world or that this is a game.");

            sb.Append(NpcActions.PromptInstruction());

            return sb.ToString();
        }

        // System prompt for one side of a wild-speaker exchange (two orcs, two
        // liches, …). Unlike the townsfolk path it grounds in the creature's own
        // authored persona and SceneHint rather than a vocation-keyed townsperson
        // voice, and frames the other as one of its own kind rather than a fellow
        // townsperson in a town it holds a post in.
        private static string BuildMonsterChatterPrompt(LLMTalkingMobile self, NpcIdentity idSelf,
            BaseCreature other, string recapOfOther, bool initiating)
        {
            string persona = string.IsNullOrEmpty(self.Persona) ? self.DefaultPersona : self.Persona;

            string kind = LLMAmbientSpeech.InferVocation(self);
            if (string.IsNullOrEmpty(kind))
                kind = "creature";

            StringBuilder sb = new StringBuilder();

            sb.Append("You are ");
            sb.Append(string.IsNullOrEmpty(self.Name) ? ("a " + kind) : self.Name);

            if (!string.IsNullOrEmpty(self.Title))
            {
                sb.Append(" ");
                sb.Append(self.Title);
            }

            sb.Append(", ");
            sb.Append(persona);
            sb.Append(". You live in the medieval fantasy realm of Britannia, the world of Ultima Online. ");

            if (idSelf != null)
            {
                if (!string.IsNullOrEmpty(idSelf.Personality))
                {
                    sb.Append("Your temperament is ");
                    sb.Append(idSelf.Personality);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(idSelf.SpeechStyle))
                {
                    sb.Append("You speak in a manner that is ");
                    sb.Append(idSelf.SpeechStyle);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(idSelf.Mood))
                {
                    sb.Append("Right now you are ");
                    sb.Append(idSelf.Mood);
                    sb.Append(". ");
                }
            }

            sb.Append("Before you stands ");
            if (string.IsNullOrEmpty(other.Name))
            {
                sb.Append("another ");
                sb.Append(kind);
            }
            else
            {
                sb.Append(other.Name);
                sb.Append(", another ");
                sb.Append(kind);
            }
            sb.Append(" — one of your own kind. ");

            if (!string.IsNullOrEmpty(recapOfOther))
            {
                sb.Append(recapOfOther);
                sb.Append(" ");
            }

            if (initiating)
                sb.Append("Address them in your own voice — a taunt, a boast, a demand, a scrap of your own concerns. ");
            else
                sb.Append("Answer them in your own voice. ");

            sb.Append("Reply with at most ONE short sentence, in character. ");
            sb.Append("Speak only your own words: no name label, no quotation marks, no narration. ");
            sb.Append("Always stay in character. Never break character, never mention being an AI or a computer, never mention the modern world or that this is a game.");

            string scene = self.SceneHint;
            if (!string.IsNullOrEmpty(scene))
            {
                sb.Append(" ");
                sb.Append(scene);
            }

            sb.Append(NpcActions.PromptInstruction());

            return sb.ToString();
        }

        private static string AnArticle(string word)
        {
            if (string.IsNullOrEmpty(word))
                return "a";

            char c = char.ToLowerInvariant(word[0]);
            bool vowel = (c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u');
            return vowel ? "an" : "a";
        }
    }
}
