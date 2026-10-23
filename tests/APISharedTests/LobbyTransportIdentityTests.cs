using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System;
using System.Reflection;

namespace APISharedTests
{
    [TestClass]
    public class LobbyTransportIdentityTests
    {
        [TestMethod]
        public void LobbyInstanceReplacementWakesObserversEvenWithEqualSnapshotValues()
        {
            object identity = new object();
            var snapshot = new LobbyStateSnapshot(10, new Dictionary<int, ulong> { [4] = 100 }, false, 4, false, "", "");
            var service = new LobbyStateService(null, () => snapshot, () => identity);
            int calls = 0;
            Assert.IsTrue(service.Bind("test.host-sync").TryRegisterObserver("lobby", _ => calls++, out _));
            var observe = (Action)Delegate.CreateDelegate(typeof(Action), service,
                typeof(LobbyStateService).GetMethod("ObserveAndPublish", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.AreEqual(1, calls);
            observe();
            Assert.AreEqual(1, calls);
            identity = new object();
            observe();
            Assert.AreEqual(2, calls);
            observe();
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void LocalIdentityBecomesObservableOnlyWhenRosterAndTransportAgree()
        {
            var players = new Dictionary<int, ulong> { [4] = 100 };
            foreach (int transport in new[] { -1, 0, 2 })
                Assert.AreEqual(0, LobbyStateService.ResolveTransportLocalPlayerId(4, transport, players));
            Assert.AreEqual(4, LobbyStateService.ResolveTransportLocalPlayerId(4, 4, players));
            Assert.AreEqual(0, LobbyStateService.ResolveTransportLocalPlayerId(2, 2, players));
            Assert.AreEqual(0, LobbyStateService.ResolveTransportLocalPlayerId(-1, -1, players));

            // Remote peers may still be unresolved on both observations. The local ID
            // transition must nevertheless wake the existing changed-snapshot observer.
            var before = new LobbyStateSnapshot(10, players, true,
                LobbyStateService.ResolveTransportLocalPlayerId(4, -1, players), false, "", "");
            var after = new LobbyStateSnapshot(10, players, true,
                LobbyStateService.ResolveTransportLocalPlayerId(4, 4, players), false, "", "");
            Assert.IsFalse(LobbyStateService.ValueEquals(before, after));
        }
    }
}
