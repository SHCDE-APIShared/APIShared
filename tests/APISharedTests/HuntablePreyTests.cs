using System.Linq;
using APIShared.Wildlife;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed unsafe class HuntablePreyTests
    {
        [TestMethod]
        public void OneRegistrationWinsEachTypeIndependentOfRegistrationOrder()
        {
            var high = Prey("z-owner", "high", eChimps.CHIMP_TYPE_RABBIT, priority: 5);
            var tiedWinner = Prey("a-owner", "a", eChimps.CHIMP_TYPE_RABBIT);
            var tiedLoser = Prey("a-owner", "b", eChimps.CHIMP_TYPE_RABBIT);
            var camel = Prey("b-owner", "camel", eChimps.CHIMP_TYPE_CAMEL);
            var all = new[] { tiedLoser, camel, tiedWinner, high };

            var catalog = HuntablePreyCatalogBuilder.Compile(all);
            var reordered = HuntablePreyCatalogBuilder.Compile(all.Reverse().ToArray());

            Assert.AreEqual(HuntablePreyRegistrationStatus.Admitted, high.Status);
            Assert.AreEqual(HuntablePreyRegistrationStatus.ExcludedByPriority, tiedWinner.Status);
            Assert.AreEqual(HuntablePreyRegistrationStatus.ExcludedByPriority, tiedLoser.Status);
            Assert.AreEqual(HuntablePreyRegistrationStatus.Admitted, camel.Status);
            Assert.IsTrue(catalog.TryGet((int)eChimps.CHIMP_TYPE_RABBIT, out var rabbit));
            Assert.AreEqual("high", rabbit.RegistrationId);
            Assert.AreEqual(catalog.Digest, reordered.Digest);
            // Deer and goat stay in every catalog; entries are ordered by type.
            CollectionAssert.AreEqual(catalog.Entries.Select(entry => entry.UnitType).OrderBy(type => type).ToArray(),
                catalog.Entries.Select(entry => entry.UnitType).ToArray());
            Assert.IsTrue(catalog.IsHuntable((int)eChimps.CHIMP_TYPE_DEER) && catalog.IsHuntable((int)eChimps.CHIMP_TYPE_GOAT));
            Assert.IsTrue(catalog.HasRegistrations);

            Assert.AreEqual("a", Compile(tiedLoser, tiedWinner).Entries.Single(entry => !entry.IsVanilla).RegistrationId);
        }

        [TestMethod]
        public void DisabledAndUnsupportedTypesNeverEnterTheCatalogOrTheReleaseTable()
        {
            var disabled = Prey("owner", "chicken", eChimps.CHIMP_TYPE_CHICKEN);
            Assert.IsTrue(disabled.SetEnabledFlag(false));
            var warDog = Prey("owner", "dog", eChimps.CHIMP_TYPE_WAR_DOG);
            var engineer = Prey("owner", "engineer", eChimps.CHIMP_TYPE_ENGINEER);

            var catalog = Compile(disabled, warDog, engineer);

            Assert.AreEqual(HuntablePreyRegistrationStatus.Disabled, disabled.Status);
            Assert.AreEqual(HuntablePreyRegistrationStatus.Unsupported, warDog.Status);
            Assert.AreEqual(HuntablePreyRegistrationStatus.Unsupported, engineer.Status);
            Assert.AreNotEqual("", warDog.Diagnostic);
            Assert.IsFalse(catalog.HasRegistrations);
            Assert.AreEqual(HuntablePreyCatalogBuilder.VanillaOnly.Digest, catalog.Digest);
            Assert.IsNull(HuntablePreyCatalogBuilder.BuildDemandTable(catalog));
        }

        [TestMethod]
        public void ARegisteredDeerReplacesTheBuiltInEntryButKeepsVanillasOwnRelease()
        {
            var deer = Prey("owner", "deer", eChimps.CHIMP_TYPE_DEER, handlingCost: 40);
            var rabbit = Prey("owner", "rabbit", eChimps.CHIMP_TYPE_RABBIT);
            var catalog = Compile(deer, rabbit);

            Assert.IsTrue(catalog.TryGet((int)eChimps.CHIMP_TYPE_DEER, out var entry));
            Assert.IsFalse(entry.IsVanilla);
            Assert.AreEqual(40, entry.HandlingCost);
            byte[] table = HuntablePreyCatalogBuilder.BuildDemandTable(catalog);
            Assert.AreEqual(0, table[(int)eChimps.CHIMP_TYPE_DEER]);
            Assert.AreEqual(1, table[(int)eChimps.CHIMP_TYPE_RABBIT]);
        }

        [TestMethod]
        public void GameplayDataChangesTheDigest()
        {
            string baseline = Compile(Prey("owner", "id", eChimps.CHIMP_TYPE_CAMEL)).Digest;
            Assert.AreEqual(baseline, Compile(Prey("owner", "id", eChimps.CHIMP_TYPE_CAMEL)).Digest);
            Assert.AreNotEqual(baseline, Compile(Prey("owner", "id", eChimps.CHIMP_TYPE_CAMEL, handlingCost: 1)).Digest);
            Assert.AreNotEqual(baseline, Compile(Prey("owner", "other", eChimps.CHIMP_TYPE_CAMEL)).Digest);
            Assert.AreNotEqual(baseline, Compile(Prey("owner", "id", eChimps.CHIMP_TYPE_RABBIT)).Digest);
            Assert.AreNotEqual(baseline, HuntablePreyCatalogBuilder.VanillaOnly.Digest);
        }

        // Vanilla's Hunter writes lease 2 on each of its updates; the release decrements once per visit.
        // A held prey must stay reserved for any number of ticks; a released one must become free within
        // two passes over the unit array, which is what Vanilla's deer update achieves within two updates.
        [TestMethod]
        public void HeldLeasesStayReservedAndReleasedLeasesExpireWithinTwoPasses()
        {
            const int length = 1000;
            const int heldSlot = 700, releasedSlot = 12, deerSlot = 300, otherSlot = 500;
            var units = new GameUnit[length];
            units[heldSlot].r_UnitChimp = eChimps.CHIMP_TYPE_RABBIT;
            units[releasedSlot].r_UnitChimp = eChimps.CHIMP_TYPE_RABBIT;
            units[deerSlot].r_UnitChimp = eChimps.CHIMP_TYPE_DEER;
            units[otherSlot].r_UnitChimp = eChimps.CHIMP_TYPE_HUNTER;
            byte[] table = HuntablePreyCatalogBuilder.BuildDemandTable(Compile(Prey("owner", "rabbit", eChimps.CHIMP_TYPE_RABBIT)));
            int passTicks = (length + HuntablePreyRuntime.SlotsPerTick - 1) / HuntablePreyRuntime.SlotsPerTick;

            fixed (GameUnit* first = units)
            {
                SetLease(first, releasedSlot, 2);
                SetLease(first, deerSlot, 2);
                SetLease(first, otherSlot, 7);
                int releasedAtTick = -1;
                for (int tick = 1; tick <= passTicks * 10; tick++)
                {
                    SetLease(first, heldSlot, 2); // the holding Hunter's update in this tick
                    HuntablePreyRuntime.ReleaseSlots(first, length, table, tick, 1);
                    Assert.IsTrue(Lease(first, heldSlot) > 0, "A held lease reached zero at tick " + tick + ".");
                    if (releasedAtTick < 0 && Lease(first, releasedSlot) == 0) releasedAtTick = tick;
                }
                Assert.IsTrue(releasedAtTick > 0 && releasedAtTick <= passTicks * 2, "released at " + releasedAtTick);
                Assert.AreEqual(2, Lease(first, deerSlot), "Deer keep Vanilla's own release.");
                Assert.AreEqual(7, Lease(first, otherSlot), "Unregistered types are never touched.");

                // A publication that covers several ticks visits the same slots as consecutive ticks.
                SetLease(first, releasedSlot, 2);
                HuntablePreyRuntime.ReleaseSlots(first, length, table, passTicks * 2, passTicks * 2);
                Assert.AreEqual(1, Lease(first, releasedSlot), "A long gap visits each slot at most once.");
            }
        }

        // The native displacement 0xAA4 maps to the public pointer through the same 0x65C header as two
        // independently audited fields the Hunter code reads next to it (AI state 0x918, target global 0x9F8).
        [TestMethod]
        public void ReservationOffsetUsesTheSameHeaderAsTheHuntersOtherFields()
        {
            GameUnit probe = default;
            byte* origin = (byte*)&probe;
            Assert.AreEqual(0x918 - HuntablePreyNativeContract.PublicHeader, (int)((byte*)&probe.r_AIState - origin));
            Assert.AreEqual(0x9F8 - HuntablePreyNativeContract.PublicHeader, (int)((byte*)&probe.r_AI_ContextTargetUnitGlobalId - origin));
            Assert.AreEqual(HuntablePreyNativeContract.UnitSlotSize, sizeof(GameUnit));
            Assert.AreEqual(HuntablePreyNativeContract.PublicHeader,
                System.Runtime.InteropServices.Marshal.OffsetOf(typeof(GameUnitManager), nameof(GameUnitManager.GameUnitArray)).ToInt32());
        }

        private static void SetLease(GameUnit* first, int slot, short value) =>
            *(short*)((byte*)(first + slot) + HuntablePreyRuntime.ReservationOffset) = value;

        private static short Lease(GameUnit* first, int slot) =>
            *(short*)((byte*)(first + slot) + HuntablePreyRuntime.ReservationOffset);

        private static HuntablePreyCatalog Compile(params HuntablePreyRegistration[] registrations) =>
            HuntablePreyCatalogBuilder.Compile(registrations);

        private static HuntablePreyRegistration Prey(string owner, string id, eChimps type, int priority = 0, int handlingCost = 0) =>
            new HuntablePreyRegistration(owner, id, new HuntablePreyDefinition((int)type, handlingCost), priority,
                HuntablePreyCatalogBuilder.UnsupportedReason((int)type));
    }
}
