using System;
using System.IO;
using System.Linq;
using APIShared.Units.AiMilitary.Native;
using APIShared.Units.AiMilitary.Orders;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

internal static unsafe class Program
{
    private static int checks;
    private static readonly AiNativeGroup Group = new AiNativeGroup(1, 77, 1);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Main()
    {
        InitializationWaitTests.Run();
        using var backend = new AiMilitaryNativeBackend();
        var initializer = new AiStrategicRecruitInitializer(backend);
        byte* unit = backend.NativeRawUnit(2); *(short*)(unit + 0xA9E) = 1; *(ushort*)(unit + 0x930) = 1;
        *(uint*)(unit + 0x6F0) = 200; *(ushort*)(unit + 0x6EE) = 1;
        *(short*)(unit + 0x71C) = 20; *(short*)(unit + 0x71E) = 20;
        const int Owner = 0x177BC;
        *(int*)((byte*)backend.Module + 0x2EA70DC + Owner) = 2;
        *(byte*)((byte*)backend.Module + 0x4E79D90 + 8010) = 1;
        *(byte*)((byte*)backend.Module + 0x4DDD350 + 8010) = 40;
        *(short*)((byte*)backend.Module + 0x4B6AA50 + 8022 * 2) = 4;
        GameBuildingManagerAPI.Instance.TryGetBuildingById(4, out GameBuilding* building);
        building->r_AliveState = AliveState.IsAlive; building->r_PlayerIdOwner = 2;
        *(short*)((byte*)backend.Module + 0x4C559B0 + 8030 * 2) = 4;
        *(ushort*)(backend.NativeRawUnit(4) + 0x6EE) = 2;
        int* Row(int rva, int task = 0, int approach = 8020, int cost = 0, int reserved = 0)
        {
            if (task == 0) task = rva >= 0x2EAEE34 + Owner && rva < 0x2EAEE34 + Owner + 32 ||
                rva >= 0x2EB2CD0 + Owner && rva < 0x2EB2CD0 + Owner + 32 ? 8022 : rva == 0x2EB9670 ? 8030 : 8010;
            int* r = (int*)((byte*)backend.Module + rva); r[0] = task; r[1] = approach; r[2] = cost; r[3] = reserved; return r;
        }
        void Count(int rva, int count = 1) { *(int*)((byte*)backend.Module + rva) = count; }
        bool Run(int action)
        {
            *(short*)(unit + 0x918) = 0;
            *(uint*)((byte*)backend.Module + 0x48F71B0 + 8010 * 4) = (uint)(action == 0x3F7 ? TilePropertyFlag.IsMoat : TilePropertyFlag.IsWall);
            return initializer.TryInitialize(2, 200, Group, new AiCurrentOrder(AiOrderKind.Strategic, action: action), out _);
        }
        // Primary cost weighting and stable first-minimum selection.
        Count(0x2EA70E4 + Owner, 2);
        int* a = Row(0x2EA70EC + Owner, cost: 3);
        int* b = Row(0x2EA70FC + Owner, cost: 0);
        Check(Run(0x3F2) && a[3] == 0 && b[3] == 2, "weighted primary candidate reserves only its selected entry");
        Check(*(short*)(unit + 0x95A) == 0x3F2 && *(int*)(unit + 0xA00) == 8010 && *(short*)(unit + 0x918) == 101, "fresh strategic fields published");
        a[3] = b[3] = 0;
        backend.Route = () => false;
        Check(!Run(0x3F2) && b[3] == 0 && a[3] == 0, "failed route rolls back only new reservation");
        backend.Route = () => { b[0] = 12345; b[3] = 99; return false; };
        Check(!Run(0x3F2) && b[3] == 99, "rollback preserves concurrently replaced task identity/owner");
        backend.Route = null; a[3] = b[3] = 44;
        Check(!Run(0x3F2) && a[3] == 44 && b[3] == 44, "full reservation list never reset for recruit");
        Count(0x2EA70E4 + Owner, 0);
        // Every native fresh-selector family uses its own target list.
        int[,] families = { { 0x3F3, 0x2EAAF90, 0x2EAAF98 }, { 0x3F4, 0x2EAAF90, 0x2EAAF98 },
            { 0x3F5, 0x2EAEE2C, 0x2EAEE34 }, { 0x3F6, 0x2EB2CC8, 0x2EB2CD0 },
            { 0x3F7, 0x2EB6B64, 0x2EB6B6C }, { 0x413, 0x2EBAA00, 0x2EBAA08 } };
        for (int i = 0; i < families.GetLength(0); i++)
        {
            int action = families[i, 0], count = families[i, 1] + Owner, row = families[i, 2] + Owner;
            Count(count); int* candidate = Row(row);
            Check(Run(action) && candidate[3] == 2, "dedicated family selection " + action.ToString("X"));
            Check(*(short*)(unit + 0x918) == (action == 0x3F4 ? 103 : 101), "family-specific state " + action.ToString("X"));
            Count(count, 0);
        }
        Count(0x2EB9668); int* shock = Row(0x2EB9670);
        Check(Run(0x3FB) && shock[3] == 2, "shock uses global candidate list"); Count(0x2EB9668, 0);
        Count(0x2EA3780); int* shared = Row(0x2EA3788, reserved: 66);
        Check(Run(0x3FD) && shared[3] == 66, "shared fallback does not steal full first reservation");
        shared[3] = 0; Check(Run(0x3FD) && shared[3] == 2, "shared list reserves free slot");
        Check(!Run(0x414) && !Run(0x999), "unimplemented/unknown machine actions fail explicitly");
        // 420 normalizes Role2, and applies secondary distance+5 preference without list reset.
        Count(0x2EA70E4 + Owner); a = Row(0x2EA70EC + Owner, cost: 5);
        Count(0x2EB2CC8 + Owner); int* secondary = Row(0x2EB2CD0 + Owner);
        Check(Run(0x420) && secondary[3] == 2 && a[3] == 0 && *(short*)(unit + 0x95A) == 0x3F2, "420 preference and normalized action");
        Count(0x2EB2CC8 + Owner, 2); secondary[3] = 0;
        int* nearer = Row(0x2EB2CE0 + Owner, approach: 8820);
        Check(Run(0x3F6) && nearer[3] == 2 && secondary[3] == 0, "3F6 uses strict nearest distance without 420 secondary bias");
        *(short*)(unit + 0x918) = 101;
        Check(!initializer.TryInitialize(2, 200, Group, new AiCurrentOrder(AiOrderKind.Strategic, action: 0x3F2), out _), "fresh strategic native state exclusions retained");
        backend.PositionResult = -1; shared[3] = 0;
        backend.Route = () => throw new Exception("1946A0 -1 must not build route");
        Check(Run(0x3FD), "only exact zero from positioning helper triggers route construction");
        backend.Route = null; backend.PositionResult = 0;
        ReceiptTests(); LedgerTests(backend); IntentTests(backend); WindowTests(); MutationTests(backend);
        Console.WriteLine("PASS: " + checks + " production selector, reservation rollback, receipt and ledger assertions.");
    }
    private static void MutationTests(AiMilitaryNativeBackend backend)
    {
        long generation = 1;
        using (var scope = AiRecruitMutationReceipts.Enter(() => generation == 1, 2, 1))
        {
            Check(AiRecruitMutationReceipts.BeforeAssign(2, 1), "final Original boundary admits exact recruit membership");
            AiRecruitMutationReceipts.AfterAssign(); Check(scope.WasAssigned, "actual assignment receipt completes exactly once");
            generation = 2;
            Check(!AiRecruitMutationReceipts.BeforeAssign(2, 1), "world change blocks same-slot/same-ID old assignment before Original");
            Check(!AiUnitMoveReceipts.Before(2, 10, 20, 0), "world change blocks old recruit route before Original");
            Check(AiRecruitMutationReceipts.FilterReturn(77) == 0, "Post world reload cannot return stale tribe ID to Fixes allocator");
            using (var restored = AiRecruitMutationReceipts.Enter(() => generation == 2, 7, 3))
            {
                Check(AiRecruitMutationReceipts.BeforeAssign(7, 3), "new-world raw restoration has its own nested generation scope");
                AiRecruitMutationReceipts.AfterAssign(); Check(restored.WasAssigned, "nested new-world assignment can finish safely");
            }
            using (AiRecruitMutationReceipts.Enter(() => generation == 2))
            {
                Check(AiRecruitMutationReceipts.BeforeAssign(7, 3), "native new-world load scope bypasses only the retired outer recruit identity");
                Check(AiUnitMoveReceipts.Before(7, 30, 40, 0), "native new-world load can initialize its own unit movement");
                Check(AiRecruitMutationReceipts.FilterTribeCreateReturn(23, 23) == 23, "native new-world load can create its own tribe");
            }
            Check(!AiRecruitMutationReceipts.IsCurrentWorld, "return to ended outer operation remains invalid");
        }
        Check(AiRecruitMutationReceipts.IsCurrentWorld, "mutation guard disposed without leaking thread context");
        using (var redirected = AiRecruitMutationReceipts.Enter(() => true, 2, 1))
        {
            Check(!AiRecruitMutationReceipts.BeforeAssign(3, 1), "foreign callback cannot redirect assignment to another member");
            Check(!AiRecruitMutationReceipts.BeforeAssign(2, 1) && !redirected.WasAssigned, "redirected invocation cannot become successful later");
        }
        using (AiRecruitMutationReceipts.Enter(() => true))
        {
            Check(AiRecruitMutationReceipts.FilterTribeCreateReturn(17, 17) == 17, "actual Original-created tribe identity survives wrapper Post");
            Check(AiRecruitMutationReceipts.FilterTribeCreateReturn(99, 17) == 0, "Post cannot redirect allocator to foreign valid empty tribe");
            Check(AiRecruitMutationReceipts.FilterTribeCreateReturn(99, 0) == 0, "vetoed Original cannot fabricate a valid tribe ID");
            Check(AiRecruitMutationReceipts.FilterTribeCreateReturn(18, 18) == 18, "successive creates in one placement scope retain independent receipts");
        }
        bool sameIdentity = true;
        using (AiRecruitMutationReceipts.Enter(() => true, 2, 1, () => sameIdentity))
        {
            sameIdentity = false;
            Check(!AiRecruitMutationReceipts.BeforeAssign(2, 1), "same-world unit slot reuse is rejected before membership Original");
            Check(!AiUnitMoveReceipts.Before(2, 10, 20, 0), "same-world unit slot reuse is rejected before route Original");
        }
        using (AiRecruitMutationReceipts.Enter(() => true, 2, 1, () => true, () => false))
            Check(!AiRecruitMutationReceipts.BeforeAssign(2, 1), "unexpected intervening membership blocks duplicate native assignment");
        byte* image = (byte*)backend.Module, unit = backend.NativeRawUnit(2);
        *(short*)(unit + 0x918) = 0; *(uint*)(image + 0x48F71B0 + 8010 * 4) = (uint)TilePropertyFlag.IsMoat;
        int* row = (int*)(image + 0x2EB6B6C + 0x177BC); row[3] = 0;
        backend.WorldGeneration = 10;
        backend.Route = () => { backend.WorldGeneration = 11; row[3] = 2; return false; };
        var initializer = new AiStrategicRecruitInitializer(backend);
        Check(!initializer.TryInitialize(2, 200, Group, new AiCurrentOrder(AiOrderKind.Strategic, action: 0x3F7), out _) && row[3] == 2,
            "world transition never rolls back an identical new-world task/unit reservation");
        backend.Route = null;
    }
    private static void WindowTests()
    {
        Check(AiGroupWindowPolicy.TryCount(-5, 3, true, out int count) && count == 1, "negative cover count follows native base slot path");
        Check(AiGroupWindowPolicy.TryCount(0, 2, true, out count) && count == 1, "zero reserve count follows native base slot path");
        Check(AiGroupWindowPolicy.TryCount(8, 8, true, out count) && count == 8, "wall physical window accepts its audited endpoint");
        Check(!AiGroupWindowPolicy.TryCount(9, 8, true, out _), "wall window cannot overwrite following native slots");
        Check(!AiGroupWindowPolicy.TryCount(0, 2, false, out _) && !AiGroupWindowPolicy.TryCount(-1, 10, false, out _), "storm/patrol require a positive planner window");
    }
    private static void IntentTests(AiMilitaryNativeBackend backend)
    {
        const int Owner = 0x177BC, Task = 8010;
        byte* image = (byte*)backend.Module, unit = backend.NativeRawUnit(2);
        var player = Fixture.Player(1); var enemy = Fixture.Player(2);
        player->r_AILordMinusOne = 1; player->r_AISiegeState = 5; player->r_AISiegeAttempts = 2; player->r_SiegeAttackTargetPlayerId = 2;
        enemy->r_LordUnitId = 3; enemy->r_LordUnitGlobalId = 303;
        var lord = (GameUnit*)(backend.NativeRawUnit(3) + 0x65C);
        lord->r_GlobalId = 303; lord->r_UnitChimp = eChimps.CHIMP_TYPE_LORD; lord->r_ControllableForPlayerId = 2;
        *(ushort*)(backend.Tribe(Group) + 0x60) = 1 << 2;
        *(int*)(image + 0x2EB6B64 + Owner) = 1;
        int* row = (int*)(image + 0x2EB6B6C + Owner); row[0] = Task; row[1] = 8020; row[2] = row[3] = 0;
        var ledger = new AiOrderLedger(backend); ledger.Clear(8);
        void Role(int role, int storage)
        {
            for (int i = 0; i < 300; i++) { player->r_AITribeIdsByStorageRole[i] = 0; player->r_AITribeGlobalIdsByStorageRole[i] = 0; }
            player->r_AITribeIdsByStorageRole[storage] = 1; player->r_AITribeGlobalIdsByStorageRole[storage] = 77;
            *(ushort*)(unit + 0xA82) = (ushort)role;
        }
        bool Current(int action)
        { ledger.ObserveStrategic(8, Group, action, 1); return ledger.TryGetCurrent(8, Group, out _, out _); }
        Role(11, 17); *(uint*)(image + 0x48F71B0 + Task * 4) = (uint)TilePropertyFlag.IsMoat;
        Check(Current(0x3F7), "ongoing moat intent confirmed by role/storage/phase/attempt/target and live task list");
        player->r_AISiegeState = 6;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "phase6 does not revive phase5 moat intent"); player->r_AISiegeState = 5;
        Check(Current(0x3F7), "new final broadcast restores valid current intent"); player->r_AISiegeAttempts++;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "new siege attempt invalidates old broadcast"); player->r_AISiegeAttempts = 2;
        Check(Current(0x3F7), "attempt reset fixture resealed"); enemy->r_LordUnitGlobalId = 999;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "target lord slot reuse rejects old intent"); enemy->r_LordUnitGlobalId = 303;
        Check(Current(0x3F7), "target identity fixture resealed"); *(uint*)(image + 0x48F71B0 + Task * 4) = 0;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "completed/removed moat cannot keep cached job alive");
        Role(14, 13); *(uint*)(image + 0x48F71B0 + Task * 4) = (uint)TilePropertyFlag.IsWall;
        *(int*)(image + 0x2EAAF90 + Owner) = 1;
        row = (int*)(image + 0x2EAAF98 + Owner); row[0] = Task; row[1] = 8020; row[2] = row[3] = 0;
        Check(Current(0x3F4), "ladder intent is bound to role14/storage13 and current climbable enemy wall");
        *(uint*)(image + 0x48F71B0 + Task * 4) |= (uint)TilePropertyFlag.CrenelatedHigh;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "unclimbable replacement wall invalidates ladder intent");
        *(uint*)(image + 0x48F71B0 + Task * 4) = (uint)TilePropertyFlag.IsWall;
        Role(12, 11); *(int*)(image + 0x2EA70E4 + Owner) = 1;
        row = (int*)(image + 0x2EA70EC + Owner); row[0] = Task; row[1] = 8020; row[2] = row[3] = 0;
        Check(Current(0x3F2), "assassin fixed role accepts current standard attack family"); *(int*)(image + 0x3665FB8) = 1;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "changed native attack mode supersedes old action family");
        Check(Current(0x420), "new enhanced attack broadcast accepts current420 intent"); *(int*)(image + 0x3665FB8) = 0;
        Role(18, 189); player->r_AISiegeState = 4;
        *(int*)(image + 0x2EB9668) = 1; row = (int*)(image + 0x2EB9670); row[0] = 8030; row[1] = 8020; row[2] = row[3] = 0;
        Check(Current(0x3FB), "shock accepts only observed current global task owner/target"); AiMilitaryNativeObservers.Owner = 8;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "another AI overwriting global jobs invalidates shock source"); AiMilitaryNativeObservers.Owner = 1;
        var task = (MoatWorkTask*)(image + 0x100000); task->r_TileId = Task; task->r_TileX = 10; task->r_TileY = 10;
        task->r_OwnerPlayerId = 1; task->r_WorkType = MoatWorkType.DigPlannedMoat;
        *(uint*)(image + 0x48F71B0 + Task * 4) = (uint)TilePropertyFlag.PlannedMoat;
        ledger.ObserveAccepted(8, Group, new AiCurrentOrder(AiOrderKind.DigMoat, 10, 10, 6), 1);
        Check(ledger.TryGetCurrent(8, Group, out _, out _), "explicit command6 live moat task persists");
        *(uint*)(image + 0x48F71B0 + Task * 4) |= (uint)TilePropertyFlag.IsMoat;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "completed dig work invalidates old command6");
        player->r_AISiegeState = 5; *(uint*)(image + 0x48F71B0 + Task * 4) = (uint)TilePropertyFlag.IsWall;
        foreach (var pair in new[] { (12, 11), (13, 12), (19, 190), (20, 192) })
        { Role(pair.Item1, pair.Item2); Check(Current(0x3F2), "role-specific standard family " + pair.Item1); }
        Role(17, 186); Check(!Current(0x3F2), "cover storage does not fabricate a strategic attack terminal");
        Role(16, 15); Check(!Current(0x3F2), "storm storage retains movement planner contract");
        Role(15, 14); Check(!Current(0x3F2), "tunneler reserve does not impersonate detached context jobs");
        Role(11, 17); *(uint*)(image + 0x48F71B0 + Task * 4) = (uint)TilePropertyFlag.IsMoat;
        Check(Current(0x3F7), "moat resealed for exact storage identity test"); player->r_AITribeGlobalIdsByStorageRole[17] = 999;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "storage slot global identity mismatch invalidates intent");
        player->r_AITribeGlobalIdsByStorageRole[17] = 77; Check(Current(0x3F7), "storage identity restored fixture");
        *(ushort*)(unit + 0xA82) = 12;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "member role mismatch cannot borrow moat planner intent");
        *(ushort*)(unit + 0xA82) = 11; Check(Current(0x3F7), "member role fixture restored");
        *(uint*)(image + 0x48F71B0 + 8020 * 4) = (uint)TilePropertyFlag.IsWall;
        Check(!ledger.TryGetCurrent(8, Group, out _, out _), "blocked approach invalidates cached native candidate");
        *(uint*)(image + 0x48F71B0 + 8020 * 4) = 0;
    }
    private static void ReceiptTests()
    {
        Check(AiUnitMoveReceipts.TryRun(2, 10, 20, 0, () => { Check(AiUnitMoveReceipts.Before(2, 10, 20, 0), "matching original admitted"); AiUnitMoveReceipts.After(1); return 0; }, out _), "raw native success survives Post false");
        Check(!AiUnitMoveReceipts.TryRun(2, 10, 20, 0, () => { AiUnitMoveReceipts.Before(2, 10, 20, 0); AiUnitMoveReceipts.After(0); return 1; }, out _), "Post true cannot mask native failure");
        Check(!AiUnitMoveReceipts.TryRun(2, 10, 20, 0, () => { Check(!AiUnitMoveReceipts.Before(3, 10, 20, 0), "redirected recruit rejected before native mutation"); return 1; }, out _), "redirect has no successful receipt");
        Check(!AiUnitMoveReceipts.TryRun(2, 10, 20, 0, () => 1, out _), "veto/no original is not success");
        Check(!AiUnitMoveReceipts.IsActive, "thread receipt cleared after invocation");
    }
    private static void LedgerTests(AiMilitaryNativeBackend backend)
    {
        var ledger = new AiOrderLedger(backend); ledger.Clear(3);
        ledger.ObserveMove(2, Group, 30, 30, 0, true, 0, 1);
        Check(!ledger.TryGetCurrent(3, Group, out _, out _), "old-world order cannot enter new ledger");
        ledger.ObserveMove(3, Group, 30, 30, 0, true, 0, 1);
        Check(ledger.TryGetCurrent(3, Group, out var order, out _) && order.Kind == AiOrderKind.Move, "accepted move remains current");
        backend.Completed = true;
        Check(ledger.TryGetCurrent(3, Group, out order, out _) && order.Kind == AiOrderKind.Idle, "native movement completion ends move");
        ledger.ObserveStrategic(3, Group, 0x3F7, 1);
        Check(!ledger.TryGetCurrent(3, Group, out _, out _), "no strategic lifecycle proof fails closed");
        ledger.EnterGroupDispatch(Group); ledger.EnterGroupDispatch(Group); ledger.ExitGroupDispatch(Group);
        Check(ledger.IsInGroupDispatch(Group), "nested dispatch suppresses independent-route invalidation until outer exit");
        ledger.ExitGroupDispatch(Group); Check(!ledger.IsInGroupDispatch(Group), "dispatch scope balances");
        ledger.ObserveAccepted(3, Group, new AiCurrentOrder(AiOrderKind.Hold, 30, 30), 1);
        ledger.Invalidate(Group); Check(!ledger.TryGetCurrent(3, Group, out _, out _), "explicit supersede invalidates hold");
        var savedOrder = new AiCurrentOrder(AiOrderKind.Hold, 30, 40, 9, 123, 456, 1010, -7, 2);
        ledger.ObserveAccepted(3, Group, savedOrder, 1);
        byte[] payload = ledger.Capture(); ledger.Clear(4); ledger.Restore(payload, 4);
        Check(ledger.TryGetCurrent(4, Group, out order, out _) && order.Kind == savedOrder.Kind && order.X == 30 && order.Y == 40 &&
            order.Command == 9 && order.TargetId == 123 && order.TargetGlobalId == 456 && order.Action == 1010 && order.Extra == -7 && order.MoveType == 2,
            "save roundtrip preserves every effective order field and rebinds world generation");
        Check(payload.SequenceEqual(ledger.Capture()), "local world generation is absent from serialized order ledger");
        byte[] duplicate = new byte[payload.Length + 48]; Array.Copy(payload, duplicate, payload.Length);
        Array.Copy(payload, 8, duplicate, payload.Length, 48); BitConverter.GetBytes(2).CopyTo(duplicate, 4);
        bool refused = false; try { ledger.Restore(duplicate, 9); } catch (InvalidDataException) { refused = true; }
        Check(refused && ledger.TryGetCurrent(4, Group, out _, out _), "duplicate record is rejected before changing current world registry");
        byte[] invalidKind = (byte[])payload.Clone(); BitConverter.GetBytes(999).CopyTo(invalidKind, 20);
        refused = false; try { ledger.Restore(invalidKind, 9); } catch (InvalidDataException) { refused = true; }
        Check(refused && ledger.TryGetCurrent(4, Group, out _, out _), "invalid enum domain is rejected atomically");
        ledger.ObserveAccepted(4, Group, new AiCurrentOrder(AiOrderKind.AttackTile, 20, 30), 1);
        ledger.Restore(ledger.Capture(), 5);
        Check(ledger.TryGetCurrent(5, Group, out order, out _) && order.Kind == AiOrderKind.AttackTile, "persisted attack-tile kind10 remains supported");
    }
}
