using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared
{
    /// <summary>A synchronous Assassin distance-field builder, using Vanilla's complete native arguments.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int AssassinPathBuilder(IntPtr context, int startX, int startY,
        int targetX, int targetY, int maximumNodes, int continuation);

    /// <summary>
    /// Process-wide owner of Assassin distance-field and reconstruction hooks.
    /// Consumers register behavior; no consumer installs a second hook at these sites.
    /// </summary>
    public static unsafe partial class AssassinPathAPI
    {
        internal const string ReferenceHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int BuildingGridRva = 0x4B6AA50;
        internal const int TileFlagsRva = 0x48F71B0;
        internal const int BuildingTypeBaseRva = 0x64CCCDE;
        internal const int BuildingAliveBaseRva = 0x64CCCDC;
        internal const int TileCount = 320800;
        internal const int BuildingStride = 0x32C;
        private static readonly object Sync = new object();
        private static IntPtr module;
        private static int memoryLength;
        private static ScanRegion region;
        private static ManualLogSource log;
        private static bool supported;
        private static HookTransaction transaction;
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static DetourHandle<AssassinPathBuilder> builderHook;
        private static HookHandle<X64InlineHook>[] endpointHooks;
        private static readonly IntPtr policyFlags = Marshal.AllocHGlobal(sizeof(int));
        private static AssassinPathBuilder weightedBuilder;
        private static string weightedOwner;
        private static string gateOwner;
        private static int flags;
        private static int callbackLogged;
        private static int builderFailureLogged;
        internal static readonly NativeDetourBackend Backend = new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });

        static AssassinPathAPI() { Marshal.WriteInt32(policyFlags, 0); }

        /// <summary>Whether the sole shared hook transaction has been published successfully.</summary>
        public static bool IsInstalled => publication.IsReady;
        /// <summary>Whether a test consumer currently enables direct gatehouse climb eligibility.</summary>
        public static bool DirectGatehouseClimbingEnabled => (Volatile.Read(ref flags) & 2) != 0;

        internal static void Initialize(IntPtr nativeModule, ReadOnlySpan<byte> memory,
            ScanRegion nativeRegion, string hash, ManualLogSource logger)
        {
            lock (Sync)
            {
                module = nativeModule;
                memoryLength = memory.Length;
                region = nativeRegion;
                supported = string.Equals(hash, ReferenceHash, StringComparison.OrdinalIgnoreCase);
                log = logger;
            }
        }

        /// <summary>
        /// Registers the sole weighted builder. The handler must call RunVanillaBuilder exactly
        /// once before any optional replacement, and handle its own correction/diagnostic errors.
        /// The delegate remains rooted until process exit.
        /// </summary>
        /// <remarks>The handler runs synchronously inside the native Assassin builder. An exception it
        /// throws never reaches native code: APIShared catches it, logs the first such failure of the
        /// process once, and answers the call with Vanilla's field, never a partially replaced one. If the
        /// handler had completed RunVanillaBuilder and left the field unchanged (the generation int at
        /// context+4 is the same), that result is returned and Vanilla does not run again. If Vanilla had
        /// not run, it runs once. If the handler had changed the field, Vanilla rebuilds it with a fresh
        /// search (continuation 0). A route the handler staged for the request is dropped. A handler that
        /// writes the field must advance the generation first, as Vanilla does.</remarks>
        public static void RegisterWeightedBuilder(string ownerGuid, AssassinPathBuilder handler)
        {
            if (string.IsNullOrEmpty(ownerGuid)) throw new ArgumentException("Owner GUID required.", nameof(ownerGuid));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (Sync)
            {
                if (weightedOwner != null && !string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal))
                    throw new InvalidOperationException("Assassin weighted builder already belongs to " + weightedOwner);
                EnsureInstalled();
                weightedOwner = ownerGuid;
                Volatile.Write(ref weightedBuilder, handler);
            }
        }
    }
}
