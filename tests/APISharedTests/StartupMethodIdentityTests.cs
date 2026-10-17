using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class StartupMethodIdentityTests
    {
        private static ILHook fixtureHook;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Probe(Action callback) { callback(); GC.KeepAlive(callback); }

        [TestMethod]
        public void RegisteredBodyIsRecognizedByRuntimeIdentityButUnregisteredCopyIsNot()
        {
            var original = typeof(StartupMethodIdentityTests).GetMethod(nameof(Probe), BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsTrue(StartupMethodIdentity.Matches(original, original, null));
            // A process-rooted hook on this private fixture method only. Never
            // uninstall a published hook or alter the actual Chainloader in tests.
            if (fixtureHook == null)
            {
                Assert.IsNull(StartupMethodIdentity.ActiveBody(original));
                fixtureHook = new ILHook(original, il => { });
            }
            MethodBase body = StartupMethodIdentity.ActiveBody(original);
            Assert.IsNotNull(body);
            Probe(() => {
                Assert.IsTrue(Array.Exists(new StackTrace(false).GetFrames(), f =>
                    StartupMethodIdentity.Matches(f.GetMethod(), original, body)), "Active generated loader body");
            });
            using (var clone = new DynamicMethodDefinition(original))
            {
                var invoke = (Action<Action>)clone.Generate().CreateDelegate(typeof(Action<Action>));
                invoke(() => {
                    Assert.IsFalse(Array.Exists(new StackTrace(false).GetFrames(), f =>
                        StartupMethodIdentity.Matches(f.GetMethod(), original, body)), "A similarly named independent copy is not the active body");
                });
            }
            GC.KeepAlive(fixtureHook);
        }
    }
}
