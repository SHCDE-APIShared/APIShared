using System;
using System.Collections.Generic;
using APIShared.Maps;
using APIShared.Lobby;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class MultiplayerMapLookupTests
    {
        private static FileHeader Header(string path = "package/Assets/01/map.map", uint crc = 17) =>
            new FileHeader { fileName = "map", filePath = path, crc = crc };

        [TestMethod]
        public void PackagedMapWorksWithoutCatalogMutationAndUnclaimedQueriesKeepVanilla()
        {
            var dispatch = new MultiplayerMapLookupDispatch(_ => Assert.Fail("Unexpected provider failure"));
            FileHeader selected = Header();
            bool active = true;
            Assert.IsTrue(dispatch.Providers.TryAdd("third-party", "current-map", query =>
                active && query.FileName == "map" && query.Crc == 17 ? selected : null, out _));
            Assert.IsFalse(dispatch.Providers.TryAdd("third-party", "current-map", _ => null, out _));
            int calls = 0;
            Func<FileHeader> original = () => { calls++; return null; };
            Assert.AreSame(selected, dispatch.Resolve("map", 17, original).Header);
            Assert.AreEqual(0, calls);
            Assert.IsNull(dispatch.Resolve("map", 18, original).Header);
            Assert.AreEqual(1, calls);
            active = false;
            Assert.AreEqual(MultiplayerMapLookupStatus.Vanilla, dispatch.Resolve("map", 17, original).Status);
            Assert.AreEqual(2, calls);
            selected = Header("other-installation/Assets/02/map.map"); active = true;
            Assert.AreSame(selected, dispatch.Resolve("map", 17, original).Header);
        }

        [TestMethod]
        public void ConflictsFailClosedWhileCallbackFaultsAreIsolated()
        {
            var errors = new List<string>();
            var dispatch = new MultiplayerMapLookupDispatch(errors.Add);
            dispatch.Providers.TryAdd("a", "map", _ => Header("a/map.map"), out _);
            dispatch.Providers.TryAdd("b", "map", _ => Header("b/map.map"), out _);
            var result = dispatch.Resolve("map", 17, () => { Assert.Fail("Conflict must not resolve another map"); return null; });
            Assert.AreEqual(MultiplayerMapLookupStatus.Conflict, result.Status);
            Assert.IsNull(result.Header);
            Assert.IsTrue(errors.Count > 0);
            var isolated = new MultiplayerMapLookupDispatch(errors.Add);
            isolated.Providers.TryAdd("a", "bad", _ => throw new InvalidOperationException("provider failed"), out _);
            isolated.Providers.TryAdd("b", "wrong-crc", _ => Header(crc: 18), out _);
            var expected = Header();
            isolated.Providers.TryAdd("c", "good", _ => expected, out _);
            isolated.Observers.TryAdd("d", "bad-observer", _ => throw new InvalidOperationException("observer failed"), out _);
            Assert.AreSame(expected, isolated.Resolve("map", 17, () => null).Header);
            Assert.IsTrue(errors.Exists(item => item.Contains("provider failed")));
            Assert.IsTrue(errors.Exists(item => item.Contains("observer failed")));
        }

        [TestMethod]
        public void RegistrationsAddedDuringQueryApplyToTheNextQueryAndOriginalErrorsPropagate()
        {
            var dispatch = new MultiplayerMapLookupDispatch(_ => { });
            int observed = 0;
            dispatch.Providers.TryAdd("a", "register", query => {
                dispatch.Observers.TryAdd("b", "late", result => observed++, out _);
                return null;
            }, out _);
            dispatch.Resolve("map", 17, () => null);
            Assert.AreEqual(0, observed);
            dispatch.Resolve("map", 17, () => null);
            Assert.AreEqual(1, observed);
            Assert.ThrowsExactly<InvalidOperationException>(() => dispatch.Resolve("map", 17,
                () => throw new InvalidOperationException("Vanilla")));
        }

        [TestMethod]
        public void SystemRulesPreserveOrdinaryChatExistingHintsAndOtherRulesAfterFailure()
        {
            var dispatch = new LobbyChatPresentationDispatch(_ => { });
            dispatch.Rules.TryAdd("a", "bad", _ => throw new InvalidOperationException(), out _);
            dispatch.Rules.TryAdd("third-party", "support", message => message.Message.StartsWith("Support: "), out _);
            Assert.IsFalse(dispatch.Classify("player", "ordinary chat", false));
            Assert.IsTrue(dispatch.Classify("player", "Support: missing map", false));
            Assert.IsTrue(dispatch.Classify("Vanilla", "already a system hint", true));
        }
    }
}
