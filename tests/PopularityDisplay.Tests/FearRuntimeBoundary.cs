namespace APIShared.Economy
{
    // The native/IL adapter is tested against installed binaries in APISharedTests.
    internal static class FearPopularityRuntime
    {
        internal static bool Available => true;
        internal static bool TargetedAvailable => true;
        internal static string TargetedDiagnostic => null;
        internal static string Diagnostic => null;
        internal static void ReportApplied(int level, int units) { }
        internal static void ReportOverflow() { }
    }
    internal static class PopularityPlayerRuntime
    {
        internal static int Classify(int playerId,System.IntPtr resources)=>0;
    }
}
