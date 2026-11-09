using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Simulation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class EngineRunObserverTests
    {
        [TestMethod]
        public void RegistrationRequiresBothCallbacksAndAUniqueOwnerLocalId()
        {
            var registry = new EngineRunObserverRegistry();
            var calls = new List<string>();
            Action begin = () => calls.Add("begin");
            Action end = () => calls.Add("end");

            Assert.IsFalse(registry.TryRegister(" ", "run", begin, end, out string ownerFailure, 0));
            Assert.IsFalse(registry.TryRegister("owner", "", begin, end, out string idFailure, 0));
            Assert.IsFalse(registry.TryRegister("owner", "run", null, end, out string beginFailure, 0));
            Assert.IsFalse(registry.TryRegister("owner", "run", begin, null, out string endFailure, 0));
            foreach (string reason in new[] { ownerFailure, idFailure, beginFailure, endFailure })
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason));

            Assert.IsTrue(registry.TryRegister("owner", "run", begin, end, out _, 0));
            Assert.IsFalse(registry.TryRegister("owner", "run", begin, end, out string duplicateFailure, 1));
            Assert.IsFalse(string.IsNullOrWhiteSpace(duplicateFailure));
            Assert.IsTrue(registry.TryRegister("another-owner", "run", begin, end, out _, 0));
            Assert.AreEqual(0, calls.Count, "Registering does not replay an engine call.");
        }

        [TestMethod]
        public void BothPhasesUseOneOrderedSnapshotOnTheCallingThread()
        {
            var registry = new EngineRunObserverRegistry();
            var calls = new List<string>();
            var threads = new List<int>();
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            int originals = 0;
            bool originalFlag = false;
            bool lateRegistered = false;
            void Record(string phase)
            {
                calls.Add(phase);
                threads.Add(Thread.CurrentThread.ManagedThreadId);
            }
            void Register(string owner, string id, int order)
            {
                Assert.IsTrue(registry.TryRegister(owner, id, () => Record("begin " + id),
                    () => Record("end " + id), out _, order));
            }

            Register("z-owner", "z", 0);
            Register("a-owner", "second", 0);
            Register("a-owner", "first", 0);
            Assert.IsTrue(registry.TryRegister("z-owner", "early", () =>
            {
                Record("begin early");
                if (!lateRegistered)
                    lateRegistered = registry.TryRegister("new-owner", "late", () => Record("begin late"),
                        () => Record("end late"), out _, 1);
            }, () => Record("end early"), out _, -1));
            Assert.AreEqual(0, calls.Count);

            int Original(bool mpFrameSkip)
            {
                Record("original");
                originals++;
                originalFlag = mpFrameSkip;
                return 73;
            }

            Assert.AreEqual(73, registry.Invoke(true, Original));
            Assert.IsTrue(originalFlag);
            Assert.IsTrue(lateRegistered);
            CollectionAssert.AreEqual(new[] { "begin early", "begin first", "begin second", "begin z", "original",
                "end early", "end first", "end second", "end z" }, calls);

            calls.Clear();
            Assert.AreEqual(73, registry.Invoke(false, Original));
            Assert.IsFalse(originalFlag);
            Assert.AreEqual(2, originals);
            CollectionAssert.AreEqual(new[] { "begin early", "begin first", "begin second", "begin z", "begin late",
                "original", "end early", "end first", "end second", "end z", "end late" }, calls);
            Assert.IsTrue(threads.TrueForAll(thread => thread == callerThread));
        }

        [TestMethod]
        public void CallbackAndDiagnosticFailuresLeaveOriginalResultAndOtherCallbacksIntact()
        {
            var calls = new List<string>();
            var errors = new List<Exception>();
            var beginFailure = new InvalidOperationException("begin");
            var endFailure = new InvalidOperationException("end");
            var registry = new EngineRunObserverRegistry(error =>
            {
                errors.Add(error);
                throw new InvalidOperationException("diagnostic reporter failed");
            });

            Assert.IsTrue(registry.TryRegister("one", "run", () =>
            {
                calls.Add("begin one");
                throw beginFailure;
            }, () => calls.Add("end one"), out _, 0));
            Assert.IsTrue(registry.TryRegister("two", "run", () => calls.Add("begin two"), () =>
            {
                calls.Add("end two");
                throw endFailure;
            }, out _, 0));
            Assert.IsTrue(registry.TryRegister("three", "run", () => calls.Add("begin three"),
                () => calls.Add("end three"), out _, 1));

            Assert.AreEqual(0, registry.Invoke(false, _ =>
            {
                calls.Add("original");
                return 0;
            }));
            CollectionAssert.AreEqual(new[] { "begin one", "begin two", "begin three", "original",
                "end one", "end two", "end three" }, calls);
            CollectionAssert.AreEqual(new Exception[] { beginFailure, endFailure }, errors);
        }

        [TestMethod]
        public void NestedExceptionCompletesItsOwnSnapshotBeforeOuterCompletion()
        {
            var registry = new EngineRunObserverRegistry();
            var calls = new List<string>();
            var originalFailure = new InvalidOperationException("nested original");
            int depth = 0;
            bool lateRegistered = false;
            Exception nestedFailure = null;
            Assert.IsTrue(registry.TryRegister("owner", "stable", () => calls.Add("begin stable " + depth),
                () => calls.Add("end stable " + depth), out _, 0));

            Assert.AreEqual(11, registry.Invoke(false, skip =>
            {
                calls.Add("outer original");
                lateRegistered = registry.TryRegister("owner", "late", () => calls.Add("begin late " + depth),
                    () => calls.Add("end late " + depth), out _, 1);
                depth++;
                try
                {
                    registry.Invoke(true, _ =>
                    {
                        calls.Add("nested original");
                        throw originalFailure;
                    });
                }
                catch (Exception error) { nestedFailure = error; }
                finally { depth--; }
                return 11;
            }));

            Assert.IsTrue(lateRegistered);
            Assert.AreSame(originalFailure, nestedFailure);
            CollectionAssert.AreEqual(new[] { "begin stable 0", "outer original", "begin stable 1", "begin late 1",
                "nested original", "end stable 1", "end late 1", "end stable 0" }, calls);

            calls.Clear();
            Assert.AreEqual(17, registry.Invoke(false, _ =>
            {
                calls.Add("next original");
                return 17;
            }));
            CollectionAssert.AreEqual(new[] { "begin stable 0", "begin late 0", "next original", "end stable 0",
                "end late 0" }, calls);
        }
    }
}
