# Finding entity names, component names and values

Every key in `patch.json` is a name from the game's data. This document explains how to discover
those names and their current values for **your** installed version, rather than trusting a list
someone published years ago.

## The three files that tell you everything

All three live in `Deserts of Kharak/Data/`.

| File | Written when | Tells you |
| --- | --- | --- |
| `Subsystem.entities.log` | every game start | every entity type, its components, and each weapon's targeting data |
| `Subsystem.log` | every game start, if the patch parsed | what your patch changed, and the **previous value** of each field |
| `output_log.txt` | continuously, by the game | whether Subsystem loaded and whether the patch threw |

## Subsystem.entities.log — the name reference

Written on every game start, before the patch is read, so it is produced even when `patch.json`
is missing or broken.

```
C_HAC_Upgrade01_MP
    AbilityAttributesData: Ability_C_Grenade_MP
    DetectableAttributesData
    UnitAttributesData: C_HAC_Upgrade01_UnitAttributesAsset_MP
    UnitMovementAttributesData
    WeaponAttributesData: C_HAC_Upgrade01_Weapon_G2A_MP
        weapon ID (for commander buffs): <the loadout's ID for this weapon>
        auto-acquire: yes   auto-fire: yes   damage: 250   cooldown: 700ms
        DamageType: Missile   TargetStyle: TurretImpactRun
        ranges: Short=1400 Medium=1400 Long=1400
        vs Air (Or): CanTarget 1
```

How to read it:

- **Column 0** is an entity type name — a key under `Entities` in `patch.json`.
- **Four-space indent** is a component. `Type: Name` means the component is addressed *by that
  name* (weapons, abilities, storages). A bare `Type` means it is addressed by type alone.
- **Eight-space indent** is per-weapon detail, described below.

Note that the component name is not always the entity name: above, the entity is
`C_HAC_Upgrade01_MP` but its weapon is `C_HAC_Upgrade01_Weapon_G2A_MP`.

### The weapon detail lines

- `weapon ID (for commander buffs)` — the ID this weapon has in the unit's loadout. It is *not*
  necessarily the component name on the line above. The `Entities` section keys weapons by the
  component name; buffs under `Commanders` key on this ID instead, and a wrong one is matched
  silently against nothing. The line is only printed for weapons that are actually bound to a
  unit's loadout.
- `auto-acquire` / `auto-fire` — whether the unit uses this weapon on its own. A weapon with
  `auto-fire: no` only fires when you trigger its ability. That is how grenade and smoke
  launchers work, and it is why a unit can look like it "refuses to shoot".
- `ranges` — the distance bands. This is the weapon's actual reach.
- `vs <class> (<op>): <modifier> <amount>` — one line per entry in `Modifiers`, **in index
  order**. This is where ground-versus-air gating lives: `CanTarget` / `CanNotTarget` against a
  `UnitClass`. The index order matters when patching (see
  [patch-reference.md](patch-reference.md#lists-are-keyed-by-index-not-by-name)).

## Subsystem.log — current values, for free

You do not need to look up a field's current value anywhere. Patch it to anything and the log
tells you what it was:

```
EntityType: C_HAC_Upgrade01_MP

  UnitAttributes:

    MaxHealth: 6000 (was: 1800)
    Armour: 20 (was: 5)

  WeaponAttributes: C_HAC_Upgrade01_Weapon_G2A_MP

    BaseDamagePerRound: 900 (was: 250)
```

This is the fastest way to learn the game's native scale for a field before deciding on a value.

It also reports what it could **not** find:

| Line | Meaning |
| --- | --- |
| `NOTICE: EntityType not found` | the `Entities` key does not exist in this install |
| `ERROR: <Type> not found` | the entity exists but has no such component, or no component by that name |
| `(created)` | the element did not exist and was added |
| `(removed)` | the element was removed |

If `Subsystem.log` does not appear at all, the patch never ran — see
[troubleshooting.md](troubleshooting.md).

## SubsystemLint — check before you launch

```sh
dotnet run --project SubsystemLint -c Release
```

With no arguments it checks `Data/patch.json` of the detected install, using
`Data/Subsystem.entities.log` for name checking when it exists. It validates:

- property names against the patch classes, with a spelling suggestion
- value types (a string where a number belongs, a decimal in an integer field)
- enum values, including `[Flags]` combinations like `"Hover, Carrier"`
- list keys that must be integers
- entity and component names against your install, with a spelling suggestion

Exit codes: `0` clean, `2` problems found, `1` could not run.

This matters more than it looks. An unknown property does not just get ignored — it makes the
whole patch throw and apply nothing. See
[patch-reference.md](patch-reference.md#an-unknown-key-discards-the-entire-patch).

## Identifying which entity is which unit

Entity names are internal codenames and do not always match the in-game name. `C_HAC` is the
Armored Assault Vehicle; `C_HAC_Upgrade01` is the Missile Battery.

Three ways to map a unit to its entity, in increasing order of effort:

1. **Patch a stat and look at the unit card.** Set `MaxHealth` to something unmistakable, start a
   match, build the unit. `Subsystem.log` confirms which entity you actually hit.
2. **Read the component names.** They are usually descriptive: a unit with
   `WeaponAttributesData: ..._Weapon_G2A_...` is anti-air, `..._G2G_...` is ground,
   `..._G2GA_...` is both.
3. **Search the localization strings.** The game's UI text is in `Data/sharedassets0.assets` as
   CSV rows. Searching it for a unit's in-game name often reveals the codename — the tutorial
   objective "Produce Three Armored Assault Units (`{hacsProduced}`/3)" is what confirms that
   "Armored Assault" means `HAC`.

   ```sh
   grep -aoE "[ -~]{0,60}Armored Assault[ -~]{0,60}" \
     "<...>/Deserts of Kharak/Data/sharedassets0.assets" | sort -u
   ```

## Campaign and multiplayer are different entities

Most units exist twice, and **they do not share stats**:

| Campaign | Multiplayer / skirmish |
| --- | --- |
| `C_HAC` | `C_HAC_MP` |
| `C_HAC_Upgrade01` | `C_HAC_Upgrade01_MP` |

Their components are suffixed to match, and not always with the same suffix:

| Campaign component | Multiplayer component |
| --- | --- |
| `C_HAC_Upgrade01_Weapon_G2A_Campaign` | `C_HAC_Upgrade01_Weapon_G2A_MP` |
| `C_HAC_Upgrade01_Weapon_Ballistic_Grenade` | `C_HAC_Upgrade01_Weapon_Ballistic_Grenade_MP` |

Note the campaign G2A weapon is `_Campaign`, not unsuffixed. Do not assume the pattern — read the
dump.

The two variants can also differ in **content**, not just numbers. The multiplayer AAV
(`C_HAC_MP`) carries a `C_HAC_Weapon_G2GA_MP` that engages ground and air; the campaign AAV
(`C_HAC`) has no such weapon and cannot shoot air at all.

Patching both variants in one file is harmless — an entity you are not currently playing with is
simply patched and never used. `SubsystemLint` will catch it if you put multiplayer component
names under a campaign entity.

## Reading the game's own assemblies

For questions the logs cannot answer — what an enum's legal values are, how a field is consumed
by the simulation — decompile the assemblies in `Data/Managed/`. Any IL tool works;
[Mono.Cecil](https://www.nuget.org/packages/Mono.Cecil) is enough to enumerate types and dump
method bodies, and is already a dependency of the tooling here.

The assemblies worth knowing:

| Assembly | Contains |
| --- | --- |
| `BBI.Game.Data.dll` | the attribute interfaces and enums that `patch.json` maps onto |
| `BBI.Core.dll` | `EntityTypeCollection`, `EntityTypeAttributes`, `Fixed64`, and the bundled LitJson |
| `BBI.Game.dll` | the simulation — how weapons fire, how ranges and modifiers are consumed |
| `BBI.Unity.Game.dll` | presentation, the HUD, and the hook site |
