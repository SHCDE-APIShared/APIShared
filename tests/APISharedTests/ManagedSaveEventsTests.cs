using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using APIShared.Savegames;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class ManagedSaveEventsTests
    {
        private static readonly Exception SaveError = new InvalidOperationException("save failed");
        private static readonly Exception LateFinalizerError = new InvalidOperationException("later finalizer failed");
        private static int originalCalls;

        [TestMethod]
        public void SaveObserversPairWithTheirOwnInvocationAcrossVetoFailureAndNesting()
        {
            var target = typeof(ManagedSaveEventsTests).GetMethod(nameof(Save), BindingFlags.NonPublic | BindingFlags.Static);
            var harmony = new Harmony("APISharedTests.ManagedSaveEvents.Veto");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ManagedSaveEventsTests), nameof(Veto))
                { priority = int.MaxValue });
            harmony.Patch(target, finalizer: new HarmonyMethod(typeof(ManagedSaveEventsTests), nameof(ThrowAfterObserver))
                { priority = int.MinValue, after = new[] { "APIShared.ManagedSaveEvents" } });

            var trace = new List<string>();
            var completions = new List<(ManagedSaveCompletion Completion, object State, int Thread)>();
            var begun = new Dictionary<string, ManagedSaveCall>();
            Assert.IsTrue(ManagedSaveEvents.TryRegister(() => target, "tests", "capture",
                call =>
                {
                    trace.Add("begin:" + call.Path);
                    begun.Add(call.Path, call);
                    return call;
                },
                (completion, state) =>
                {
                    trace.Add("end:" + completion.Call.Path);
                    completions.Add((completion, state, Thread.CurrentThread.ManagedThreadId));
                }, out string reason, 0), reason);
            Assert.IsTrue(ManagedSaveEvents.TryRegister(() => target, "tests", "failing-observer",
                call => throw new InvalidOperationException("begin observer"),
                (completion, state) => throw new InvalidOperationException("end observer"),
                out reason, -1), reason);

            Assert.IsTrue(InvokeSave("outer"));
            Assert.IsFalse(InvokeSave("false"));
            Assert.IsTrue(InvokeSave("veto"));
            Assert.AreSame(SaveError, Assert.ThrowsExactly<InvalidOperationException>(() => InvokeSave("throw")));
            Assert.AreSame(LateFinalizerError, Assert.ThrowsExactly<InvalidOperationException>(() => InvokeSave("late")));
            Assert.AreEqual(5, originalCalls);
            CollectionAssert.AreEqual(new[] { "begin:outer", "begin:inner", "end:inner", "end:outer",
                "begin:false", "end:false", "begin:veto", "end:veto", "begin:throw", "end:throw",
                "begin:late", "end:late" }, trace);

            foreach (var entry in completions)
            {
                var completion = entry.Completion;
                Assert.AreSame(begun[completion.Call.Path], completion.Call);
                Assert.AreSame(completion.Call, entry.State);
                Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, entry.Thread);
                Assert.AreEqual(4, completion.Call.RealScreenCentreY);
                Assert.IsTrue(completion.Call.MapSave);
            }
            Assert.AreEqual(true, completions[0].Completion.Result);
            Assert.AreEqual(true, completions[1].Completion.Result);
            Assert.AreEqual(false, completions[2].Completion.Result);
            Assert.IsTrue(completions[3].Completion.WasSkipped);
            Assert.IsNull(completions[3].Completion.Result);
            Assert.AreSame(SaveError, completions[4].Completion.Exception);
            Assert.IsNull(completions[4].Completion.Result);
            Assert.AreEqual(true, completions[5].Completion.Result);
            Assert.IsNull(completions[5].Completion.Exception);
        }

        private static bool InvokeSave(string path) => Save(path, 1, 2, 3, 4, false, false, true);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Save(string path, int x, int y, int realX, int realY, bool locked, bool temporary, bool map)
        {
            originalCalls++;
            if (path == "outer") return InvokeSave("inner");
            if (path == "throw") throw SaveError;
            return path != "false";
        }

        private static bool Veto(string __0, ref bool __result)
        {
            if (__0 != "veto") return true;
            __result = true;
            return false;
        }

        private static void ThrowAfterObserver(string __0)
        {
            if (__0 == "late") throw LateFinalizerError;
        }
    }
}
