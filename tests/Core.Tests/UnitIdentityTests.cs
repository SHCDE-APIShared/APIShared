using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class UnitIdentityTests
    {
        private sealed class Fixture
        {
            internal object World = new object();
            internal bool Allowed = true;
            internal readonly Dictionary<int, UnitIdentityProbe> Units = new Dictionary<int, UnitIdentityProbe>();
            internal readonly UnitIdentityRegistry Registry;
            internal Fixture() { Registry = new UnitIdentityRegistry(Read, () => World, () => Allowed, 1); }
            private UnitIdentityProbe Read(int slot) => Units.TryGetValue(slot, out var unit) ? unit : default;
            internal void Unit(int slot = 7, uint globalId = 42, int type = 2, int transform = 0, bool alive = true) =>
                Units[slot] = new UnitIdentityProbe { Alive = alive, GlobalId = globalId, NativeType = type, TransformType = transform };
            internal UnitVariantDefinition Variant(string owner = "owner", string id = "ranger", int carrier = 2)
            {
                Assert.IsTrue(Registry.Register(owner, id, carrier, out var result, out var reason), reason);
                return result;
            }
            internal void Mark(UnitVariantDefinition definition, int slot = 7, uint globalId = 42) =>
                Assert.IsTrue(Registry.Mark(definition, slot, globalId, out var reason), reason);
        }

        [TestMethod]
        public void RegistrationUsesOwnerLocalStableKeysAndRejectsInvalidOrDuplicateDefinitions()
        {
            var f = new Fixture();
            f.Variant(); f.Variant("other"); f.Variant("ow", "nerranger");
            Assert.IsFalse(f.Registry.Register("owner", "ranger", 2, out _, out _));
            Assert.IsFalse(f.Registry.Register(" ", "ranger", 2, out _, out _));
            Assert.IsFalse(f.Registry.Register("owner", new string('x', 129), 2, out _, out _));
            Assert.IsFalse(f.Registry.Register("owner", "other", 65536, out _, out _));
            Assert.IsFalse(f.Registry.Register("owner", "bad\ud800", 2, out _, out _));
            Assert.IsFalse(f.Registry.Register("bad\udc00", "variant", 2, out _, out _));
            Assert.IsTrue(f.Registry.Register("owner", "valid\ud83d\ude00", 2, out _, out _));
        }

        [TestMethod]
        public void OneExactVariantIsIdempotentAndForeignClaimsCannotOverwriteOrRemoveIt()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); var foreign = f.Variant("foreign");
            f.Mark(own); f.Mark(own);
            Assert.IsFalse(f.Registry.Mark(foreign, 7, 42, out string reason));
            StringAssert.Contains(reason, "owner/ranger");
            Assert.IsFalse(f.Registry.Unmark(foreign, 7, 42, out _));
            Assert.IsTrue(f.Registry.TryGet("owner", 7, 42, out var identity));
            Assert.AreEqual("ranger", identity.VariantId);
            Assert.IsFalse(f.Registry.TryGet("foreign", 7, 42, out _));
            Assert.IsTrue(f.Registry.Unmark(own, 7, 42, out _));
            Assert.IsTrue(f.Registry.Unmark(own, 7, 42, out _));
        }

        [TestMethod]
        public void UnauthorizedMutationAndWrongNativeIdentityPreserveExistingMarks()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            f.Allowed = false;
            Assert.IsFalse(f.Registry.Unmark(own, 7, 42, out _));
            Assert.IsFalse(f.Registry.Mark(own, 7, 42, out _));
            f.Allowed = true;
            Assert.IsFalse(f.Registry.Mark(own, 7, 43, out _));
            Assert.IsFalse(f.Registry.Mark(own, 0, 42, out _));
            Assert.IsTrue(f.Registry.TryGet("owner", 7, 42, out _));
        }

        [TestMethod]
        public void PaidPeasantIdentitySurvivesOnlyTheNativeMatchingTransformation()
        {
            var f = new Fixture(); f.Unit(type: 1, transform: 2); var own = f.Variant(); f.Mark(own);
            Assert.IsTrue(f.Registry.TryGetLive("owner", 7, 42, out var pending));
            Assert.IsTrue(pending.IsPendingRecruitment);
            var saved = f.Registry.Encode();
            f.Registry.Clear(); f.Registry.BindWorld(f.World, UnitIdentityRegistry.Decode(saved));
            Assert.IsTrue(f.Registry.TryGet("owner", 7, 42, out pending));
            Assert.IsTrue(pending.IsPendingRecruitment, "A paid recruit must survive save/load before changing carrier.");
            f.Unit(type: 2); f.Registry.Reconcile();
            Assert.IsTrue(f.Registry.TryGet("owner", 7, 42, out var settled));
            Assert.IsFalse(settled.IsPendingRecruitment);
            f.Unit(type: 1, transform: 3); f.Registry.Reconcile();
            Assert.IsFalse(f.Registry.TryGet("owner", 7, 42, out _), "Disband/assignment cannot inherit the variant.");
            Assert.IsFalse(f.Registry.Mark(own, 7, 42, out _));
        }

        [TestMethod]
        public void SlotReuseAndUnsignedNativeIdsNeverInheritAnotherIdentity()
        {
            var f = new Fixture(); f.Unit(globalId: uint.MaxValue); var own = f.Variant(); f.Mark(own, globalId: uint.MaxValue);
            Assert.IsTrue(f.Registry.TryGetLive("owner", 7, uint.MaxValue, out _));
            f.Unit(globalId: 99);
            Assert.IsFalse(f.Registry.TryGetLive("owner", 7, uint.MaxValue, out _));
            Assert.IsFalse(f.Registry.Unmark(own, 7, 99, out _));
            f.Registry.Reconcile();
            Assert.IsFalse(f.Registry.TryGet("owner", 7, uint.MaxValue, out _));
            f.Mark(own, globalId: 99);
            Assert.AreEqual(1, f.Registry.Members(own).Count);
        }

        [TestMethod]
        public void ACancelledDeleteKeepsTheLiveExactUnitButConfirmedDeathRemovesIt()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            f.Registry.Reconcile();
            Assert.IsTrue(f.Registry.TryGetLive("owner", 7, 42, out _), "No deletion is inferred from a request or event alone.");
            f.Unit(alive: false); f.Registry.Reconcile();
            Assert.IsFalse(f.Registry.TryGet("owner", 7, 42, out _));
        }

        [TestMethod]
        public void WorldInvalidationImmediatelyHidesSnapshotsAndReusedIdsStartFresh()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            var retained = f.Registry.Members(own);
            f.World = null;
            Assert.IsFalse(f.Registry.TryGet("owner", 7, 42, out _));
            Assert.AreEqual(0, f.Registry.Members(own).Count);
            f.World = new object();
            Assert.IsFalse(f.Registry.TryGetLive("owner", 7, 42, out _));
            f.Mark(own); Assert.AreEqual(1, f.Registry.Members(own).Count);
            Assert.AreEqual(1, retained.Count, "A detached previously returned list remains a point-in-time value.");
            f.Registry.Clear(); // BeforeLoad, End, NoRecord and OutOfScope all retire prior-world marks.
            Assert.IsFalse(f.Registry.TryGet("owner", 7, 42, out _));
        }

        [TestMethod]
        public void CanonicalSaveBytesIgnoreRegistrationAndMarkingOrder()
        {
            var a = new Fixture(); var b = new Fixture();
            var aa = a.Variant("a"); var ab = a.Variant("b");
            var bb = b.Variant("b"); var ba = b.Variant("a");
            a.Unit(2, 200); a.Unit(9, 900); b.Unit(2, 200); b.Unit(9, 900);
            a.Mark(aa, 2, 200); a.Mark(ab, 9, 900);
            b.Mark(bb, 9, 900); b.Mark(ba, 2, 200);
            CollectionAssert.AreEqual(a.Registry.Encode(), b.Registry.Encode());
        }

        [TestMethod]
        public void AbsentOwnersAndUnknownVariantsStayDormantAndRoundTripWithoutLosingClaims()
        {
            var source = new Fixture(); source.Unit(); source.Mark(source.Variant());
            byte[] bytes = source.Registry.Encode();
            var absent = new Fixture(); absent.Unit(); var other = absent.Variant("other");
            absent.Registry.BindWorld(absent.World, UnitIdentityRegistry.Decode(bytes));
            Assert.IsFalse(absent.Registry.TryGet("owner", 7, 42, out _));
            Assert.IsFalse(absent.Registry.Mark(other, 7, 42, out _), "Dormant exclusive identity still reserves the exact instance.");
            CollectionAssert.AreEqual(bytes, absent.Registry.Encode());
            var restored = new Fixture(); restored.Unit(); restored.Variant();
            restored.Registry.BindWorld(restored.World, UnitIdentityRegistry.Decode(absent.Registry.Encode()));
            Assert.IsTrue(restored.Registry.TryGetLive("owner", 7, 42, out _));
        }

        [TestMethod]
        public void MalformedPayloadsCannotPartlyReplaceStateAndStaleRestoreRowsAreDropped()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            byte[] bytes = f.Registry.Encode();
            var trailing = new byte[bytes.Length + 1]; Array.Copy(bytes, trailing, bytes.Length);
            Assert.ThrowsExactly<InvalidDataException>(() => UnitIdentityRegistry.Decode(trailing));
            Assert.ThrowsExactly<InvalidDataException>(() => UnitIdentityRegistry.Decode(new byte[7]));
            var badCount = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, badCount, 4, 4);
            Assert.ThrowsExactly<InvalidDataException>(() => UnitIdentityRegistry.Decode(badCount));
            Assert.IsTrue(f.Registry.TryGetLive("owner", 7, 42, out _));
            f.Unit(globalId: 99);
            f.Registry.BindWorld(f.World, UnitIdentityRegistry.Decode(bytes));
            Assert.IsFalse(f.Registry.TryGet("owner", 7, 42, out _));
        }

        [TestMethod]
        public void SerializedByteBudgetIncludesDormantRestoredRowsAndRejectsBeforeMutation()
        {
            var f = new Fixture();
            string owner = new string('o', 128), id = new string('v', 128);
            var own = f.Variant(owner, id);
            var rows = new List<UnitIdentityRow>();
            // Each long-key row is exactly 272 bytes, including both 7-bit length prefixes.
            int fits = (UnitIdentityRegistry.MaximumBytes - 8) / 272;
            for (int slot = 1; slot <= fits; slot++)
            {
                f.Unit(slot, (uint)slot);
                rows.Add(new UnitIdentityRow { Owner = owner, Variant = id, NativeType = 2, UnitId = slot, GlobalId = (uint)slot });
            }
            f.Registry.BindWorld(f.World, rows);
            Assert.AreEqual(8 + fits * 272, f.Registry.Encode().Length);
            f.Unit(fits + 1, (uint)(fits + 1));
            Assert.IsFalse(f.Registry.Mark(own, fits + 1, (uint)(fits + 1), out _));
            Assert.AreEqual(fits, f.Registry.Members(own).Count);
            Assert.IsTrue(f.Registry.Unmark(own, 1, 1, out _));
            Assert.IsTrue(f.Registry.Mark(own, fits + 1, (uint)(fits + 1), out _));
            byte[] saved = f.Registry.Encode();
            var dormant = new Fixture();
            for (int slot = 2; slot <= fits + 1; slot++) dormant.Unit(slot, (uint)slot);
            dormant.Registry.BindWorld(dormant.World, UnitIdentityRegistry.Decode(saved));
            var different = dormant.Variant("other", "other"); dormant.Unit(fits + 2, (uint)(fits + 2));
            // 8 spare bytes cannot hold even a short row; absent owners still consume save budget.
            Assert.IsFalse(dormant.Registry.Mark(different, fits + 2, (uint)(fits + 2), out _));
            CollectionAssert.AreEqual(saved, dormant.Registry.Encode());
        }

        [TestMethod]
        public void EarlierNativeLoadedMutationAppliesStagedRestoreBeforeClaimingAnotherUnit()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            byte[] saved = f.Registry.Encode();
            f.Registry.Clear(); f.World = null;
            f.Registry.StageRestore(UnitIdentityRegistry.Decode(saved));
            f.World = new object(); f.Unit(8, 88);
            // NativeWorld was published, but the identity service's observer has not run yet.
            f.Mark(own, 8, 88);
            f.Registry.ApplyPrepared();
            Assert.IsTrue(f.Registry.TryGetLive("owner", 7, 42, out _));
            Assert.IsTrue(f.Registry.TryGetLive("owner", 8, 88, out _));
            Assert.AreEqual(2, f.Registry.Members(own).Count);
        }

        [TestMethod]
        public void EarlierNativeLoadedUnmarkAndCaptureConsumeThePendingRestoreExactlyOnce()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            byte[] saved = f.Registry.Encode();
            f.Registry.Clear(); f.World = new object();
            f.Registry.StageRestore(UnitIdentityRegistry.Decode(saved));
            CollectionAssert.AreEqual(saved, f.Registry.Capture(), "A save in an earlier NativeLoaded observer cannot write an empty identity set.");
            f.Registry.ApplyPrepared(); Assert.IsTrue(f.Registry.TryGetLive("owner", 7, 42, out _));
            f.Registry.Clear(); f.World = new object();
            f.Registry.StageRestore(UnitIdentityRegistry.Decode(saved));
            Assert.IsTrue(f.Registry.Unmark(own, 7, 42, out _));
            f.Registry.ApplyPrepared();
            Assert.IsFalse(f.Registry.TryGet("owner", 7, 42, out _), "An earlier successful unmark cannot be reversed by lifecycle observer order.");
        }

        [TestMethod]
        public void MultiplayerMutationNeedsTheMonitorAndNativeTickOrAuthenticatedChore()
        {
            Assert.IsFalse(UnitIdentityRegistry.AllowsMutation(false, true, false, true, true));
            Assert.IsFalse(UnitIdentityRegistry.AllowsMutation(true, false, false, true, true));
            Assert.IsTrue(UnitIdentityRegistry.AllowsMutation(true, true, false, false, false));
            Assert.IsFalse(UnitIdentityRegistry.AllowsMutation(true, true, true, false, false), "A generic quiescent work scope or local UI lock is insufficient.");
            Assert.IsTrue(UnitIdentityRegistry.AllowsMutation(true, true, true, true, false));
            Assert.IsTrue(UnitIdentityRegistry.AllowsMutation(true, true, true, false, true));
        }

        [TestMethod]
        public void StagedArchiveCannotBindToAnotherNativeMission()
        {
            object world = new object(); long session = 2;
            var unit = new UnitIdentityProbe { Alive = true, GlobalId = 42, NativeType = 2 };
            var registry = new UnitIdentityRegistry(_ => unit, () => world, () => true, 1, () => session);
            Assert.IsTrue(registry.Register("owner", "ranger", 2, out var own, out _));
            registry.StageRestore(new[] { new UnitIdentityRow { Owner = "owner", Variant = "ranger", NativeType = 2, UnitId = 7, GlobalId = 42 } }, 3);
            Assert.IsFalse(registry.Mark(own, 7, 42, out _));
            Assert.IsFalse(registry.Unmark(own, 7, 42, out _));
            Assert.IsNull(registry.Capture());
            Assert.IsFalse(registry.TryGet("owner", 7, 42, out _));
            session = 3; world = new object();
            Assert.IsTrue(registry.ApplyPrepared());
            Assert.IsTrue(registry.TryGetLive("owner", 7, 42, out _));
        }

        [TestMethod]
        public void CaptureRefusesAWorldInvalidatedDuringNativeReconciliation()
        {
            object world = new object(); bool invalidate = false;
            var registry = new UnitIdentityRegistry(_ => {
                if (invalidate) world = null;
                return new UnitIdentityProbe { Alive = true, GlobalId = 42, NativeType = 2 };
            }, () => world, () => true, 1);
            Assert.IsTrue(registry.Register("owner", "ranger", 2, out var own, out _));
            Assert.IsTrue(registry.Mark(own, 7, 42, out _));
            byte[] original = registry.Encode();
            invalidate = true;
            Assert.IsNull(registry.Capture(), "A save must return no replacement payload if native-world invalidation raced its capture.");
            Assert.IsFalse(registry.IsCurrentWorldReady);
            invalidate = false; world = new object();
            registry.Clear(); registry.StageRestore(UnitIdentityRegistry.Decode(original));
            Assert.IsTrue(registry.TryGetLive("owner", 7, 42, out _));
        }

        [TestMethod]
        public void ReadinessIsNonmutatingAndProtectedLiveReadAppliesStagedData()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            byte[] saved = f.Registry.Encode();
            f.Registry.Clear(); f.World = new object(); f.Registry.StageRestore(UnitIdentityRegistry.Decode(saved));
            Assert.IsFalse(f.Registry.IsCurrentWorldReady);
            Assert.IsFalse(f.Registry.TryGet("owner", 7, 42, out _), "Presentation queries must not apply native-world state.");
            Assert.IsTrue(f.Registry.TryGetLive("owner", 7, 42, out _));
            Assert.IsTrue(f.Registry.IsCurrentWorldReady);
            Assert.IsTrue(f.Registry.ApplyPrepared(), "Later NativeLoaded application is harmless.");
            f.Registry.Clear(); f.Registry.StageRestore(Array.Empty<UnitIdentityRow>());
            Assert.IsFalse(f.Registry.IsCurrentWorldReady);
            Assert.IsTrue(f.Registry.ApplyPrepared());
            Assert.IsTrue(f.Registry.IsCurrentWorldReady, "A valid empty restored world is ready.");
        }

        // Incremental publication (one page per mark) must stay observably identical to a complete rebuild:
        // the same TryGet answers for every owner and slot, the same slot-ordered member lists, and earlier
        // retained member snapshots never change. The model below is independent of the registry's code.
        [TestMethod]
        public void IncrementalPublicationMatchesAnIndependentModelAndKeepsRetainedSnapshots()
        {
            var f = new Fixture();
            var variants = new[] { f.Variant("a", "ranger"), f.Variant("b", "ranger"), f.Variant("a", "scout", carrier: 3) };
            const int Slots = 300;
            var random = new Random(1234);
            var model = new Dictionary<int, (UnitVariantDefinition Variant, uint GlobalId)>();
            uint nextId = 1000;
            for (int slot = 1; slot <= Slots; slot++) f.Unit(slot, nextId++, type: random.Next(2, 4));
            // One dormant restored row (an owner that never registers) must stay invisible and be replaceable.
            f.Registry.BindWorld(f.World, new[] { new UnitIdentityRow { Owner = "absent", Variant = "x", NativeType = 2, UnitId = 5,
                GlobalId = f.Units[5].GlobalId } });
            var retained = new List<(UnitVariantDefinition Variant, IReadOnlyList<UnitIdentitySnapshot> List, int[] Slots)>();
            for (int step = 0; step < 4000; step++)
            {
                int slot = random.Next(1, Slots + 1);
                var variant = variants[random.Next(variants.Length)];
                var unit = f.Units[slot];
                switch (random.Next(6))
                {
                    case 0: case 1: case 2:
                        if (f.Registry.Mark(variant, slot, unit.GlobalId, out _)) model[slot] = (variant, unit.GlobalId);
                        break;
                    case 3:
                        if (f.Registry.Unmark(variant, slot, unit.GlobalId, out _) && model.TryGetValue(slot, out var held) &&
                            held.Variant == variant && held.GlobalId == unit.GlobalId) model.Remove(slot);
                        break;
                    case 4: // slot reuse: a new exact unit; the old mark is reconciled away
                        f.Unit(slot, nextId++, type: unit.NativeType);
                        f.Registry.Reconcile();
                        model.Remove(slot);
                        break;
                    default: // a peasant transforming into the variant's carrier becomes pending, then completes
                        if (model.TryGetValue(slot, out var marked))
                        {
                            f.Unit(slot, unit.GlobalId, type: 1, transform: marked.Variant.NativeType);
                            f.Registry.Reconcile();
                            f.Unit(slot, unit.GlobalId, type: marked.Variant.NativeType);
                            f.Registry.Reconcile();
                        }
                        break;
                }
                if (step % 500 == 499) // many deaths in one tick take the complete-rebuild path
                {
                    for (int k = 0; k < 40; k++)
                    {
                        int reused = random.Next(1, Slots + 1);
                        f.Unit(reused, nextId++, type: f.Units[reused].NativeType);
                        model.Remove(reused);
                    }
                    f.Registry.Reconcile();
                }
                if (step % 97 == 0)
                    foreach (var definition in variants)
                    {
                        var list = f.Registry.Members(definition);
                        var slots = new int[list.Count];
                        for (int i = 0; i < slots.Length; i++) slots[i] = list[i].UnitId;
                        retained.Add((definition, list, slots));
                    }
                if (step % 13 != 0) continue;
                foreach (var definition in variants)
                {
                    var expected = new List<int>();
                    foreach (var entry in model) if (entry.Value.Variant == definition) expected.Add(entry.Key);
                    expected.Sort();
                    var members = f.Registry.Members(definition);
                    var actual = new List<int>();
                    foreach (var member in members) actual.Add(member.UnitId);
                    CollectionAssert.AreEqual(expected, actual, "member enumeration at step " + step);
                    for (int i = 0; i < members.Count; i++) Assert.AreEqual(expected[i], members[i].UnitId, "member index at step " + step);
                }
                for (int s = 1; s <= Slots; s++)
                {
                    uint id = f.Units[s].GlobalId;
                    foreach (string owner in new[] { "a", "b", "absent" })
                    {
                        bool has = model.TryGetValue(s, out var entry) && entry.Variant.Owner == owner && entry.GlobalId == id;
                        Assert.AreEqual(has, f.Registry.TryGet(owner, s, id, out var found), "slot " + s + " owner " + owner + " at step " + step);
                        if (has) Assert.AreEqual(entry.Variant.Id, found.VariantId);
                    }
                }
            }
            foreach (var kept in retained)
            {
                Assert.AreEqual(kept.Slots.Length, kept.List.Count, "a retained member snapshot never changes");
                for (int i = 0; i < kept.Slots.Length; i++) Assert.AreEqual(kept.Slots[i], kept.List[i].UnitId);
            }
        }

        // One mark used to rebuild the whole published table (about 0.4 us per existing mark). It now copies a
        // fixed number of pages, so its allocation must not grow with the number of marks.
        [TestMethod]
        public void OneMarkCostDoesNotGrowWithTheMarkCount()
        {
            long Measure(int existing)
            {
                var f = new Fixture(); var own = f.Variant();
                for (int slot = 1; slot <= existing + 1; slot++) f.Unit(slot, (uint)(5000 + slot));
                for (int slot = 1; slot <= existing; slot++) f.Mark(own, slot, (uint)(5000 + slot));
                int probe = existing + 1; uint id = (uint)(5000 + probe);
                for (int i = 0; i < 16; i++) { f.Mark(own, probe, id); f.Registry.Unmark(own, probe, id, out _); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 64; i++) { f.Mark(own, probe, id); f.Registry.Unmark(own, probe, id, out _); }
                watch.Stop();
                long perOperation = (GC.GetAllocatedBytesForCurrentThread() - before) / 128;
                Console.WriteLine("existing=" + existing + " bytes/op=" + perOperation + " us/op=" + (watch.Elapsed.TotalMilliseconds * 1000 / 128).ToString("F2"));
                return perOperation;
            }
            long small = Measure(200), large = Measure(4000);
            Assert.IsTrue(large < 64 * 1024, "one mark allocates a bounded number of pages: " + large + " bytes");
            Assert.IsTrue(large < 2 * small + 4096, "cost must not scale with the mark count: " + small + " vs " + large + " bytes");
        }

        [TestMethod]
        public void WarmPresentationLiveAndUnchangedReconciliationPathsAllocateNothing()
        {
            var f = new Fixture(); f.Unit(); var own = f.Variant(); f.Mark(own);
            for (int i = 0; i < 1000; i++)
            { f.Registry.TryGet("owner", 7, 42, out _); f.Registry.TryGetLive("owner", 7, 42, out _); f.Registry.Members(own); f.Registry.Reconcile(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            { f.Registry.TryGet("owner", 7, 42, out _); f.Registry.TryGetLive("owner", 7, 42, out _); f.Registry.Members(own); f.Registry.Reconcile(); }
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
