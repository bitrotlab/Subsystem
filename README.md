# Subsystem

Subsystem is a mod loader for _Homeworld: Deserts of Kharak_. It aims to have a safe, auditable and data-oriented design.

### Disclaimer

Subsystem is currently in alpha. It may be extremely unstable, and it's not likely to provide useful debugging information. Use at your own risk, and please try to dig into logs on your own before reporting crashes or other odd behavior.

## Installation

> **The 0.4.0 release instructions no longer work.** See [Why the old install broke](#why-the-old-install-broke).
> Use `SubsystemPatcher` instead — it patches the copy of `BBI.Unity.Game.dll` you already have,
> so it keeps working on current versions of the game.

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8.0 or newer).

```sh
# 1. build the mod assembly against your installed game
dotnet build build/Subsystem.Sdk.csproj -c Release

# 2. copy it into the game
cp build/bin/Release/net35/Subsystem.dll "<...>/Deserts of Kharak/Data/Managed/"

# 3. hook it into the game's own BBI.Unity.Game.dll
dotnet run --project SubsystemPatcher -c Release
```

Both steps locate a Steam install automatically (including Flatpak Steam and Proton prefixes on
Linux). If that fails, point them at the folder yourself:

```sh
dotnet build build/Subsystem.Sdk.csproj -c Release -p:ManagedDir="<...>/Deserts of Kharak/Data/Managed"
dotnet run --project SubsystemPatcher -c Release -- --managed "<...>/Deserts of Kharak/Data/Managed"
```

Other `SubsystemPatcher` options:

| Option | Effect |
| --- | --- |
| `--verify` | Report whether the hook and `Subsystem.dll` are installed, and whether `BBI.Unity.Game.dll` matches the rest of the install |
| `--restore` | Put the original `BBI.Unity.Game.dll` back from the backup |
| `--force` | Re-apply the hook even if it is already present |

The original file is saved as `BBI.Unity.Game.dll.subsystem-backup` before anything is written.
You can also always recover it with Steam: *Properties → Installed Files → Verify integrity*.

**Re-run the patcher after every game update.** Steam replaces `BBI.Unity.Game.dll`, which removes
the hook. The game will simply run unmodded until you patch it again.

### Why the old install broke

The 0.4.0 zip shipped a whole pre-patched `BBI.Unity.Game.dll` built against the November 2018
game and told you to overwrite the game's own copy. The actual modification in that file is three
IL instructions; everything else is just the 2018 game, so installing it rolls back years of game
code. `SubsystemPatcher` applies those same three instructions to *your* copy instead.

[docs/compatibility.md](docs/compatibility.md) has the full account: the hook, the 19 types and 51
members the old assembly references that no longer exist, and the verification that the rewrite
changes nothing else.

## Documentation

| | |
| --- | --- |
| [Finding names and values](docs/finding-names-and-values.md) | Discovering entity, component and weapon names for **your** install, and reading current values out of the logs |
| [patch.json reference](docs/patch-reference.md) | Every patchable property, the enum values, and the rules that will bite you |
| [Troubleshooting](docs/troubleshooting.md) | Symptom-first: nothing happened, a unit will not shoot, a change did not apply |
| [Compatibility](docs/compatibility.md) | What broke in 2018, why, and how the fix works |
| [Building and releasing](docs/building.md) | Build requirements, project layout, CI and releases |

## Usage

Create a file at `Deserts of Kharak/Data/patch.json`. The name and location must match exactly,
and all keys are case-sensitive.

```json
{
  "Entities": {
    "C_Escort_MP": {
      "WeaponAttributes": {
        "C_Escort_Weapon_G2G_MP": {
          "BaseDamagePerRound": 14,
          "ExcludeFromHeightAdvantage": true
        }
      }
    },
    "G_Baserunner_MP": {
      "UnitAttributes": {
        "MaxHealth": 3500,
        "Armour": 3,
        "Resource1Cost": 225,
        "ProductionTime": 18.0
      }
    }
  }
}
```

Stats are reloaded at the beginning of every match. `patch.example.json` is the same file, ready
to copy.

After a match starts, `Data/Subsystem.log` lists everything that changed along with each field's
**previous** value — which is also the easiest way to learn the game's native scale for a stat
before deciding what to set it to.

See the [patch.json reference](docs/patch-reference.md) for every property you can set.

### Check your patch before you launch

```sh
dotnet run --project SubsystemLint -c Release
```

It reports unknown keys, wrong value types, invalid enum values, list keys that are not integers,
and entity and component names that do not exist in your install — with spelling suggestions.
Exit code 0 when clean, 2 when it finds problems.

Worth running every time, because **a single unknown key discards your entire patch**. LitJson's
`ignore_extra_keys` defaults to `false`, so one unrecognised property makes the whole document
throw; `AttributeLoader` catches it, logs one line to `output_log.txt`, and applies nothing at
all. "My change did nothing" is usually a typo somewhere else in the file.

### Finding entity and component names

Every game start, Subsystem writes `Data/Subsystem.entities.log` — every entity type the game
loaded, its components, and each weapon's ranges, auto-fire flags and target-class modifiers:

```
C_HAC_Upgrade01_MP
    UnitAttributesData: C_HAC_Upgrade01_UnitAttributesAsset_MP
    WeaponAttributesData: C_HAC_Upgrade01_Weapon_G2A_MP
        auto-acquire: yes   auto-fire: yes   damage: 250   cooldown: 700ms
        ranges: Short=1400 Medium=1400 Long=1400
        vs Air (Or): CanTarget 1
```

Generated from what your install actually loaded, so it stays correct across game updates and
DLC. [Finding names and values](docs/finding-names-and-values.md) explains how to read it, how to
work out which entity is which unit, and why campaign and multiplayer are separate entities.

The [2018 gist](https://gist.github.com/Majiir/1c78930ad70a16e1bd9a116948f55409) of entity names
is still around, but some of those names no longer exist. Prefer the dump.

### Multiplayer

All players must have the same version of Subsystem **and** byte-identical `patch.json`, or the
game will desync.

### Serialization notes

* `[Flags]` enums take one comma-separated string: `"Class": "Hover, Carrier"`
* Other enums are plain strings: `"HackableProperties": "InstantHackable"`
* `Fixed64` values are written as JSON numbers. Technically lossy, fine in practice.
