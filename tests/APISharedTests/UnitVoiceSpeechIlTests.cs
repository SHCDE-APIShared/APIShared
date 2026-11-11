using System;
using System.Linq;
using APIShared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APISharedTests
{
    [TestClass]
    public sealed class UnitVoiceSpeechIlTests
    {
        [TestMethod]
        public void InstalledSpeechMethodsValidateAndPatchOnlyTheAuditedCalls()
        {
            var methods = UnitVoiceSpeechIlContract.Validate();
            using (var copy = new DynamicMethodDefinition(methods.MoveNext))
            using (var il = new ILContext(copy.Definition))
            {
                UnitVoiceSpeechIlContract.ApplyAsync(il, methods);
                Assert.AreEqual(3, Helpers(il, "Load")); Assert.AreEqual(3, Helpers(il, "PlayOneShot"));
                Assert.IsFalse(il.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.AudioSource" && m.Name == "PlayOneShot"));
            }
            using (var copy = new DynamicMethodDefinition(methods.Play))
            using (var il = new ILContext(copy.Definition))
            {
                UnitVoiceSpeechIlContract.ApplyStarts(il); UnitVoiceSpeechIlContract.ApplyStops(il, 3); UnitVoiceSpeechIlContract.ApplyReleases(il, 3);
                Assert.AreEqual(3, Helpers(il, "Start")); Assert.AreEqual(3, Helpers(il, "Unload")); Assert.AreEqual(3, Helpers(il, "Stop"));
            }
            using (var copy = new DynamicMethodDefinition(methods.Monitor))
            using (var il = new ILContext(copy.Definition))
            {
                UnitVoiceSpeechIlContract.ApplyMonitorSweep(il, methods); UnitVoiceSpeechIlContract.ApplyReleases(il, 3);
                Assert.AreEqual(3, Helpers(il, "Unload")); Assert.AreEqual(1, Helpers(il, "Sweep"));
                Assert.AreEqual(2, il.Body.Instructions.Count(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.AudioClip" && m.Name == "UnloadAudioData"), "Music cleanup must remain original.");
            }
            using (var copy = new DynamicMethodDefinition(methods.StopAll))
            using (var il = new ILContext(copy.Definition))
            {
                UnitVoiceSpeechIlContract.ApplyStops(il, 3); UnitVoiceSpeechIlContract.ApplyReleases(il, 3); UnitVoiceSpeechIlContract.ApplyStopAllCleanup(il);
                Assert.AreEqual(3, Helpers(il, "Stop")); Assert.AreEqual(3, Helpers(il, "Unload")); Assert.AreEqual(1, Helpers(il, "StopAllCompleted"));
            }
        }

        [TestMethod]
        public void IncomingBranchToAReplacedCallIsRejectedBeforeAnEdit()
        {
            var methods = UnitVoiceSpeechIlContract.Validate();
            using (var copy = new DynamicMethodDefinition(methods.Play))
            using (var il = new ILContext(copy.Definition))
            {
                var original = UnitVoiceSpeechIlContract.FindStarts(il)[0];
                il.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Br, original));
                Assert.ThrowsExactly<InvalidOperationException>(() => UnitVoiceSpeechIlContract.ApplyStarts(il));
                Assert.AreEqual(0, Helpers(il, "Start"));
            }
        }
        private static int Helpers(ILContext il, string name) => il.Body.Instructions.Count(i => i.Operand is MethodReference m &&
            m.DeclaringType.FullName == typeof(UnitVoiceSpeechPipeline).FullName && m.Name == name);
    }
}
