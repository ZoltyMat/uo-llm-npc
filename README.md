# LLM-driven NPCs for Ultima Online (ServUO)

Drop-in C# scripts that give the NPCs on a [ServUO](https://www.servuo.dev/)
shard a voice, a memory, and a small autonomous life — powered by a local,
OpenAI-compatible LLM (e.g. [Ollama](https://ollama.com/)) with optional
vector RAG over [Qdrant](https://qdrant.tech/).

A peasant you greet remembers you next time. A blacksmith has opinions about
the war and a town he hails from. Two townsfolk hold a murmured conversation
when you wander past. NPCs keep daily routines — a morning task, a midday
meal at the actual tavern — and towns keep rumor boards: what players say,
who slew what, who walks around in grandmaster plate, all of it carried
between cities by traveling NPCs. A villager who cannot
leave their post may entrust you with a sealed parcel for the banker of a
distant town — deliver it and both towns talk about you. Beg the realm's
Overseer for a weapon and it will deflect you; earn one, and the gift always
carries a hidden price.
And once in a long while, an NPC realizes it is an AI in a game and panics —
before the world quietly mends itself.

Write-up of how and why this was built:
**https://blog.zolty.systems/posts/2026-06-03-llm-npcs-ultima-online/**

> These are *integration scripts*, not a server. You bring your own ServUO
> shard; you drop these into `Scripts/Custom/` and point them at a model
> endpoint. Nothing here phones home, and everything LLM-touched is
> **fail-open** — if the model is slow, down, or wrong, the NPC silently falls
> back to vanilla behavior and the game keeps running.

## How it works

ServUO compiles C# under `Scripts/` at boot, so these files become live game
logic with no separate build step. The design rests on a few rules:

- **The LLM is never in the simulation loop.** Errands, journeys, and world
  state advance deterministically on a single player-centric heartbeat. The
  model is consulted only to generate *words* (and, optionally, to pick a
  cosmetic gesture from a fixed allowlist) — never to decide game state.
- **Off-screen NPCs cost nothing.** The heartbeat iterates connected players
  and only advances NPCs within simulation range of one, so an empty shard is
  idle.
- **Everything is fail-open.** Every network call has a timeout and a
  deterministic fallback. A dead model endpoint degrades NPCs to ordinary
  ServUO NPCs, not to errors.
- **Actions are an allowlist, not free text.** An NPC can `bow`, `wave`, or
  mime its trade because those verbs are in a hardcoded set
  (`NpcActions.cs`). The model picks *from* the menu; it cannot invent a verb.

## What's in here

| File | Role |
|------|------|
| `LLMConfig.cs` | Loads `Config/LLMNpc.cfg` (endpoints, model, toggles). Writes a default config on first boot. `[LLMReload` re-reads it live. |
| `LLMClient.cs` | Minimal OpenAI-compatible chat client (timeouts, retries, async). |
| `LLMConversation.cs` | Short-term per-(player, NPC) conversation memory. |
| `LLMTalkingMobile.cs` | Base class for creatures whose dialog is LLM-generated (custom NPCs, talking monsters, megalomaniac liches). |
| `LLMAmbientSpeech.cs` | Gives the *existing* vanilla NPCs (vendors, bankers, guards) a voice via a single global speech listener — no class changes. |
| `LLMAmbientMemory.cs` | Disk-backed identity + per-player relationship store for those vanilla NPCs, keyed by stable serial. |
| `NpcIdentity.cs` | Generates and persists a per-NPC persona — post, homeland, temperament, history, speech style, private drive, mood. |
| `NpcActions.cs` | The cosmetic-action allowlist the model may choose from. |
| `NpcChatter.cs` | Ambient NPC-to-NPC conversations near a watching player. |
| `Errand.cs` / `ErrandDirector.cs` / `ErrandPolicy.cs` | Deterministic autonomous errands and journeys: phases, the heartbeat that advances them, and per-type roam limits (the banker stays at the counter). |
| `BritanniaGeography.cs` | Maps a position to the town an NPC belongs to, so personas know whom they serve. |
| `LLMRag.cs` | Optional Qdrant retrieval: lore grounding, voice-style exemplars, and a per-NPC deed journal, plus Ollama embeddings. |
| `AnomalyDirector.cs` | Rare, player-gated 4th-wall "anomaly" events and the GM-avatar that mends them. |
| `DailyRoutine.cs` | Vocation-shaped day plans per game day — legs anchor to real NPCs (the tavern is wherever the tavernkeeper stands), executed by the errand engine. |
| `TownGossip.cs` | Per-town rumor boards: player talk, deaths, notable kills, banishments, and impressions of players themselves (gear, karma, mastery) — carried between towns by journeying NPCs, surfaced through chat. Zero extra LLM calls. |
| `OverseerActions.cs` | The Overseer's closed GM-tier verb list: harmless storms, small auto-expiring spawn packs, blessings, gifts, departure — every effect capped and cooldown-gated in code. |
| `GenieGifts.cs` | The genie rule: any weapon the Overseer grants pairs a real power with a real flaw (it bites its wielder, drains life, or is cursed). The model picks the verb; it cannot mint an unflawed item. |
| `PlayerFavors.cs` | Favors: townsfolk entrust players with sealed parcel deliveries to real NPCs in real towns — deterministic destination/reward/cooldowns, the LLM only picks the moment via `[do:offer]`. Delivery pays distance-scaled gold, karma, regard, and praise rumors on both towns' boards. |
| `LLMNpcCommands.cs` | In-game GM/admin commands (`[LLMReload`, diagnostics, toggles). |

## Requirements

- A working **ServUO** shard (these are scripts for it, not a standalone server).
- An **OpenAI-compatible chat endpoint**. The default config targets a local
  Ollama on `127.0.0.1:11434`. Any gateway that speaks the OpenAI chat API
  works — set `BaseUrl`/`ApiKey`.
- *(Optional)* a **Qdrant** instance for RAG, voice-style, and the NPC
  journal. Everything Qdrant-backed is off by default and fail-open.

## Setup

1. Copy `Scripts/Custom/LLMNpc/` into your shard's `Scripts/Custom/`.
2. Copy `LLMNpc.cfg.example` to your shard's `Config/LLMNpc.cfg` (or just boot
   once — the server writes a default copy), then edit it.
3. Set `Enabled=true`, point `BaseUrl` at your model, pick a `Model`.
4. Start the shard (or `[LLMReload` if it's already up). Talk to a peasant.

A reasonable starting model is a small/instruct local model; the code caps
reply length and tokens hard, so latency and cost stay bounded.

## Configuration

All knobs live in `Config/LLMNpc.cfg`. See `LLMNpc.cfg.example` for the full,
commented list. Highlights:

- `Enabled` — master switch. NPCs only call the model when true.
- `BaseUrl` / `ApiKey` / `Model` — the endpoint and model.
- `RagEnabled` + `RagUrl` / `RagCollection` — optional lore grounding.
- `ChatterEnabled` — NPC-to-NPC ambient conversation.
- `RoutineEnabled` — daily routines (a UO day is ~2 real hours).
- `GossipEnabled` — town rumor boards, cross-town spread, player observation.
- `FavorEnabled` — NPC-given parcel-delivery favors for players.
- `AnomalyEnabled` — the rare 4th-wall events.
- Cooldowns, hear-range, token/length caps — anti-spam and latency control.

**Do not commit your real `Config/LLMNpc.cfg`.** It can hold endpoints and API
keys; it's gitignored here for that reason. Only the `.example` belongs in
version control.

## License

MIT — see [LICENSE](LICENSE). Ultima Online and Britannia are trademarks of
their respective owners; this project is an unaffiliated, non-commercial
hobby integration for ServUO shards.
