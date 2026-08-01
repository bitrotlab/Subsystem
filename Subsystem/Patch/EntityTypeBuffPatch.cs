using BBI.Game.Data;
using System.Collections.Generic;

namespace Subsystem.Patch
{
    public class EntityTypeBuffPatch
    {
        // Match every entity type whose name starts with the key, instead of just the one type
        // named by it. Same semantics as ModifyEntityTypeBuffCommand.
        public bool? UseAsPrefix { get; set; }

        // Optional extra filter on UnitAttributes.Class. None (the default) means no filter.
        public UnitClass? UnitClass { get; set; }

        // How UnitClass is matched: Or = shares any flag, And = has all of them.
        public FlagOperator? ClassOperator { get; set; }

        public Dictionary<string, AttributeBuffPatch> Buffs { get; set; } = new Dictionary<string, AttributeBuffPatch>();
    }
}
