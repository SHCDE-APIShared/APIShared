using APIShared.ScriptExtenderReplacements;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CorrectedPathing = APIShared.ScriptExtenderReplacements.GamePathingManagerAPI;

namespace APISharedTests
{
    [TestClass]
    public unsafe class PathfindingReplacementTests
    {
        private sealed class Tables : IDisposable
        {
            // Allocate an image-sized owned buffer so production validation is exercised unchanged.
            internal const int Length = 0x32BDB0 + 540 * 4 + 4;
            internal readonly IntPtr Module = Marshal.AllocHGlobal(Length);
            internal readonly CorrectedPathing Api;
            internal int* Profiles => (int*)((byte*)Module + 0x322540);
            internal int* Permissions => (int*)((byte*)Module + 0x32BDB0);

            internal Tables()
            {
                for (int i = -1; i <= 90; i++) Profiles[i] = 1000 + i;
                for (int i = -1; i <= 540; i++) Permissions[i] = 2000 + i;
                var state = PathfindingTableState.Validate(Module, Length, PathfindingTableState.NativeHash,
                    0x322540, (ulong)(Module.ToInt64() + 0x32BDB0));
                Assert.IsTrue(state.IsAvailable, state.Reason);
                Api = new CorrectedPathing(state);
            }
            public void Dispose() => Marshal.FreeHGlobal(Module);
        }

        [TestMethod]
        public void SevenMethodSignaturesAndConstantsMatchTheSeFamily()
        {
            string[] methods = {
                "GetUnitTypePathfindingProfiles", "GetUnitTypePathfindingConnectionClasses",
                "GetUnitTypePathfindingConnectionClassPermissions", "GetUnitTypePathfindingProfile",
                "SetUnitTypePathfindingProfile", "CanUnitTypeUsePathConnectionClass",
                "SetUnitTypeCanUsePathConnectionClass"
            };
            foreach (string name in methods)
            {
                var actual = typeof(CorrectedPathing).GetMethod(name);
                var expected = typeof(SHCDESE.API.GamePathingManagerAPI).GetMethod(name);
                Assert.IsNotNull(actual, name);
                Assert.IsNotNull(expected, name);
                Assert.AreEqual(expected.ReturnType, actual.ReturnType, name);
                Assert.AreEqual(expected.IsStatic, actual.IsStatic, name);
                CollectionAssert.AreEqual(expected.GetParameters().Select(p => p.ParameterType).ToArray(),
                    actual.GetParameters().Select(p => p.ParameterType).ToArray(), name);
                CollectionAssert.AreEqual(expected.GetParameters().Select(p => p.Name).ToArray(),
                    actual.GetParameters().Select(p => p.Name).ToArray(), name);
            }
            Assert.AreEqual(7, typeof(CorrectedPathing).GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly).Count(m => !m.IsSpecialName));
            Assert.AreEqual(89, ReadConstant(typeof(CorrectedPathing), "PATHFINDING_UNIT_TYPE_COUNT"));
            Assert.AreEqual(90, ReadConstant(typeof(CorrectedPathing), "NATIVE_PATHFINDING_UNIT_TYPE_CAPACITY"));
            foreach (string name in new[] { "PATHFINDING_UNIT_TYPE_COUNT", "FIRST_PATH_CONNECTION_CLASS", "LAST_PATH_CONNECTION_CLASS" })
                Assert.AreEqual(ReadConstant(typeof(SHCDESE.API.GamePathingManagerAPI), name),
                    ReadConstant(typeof(CorrectedPathing), name));
            Assert.AreEqual(0, typeof(CorrectedPathing).GetConstructors().Length);
        }

        private static int ReadConstant(Type type, string name) =>
            (int)type.GetField(name, BindingFlags.Public | BindingFlags.Static).GetRawConstantValue();

        private enum ChangedUnitCount : ushort { CHIMP_NUM_TYPES = 90 }
        private enum ChangedUnitWidth : int { CHIMP_NUM_TYPES = 89 }
        private enum ChangedMiddleClass : int
        {
            LadderClimb = 1, Connection2 = 9, GatehouseBig = 3, GatehouseSmall = 4,
            Connection5 = 5, Connection6 = 6
        }
        private enum ChangedClassWidth : byte
        {
            LadderClimb = 1, Connection2 = 2, GatehouseBig = 3, GatehouseSmall = 4,
            Connection5 = 5, Connection6 = 6
        }
        private enum ChangedProfileWidth : short { Large = 0, Default = 1 }
        private enum ChangedProfileValues : int { Large = 1, Default = 0 }

        [TestMethod]
        public void ChangedEnumWidthsAndInteriorValuesAreRejectedBeforeAnyNativeAccess()
        {
            var units = typeof(eChimps);
            var classes = typeof(PathConnectionClass);
            var profiles = typeof(PathfindingProfile);
            Assert.IsTrue(PathfindingTableState.HasSupportedEnumContract(units, classes, profiles));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(typeof(ChangedUnitCount), classes, profiles));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(typeof(ChangedUnitWidth), classes, profiles));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(units, typeof(ChangedMiddleClass), profiles));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(units, typeof(ChangedClassWidth), profiles));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(units, classes, typeof(ChangedProfileWidth)));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(units, classes, typeof(ChangedProfileValues)));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(null, classes, profiles));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(units, typeof(int), profiles));
            Assert.IsFalse(PathfindingTableState.HasSupportedEnumContract(units, classes, null));
        }

        [TestMethod]
        public void AllNativeRowsAndStorageSlotsAreWritableLiveViews()
        {
            using (var tables = new Tables())
            {
                Assert.IsTrue(tables.Api.IsAvailable);
                Assert.AreEqual(string.Empty, tables.Api.UnavailabilityReason);
                var profiles = tables.Api.GetUnitTypePathfindingProfiles();
                var flat = tables.Api.GetUnitTypePathfindingConnectionClasses();
                Assert.AreEqual(90, profiles.Length);
                Assert.AreEqual(540, flat.Length);
                for (int cls = 1; cls <= 6; cls++)
                {
                    var row = tables.Api.GetUnitTypePathfindingConnectionClassPermissions((PathConnectionClass)cls);
                    Assert.AreEqual(90, row.Length);
                    for (int type = 0; type < 90; type++)
                    {
                        // Native class-zero base is 90 Int32s before the public class-one base.
                        int nativeIndex = cls * 0x5A + type - 0x5A;
                        Assert.AreEqual(tables.Permissions[nativeIndex], row[type]);
                        row[type] = -nativeIndex - 1;
                        Assert.AreEqual(-nativeIndex - 1, flat[nativeIndex]);
                        Assert.AreEqual(-nativeIndex - 1, tables.Permissions[nativeIndex]);
                    }
                }
                profiles[89] = PathfindingProfile.Default;
                flat[539] = 72;
                Assert.AreEqual(1, tables.Profiles[89]);
                Assert.AreEqual(72, tables.Permissions[539]);
                tables.Profiles[3] = 17;
                tables.Permissions[90] = -4;
                Assert.AreEqual((PathfindingProfile)17, profiles[3]);
                Assert.AreEqual((PathfindingProfile)17, tables.Api.GetUnitTypePathfindingProfile((eChimps)3));
                Assert.AreEqual(-4, flat[90]);
                Assert.IsTrue(tables.Api.CanUnitTypeUsePathConnectionClass((eChimps)0, (PathConnectionClass)2));
                Assert.AreEqual(999, tables.Profiles[-1]);
                Assert.AreEqual(1090, tables.Profiles[90]);
                Assert.AreEqual(1999, tables.Permissions[-1]);
                Assert.AreEqual(2540, tables.Permissions[540]);
            }
        }

        [TestMethod]
        public void EveryTypedPermissionWriteChangesOnlyItsNativeTarget()
        {
            using (var tables = new Tables())
            {
                for (int cls = 1; cls <= 6; cls++)
                    for (int type = 0; type < 89; type++)
                    {
                        int target = cls * 0x5A + type - 0x5A;
                        Assert.IsTrue(tables.Api.CanUnitTypeUsePathConnectionClass((eChimps)type, (PathConnectionClass)cls));
                        Assert.IsTrue(tables.Api.SetUnitTypeCanUsePathConnectionClass((eChimps)type, (PathConnectionClass)cls, false));
                        Assert.IsFalse(tables.Api.CanUnitTypeUsePathConnectionClass((eChimps)type, (PathConnectionClass)cls));
                        for (int index = -1; index <= 540; index++)
                            Assert.AreEqual(index == target ? 0 : 2000 + index, tables.Permissions[index]);
                        Assert.IsTrue(tables.Api.SetUnitTypeCanUsePathConnectionClass((eChimps)type, (PathConnectionClass)cls, true));
                        Assert.AreEqual(1, tables.Permissions[target]);
                        tables.Permissions[target] = 2000 + target;
                    }
                for (int i = -1; i <= 90; i++) Assert.AreEqual(1000 + i, tables.Profiles[i]);
            }
        }

        [TestMethod]
        public void EveryTypedProfileWritePreservesAdjacentSlotsAndPermissionTables()
        {
            using (var tables = new Tables())
            {
                for (int type = 0; type < 89; type++)
                {
                    Assert.IsTrue(tables.Api.SetUnitTypePathfindingProfile((eChimps)type, PathfindingProfile.Default));
                    Assert.AreEqual(PathfindingProfile.Default, tables.Api.GetUnitTypePathfindingProfile((eChimps)type));
                    for (int i = -1; i <= 90; i++)
                        Assert.AreEqual(i == type ? 1 : 1000 + i, tables.Profiles[i]);
                    Assert.IsTrue(tables.Api.SetUnitTypePathfindingProfile((eChimps)type, PathfindingProfile.Large));
                    Assert.AreEqual(0, tables.Profiles[type]);
                    tables.Profiles[type] = 1000 + type;
                }
                for (int i = -1; i <= 540; i++) Assert.AreEqual(2000 + i, tables.Permissions[i]);
            }
        }

        [TestMethod]
        public void ClassTwoArcherRegressionUsesOffset112InsteadOf111()
        {
            using (var tables = new Tables())
            {
                tables.Permissions[111] = 0;
                tables.Permissions[112] = 1;
                Assert.IsTrue(tables.Api.CanUnitTypeUsePathConnectionClass(eChimps.CHIMP_TYPE_ARCHER, PathConnectionClass.Connection2));
                Assert.IsTrue(tables.Api.SetUnitTypeCanUsePathConnectionClass(eChimps.CHIMP_TYPE_ARCHER, PathConnectionClass.Connection2, false));
                Assert.AreEqual(0, tables.Permissions[112]);
                Assert.AreEqual(0, tables.Permissions[111]);
                Assert.IsTrue(tables.Api.SetUnitTypeCanUsePathConnectionClass(eChimps.CHIMP_TYPE_ARCHER, PathConnectionClass.Connection2, true));
                Assert.AreEqual(1, tables.Permissions[112]);
                Assert.AreEqual(0, tables.Permissions[111]);
            }
        }

        [TestMethod]
        public void InvalidArgumentsDoNotReadOrWriteStorage()
        {
            using (var tables = new Tables())
            {
                foreach (var type in new[] { (eChimps)89, (eChimps)90, (eChimps)ushort.MaxValue })
                {
                    Assert.AreEqual(default(PathfindingProfile), tables.Api.GetUnitTypePathfindingProfile(type));
                    Assert.IsFalse(tables.Api.SetUnitTypePathfindingProfile(type, PathfindingProfile.Default));
                    Assert.IsFalse(tables.Api.CanUnitTypeUsePathConnectionClass(type, PathConnectionClass.Connection2));
                    Assert.IsFalse(tables.Api.SetUnitTypeCanUsePathConnectionClass(type, PathConnectionClass.Connection2, true));
                }
                foreach (int cls in new[] { -1, 0, 7, int.MinValue, int.MaxValue })
                {
                    Assert.IsTrue(tables.Api.GetUnitTypePathfindingConnectionClassPermissions((PathConnectionClass)cls).IsEmpty);
                    Assert.IsFalse(tables.Api.CanUnitTypeUsePathConnectionClass((eChimps)22, (PathConnectionClass)cls));
                    Assert.IsFalse(tables.Api.SetUnitTypeCanUsePathConnectionClass((eChimps)22, (PathConnectionClass)cls, true));
                }
                foreach (int profile in new[] { -1, 2, int.MinValue, int.MaxValue })
                    Assert.IsFalse(tables.Api.SetUnitTypePathfindingProfile((eChimps)22, (PathfindingProfile)profile));
                for (int i = -1; i <= 90; i++) Assert.AreEqual(1000 + i, tables.Profiles[i]);
                for (int i = -1; i <= 540; i++) Assert.AreEqual(2000 + i, tables.Permissions[i]);
            }
        }

        [TestMethod]
        public void UnknownUninitializedAndInconsistentStatesNeverDereferencePointers()
        {
            const long module = 0x100000;
            const ulong permissionVa = module + 0x32BDB0;
            var states = new[] {
                PathfindingTableState.Unavailable("Not initialized"),
                PathfindingTableState.Validate(new IntPtr(1), int.MaxValue, null, 0, 0),
                PathfindingTableState.Validate(new IntPtr(1), int.MaxValue, "changed", 0, 0),
                PathfindingTableState.Validate(IntPtr.Zero, Tables.Length, PathfindingTableState.NativeHash, 0x322540, permissionVa),
                PathfindingTableState.Validate(new IntPtr(-1), Tables.Length, PathfindingTableState.NativeHash, 0x322540, permissionVa),
                PathfindingTableState.Validate(new IntPtr(module), -1, PathfindingTableState.NativeHash, 0x322540, permissionVa),
                PathfindingTableState.Validate(new IntPtr(module), 0x322540 + 359, PathfindingTableState.NativeHash, 0x322540, permissionVa),
                PathfindingTableState.Validate(new IntPtr(module), Tables.Length - 5, PathfindingTableState.NativeHash, 0x322540, permissionVa),
                PathfindingTableState.Validate(new IntPtr(long.MaxValue - 1), Tables.Length, PathfindingTableState.NativeHash, 0x322540, permissionVa),
                PathfindingTableState.Validate(new IntPtr(module), Tables.Length, PathfindingTableState.NativeHash, 0x322544, permissionVa),
                PathfindingTableState.Validate(new IntPtr(module), Tables.Length, PathfindingTableState.NativeHash, 0x322540, permissionVa + 4)
            };
            foreach (var state in states)
            {
                var api = new CorrectedPathing(state);
                Assert.IsFalse(api.IsAvailable);
                Assert.IsFalse(string.IsNullOrEmpty(api.UnavailabilityReason));
                Assert.IsTrue(api.GetUnitTypePathfindingProfiles().IsEmpty);
                Assert.IsTrue(api.GetUnitTypePathfindingConnectionClasses().IsEmpty);
                Assert.IsTrue(api.GetUnitTypePathfindingConnectionClassPermissions(PathConnectionClass.Connection2).IsEmpty);
                Assert.AreEqual(default(PathfindingProfile), api.GetUnitTypePathfindingProfile((eChimps)22));
                Assert.IsFalse(api.SetUnitTypePathfindingProfile((eChimps)22, PathfindingProfile.Default));
                Assert.IsFalse(api.CanUnitTypeUsePathConnectionClass((eChimps)22, PathConnectionClass.Connection2));
                Assert.IsFalse(api.SetUnitTypeCanUsePathConnectionClass((eChimps)22, PathConnectionClass.Connection2, true));
            }
        }
    }
}
