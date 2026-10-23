namespace APIShared
{
    internal static class SavegameLoadDecision
    {
        // Permission always wins over a preference or missing UI. Invalid records keep
        // their documented legacy behavior; they are not treated as a SavedOnly save.
        internal static bool UseCurrent(SavegameLoadChoiceState state, bool? choice) =>
            state == SavegameLoadChoiceState.Legacy || state == SavegameLoadChoiceState.Invalid ||
            state == SavegameLoadChoiceState.Selectable && (choice ?? true);
    }
}
