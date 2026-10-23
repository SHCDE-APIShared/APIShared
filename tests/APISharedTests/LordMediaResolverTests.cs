using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API;
using SHCDESE.Interop.Enums;
using System;
using System.Reflection;

namespace APISharedTests
{
    [TestClass]
    public class LordMediaResolverTests
    {
        [TestMethod]
        public void EmptyIdentityRetainsOriginalMediaWithoutCallingTheExtenderLookup()
        {
            int calls = 0;
            bool Next(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio)
            { calls++; video = "custom-video"; audio = "custom-audio"; return true; }
            foreach (string name in new[] { null, "" })
            {
                Assert.IsFalse(LordMediaResolverRuntime.Resolve(Next, null, name, default, out string video, out string audio));
                Assert.AreEqual(string.Empty, video);
                Assert.AreEqual(string.Empty, audio);
            }
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void NonemptyIdentitiesRetainTheOriginalResultArgumentsAndExceptions()
        {
            int calls = 0;
            bool Next(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio)
            {
                calls++;
                Assert.IsNull(self);
                Assert.AreEqual(default(AILordMessageType), type);
                video = "video:" + name;
                audio = "audio:" + name;
                return name == "known";
            }
            foreach (string name in new[] { "known", "unknown", " \t" })
            {
                Assert.AreEqual(name == "known", LordMediaResolverRuntime.Resolve(Next, null, name, default, out string video, out string audio));
                Assert.AreEqual("video:" + name, video);
                Assert.AreEqual("audio:" + name, audio);
            }
            Assert.AreEqual(3, calls);
            bool Throw(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio) => throw new ApplicationException("original failure");
            Assert.ThrowsExactly<ApplicationException>(() => LordMediaResolverRuntime.Resolve(Throw, null, "known", default, out _, out _));
        }

        [TestMethod]
        public void InstalledExtenderEntryPassesTheProductionInstallationChecks()
        {
            MethodInfo target = typeof(GameAIManagerAPI).GetMethod("TryGetVideoAndAudio", BindingFlags.NonPublic | BindingFlags.Instance);
            LordMediaResolverRuntime.ValidateTarget(target);
        }

        [TestMethod]
        public void ChangedSignatureOrEntryIsRefusedBeforeHookPublication()
        {
            Assert.ThrowsExactly<MissingMethodException>(() => LordMediaResolverRuntime.ValidateTarget(null));
            foreach (string name in new[] { "WrongResult", "Static", "ChangedEntry", "NoBody" })
                Assert.ThrowsExactly<InvalidOperationException>(() => LordMediaResolverRuntime.ValidateTarget(
                    typeof(ResolverFixture).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)), name);
        }

        private abstract class ResolverFixture
        {
            protected int WrongResult(string name, AILordMessageType type, out string video, out string audio)
            { video = audio = ""; return 0; }
            protected static bool Static(string name, AILordMessageType type, out string video, out string audio)
            { video = audio = ""; return false; }
            protected bool ChangedEntry(string name, AILordMessageType type, out string video, out string audio)
            { video = "changed"; audio = ""; return false; }
            protected abstract bool NoBody(string name, AILordMessageType type, out string video, out string audio);
        }
    }
}
