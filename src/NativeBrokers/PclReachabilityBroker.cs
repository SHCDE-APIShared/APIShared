using System;

namespace APIShared.NativeBrokers
{
    /// <summary>Shared owner of native entry 0xE2610 (PCL reachability query) with ordered Pre overrides
    /// and Post observers. Use it instead of detouring 0xE2610 yourself.</summary>
    /// <remarks>
    /// <para>Fast path: nothing is patched until the first successful registration. From then on the
    /// entry holds one process-lived detour whose native stub checks a demand byte: with no active
    /// registration it jumps straight to the original (no managed transition, lock or allocation);
    /// otherwise one managed thunk runs every Pre callback in order until one overrides, the original once
    /// unless overridden, then every Post callback in order. Dispatch allocates nothing.</para>
    /// <para>Foreign owners: the broker never installs over another detour. If the live entry bytes differ
    /// from the audited prologue, or another owner registered the entry in
    /// <see cref="APIShared.NativeSites.NativeSiteRegistry"/>, the broker becomes
    /// <see cref="NativeBrokerState.ForeignOwner"/>, logs once, records the refusal in the registry
    /// (<c>NativeSiteRegistry.Refusals</c>) and every <see cref="TryRegister"/> fails: keep your own path
    /// as the fallback. Once installed, the entry is registered there as APIShared's
    /// <c>NativeBrokers.PclReachability</c>.</para>
    /// <para>Thread: callbacks run synchronously on the thread that called the native function, usually
    /// the game's simulation or input thread. Do not block or call Unity APIs there. Calls made from inside
    /// a callback through the live entry dispatch again, exactly as with a private detour; use
    /// <see cref="CallOriginal"/> to reach the original without consumers.</para>
    /// <para>Ordering: ascending <c>order</c>, then ordinal owner GUID, then ordinal registration ID,
    /// independent of registration time. Exceptions: a throwing callback's override or result change is
    /// rolled back, it is counted in <see cref="CallbackFailures"/> and reported, and the other callbacks
    /// and the native caller continue unaffected.</para>
    /// <para>Contract details and the migration notes per consumer: docs/NATIVE_BROKERS.md.</para>
    /// </remarks>
    public static class PclReachabilityBroker
    {
        /// <summary>RVA of the brokered entry in the audited CrusaderDE.dll.</summary>
        public const int EntryRva = NativeBrokerRuntime.PclReachabilityRva;

        internal static readonly PclReachabilityBrokerCore Process =
            new PclReachabilityBrokerCore(NativeBrokerRuntime.PclReachabilityDemand, NativeBrokerRuntime.ScanLimit);

        /// <summary>Current availability. <see cref="NativeBrokerState.Dormant"/> and
        /// <see cref="NativeBrokerState.Installed"/> accept registrations.</summary>
        public static NativeBrokerState State => Process.Site.State;

        /// <summary>Human-readable reason for <see cref="State"/>, for logs.</summary>
        public static string Diagnostic => Process.Site.Diagnostic;

        /// <summary>Isolated callback failures across all registrations.</summary>
        public static long CallbackFailures => Process.Dispatcher.Registry.Failures;

        /// <summary>Registers ordered callbacks; the first successful registration installs the broker.</summary>
        /// <remarks>Any thread, at the earliest from a LibraryLoaded handler of a plugin that declares a
        /// (soft) BepInDependency on APIShared_Serp. A plugin that may meet an older consumer still detouring
        /// 0xE2610 itself (BugfixesAndQoL before its opt-in) should also declare a soft dependency on it, so
        /// that consumer installs first and the broker refuses instead of displacing it. Pass at least one
        /// callback. The registration becomes active immediately, also for calls already running on other
        /// threads that have not reached its phase yet.</remarks>
        /// <param name="ownerGuid">Owning plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique per owner.</param>
        /// <param name="order">Ascending callback order within each phase.</param>
        /// <param name="pre">Optional; may call <see cref="PclReachabilityCall.TryOverride"/>. Use Post to observe.</param>
        /// <param name="post">Optional; may call <see cref="PclReachabilityCall.TrySetResult"/>.</param>
        /// <param name="registration">Handle on success; dispose it to unregister.</param>
        /// <param name="reason">Failure reason, empty on success.</param>
        /// <returns>False for invalid arguments, a duplicate owner/ID pair, or a broker that is not
        /// <see cref="NativeBrokerState.Dormant"/> or <see cref="NativeBrokerState.Installed"/>.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, int order,
            PclReachabilityHandler pre, PclReachabilityHandler post,
            out NativeBrokerRegistration registration, out string reason) =>
            Process.Dispatcher.Registry.TryAdd(ownerGuid, registrationId, order, pre, post, out registration, out reason);

        /// <summary>Calls the original 0xE2610 without broker consumers, on the calling thread.</summary>
        /// <remarks>Uses the trampoline once the broker is installed. Before that, and while the broker is
        /// refused, it calls the live entry, which runs a foreign owner's detour if one is installed.
        /// Throws <see cref="InvalidOperationException"/> before APIShared has bound CrusaderDE.dll.</remarks>
        public static int CallOriginal(IntPtr manager, int player, int firstPcl, int secondPcl, int mode) =>
            Process.Site.Original(manager, player, firstPcl, secondPcl, mode);
    }

    // One broker instance: the process one above, or a private one on copied code in tests.
    internal sealed class PclReachabilityBrokerCore
    {
        internal readonly NativeBrokerSite<PclReachabilityNative> Site;
        internal readonly PclReachabilityDispatcher Dispatcher;
        internal PclReachabilityBrokerCore(IntPtr demand, int scanLimit)
        {
            // The bridge enters Dispatch directly, only while demand is nonzero.
            Dispatcher = new PclReachabilityDispatcher(() => Site.EnsureInstalled(), active => Site.SetDemand(active));
            Site = new NativeBrokerSite<PclReachabilityNative>(ApiSharedNativeSites.BrokerPclReachability,
                NativeBrokerRuntime.PclReachabilityRva, NativeBrokerRuntime.PclReachabilityBytes,
                NativeBrokerRuntime.PclReachabilityDisplaced, demand, Dispatcher.Dispatch,
                trampoline => Dispatcher.Original = trampoline, scanLimit);
        }
    }
}
