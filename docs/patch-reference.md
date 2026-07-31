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

Four properties are lists, and their keys must be consecutive integers as strings:

- `ExperienceAttributesPatch.Levels`
- `ExperienceLevelAttributesPatch.Buff`
- `WeaponAttributesPatch.Modifiers`
- `WeaponAttributesPatch.EntityTypesToSpawnOnImpact`

```json
"Modifiers": {
  "0": { "TargetClass": "Air", "ClassOperator": "Or", "Modifier": "CanTarget", "Amount": 1 }
}
```

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
```

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
