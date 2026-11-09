using APIShared.Simulation;

namespace APIShared.Units.AiMilitary
{
    // A work-pump publication is not a simulation tick: paused EngineInterface.run
    // calls can publish indefinitely. Only the current world's native clock spends this budget.
    internal struct AiInitializationWaitBudget
    {
        private bool started;
        private int firstTick;
        internal bool IsExpired(int currentTick, int maximumTicks)
        {
            if (!started) { firstTick = currentTick; started = true; return false; }
            return SimulationClock.TryGetElapsedTicks(firstTick, currentTick, out int elapsed) && elapsed >= maximumTicks;
        }
    }
}
