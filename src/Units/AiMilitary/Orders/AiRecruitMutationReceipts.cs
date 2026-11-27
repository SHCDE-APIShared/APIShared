using System;

namespace APIShared.Units.AiMilitary.Orders
{
    // A managed world can change from a synchronous R3 callback even while the
    // engine monitor is held, because Monitor is reentrant. Checks at the final
    // Original boundary prevent an old operation mutating reused slots in that world.
    internal static class AiRecruitMutationReceipts
    {
        [ThreadStatic] private static Scope current;
        internal static bool IsCurrentWorld => current == null || current.IsCurrent;
        internal static bool IsRecruitIdentityCurrent => AssignmentScope()?.IdentityCurrent ?? true;
        internal static Scope Enter(Func<bool> isCurrentWorld, int unitId = 0, int tribeId = 0,
            Func<bool> identityCurrent = null, Func<bool> canAssign = null)
        {
            if (isCurrentWorld == null) throw new ArgumentNullException(nameof(isCurrentWorld));
            var scope = new Scope(current, isCurrentWorld, unitId, tribeId, identityCurrent, canAssign); current = scope; return scope;
        }
        internal static bool BeforeAssign(int unitId, int tribeId)
        {
            if (!IsCurrentWorld) return false;
            var scope = AssignmentScope();
            if (scope == null) return true;
            if (unitId != scope.UnitId || tribeId != scope.TribeId || scope.AssignCount != 0 || scope.Redirected ||
                !scope.IdentityCurrent || !scope.CanAssign)
            { scope.Redirected = true; return false; }
            scope.AssignCount++; return true;
        }
        internal static void AfterAssign()
        { var scope = AssignmentScope(); if (scope != null) scope.AssignCompleted = true; }
        private static Scope AssignmentScope()
        {
            for (var scope = current; scope != null && scope.IsCurrent; scope = scope.Previous)
                if (scope.UnitId != 0) return scope;
            return null;
        }
        internal static long FilterReturn(long value) => IsCurrentWorld ? value : 0;
        internal static long FilterTribeCreateReturn(long value, long rawOriginalId)
        {
            var scope = current;
            if (scope == null) return value;
            return scope.IsCurrent && rawOriginalId > 0 && value == rawOriginalId ? value : 0;
        }

        internal sealed class Scope : IDisposable
        {
            private readonly Scope previous;
            internal Scope Previous => previous;
            private readonly Func<bool> isCurrentWorld;
            private readonly Func<bool> identityCurrent, canAssign;
            internal readonly int UnitId, TribeId;
            internal int AssignCount;
            internal bool AssignCompleted, Redirected;
            internal Scope(Scope previous, Func<bool> isCurrentWorld, int unitId, int tribeId, Func<bool> identityCurrent, Func<bool> canAssign)
            { this.previous = previous; this.isCurrentWorld = isCurrentWorld; UnitId = unitId; TribeId = tribeId; this.identityCurrent = identityCurrent; this.canAssign = canAssign; }
            internal bool IsCurrent { get { try { return isCurrentWorld(); } catch { return false; } } }
            internal bool IdentityCurrent { get { try { return identityCurrent == null || identityCurrent(); } catch { return false; } } }
            internal bool CanAssign { get { try { return canAssign == null || canAssign(); } catch { return false; } } }
            internal bool WasAssigned => AssignCount == 1 && AssignCompleted && !Redirected && IsCurrent && IdentityCurrent;
            public void Dispose()
            {
                if (!ReferenceEquals(current, this)) throw new InvalidOperationException("AI recruit mutation scopes must be disposed in order on their owning thread.");
                current = previous;
            }
        }
    }
}
