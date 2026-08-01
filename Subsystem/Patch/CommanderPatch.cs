using System.Collections.Generic;

namespace Subsystem.Patch
{
    public class CommanderPatch
    {
        public Dictionary<string, EntityTypeBuffPatch> EntityTypeBuffs { get; set; } = new Dictionary<string, EntityTypeBuffPatch>();
    }
}
