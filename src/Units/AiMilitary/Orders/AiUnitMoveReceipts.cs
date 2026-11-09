using System;

namespace APIShared.Units.AiMilitary.Orders
{
    // Invocation receipt for API-owned fresh-unit path publication. Records only the final call to
    // the SE Original delegate, never a mutable R3 Post result. Scope is thread-local and synchronous.
    internal static class AiUnitMoveReceipts
    {
        [ThreadStatic] private static Receipt current;
        internal static bool IsActive => current != null;
        internal static bool TryRun(int unitId, int x, int y, int extra, Func<long> invoke, out string reason)
        {
            var receipt = new Receipt(unitId, x, y, extra);
            var previous = current;
            current = receipt;
            try { invoke(); }
            finally { current = previous; }
            bool success = receipt.Count == 1 && receipt.Completed && receipt.Result == 1 && !receipt.Redirected &&
                AiRecruitMutationReceipts.IsCurrentWorld && AiRecruitMutationReceipts.IsRecruitIdentityCurrent;
            reason = success ? string.Empty : receipt.Redirected ? "A Pre callback redirected the recruit route; original was refused." :
                receipt.Count == 0 ? "The recruit route was vetoed or no Original receipt was observed." :
                "The actual native recruit route did not succeed exactly once.";
            return success;
        }
        internal static bool Before(int unitId, int x, int y, int extra)
        {
            if (!AiRecruitMutationReceipts.IsCurrentWorld || !AiRecruitMutationReceipts.IsRecruitIdentityCurrent) return false;
            var receipt = current;
            if (receipt == null) return true;
            if (receipt.UnitId != unitId || receipt.X != x || receipt.Y != y || receipt.Extra != extra)
            { receipt.Redirected = true; return false; }
            receipt.Count++;
            return receipt.Count == 1;
        }
        internal static void After(long result)
        {
            var receipt = current;
            if (receipt == null) return;
            receipt.Result = result; receipt.Completed = true;
        }
        private sealed class Receipt
        {
            internal Receipt(int id, int x, int y, int extra) { UnitId = id; X = x; Y = y; Extra = extra; }
            internal readonly int UnitId, X, Y, Extra;
            internal int Count;
            internal bool Redirected, Completed;
            internal long Result;
        }
    }
}
