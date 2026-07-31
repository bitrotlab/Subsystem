using Mono.Cecil;
using System.Text.Json;

namespace SubsystemLint;

/// <summary>How a Dictionary&lt;string, T&gt; key in the patch model is interpreted.</summary>
internal enum KeyKind
{
    /// <summary>Entity type name, e.g. "C_HAC_Upgrade01_MP".</summary>
    Entity,
    /// <summary>Name of a named component on the entity, e.g. a weapon.</summary>
    Component,
    /// <summary>Consecutive list index; AttributeLoader requires these to parse as integers.</summary>
    Index,
    /// <summary>Free-form identifier that cannot be checked statically.</summary>
    Free,
}

internal sealed class Schema
{
    private readonly AssemblyDefinition _subsystem;
    private readonly Dictionary<string, TypeDefinition> _types = new(StringComparer.Ordinal);

    // Which dictionaries are keyed by a name and which by a list index. AttributeLoader's
    // applyListPatch requires integer keys; the rest key on names in the game data.
    private static readonly Dictionary<string, KeyKind> KeyKinds = new(StringComparer.Ordinal)
    {
        ["Subsystem.Patch.AttributesPatch.Entities"] = KeyKind.Entity,
        ["Subsystem.Patch.EntityTypePatch.AbilityAttributes"] = KeyKind.Component,
        ["Subsystem.Patch.EntityTypePatch.StorageAttributes"] = KeyKind.Component,
        ["Subsystem.Patch.EntityTypePatch.WeaponAttributes"] = KeyKind.Component,
        ["Subsystem.Patch.UnitHangarAttributesPatch.HangarBays"] = KeyKind.Component,
        ["Subsystem.Patch.StorageAttributesPatch.InventoryLoadout"] = KeyKind.Free,
        ["Subsystem.Patch.ExperienceAttributesPatch.Levels"] = KeyKind.Index,
        ["Subsystem.Patch.ExperienceLevelAttributesPatch.Buff"] = KeyKind.Index,
        ["Subsystem.Patch.WeaponAttributesPatch.Modifiers"] = KeyKind.Index,
        ["Subsystem.Patch.WeaponAttributesPatch.EntityTypesToSpawnOnImpact"] = KeyKind.Index,
    };

    public Schema(string subsystemDll, string managed)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory(".");
        resolver.AddSearchDirectory(managed);
        _subsystem = AssemblyDefinition.ReadAssembly(
            subsystemDll, new ReaderParameters { AssemblyResolver = resolver });

        foreach (var t in _subsystem.MainModule.GetTypes())
            _types[t.FullName] = t;
    }

    public void Validate(JsonElement element, string typeName, string path, Report report, EntityNames? names)
        => ValidateType(element, Resolve(typeName), path, report, names, currentEntity: null);

    private TypeDefinition Resolve(string fullName) =>
        _types.TryGetValue(fullName, out var t)
            ? t
            : throw new InvalidOperationException($"type not found in Subsystem.dll: {fullName}");

    private void ValidateType(JsonElement element, TypeDefinition type, string path,
                              Report report, EntityNames? names, string? currentEntity)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            report.Error(path, $"expected an object for {type.Name}, found {Describe(element)}");
            return;
        }

        var properties = type.Properties
            .Where(p => p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true })
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var member in element.EnumerateObject())
        {
            var childPath = path.Length == 0 ? member.Name : $"{path}.{member.Name}";

            if (!properties.TryGetValue(member.Name, out var prop))
            {
                var hint = Suggest(member.Name, properties.Keys);
                report.Error(childPath,
                    $"'{member.Name}' is not a property of {type.Name}"
                    + (hint != null ? $" — did you mean '{hint}'?" : "")
                    + "  [this makes LitJson throw, so the WHOLE patch is discarded]");
                continue;
            }

            ValidateValue(member.Value, prop.PropertyType, childPath, report, names, currentEntity,
                          $"{type.FullName}.{prop.Name}");
        }
    }

    private void ValidateValue(JsonElement element, TypeReference typeRef, string path,
                               Report report, EntityNames? names, string? currentEntity, string memberKey)
    {
        // Nullable<T> — every scalar in the patch model is one of these.
        if (typeRef is GenericInstanceType { Name: "Nullable`1" } nullable)
        {
            if (element.ValueKind == JsonValueKind.Null) return;
            ValidateValue(element, nullable.GenericArguments[0], path, report, names, currentEntity, memberKey);
            return;
        }

        if (typeRef is GenericInstanceType { Name: "Dictionary`2" } dict)
        {
            ValidateDictionary(element, dict, path, report, names, currentEntity, memberKey);
            return;
        }

        switch (typeRef.FullName)
        {
            case "System.String":
                Expect(element, JsonValueKind.String, path, report, "a string");
                return;
            case "System.Boolean":
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    report.Error(path, $"expected true/false, found {Describe(element)}");
                return;
            case "System.Int32":
                if (element.ValueKind != JsonValueKind.Number) { report.Error(path, $"expected a whole number, found {Describe(element)}"); return; }
                if (!element.TryGetInt32(out _))
                    report.Error(path, $"expected a whole number that fits in an int, found {element.GetRawText()}");
                return;
            case "System.Double" or "System.Single":
                if (element.ValueKind != JsonValueKind.Number)
                    report.Error(path, $"expected a number, found {Describe(element)}");
                return;
        }

        var def = SafeResolve(typeRef);

        if (def is { IsEnum: true })
        {
            ValidateEnum(element, def, path, report);
            return;
        }

        // string[] / IEnumerable<string> — e.g. Dependencies, ThreatCounters.
        if (typeRef is ArrayType || typeRef is GenericInstanceType { Name: "IEnumerable`1" or "List`1" })
        {
            if (element.ValueKind != JsonValueKind.Array)
                report.Error(path, $"expected an array, found {Describe(element)}");
            return;
        }

        if (def != null && def.FullName.StartsWith("Subsystem.Patch.", StringComparison.Ordinal))
        {
            ValidateType(element, def, path, report, names, currentEntity);
            return;
        }

        report.Note(path, $"not checked (type {typeRef.FullName})");
    }

    private void ValidateDictionary(JsonElement element, GenericInstanceType dict, string path,
                                    Report report, EntityNames? names, string? currentEntity, string memberKey)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            report.Error(path, $"expected an object of named entries, found {Describe(element)}");
            return;
        }

        var kind = KeyKinds.TryGetValue(memberKey, out var k) ? k : KeyKind.Free;
        var valueType = dict.GenericArguments[1];

        foreach (var entry in element.EnumerateObject())
        {
            var childPath = $"{path}.{entry.Name}";
            var entity = currentEntity;

            switch (kind)
            {
                case KeyKind.Index:
                    if (!int.TryParse(entry.Name, out _))
                        report.Error(childPath,
                            $"'{entry.Name}' must be a list index (\"0\", \"1\", ...); "
                            + "AttributeLoader stops processing this list otherwise");
                    break;

                case KeyKind.Entity:
                    entity = entry.Name;
                    if (names != null && !names.HasEntity(entry.Name))
                    {
                        var hint = Suggest(entry.Name, names.Entities);
                        report.Error(childPath,
                            $"no entity type named '{entry.Name}' in this install"
                            + (hint != null ? $" — did you mean '{hint}'?" : ""));
                    }
                    break;

                case KeyKind.Component:
                    if (names != null && entity != null && names.HasEntity(entity)
                        && !names.HasComponent(entity, entry.Name))
                    {
                        var hint = Suggest(entry.Name, names.ComponentsOf(entity));
                        report.Error(childPath,
                            $"'{entity}' has no component named '{entry.Name}'"
                            + (hint != null ? $" — did you mean '{hint}'?" : ""));
                    }
                    break;
            }

            ValidateValue(entry.Value, valueType, childPath, report, names, entity, memberKey);
        }
    }

    private static void ValidateEnum(JsonElement element, TypeDefinition def, string path, Report report)
    {
        var members = def.Fields.Where(f => f.IsStatic && f.IsLiteral).Select(f => f.Name).ToList();

        if (element.ValueKind == JsonValueKind.Number) return; // numeric enum values are legal

        if (element.ValueKind != JsonValueKind.String)
        {
            report.Error(path, $"expected one of {string.Join(", ", members)}, found {Describe(element)}");
            return;
        }

        var raw = element.GetString() ?? "";
        var isFlags = def.CustomAttributes.Any(a => a.AttributeType.Name == "FlagsAttribute");

        // [Flags] enums accept "A, B" per the README.
        var parts = isFlags
            ? raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [raw];

        foreach (var part in parts)
        {
            if (members.Contains(part, StringComparer.Ordinal)) continue;
            var hint = Suggest(part, members);
            report.Error(path,
                $"'{part}' is not a valid {def.Name} value"
                + (hint != null ? $" — did you mean '{hint}'?" : $" (valid: {string.Join(", ", members)})"));
        }

        if (!isFlags && raw.Contains(','))
            report.Error(path, $"{def.Name} is not a [Flags] enum, so it takes a single value");
    }

    private static void Expect(JsonElement e, JsonValueKind kind, string path, Report report, string what)
    {
        if (e.ValueKind != kind) report.Error(path, $"expected {what}, found {Describe(e)}");
    }

    private static string Describe(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => $"the string {e.GetRawText()}",
        JsonValueKind.Number => $"the number {e.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {e.GetRawText()}",
        _ => "null",
    };

    private static TypeDefinition? SafeResolve(TypeReference r)
    {
        try { return r.Resolve(); } catch { return null; }
    }

    /// <summary>Closest candidate by edit distance, when it is close enough to be worth suggesting.</summary>
    internal static string? Suggest(string value, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var c in candidates)
        {
            var d = Distance(value, c);
            if (d < bestDistance) { bestDistance = d; best = c; }
        }

        var threshold = Math.Max(2, value.Length / 3);
        return bestDistance <= threshold ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
