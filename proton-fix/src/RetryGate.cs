using System;
using System.Runtime.CompilerServices;

namespace APIShared.ProtonWorkshopFix
{
    internal static class RetryGate
    {
        private static RetryController controller;

        internal static void Initialize(int limit, Func<object, bool> preflight, Func<bool> available,
            Func<bool> consume, Func<bool> active)
        {
            controller = new RetryController(limit, preflight, available, consume, active, PatcherLog.Warning);
        }

        internal static int AfterRead(int rawAttempts, object manager) => controller?.AfterRead(rawAttempts, manager) ?? rawAttempts;
        internal static int BeforeWrite(int nextAttempts, object manager) => controller?.BeforeWrite(nextAttempts, manager) ?? nextAttempts;
        internal static bool CanLaunch(object manager) => controller == null || controller.CanLaunch(manager);
    }

    // Pure decision runtime: callbacks are injected filesystem/platform boundaries, not copies of its behavior.
    internal sealed class RetryController
    {
        private sealed class Attempt
        {
            internal int Raw;
            internal bool Pending, Denied;
        }
        private readonly ConditionalWeakTable<object, Attempt> attempts = new ConditionalWeakTable<object, Attempt>();
        private readonly int limit;
        private readonly Func<object, bool> preflight;
        private readonly Func<bool> available, consume, active;
        private readonly Action<string> warning;

        internal RetryController(int limit, Func<object, bool> preflight, Func<bool> available, Func<bool> consume,
            Func<bool> active, Action<string> warning)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
            this.limit = limit; this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
            this.available = available ?? throw new ArgumentNullException(nameof(available));
            this.consume = consume ?? throw new ArgumentNullException(nameof(consume));
            this.active = active ?? throw new ArgumentNullException(nameof(active)); this.warning = warning ?? (_ => { });
        }

        internal int AfterRead(int raw, object owner)
        {
            if (owner == null || !active()) return raw;
            var state = attempts.GetValue(owner, _ => new Attempt());
            lock (state)
            {
                state.Raw = raw; state.Pending = false; state.Denied = false;
                if (raw < limit) return raw;
                try
                {
                    if (!available() || !preflight(owner)) return raw;
                    state.Pending = true;
                    warning("A verified Linux updater is available. The blocked Extender deployment may be confirmed once more; declining does not consume that retry.");
                    return limit - 1;
                }
                catch (Exception error) { warning("Blocked deployment was not released: " + error.Message); return raw; }
            }
        }

        internal int BeforeWrite(int next, object owner)
        {
            if (owner == null || !active() || !attempts.TryGetValue(owner, out var state)) return next;
            lock (state)
            {
                if (!state.Pending) return next;
                state.Pending = false;
                try
                {
                    // Claim before allowing the launcher. A crash or partial record still consumes the one retry.
                    if (!consume()) throw new InvalidOperationException("The one-time retry was already consumed or could not be claimed.");
                    return state.Raw == int.MaxValue ? int.MaxValue : state.Raw + 1;
                }
                catch (Exception error)
                {
                    state.Denied = true;
                    warning("Linux updater remains stopped because retry ownership could not be saved: " + error.Message);
                    return state.Raw;
                }
            }
        }

        internal bool CanLaunch(object owner)
        {
            if (owner == null || !attempts.TryGetValue(owner, out var state)) return true;
            lock (state) return !state.Denied;
        }
    }
}
