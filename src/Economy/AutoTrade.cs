using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.NativeSites;
using BepInEx.Logging;
using SHCDESE.API.LowLevel;

namespace APIShared.Economy
{
    /// <summary>Reads the confirmed Vanilla AutoTrade option for the current mission.</summary>
    public static class AutoTrade
    {
        internal const int GateRva = 0xD03BD, ModeRva = 0x8574B90, PermissionRva = 0x87ECAC0;
        private static readonly byte[] GateBytes = {
            0x83, 0x3D, 0xCC, 0x47, 0x4A, 0x08, 0x00, 0x74, 0x0D,
            0x83, 0x3D, 0xF3, 0xC6, 0x71, 0x08, 0x00, 0x0F, 0x84, 0x18, 0x01, 0x00, 0x00
        };
        private static NativeOption native;
        private static Decision current;

        /// <summary>
        /// Returns true when the confirmed mission option is available. On success, <paramref name="allowed"/>
        /// is the actual Vanilla option, independent of a player's per-good settings or market availability.
        /// Returns false with allowed=false before mission readiness, outside parties with an applicable option,
        /// on unsupported builds, or when a legacy save has no confirmed option. AutoTrade producers should
        /// proceed only if both results are true. This query does not issue a command or change the setting.
        /// </summary>
        public static bool TryGetPermission(out bool allowed)
        {
            allowed = false;
            var context = MissionLifecycleService.ActiveContext;
            var clock = MissionLifecycleService.NativeClock;
            var decision = Volatile.Read(ref current);
            var source = Volatile.Read(ref native);
            if (source == null || decision == null || context == null || clock == null ||
                !MissionLifecycleService.HasStartedMission || decision.Session != context.SessionId ||
                clock.Context.SessionId != context.SessionId || Marshal.ReadInt32(source.Mode) != decision.Mode ||
                source.ReadPermission() != decision.Permission)
                return false;
            if (!ReferenceEquals(context, MissionLifecycleService.ActiveContext) ||
                !ReferenceEquals(clock, MissionLifecycleService.NativeClock))
                return false;
            allowed = decision.Permission;
            return true;
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (context.ModuleHandle == IntPtr.Zero || context.Memory.Length < PermissionRva + sizeof(int) ||
                    !MatchesGate(context.Memory.Slice(GateRva, GateBytes.Length)))
                    throw new InvalidOperationException("The Vanilla AutoTrade gate could not be validated.");
                NativeIndirectEntry.VerifyBytes(IntPtr.Add(context.ModuleHandle, GateRva), GateBytes);
                if (!AutoTradeArchive.TryCreate(Capture, out var archive, out string reason))
                    throw new InvalidOperationException(reason);
                Volatile.Write(ref native, new NativeOption(context.ModuleHandle, archive, log));
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Vanilla AutoTrade option service unavailable: " + error.Message);
            }
        }

        private static bool? Capture() => TryGetPermission(out bool allowed) ? (bool?)allowed : null;

        internal static void PrepareLegacy(long session, string path) =>
            Volatile.Read(ref native)?.Archive.PrepareLegacy(session, path);

        // Invoked only for a successful, still-pending mission, under EngineInterface.threadLock.
        internal static void CompleteNativeLoad(long session, MissionStartKind kind, string path, bool multiplayer)
        {
            var source = Volatile.Read(ref native);
            if (source == null) return;
            try
            {
                int mode = Marshal.ReadInt32(source.Mode);
                bool? archived = kind == MissionStartKind.LoadedSave && IsSetupMode(mode)
                    ? source.Archive.Resolve(session, path, multiplayer)
                    : null;
                if (!TrySelectPermission(kind, mode, source.ReadPermission(), archived, out bool permission))
                {
                    Volatile.Write(ref current, null);
                    return;
                }
                NativeIndirectEntry.VerifyBytes(source.Gate, GateBytes);
                if (kind == MissionStartKind.LoadedSave && source.ReadPermission() != permission)
                    Marshal.WriteInt32(source.Permission, permission ? 1 : 0);
                Volatile.Write(ref current, new Decision(session, mode, permission));
            }
            catch (Exception error)
            {
                Volatile.Write(ref current, null);
                NativeApiLog.Error(source.Log, "Vanilla AutoTrade option could not be resolved for the loaded mission: " + error.Message);
            }
        }

        // Vanilla's native modes 1 and 99 both apply the confirmed setup option.
        private static bool IsSetupMode(int mode) => mode == 1 || mode == 99;

        internal static bool TrySelectPermission(MissionStartKind kind, int mode, bool nativeValue, bool? archived,
            out bool permission)
        {
            permission = false;
            if (!IsSetupMode(mode)) return false;
            if (kind == MissionStartKind.NewGame)
            {
                permission = nativeValue;
                return true;
            }
            if (kind != MissionStartKind.LoadedSave || !archived.HasValue) return false;
            permission = archived.Value;
            return true;
        }

        internal static bool MatchesGate(ReadOnlySpan<byte> bytes) => bytes.SequenceEqual(GateBytes);

        private sealed class NativeOption
        {
            internal readonly IntPtr Gate, Mode, Permission;
            internal readonly AutoTradeArchive Archive;
            internal readonly ManualLogSource Log;
            internal NativeOption(IntPtr module, AutoTradeArchive archive, ManualLogSource log)
            {
                Gate = IntPtr.Add(module, GateRva);
                Mode = IntPtr.Add(module, ModeRva);
                Permission = IntPtr.Add(module, PermissionRva);
                Archive = archive;
                Log = log;
            }

            internal bool ReadPermission() => Marshal.ReadInt32(Permission) != 0;
        }

        private sealed class Decision
        {
            internal readonly long Session;
            internal readonly int Mode;
            internal readonly bool Permission;
            internal Decision(long session, int mode, bool permission)
            { Session = session; Mode = mode; Permission = permission; }
        }
    }
}
