using System;
using System.Runtime.InteropServices;
using APIShared.Buildings;
using SHCDESE.Interop;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string why) { checks++; if (!condition) throw new Exception(why); }
    private static void Alias(eStructs alias, eStructs canonical)
    {
        var target = ConstructionCostTarget.FromStructure(alias);
        Check(target.IsValid && target.SupportsVanillaCosts, alias + " supported");
        Check(target == ConstructionCostTarget.FromStructure(canonical), alias + " canonical equality");
        Check(ConstructionCostTarget.TryParseKey(target.Key, out var restored) && restored == target, alias + " symbolic persistence");
    }
    private static void Main()
    {
        Alias(eStructs.STRUCT_MANGONEL, eStructs.STRUCT_TOWER1_DESTROYED);
        Alias(eStructs.STRUCT_BALLISTA, eStructs.STRUCT_TOWER2_DESTROYED);
        foreach (eStructs value in new[] { eStructs.STRUCT_GATE_WOOD1A, eStructs.STRUCT_GATE_WOOD1B, eStructs.STRUCT_GATE_WOOD1C, eStructs.STRUCT_GATE_WOOD1D }) Alias(value, eStructs.STRUCT_GATE_WOOD);
        foreach (eStructs value in new[] { eStructs.STRUCT_GATE_STONE1A, eStructs.STRUCT_GATE_STONE1B }) Alias(value, eStructs.STRUCT_GATE_INNER);
        foreach (eStructs value in new[] { eStructs.STRUCT_GATE_STONE2A, eStructs.STRUCT_GATE_STONE2B }) Alias(value, eStructs.STRUCT_GATE_MAIN);
        foreach (eStructs value in new[] { eStructs.STRUCT_GARDEN_SMALL, eStructs.STRUCT_GARDEN_MED, eStructs.STRUCT_GARDEN_LARGE }) Alias(value, eStructs.STRUCT_GARDEN);
        foreach (eStructs value in new[] { eStructs.STRUCT_POND_SMALL, eStructs.STRUCT_POND_LARGE, eStructs.STRUCT_POND5, eStructs.STRUCT_POND6, eStructs.STRUCT_POND7, eStructs.STRUCT_POND8 }) Alias(value, eStructs.STRUCT_POND);
        Check(ConstructionCostTarget.FromMapper(eMappers.MAPPER_BALLISTA) == ConstructionCostTarget.FromStructure(eStructs.STRUCT_BALLISTA), "ballista mapper coalesces");
        Check(ConstructionCostTarget.FromMapper(eMappers.MAPPER_GATE_WOOD) == ConstructionCostTarget.FromMapper(eMappers.MAPPER_GATE_WOOD1A), "abstract wooden gate mapper supported");
        var brazier = ConstructionCostTarget.FromMapper(eMappers.MAPPER_BRAZIER);
        Check(brazier.SupportsHumanExtraCosts && !brazier.SupportsVanillaCosts, "brazier never addresses a cost table");
        Check(brazier == ConstructionCostTarget.FromStructure(eStructs.STRUCT_BRAZIER), "brazier domains coalesce");
        Check(!ConstructionCostTarget.FromStructure(eStructs.STRUCT_NULL).IsValid, "null rejected");
        Check(!ConstructionCostTarget.FromStructure(eStructs.STRUCT_MAX).IsValid, "native sentinel/wall rejected");
        Check(!ConstructionCostTarget.FromStructure((eStructs)ushort.MaxValue).IsValid, "undefined structure rejected");
        Check(!ConstructionCostTarget.FromMapper(unchecked((eMappers)int.MaxValue)).IsValid, "undefined mapper rejected");
        Check(!ConstructionCostTarget.TryParseKey("Structure:116", out _), "numeric key rejected");
        Check(!ConstructionCostTarget.TryParseKey("Structure:STRUCT_HOVEL, STRUCT_OUTPOST_BEDOUIN", out _), "composed enum names are not a symbolic identity");
        var mapperDomain = ConstructionCostTarget.FromMapper(eMappers.MAPPER_WOODSMAN);
        var wrongDomain = ConstructionCostTarget.FromStructure((eStructs)(int)eMappers.MAPPER_WOODSMAN);
        Check(mapperDomain != wrongDomain, "same numeric input in different enum domains is not guessed");

        const int rows = ConstructionCosts.NativeRowCount;
        IntPtr defaults = Marshal.AllocHGlobal((rows * 5 + 2) * 4), current = Marshal.AllocHGlobal((rows * 5 + 2) * 4);
        try
        {
            for (int index = 0; index < rows * 5 + 2; index++) { Marshal.WriteInt32(defaults, index * 4, 7000 + index); Marshal.WriteInt32(current, index * 4, 9000 + index); }
            int[] visual = new int[(rows - 1) * 5];
            var tables = new ConstructionCostTables(IntPtr.Add(defaults, 4), IntPtr.Add(current, 4), rows, visual);
            Check(tables.TryRead(87, true, out var cost, out _) && cost.Gold == 7000 + 1 + 87 * 5 + 4, "default row read actual buffer");
            Check(tables.TryWrite(87, 4, 73, out _), "ballista canonical row writes");
            Check(Marshal.ReadInt32(current, (1 + 87 * 5 + 4) * 4) == 73 && visual[87 * 5 + 4] == 73, "native and visual update together");
            int beforeNative = Marshal.ReadInt32(current, (1 + (rows - 1) * 5) * 4);
            Check(tables.TryRead(rows - 1, false, out _, out _), "last native row readable");
            Check(!tables.TryWrite(rows - 1, 0, 10, out _), "missing visual row rejects before native write");
            Check(Marshal.ReadInt32(current, (1 + (rows - 1) * 5) * 4) == beforeNative, "rejected native row remains unchanged");
            foreach (int row in new[] { -1, rows, 115, 116, int.MaxValue })
            {
                Check(!tables.TryRead(row, false, out _, out _), "bad read row rejected");
                Check(!tables.TryWrite(row, 0, 10, out _), "bad write row rejected");
            }
            Check(!tables.TryWrite(1, 5, 10, out _) && !tables.TryWrite(1, 0, -1, out _), "bad material and setting sentinel rejected");
            Check(Marshal.ReadInt32(current) == 9000 && Marshal.ReadInt32(current, (rows * 5 + 1) * 4) == 9000 + rows * 5 + 1, "native guard sentinels unchanged");
            Check(!ConstructionCosts.TrySetCurrent(brazier, eGoods.STORED_GOLD, 20, out _), "public API rejects unsupported target before initialization");
        }
        finally { Marshal.FreeHGlobal(defaults); Marshal.FreeHGlobal(current); }
        Console.WriteLine("Construction costs: " + checks + " checks passed (real SE enum/mapping, isolated native buffers; no game rendering).");
    }
}
