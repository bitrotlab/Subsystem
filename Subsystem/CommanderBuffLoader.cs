using BBI.Core.Data;
using BBI.Game.Data;
using BBI.Game.SaveLoad;
using BBI.Game.Simulation;
using LitJson;
using Subsystem.Patch;
using Subsystem.Wrappers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Subsystem
{
    /// <summary>
    /// Applies the "Commanders" section of patch.json: stat changes that affect one commander's
    /// units only, instead of every unit of that type on the map.
    ///
    /// The "Entities" section patches the shared entity type templates, so it necessarily hits
    /// every player. The engine keeps a *per-commander* copy of each buffable entity type
    /// (EntityTypeCollection.GetCommanderSpecificEntityType) and modifies it with attribute
    /// buffs — that is how a research upgrade gives one player better tanks and leaves the
    /// enemy's alone. This applies the same mechanism from patch.json.
    ///
    /// Two consequences of using the game's own buff system rather than patching the
    /// per-commander copy directly:
    ///
    ///  - buffs are saved with the game (SkirmishSaveState.CommanderTypeBuffs) and restored by
    ///    Sim.OnLoad, so they survive loading a save. Component patches would not: OnLoad calls
    ///    ResetUserSpecificEntityTypes and rebuilds the per-commander copies from scratch.
    ///  - only the attributes the engine can buff are reachable. That is Buff.CategoryAndID,
    ///    not the whole of patch.json.
    ///
    /// This runs from a second hook, after the Sim has been constructed: the per-commander
    /// copies do not exist until Sim's constructor calls MakeAllTypesBuffableForCommander, so
    /// none of this can be done from AttributeLoader's hook.
    /// </summary>
    public class CommanderBuffLoader
    {
        private const string ExtensionsTypeName = "BBI.Game.Simulation.EntityTypeBuffExtensions";

        // EntityTypeBuffExtensions is internal to BBI.Game. Re-implementing it here would mean
        // duplicating eleven component categories and keeping them in step with the game across
        // updates; calling the game's own code is shorter and correct by construction.
        private static MethodInfo sAddBuffsForCommander;
        private static MethodInfo sGetAllEntityTypeBuffs;

        private readonly StringWriter writer;
        private readonly StringLogger logger;
        private readonly AttributeLoader buffSetPatcher;

        // Snapshot of every entity type name, taken once: nothing adds types after the sim is up,
        // and there are ~900 of them to walk per entry otherwise.
        private List<string> entityTypeNames;

        public CommanderBuffLoader()
        {
            writer = new StringWriter();
            logger = new StringLogger(writer);
            buffSetPatcher = new AttributeLoader(logger);
        }

        public void ApplyCommanderBuffs(EntityTypeCollection entityTypeCollection)
        {
            try
            {
                var patch = readPatch();
                if (patch == null || patch.Commanders.Count == 0) { return; }

                if (!resolveEngineApi()) { flush(); return; }

                entityTypeNames = new List<string>();
                foreach (var name in entityTypeCollection.GetAllEntityTypeNames())
                {
                    entityTypeNames.Add(name);
                }
                entityTypeNames.Sort(StringComparer.Ordinal);

                using (logger.BeginScope("Commander buffs"))
                {
                    logCommanderRoster();

                    foreach (var kvp in patch.Commanders)
                    {
                        applyCommanderPatch(entityTypeCollection, kvp.Key, kvp.Value);
                    }
                }

                flush();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SUBSYSTEM] Error applying commander buffs: {e}");
            }
        }

        // patch.json is read again rather than handed over from AttributeLoader: the two hooks
        // are far apart in the game's start-up, and a null static between them would silently
        // apply nothing after any future reordering. Re-reading one small file at match start
        // costs nothing. A malformed file has already been reported by AttributeLoader.
        private static AttributesPatch readPatch()
        {
            var jsonPath = Path.Combine(Application.dataPath, "patch.json");
            if (!File.Exists(jsonPath)) { return null; }

            return JsonMapper.ToObject<AttributesPatch>(File.ReadAllText(jsonPath));
        }

        private bool resolveEngineApi()
        {
            if (sAddBuffsForCommander != null && sGetAllEntityTypeBuffs != null) { return true; }

            var type = typeof(CommanderID).Assembly.GetType(ExtensionsTypeName);
            if (type == null)
            {
                logger.Log($"ERROR: {ExtensionsTypeName} not found in BBI.Game — the game's code changed shape, commander buffs are unavailable");
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

            sAddBuffsForCommander = type.GetMethod("AddBuffsForCommander", flags, null,
                new[] { typeof(EntityTypeCollection), typeof(string), typeof(AttributeBuffSet), typeof(CommanderID), typeof(bool) }, null);

            sGetAllEntityTypeBuffs = type.GetMethod("GetAllEntityTypeBuffs", flags, null,
                new[] { typeof(EntityTypeAttributes) }, null);

            if (sAddBuffsForCommander == null || sGetAllEntityTypeBuffs == null)
            {
                logger.Log($"ERROR: {ExtensionsTypeName} no longer has the expected AddBuffsForCommander/GetAllEntityTypeBuffs — commander buffs are unavailable");
                sAddBuffsForCommander = null;
                sGetAllEntityTypeBuffs = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// The commander IDs in play, so that "which number am I?" is answered by the log instead
        /// of by guesswork. SimController caches the first two in PostLoadInit, which is the call
        /// this hook is injected after, so they are populated by now.
        /// </summary>
        private void logCommanderRoster()
        {
            logger.Log($"local commander: {SimController.LocalPlayerCommanderID.ID}");
            logger.Log($"first enemy CPU commander: {SimController.EnemyCPUCommander.ID}");

            // Sim.Instance and Sim.CommanderManager are not public. The full roster is a
            // diagnostic and nothing depends on it, so a failure here costs a log line and
            // no more.
            try
            {
                var simType = typeof(SimController).Assembly.GetType("BBI.Game.Simulation.Sim");
                var instanceField = simType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var managerProperty = simType.GetProperty("CommanderManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                var manager = managerProperty.GetValue(instanceField.GetValue(null), null);
                var commandersField = manager.GetType().GetField("mCommanders", BindingFlags.NonPublic | BindingFlags.Instance);
                var commanders = (IDictionary)commandersField.GetValue(manager);

                foreach (DictionaryEntry entry in commanders)
                {
                    var commander = (Commander)entry.Value;
                    logger.Log($"  commander {commander.ID.ID}: {commander.Name} (team {commander.TeamID.ID})");
                }
            }
            catch (Exception e)
            {
                logger.Log($"  (full commander roster unavailable: {e.Message})");
            }
        }

        private void applyCommanderPatch(EntityTypeCollection entityTypeCollection, string key, CommanderPatch commanderPatch)
        {
            using (logger.BeginScope($"Commander: {key}"))
            {
                int id;
                if (!int.TryParse(key, out id))
                {
                    logger.Log($"ERROR: '{key}' is not a commander ID (a whole number)");
                    return;
                }

                var commanderID = new CommanderID(id);

                foreach (var kvp in commanderPatch.EntityTypeBuffs)
                {
                    applyEntityTypeBuffPatch(entityTypeCollection, commanderID, kvp.Key, kvp.Value);
                }
            }
        }

        private void applyEntityTypeBuffPatch(EntityTypeCollection entityTypeCollection, CommanderID commanderID, string typeSpec, EntityTypeBuffPatch patch)
        {
            using (logger.BeginScope($"EntityType: {typeSpec}"))
            {
                var desired = new AttributeBuffSetWrapper();
                buffSetPatcher.ApplyAttributeBuffSetPatch(patch.Buffs, desired);

                // Commander_* buffs go on the Commander object, not on an entity type.
                // Sim.AddEntityTypeBuffs does that through API that is internal to BBI.Game and
                // out of reach here, so reject them rather than accept them and do nothing.
                for (var i = desired.Buffs.Count - 1; i >= 0; i--)
                {
                    if (desired.Buffs[i].Category != Buff.Category.Commander) { continue; }

                    logger.Log($"ERROR: Buffs.{i} is a Commander_* buff, which is not supported here — ignored");
                    desired.Buffs.RemoveAt(i);
                }

                if (desired.Buffs.Count == 0)
                {
                    logger.Log("NOTICE: no buffs to apply");
                    return;
                }

                var matched = 0;

                foreach (var entityTypeName in matchingEntityTypes(entityTypeCollection, typeSpec, patch))
                {
                    matched++;
                    applyToEntityType(entityTypeCollection, commanderID, entityTypeName, desired);
                }

                if (matched == 0)
                {
                    logger.Log("NOTICE: matched no entity type with UnitAttributes");
                }
            }
        }

        // Mirrors the matching Sim.AddEntityTypeBuffs does: name or name prefix, an optional
        // UnitClass filter, and only types that have UnitAttributes — the engine will not buff
        // anything else.
        private List<string> matchingEntityTypes(EntityTypeCollection entityTypeCollection, string typeSpec, EntityTypeBuffPatch patch)
        {
            var useAsPrefix = patch.UseAsPrefix ?? false;
            var unitClass = patch.UnitClass ?? UnitClass.None;
            var classOperator = patch.ClassOperator ?? FlagOperator.Or;

            var matches = new List<string>();

            foreach (var name in entityTypeNames)
            {
                if (useAsPrefix ? !name.StartsWith(typeSpec) : name != typeSpec) { continue; }

                var entityType = entityTypeCollection.GetEntityType(name);
                if (entityType == null) { continue; }

                var unitAttributes = entityType.Get<UnitAttributes>();
                if (unitAttributes == null) { continue; }

                if (unitClass != UnitClass.None && !classMatches(unitAttributes.Class, unitClass, classOperator)) { continue; }

                matches.Add(name);
            }

            return matches;
        }

        private static bool classMatches(UnitClass actual, UnitClass wanted, FlagOperator classOperator)
        {
            return classOperator == FlagOperator.And
                ? (actual & wanted) == wanted
                : (actual & wanted) != 0;
        }

        private void applyToEntityType(EntityTypeCollection entityTypeCollection, CommanderID commanderID, string entityTypeName, AttributeBuffSetWrapper desired)
        {
            var entityType = entityTypeCollection.GetCommanderSpecificEntityType(entityTypeName, commanderID.ID);
            if (entityType == null)
            {
                logger.Log($"{entityTypeName}: ERROR: no commander-specific entity type");
                return;
            }

            var existing = (IDBuffSaveState[])sGetAllEntityTypeBuffs.Invoke(null, new object[] { entityType });

            // Loading a save re-runs this hook, and Sim.OnLoad has already restored the buffs
            // that were saved. Adding them a second time would double an Add or AddPercent, so
            // only buffs that are not already on this type are applied. Two identical entries in
            // one Buffs list therefore collapse into one.
            var fresh = new AttributeBuffSetWrapper();
            foreach (var buff in desired.Buffs)
            {
                if (!alreadyApplied(existing, buff)) { fresh.Buffs.Add(buff); }
            }

            if (fresh.Buffs.Count == 0)
            {
                logger.Log($"{entityTypeName}: already applied");
                return;
            }

            // Not silent. The BuffChangedEvent is what makes units that already exist pick the
            // change up: Unit.OnBuffChangedEvent rescales Health against the new MaxHealth and
            // calls CheckRebindWeapons. Campaign missions restore the carried-over fleet during
            // SimController.Initialize, which is before this hook runs, so suppressing the event
            // left every surviving unit on its old health and its old weapons.
            sAddBuffsForCommander.Invoke(null, new object[] { entityTypeCollection, entityTypeName, fresh, commanderID, false });

            logger.Log($"{entityTypeName}: applied {fresh.Buffs.Count} of {desired.Buffs.Count} buff(s)");
        }

        private static bool alreadyApplied(IDBuffSaveState[] existing, AttributeBuffWrapper buff)
        {
            if (existing == null) { return false; }

            foreach (var state in existing)
            {
                if (state.Category != buff.Category) { continue; }

                // An empty Name in the patch means "every component in this category", which is
                // how AttributeBuffSet.GetBuffs reads it.
                if (!string.IsNullOrEmpty(buff.Name) && state.ID != buff.Name) { continue; }

                if (state.Buffs == null) { continue; }

                foreach (var applied in state.Buffs)
                {
                    if (applied.AttributeID == buff.AttributeID
                        && applied.Mode == buff.Mode
                        && applied.Value == buff.Value)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // AttributeLoader has already written Subsystem.log by the time this runs.
        private void flush()
        {
            var log = writer.ToString();
            if (log.Length == 0) { return; }

            File.AppendAllText(Path.Combine(Application.dataPath, "Subsystem.log"), Environment.NewLine + log);
        }
    }
}
