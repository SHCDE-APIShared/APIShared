using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared.Patching;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace APISharedTests
{
    // Each test patches its own private fixture method; hooks stay rooted for the process, as in the game.
    [TestClass]
    [DoNotParallelize]
    public sealed class SafeIlHookTests
    {
        private const string Owner = "APISharedTests";
        private static volatile bool failRebuild;

        [MethodImpl(MethodImplOptions.NoInlining)] private static int Installed() => 1;
        [MethodImpl(MethodImplOptions.NoInlining)] private static int Shared() => 1;
        [MethodImpl(MethodImplOptions.NoInlining)] private static int Rejected() => 1;
        [MethodImpl(MethodImplOptions.NoInlining)] private static int Partial() => 1;

        private static MethodInfo Fixture(string name) =>
            typeof(SafeIlHookTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // Adds a constant to the returned value: a visible, composable edit.
        private static void AddBeforeReturn(ILContext il, int value)
        {
            var cursor = new ILCursor(il);
            cursor.GotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ret);
            cursor.Emit(OpCodes.Ldc_I4, value);
            cursor.Emit(OpCodes.Add);
        }

        // Edits first, then fails: the worst case for the other hooks of the method.
        private static void EditThenThrow(ILContext il)
        {
            AddBeforeReturn(il, 1000);
            throw new InvalidOperationException("planned failure");
        }

        [TestMethod]
        public void AnInstalledEditIsPublishedAndReported()
        {
            Assert.IsTrue(SafeIlHook.TryInstall(Owner, "APISharedTests.Installed", Fixture(nameof(Installed)),
                il => AddBeforeReturn(il, 1), null, out SafeIlHook hook, out string reason), reason);
            Assert.AreEqual(2, Installed());
            Assert.IsTrue(hook.IsApplied);
            Assert.AreEqual("Applied", hook.Diagnostic);
            Assert.AreEqual(0, hook.Failures);
        }

        [TestMethod]
        public void AFailingFirstEditInstallsNothing()
        {
            Assert.IsFalse(SafeIlHook.TryInstall(Owner, "APISharedTests.Rejected", Fixture(nameof(Rejected)),
                EditThenThrow, null, out SafeIlHook hook, out string reason));
            Assert.IsNull(hook);
            StringAssert.Contains(reason, "planned failure");
            Assert.AreEqual(1, Rejected(), "The method keeps its original body.");
        }

        [TestMethod]
        public void AFailingRebuildKeepsEveryOtherPatchAndRecoversLater()
        {
            MethodInfo method = Fixture(nameof(Shared));
            var other = new ILHook(method, il => AddBeforeReturn(il, 10), new ILHookConfig { ManualApply = true, ID = "APISharedTests.Other" });
            other.Apply();
            Assert.IsTrue(SafeIlHook.TryInstall(Owner, "APISharedTests.Shared", method,
                il => { if (failRebuild) EditThenThrow(il); else AddBeforeReturn(il, 100); }, null, out SafeIlHook hook, out string reason), reason);
            Assert.AreEqual(111, Shared());

            // A later patch of the same method reruns every manipulator. Ours fails and must not take the others down.
            failRebuild = true;
            var later = new ILHook(method, il => AddBeforeReturn(il, 5), new ILHookConfig { ManualApply = true, ID = "APISharedTests.Later" });
            later.Apply();
            Assert.AreEqual(16, Shared(), "Both other edits stay; ours and its partial edit are gone.");
            Assert.IsFalse(hook.IsApplied);
            StringAssert.Contains(hook.Diagnostic, "planned failure");
            Assert.AreEqual(1, hook.Failures);

            // The next rebuild runs the edit again from the original IL.
            failRebuild = false;
            var recovery = new ILHook(method, il => { }, new ILHookConfig { ManualApply = true, ID = "APISharedTests.Recovery" });
            recovery.Apply();
            Assert.AreEqual(116, Shared());
            Assert.IsTrue(hook.IsApplied);
            GC.KeepAlive(other); GC.KeepAlive(later); GC.KeepAlive(recovery);
        }

        [TestMethod]
        public void TryEditRestoresAPartiallyEditedBody()
        {
            MethodInfo method = Fixture(nameof(Partial));
            bool kept = false;
            var hook = new ILHook(method, il =>
            {
                AddBeforeReturn(il, 10);
                kept = !SafeIlHook.TryEdit(il, EditThenThrow, out Exception error) && error.Message == "planned failure";
            }, new ILHookConfig { ManualApply = true, ID = "APISharedTests.Partial" });
            hook.Apply();
            Assert.IsTrue(kept);
            Assert.AreEqual(11, Partial(), "The failed part is rolled back; the earlier edit of the same manipulator stays.");
            GC.KeepAlive(hook);
        }
    }
}
