using System;
using APIShared.Units.AiMilitary;

internal static class InitializationWaitTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        var budget = new AiInitializationWaitBudget();
        for (int i = 0; i < 5000; i++) Check(!budget.IsExpired(0, 3000), "Paused work-pump publications must not consume native ticks.");
        Check(!budget.IsExpired(2999, 3000), "2999 active native ticks do not expire initialization.");
        Check(budget.IsExpired(3000, 3000), "3000 active native ticks expire initialization.");
        budget = default;
        int start = int.MaxValue - 100;
        Check(!budget.IsExpired(start, 3000), "A negative/signed-wrap clock is not an unavailable sentinel.");
        Check(!budget.IsExpired(unchecked(start + 2999), 3000), "Wrapped native clock preserves the 2999 boundary.");
        Check(budget.IsExpired(unchecked(start + 3000), 3000), "Wrapped native clock expires at exactly 3000 ticks.");
        budget = default;
        Check(!budget.IsExpired(100, 3000) && !budget.IsExpired(99, 3000), "An older/ambiguous clock must not advance a wait budget.");
        Check(!budget.IsExpired(3099, 3000) && budget.IsExpired(3100, 3000), "Older probes cannot change the original time anchor.");
        Console.WriteLine("PASS: production initialization wait budget: 5000 paused polls, 2999/3000 active ticks, signed wrap and backwards probes.");
    }
}

// Only SimulationClock's unrelated world-source boundary is doubled. The actual
// TryGetElapsedTicks implementation above is linked unchanged, not reimplemented.
namespace APIShared
{
    public sealed class MissionContext { }
}
namespace APIShared.Simulation
{
    internal static class SimulationTickSource
    {
        internal static bool TryRead(APIShared.MissionContext context, out int tick) { tick = 0; return false; }
        internal static void Report(string stage, Exception error) { }
    }
}
