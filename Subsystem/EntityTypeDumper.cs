using BBI.Core;
using BBI.Core.Data;
using BBI.Game.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Subsystem
{
    // Writes out every entity type the game actually loaded, with the components attached to
    // each one. Component names are what patch.json keys on, so this is the authoritative
    // answer to "what can I write in patch.json" for the version of the game you have
    // installed -- unlike a name list scraped from asset files, which misses anything the
    // build compresses or renames.
    public static class EntityTypeDumper
    {
        // For weapons, the interesting question is "what can this thing actually shoot, and does
        // it do so on its own" -- which is the auto-fire flags plus the per-target-class
        // modifiers, not the damage numbers.
        private static string DescribeWeapon(WeaponAttributes weapon, WeaponBinding[] loadout)
        {
            if (weapon == null) { return ""; }

            var sb = new StringBuilder();

            // Commander buffs on UnitWeapon_* and WeaponRange_* key on the loadout's WeaponID,
            // which is not always the weapon's own name -- the name above is what the
            // "Entities" section keys on. Print both so they are not confused.
            var weaponID = FindWeaponID(weapon, loadout);
            if (weaponID != null)
            {
                sb.AppendFormat("        weapon ID (for commander buffs): {0}\n", weaponID);
            }

            sb.AppendFormat("        auto-acquire: {0}   auto-fire: {1}   damage: {2}   cooldown: {3}ms\n",
                weapon.ExcludeFromAutoTargetAcquisition ? "no" : "yes",
                weapon.ExcludeFromAutoFire ? "no" : "yes",
                weapon.BaseDamagePerRound,
                weapon.CooldownTimeMS);

            sb.AppendFormat("        DamageType: {0}   TargetStyle: {1}\n",
                weapon.DamageType, weapon.TargetStyle);

            if (weapon.Ranges != null && weapon.Ranges.Length > 0)
            {
                sb.Append("        ranges:");
                foreach (var range in weapon.Ranges)
                {
                    if (range == null) { continue; }
                    sb.AppendFormat(" {0}={1}", range.Range, range.Distance);
                }
                sb.Append("\n");
            }

            if (weapon.Modifiers != null && weapon.Modifiers.Length > 0)
            {
                foreach (var modifier in weapon.Modifiers)
                {
                    if (modifier == null) { continue; }
                    sb.AppendFormat("        vs {0} ({1}): {2} {3}\n",
                        modifier.TargetClass, modifier.ClassOperator, modifier.Modifier, modifier.Amount);
                }
            }

            return sb.ToString();
        }

        // Regen, repair and the rest of the interesting abilities are all AbilityClass plus one
        // populated sub-attribute, so the type alone does not tell you what an ability does.
        // Passive self-repair, for instance, is ApplyStatusEffect + autocast-on-spawn pointing
        // at a status effect whose modifier carries the heal rate.
        private static string DescribeAbility(AbilityAttributes ability)
        {
            if (ability == null) { return ""; }

            var sb = new StringBuilder();

            // TargetingType Passive is what makes UnitManager.ActivatePassiveAbilities fire an
            // ability on spawn without the player casting it, so it is the flag that separates
            // "heals by itself" from "there is a button somewhere".
            sb.AppendFormat("        type: {0}   targeting: {1}", ability.AbilityType, ability.TargetingType);

            if (ability.Autocast != null && ability.Autocast.IsAutocastable)
            {
                sb.AppendFormat("   autocast: yes (on spawn: {0})", ability.Autocast.AutocastEnabledOnSpawn ? "yes" : "no");
            }

            if (ability.IsToggleable) { sb.Append("   toggleable"); }

            sb.AppendFormat("   cooldown: {0}s   warmup: {1}s\n", ability.CooldownTimeSecs, ability.WarmupTimeSecs);

            if (ability.Repair != null && !string.IsNullOrEmpty(ability.Repair.WeaponID))
            {
                sb.AppendFormat("        repairs with weapon ID: {0}\n", ability.Repair.WeaponID);
            }

            var apply = ability.ApplyStatusEffect;
            if (apply == null || apply.StatusEffectsToApply == null) { return sb.ToString(); }

            foreach (var effect in apply.StatusEffectsToApply)
            {
                if (effect == null) { continue; }

                sb.AppendFormat("        applies status effect: {0}   lifetime: {1}   duration: {2}   maxStacks: {3}\n",
                    effect.Name, effect.Lifetime, effect.Duration, effect.MaxStacks);

                if (effect.Modifiers == null) { continue; }

                foreach (var modifier in effect.Modifiers)
                {
                    var healthOverTime = modifier.HealthOverTimeAttributes;
                    if (healthOverTime.Amount == 0) { continue; }

                    sb.AppendFormat("            health over time: {0} per {1}ms, {2}, id \"{3}\"\n",
                        healthOverTime.Amount, healthOverTime.MSTickDuration,
                        healthOverTime.DamageType, healthOverTime.ID);
                }
            }

            return sb.ToString();
        }

        private static string FindWeaponID(WeaponAttributes weapon, WeaponBinding[] loadout)
        {
            if (loadout == null) { return null; }

            foreach (var binding in loadout)
            {
                if (binding != null && binding.Weapon != null && binding.Weapon.Name == weapon.Name)
                {
                    return binding.WeaponID;
                }
            }

            return null;
        }

        public static void Dump(EntityTypeCollection entityTypeCollection, TextWriter writer)
        {
            var names = new List<string>();
            foreach (var name in entityTypeCollection.GetAllEntityTypeNames())
            {
                names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);

            writer.WriteLine("# Subsystem entity type dump");
            writer.WriteLine("# {0} entity types", names.Count);
            writer.WriteLine("#");
            writer.WriteLine("# Each entry is an \"Entities\" key in patch.json. Indented lines are the");
            writer.WriteLine("# components on that entity; a component shown as \"Type: Name\" is keyed by");
            writer.WriteLine("# that name in patch.json, one shown as just \"Type\" is not.");
            writer.WriteLine();

            foreach (var name in names)
            {
                writer.WriteLine(name);

                var entityType = entityTypeCollection.GetEntityType(name);
                if (entityType == null)
                {
                    writer.WriteLine("    (could not be resolved)");
                    continue;
                }

                var components = entityType.GetAll<object>();
                if (components == null || components.Length == 0)
                {
                    writer.WriteLine("    (no components)");
                    continue;
                }

                var unitAttributes = entityType.Get<UnitAttributes>();
                var loadout = unitAttributes != null ? unitAttributes.WeaponLoadout : null;

                var entries = new List<KeyValuePair<string, string>>();
                foreach (var component in components)
                {
                    if (component == null) { continue; }

                    var typeName = component.GetType().Name;
                    var named = component as INamed;

                    var header = named != null && !string.IsNullOrEmpty(named.Name)
                        ? string.Format("    {0}: {1}", typeName, named.Name)
                        : string.Format("    {0}", typeName);

                    var detail = DescribeWeapon(component as WeaponAttributes, loadout)
                               + DescribeAbility(component as AbilityAttributes);

                    entries.Add(new KeyValuePair<string, string>(header, detail));
                }

                entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                foreach (var entry in entries)
                {
                    writer.WriteLine(entry.Key);
                    if (entry.Value.Length > 0) { writer.Write(entry.Value); }
                }
            }
        }
    }
}
