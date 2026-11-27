using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Wildlife;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed unsafe class HerdTests
    {
        // The audited raw herd record must match the referenced Extender's GameTribe, or herd reads,
        // routes and spawned-herd cuts would touch the wrong fields.
        [TestMethod]
        public void ExtenderTribeLayoutMatchesTheAuditedHerdRecord()
        {
            Assert.IsTrue(HerdNativeContract.TryValidateLayout(out string reason), reason);
        }

        [TestMethod]
        public void EveryHerdKindIsAHerdWithItsOwnVanillaSpawnArt()
        {
            var kinds = Enum.GetValues(typeof(HerdKind)).Cast<HerdKind>().ToArray();
            Assert.IsTrue(kinds.All(kind => HerdRuntime.IsHerdKind((short)kind)));
            var arts = kinds.Select(HerdRuntime.SpawnArt).ToArray();
            Assert.IsTrue(arts.All(art => art != 0));
            Assert.AreEqual(arts.Length, arts.Distinct().Count());
            Assert.IsFalse(HerdRuntime.IsHerdKind(2), "Troop group kinds are no herds.");
        }

        // A herd stays calm while any rule marks it; one rule never undoes another's mark.
        [TestMethod]
        public void CalmMarksOfSeveralRulesCombine()
        {
            IntPtr native = Marshal.AllocHGlobal(16);
            try
            {
                new Span<byte>((void*)native, 16).Fill(0xFF);
                var table = new HerdCalmTable((byte*)native, 16);
                byte* calm = (byte*)native;
                Assert.AreEqual(0, calm[5], "A new table starts with no calm herd.");
                int first = table.AddRule(), second = table.AddRule();

                Assert.IsTrue(table.Set(first, 5, true));
                Assert.IsTrue(table.Set(first, 5, true), "Repeating a mark is accepted and counted once.");
                Assert.IsTrue(table.Set(second, 5, true));
                Assert.AreEqual(1, calm[5]);
                Assert.IsTrue(table.Set(first, 5, false));
                Assert.AreEqual(1, calm[5], "The second rule still keeps the herd calm.");
                table.Clear(second);
                Assert.AreEqual(0, calm[5]);

                Assert.IsTrue(table.Set(first, 7, true) && table.Set(second, 8, true));
                table.Clear(first);
                Assert.AreEqual(0, calm[7]);
                Assert.AreEqual(1, calm[8], "Clearing one rule keeps the marks of the others.");
                table.ClearAll();
                Assert.AreEqual(0, calm[8]);
                Assert.IsTrue(table.Set(second, 8, true), "Rules still work after a map reset.");
                Assert.AreEqual(1, calm[8]);

                Assert.IsFalse(table.Set(first, 0, true), "Herd 0 is Vanilla's reserved slot.");
                Assert.IsFalse(table.Set(first, 16, true));
                Assert.IsFalse(table.Set(2, 5, true), "An unknown rule changes nothing.");
            }
            finally { Marshal.FreeHGlobal(native); }
        }

        // Vanilla walks to route[index] without looking at the count, so a route that a handler emptied must
        // not keep a stale first waypoint; an unchanged empty Vanilla route stays as Vanilla built it.
        [TestMethod]
        public void RouteChangesKeepVanillasRouteReadersSafe()
        {
            IntPtr memory = Marshal.AllocHGlobal(sizeof(GameTribe));
            try
            {
                var record = (GameTribe*)memory;
                new Span<byte>((void*)memory, sizeof(GameTribe)).Clear();
                short* waypoints = (short*)&record->r_PatrolPoint1TileX;
                for (int index = 0; index < 3; index++) { waypoints[index * 2] = (short)(100 + index); waypoints[index * 2 + 1] = (short)(200 + index); }
                record->r_CurrentPatrolPoints = 3;

                var access = new HerdRuntime.RouteAccess(record);
                Assert.AreEqual(3, access.Count);
                Assert.AreEqual(101, access.Get(1).TileX);
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => access.Get(3));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => access.Truncate(4));
                access.Truncate(1);
                access.Close(10, 20);
                Assert.AreEqual(1, record->r_CurrentPatrolPoints);
                Assert.AreEqual(100, waypoints[0], "A kept first waypoint stays.");
                Assert.ThrowsExactly<InvalidOperationException>(() => { _ = access.Count; }, "The route is only valid during its handler.");

                access = new HerdRuntime.RouteAccess(record);
                access.Truncate(0);
                access.Close(10, 20);
                Assert.AreEqual(0, record->r_CurrentPatrolPoints);
                Assert.AreEqual(10, waypoints[0]);
                Assert.AreEqual(20, waypoints[1], "An emptied route points at the leader's own tile.");

                waypoints[0] = 55; waypoints[1] = 66;
                access = new HerdRuntime.RouteAccess(record);
                access.Close(10, 20);
                Assert.AreEqual(55, waypoints[0], "An empty route nobody changed stays Vanilla's.");

                access = new HerdRuntime.RouteAccess(record);
                access.Truncate(0);
                access.Close(-1, -1);
                Assert.AreEqual(55, waypoints[0], "Without a leader there is no tile to use.");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
}
