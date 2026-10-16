using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared.NativeBrokers
{
    /// <summary>Availability of one shared native entry broker.</summary>
    public enum NativeBrokerState
    {
        /// <summary>APIShared has not bound CrusaderDE.dll yet. Register from a LibraryLoaded handler of a
        /// plugin that declares a (soft) BepInDependency on APIShared_Serp, or later.</summary>
        Pending,
        /// <summary>Bound to the audited build. Nothing is patched until the first registration.</summary>
        Dormant,
        /// <summary>The broker's entry detour is published for the process lifetime. Calls enter managed
        /// code only while at least one registration is active.</summary>
        Installed,
        /// <summary>Another owner patched the entry or registered it in the native-site registry first.
        /// The broker never installs over it: registrations fail and consumers keep their own path.</summary>
        ForeignOwner,
        /// <summary>Unsupported game build or a failed installation. Registrations fail.</summary>
        Unavailable
    }

    /// <summary>Handle of one broker registration. Dispose it to unregister.</summary>
    /// <remarks>Registrations are not persisted and have no State shared with other owners.
    /// <see cref="Dispose"/> may be called from any thread, any number of times, including from inside a
    /// callback of this or another registration. After it returns, no new callback of this registration
    /// starts; a callback that already started on another thread may still finish. Disposing the last
    /// active registration of a broker returns that entry to its zero-registration fast path.</remarks>
    public sealed class NativeBrokerRegistration : IDisposable
    {
        internal int ActiveValue = 1;
        private readonly Action<NativeBrokerRegistration> remove;
        internal NativeBrokerRegistration(string ownerGuid, string registrationId, int order, Action<NativeBrokerRegistration> remove)
        { OwnerGuid = ownerGuid; RegistrationId = registrationId; Order = order; this.remove = remove; }
        /// <summary>Owning plugin GUID given at registration.</summary>
        public string OwnerGuid { get; }
        /// <summary>Registration ID, unique per owner and broker.</summary>
        public string RegistrationId { get; }
        /// <summary>Ascending callback order given at registration.</summary>
        public int Order { get; }
        /// <summary>False once <see cref="Dispose"/> has been called.</summary>
        public bool IsActive => Volatile.Read(ref ActiveValue) != 0;
        /// <summary>Unregisters. Idempotent and thread-safe.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref ActiveValue, 0) != 0)
                remove(this);
        }
        /// <inheritdoc/>
        public override string ToString() => OwnerGuid + "/" + RegistrationId + " (order " + Order + ")";
    }

    /// <summary>Receives one call of native entry 0xE2610 by reference. Change the call only through
    /// <paramref name="call"/>; a stored copy is a detached snapshot and changes nothing.</summary>
    /// <param name="call">The call; valid only for the duration of the callback.</param>
    public delegate void PclReachabilityHandler(ref PclReachabilityCall call);

    /// <summary>One call of native entry 0xE2610, the PCL (path region) reachability query. A value type
    /// passed by reference: dispatch allocates nothing.</summary>
    /// <remarks>The arguments are the native call's own values and cannot be changed. The native function
    /// usually returns a PCL ID; some callers test it only for zero. Which of the two PCL arguments is the
    /// source and which the target is not audited here: APIShared's gate-routing ABI names them
    /// (targetPcl, sourcePcl), BugfixesAndQoL (sourceRegion, targetRegion), so they are named by position.</remarks>
    public struct PclReachabilityCall
    {
        private int result, originalResult;
        private bool overridden, originalCalled, post;
        internal PclReachabilityCall(IntPtr manager, int player, int firstPcl, int secondPcl, int mode)
        {
            Manager = manager; Player = player; FirstPcl = firstPcl; SecondPcl = secondPcl; Mode = mode;
            result = originalResult = 0; overridden = originalCalled = post = false;
        }
        /// <summary>First native argument (RCX): the path manager the caller passed.</summary>
        public IntPtr Manager { get; }
        /// <summary>Second native argument (EDX): the player ID the query is made for.</summary>
        public int Player { get; }
        /// <summary>Third native argument (R8D): a PCL ID.</summary>
        public int FirstPcl { get; }
        /// <summary>Fourth native argument (R9D): a PCL ID.</summary>
        public int SecondPcl { get; }
        /// <summary>Fifth native argument (stack): the route mode.</summary>
        public int Mode { get; }
        /// <summary>The value the call will return. In Pre it is 0 until a Pre overrides; in Post it is the
        /// original's result or the override, including replacements by earlier Post callbacks.</summary>
        public int Result => result;
        /// <summary>True when a Pre callback overrode the call; the original was then not called.</summary>
        public bool IsOverridden => overridden;
        /// <summary>True in Post when the original ran for this call.</summary>
        public bool OriginalCalled => originalCalled;
        /// <summary>In Post, what the original returned, unaffected by earlier Post replacements; 0 when
        /// <see cref="OriginalCalled"/> is false.</summary>
        public int OriginalResult => originalResult;
        /// <summary>True during Post callbacks, false during Pre callbacks.</summary>
        public bool IsPost => post;
        /// <summary>Pre only: skips the original and every later Pre callback, and makes
        /// <paramref name="value"/> the result. Post callbacks still run. Returns false without a change in
        /// Post. Rolled back if the same callback then throws.</summary>
        public bool TryOverride(int value)
        {
            if (post || overridden) return false;
            result = value; overridden = true; return true;
        }
        /// <summary>Post only: replaces the result the native caller receives. The last successful
        /// replacement in callback order wins. Returns false without a change in Pre. Rolled back if the same
        /// callback then throws.</summary>
        public bool TrySetResult(int value)
        {
            if (!post) return false;
            result = value; return true;
        }
        internal void ClearOverride() { overridden = false; result = 0; }
        internal void CompleteOriginal(int value) { result = originalResult = value; originalCalled = true; }
        internal void EnterPost() => post = true;
        internal void RestoreResult(int value) => result = value;
    }

    /// <summary>Receives one completed call of native entry 0xD90D0 (Post only).</summary>
    /// <param name="call">The call's arguments, copied.</param>
    public delegate void TileRefreshHandler(TileRefreshCall call);

    /// <summary>One completed call of native entry 0xD90D0, the tile refresh helper both current consumers
    /// observe (LandscapeThemes records accepted terrain cells, BugfixesAndQoL invalidates its movement
    /// search data). Observed after the original returned; nothing can be changed.</summary>
    public readonly struct TileRefreshCall
    {
        internal TileRefreshCall(IntPtr manager, int tileY, int tileId) { Manager = manager; TileY = tileY; TileId = tileId; }
        /// <summary>First native argument (RCX).</summary>
        public IntPtr Manager { get; }
        /// <summary>Second native argument (EDX): the centre tile's Y coordinate.</summary>
        public int TileY { get; }
        /// <summary>Third native argument (R8D): the centre tile's ID.</summary>
        public int TileId { get; }
    }

    // Exact native ABIs of the two entries (Win64). The managed thunks and the trampolines use these.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int PclReachabilityNative(IntPtr manager, int player, int firstPcl, int secondPcl, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void TileRefreshNative(IntPtr manager, int tileY, int tileId);
}
