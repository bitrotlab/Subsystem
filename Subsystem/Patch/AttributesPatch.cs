using System.Collections.Generic;

namespace Subsystem.Patch
{
    public class AttributesPatch
    {
        public Dictionary<string, EntityTypePatch> Entities { get; set; } = new Dictionary<string, EntityTypePatch>();

        // Keyed by commander ID. Applied by CommanderBuffLoader from a second hook, because the
        // per-commander entity types do not exist yet when Entities is applied.
        public Dictionary<string, CommanderPatch> Commanders { get; set; } = new Dictionary<string, CommanderPatch>();
    }
}
