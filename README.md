# WotlkBot — CoA balance-harness fork

> **Fork of [Artanidos/WotlkBot](https://github.com/Artanidos/WotlkBot)** — all credit for the
> client to Artanidos, and to [justMaku/mClient](https://github.com/justMaku/mClient) (Michał
> Kałużny) beneath it. This fork repurposes it from a *party bot* into an **automated damage- and
> balance-measurement harness** for a custom-class WotLK 3.3.5a server.
>
> Upstream's original README is preserved at [`README.upstream.md`](README.upstream.md).

Instead of a human standing at a training dummy spamming a key, this logs a headless client into
the server, casts real spells, and reports the damage **the server itself resolved** — so crit,
resists, procs and scaling all come from the live combat pipeline. No game client, no window
focus, no combat-log flush.

---

## What changed vs upstream

### Protocol fixes — offered back as [PR #4](https://github.com/Artanidos/WotlkBot/pull/4)

These are upstream bugs, not fork-specific behaviour:

| file | bug |
|---|---|
| `Client/Network/PacketLoop.cs` | SMSG headers are **variable length** — packets `>= 0x8000` carry a **3-byte** size with the high bit set. Always reading 2 bytes consumed one byte too few from the **stateful ARC4 keystream**, so the first large packet (the initial object update on entering the world) desynced the cipher and *every packet after it* decoded to garbage. |
| `Client/Clients/WorldServerClient/WorldServerClient.cs` | `UnpackGuid` advanced a running `shift` only on **set** mask bits, packing present bytes contiguously and discarding their positions: `F130007F9B000202` → `F1307F9B0202`. Each present byte belongs at `8 * i`. Every guid the client parsed was corrupt, so object scans matched nothing — and a corrupt guid looks exactly like "that object no longer exists". |
| `Client/Utils/Log.cs` | `string.Format` called unconditionally; packet data containing `{` threw `FormatException`, which propagated out of object-update parsing and left the object manager empty. |

### Harness additions — fork-specific

| file | what |
|---|---|
| `WorldServerClient.CombatLog.cs` | `SMSG_SPELLNONMELEEDAMAGELOG` handler printing `DMG …` lines, plus periodic tick / HoT decoding. **This is what makes anything measurable.** |
| `WorldServerClient.State.cs` | aura / stack readout, for build-and-spend resource pools |
| `WorldServerClient.Combat.cs` | `SMSG_CAST_RESULT` → `CASTFAIL … code=<n>`. **A rejected cast used to be completely silent**, which is indistinguishable from "this ability does no damage" — a *balance conclusion* drawn from a parsing gap. |
| `WorldServerClient.Movement.cs` | `FaceTarget` — a headless bot has no camera, so directional spells failed `SPELL_FAILED_UNIT_NOT_INFRONT` |
| `Bot/Program.cs` | graceful logout, persistent command-file mode, multi-account roster support |

---

## Running it

```bash
cd bin
./WotlkBot.exe <host> <account> <password> <character> <spellId> <count> <delayMs> auto stay \
    > ../run.txt 2>&1 &
```

Then drive it by writing to its per-character command file, `cmd-<CharName>.txt`:

```
target entry 31146      # by creature ENTRY, not guid
cast 960086 15 6500     # spellId, count, delay
quit                    # ALWAYS quit cleanly
```

### Five traps that cost real measurement time

1. **Launch with `count = 0`.** Any non-zero count fires that batch *immediately* at the
   auto-selected nearest creature, before you can aim it. At the Stormwind dummies that is a
   **level 60** target, and the resulting average silently blends no-miss L60 hits with
   17 %-miss L83 hits.
2. **Target by `entry`, never a hardcoded guid.** Runtime creature guids are regenerated per
   world session (`map->GenerateLowGuid`), so a guid documented as "stable" is junk after a restart.
3. **Never `taskkill` — send `quit`.** A hard kill strands the character at `online = 1`, and the
   next login then fails every cast with `SPELL_FAILED_BAD_TARGETS`.
4. **One bot per *account*.** An account cannot hold two simultaneous logins, so concurrency is
   bounded by account count, not character count.
5. **Zero `CASTGO` *and* zero `CASTFAIL` means the character does not know the spell.** A real
   failure emits a code; silence means no packet was ever sent.

### It is a detector, not a calibrated instrument

Measured against a known `+50 %` damage buff, the harness read **`+29.5 %`**. Treat results as
*"does this do anything at all"* and expect real effects to be larger than measured. And a null
result is only meaningful with a **positive control from the same code path** — a null and a blind
instrument are indistinguishable from the inside.

### Known limitation

**No minimum-range handling.** The bot closes to melee and casts, so every *ranged* ability is
refused client-side with `SPELL_FAILED_TOO_CLOSE (128)`. Any null or zero-damage result on a
ranged spell is meaningless until that is fixed — check `SpellRange.dbc` `minHostile` before
believing a zero.

---

## The harness is mostly *not* in this repo

This client is the **transport**. The measurement logic — roster generation, wave orchestration,
estimators, VOID-guards, offline log re-parsing, crash symbol resolution — is ~315 Python tools
living alongside the server project. This repo is the part that talks to the server.

## Licence

Upstream carries **no licence file**, so it is all-rights-reserved by default; this fork exists
under GitHub's Terms of Service fork grant. If you want to reuse any of it beyond that, ask
[@Artanidos](https://github.com/Artanidos) — and it would be good if upstream added an explicit
licence.
