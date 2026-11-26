using System;
using System.Runtime.InteropServices;
using APIShared.Sight;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Managed contracts of APIShared.Sight. The native routines and the dispatch stub execute in
    // tests/WorkerProductionBonus.NativeTests/SightDispatchTests.cs against the audited image.
    [TestClass]
    [DoNotParallelize]
    public sealed unsafe class SightTests
    {
        // Unit points read the referenced Extender's GameUnit; it must match the raw fields Vanilla's callers read.
        [TestMethod]
        public void ExtenderUnitLayoutMatchesTheAuditedSightFields()
        {
            Assert.IsTrue(LineOfSightNative.TryValidateUnitLayout(out string reason), reason);
        }

        // Vanilla's two readers of the raw result: the Hunter target search (1..0x1B0) and the ranged
        // attack order (above 0). A wrong bound would make Hunters chase what Vanilla cannot see, or the reverse.
        [TestMethod]
        public void ResultRulesMatchVanillasReaders()
        {
            foreach (int hidden in new[] { int.MinValue, -1, 0, 0x1B1, LineOfSight.StepLimitResult, LineOfSight.SamePointResult })
                Assert.IsFalse(LineOfSight.IsVisibleForTargetQuery(hidden), "Target search hides " + hidden + ".");
            foreach (int seen in new[] { 1, 2, 0x1B0 })
                Assert.IsTrue(LineOfSight.IsVisibleForTargetQuery(seen), "Target search sees " + seen + ".");
            Assert.IsFalse(LineOfSight.IsVisibleForDirectAttack(0));
            Assert.IsFalse(LineOfSight.IsVisibleForDirectAttack(-1));
            Assert.IsTrue(LineOfSight.IsVisibleForDirectAttack(1));
            Assert.IsTrue(LineOfSight.IsVisibleForDirectAttack(LineOfSight.SamePointResult));
        }

        // A type blocks while any enabled rule exists for it; one owner never undoes another's rule, and a
        // disabled-only state restores Vanilla's marker the native stub treats as "no change".
        [TestMethod]
        public void BlockingRulesOfSeveralOwnersCombine()
        {
            IntPtr native = Marshal.AllocHGlobal(SightNativeAssembly.DispatchTypeCount);
            try
            {
                new Span<byte>((void*)native, SightNativeAssembly.DispatchTypeCount).Clear();
                var table = new SightBlockingTable((byte*)native);
                byte* bytes = (byte*)native;
                for (int index = 0; index < SightNativeAssembly.DispatchTypeCount; index++)
                    Assert.AreEqual(SightNativeAssembly.VanillaCase, bytes[index], "A new table changes no type.");

                const byte ordinary = SightNativeAssembly.OrdinaryHeightCase;
                Assert.IsFalse(table.Change(0, false, ordinary), "Disabling a type nobody enabled changes nothing.");
                Assert.AreEqual(SightNativeAssembly.VanillaCase, bytes[0]);
                Assert.IsTrue(table.Change(0, true, ordinary));
                Assert.IsFalse(table.Change(0, true, ordinary), "A second owner keeps the byte.");
                Assert.AreEqual(ordinary, bytes[0]);
                Assert.IsTrue(table.IsActive(0));
                Assert.IsFalse(table.Change(0, false, ordinary), "The other owner still keeps the type blocking.");
                Assert.AreEqual(ordinary, bytes[0]);
                Assert.IsTrue(table.Change(0, false, ordinary));
                Assert.AreEqual(SightNativeAssembly.VanillaCase, bytes[0], "Without enabled rules Vanilla's dispatch returns.");
                Assert.IsFalse(table.IsActive(0));
                Assert.AreEqual(SightNativeAssembly.VanillaCase, bytes[1], "Other types stay untouched.");
                Assert.IsFalse(table.Change(SightNativeAssembly.DispatchTypeCount, true, ordinary), "Out-of-range types are refused.");
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        // Points outside the map or with heights that could overflow Vanilla's interpolation never reach native code.
        [TestMethod]
        public void InvalidQueriesFailClosedWithoutNativeCall()
        {
            Assert.IsFalse(LineOfSight.TryEvaluate(new SightPoint(0, 0, 0), new SightPoint(8, 8, 0), (LineOfSightRoutine)5,
                out int result, out LineOfSightFailure failure));
            Assert.AreEqual(0, result);
            Assert.AreEqual(LineOfSight.IsAvailable ? LineOfSightFailure.InvalidRoutine : LineOfSightFailure.Unavailable, failure);
            // ID 0 is rejected before any unit manager or native access.
            Assert.IsFalse(LineOfSight.TryEvaluateUnits(0, 0, LineOfSightRoutine.Bidirectional, out result, out failure));
            Assert.AreEqual(LineOfSightFailure.InvalidUnit, failure);
            Assert.IsFalse(LineOfSight.TryGetUnitPoint(0, LineOfSight.ViewerEyeHeight, out _));
        }
    }
}
