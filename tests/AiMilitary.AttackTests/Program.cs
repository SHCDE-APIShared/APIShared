using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Units.AiMilitary.Native;
using APIShared.Units.AiMilitary.Orders;
using SHCDESE.Interop;

internal static unsafe class Program
{
    private static AiMilitaryNativeBackend backend;
    private static AiAttackRecruitInitializer handler;
    private static readonly AiNativeGroup Group = new AiNativeGroup(1, 123, 1);
    private static int missing, checks;
    private static string changeWorldDuring;
    private static byte* Raw => backend.NativeRawUnit(1);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static byte[] Snapshot(int id) { var b = new byte[0x490]; Marshal.Copy((IntPtr)(backend.NativeRawUnit(id) + 0x65C), b, 0, b.Length); return b; }
    private static AiCurrentOrder UnitOrder => new AiCurrentOrder(AiOrderKind.AttackUnit, command: 4, targetId: 2, targetGlobalId: 22);
    private static void Reset(eChimps type)
    {
        backend.Unit(1, type, 11); backend.Unit(2, eChimps.CHIMP_TYPE_LORD, 22); backend.Unit(3, eChimps.CHIMP_TYPE_SPEARMAN, 33);
        new Span<byte>(backend.Tribe(Group), 0x688).Clear(); backend.VetoRoute = false; backend.RouteCalls = missing = 0;
        changeWorldDuring = null;
    }
    public static void Main()
    {
        using (backend = new AiMilitaryNativeBackend())
        {
            handler = new AiAttackRecruitInitializer(backend, new AiMilitaryNativeBindings());
            Reset(eChimps.CHIMP_TYPE_ARCHER); var old = Snapshot(3);
            Check(handler.TryInitialize(1, 11, Group, UnitOrder, out _), "ranged unit join");
            Check(*(short*)(Raw + 0x9F4) == 4 && *(short*)(Raw + 0x9F6) == 2 && *(uint*)(Raw + 0x9F8) == 22, "ranged target identity");
            Check(Snapshot(3).SequenceEqual(old) && backend.RouteCalls == 0, "ranged join preserves old members");
            Check(!handler.TryInitialize(1, 11, Group, new AiCurrentOrder(AiOrderKind.AttackUnit, command: 4, targetId: 2), out _),
                "a zero global target identity is never the neutral lookup wildcard");
            Reset(eChimps.CHIMP_TYPE_CATAPULT);
            Check(handler.TryInitialize(1, 11, Group, UnitOrder, out _) && *(short*)(Raw + 0x9F4) == 5 && *(short*)(Raw + 0x9FC) == 11, "catapult uses native position attack");
            foreach (var type in new[] { eChimps.CHIMP_TYPE_BATTERING_RAM, eChimps.CHIMP_TYPE_PORTABLE_SHIELD, eChimps.CHIMP_TYPE_SIEGE_TOWER })
            { Reset(type); Check(!handler.TryInitialize(1, 11, Group, UnitOrder, out _), "unsupported machine unit attack must reject"); }
            Reset(eChimps.CHIMP_TYPE_SPEARMAN); backend.VetoRoute = true; old = Snapshot(3);
            Check(!handler.TryInitialize(1, 11, Group, UnitOrder, out _) && *(short*)(backend.Tribe(Group) + 0x63C) == 0, "route veto must not publish tracking");
            Check(Snapshot(3).SequenceEqual(old), "route veto preserves old members");
            Reset(eChimps.CHIMP_TYPE_SPEARMAN); old = Snapshot(3);
            Check(handler.TryInitialize(1, 11, Group, UnitOrder, out _) && backend.RouteCalls == 1, "melee gets exactly one own route");
            Check(*(short*)(backend.Tribe(Group) + 0x63C) == 2 && Snapshot(3).SequenceEqual(old), "first melee publishes tracking without changing old members");
            Reset(eChimps.CHIMP_TYPE_CATAPULT); missing = 1;
            Check(!handler.TryInitialize(1, 11, Group, UnitOrder, out _), "incomplete crew rejects");
            Reset(eChimps.CHIMP_TYPE_CATAPULT);
            var tile = new AiCurrentOrder(AiOrderKind.AttackTile, 12, 13, command: 22, extra: 15);
            Check(handler.TryInitialize(1, 11, Group, tile, out _) && Raw[0xA1D] == 5 && *(short*)(Raw + 0x9F4) == 0, "22 native fallback preserves catapult last-command branch");
            *(int*)((byte*)backend.Module + 0x379D048 + 0x583C) = 1;
            Check(handler.TryInitialize(1, 11, Group, tile, out _) && *(short*)(Raw + 0x9F4) == 22 && *(short*)(Raw + 0x716) == 96, "22 enabled catapult branch");
            BuildingTests();
            WorldChangeTests();
            Console.WriteLine("PASS: " + checks + " production attack-member assertions; other-member preservation, exact machine branches, route veto, target identity and bounded selection.");
        }
    }
    private static void BuildingTests()
    {
        SHCDESE.API.GameBuildingManagerAPI.Instance.Building = (GameBuilding*)((byte*)backend.Module + 0x1000);
        var view = SHCDESE.API.GameBuildingManagerAPI.Instance.Building; view->r_AliveState = SHCDESE.Interop.Enums.AliveState.IsAlive; view->r_GlobalId = 44;
        byte* building = (byte*)backend.Module + 0x64CCBB0 + 0x32C;
        *(short*)(building + 0x154) = 1; *(short*)(building + 0x14A) = 12; *(short*)(building + 0x14C) = 10;
        var order = new AiCurrentOrder(AiOrderKind.AttackBuilding, command: 9, targetId: 1, targetGlobalId: 44);
        foreach (var type in new[] { eChimps.CHIMP_TYPE_ARAB_BALLISTA, eChimps.CHIMP_TYPE_BATTERING_RAM })
        {
            Reset(type); *(short*)(backend.Tribe(Group) + 0x64C) = 1; *(uint*)(backend.Tribe(Group) + 0x650) = 44;
            oldMember = Snapshot(3);
            Check(handler.TryInitialize(1, 11, Group, order, out _), "machine building branch " + type);
            Check(Snapshot(3).SequenceEqual(oldMember), "building join changes no old member");
            Check(type == eChimps.CHIMP_TYPE_ARAB_BALLISTA ? *(short*)(Raw + 0x9F4) == 9 && backend.RouteCalls == 0 : *(int*)(Raw + 0xA00) == 8012 && backend.RouteCalls == 1, "direct versus ram approach");
        }
        var stale = new AiCurrentOrder(AiOrderKind.AttackBuilding, command: 9, targetId: 1, targetGlobalId: 99);
        Check(!handler.TryInitialize(1, 11, Group, stale, out _), "stale building must reject");
    }
    private static byte[] oldMember;
    private static void ChangedWorld(string stage)
    {
        if (changeWorldDuring != stage) return;
        changeWorldDuring = null; backend.WorldGeneration++;
        // A reentrant load may restore the exact same native IDs/GIDs. These fields
        // now belong to that new world and must survive the older initializer.
        *(short*)(Raw + 0x9F4) = 777; *(short*)(Raw + 0xA30) = 888;
        *(short*)(backend.Tribe(Group) + 0x63C) = 999;
    }
    private static void WorldChangeTests()
    {
        foreach (string stage in new[] { "predict", "unit-candidates", "clear", "crew" })
        {
            Reset(stage == "crew" ? eChimps.CHIMP_TYPE_CATAPULT : stage == "clear" ? eChimps.CHIMP_TYPE_ARAB_BALLISTA : eChimps.CHIMP_TYPE_SPEARMAN);
            changeWorldDuring = stage;
            Check(!handler.TryInitialize(1, 11, Group, UnitOrder, out _) && *(short*)(Raw + 0x9F4) == 777 && *(short*)(Raw + 0xA30) == 888 &&
                *(short*)(backend.Tribe(Group) + 0x63C) == 999, "same-ID reentrant world during " + stage + " keeps new-world fields");
        }
        foreach (string stage in new[] { "building-candidates", "all-assassins", "flood", "ram" })
        {
            Reset(stage == "ram" ? eChimps.CHIMP_TYPE_BATTERING_RAM : eChimps.CHIMP_TYPE_SPEARMAN);
            *(short*)(backend.Tribe(Group) + 0x64C) = 1; *(uint*)(backend.Tribe(Group) + 0x650) = 44;
            changeWorldDuring = stage;
            var order = new AiCurrentOrder(AiOrderKind.AttackBuilding, command: 9, targetId: 1, targetGlobalId: 44);
            Check(!handler.TryInitialize(1, 11, Group, order, out _) && *(short*)(Raw + 0x9F4) == 777 && *(short*)(Raw + 0xA30) == 888,
                "same-ID reentrant world during " + stage + " keeps new-world fields");
        }
    }
    public static void Predict(IntPtr tribes, int attacker, int target, ref int x, ref int y) { Check(attacker == 1, "predict uses recruit"); x = 12; y = 10; ChangedWorld("predict"); }
    private static void Candidates() { int* list = (int*)((byte*)backend.Module + 0x60C89A4); list[0] = 8011; list[1] = 8012;
        *(short*)((byte*)backend.Module + 0x3AAE2A4 + 8011 * 2) = 10; *(short*)((byte*)backend.Module + 0x3AAE2A4 + 8012 * 2) = 10;
        *(short*)((byte*)backend.Module + 0x4B6AA50 + 8012 * 2) = 1; }
    public static void UnitCandidates(IntPtr paths, int tribe, int target, int x, int y, int count, int pcl, int player) { Check(count == 1, "singleton candidate count"); Candidates(); ChangedWorld("unit-candidates"); }
    public static void BuildingCandidates(IntPtr paths, int tribe, int building, int count, int pcl, int player) { Check(count == 1, "singleton building candidate count"); Candidates(); ChangedWorld("building-candidates"); }
    public static int RamPosition(IntPtr paths, int unit, int building, ref int attackTile) { Check(unit == 1, "ram selection uses recruit"); attackTile = 8012; ChangedWorld("ram"); return 8011; }
    public static int AllAssassins(IntPtr manager, int tribe) { ChangedWorld("all-assassins"); return 0; }
    public static int MissingCrew(IntPtr manager, int unit) { ChangedWorld("crew"); return missing; }
    public static void ClearPath(IntPtr manager, int unit) { Check(unit == 1, "clear path uses recruit"); ChangedWorld("clear"); }
    public static int Flood(IntPtr paths, int x, int y, int tx, int ty, int budget, int resume)
    { *(int*)((byte*)paths + 4) = 1; *(short*)((byte*)backend.Module + 0x52C2550 + 8011 * 2) = 1; ChangedWorld("flood"); return 0; }
    public static int Wide(IntPtr paths, int x, int y, int tx, int ty, int budget) => Flood(paths, x, y, tx, ty, budget, 0);
}
