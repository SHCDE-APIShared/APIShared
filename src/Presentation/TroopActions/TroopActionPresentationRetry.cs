namespace APIShared.Presentation
{
    // An incompatible HUD must not retry grouping and emit errors every frame. Logical
    // visibility/enable changes do not repair its structure; an explicit factory refresh can.
    internal sealed class TroopActionPresentationRetry
    {
        private object failedPanel, failedRoot;
        private TroopActionButtonRegistry.State[] failedStates;

        internal bool CanAttempt(object panel, object root, TroopActionButtonRegistry.State[] states)
        {
            if (failedStates == null || !ReferenceEquals(panel, failedPanel) ||
                !ReferenceEquals(root, failedRoot) || states.Length != failedStates.Length) return true;
            for (int i = 0; i < states.Length; i++)
                if (!ReferenceEquals(states[i].Entry, failedStates[i].Entry) ||
                    states[i].Version != failedStates[i].Version) return true;
            return false;
        }

        internal void RecordFailure(object panel, object root, TroopActionButtonRegistry.State[] states)
        { failedPanel = panel; failedRoot = root; failedStates = states; }

        internal void Clear()
        { failedPanel = failedRoot = null; failedStates = null; }
    }
}
