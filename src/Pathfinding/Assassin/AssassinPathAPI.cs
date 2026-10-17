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
    public static unsafe class AssassinPathAPI
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

        /// <summary>Runs Vanilla through the shared original trampoline; it never calls a registered replacement.</summary>
        public static int RunVanillaBuilder(IntPtr context, int startX, int startY,
            int targetX, int targetY, int maximumNodes, int continuation)
        {
            // The handle is published with its original before the patch is written (EnsureInstalled), so a
            // native call cannot outrun it; IsInstalled becomes true only after whole-feature validation.
            DetourHandle<AssassinPathBuilder> published = Volatile.Read(ref builderHook);
            if (published == null || !published.TryGetOriginal(out AssassinPathBuilder original))
                throw new InvalidOperationException("Shared Assassin path hooks are unavailable.");
            int result = original(context, startX, startY, targetX, targetY, maximumNodes, continuation);
            BuildFrame frame = buildFrame;
            if (frame != null && frame.Depth != 0 && frame.Context == context) frame.Complete(result, ReadGeneration(context));
            return result;
        }

        // Vanilla 0xD9C40 starts every new search (continuation == 0) by incrementing the int at context+4
        // (D9D00) and stamps each tile it writes with it (D9F47); a stamp equal to the generation marks a
        // valid distance. Past 32000 it resets the generation to 1 and clears the stamp array (D9D0C-D9D26).
        // A consumer that writes the field must advance the generation first, as Vanilla does.
        private const int GenerationOffset = 4;

        private static unsafe int ReadGeneration(IntPtr context) =>
            context == IntPtr.Zero ? 0 : *(int*)((byte*)context + GenerationOffset);

        // Per thread that runs Build (the native pathfinding caller): whether the handler of the current
        // Build call completed Vanilla, its result and the generation right after it. Nested Build calls
        // keep the outer call's record (Enter/Leave).
        private sealed class BuildFrame
        {
            internal IntPtr Context;
            internal int Depth, VanillaResult, VanillaGeneration;
            internal bool VanillaCompleted;

            internal readonly struct Saved
            {
                internal readonly IntPtr Context;
                internal readonly int VanillaResult, VanillaGeneration;
                internal readonly bool VanillaCompleted;
                internal Saved(BuildFrame frame)
                {
                    Context = frame.Context; VanillaResult = frame.VanillaResult;
                    VanillaGeneration = frame.VanillaGeneration; VanillaCompleted = frame.VanillaCompleted;
                }
            }

            internal Saved Enter(IntPtr context)
            {
                var outer = new Saved(this);
                Context = context; VanillaCompleted = false; Depth++;
                return outer;
            }

            internal void Leave(Saved outer)
            {
                Depth--;
                Context = outer.Context; VanillaResult = outer.VanillaResult;
                VanillaGeneration = outer.VanillaGeneration; VanillaCompleted = outer.VanillaCompleted;
            }

            internal void Complete(int result, int generation)
            {
                VanillaCompleted = true; VanillaResult = result; VanillaGeneration = generation;
            }
        }

        [ThreadStatic] private static BuildFrame buildFrame;

        // Cold path: the first Build on a thread.
        private static BuildFrame CreateFrame() => buildFrame = new BuildFrame();

        /// <summary>Publishes the weighted builder's existing reconstruction relaxation as logical data only.</summary>
        public static void SetWeightedReconstructionEnabled(string ownerGuid, bool enabled)
        {
            lock (Sync)
            {
                if (!string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only the registered weighted builder can change its reconstruction policy.");
                PublishFlags(enabled ? flags | 1 : flags & ~1);
            }
        }

        /// <summary>Enables or disables the registered experiment's gatehouse endpoint rule without repatching code.</summary>
        public static void SetDirectGatehouseClimbing(string ownerGuid, bool enabled)
        {
            if (string.IsNullOrEmpty(ownerGuid)) throw new ArgumentException("Owner GUID required.", nameof(ownerGuid));
            lock (Sync)
            {
                if (gateOwner != null && !string.Equals(gateOwner, ownerGuid, StringComparison.Ordinal))
                    throw new InvalidOperationException("Assassin gatehouse rule already belongs to " + gateOwner);
                EnsureInstalled();
                gateOwner = ownerGuid;
                PublishFlags(enabled ? flags | 2 : flags & ~2);
            }
        }

        /// <summary>
        /// Checks only the optional building-ID exception for one tile. Callers must still enforce
        /// Vanilla's direction, surface and wall requirements and their own player policy.
        /// </summary>
        public static bool IsDirectGatehouseClimbEndpoint(int tileId)
        {
            if (!IsInstalled || !DirectGatehouseClimbingEnabled || (uint)tileId >= TileCount) return false;
            ushort buildingId = ((ushort*)((byte*)module + BuildingGridRva))[tileId];
            if (buildingId == 0 || buildingId > 3999) return false;
            if ((((uint*)((byte*)module + TileFlagsRva))[tileId] & (uint)TilePropertyFlag.IsWall) == 0) return false;
            int record = buildingId * BuildingStride;
            short alive = *(short*)((byte*)module + BuildingAliveBaseRva + record);
            if (alive == (short)AliveState.None || alive == (short)AliveState.MarkedForDeletion) return false;
            eStructs type = *(eStructs*)((byte*)module + BuildingTypeBaseRva + record);
            return type == eStructs.STRUCT_GATE_MAIN || type == eStructs.STRUCT_GATE_INNER;
        }

        // TEMP_GATE_ROUTE_ACCEPTANCE: remove this native reader with the temporary observer.
        /// <summary>Reads the physical connection/surface branch only; never invokes a native step or changes state.</summary>
        public static AssassinTransitionKind ClassifyNativeTransition(int fromTile, int toTile, int direction, bool climbEnabled)
        {
            if (!supported || module == IntPtr.Zero || (uint)fromTile >= TileCount ||
                (uint)toTile >= TileCount || (uint)direction > 7) return AssassinTransitionKind.Unknown;
            byte* connections = (byte*)module + 0x51890D0;
            byte* masks = (byte*)module + 0x312620;
            uint* surfaces = (uint*)((byte*)module + TileFlagsRva);
            ushort* buildings = (ushort*)((byte*)module + BuildingGridRva);
            // The special-surface predicate is deliberately not called by a read-only observer.
            bool surfaceAccepted = (surfaces[toTile] & 0x4A5014B1u) == 0;
            bool endpointsAccepted = (buildings[fromTile] == 0 || IsDirectGatehouseClimbEndpoint(fromTile)) &&
                (buildings[toTile] == 0 || IsDirectGatehouseClimbEndpoint(toTile));
            return AssassinGateTransitionPolicy.Classify(direction, connections[fromTile], connections[toTile],
                masks[direction], masks[direction ^ 4], surfaces[fromTile], surfaces[toTile],
                surfaceAccepted, climbEnabled, endpointsAccepted);
        }

        private static void PublishFlags(int value)
        {
            Marshal.WriteInt32(policyFlags, value); // Aligned data publication; executable memory never changes.
            Volatile.Write(ref flags, value);
        }

        /// <summary>
        /// Stages an exact low-nibble-first route for the current owned single-unit builder.
        /// The synchronous validator is called again before publication; no route survives its frame.
        /// </summary>
        public static bool TryStageWeightedRoute(string ownerGuid, IntPtr context,
            int startX, int startY, int targetX, int targetY, int playerId,
            byte[] packedDirections, int directionCount, Func<bool> validate)
        {
            if (!string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal)) return false;
            return AssassinRouteHandoff.Stage(context, startX, startY, targetX, targetY,
                playerId, packedDirections, directionCount, validate);
        }

        /// <summary>TEMP_GATE_ROUTE_ACCEPTANCE: reason from the existing handoff guard; never probes identity.</summary>
        public static string TemporaryWeightedRequestReason => AssassinRouteHandoff.TemporaryRequestReason ?? "unknown";

        /// <summary>Returns the current owned unit's full control word and movement delay without scanning units.</summary>
        public static bool TryGetCurrentWeightedRequest(string ownerGuid, IntPtr context,
            int startX, int startY, int targetX, int targetY, out int controlPlayer, out int speedDelay)
        {
            controlPlayer = speedDelay = -1;
            return !string.IsNullOrEmpty(ownerGuid) &&
                string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal) &&
                AssassinRouteHandoff.TryResolve(context, startX, startY, targetX, targetY,
                    out controlPlayer, out speedDelay);
        }

        /// <summary>Stages a route and reports final validation/publication duration and success; observer errors are isolated.</summary>
        public static bool TryStageWeightedRoute(string ownerGuid, IntPtr context,
            int startX, int startY, int targetX, int targetY, int playerId,
            byte[] packedDirections, int directionCount, Func<bool> validate, Action<long, bool> completion)
        {
            if (string.IsNullOrEmpty(ownerGuid) || !string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal)) return false;
            return AssassinRouteHandoff.Stage(context, startX, startY, targetX, targetY,
                playerId, packedDirections, directionCount, validate, completion);
        }

        /// <summary>A single-unit builder is active; failed staging must preserve its native field.</summary>
        public static bool HasSingleUnitRoutePublication => AssassinRouteHandoff.HasFrame;

        private static int Build(IntPtr context, int startX, int startY, int targetX,
            int targetY, int maximumNodes, int continuation)
        {
            // Native boundary (issue #11): no managed exception may unwind into the native pathfinder, from
            // the handler, the first-call log or the frame setup. The success path adds only these protected
            // regions and the per-thread frame record; all failure work is in BuilderFailed.
            try
            {
                if (!publication.IsReady)
                    return RunVanillaBuilder(context, startX, startY, targetX, targetY, maximumNodes, continuation);
                if (Interlocked.Exchange(ref callbackLogged, 1) == 0)
                    NativeApiLog.Debug(log, "Assassin shared runtime hook confirmed after startup; " +
                        $"source=({startX},{startY}), target=({targetX},{targetY}), limit={maximumNodes}, continuation={continuation}.");
                BuildFrame frame = buildFrame ?? CreateFrame();
                BuildFrame.Saved outer = frame.Enter(context);
                try
                {
                    AssassinPathBuilder handler = Volatile.Read(ref weightedBuilder);
                    return handler == null
                        ? RunVanillaBuilder(context, startX, startY, targetX, targetY, maximumNodes, continuation)
                        : handler(context, startX, startY, targetX, targetY, maximumNodes, continuation);
                }
                catch (Exception error)
                {
                    return BuilderFailed(error, frame, context, startX, startY, targetX, targetY, maximumNodes, continuation);
                }
                finally { frame.Leave(outer); }
            }
            catch (Exception error)
            {
                return BuilderFailed(error, null, context, startX, startY, targetX, targetY, maximumNodes, continuation);
            }
        }

        // Cold path of Build (issue #36). Logs the first failure of the process, then answers this native call
        // without ever returning a partially replaced field:
        // - the handler completed Vanilla and the generation is unchanged since: Vanilla's field is intact, so
        //   its first result is returned and Vanilla does not run again;
        // - Vanilla did not run: it runs once, with the call's arguments;
        // - the handler changed the field after Vanilla: the field is rebuilt by a fresh search
        //   (continuation 0), because a resumed search cannot be replayed (Vanilla continues its queue in
        //   the context), while a fresh search from the same state rebuilds the same field.
        // A route the handler staged for this request is dropped, so the frame publishes none.
        // Besides the field, a Vanilla run only increments the generation and a per-run counter at
        // context+0xBC (D9D50) and, every 32000 searches, clears the stamp array; it draws no random
        // numbers and raises no events.
        private static int BuilderFailed(Exception error, BuildFrame frame, IntPtr context, int startX, int startY,
            int targetX, int targetY, int maximumNodes, int continuation)
        {
            if (Volatile.Read(ref builderFailureLogged) == 0 && Interlocked.Exchange(ref builderFailureLogged, 1) == 0)
            {
                try { NativeApiLog.Error(log, "Assassin path callback failed; Vanilla result returned. Later failures are not logged: " + error); }
                catch { }
            }
            try
            {
                AssassinRouteHandoff.DiscardStaged(context);
                bool completed = frame != null && frame.VanillaCompleted && frame.Context == context;
                if (completed && ReadGeneration(context) == frame.VanillaGeneration) return frame.VanillaResult;
                return RunVanillaBuilder(context, startX, startY, targetX, targetY, maximumNodes, completed ? 0 : continuation);
            }
            catch
            {
                return 0; // Last resort: Vanilla itself failed, and nothing may unwind into native code.
            }
        }

        private static void EnsureInstalled()
        {
            publication.ThrowIfUnavailable();
            if (IsInstalled) return;
            if (!supported || module == IntPtr.Zero || region == null)
                throw new InvalidOperationException("Shared Assassin hooks require the audited native hash and loaded library.");
            var memory = new ReadOnlySpan<byte>(module.ToPointer(), memoryLength);
            AssassinPathNativeDefinition.Validate(memory, unchecked((ulong)module.ToInt64()), log);
            NativeOwnershipRegistry sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(sites, ApiSharedNativeSites.AssassinPath, ApiSharedNativeSites.AssassinPathSpans(),
                reason => new InvalidOperationException(reason));
            var pendingBuilder = new DetourHandle<AssassinPathBuilder>();
            var pendingEndpoints = new HookHandle<X64InlineHook>[4];
            HookTransaction pending = null;
            try
            {
                // InstallOnCommit = false: builderHook is published with its original before any patch
                // is written, so Build never runs before RunVanillaBuilder can (NativeHookPublication).
                pending = new HookTransaction(region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow,
                        OwnsHooks = true, InstallOnCommit = false, Backend = Backend });
                ulong nativeBase = unchecked((ulong)module.ToInt64());
                for (int index = 0; index < pendingEndpoints.Length; index++)
                {
                    int site = index;
                    pendingEndpoints[index] = new HookHandle<X64InlineHook>();
                    pending.AddInline(pendingEndpoints[index],
                        HookTarget.FromAddress(nativeBase + (uint)AssassinPathNativeDefinition.Sites[index]),
                        (assembler, instructions, returnAddress) => AssassinEndpointEmitter.Emit(
                            assembler, instructions, nativeBase, unchecked((ulong)policyFlags.ToInt64()), site),
                        hookSize: 14);
                }
                pending.AddDetour(pendingBuilder, HookTarget.FromAddress(nativeBase + 0xD9C40), (AssassinPathBuilder)Build);
                CommitResult result = pending.Commit();
                if (!result.IsCompleteSuccess || !pendingBuilder.Success)
                    throw new InvalidOperationException("Shared Assassin hook transaction failed.");
                for (int index = 0; index < pendingEndpoints.Length; index++)
                    if (!pendingEndpoints[index].Success || pendingEndpoints[index].IsInstalled ||
                        pendingEndpoints[index].Hook.DisplacedByteCount != AssassinPathNativeDefinition.Lengths[index])
                        throw new InvalidOperationException("Prepared Assassin endpoint displacement mismatch at site " + index);
                Volatile.Write(ref builderHook, pendingBuilder);
                transaction = pending;
                endpointHooks = pendingEndpoints;
                NativeHookPublication.Enable(pending, result, publication, null, () =>
                {
                    AssassinPathNativeDefinition.ValidateInstalledDetour(pendingBuilder.Hook, nativeBase);
                    for (int index = 0; index < pendingEndpoints.Length; index++)
                        if (!pendingEndpoints[index].Success || !pendingEndpoints[index].IsInstalled ||
                            pendingEndpoints[index].Hook.DisplacedByteCount != AssassinPathNativeDefinition.Lengths[index])
                            throw new InvalidOperationException("Shared Assassin endpoint displacement mismatch at site " + index);
                });
            }
            catch (Exception ex)
            {
                publication.RollbackUnpublished(pending, () =>
                {
                    Volatile.Write(ref builderHook, null);
                    transaction = null; endpointHooks = null;
                    ApiSharedNativeSites.Release(sites, ApiSharedNativeSites.AssassinPath);
                });
                try { NativeApiLog.Error(log, "Shared Assassin hooks unavailable; Vanilla retained: " + ex); } catch { }
                throw;
            }
            try { NativeApiLog.Debug(log, "Assassin shared hooks installed; owner=APIShared, builder=0xD9C40, " +
                "scheme=Indirect/displaced=10, endpoints=0xD9F0C/16,0xD9F1C/15,0xE19D8/18,0xE19F9/23."); } catch { }
        }
    }
}
