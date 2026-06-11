using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // Gives the EXISTING vanilla ServUO NPCs (vendors, bankers, townsfolk, guards)
    // a voice, without touching their classes. LLMTalkingMobile subclasses wire
    // speech in via HandlesOnSpeech/OnSpeech; every other NPC ignores free-form
    // chat. This is the gap filler: a single global EventSink.Speech listener that,
    // when a player speaks near an ordinary human NPC, routes the line through the
    // same LLM pipeline and has the nearest such NPC answer.
    //
    // It is purely additive — it never sets e.Handled/e.Blocked, so vanilla vendor
    // commerce, banking, and training keywords keep working. Utterances that look
    // like a command (buy/sell/bank/...) are skipped entirely so those flows are
    // untouched.
    //
    // Vanilla mobiles can't serialize an NpcIdentity (that needs a subclass), so
    // their rolled identity and per-player relationships live in the disk-backed
    // LLMAmbientMemory singleton, keyed by Serial — so a blacksmith keeps the same
    // character and remembers who has spoken with them across reboots. Short-term
    // memory still reuses LLMConversation exactly as the custom NPCs do.
    public static class LLMAmbientSpeech
    {
        // Whole-word triggers that belong to vanilla NPC handling (vendor buy/sell,
        // banker balance/withdraw, animal trainer, etc.). If the player's line
        // contains any of these as a discrete word, we stay out of the way.
        private static readonly string[] m_CommandWords = new string[]
        {
            "buy", "sell", "bank", "balance", "withdraw", "deposit", "check",
            "statement", "train", "vendor", "stable", "claim", "release", "browse"
        };

        public static void Initialize()
        {
            // Auto-invoked by reflection after world load: the deserialized memory
            // singleton (if any) has already claimed its instance, so EnsureExists
            // only creates one on a fresh world, and Prune drops serials that no
            // longer resolve to a live mobile.
            LLMAmbientMemory.EnsureExists();
            LLMAmbientMemory.Prune();

            EventSink.Speech += EventSink_Speech;
        }

        private static void EventSink_Speech(SpeechEventArgs e)
        {
            try
            {
                if (e == null || e.Blocked || e.Handled)
                    return;

                if (!LLMConfig.Enabled)
                    return;

                Mobile from = e.Mobile;

                if (from == null || !from.Player || from.Deleted)
                    return;

                Map map = from.Map;
                if (map == null || map == Map.Internal)
                    return;

                string text = e.Speech;
                if (string.IsNullOrEmpty(text))
                    return;

                text = text.Trim();
                if (text.Length == 0)
                    return;

                if (!string.IsNullOrEmpty(LLMConfig.AllowedMap) &&
                    (map.Name == null || !map.Name.Equals(LLMConfig.AllowedMap, StringComparison.OrdinalIgnoreCase)))
                    return;

                // Leave vendor/bank/trainer command lines to the stock handlers.
                if (LooksLikeCommand(text))
                    return;

                int range = LLMConfig.HearRange;

                BaseCreature best = null;
                double bestDist = double.MaxValue;
                double nearestCustom = double.MaxValue;

                IPooledEnumerable eable = map.GetMobilesInRange(from.Location, range);

                foreach (Mobile m in eable)
                {
                    if (m == from || m.Deleted || m.Player || !m.Alive)
                        continue;

                    BaseCreature bc = m as BaseCreature;
                    if (bc == null)
                        continue;

                    if (bc.Controlled || bc.Summoned)
                        continue;

                    // Human townsfolk have always been eligible. Also let hostile
                    // creatures (monsters/undead — negative karma) answer, so spawned
                    // witches, ghouls, orcs and the like respond in-character instead
                    // of standing mute while a farther human NPC fields the line.
                    // Benign non-human fauna (chickens, deer: karma >= 0) stay silent.
                    if (!m.Body.IsHuman && bc.Karma >= 0)
                        continue;

                    double dist = from.GetDistanceToSqrt(m);

                    // The custom LLM NPCs answer themselves via OnSpeech; track the
                    // closest one so we can defer to it (avoid two NPCs replying to
                    // the same line the player clearly aimed at the custom one).
                    if (m is LLMTalkingMobile)
                    {
                        if (dist < nearestCustom)
                            nearestCustom = dist;
                        continue;
                    }

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = bc;
                    }
                }

                eable.Free();

                if (best == null)
                    return;

                // A custom NPC is closer and will already respond — don't pile on.
                if (nearestCustom <= bestDist)
                    return;

                Respond(best, from, text);
            }
            catch (Exception ex)
            {
                Console.WriteLine("LLMAmbientSpeech: " + ex.Message);
            }
        }

        private static void Respond(BaseCreature npc, Mobile player, string text)
        {
            int npcSerial = npc.Serial.Value;
            int playerSerial = player.Serial.Value;

            NpcIdentity id = EnsureIdentity(npc);

            List<LLMMessage> history = LLMConversation.Get(npcSerial, playerSerial);
            history.Add(new LLMMessage("user", text));

            // Build the prompt from prior relationship state, THEN fold this line in,
            // so the NPC reacts to what it knew before the player just spoke.
            string system = BuildSystemPrompt(npc, player, id);

            NoteInteraction(npcSerial, playerSerial, id, text);

            // P11: a salient line may enter the town's talk (chance-gated inside),
            // where chatter and journeying NPCs can spread it.
            TownGossip.MaybeAddPlayerRumor(npc, player, text);

            // P15: an eligible townsperson may roll a parcel-favor to offer this
            // player (heavily gated inside; asking for work guarantees it). Done
            // BEFORE the prompt is built so the offer can ride this very reply.
            FavorDirector.MaybeCreatePending(npc, player, text);

            string key = npcSerial.ToString();
            string archetype = id != null ? id.Archetype : "";
            string region = id != null ? id.Town : "";

            BaseCreature mob = npc;

            LLMClient.TryDispatch(key, system, history, text, region, archetype, npcSerial, delegate(bool ok, string reply)
            {
                if (mob == null || mob.Deleted)
                    return;

                if (!ok || string.IsNullOrEmpty(reply))
                    return;

                // The LLM may end its reply with a [do:VERB] action tag; strip it
                // from the spoken line and run it as a safe cosmetic gesture —
                // or, when a pending favor exists, the [do:offer] hand-over (P15).
                string verb;
                string spoken = NpcActions.Extract(reply, FavorDirector.OfferVerbs, out verb);

                LLMConversation.Record(npcSerial, playerSerial, "user", text);
                LLMConversation.Record(npcSerial, playerSerial, "assistant", spoken);

                if (spoken.Length > 0)
                    mob.Say(spoken);

                if (!FavorDirector.TryPerformOffer(mob, player, verb))
                    NpcActions.Perform(mob, verb);
            });
        }

        // Fetch this NPC's persisted identity, generating + storing one on first
        // use. Public so the ErrandDirector can ground an LLM-written errand
        // purpose in the SAME character the NPC will later speak as — the errand
        // an NPC runs and the way it talks then draw from one identity.
        public static NpcIdentity EnsureIdentity(Mobile npc)
        {
            // A talking mobile carries its own serialized identity on the
            // subclass. Return THAT, never a fresh ambient copy — so its chat,
            // errand purpose, and NPC-to-NPC chatter all speak as one character
            // instead of diverging into two parallel identities for one body.
            LLMTalkingMobile talker = npc as LLMTalkingMobile;
            if (talker != null)
                return talker.Identity;

            int s = npc.Serial.Value;

            NpcIdentity id;
            if (LLMAmbientMemory.TryGetIdentity(s, out id))
                return id;

            string vocation = InferVocation(npc);
            string town = BritanniaGeography.TownOf(npc);
            id = NpcIdentity.Generate(vocation, town, npc.Female);
            LLMAmbientMemory.SetIdentity(s, id);

            return id;
        }

        // Mirrors LLMTalkingMobile.NoteInteraction: fold the utterance into the
        // persistent relationship and let mood drift, regardless of whether the
        // dispatch is throttled.
        private static void NoteInteraction(int npcSerial, int playerSerial, NpcIdentity id, string text)
        {
            LLMAmbientMemory.GetOrCreateRelationship(npcSerial, playerSerial).Note(text);

            if (id != null)
                id.DriftMood();
        }

        // Best-effort trade from the NPC's type name + title. Unknown vocations are
        // fine — NpcIdentity.Generate falls back to generic flavor pools. Public so
        // the ErrandDirector keys profession-appropriate errands off the same logic.
        public static string InferVocation(Mobile npc)
        {
            string s = (npc.GetType().Name + " " + (npc.Title == null ? "" : npc.Title)).ToLowerInvariant();

            // The custom wild speakers (LLM-prefixed type names) come first so two
            // ogres read as "ogre" rather than the generic fallback, and so the orc
            // case keys off "feralorc" — bare "orc" would also match "Sorcerer".
            if (s.IndexOf("lich") >= 0)
                return "lich";
            if (s.IndexOf("ogre") >= 0)
                return "ogre";
            if (s.IndexOf("lizardman") >= 0)
                return "lizardman";
            if (s.IndexOf("ratman") >= 0)
                return "ratman";
            if (s.IndexOf("gargoyle") >= 0)
                return "gargoyle";
            if (s.IndexOf("daemon") >= 0)
                return "daemon";
            if (s.IndexOf("feralorc") >= 0)
                return "orc";

            if (s.IndexOf("bank") >= 0 || s.IndexOf("minter") >= 0)
                return "banker";

            if (s.IndexOf("smith") >= 0 || s.IndexOf("armor") >= 0 || s.IndexOf("weapon") >= 0)
                return "blacksmith";

            if (s.IndexOf("tavern") >= 0 || s.IndexOf("barkeep") >= 0 || s.IndexOf("bartender") >= 0 ||
                s.IndexOf("innkeeper") >= 0 || s.IndexOf("waiter") >= 0 || s.IndexOf("cook") >= 0)
                return "tavernkeeper";

            if (s.IndexOf("farmer") >= 0 || s.IndexOf("peasant") >= 0 || s.IndexOf("beggar") >= 0 ||
                s.IndexOf("townsfolk") >= 0 || s.IndexOf("villager") >= 0 || s.IndexOf("gypsy") >= 0 ||
                s.IndexOf("fisher") >= 0 || s.IndexOf("sailor") >= 0 || s.IndexOf("noble") >= 0)
                return "villager";

            return "";
        }

        public static string PersonaFor(string vocation)
        {
            switch (vocation)
            {
                case "banker":
                    return "a banker of the realm who safeguards the gold and goods of those who walk through your door, mindful of coin and discreet about your patrons";
                case "blacksmith":
                    return "the town smith, gruff and proud of your craft, you work weapons and armor and speak plainly";
                case "tavernkeeper":
                    return "the warm, talkative keeper of the local tavern, full of rumors, ale, and welcome for travelers";
                case "villager":
                    return "a humble townsperson who knows the local gossip and goes about an honest day's work";
                default:
                    return "common folk of the realm, going about your day in town";
            }
        }

        // A creature-kind label for the monster prompt. Reuses the named-monster
        // cases InferVocation already knows (lich, ogre, orc, ...) and otherwise
        // humanizes the type name (e.g. "Ghoul" -> "ghoul", "OrcishMage" -> "orcish
        // mage"), so ANY hostile mob gets a sensible self-description with no table.
        public static string InferCreatureKind(Mobile npc)
        {
            string v = InferVocation(npc);
            switch (v)
            {
                case "lich":
                case "ogre":
                case "lizardman":
                case "ratman":
                case "gargoyle":
                case "daemon":
                case "orc":
                    return v;
            }

            return Humanize(npc.GetType().Name);
        }

        // "LLMFeralOrc" -> "feral orc", "OrcishMage" -> "orcish mage", "Ghoul" ->
        // "ghoul": drop a leading custom "LLM" prefix, split interior capitals,
        // lowercase. A last-ditch fallback keeps the slot non-empty.
        private static string Humanize(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return "creature";

            if (typeName.StartsWith("LLM", StringComparison.Ordinal))
                typeName = typeName.Substring(3);

            StringBuilder sb = new StringBuilder(typeName.Length + 4);
            for (int i = 0; i < typeName.Length; i++)
            {
                char c = typeName[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(typeName[i - 1]))
                    sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }

            string s = sb.ToString().Trim();
            return s.Length == 0 ? "creature" : s;
        }

        // Prompt for a hostile creature the ambient listener voices (P8 "talking
        // monsters"): ghoul, witch, orc, lich, daemon, and so on. Menacing and
        // terse, never the honest-townsperson framing. A rolled identity (name,
        // temperament, mood) is folded in when present; a generic mob just speaks
        // as its kind. No town/post/errand context — monsters keep no honest trade.
        private static string BuildMonsterPrompt(Mobile npc, Mobile player, NpcIdentity id)
        {
            string kind = InferCreatureKind(npc);

            StringBuilder sb = new StringBuilder();

            sb.Append("You are ");
            if (!string.IsNullOrEmpty(npc.Name))
            {
                sb.Append(npc.Name);
                sb.Append(", ");
            }
            sb.Append("a ");
            sb.Append(kind);
            sb.Append(", a hostile creature that prowls the wilds and dungeons of Britannia, the world of Ultima Online. ");

            if (id != null)
            {
                if (!string.IsNullOrEmpty(id.Personality))
                {
                    sb.Append("Your nature is ");
                    sb.Append(id.Personality);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.Mood))
                {
                    sb.Append("Right now you are ");
                    sb.Append(id.Mood);
                    sb.Append(". ");
                }
            }

            sb.Append("You are no townsperson and keep no honest trade. ");
            sb.Append("Speak as the creature you are — menacing, terse, and strange — in at most two short sentences, in a medieval, in-world tone. ");
            sb.Append("You may be cruel, cryptic, or hungry, but you still answer when spoken to. ");
            sb.Append("Never break character, never mention being an AI or a computer, never mention the modern world or that this is a game. ");

            sb.Append("An adventurer named ");
            sb.Append(string.IsNullOrEmpty(player.Name) ? "a stranger" : player.Name);
            sb.Append(" has spoken to you. ");

            NpcRelationship rel = LLMAmbientMemory.GetRelationship(npc.Serial.Value, player.Serial.Value);
            sb.Append(rel == null ? "You do not know them." : rel.Recap());

            sb.Append(NpcActions.PromptInstruction());

            return sb.ToString();
        }

        private static string BuildSystemPrompt(Mobile npc, Mobile player, NpcIdentity id)
        {
            // Hostile creatures (negative karma) speak as the menacing monsters they
            // are, not as honest townsfolk — and their body may not even be human
            // (ghoul, wisp), so the townsperson framing below never fits them.
            BaseCreature creature = npc as BaseCreature;
            if (creature != null && creature.Karma < 0)
                return BuildMonsterPrompt(npc, player, id);

            string vocation = InferVocation(npc);
            string persona = PersonaFor(vocation);

            StringBuilder sb = new StringBuilder();

            sb.Append("You are ");
            sb.Append(string.IsNullOrEmpty(npc.Name) ? "an unnamed townsperson" : npc.Name);

            if (!string.IsNullOrEmpty(npc.Title))
            {
                sb.Append(" ");
                sb.Append(npc.Title);
            }

            sb.Append(", ");
            sb.Append(persona);
            sb.Append(". You live in the medieval fantasy realm of Britannia, the world of Ultima Online. ");

            if (id != null)
            {
                sb.Append("You hold your post in the town of ");
                sb.Append(id.Town);
                sb.Append(". ");

                if (!string.IsNullOrEmpty(id.Origin) && id.Origin != id.Town)
                {
                    sb.Append("You hail originally from ");
                    sb.Append(id.Origin);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.Personality))
                {
                    sb.Append("Your temperament is ");
                    sb.Append(id.Personality);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.Backstory))
                {
                    sb.Append(id.Backstory);
                    sb.Append(" ");
                }

                if (!string.IsNullOrEmpty(id.SpeechStyle))
                {
                    sb.Append("You speak in a manner that is ");
                    sb.Append(id.SpeechStyle);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.Motivation))
                {
                    sb.Append("Privately — never say this outright — you are ");
                    sb.Append(id.Motivation);
                    sb.Append(". ");
                }

                if (!string.IsNullOrEmpty(id.Mood))
                {
                    sb.Append("Right now you are ");
                    sb.Append(id.Mood);
                    sb.Append(". ");
                }
            }

            sb.Append("Always stay in character. Reply with at most two short sentences in a medieval, in-world tone. ");
            sb.Append("Never break character, never mention being an AI or a computer, never mention the modern world or that this is a game. ");

            sb.Append("You are speaking with an adventurer named ");
            sb.Append(string.IsNullOrEmpty(player.Name) ? "a stranger" : player.Name);
            sb.Append(". ");

            NpcRelationship rel = LLMAmbientMemory.GetRelationship(npc.Serial.Value, player.Serial.Value);
            sb.Append(rel == null ? "You have never met them before." : rel.Recap());

            AppendErrandContext(sb, npc.Serial.Value);

            // P10: today's stated aim, if the dawn call rolled one.
            string intent = DailyRoutine.IntentionOf(npc.Serial.Value);
            if (!string.IsNullOrEmpty(intent))
            {
                sb.Append(" Today you have a mind for ");
                sb.Append(intent);
                sb.Append(".");
            }

            // P11/P12: the talk of the town, sharable when conversation invites it.
            sb.Append(TownGossip.PromptBlock(id != null && !string.IsNullOrEmpty(id.Town)
                ? id.Town : BritanniaGeography.TownOf(npc)));

            // P15: an open parcel-favor this NPC could offer the traveler.
            sb.Append(FavorDirector.PromptBlock(npc.Serial.Value, player.Serial.Value));

            sb.Append(NpcActions.PromptInstruction());

            return sb.ToString();
        }

        // If the NPC is mid-errand, tell the model so — and license it to share when
        // asked. This rides the existing chat call (no extra LLM request) so "what
        // are you doing out here?" gets an answer grounded in the deterministic
        // errand the ErrandDirector is actually running.
        private static void AppendErrandContext(StringBuilder sb, int npcSerial)
        {
            Errand e;
            if (!LLMAmbientMemory.TryGetErrand(npcSerial, out e) || e == null || !e.Active)
                return;

            sb.Append(" ");

            if (e.Journey)
            {
                // A cross-continent trip: e.Kind already names the destination city.
                sb.Append("You are far from your home town just now, ");
                sb.Append(e.Kind);
                sb.Append(". It has been a long road, and you do not expect to be here long.");
            }
            else
            {
                switch (e.State)
                {
                    case ErrandState.Outbound:
                        sb.Append("You are away from your usual place right now, out ");
                        sb.Append(e.Kind);
                        sb.Append(".");
                        break;
                    case ErrandState.Dwelling:
                        sb.Append("You are out about town ");
                        sb.Append(e.Kind);
                        sb.Append(", and have nearly seen to it.");
                        break;
                    case ErrandState.Returning:
                        sb.Append("You are on your way back to your usual place, having been ");
                        sb.Append(e.Kind);
                        sb.Append(".");
                        break;
                }
            }

            sb.Append(" If the traveler asks what you are doing, where you are going, or why you are here rather than at your post, you may tell them about this errand — briefly and in character. Do not recite it unprompted.");
        }

        private static bool LooksLikeCommand(string text)
        {
            string[] words = text.ToLowerInvariant().Split(
                new char[] { ' ', '\t', ',', '.', '!', '?', ';', ':' },
                StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < words.Length; i++)
            {
                for (int j = 0; j < m_CommandWords.Length; j++)
                {
                    if (words[i] == m_CommandWords[j])
                        return true;
                }
            }

            return false;
        }
    }
}
