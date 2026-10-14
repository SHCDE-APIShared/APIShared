using BepInEx.Logging;
using SHCDESE.GameGlobals;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Threading;

namespace APIShared.ScriptExtenderReplacements
{
    // Immutable publication keeps all methods fail-closed until the complete address contract is known.
    internal sealed class PathfindingTableState
    {
        internal const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int ProfileRva = 0x322540;
        internal const int PermissionRva = 0x32BDB0;
        internal const int ConnectionClassCount = 6;
        internal const int PermissionCount = 90 * ConnectionClassCount;
        internal readonly IntPtr Profiles, Permissions;
        internal readonly string Reason;
        internal bool IsAvailable => Profiles != IntPtr.Zero && Permissions != IntPtr.Zero;

        private PathfindingTableState(IntPtr profiles, IntPtr permissions, string reason)
        { Profiles = profiles; Permissions = permissions; Reason = reason; }

        internal static PathfindingTableState Unavailable(string reason) =>
            new PathfindingTableState(IntPtr.Zero, IntPtr.Zero, reason);

        // Validation never dereferences an untrusted pointer or compares mutable table contents.
        // Native 181E00 uses (class * 0x5A + type) * 4 from class-zero RVA 32BC48.
        // Profile readers 18E1E0, 196280 and 198ED0 index Int32s from RVA 322540.
        internal static PathfindingTableState Validate(IntPtr module, int imageLength, string hash,
            ulong extenderProfileRva, ulong extenderPermissionVa)
        {
            if (!string.Equals(hash, NativeHash, StringComparison.OrdinalIgnoreCase))
                return Unavailable("Unsupported CrusaderDE.dll SHA-256.");
            if (IntPtr.Size != 8 || module.ToInt64() <= 0 ||
                !HasSupportedEnumContract(typeof(eChimps), typeof(PathConnectionClass), typeof(PathfindingProfile)))
                return Unavailable("Unsupported process, module base or Script Extender enum contract.");
            if (imageLength < ProfileRva + 90 * sizeof(int) ||
                imageLength < PermissionRva + PermissionCount * sizeof(int) ||
                module.ToInt64() > long.MaxValue - imageLength)
                return Unavailable("Native pathfinding tables are outside the loaded module view.");
            long profile = module.ToInt64() + ProfileRva;
            long permissions = module.ToInt64() + PermissionRva;
            if (extenderProfileRva != ProfileRva || extenderPermissionVa != (ulong)permissions)
                return Unavailable("Script Extender table addresses disagree with the audited native image.");
            return new PathfindingTableState(new IntPtr(profile), new IntPtr(permissions), string.Empty);
        }

        internal static bool HasSupportedEnumContract(Type unitTypes, Type connectionClasses, Type profiles) =>
            // Read loaded metadata, including storage width: sizeof(int) and Span<Profile>
            // must describe the same native element. SE constants are otherwise inlined at compile time.
            HasUnderlyingType(unitTypes, typeof(ushort)) &&
            HasUnderlyingType(connectionClasses, typeof(int)) &&
            HasUnderlyingType(profiles, typeof(int)) &&
            HasNamedValue(unitTypes, 89, nameof(eChimps.CHIMP_NUM_TYPES)) &&
            HasNamedValue(connectionClasses, 1, nameof(PathConnectionClass.LadderClimb)) &&
            HasNamedValue(connectionClasses, 2, nameof(PathConnectionClass.Connection2)) &&
            HasNamedValue(connectionClasses, 3, nameof(PathConnectionClass.GatehouseBig)) &&
            HasNamedValue(connectionClasses, 4, nameof(PathConnectionClass.GatehouseSmall)) &&
            HasNamedValue(connectionClasses, 5, nameof(PathConnectionClass.Connection5)) &&
            HasNamedValue(connectionClasses, 6, nameof(PathConnectionClass.Connection6)) &&
            HasNamedValue(profiles, 0, nameof(PathfindingProfile.Large)) &&
            HasNamedValue(profiles, 1, nameof(PathfindingProfile.Default));

        private static bool HasUnderlyingType(Type type, Type underlying) =>
            type != null && type.IsEnum && Enum.GetUnderlyingType(type) == underlying;

        private static bool HasNamedValue(Type type, int value, string name) =>
            Enum.GetName(type, Enum.ToObject(type, value)) == name;
    }

    internal static class PathfindingTables
    {
        private static int initialized;

        // Called once by the durable LibraryLoaded publisher, before APIShared readiness publication.
        internal static void Initialize(IntPtr module, int imageLength, string hash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0) return;
            PathfindingTableState state;
            try
            {
                // Unknown images need not instantiate any lazy SE manager.
                if (!string.Equals(hash, PathfindingTableState.NativeHash, StringComparison.OrdinalIgnoreCase))
                    state = PathfindingTableState.Unavailable("Unsupported CrusaderDE.dll SHA-256.");
                else
                {
                    var globals = GameGlobalsManager.Instance;
                    state = PathfindingTableState.Validate(module, imageLength, hash,
                        globals.PathfindingProfilesUnitTableRVA, globals.PathfindingConnectionClassesUnitTableVA);
                }
            }
            catch (Exception error)
            {
                state = PathfindingTableState.Unavailable("Table validation failed: " + error.GetType().Name + ": " + error.Message);
            }
            GamePathingManagerAPI.Instance.Publish(state);
            if (state.IsAvailable)
                NativeApiLog.Info(log, "SE_PATHFINDING_REPLACEMENT available: profiles=90, permissions=540, rowStride=90, validUnitTypes=0..88; no hooks installed.");
            else
                NativeApiLog.Warning(log, "SE_PATHFINDING_REPLACEMENT unavailable: " + state.Reason + " Other APIShared services remain independent.");
        }
    }
}
