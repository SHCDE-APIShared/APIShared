using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Sight
{
    // Native side of LineOfSight (static audit, hash FBCB9319, 2026-11-14; see UpdateToNewDLL.md "Line of sight").
    // Calls Vanilla's sight wrapper 0xA06F0 and core 0x9E350 through their live entries; installs nothing.
    // Initialized once from APIShared's LibraryLoaded native steps and rooted statically for the process.
    internal static unsafe class LineOfSightNative
    {
        internal const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const string SiteGroup = "Sight.LineOfSightCalls";
        internal const int WrapperRva = 0xA06F0, CoreRva = 0x9E350;
        internal const int QueryCallRva = 0x18B0A2, AttackCallRva = 0x18ED6A;

        // The complete wrapper (156 bytes): forward core(context, start, end, 0); when that returns 0, the reverse
        // core(context, end, start, 0) and its result. Both relative calls resolve to 0x9E350.
        internal static readonly byte[] WrapperBytes = NativeInlineStub.Hex(
            "488BC4488958084889681048897018488978204154415641574883EC40448BB42490000000418BD9448BBC2488000000418BF8" +
            "448BA424800000008BF2C740E000000000488BE9448970D8448978D0448960C8E808DCFFFF85C0752189442438458BCE895C2430" +
            "458BC7897C2428418BD4488BCD89742420E8E3DBFFFF488B5C2460488B6C2468488B742470488B7C2478" +
            "4883C440415F415E415CC3");

        // Core entry: spills of context, start X and start Y, eight pushes, sub rsp,0x68. Its only memory write
        // besides its own stack is [context+0xC]; its eighth argument (stack, read at 0x9E4AE and later) switches
        // every obstacle check off when it is not zero.
        internal static readonly byte[] CoreEntryBytes = NativeInlineStub.Hex(
            "44894424188954241048894C24085355565741544155415641574883EC68");

        // The two Hunter callers that pin the result rules and the eye heights of LineOfSight's constants:
        // target search: add ecx,0x1A (target) at 0x18B052, add ecx,0x1E (viewer) at 0x18B070, call wrapper,
        // dec eax; cmp eax,0x1AF; ja (visible 1..0x1B0);
        // ranged attack order: mov [rsp+0x50],0x1E (viewer default) at 0x18E9DE, add ecx,0x1A (target) at 0x18ED3C,
        // call wrapper; mov edx,eax; test eax,eax; jle (visible above 0).
        private static readonly (int Rva, byte[] Bytes)[] RuleAnchors =
        {
            (0x18B052, NativeInlineStub.Hex("83C11A")),
            (0x18B070, NativeInlineStub.Hex("83C11E")),
            (QueryCallRva, NativeInlineStub.Hex("E84956F1FFFFC83DAF010000771F")),
            (0x18E9DE, NativeInlineStub.Hex("C74424501E000000")),
            (0x18ED3C, NativeInlineStub.Hex("83C11A")),
            (AttackCallRva, NativeInlineStub.Hex("E88119F1FF8BD085C00F8EEB000000"))
        };

        // int core(context, startX, startY, startHeight, endX, endY, endHeight, noObstacles); the wrapper takes the
        // first seven. Both are Microsoft x64 functions; the calling convention attribute is ignored on x64.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WrapperCall(IntPtr context, int startX, int startY, int startHeight, int endX, int endY, int endHeight);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CoreCall(IntPtr context, int startX, int startY, int startHeight, int endX, int endY, int endHeight,
            int noObstacles);

        // Private context: 16 bytes surrounded by canaries; Vanilla may write only context+0xC.
        private const int ContextGuardBefore0 = 0x13579BDF, ContextGuardBefore1 = 0x2468ACE0;
        private const int ContextGuardAfter0 = 0x55AA33CC, ContextGuardAfter1 = unchecked((int)0xAA55CC33);

        private static ManualLogSource log;
        private static WrapperCall wrapper;
        private static CoreCall core;
        private static int available;
        private static string unavailable = "The line-of-sight service has not been initialized.";
        private static int violationLogged;
        [ThreadStatic] private static int depth;

        internal static bool IsAvailable => Volatile.Read(ref available) != 0;
        internal static string UnavailableReason => IsAvailable ? "" : Volatile.Read(ref unavailable);

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (IsAvailable) return;
            log = logger;
            try
            {
                if (!TryValidate(context, hash, out string invalid))
                    throw new InvalidOperationException(invalid);
                if (IntPtr.Size != 8)
                    throw new PlatformNotSupportedException("Line of sight requires the 64-bit game process.");
                // Calls only: the wrapper and core are entered through their live entries, whatever hook runs there.
                ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteGroup, Spans(),
                    reason => new InvalidOperationException(reason));
                IntPtr image = context.ModuleHandle;
                wrapper = Marshal.GetDelegateForFunctionPointer<WrapperCall>(image + WrapperRva);
                core = Marshal.GetDelegateForFunctionPointer<CoreCall>(image + CoreRva);
                Volatile.Write(ref available, 1);
                NativeApiLog.Info(log, "Line of sight ready: wrapper 0x" + WrapperRva.ToString("X") + ", core 0x" + CoreRva.ToString("X") +
                    ", private 16-byte context with guards, mode=reference-rva (audited image only).");
            }
            catch (Exception error)
            {
                wrapper = null;
                core = null;
                Volatile.Write(ref unavailable, "Line of sight is unavailable: " + error.Message);
                NativeApiLog.Error(log, "Line of sight unavailable; queries fail closed: " + error.Message);
            }
        }

        internal static NativeSiteSpan[] Spans() => new[]
        {
            new NativeSiteSpan(WrapperRva, 1, NativeSiteMode.CallOnly),
            new NativeSiteSpan(CoreRva, 1, NativeSiteMode.CallOnly)
        };

        // Load-image checks only: the calls accept whatever detour a live entry carries, as every native caller does.
        internal static bool TryValidate(CrusaderLibraryLoadContext context, string hash, out string reason)
        {
            if (!string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
            { reason = "Line of sight is audited only for native image " + AuditedHash + "."; return false; }
            if (context == null || context.ModuleHandle == IntPtr.Zero)
            { reason = "The native load image is unavailable."; return false; }
            ReadOnlySpan<byte> memory = context.Memory;
            if (!NativeInlineStub.SnapshotMatches(memory, WrapperRva, WrapperBytes))
            { reason = "The sight wrapper at RVA 0x" + WrapperRva.ToString("X") + " differs from the audit."; return false; }
            if (!NativeInlineStub.SnapshotMatches(memory, CoreRva, CoreEntryBytes))
            { reason = "The sight core entry at RVA 0x" + CoreRva.ToString("X") + " differs from the audit."; return false; }
            foreach (var (rva, bytes) in RuleAnchors)
                if (!NativeInlineStub.SnapshotMatches(memory, rva, bytes))
                { reason = "The sight rule instruction at RVA 0x" + rva.ToString("X") + " differs from the audit."; return false; }
            if (!TryValidateUnitLayout(out reason)) return false;
            reason = "";
            return true;
        }

        // The Extender's GameUnit (record origin 0x65C) against the raw fields both Hunter callers read:
        // +0x70E/+0x710 world X/Y, +0x712 height, +0x714 height offset, 0x490-byte records.
        internal static bool TryValidateUnitLayout(out string reason)
        {
            if (sizeof(GameUnit) != 0x490 ||
                Offset(nameof(GameUnit.r_CurrentWorldPositionX)) != 0xB2 || Offset(nameof(GameUnit.r_CurrentWorldPositionY)) != 0xB4 ||
                Offset(nameof(GameUnit.r_HeightElevation)) != 0xB6 || Offset(nameof(GameUnit.N0000006A)) != 0xB8)
            { reason = "The Extender's GameUnit position and height fields differ from the audited raw unit record."; return false; }
            reason = "";
            return true;
        }

        private static int Offset(string field) => Marshal.OffsetOf(typeof(GameUnit), field).ToInt32();

        internal static bool TryEvaluate(SightPoint viewer, SightPoint target, LineOfSightRoutine routine,
            out int result, out LineOfSightFailure failure)
        {
            result = 0;
            if (!IsAvailable) { failure = LineOfSightFailure.Unavailable; return false; }
            if (routine != LineOfSightRoutine.Bidirectional && routine != LineOfSightRoutine.Directional)
            { failure = LineOfSightFailure.InvalidRoutine; return false; }
            if (!IsHeightInRange(viewer.Height) || !IsHeightInRange(target.Height))
            { failure = LineOfSightFailure.HeightOutOfRange; return false; }
            if (!IsInsideMap(viewer) || !IsInsideMap(target))
            { failure = LineOfSightFailure.OutsideMap; return false; }
            if (depth != 0) { failure = LineOfSightFailure.Reentrant; return false; }

            depth = 1;
            try
            {
                int* guarded = stackalloc int[8];
                guarded[0] = ContextGuardBefore0;
                guarded[1] = ContextGuardBefore1;
                guarded[2] = 0;
                guarded[3] = 0;
                guarded[4] = 0;
                guarded[5] = 0;
                guarded[6] = ContextGuardAfter0;
                guarded[7] = ContextGuardAfter1;
                var context = (IntPtr)(guarded + 2);
                result = routine == LineOfSightRoutine.Bidirectional
                    ? wrapper(context, viewer.X, viewer.Y, viewer.Height, target.X, target.Y, target.Height)
                    // The eighth argument is Vanilla's own value: the wrapper's first core call passes 0.
                    : core(context, viewer.X, viewer.Y, viewer.Height, target.X, target.Y, target.Height, 0);
                // Only context+0xC (guarded[5]) may change.
                if (guarded[0] != ContextGuardBefore0 || guarded[1] != ContextGuardBefore1 || guarded[2] != 0 ||
                    guarded[3] != 0 || guarded[4] != 0 || guarded[6] != ContextGuardAfter0 || guarded[7] != ContextGuardAfter1)
                {
                    DisableAfterViolation();
                    result = 0;
                    failure = LineOfSightFailure.NativeContractViolation;
                    return false;
                }

                failure = LineOfSightFailure.None;
                return true;
            }
            finally
            {
                depth = 0;
            }
        }

        private static bool IsHeightInRange(int height) =>
            height >= -LineOfSight.MaximumHeightMagnitude && height <= LineOfSight.MaximumHeightMagnitude;

        // Vanilla indexes its tile grids with X / 8 and the row table with Y / 8 without bounds checks. A straight
        // trace between two tiles of the convex map diamond stays inside the 800 by 800 grids Vanilla allocates.
        private static bool IsInsideMap(SightPoint point)
        {
            if (point.X < 0 || point.Y < 0)
                return false;
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            return tiles != null && tiles.IsTileInsideMapBounds(point.TileX, point.TileY);
        }

        private static void DisableAfterViolation()
        {
            Volatile.Write(ref unavailable, "Line of sight disabled itself: Vanilla wrote outside the private context slot +0xC.");
            Volatile.Write(ref available, 0);
            if (Interlocked.Exchange(ref violationLogged, 1) == 0)
                NativeApiLog.Error(log, "Line of sight disabled for the process: the native sight routine wrote outside context+0xC; " +
                    "no game state was changed by APIShared.");
        }
    }
}
