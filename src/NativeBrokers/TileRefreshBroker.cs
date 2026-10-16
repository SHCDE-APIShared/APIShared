using System;

namespace APIShared.NativeBrokers
{
    /// <summary>Shared owner of native entry 0xD90D0 (tile refresh helper) with ordered Post observers.
    /// Use it instead of detouring 0xD90D0 yourself.</summary>
    /// <remarks>
    /// <para>Same fast path, foreign-owner rule, threading and ordering as
    /// <see cref="PclReachabilityBroker"/>: nothing is patched before the first registration; with no
    /// active registration the native stub jumps straight to the original; a non-pristine or registered
    /// foreign entry makes the broker refuse (<see cref="NativeBrokerState.ForeignOwner"/>) and
    /// registrations fail, so consumers keep their own path. Post only: the original runs first, then
    /// every callback in order with a copy of the arguments. A throwing callback is counted, reported and
    /// isolated. Registered in the native-site registry as APIShared's <c>NativeBrokers.TileRefresh</c>.</para>
    /// <para>Contract details and the migration notes per consumer: docs/NATIVE_BROKERS.md.</para>
    /// </remarks>
    public static class TileRefreshBroker
    {
        /// <summary>RVA of the brokered entry in the audited CrusaderDE.dll.</summary>
        public const int EntryRva = NativeBrokerRuntime.TileRefreshRva;

        internal static readonly TileRefreshBrokerCore Process =
            new TileRefreshBrokerCore(NativeBrokerRuntime.TileRefreshDemand, NativeBrokerRuntime.ScanLimit);

        /// <summary>Current availability. <see cref="NativeBrokerState.Dormant"/> and
        /// <see cref="NativeBrokerState.Installed"/> accept registrations.</summary>
        public static NativeBrokerState State => Process.Site.State;

        /// <summary>Human-readable reason for <see cref="State"/>, for logs.</summary>
        public static string Diagnostic => Process.Site.Diagnostic;

        /// <summary>Isolated callback failures across all registrations.</summary>
        public static long CallbackFailures => Process.Dispatcher.Registry.Failures;

        /// <summary>Registers an ordered Post observer; the first successful registration installs the broker.</summary>
        /// <remarks>Same timing, thread and dependency advice as <see cref="PclReachabilityBroker.TryRegister"/>.</remarks>
        /// <param name="ownerGuid">Owning plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique per owner.</param>
        /// <param name="order">Ascending callback order.</param>
        /// <param name="post">Called after the original returned.</param>
        /// <param name="registration">Handle on success; dispose it to unregister.</param>
        /// <param name="reason">Failure reason, empty on success.</param>
        /// <returns>False for invalid arguments, a duplicate owner/ID pair, or a broker that is not
        /// <see cref="NativeBrokerState.Dormant"/> or <see cref="NativeBrokerState.Installed"/>.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, int order, TileRefreshHandler post,
            out NativeBrokerRegistration registration, out string reason) =>
            Process.Dispatcher.Registry.TryAdd(ownerGuid, registrationId, order, null, post, out registration, out reason);

        /// <summary>Calls the original 0xD90D0 without broker consumers, on the calling thread. Same rules as
        /// <see cref="PclReachabilityBroker.CallOriginal"/>.</summary>
        public static void CallOriginal(IntPtr manager, int tileY, int tileId) =>
            Process.Site.Original(manager, tileY, tileId);
    }

    internal sealed class TileRefreshBrokerCore
    {
        internal readonly NativeBrokerSite<TileRefreshNative> Site;
        internal readonly TileRefreshDispatcher Dispatcher;
        internal TileRefreshBrokerCore(IntPtr demand, int scanLimit)
        {
            // The bridge enters Dispatch directly, only while demand is nonzero.
            Dispatcher = new TileRefreshDispatcher(() => Site.EnsureInstalled(), active => Site.SetDemand(active));
            Site = new NativeBrokerSite<TileRefreshNative>(ApiSharedNativeSites.BrokerTileRefresh,
                NativeBrokerRuntime.TileRefreshRva, NativeBrokerRuntime.TileRefreshBytes,
                NativeBrokerRuntime.TileRefreshDisplaced, demand, Dispatcher.Dispatch,
                trampoline => Dispatcher.Original = trampoline, scanLimit);
        }
    }
}
