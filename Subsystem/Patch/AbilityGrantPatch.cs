namespace Subsystem.Patch
{
    public class AbilityGrantPatch
    {
        // Entity type name of the ability to copy, e.g. "Ability_C_Battlecruiser_Regen".
        // Subsystem.entities.log lists them, with what each one actually does.
        public string From { get; set; }

        // Leave a unit alone if it already regenerates -- that is, if any ability it has applies
        // a healing health-over-time effect. Defaults to true, which is what you want for
        // "give self-repair to everything that does not already have it".
        public bool? SkipIfSelfHealing { get; set; }
    }
}
