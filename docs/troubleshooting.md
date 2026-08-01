# Troubleshooting

Symptoms first, in roughly the order people hit them.

## The game breaks between the menu and a match

You have the 0.4.0 `BBI.Unity.Game.dll` installed. That file is the whole 2018 game assembly, and
dropping it on a current install rolls back years of game code. See
[compatibility.md](compatibility.md).

```sh
dotnet run --project SubsystemPatcher -c Release -- --verify
```

If it reports `matches this install : NO — N dangling references`, restore the real file:

- `dotnet run --project SubsystemPatcher -c Release -- --restore`, or
- Steam → right-click the game → Properties → Installed Files → Verify integrity

Then install properly, per the [README](../README.md#installation).

## Nothing happens at all, and there is no Subsystem.log

Check `Data/output_log.txt`:

| What you see | Meaning |
| --- | --- |
| no `Platform assembly: ...Subsystem.dll` line | `Subsystem.dll` is not in `Data/Managed/` |
| that line, but no `[SUBSYSTEM]` line | the hooks are not in `BBI.Unity.Game.dll` — run the patcher |
| `[SUBSYSTEM] Error applying patch file: ...` | `patch.json` failed to parse — see below |
| `[SUBSYSTEM] Applied attributes patch.` | it worked; read `Data/Subsystem.log` |

Remember that **a game update replaces `BBI.Unity.Game.dll` and removes the hook.** The symptom
is the mod silently doing nothing. Re-run the patcher.

## "Error applying patch file" — one typo killed everything

An unknown property makes LitJson throw, which discards the **entire** patch, not just the bad
entry. See [the rule](patch-reference.md#an-unknown-key-discards-the-entire-patch).

```sh
dotnet run --project SubsystemLint -c Release
```

It will point at the offending key and usually suggest the correct spelling.

## Subsystem.log says the entity or component was not found

```
NOTICE: EntityType not found
ERROR: WeaponAttributes not found
```

The name does not exist in your install. Common causes:

- **Campaign versus multiplayer.** Most units exist twice — `C_HAC` and `C_HAC_MP` — and their
  components carry matching suffixes, but not always the same one: the campaign G2A weapon is
  `..._Weapon_G2A_Campaign`, not unsuffixed.
- **Names changed since 2018.** `Tier_3_C_Artillery` from the old README no longer exists.
- **The component name is not the entity name.** `C_HAC_Upgrade01_MP` carries a weapon called
  `C_HAC_Upgrade01_Weapon_G2A_MP`.

`Data/Subsystem.entities.log` lists every valid name; `SubsystemLint` checks your file against it.

## Subsystem.log shows the change, but the game does not

Almost always one of these three.

**The unit already existed.** Health, armour, speed, pop cost and sensor radius are copied into a
unit's simulation state when it spawns. Existing units — including everything in a saved game —
keep the old values. Build a new one, or start a new match.

**You are looking at a buffed unit.** The stat card shows live state including veterancy and
upgrades, so a unit with a base `MaxSpeed` of 50 can read `SPEED 65`. A freshly built unit shows
the base value.

**You patched the variant you are not playing.** Patching the `_MP` entity does nothing in
campaign, and vice versa. Patching both in one file is harmless and avoids the question entirely.

## A unit will not shoot

Before assuming the mod broke it, check what the unit can actually shoot. In
`Data/Subsystem.entities.log`:

```
    WeaponAttributesData: C_HAC_Upgrade01_Weapon_G2A_MP
        auto-acquire: yes   auto-fire: yes   damage: 250   cooldown: 700ms
        ranges: Short=1400 Medium=1400 Long=1400
        vs Air (Or): CanTarget 1
```

- **`_G2A_` means anti-air only.** It will never engage ground targets. `_G2G_` is ground,
  `_G2GA_` is both. This is vanilla behaviour, not a modding failure.
- **`auto-fire: no` means you have to fire it.** Grenade and smoke launchers are abilities. The
  unit will sit there looking idle until you trigger them.
- **`ranges` is the actual reach.** Raising `SensorRadius` and `AggroRange` well beyond the
  weapon's range makes a unit acquire targets it cannot shoot, which looks like it is refusing to
  fire. Either raise the range bands to match or leave the aggro range alone.
- **`vs <class>` lines gate targeting.** `CanNotTarget` against a class means exactly that.

To confirm a weapon is not the problem, patch the unit with **no** `WeaponAttributes` block at
all and see whether the behaviour changes. If it does not, the weapon data was never involved.

## A commander buff did nothing

`Subsystem.log` gets a `Commander buffs` section at the end of every match start. Read it first —
it names the commanders that exist and reports every entity type it touched.

| What the log says | Meaning |
| --- | --- |
| no `Commander buffs` section at all | there is no `Commanders` key in `patch.json`, or the second hook is missing — run `SubsystemPatcher --verify` |
| `ERROR: ... EntityTypeBuffExtensions not found` | a game update moved the engine API this depends on; commander buffs are unavailable until it is re-identified |
| `NOTICE: matched no entity type with UnitAttributes` | the entity name (or prefix, or `UnitClass` filter) matched nothing that can be buffed |
| `already applied` | the buff was restored from a save; this is the deduplication working, not a failure |
| `applied N of M buff(s)` | it worked |

If the log says it applied and the stat still looks unchanged, the usual suspects from the
section above apply — most often that the unit already existed.

**A weapon buff that reports as applied but changes nothing** is almost always the wrong `Name`.
Weapon buffs key on the loadout's weapon ID, not on the weapon component name that the `Entities`
section uses. `Subsystem.entities.log` prints the right one as
`weapon ID (for commander buffs)`. Omitting `Name` buffs every weapon on the unit and sidesteps it.

**Buffing the wrong commander** produces no error, because any commander ID is a legal key. Check
the `local commander:` line in the log and key on that number.

## A list entry did not apply

`Modifiers`, `EntityTypesToSpawnOnImpact`, `Levels`, `Buff` and `Buffs` are keyed by **list
index**, not by name. A key that is not an integer, or that is greater than the current number of entries, stops
processing of that list. See
[the rule](patch-reference.md#lists-are-keyed-by-index-not-by-name).

## Multiplayer desync

Every player needs the same Subsystem version and byte-identical `patch.json`. There is no
partial compatibility.

## Unrelated errors you can ignore

These appear in `output_log.txt` on a healthy install and have nothing to do with Subsystem:

```
NullReferenceException
  at PlayEveryWare.EpicOnlineServices.EOSManager.GetEOSP2PInterface ()
  at GG.EpicGames.EpicP2PIntegration.IsP2PPacketAvailable (...)
```

Epic Online Services when you are offline or not signed in. Likewise `Error: Online: [SPARK]
Service encountered an error!`, `NameResolutionFailure`, missing shader fallbacks, and the game's
own `Tried to add invalid ability type name ...` messages.

## Starting over

```sh
dotnet run --project SubsystemPatcher -c Release -- --restore
```

Restores the original `BBI.Unity.Game.dll` byte-for-byte from the backup the patcher made. Delete
`Subsystem.dll`, `patch.json`, `Subsystem.log`, `Subsystem.entities.log` and
`BBI.Unity.Game.dll.subsystem-backup` for a fully clean install. Verifying the game files in
Steam also works if the backup is gone.
