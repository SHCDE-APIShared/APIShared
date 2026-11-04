using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using SHCDESE.API.LowLevel;

namespace APIShared.Simulation
{
    /// <summary>Read-only access to the actual, save-preserved native peace-time state.</summary>
    public static class PeaceTimeState
    {
        private const string DiagnosticId = NativeCapabilityIds.PeaceTimeState;
        internal const int FlagAnchorRva = 0xCA874, FlagRva = 0x38722DC;
        private static int initializationAttempted;
        private static PeaceTimeReader reader;
        private static NativeCapabilityDiagnostic diagnostic = new NativeCapabilityDiagnostic(
            DiagnosticId, NativeCapabilityState.Pending, "", "Peace-time source preparation has not run.");

        /// <summary>Availability of the native source, independent of whether a ready mission currently exists.</summary>
        public static NativeCapabilityDiagnostic Diagnostic => Volatile.Read(ref diagnostic);

        /// <summary>Reads the current native peace flag in a fully started mission, including the editor.</summary>
        /// <remarks>The return value means availability; false output on a successful read means inactive peace.
        /// The source is not configured minutes, a reconstructed timer or UI text. Menu, pending/failed load,
        /// unload or a world change during the read returns false. Read only inside an existing native query/
        /// simulation context, or while the engine is stopped/locked. This getter acquires no engine lock,
        /// dispatches no threads, installs no hook and activates no timer/event subscription.</remarks>
        public static bool TryGetActive(out bool active)
        {
            active = false;
            PeaceTimeReader current = Volatile.Read(ref reader);
            return current != null && current.TryRead(out active);
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initializationAttempted, 1, 0) != 0) return;
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref diagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.UnsupportedBuild, hash, "The native peace-state reader requires the audited build."));
                NativeApiLog.Error(log, "Peace-time source unavailable for native build " + hash + "; peace-dependent consumers must retain their ordinary behavior.");
                return;
            }
            try
            {
                int offset = ValidateAnchor(context.Memory);
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var missions, out var failure))
                    throw new InvalidOperationException("Peace-time state needs mission lifecycle: " + failure?.Reason);
                IntPtr flag = new IntPtr(checked(context.ModuleHandle.ToInt64() + offset));
                var source = new PeaceTimeReader(() => missions.Current, () => MissionLifecycleService.NativeClock,
                    () => Marshal.ReadByte(flag));
                Volatile.Write(ref reader, source);
                Volatile.Write(ref diagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.Available, hash, "Native active-flag read validated from the peace updater."));
                NativeApiLog.Info(log, "Peace-time source prepared: method=reference-rva; anchor=0xCA874; flag=0x38722DC; native mission state, no timer or hook.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref diagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.ValidationFailed, hash, error.Message));
                NativeApiLog.Error(log, "Peace-time source unavailable; consumers must treat Peace Time as unknown: " + error);
            }
        }

        internal static int ValidateAnchor(ReadOnlySpan<byte> image)
        {
            // cmp byte ptr [rip+disp32],0, untouched by the SP peace consumer patches.
            byte[] expected = { 0x80, 0x3D, 0x61, 0x7A, 0x7A, 0x03, 0x00 };
            if (image.Length <= FlagRva || image.Length < FlagAnchorRva + expected.Length)
                throw new InvalidOperationException("Peace-time source lies outside the native image.");
            var anchor = image.Slice(FlagAnchorRva, expected.Length);
            for (int i = 0; i < expected.Length; i++)
                if (anchor[i] != expected[i]) throw new InvalidOperationException("The native peace-flag anchor changed.");
            int displacement = anchor[2] | anchor[3] << 8 | anchor[4] << 16 | anchor[5] << 24;
            int target = checked(FlagAnchorRva + expected.Length + displacement);
            if (target != FlagRva) throw new InvalidOperationException("The native peace-flag target changed.");
            return target;
        }
    }

    // Only native memory and lifecycle observations are replaceable boundaries in tests.
    // Clock identity changes before load/unload callbacks and across native-world replacement.
    internal sealed class PeaceTimeReader
    {
        private readonly Func<MissionContext> current;
        private readonly Func<MissionLifecycleState.NativeClockState> clock;
        private readonly Func<byte> read;
        internal PeaceTimeReader(Func<MissionContext> current,
            Func<MissionLifecycleState.NativeClockState> clock, Func<byte> read)
        { this.current = current; this.clock = clock; this.read = read; }
        internal bool TryRead(out bool active)
        {
            active = false;
            MissionContext mission = current();
            var native = clock();
            if (mission == null || native == null || native.Context.SessionId != mission.SessionId) return false;
            byte value;
            try { value = read(); } catch { return false; }
            if (!ReferenceEquals(mission, current()) || !ReferenceEquals(native, clock())) return false;
            active = value != 0;
            return true;
        }
    }
}
