namespace SubsystemLint;

/// <summary>
/// The entity/component names from a Subsystem.entities.log, which the mod writes on every
/// game start. Lines are either an entity name at column 0, or an indented "Type" /
/// "Type: Name" component belonging to the entity above it.
/// </summary>
internal sealed class EntityNames
{
    private readonly Dictionary<string, HashSet<string>> _components = new(StringComparer.Ordinal);

    public IEnumerable<string> Entities => _components.Keys;

    public bool HasEntity(string entity) => _components.ContainsKey(entity);

    public bool HasComponent(string entity, string component) =>
        _components.TryGetValue(entity, out var set) && set.Contains(component);

    public IEnumerable<string> ComponentsOf(string entity) =>
        _components.TryGetValue(entity, out var set) ? set : [];

    public static EntityNames Load(string path)
    {
        var names = new EntityNames();
        string? current = null;

        foreach (var raw in File.ReadLines(path))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) continue;

            if (!char.IsWhiteSpace(raw[0]))
            {
                current = raw.Trim();
                if (current.Length > 0) names._components.TryAdd(current, new HashSet<string>(StringComparer.Ordinal));
                continue;
            }

            if (current == null) continue;

            // Components are indented exactly four spaces; anything deeper is per-component
            // detail (weapon ranges, target-class modifiers) and is not a name.
            if (raw.Length > 4 && raw[4] == ' ') continue;

            // "    WeaponAttributes: C_HAC_Upgrade01_Weapon_G2A_MP" -> record the name part.
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var componentName = line[(colon + 1)..].Trim();
            if (componentName.Length > 0) names._components[current].Add(componentName);
        }

        return names;
    }
}
