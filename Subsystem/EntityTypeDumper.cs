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
        private static string DescribeWeapon(WeaponAttributes weapon)
        {
            if (weapon == null) { return ""; }

            var sb = new StringBuilder();

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

                var entries = new List<KeyValuePair<string, string>>();
                foreach (var component in components)
                {
                    if (component == null) { continue; }

                    var typeName = component.GetType().Name;
                    var named = component as INamed;

                    var header = named != null && !string.IsNullOrEmpty(named.Name)
                        ? string.Format("    {0}: {1}", typeName, named.Name)
                        : string.Format("    {0}", typeName);

                    entries.Add(new KeyValuePair<string, string>(header, DescribeWeapon(component as WeaponAttributes)));
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
