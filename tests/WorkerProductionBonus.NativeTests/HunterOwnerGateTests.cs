using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Wildlife;
using Iced.Intel;
using RedBird.X64.Extensions;

// Executes APIShared's own-player owner gates (Wildlife.HunterOwnerGates) at the three audited spans of the
// private image copy: every allowed case reaches Vanilla's pass target, every other case keeps Vanilla's
// rejection (or, at the arrow, its ZF for the following JNE), with all other registers preserved.
internal static partial class Program
{
    private const int OwnerQueryRva = 0x18AFD8, OwnerAttackRva = 0x18EBDA, OwnerArrowRva = 0x192AA7, OwnerSpan = 14;
    private const int TeamTableRva = 0x37EDF3C, ProjectileSourceRva = 0x2F7A736, ProjectileHeightRva = 0x2F7A6D0;
    private const int Hunter = 6, Chicken = 0x3E, Deer = 0x2C;

    private static unsafe void TestHunterOwnerGates()
    {
        Check(Convert.ToHexString(Read(OwnerQueryRva, OwnerSpan)) == "6683BBF6FDFFFF000F85F7000000", "Owner query span is the audited one.");
        Check(Convert.ToHexString(Read(OwnerAttackRva, OwnerSpan)) == "413984893CDF7E030F84BF000000", "Owner attack span is the audited one.");
        Check(Convert.ToHexString(Read(OwnerArrowRva, OwnerSpan)) == "433984193CDF7E03440FB75C2478", "Owner arrow span is the audited one.");
        IntPtr types = Marshal.AllocHGlobal(HunterNativeAssembly.TypeCount);
        try
        {
            new Span<byte>((void*)types, HunterNativeAssembly.TypeCount).Clear();
            Marshal.WriteByte(types, Chicken, 1);
            ulong table = unchecked((ulong)types.ToInt64());
            TestOwnerQuery(table);
            TestOwnerAttack(table);
            TestOwnerArrow(table);
        }
        finally { Marshal.FreeHGlobal(types); }
    }

    private static int Unit(int id, int field) => HunterManagerRva + id * 0x490 + field;

    private static void TestOwnerQuery(ulong table)
    {
        const int hunter = 3, candidate = 5;
        var original = WorkerProductionBonusNativeDefinition.Decode(Read(OwnerQueryRva, OwnerSpan), Address(OwnerQueryRva));
        // (candidate owner, Hunter owner, candidate type) -> 1 pass, 2 reject
        foreach (var (owner, hunterOwner, type, expected) in new[] {
            (0, 2, Deer, 1), (2, 2, Chicken, 1), (2, 1, Chicken, 2), (2, 2, Deer, 2) })
        {
            Word(Unit(candidate, HunterNativeAssembly.NativeOwnerOffset), owner);
            Word(Unit(candidate, HunterNativeAssembly.NativeTypeOffset), type);
            Word(Unit(hunter, HunterNativeAssembly.NativeOwnerOffset), hunterOwner);
            using var harness = new MachineHarness(a =>
            {
                HunterNativeAssembly.EmitOwnerQuery(a, original, table, Address(0x18B0DD));
                a.AddUnrestrictedJmp(Address(OwnerQueryRva + OwnerSpan));
            }, new Dictionary<int, int> { [OwnerQueryRva + OwnerSpan] = 1, [0x18B0DD] = 2 });
            harness.ResetInput();
            harness.Set(3, Address(Unit(candidate, HunterNativeAssembly.NativeDeadOffset)));
            harness.Set(13, Address(Unit(hunter, 0)));
            harness.Run();
            Check(harness.Marker == expected, "Owner query decides " + owner + "/" + hunterOwner + "/" + type + " as Vanilla plus the table.");
            harness.CheckPreserved(0, 11);
        }
        Word(Unit(candidate, HunterNativeAssembly.NativeOwnerOffset), 0); Word(Unit(candidate, HunterNativeAssembly.NativeTypeOffset), 0);
        Word(Unit(hunter, HunterNativeAssembly.NativeOwnerOffset), 0);
    }

    private static void TestOwnerAttack(ulong table)
    {
        const int target = 5;
        var original = WorkerProductionBonusNativeDefinition.Decode(Read(OwnerAttackRva, OwnerSpan), Address(OwnerAttackRva));
        // (target team, attacker team, attacker type, target owner, attacker owner, target type) -> 1 pass, 2 reject
        foreach (var (targetTeam, attackerTeam, attacker, targetOwner, attackerOwner, type, expected) in new[] {
            (1, 2, 30, 1, 2, Deer, 1), (1, 1, Hunter, 2, 2, Chicken, 1), (1, 1, 30, 2, 2, Chicken, 2),
            (1, 1, Hunter, 2, 3, Chicken, 2), (1, 1, Hunter, 2, 2, Deer, 2) })
        {
            Put(TeamTableRva + targetOwner * 4, targetTeam);
            Word(Unit(target, HunterNativeAssembly.NativeTypeOffset), type);
            using var harness = new MachineHarness(a =>
            {
                HunterNativeAssembly.EmitOwnerAttack(a, original, table, Address(0x18ECA7));
                a.AddUnrestrictedJmp(Address(OwnerAttackRva + OwnerSpan));
            }, new Dictionary<int, int> { [OwnerAttackRva + OwnerSpan] = 1, [0x18ECA7] = 2 });
            harness.ResetInput();
            harness.Set(0, (ulong)attackerTeam); harness.Set(1, (ulong)targetOwner); harness.Set(9, Address(0));
            harness.Set(8, Address(HunterManagerRva)); harness.Set(5, (ulong)(target * 0x490));
            harness.Set(12, (ulong)attacker); harness.Set(13, (ulong)attackerOwner);
            harness.Run();
            Check(harness.Marker == expected, "Owner attack decides team " + targetTeam + "/" + attackerTeam + ", attacker " + attacker +
                ", owners " + targetOwner + "/" + attackerOwner + ", type " + type + ".");
            harness.CheckPreserved(0, 11);
            Put(TeamTableRva + targetOwner * 4, 0);
        }
        Word(Unit(target, HunterNativeAssembly.NativeTypeOffset), 0);
    }

    private static void TestOwnerArrow(ulong table)
    {
        const int victim = 5, source = 3;
        var original = WorkerProductionBonusNativeDefinition.Decode(Read(OwnerArrowRva, OwnerSpan), Address(OwnerArrowRva));
        Word(Unit(source, HunterNativeAssembly.NativeTypeOffset), Hunter);
        // (team differs, projectile type, victim type, owners equal, source is Hunter, victim dead, projectile height)
        // -> 1 resume with ZF as Vanilla, 2 hit continues behind the warning, 3 miss
        foreach (var (teamsDiffer, projectile, type, ownersEqual, sourceHunter, dead, height, expected) in new[] {
            (true, 1, Chicken, true, true, false, 5, 1), (false, 1, Chicken, true, true, false, 5, 2),
            (false, 1, Deer, true, true, false, 5, 1), (false, 2, Chicken, true, true, false, 5, 1),
            (false, 1, Chicken, false, true, false, 5, 1), (false, 1, Chicken, true, false, false, 5, 1),
            (false, 1, Chicken, true, true, true, 5, 3), (false, 1, Chicken, true, true, false, 40, 3) })
        {
            const int projectileOwner = 2;
            int victimOwner = ownersEqual ? projectileOwner : 4;
            Put(TeamTableRva + projectileOwner * 4, teamsDiffer ? 7 : 1);
            Word(ProjectileSourceRva, sourceHunter ? source : victim);
            Word(ProjectileHeightRva, height);
            Word(Unit(victim, HunterNativeAssembly.NativeTypeOffset), type);
            Word(Unit(victim, HunterNativeAssembly.NativeDeadOffset), dead ? 1 : 0);
            Word(Unit(victim, HunterNativeAssembly.NativeHeightOffset), 0);
            Word(Unit(victim, HunterNativeAssembly.NativeStandingHeightOffset), 0);
            using var harness = new MachineHarness(a =>
            {
                HunterNativeAssembly.EmitOwnerArrow(a, original, table, Address(0x192B93), Address(0x1929D0),
                    ProjectileSourceRva, ProjectileHeightRva);
                a.AddUnrestrictedJmp(Address(OwnerArrowRva + OwnerSpan));
            }, new Dictionary<int, int> { [OwnerArrowRva + OwnerSpan] = 1, [0x192B93] = 2, [0x1929D0] = 3 });
            harness.ResetInput();
            harness.Set(0, 1); harness.Set(8, projectileOwner); harness.Set(9, projectileOwner * 4); harness.Set(10, (ulong)victimOwner);
            harness.Set(11, Address(0)); harness.Set(1, (ulong)projectile); harness.Set(12, (ulong)type); harness.Set(13, 0);
            harness.Set(15, Address(HunterManagerRva)); harness.Set(6, (ulong)(victim * 0x490)); harness.Set(2, 10);
            harness.Run();
            Check(harness.Marker == expected, "Owner arrow decides case " + teamsDiffer + "/" + projectile + "/" + type + "/" +
                ownersEqual + "/" + sourceHunter + "/" + dead + "/" + height + ".");
            if (expected == 1)
                Check(((harness.Flags & 0x40) != 0) == !teamsDiffer, "Owner arrow keeps Vanilla's team ZF for the following JNE.");
            harness.CheckPreserved(0, 1, 9, 11);
            Put(TeamTableRva + projectileOwner * 4, 0);
        }
        Word(Unit(source, HunterNativeAssembly.NativeTypeOffset), 0); Word(Unit(victim, HunterNativeAssembly.NativeTypeOffset), 0);
        Word(Unit(victim, HunterNativeAssembly.NativeDeadOffset), 0); Word(ProjectileSourceRva, 0); Word(ProjectileHeightRva, 0);
    }
}
