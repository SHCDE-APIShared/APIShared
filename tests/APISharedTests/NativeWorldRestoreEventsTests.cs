using System;
using System.Collections.Generic;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class NativeWorldRestoreEventsTests
    {
        [TestMethod]
        public void OrderedParticipantsVetoAndIsolateExceptionsWithoutRetry()
        {
            var trace = new List<string>();
            string owner = "restore-tests-" + Guid.NewGuid().ToString("N");
            Assert.IsFalse(NativeWorldRestoreEvents.TryRegisterCore(owner, "unavailable", c => true, out _, 0, false));
            Assert.IsTrue(NativeWorldRestoreEvents.TryRegisterCore(owner, "second", c =>
            { if (c.InvocationId >= 91001 && c.InvocationId <= 91003) trace.Add("second"); return true; }, out _, 20, true));
            Assert.IsTrue(NativeWorldRestoreEvents.TryRegisterCore(owner, "first", c =>
            {
                if (c.InvocationId < 91001 || c.InvocationId > 91003) return true;
                Assert.IsNull(c.Mission); trace.Add("first");
                if (c.InvocationId == 91003) throw new InvalidOperationException("restore rejected");
                return c.InvocationId != 91002;
            }, out _, 10, true));
            Assert.IsFalse(NativeWorldRestoreEvents.TryRegisterCore(owner, "first", c => true, out _, 10, true));
            Assert.IsTrue(NativeWorldRestoreEvents.Dispatch(new NativeWorldRestoreContext(91001, null)));
            CollectionAssert.AreEqual(new[] { "first", "second" }, trace);
            trace.Clear();
            Assert.IsFalse(NativeWorldRestoreEvents.Dispatch(new NativeWorldRestoreContext(91002, null)));
            CollectionAssert.AreEqual(new[] { "first" }, trace);
            trace.Clear(); long failures = NativeWorldRestoreEvents.CallbackFailures;
            Assert.IsFalse(NativeWorldRestoreEvents.Dispatch(new NativeWorldRestoreContext(91003, null)));
            Assert.AreEqual(failures + 1, NativeWorldRestoreEvents.CallbackFailures);
            CollectionAssert.AreEqual(new[] { "first" }, trace);
        }
    }
}
