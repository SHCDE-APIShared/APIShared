using System;
using System.Collections.Generic;
using APIShared;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Contract of the cross-mod observer exception broker: no opinion without registrations, ordered
    // first-true wins, isolated failures, unique identities.
    [TestClass]
    public sealed class ObserverCommandExceptionsTests
    {
        private static readonly ObserverCommandRequest Tax =
            new ObserverCommandRequest(2, 5, Enums.GameActionCommand.SetTaxRate, 7, 3, 0);

        [TestMethod]
        public void WithoutRegistrationsNothingIsAllowed()
        {
            Assert.IsFalse(new ObserverCommandRegistry().IsAllowed(Tax));
        }

        [TestMethod]
        public void OrderedCallbacksStopAtTheFirstAllowAndSeeTheRequest()
        {
            var registry = new ObserverCommandRegistry();
            var calls = new List<string>();
            Assert.IsTrue(registry.TryRegister("b", "late", r => { calls.Add("b"); return true; }, out _, 10));
            Assert.IsTrue(registry.TryRegister("z", "first", r => { calls.Add("z"); return false; }, out _, -5));
            Assert.IsTrue(registry.TryRegister("a", "same-order", r => {
                calls.Add("a");
                return r.ObserverPlayerId == 2 && r.ViewedPlayerId == 5 && r.Command == Enums.GameActionCommand.SetTaxRate &&
                    r.StructureId == 7 && r.ActionState == 3;
            }, out _, 10));
            Assert.IsTrue(registry.IsAllowed(Tax));
            CollectionAssert.AreEqual(new[] { "z", "a" }, calls, "Order, then ordinal owner; the first true wins.");
        }

        [TestMethod]
        public void ThrowingCallbacksAreIsolatedAndCounted()
        {
            var registry = new ObserverCommandRegistry();
            Assert.IsTrue(registry.TryRegister("a", "throws", r => throw new InvalidOperationException("test"), out _, 0));
            Assert.IsFalse(registry.IsAllowed(Tax));
            Assert.AreEqual(1, registry.CallbackFailures);
            Assert.IsTrue(registry.TryRegister("b", "allows", r => true, out _, 0));
            Assert.IsTrue(registry.IsAllowed(Tax));
            Assert.AreEqual(2, registry.CallbackFailures);
        }

        [TestMethod]
        public void InvalidOrDuplicateIdentitiesAreRejected()
        {
            var registry = new ObserverCommandRegistry();
            Assert.IsTrue(registry.TryRegister("a", "id", r => false, out _, 0));
            Assert.IsFalse(registry.TryRegister("a", "id", r => true, out string duplicate, 0));
            StringAssert.Contains(duplicate, "Duplicate");
            Assert.IsFalse(registry.TryRegister(" ", "id", r => true, out _, 0));
            Assert.IsFalse(registry.TryRegister("a", "other", null, out _, 0));
            Assert.IsFalse(registry.IsAllowed(Tax));
        }
    }
}
