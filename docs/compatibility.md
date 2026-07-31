# Why 0.4.0 stopped working, and how the fix works

Subsystem 0.4.0 was released in November 2018 and works against the game as it shipped then.
On a current build of _Homeworld: Deserts of Kharak_ it makes the main menu misbehave and the
transition into a match fail. This document records what actually broke, so that the next person
who has to re-fix this does not have to rediscover it.

## The short version

`Subsystem.dll` was never the problem. The install instructions were.

0.4.0 shipped a **complete, pre-patched `BBI.Unity.Game.dll`** — the entire game assembly as it
existed in 2018 — and told you to overwrite the game's own copy. Doing that on a current install
replaces years of game code with the 2018 version.

The actual modification inside that 1.85 MB file is three IL instructions.

## The hook

At the end of `BBI.Unity.Game.ShipbreakersMain.ResetEntityManager()`, immediately after the call
to `InitializeEntityManager`:

```
newobj  instance void Subsystem.AttributeLoader::.ctor()
ldsfld  class BBI.Core.Data.EntityTypeCollection BBI.Unity.Game.ShipbreakersMain::sEntityTypes
call    instance void Subsystem.AttributeLoader::LoadAttributes(class BBI.Core.Data.EntityTypeCollection)
```

That is the whole modification. `ResetEntityManager` runs at the start of every match, which is
why the README says stats are reloaded at the beginning of every game.

`SubsystemPatcher` applies exactly these three instructions to whatever `BBI.Unity.Game.dll` you
have, instead of replacing the file. Because it patches your copy, it keeps working across game
updates — you just re-run it after each one.

## Why the 2018 assembly breaks a current install

The 2018 `BBI.Unity.Game.dll` references **19 types and 51 members** that no longer exist in the
current game assemblies. They are concentrated in the multiplayer, lobby, leaderboard, stats and
DLC layers, which were rewritten when the game moved to Epic Online Services:

| Assembly | Examples of references that no longer resolve |
| --- | --- |
| `BBI.Steam` | `SteamLeaderboard`, `SteamLeaderboardUploader`, `SteamLobbyFinder`, `PlayerLobbyListInfo`, `AutomatchState`, `LobbyRole`, `SteamDLCPack` |
| `BBI.Spark` | `SparkSteamBridge`, `SparkProviderSettings..ctor` |
| `BBI.Unity.Game.Data` | `Network.SteamStatDefinition` |
| `BBI.Core` | `IPlayerGroupMetaData.MigrateGroup` |

A missing member does not fail at load time — it fails when the JIT first compiles the method
that uses it, throwing `MissingMethodException` or `TypeLoadException`. Menu and match-start code
touches lobby, stats and DLC paths, which is exactly where the reported symptoms appear.

`SubsystemPatcher --verify` reports this as a dangling-reference count, and the patcher refuses
to run against a mismatched assembly rather than making things worse.

## Why the mod assembly itself still works

Checked against a current install, `Subsystem.dll` from the 0.4.0 release:

- resolves **every** type and member reference it uses — zero unresolved
- has **all 21** of its wrapper classes still satisfying the game interfaces they implement, with
  no unimplemented members and no orphaned overrides
- has **every** wrapper copy-constructor copying 100% of its interface's properties, so no field
  silently defaults to zero

The 2018 sources also still compile against current game assemblies with no errors and no
warnings, producing an assembly that differs from the released binary only by one compiler
codegen idiom in one method.

In other words: seven years of game updates did not change the data model Subsystem patches.

## Verifying that the patch is safe

The patcher rewrites `BBI.Unity.Game.dll` with Mono.Cecil, which reconstructs the whole file
rather than splicing bytes. That was checked rather than assumed:

- **Every method body is unchanged except one.** Comparing an opcode-and-operand fingerprint of
  all 15,907 members before and after, only `ResetEntityManager` differs: +3 instructions, with
  its 2 exception handlers and 9 locals intact.
- **No metadata is lost.** All 26,312 lines of custom attributes and metadata flags — 109
  `[Serializable]` types, 10 `[NonSerialized]` fields, class layout, security declarations — are
  byte-identical before and after.
- **Nothing else to lose.** The assembly has no embedded resources, no exported types, no module
  references and no P/Invoke; `ILOnly` and the 32-bit architecture flag are preserved.
- **Restore is byte-exact.** `--restore` reproduces the original file's checksum exactly.

The output file is smaller than the original. That is metadata padding, not lost content.

## Working on this in future

Two tools in this repository exist to make the next investigation cheaper:

- `SubsystemLint` validates a `patch.json` against the shapes Subsystem deserializes and the
  names your install actually loaded. See [finding-names-and-values.md](finding-names-and-values.md).
- `EntityTypeDumper` (inside the mod) writes `Data/Subsystem.entities.log` on every game start,
  listing every entity type, its components, and each weapon's targeting data.

If a future game update does move the hook site, `SubsystemPatcher` fails loudly with the type
and method it could not find, rather than producing a broken assembly.
