using BBI.Game.Data;
using System.Collections.Generic;

namespace Subsystem.Patch
{
    public class EntityTypeAbilityPatch
    {
        // Same matching as EntityTypeBuffPatch: name, or name prefix, narrowed by unit class.
        public bool? UseAsPrefix { get; set; }
        public UnitClass? UnitClass { get; set; }
        public FlagOperator? ClassOperator { get; set; }

        public Dictionary<string, AbilityGrantPatch> Abilities { get; set; } = new Dictionary<string, AbilityGrantPatch>();
    }
}
