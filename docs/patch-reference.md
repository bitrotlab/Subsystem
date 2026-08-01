# patch.json reference

The complete set of things Subsystem can change, and the rules that govern the file.

Read [finding-names-and-values.md](finding-names-and-values.md) first if you do not yet know
which entity and component names to use.

## Rules that will bite you

### An unknown key discards the entire patch

Subsystem deserializes with the LitJson bundled in `BBI.Core.dll`. Its
`JsonReader.ignore_extra_keys` field is never assigned, so it defaults to `false`, and
`JsonMapper.ReadValue` throws on the first property it does not recognise:

```
The type {0} doesn't have the property '{1}'
```

`AttributeLoader` catches that and writes a single line to the game's `output_log.txt`:

```
[SUBSYSTEM] Error applying patch file: ...
```

**No `Subsystem.log` is written and nothing at all is patched** — not just the misspelled entry,
the whole file. One typo anywhere silently disables everything.

This is what `SubsystemLint` is for. Run it before you launch.

Names behave differently: an entity or component name that does not exist skips only that entry
and is reported in `Subsystem.log`.

### Lists are keyed by index, not by name

Five properties are lists, and their keys must be consecutive integers as strings:

- `ExperienceAttributesPatch.Levels`
- `ExperienceLevelAttributesPatch.Buff`
- `WeaponAttributesPatch.Modifiers`
- `WeaponAttributesPatch.EntityTypesToSpawnOnImpact`
- `EntityTypeBuffPatch.Buffs`

```json
"Modifiers": {
  "0": { "TargetClass": "Air", "ClassOperator": "Or", "Modifier": "CanTarget", "Amount": 1 }
}
```

Entries are processed in **numeric** index order. (Before this was fixed they were processed in
string order, which puts `"10"` between `"1"` and `"2"` — so any list longer than ten entries
tripped the non-consecutive check at `"10"` and silently discarded everything from there on.)

Rules enforced by `applyListPatch`:

- an index **below** the current count **overwrites** that entry
- an index **equal to** the current count **appends**
- an index **above** the current count is an error, and processing of that list stops
- a non-integer key is an error, and processing of that list stops
- `"Remove": true` deletes the entry

So to add a modifier without destroying an existing one, you must know how many there already
are. `Subsystem.entities.log` prints them in index order.

Every other dictionary is keyed by name: `Entities`, `AbilityAttributes`, `StorageAttributes`,
`WeaponAttributes`, `HangarBays`, and `InventoryLoadout` (keyed by inventory ID).

### Some changes only apply to newly spawned units

Unit-level values — health, armour, speed, pop cost, sensor radius — are copied into a unit's
simulation state when it spawns. Patching them does not retroactively change units that already
exist in a saved game or on the field.

Weapon behaviour resolves through the weapon binding on every shot, so weapon changes take effect
on existing units immediately.

**Build a new unit, or start a new match, before concluding a change did not work.**

### The unit card includes buffs

The HUD stat card shows the unit's live simulation state, which includes veterancy and upgrade
buffs. A unit with a base `MaxSpeed` of 50 can display `SPEED 65`. A freshly built unit shows the
base value; a promoted one shows more. `Subsystem.log` always reports the underlying attribute.

### Multiplayer

Every player must have the same Subsystem version **and** byte-identical `patch.json`, or the
game will desync.

## Value syntax

| Type in the tables below | JSON |
| --- | --- |
| `int?` | number, whole — `2`, not `2.5` |
| `double?`, `float?` | number — `0.4`, `18.0` |
| `bool?` | `true` / `false` |
| `string` | string |
| `string[]` | array of strings |
| enum | string naming the member — `"Linear"` |
| `[Flags]` enum | one string, comma-separated — `"Hover, Carrier"` |

Values the game stores as `Fixed64` are written as JSON numbers. The conversion is technically
lossy but is fine in practice.

## Structure

```
Entities
└── <entity name>                       EntityTypePatch
    ├── ExperienceAttributes            ExperienceAttributesPatch
    ├── UnitAttributes                  UnitAttributesPatch
    ├── ResearchItemAttributes          ResearchItemAttributesPatch
    ├── UnitHangarAttributes            UnitHangarAttributesPatch
    ├── DetectableAttributes            DetectableAttributesPatch
    ├── UnitMovementAttributes          UnitMovementAttributesPatch
    ├── AbilityAttributes  ── <name> ── AbilityAttributesPatch
    ├── StorageAttributes  ── <name> ── StorageAttributesPatch
    └── WeaponAttributes   ── <name> ── WeaponAttributesPatch

Commanders
└── <commander ID>                      CommanderPatch
    └── EntityTypeBuffs
        └── <entity name or prefix>     EntityTypeBuffPatch
            ├── UseAsPrefix             bool
            ├── UnitClass               UnitClass
            ├── ClassOperator           FlagOperator
            └── Buffs ── <index> ────── AttributeBuffPatch
```

## Commanders — changing stats for one player only

Everything under `Entities` patches the shared entity type templates, so it applies to *every*
player who fields that unit. `Commanders` is the other option: it gives one commander's units
different numbers and leaves everyone else's alone.

```json
{
  "Commanders": {
    "1": {
      "EntityTypeBuffs": {
        "C_Escort_MP": {
          "Buffs": {
            "0": { "Attribute": "Unit_MaxHealth", "Mode": "Set", "Value": 5000 },
            "1": { "Attribute": "UnitDynamics_MaxSpeed", "Mode": "AddPercent", "Value": 25 }
          }
        }
      }
    }
  }
}
```

This is the same mechanism a research upgrade uses. The engine keeps a per-commander copy of each
entity type and applies *attribute buffs* to it; units resolve their attributes through that copy
when they spawn. Subsystem applies your buffs from a second hook, once the simulation exists.

### Which commander am I?

Commander IDs are numbers, and `Subsystem.log` prints the ones you need at the top of its
`Commander buffs` section every match:

```
Commander buffs

  local commander: 1, CPU commander: 2
```

Key on the number, not on "me". A patch that said "whoever is playing" would apply to a different
commander on every machine and desync a multiplayer game immediately.

### Matching more than one entity type

| Property | Effect |
| --- | --- |
| `UseAsPrefix` | Match every entity type whose name *starts with* the key, instead of the one named by it |
| `UnitClass` | Only types whose `UnitAttributes.Class` matches. Omitted means no class filter |
| `ClassOperator` | `Or` (shares any flag, the default) or `And` (has all of them) |

```json
"C_": { "UseAsPrefix": true, "UnitClass": "Air", "Buffs": { "0": { "Attribute": "Unit_Armour", "Mode": "Add", "Value": 2 } } }
```

Only entity types that have `UnitAttributes` can be buffed; the engine skips everything else, and
`Subsystem.log` says so when nothing matched.

### Buffs

`Buffs` is an **indexed list** — keys `"0"`, `"1"`, … — of the same shape used by
`ExperienceAttributes.Levels[].Buff`:

| Property | Type | Meaning |
| --- | --- | --- |
| `Attribute` | `Buff.CategoryAndID` | Which attribute, e.g. `Unit_MaxHealth` |
| `Mode` | `AttributeBuffMode` | `Add`, `AddPercent`, `Set`, `SetAndHold` |
| `Value` | `int` | Whole number. `AddPercent` takes `25` for +25% |
| `Name` | `string` | Which component, for the categories that have several. Omit to hit them all |

`Value` is always an `int`, including for attributes that are `double` under `Entities`. To halve
a multiplier, use `AddPercent` with `-50` rather than a fractional `Set`.

### The attributes you can buff

This is the whole list — it is fixed by the engine, and it is much smaller than what `Entities`
can reach. Anything not here (enum values, strings, projectile types, weapon `Modifiers`, turret
settings) can only be changed globally, under `Entities`.

| Category | `Attribute` values |
| --- | --- |
| Unit | `Unit_MaxHealth`, `Unit_Armour`, `Unit_Resource1Cost`, `Unit_Resource2Cost`, `Unit_PopCapCost`, `Unit_ProductionTime`, `Unit_SensorRadius`, `Unit_ContactRadius`, `Unit_NumProductionQueues`, `Unit_AggroRange`, `Unit_LeashRange`, `Unit_AlertRange`, `Unit_FireRateDisplay`, `Unit_NonAutoTargetable`, `Unit_DamageReceivedMultiplier`, `Unit_AccuracyReceivedMultiplier` |
| UnitDynamics | `UnitDynamics_MaxSpeed`, `UnitDynamics_AccelerationTime`, `UnitDynamics_BrakingTime`, `UnitDynamics_MinCruiseSpeed`, `UnitDynamics_MaxSpeedTurnRadius` |
| UnitWeapon | `UnitWeapon_BaseDamagePerRound`, `UnitWeapon_RateOfFire`, `UnitWeapon_CooldownTime`, `UnitWeapon_ReloadTime`, `UnitWeapon_AreaOfEffect`, `UnitWeapon_ExcludeFromAutoTargetAcquisition`, `UnitWeapon_ExcludeFromAutoFire`, `UnitWeapon_ActiveStatusEffectsIndex` |
| WeaponRange | `WeaponRange_DistanceShort`, `WeaponRange_DistanceMedium`, `WeaponRange_DistanceLong`, `WeaponRange_AccuracyShort`, `WeaponRange_AccuracyMedium`, `WeaponRange_AccuracyLong` |
| Ability | `Ability_CooldownTimeSecs`, `Ability_WarmupTimeSecs`, `Ability_CostR1`, `Ability_CostR2` |
| Inventory | `Inventory_Capacity`, `Inventory_StartingAmount` |
| Harvester | `Harvester_SalvageDistance`, `Harvester_CycleTime`, `Harvester_ResourcesLoadedPerCycle`, `Harvester_ResourcesExtractedPerCycle` |
| HangarBay | `HangarBay_MinDockCoolingSeconds`, `HangarBay_MaxDamageCoolingSeconds`, `HangarBay_MaxPayloadCoolingSeconds` |
| PowerShunt | `PowerShunt_PowerLevelChargeTimeSeconds`, `PowerShunt_HeatThreshold` |
| PowerSystem | `PowerSystem_StartingPowerLevelIndex`, `PowerSystem_StartingMaxPowerLevelIndex` |
| UnitCombatBehaviour | `UnitCombatBehaviour_MinDesiredCombatRange`, `UnitCombatBehaviour_MaxDesiredCombatRange` |

`Buff.CategoryAndID` also defines `Commander_PopulationCap`, `Commander_DockedReloadModifier`,
`Commander_DockedRepairModifier` and `Commander_IgnoreMinimumDockTime`. Those attach to the
commander rather than to a unit type, through engine API that is internal to `BBI.Game` and not
reachable from the mod assembly. Subsystem **rejects them with an error in `Subsystem.log`**
rather than accepting them and quietly doing nothing.

### What `Name` means, per category

Only some categories have more than one component to choose between. An omitted or empty `Name`
always means "every component in this category".

| Category | `Name` is |
| --- | --- |
| `UnitWeapon_*`, `WeaponRange_*` | the **weapon ID from the unit's loadout** — see below |
| `Ability_*` | the ability name, as printed in `Subsystem.entities.log` |
| `Inventory_*` | the inventory ID, the same key `StorageAttributes.InventoryLoadout` uses |
| `HangarBay_*` | the hangar bay name |
| everything else | ignored — the unit has only one of these |

**Weapons are the trap.** The `Entities` section keys weapons by the weapon component's name
(`C_Escort_Weapon_G2G_MP`); weapon *buffs* key on `WeaponBinding.WeaponID`, which is a different
string. Getting it wrong is silent: the buff is simply never matched. `Subsystem.entities.log`
prints the right one under each weapon:

```
    WeaponAttributesData: C_Escort_Weapon_G2G_MP
        weapon ID (for commander buffs): <-- use this one
```

If the unit has only one weapon, leave `Name` out and the question does not arise.

### Loading a save

Buffs are saved with the game and restored when you load, which is the main reason to use this
rather than patching the per-commander copies directly — those are rebuilt from scratch on load
and any direct edit to them would be lost.

Because loading a save re-runs Subsystem's hook on top of buffs the game has already restored,
a buff that is already present — same attribute, mode and value — is not applied a second time.
Without that, `Add` and `AddPercent` would compound every time you loaded. One side effect: two
identical entries in the same `Buffs` list collapse into one. Write `Add 200` rather than `Add
100` twice.

### Multiplayer

The usual rule still holds — every player needs byte-identical `patch.json` — and it is enough,
because every client applies the same buffs to the same commander IDs. What you must not do is
key on the local player; that is why there is no "me" and no name-based lookup here.

Giving one commander better units is, of course, an unfair game. This is aimed at single-player
and at skirmishes against the AI.

### Proving it is really per-commander

Buffing your own faction is not a test. In the campaign you are Coalition (`C_`) and the enemy is
Gaalsien (`G_`), so a `C_*` buff looks player-only whether or not the scoping works — and campaign
entity names carry no `_MP` suffix, so `C_HAC`, not `C_HAC_MP`.

**The control that costs nothing: change the commander key to an ID that is not in the match.**
Take a buff you have watched apply, re-key it from `"1"` to `"7"`, and start the mission again.
The buff must now do nothing — if it still applies, it was never commander-scoped. Two runs, no
risk to the game either way, and it distinguishes real scoping from a global patch outright.

The louder control is to buff the *enemy's* units under **your** commander ID:

```json
"G_": { "UseAsPrefix": true, "Buffs": { "0": { "Attribute": "Unit_MaxHealth", "Mode": "Set", "Value": 99999 } } }
```

You never own Gaalsien units, so if the scoping holds this does nothing at all. Be aware of what
you are betting: the failure mode is a mission full of unkillable enemies. Use a *nerf* rather
than a buff — `Set 100` instead of `Set 99999` — if you would rather a leak made the mission easy
than unplayable.

(One exception, if you go looking for it: capturing an enemy unit re-resolves its attributes for
its new owner — `UnitManager.TransferUnitToCommander` goes through
`GetCommanderSpecificEntityType` — so a captured unit does pick up its captor's buffs.)

In a skirmish you have the easier option of a mirror matchup: same faction on both sides, buff the
unit for one commander ID and watch the other side's identical units behave normally.

## UnitAttributes

| Property | Type | Property | Type |
| --- | --- | --- | --- |
| `Class` | `UnitClass` | `SelectionFlags` | `UnitSelectionFlags` |
| `MaxHealth` | `int` | `Armour` | `int` |
| `DamageReceivedMultiplier` | `double` | `AccuracyReceivedMultiplier` | `double` |
| `PopCapCost` | `int` | `ExperienceValue` | `int` |
| `ProductionTime` | `double` | `Resource1Cost` | `int` |
| `Resource2Cost` | `int` | `LeadPriority` | `int` |
| `AggroRange` | `double` | `LeashRange` | `double` |
| `AlertRange` | `double` | `RepairPickupRange` | `double` |
| `SensorRadius` | `double` | `ContactRadius` | `double` |
| `UnitPositionReaggroConditions` | enum | `LeashPositionReaggroConditions` | enum |
| `Selectable` | `bool` | `Controllable` | `bool` |
| `Targetable` | `bool` | `NonAutoTargetable` | `bool` |
| `RetireTargetable` | `bool` | `HackedReturnTargetable` | `bool` |
| `HackableProperties` | enum | `ExcludeFromUnitStats` | `bool` |
| `BlocksLOF` | `bool` | `WorldHeightOffset` | `double` |
| `DoNotPersist` | `bool` | `LevelBound` | `bool` |
| `StartsInHangar` | `bool` | `PriorityAsTarget` | `double` |
| `NumProductionQueues` | `int` | `ProductionQueueDepth` | `int` |
| `ShowProductionQueues` | `bool` | `NoTextNotifications` | `bool` |
| `NotificationFlags` | enum | `FireRateDisplay` | `int` |
| `BaseThreat` | `int` | `ThreatTier` | `int` |
| `ThreatCounters` | `string[]` | `ThreatCounteredBys` | `string[]` |

`AggroRange` is what makes a unit engage on its own. Leaving it alone while raising
`SensorRadius` gives you vision without the unit chasing things you did not order it to attack.

## UnitMovementAttributes

`DriveType` (`UnitDriveType`), and `Dynamics`:

| Property | Type | Property | Type |
| --- | --- | --- | --- |
| `DriveType` | `UnitDriveType` | `MaxSpeed` | `double` |
| `MinCruiseSpeed` | `double` | `ReverseFactor` | `double` |
| `AccelerationTime` | `double` | `BrakingTime` | `double` |
| `MaxSpeedTurnRadius` | `double` | `MaxEaseIntoTurnTime` | `double` |
| `Length` | `double` | `Width` | `double` |
| `DriftType` | `double` | `ReverseDriftMultiplier` | `double` |
| `DriftOvershootFactor` | `double` | `FishTailingTimeIntervals` | `double` |
| `FishTailControlRecover` | `double` | `MinDriftSlipSpeed` | `double` |
| `MaxDriftRecoverTime` | `double` | `DeathDriftTime` | `double` |
| `PermanentlyImmobile` | `bool` | | |

## WeaponAttributes

Keyed by weapon name.

| Property | Type | Property | Type |
| --- | --- | --- | --- |
| `BaseDamagePerRound` | `double` | `BaseWreckDamagePerRound` | `double` |
| `DamageType` | `DamageType` | `DamagePacketsPerShot` | `int` |
| `RateOfFire` | `int` | `NumberOfBursts` | `int` |
| `WindUpTimeMS` | `int` | `WindDownTimeMS` | `int` |
| `BurstPeriodMinTimeMS` | `int` | `BurstPeriodMaxTimeMS` | `int` |
| `CooldownTimeMS` | `int` | `ReloadTimeMS` | `int` |
| `FiringRecoil` | `float` | `LineOfSightRequired` | `bool` |
| `LeadsTarget` | `TargetAimingType` | `TargetStyle` | `WeaponTargetStyle` |
| `ExcludeFromAutoTargetAcquisition` | `bool` | `ExcludeFromAutoFire` | `bool` |
| `ExcludeFromHeightAdvantage` | `bool` | `KillSkipsUnitDeathSequence` | `bool` |
| `AreaOfEffectFalloffType` | `AOEFalloffType` | `AreaOfEffectRadius` | `double` |
| `ExcludeWeaponOwnerFromAreaOfEffect` | `bool` | `FriendlyFireDamageScalar` | `double` |
| `WeaponOwnerFriendlyFireDamageScalar` | `double` | `IsTracer` | `bool` |
| `TracerSpeed` | `double` | `TracerLength` | `double` |
| `RevealTriggers` | enum | `UnitStatusAttackingTriggers` | enum |
| `ProjectileEntityTypeToSpawn` | `string` | `ActiveStatusEffectsIndex` | `int` |
| `StatusEffectsTargetAlignment` | enum | `StatusEffectsExcludeTargetType` | `UnitClass` |

Nested:

- `RangeAttributesShort` / `RangeAttributesMedium` / `RangeAttributesLong` —
  `Accuracy`, `Distance`, `MinDistance` (all `double`), plus `Remove`
- `Turret` — `FieldOfView`, `FieldOfFire`, `RotationSpeed` (all `double`)
- `Modifiers` — **indexed list**, see below
- `EntityTypesToSpawnOnImpact` — **indexed list** of `EntityTypeToSpawn`, `SpawnRotationOffsetDegrees`
- `TargetPrioritizationAttributes` — `WeaponEffectivenessWeight`, `TargetThreatWeight`,
  `DistanceWeight`, `AngleWeight`, `TargetPriorityWeight`, `AutoTargetStickyBias`,
  `TargetSameCommanderBias`, `TargetWithinFOVBias` (all `double`)

### Modifiers — what a weapon can shoot

Each entry is `TargetClass` (`UnitClass`), `ClassOperator` (`FlagOperator`), `Modifier`
(`WeaponModifierType`) and `Amount` (`int`). This is where ground-versus-air gating lives, and
where per-class damage scaling is configured.

`WeaponModifierType`: `CanTarget`, `CanNotTarget`, `DamagePercent`, `DamageValue`,
`AccuracyShortPercent`, `AccuracyShortValue`, `AccuracyMediumPercent`, `AccuracyMediumValue`,
`AccuracyLongPercent`, `AccuracyLongValue`

Weapon names encode their role by convention: `_G2G_` ground-to-ground, `_G2A_` ground-to-air,
`_G2GA_` both. A weapon with `ExcludeFromAutoFire` set is only fired by an ability — grenade and
smoke launchers work this way, which is why a unit carrying one can still look like it never
shoots.

## Other components

**AbilityAttributes** (keyed by name) — `AbilityType`, `TargetingType`, `TargetAlignment`,
`AbilityMapTargetLayers`, `GroundAutoTargetAlignment`, `EdgeOfTargetShapeMinDistance`,
`CasterMovesToTarget`, `GroupActivationType`, `StartsRemovedInGameMode`, `CooldownTimeSecs`,
`WarmupTimeSecs`, `SharedCooldownChannel`, `SkipCastOnArrivalConditions`, `IsToggleable`,
`CastOnDeath`, `Resource1Cost`, `Resource2Cost`

**ResearchItemAttributes** — `TypeOfResearch` (`ResearchType`), `IconSpriteName`,
`LocalizedResearchTitleStringID`, `LocalizedShortDescriptionStringID`,
`LocalizedLongDescriptionStringID`, `ResearchTime`, `Dependencies` (`string[]`), `ResearchVOCode`,
`Resource1Cost`, `Resource2Cost`

**DetectableAttributes** — `DisplayLastKnownLocation`, `LastKnownDuration`,
`TimeVisibleAfterFiring`, `AlwaysVisible`, `MinimumStateAfterDetection` (`DetectionState`),
`FOWFadeDuration`, `SetHasBeenSeenBeforeOnSpawn`

**ExperienceAttributes** — `Levels`, an **indexed list** of `BuffTooltipLocID`,
`RequiredExperience`, and `Buff` (itself an indexed list of `Name`, `Attribute`, `Mode`, `Value`)

**StorageAttributes** (keyed by name) — `LinkToPlayerBank`, `IsResourceController`, and
`InventoryLoadout` keyed by inventory ID with `Capacity`, `HasUnlimitedCapacity`, `StartingAmount`.
A new inventory ID creates a new inventory.

**UnitHangarAttributes** — `AlignmentTime`, `ApproachTime`, and `HangarBays` keyed by bay name
with 24 docking and undocking properties.

## Enum values

| Enum | Values |
| --- | --- |
| `UnitClass` (flags) | `None`, `Air`, `Ground`, `Hover`, `Carrier`, `ScriptControlled`, `Small`, `Medium`, `Large`, `XLarge`, `Strikecraft`, `Armored`, `Ranged`, `Harvester`, `Support`, `All` |
| `WeaponModifierType` | `CanTarget`, `CanNotTarget`, `DamagePercent`, `DamageValue`, `AccuracyShortPercent`, `AccuracyShortValue`, `AccuracyMediumPercent`, `AccuracyMediumValue`, `AccuracyLongPercent`, `AccuracyLongValue` |
| `FlagOperator` | `Or`, `And` |
| `TargetAimingType` | `DirectAtTarget`, `LeadTarget` |
| `WeaponTargetStyle` | `RandomSpray`, `TurretImpactRun`, `Flak` |
| `AOEFalloffType` | `None`, `Linear`, `Quadratic` |
| `DamageType` | `Damage`, `Heal` |
| `UnitDriveType` | `Wheeled`, `Tracked`, `Hover`, `Air` |
| `DetectionState` | `Hidden`, `Contacted`, `Sensed` |
| `HackableProperties` | `NonHackable`, `Hackable`, `InstantHackable` |
| `ResearchType` | `Engineering`, `Upgrade` |

`SubsystemLint` validates enum values and lists the legal ones when you get it wrong, which is
usually faster than looking them up. For any enum not listed here, its definition is in
`Data/Managed/BBI.Game.Data.dll`.
