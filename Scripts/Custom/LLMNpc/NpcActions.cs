using System;
using System.Text;
using System.Text.RegularExpressions;
using Server;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // Lets an NPC's LLM reply also CHOOSE a small physical action — a bow, a
    // shrug, miming its trade — so talking to (or overhearing) townsfolk shows
    // body language, not just text.
    //
    // The LLM only ever PICKS from a closed, hand-vetted verb list; it never
    // names an engine call, a skill, an item, or a target. A deterministic parser
    // extracts the verb and a deterministic dispatcher maps it to a safe, purely
    // cosmetic effect (an overhead emote, and for the two engine-defined gestures
    // a real animation). Nothing here can attack, move, equip, consume, or alter
    // world state — so a hallucinated or adversarial reply can do no harm. This is
    // the deterministic-guardrail-before-LLM-judgment pattern: the model gets
    // expressive agency, the engine surface stays a fixed allowlist.
    public static class NpcActions
    {
        // The ONLY verbs the model may pick. Anything else is dropped.
        private static readonly string[] m_Verbs = new string[]
        {
            "bow", "salute", "nod", "cheer", "laugh", "yawn", "point",
            "eat", "drink", "work", "none"
        };

        // Pulls an action tag off a model reply: returns the spoken text with the
        // tag removed (out verb = the lowercased verb, or "" if absent/"none").
        //
        // We ASK for [do:VERB], but small local models don't reliably emit that
        // exact shape — observed in-world: [point:point], [work:work]. So we match
        // ANY bracketed chunk and look for a known verb token inside it; the tag
        // syntax is just extraction, the allowlist is the guardrail (only a
        // recognized verb ever runs). A bracket carrying no known verb is left in
        // the speech untouched, so ordinary prose in brackets isn't eaten. Among
        // multiple action brackets the LAST known verb wins.
        private static readonly Regex m_Tag = new Regex(@"\[[^\[\]]*\]");
        private static readonly Regex m_Word = new Regex(@"[a-zA-Z]+");

        public static string Extract(string reply, out string verb)
        {
            return Extract(reply, null, out verb);
        }

        // Overload with an additional caller-supplied allowlist — the Overseer
        // (P14) recognizes its GM-tier verbs on top of the cosmetic set. The
        // extra list is just more closed vocabulary; extraction stays identical.
        public static string Extract(string reply, string[] extraVerbs, out string verb)
        {
            verb = "";

            if (string.IsNullOrEmpty(reply))
                return reply == null ? "" : reply;

            string captured = "";

            string spoken = m_Tag.Replace(reply, delegate(Match m)
            {
                string chosen = "";
                MatchCollection words = m_Word.Matches(m.Value);
                for (int j = 0; j < words.Count; j++)
                {
                    string w = words[j].Value.ToLowerInvariant();
                    if (IsKnown(w) || InList(extraVerbs, w))
                        chosen = w; // last known token in this bracket wins
                }

                if (chosen.Length == 0)
                    return m.Value; // not an action tag — keep it in the speech

                if (chosen != "none")
                    captured = chosen;

                return " ";
            });

            verb = captured;
            spoken = Regex.Replace(spoken, @"\s+", " ").Trim();

            return spoken;
        }

        // Runs the chosen verb as a safe, visible gesture. No-ops on empty/unknown
        // verb, a dead/deleted/internal-map NPC, or a verb with no effect available.
        public static void Perform(Mobile npc, string verb)
        {
            if (npc == null || npc.Deleted || !npc.Alive)
                return;

            if (npc.Map == null || npc.Map == Map.Internal)
                return;

            if (string.IsNullOrEmpty(verb) || !IsKnown(verb) || verb == "none")
                return;

            try
            {
                switch (verb)
                {
                    case "bow":
                        Gesture(npc, 0); // AnimationType.Emote, action 0 (engine: Animations.cs)
                        break;
                    case "salute":
                        Gesture(npc, 1); // AnimationType.Emote, action 1
                        break;
                    case "eat":
                        EatAnim(npc);
                        npc.Emote("*takes a bite*");
                        break;
                    case "drink":
                        EatAnim(npc);
                        npc.Emote("*takes a drink*");
                        break;
                    case "work":
                        npc.Emote("*" + WorkEmote(npc) + "*");
                        break;
                    case "nod":
                        npc.Emote("*nods*");
                        break;
                    case "cheer":
                        npc.Emote("*cheers*");
                        break;
                    case "laugh":
                        npc.Emote("*laughs*");
                        break;
                    case "yawn":
                        npc.Emote("*yawns*");
                        break;
                    case "point":
                        npc.Emote("*points*");
                        break;
                }
            }
            catch (Exception ex)
            {
                LLMClient.Log("NpcActions.Perform(" + verb + "): " + ex.Message);
            }
        }

        // Appended to the system prompt so the model knows its (closed) action
        // vocabulary. Kept terse so it costs few tokens on every turn.
        public static string PromptInstruction()
        {
            return " You may OPTIONALLY end your reply with a single action tag of the form [do:VERB], " +
                   "where VERB is exactly one of: bow, salute, nod, cheer, laugh, yawn, point, eat, drink, work, none. " +
                   "Choose one only when it fits what you say or do — greet with a bow, mime your trade with work, and so on. " +
                   "Most lines need no action: use none or omit the tag. Invent no other verbs, and write nothing after the tag.";
        }

        // The two engine-defined human gestures (bow/salute) via the modern SA
        // animation packet, with the legacy packet as a pre-SA fallback. Mounted
        // mobiles can't play these (engine guard), so they fall through silently.
        private static void Gesture(Mobile npc, int action)
        {
            if (npc.Mounted || !(npc.Body.IsHuman || npc.Body.IsGargoyle))
                return;

            if (Core.SA)
                npc.Animate(AnimationType.Emote, action);
            else
                npc.Animate(action == 0 ? 32 : 33, 5, 1, true, false, 0); // legacy bow=32 / salute=33
        }

        // Eat/drink share the Eat animation category. Only the modern packet has a
        // clean semantic action for it; pre-SA just relies on the emote text.
        private static void EatAnim(Mobile npc)
        {
            if (npc.Mounted || !npc.Body.IsHuman)
                return;

            if (Core.SA)
                npc.Animate(AnimationType.Eat, 0);
        }

        // A trade-flavored "using a skill" stage direction, themed off the same
        // vocation inference the chat/errand paths use so the gesture matches the
        // NPC's craft.
        private static string WorkEmote(Mobile npc)
        {
            string vocation = LLMAmbientSpeech.InferVocation(npc);

            switch (vocation)
            {
                case "banker":
                    return "counts out a stack of gold coins";
                case "blacksmith":
                    return "hammers at the glowing forge";
                case "tavernkeeper":
                    return "wipes down the bar";
                case "villager":
                    return "sets to an honest chore";
                default:
                    return "busies about the day's work";
            }
        }

        private static bool IsKnown(string verb)
        {
            return InList(m_Verbs, verb);
        }

        private static bool InList(string[] list, string verb)
        {
            if (list == null)
                return false;

            for (int i = 0; i < list.Length; i++)
                if (list[i] == verb)
                    return true;

            return false;
        }
    }
}
