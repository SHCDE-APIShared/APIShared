using System;
using System.Linq;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noesis;

namespace APISharedTests
{
    // Production metadata, lease and layout policies; no unattached native Noesis tree.
    [TestClass]
    public sealed class AllyGoodsFrameTests
    {
        [TestMethod]
        public void ExplicitFiniteMetricsAreRetainedWithoutChoosingVanillaThemeValues()
        {
            var padding = new Thickness(2.5f, 3.5f, 4.5f, 5.5f);
            var overflow = new Thickness(7, 11, 13, 17);
            var options = new AllyGoodsFrameOptions(padding, overflow, 537.5f, 9.5f, 6.25f);
            Assert.AreEqual(padding, options.ContentPadding); Assert.AreEqual(overflow, options.VisualOverflow);
            Assert.AreEqual(537.5f, options.MaximumExtensionHeight);
            Assert.AreEqual(9.5f, options.ViewportMargin); Assert.AreEqual(6.25f, options.SectionSpacing);
            var zero = new AllyGoodsFrameOptions(new Thickness(0), new Thickness(0), float.Epsilon, 0, 0);
            Assert.AreEqual(float.Epsilon, zero.MaximumExtensionHeight);
            Assert.AreEqual(new Thickness(0), zero.ContentPadding); Assert.AreEqual(0f, zero.ViewportMargin);
        }

        [TestMethod]
        public void InvalidScalarAndEveryInvalidThicknessSideAreRejected()
        {
            foreach (float invalid in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Options(invalid, 0));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Options(100, invalid));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    new AllyGoodsFrameOptions(new Thickness(0), new Thickness(0), 100, 0, invalid));
                for (int side = 0; side < 4; side++)
                {
                    var values = new float[4]; values[side] = invalid;
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AllyGoodsFrameOptions(
                        new Thickness(values[0], values[1], values[2], values[3]), new Thickness(0), 100, 0, 0));
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AllyGoodsFrameOptions(
                        new Thickness(0), new Thickness(values[0], values[1], values[2], values[3]), 100, 0, 0));
                }
            }
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Options(0, 0));
        }

        [TestMethod]
        public void RegistrationUsesOrderedImmutableSnapshotsAndIndependentNamespaces()
        {
            var frames = new AllyGoodsFrameRegistry(); var options = Options();
            Assert.IsFalse(frames.TryRegister("", "id", _ => null, options, 0, out _, out _));
            Assert.IsFalse(frames.TryRegister("owner", " ", _ => null, options, 0, out _, out _));
            Assert.IsFalse(frames.TryRegister("owner", "id", null, options, 0, out _, out _));
            Assert.IsFalse(frames.TryRegister("owner", "id", _ => null, null, 0, out _, out _));
            Assert.IsTrue(frames.TryRegister("z", "same", _ => null, options, 0, out var first, out _));
            var previous = frames.Entries;
            Assert.IsTrue(frames.TryRegister("a", "zeta", _ => null, options, 0, out _, out _));
            Assert.IsTrue(frames.TryRegister("a", "alpha", _ => null, options, 0, out _, out _));
            Assert.IsTrue(frames.TryRegister("A", "case", _ => null, options, 0, out _, out _));
            Assert.IsTrue(frames.TryRegister("priority", "first", _ => null, options, -2, out _, out _));
            CollectionAssert.AreEqual(new[] { "priority/first", "A/case", "a/alpha", "a/zeta", "z/same" },
                frames.Entries.Select(entry => entry.Owner + "/" + entry.Id).ToArray());
            Assert.AreEqual(1, previous.Length); Assert.AreSame(first, previous[0]); Assert.AreSame(options, first.Options);
            Assert.IsFalse(frames.TryRegister("z", "same", _ => null, options, -10, out _, out _));
            Assert.IsTrue(frames.TryRegister("another", "same", _ => null, options, 0, out _, out _));
            Assert.IsTrue(new AllyGoodsSectionRegistry().TryRegister("z", "same", _ => null, 0, out _, out _));
            Assert.IsTrue(new AllyGoodsSectionRegistry().TryRegister("z", "same", _ => null,
                AllyGoodsSectionPlacement.GoodsDialogBottom, 0, out _, out _));
            Assert.IsTrue(new AllyGoodsAdornmentRegistry().TryRegister("z", "same", (_, __) => null, 0, out _, out _));
        }

        [TestMethod]
        public void FirstEnabledArbitrationStartsDisabledAndNeverInvokesFactories()
        {
            var frames = new AllyGoodsFrameRegistry(); int calls = 0;
            Assert.IsTrue(frames.TryRegister("later", "frame", _ => { calls++; return null; }, Options(), 10, out var later, out _));
            Assert.IsTrue(frames.TryRegister("first", "frame", _ => { calls++; throw new InvalidOperationException(); }, Options(), -1, out var first, out _));
            Assert.IsFalse(first.Enabled); Assert.IsFalse(later.Enabled); Assert.IsNull(frames.FirstEnabled());
            later.Enabled = true; Assert.AreSame(later, frames.FirstEnabled());
            first.Enabled = true; Assert.AreSame(first, frames.FirstEnabled());
            // The chosen provider's failed/declined visual is not a reason to select another owner.
            first.AttemptRevision = first.Revision; first.Visual = null;
            Assert.AreSame(first, frames.FirstEnabled());
            first.Enabled = false; Assert.AreSame(later, frames.FirstEnabled());
            later.Enabled = false; Assert.IsNull(frames.FirstEnabled()); Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void OpacityLeaseRestoresOriginalDefaultSentinelOrFloatOnlyOnce()
        {
            object unset = new object();
            foreach (object original in new[] { unset, (object)0.37f, 0f })
            {
                var target = new Opacity { Value = original }; var lease = new AllyGoodsFrameOpacityLease(target);
                lease.Release(); Assert.AreEqual(0, target.Restores);
                Assert.IsTrue(lease.TryAcquire()); Assert.IsTrue(lease.IsOwned); Assert.AreEqual(0f, target.Value);
                Assert.IsTrue(lease.TryAcquire()); Assert.AreEqual(1, target.Hides, "Repeated acquisition must retain the first snapshot.");
                lease.Release(); Assert.AreSame(original, target.Value); Assert.AreEqual(1, target.Restores);
                lease.Release(); Assert.AreEqual(1, target.Restores); Assert.IsFalse(lease.IsOwned);
            }
        }

        [TestMethod]
        public void BoundOpacityIsRefusedAndCanBeAcquiredOnlyAfterTheBindingIsRemoved()
        {
            object expression = new object(); var target = new Opacity { Value = expression, Bound = true };
            var lease = new AllyGoodsFrameOpacityLease(target);
            Assert.IsFalse(lease.TryAcquire()); Assert.IsFalse(lease.IsOwned); lease.Release();
            Assert.AreSame(expression, target.Value); Assert.AreEqual(0, target.Hides); Assert.AreEqual(0, target.Restores);
            object unbound = 0.61f; target.Bound = false; target.Value = unbound;
            Assert.IsTrue(lease.TryAcquire()); lease.Release();
            Assert.AreSame(unbound, target.Value); Assert.AreEqual(1, target.Hides); Assert.AreEqual(1, target.Restores);
        }

        [TestMethod]
        public void AcquisitionFailureBeforeOrAfterMutationPreservesTheOriginalAndReleasesOwnership()
        {
            foreach (bool afterMutation in new[] { false, true })
            {
                object original = 0.61f;
                var target = new Opacity { Value = original, ThrowBeforeHide = !afterMutation, ThrowAfterHide = afterMutation };
                var lease = new AllyGoodsFrameOpacityLease(target);
                Assert.ThrowsExactly<InvalidOperationException>(() => lease.TryAcquire());
                Assert.AreSame(original, target.Value); Assert.IsFalse(lease.IsOwned);
                Assert.AreEqual(afterMutation ? 1 : 0, target.Restores);
                lease.Release(); Assert.AreEqual(afterMutation ? 1 : 0, target.Restores);
                target.ThrowBeforeHide = target.ThrowAfterHide = false;
                Assert.IsTrue(lease.TryAcquire()); lease.Release();
                Assert.AreSame(original, target.Value); Assert.AreEqual(afterMutation ? 2 : 1, target.Restores);
            }
        }

        [TestMethod]
        public void ForeignLocalValuesAndNewBindingsAreNotOverwrittenOnLostOwnership()
        {
            foreach (object foreign in new object[] { 0.71f, new object() })
            {
                var target = new Opacity { Value = 0.43f }; var lease = new AllyGoodsFrameOpacityLease(target);
                Assert.IsTrue(lease.TryAcquire()); target.Value = foreign;
                Assert.IsFalse(lease.IsOwned); Assert.IsFalse(lease.TryAcquire());
                Assert.AreEqual(1, target.Hides); lease.Release(); lease.Release();
                Assert.AreSame(foreign, target.Value); Assert.AreEqual(0, target.Restores);
            }
            var bound = new Opacity { Value = 0.43f }; var boundLease = new AllyGoodsFrameOpacityLease(bound);
            Assert.IsTrue(boundLease.TryAcquire()); bound.Bound = true;
            Assert.IsFalse(boundLease.IsOwned); Assert.IsFalse(boundLease.TryAcquire()); boundLease.Release(); boundLease.Release();
            Assert.IsTrue(bound.Bound); Assert.AreEqual(0f, bound.Value); Assert.AreEqual(0, bound.Restores);
        }

        [TestMethod]
        [DataRow(320f, 100f, 440f, 540f)]
        [DataRow(484f, 100f, 484f, 584f)]
        [DataRow(484f, 200f, 540f, 640f)]
        public void PaintedBottomIsReservedBeforeLegacyWithoutShrinkingForeignRoot(float root, float content, float legacyTop, float rootResult)
        {
            var options = Options(500, 8, new Thickness(3, 4, 5, 20));
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(root, 320, 1000, content, true, 100, true, options, out var plan));
            Assert.AreEqual(320f, plan.GoodsTop); Assert.AreEqual(content, plan.GoodsHeight);
            Assert.AreEqual(legacyTop, plan.LegacyTop); Assert.AreEqual(100f, plan.LegacyHeight);
            Assert.AreEqual(rootResult, plan.RootHeight);
            Assert.IsTrue(plan.LegacyTop >= plan.GoodsTop + plan.GoodsHeight + options.VisualOverflow.Bottom);
        }

        [TestMethod]
        public void CustomMaximumAndViewportMarginBoundTheContentAndItsPaintedEdge()
        {
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1200, 400, true, 40, true,
                Options(150, 12, new Thickness(0, 0, 0, 20)), out var capped));
            Assert.AreEqual(150f, capped.GoodsHeight); Assert.AreEqual(490f, capped.LegacyTop); Assert.AreEqual(530f, capped.RootHeight);
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1200, 400, true, 40, true,
                Options(500, 12, new Thickness(0, 0, 0, 17)), out var larger));
            Assert.AreEqual(400f, larger.GoodsHeight, "Explicit provider metrics are not capped by the old 260-unit theme.");
            Assert.AreEqual(737f, larger.LegacyTop); Assert.AreEqual(777f, larger.RootHeight);
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 600, 400, true, 100, true,
                Options(500, 12, new Thickness(0, 0, 0, 20)), out var viewport));
            Assert.AreEqual(248f, viewport.GoodsHeight); Assert.AreEqual(588f, viewport.LegacyTop);
            // Provider margin reserves the painted frame edge; legacy sections keep their existing 8-unit reserve.
            Assert.AreEqual(4f, viewport.LegacyHeight); Assert.AreEqual(592f, viewport.RootHeight);
            Assert.AreEqual(12f, 600 - (viewport.GoodsTop + viewport.GoodsHeight + 20));
        }

        [TestMethod]
        public void PaddingIsAlreadyMeasuredAndOtherOverflowSidesNeverMoveTheAnchor()
        {
            var decorated = new AllyGoodsFrameOptions(new Thickness(20, 30, 40, 50), new Thickness(31, 47, 59, 0), 500, 8, 19);
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1200, 120, true, 40, true, decorated, out var actual));
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1200, 120, true, 40, true, Options(500, 8), out var plain));
            Equal(plain, actual);
            Assert.AreEqual(120f, actual.GoodsHeight, "The renderer supplies desired content including padding; do not add it twice.");
        }

        [TestMethod]
        public void NullFrameRetainsOldLayoutAndInvisibleOrZeroHeightContentReservesNoOverflow()
        {
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1000, 100, true, 40, true, out var old));
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1000, 100, true, 40, true, null, out var compatible));
            Equal(old, compatible);
            var options = Options(500, 12, new Thickness(7, 11, 13, 20));
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 1000, 100, false, 0, false, options, out var collapsed));
            Assert.AreEqual(0f, collapsed.GoodsHeight); Assert.AreEqual(484f, collapsed.LegacyTop); Assert.AreEqual(484f, collapsed.RootHeight);
            Assert.IsTrue(AllyGoodsPanelLayoutPolicy.TryCompose(484, 320, 340, 100, true, 0, false, options, out var noRoom));
            Assert.AreEqual(0f, noRoom.GoodsHeight); Assert.AreEqual(484f, noRoom.LegacyTop); Assert.AreEqual(484f, noRoom.RootHeight);
        }

        private static AllyGoodsFrameOptions Options(float maximum = 500, float margin = 8, Thickness? overflow = null) =>
            new AllyGoodsFrameOptions(new Thickness(0), overflow ?? new Thickness(0), maximum, margin, 0);
        private static void Equal(AllyGoodsPanelLayoutPlan expected, AllyGoodsPanelLayoutPlan actual)
        {
            Assert.AreEqual(expected.GoodsTop, actual.GoodsTop); Assert.AreEqual(expected.GoodsHeight, actual.GoodsHeight);
            Assert.AreEqual(expected.LegacyTop, actual.LegacyTop); Assert.AreEqual(expected.LegacyHeight, actual.LegacyHeight);
            Assert.AreEqual(expected.RootHeight, actual.RootHeight);
        }
        private sealed class Opacity : IAllyGoodsFrameOpacity
        {
            internal object Value;
            internal bool Bound;
            internal bool ThrowBeforeHide, ThrowAfterHide;
            internal int Hides, Restores;
            public bool HasBinding => Bound;
            public object LocalValue => Value;
            public void SetHidden()
            {
                Hides++;
                if (ThrowBeforeHide) throw new InvalidOperationException("Before mutation");
                Value = 0f;
                if (ThrowAfterHide) throw new InvalidOperationException("After mutation");
            }
            public void Restore(object previous) { Restores++; Value = previous; }
        }
    }
}
