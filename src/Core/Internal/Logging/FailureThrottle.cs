using System.Threading;

namespace APIShared
{
    // Counts one failure and reports whether it is the 1st, 2nd, 4th, 8th, ... occurrence, so a
    // repeating failure stays visible in the log without flooding it.
    internal static class FailureThrottle
    {
        internal static bool ShouldReport(ref long failures) => IsPowerOfTwo(Interlocked.Increment(ref failures));
        internal static bool ShouldReport(ref long failures, out long count) => IsPowerOfTwo(count = Interlocked.Increment(ref failures));
        internal static bool ShouldReport(ref int failures) => IsPowerOfTwo(Interlocked.Increment(ref failures));
        private static bool IsPowerOfTwo(long count) => (count & (count - 1)) == 0;
    }
}
