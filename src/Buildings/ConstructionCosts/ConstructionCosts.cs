using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using APIShared.Internal;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>Checked opt-in construction-cost access for structures and build-menu targets.</summary>
    /// <remarks>Use after APIShared native initialization. No SE methods are patched. Reads return snapshots;
    /// callers own the game's existing thread/simulation synchronization. Mutations affect every player;
    /// perform them only between missions or under the engine monitor. Writers have last-write semantics,
    /// not owner composition. A successful write updates both native and existing visual rows.
    /// The display query is Unity-only and includes Vanilla's current free-building rules.</remarks>
    public static class ConstructionCosts
    {
        private static ConstructionCostTables tables;
        private static string unavailableReason = "Construction cost tables have not completed native initialization.";
        private static int initialized;
        // Hash-bound layout: C33A0 copies 110 * 20 bytes from 2DE240; C8A90 reads the live
        // interleaved records at 67E5B8C. The 109-row managed table has a distinct checked length.
        internal const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int NativeRowCount = 110;
        internal const int DefaultRva = 0x2DE240;
        internal const int CurrentRva = 0x67E5B8C;

        /// <summary>Whether native and managed table contracts passed validation.</summary>
        public static bool IsAvailable => Volatile.Read(ref tables) != null;
        /// <summary>Why access is unavailable; empty when available.</summary>
        public static string UnavailableReason => IsAvailable ? string.Empty : unavailableReason;

        /// <summary>Reads the current default-table values, without mission-specific free-building rules.</summary>
        public static bool TryGetDefault(ConstructionCostTarget target, out ConstructionCostValues values, out string reason) =>
            TryRead(target, true, out values, out reason);
        /// <summary>Reads live base costs, without mission-specific free-building rules.</summary>
        public static bool TryGetCurrent(ConstructionCostTarget target, out ConstructionCostValues values, out string reason) =>
            TryRead(target, false, out values, out reason);

        /// <summary>Changes one nonnegative native material cost in both native and visual tables.</summary>
        /// <returns>False without mutation for unsupported target/good, negative amount, unavailable service,
        /// or a target outside either actual table. -1 is a consumer setting sentinel, not a native cost.</returns>
        public static bool TrySetCurrent(ConstructionCostTarget target, eGoods good, int amount, out string reason)
        {
            if (!target.SupportsVanillaCosts) { reason = "The target has no supported Vanilla construction-cost row."; return false; }
            int material = ConstructionCostValues.MaterialIndex(good);
            if (material < 0 || amount < 0) { reason = "A native construction material and nonnegative amount are required."; return false; }
            var current = Volatile.Read(ref tables);
            if (current == null) { reason = unavailableReason; return false; }
            return current.TryWrite(target.Row, material, amount, out reason);
        }

        /// <summary>Reads the cost shown by Vanilla for this target in the local current mission.</summary>
        /// <returns>False if unavailable, unsupported, off the Unity thread, or no game state exists.</returns>
        public static bool TryGetDisplayCosts(ConstructionCostTarget target, out ConstructionCostValues values, out string reason)
        {
            values = default;
            if (!target.SupportsVanillaCosts) { reason = "The target has no supported Vanilla construction-cost row."; return false; }
            var current = Volatile.Read(ref tables);
            if (current == null) { reason = unavailableReason; return false; }
            if (!UnityMainThreadDispatch.IsMainThread) { reason = "Display costs require Unity's main thread."; return false; }
            if (!current.HasVisualRow(target.Row)) { reason = "The target has no Vanilla visual cost row."; return false; }
            var state = GameData.Instance.lastGameState;
            if (state == null) { reason = "No game state is available."; return false; }
            if (GameModes.GameModeHelper.IsMapEditor() && state.game_type != (int)Enums.eGameTypeModes.GAMETYPE_SIEGE_THAT_BUILDER)
            { reason = string.Empty; return true; }
            int wood = 0, stone = 0, iron = 0, pitch = 0, gold = 0;
            GameData.getStructureCosts(target.Row, ref wood, ref stone, ref iron, ref pitch, ref gold);
            values = new ConstructionCostValues(wood, stone, iron, pitch, gold);
            reason = string.Empty;
            return true;
        }

        private static bool TryRead(ConstructionCostTarget target, bool defaults, out ConstructionCostValues values, out string reason)
        {
            values = default;
            if (!target.SupportsVanillaCosts) { reason = "The target has no supported Vanilla construction-cost row."; return false; }
            var current = Volatile.Read(ref tables);
            if (current == null) { reason = unavailableReason; return false; }
            return current.TryRead(target.Row, defaults, out values, out reason);
        }

        internal static void Initialize(IntPtr module, ReadOnlySpan<byte> image, string hash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0) return;
            try
            {
                if (!string.Equals(hash, NativeHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsupported native hash; cost-table bounds are not verified.");
                if (module == IntPtr.Zero || image.Length < CurrentRva + NativeRowCount * 20 ||
                    (int)eStructs.STRUCT_MAX != NativeRowCount)
                    throw new InvalidOperationException("Native image/table capacity differs from the verified contract.");
                // Original image bytes, never live hook bytes. The hash alone does not substitute
                // for catching an incorrect context/mapped-image input.
                byte[] pay = { 0x44, 0x8B, 0xA4, 0x88, 0x90, 0x5B, 0x7E, 0x06 };
                for (int i = 0; i < pay.Length; i++)
                    if (image[0xC8B26 + i] != pay[i]) throw new InvalidOperationException("Native cost-reader anchor differs.");
                FieldInfo field = typeof(GameData).GetField("game_data_txt", BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null || field.FieldType != typeof(int[]) || !field.IsInitOnly)
                    throw new MissingFieldException("GameData.game_data_txt is not the verified private readonly Int32 array.");
                var visual = (int[])field.GetValue(null);
                if (visual == null || visual.Length % 5 != 0 || visual.Length / 5 != NativeRowCount - 1)
                    throw new InvalidOperationException("The visual table does not have its independently verified 109 rows.");
                Volatile.Write(ref tables, new ConstructionCostTables(IntPtr.Add(module, DefaultRva), IntPtr.Add(module, CurrentRva), NativeRowCount, visual));
                unavailableReason = string.Empty;
                NativeApiLog.Debug(log, "CONSTRUCTION_COSTS available: nativeRows=110, visualRows=109; typed aliases validated; no hooks installed.");
            }
            catch (Exception ex)
            {
                unavailableReason = "Construction cost validation failed: " + ex.Message;
                NativeApiLog.Warning(log, unavailableReason);
            }
        }
    }

    // The same checked memory operations are executed by the isolated sentinel-buffer tests.
    internal sealed class ConstructionCostTables
    {
        private readonly IntPtr defaults, current;
        private readonly int rows;
        private readonly int[] visual;
        internal ConstructionCostTables(IntPtr defaults, IntPtr current, int rows, int[] visual)
        { this.defaults = defaults; this.current = current; this.rows = rows; this.visual = visual; }
        internal bool HasVisualRow(int row) => row >= 0 && visual != null && (long)row * 5 + 4 < visual.Length;
        internal bool TryRead(int row, bool fromDefaults, out ConstructionCostValues values, out string reason)
        {
            values = default;
            IntPtr pointer = fromDefaults ? defaults : current;
            if (row < 0 || row >= rows || pointer == IntPtr.Zero) { reason = "The cost row is outside the validated native table."; return false; }
            int offset = checked(row * 20);
            values = new ConstructionCostValues(Marshal.ReadInt32(pointer, offset), Marshal.ReadInt32(pointer, offset + 4),
                Marshal.ReadInt32(pointer, offset + 8), Marshal.ReadInt32(pointer, offset + 12), Marshal.ReadInt32(pointer, offset + 16));
            reason = string.Empty;
            return true;
        }
        internal bool TryWrite(int row, int material, int amount, out string reason)
        {
            // Validate both destinations before either write. In particular, the valid native dock
            // row is absent from the managed table and cannot be written through SE's unsafe setter.
            if (row < 0 || row >= rows || current == IntPtr.Zero || !HasVisualRow(row) ||
                material < 0 || material >= 5 || amount < 0)
            { reason = "The target/material does not fit both native and visual cost tables."; return false; }
            int index = checked(row * 5 + material);
            Marshal.WriteInt32(current, checked(index * 4), amount);
            visual[index] = amount;
            reason = string.Empty;
            return true;
        }
    }
}
