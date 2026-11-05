namespace APIShared.Units.AiMilitary.Engineering
{
    // Constant-size cumulative reduction. Every builder tick observes all actual
    // participants; earlier regular/unknown work cannot be erased by later crews.
    internal sealed class AiBuildProvenance
    {
        internal AiBuildProvenance(int type, bool known) { Type = type; Known = known; }
        internal readonly int Type;
        internal bool Known;
        internal bool HasContributor;
        internal bool AllAdditional = true;
        internal bool IsAdditional => Known && HasContributor && AllAdditional;
        internal void Observe(bool validIdentity, bool additional)
        {
            HasContributor = true;
            if (!validIdentity) Known = false;
            AllAdditional &= validIdentity && additional;
        }
    }
}
