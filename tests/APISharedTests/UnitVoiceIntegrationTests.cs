using System;
using System.Linq;
using System.Reflection;
using APIShared.Audio;
using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APISharedTests
{
    [TestClass]
    public sealed class UnitVoiceIntegrationTests
    {
        private static MethodInfo Render => typeof(GameMap).GetMethod("processTestMap", new[] {
            typeof(short[]), typeof(int), typeof(EngineInterface.PlayState), typeof(byte[]) });

        [TestMethod]
        public void LordAndUnitVoiceManipulatorsShareOneSpeechSiteInBothOrders()
        {
            foreach (bool lordFirst in new[] { false, true })
            using (var method = new DynamicMethodDefinition(Render))
            using (var il = new ILContext(method.Definition))
            {
                SoundIlContract.Validate(Render);
                if (lordFirst) { LordMessageIlContract.Apply(il); UnitVoicePlaybackIlContract.Apply(il); }
                else { UnitVoicePlaybackIlContract.Apply(il); LordMessageIlContract.Apply(il); }
                var calls = il.Body.Instructions.Select(x => x.Operand).OfType<MethodReference>().ToArray();
                Assert.AreEqual(1, calls.Count(x => x.DeclaringType.FullName == typeof(AudioSpeechRecordAdapter).FullName && x.Name == "Play"));
                Assert.AreEqual(0, calls.Count(x => x.DeclaringType.FullName == "SFXManager" && x.Name == "playSpeech"));
                Assert.AreEqual(2, calls.Count(x => x.DeclaringType.FullName == typeof(LordMessageRuntime).FullName && x.Name == "Text"));
                Assert.AreEqual(1, calls.Count(x => x.DeclaringType.FullName == typeof(LordMessageRuntime).FullName && x.Name == "Video"));
                // Both speech transformations leave the two independently owned SFX sites intact.
                SoundIlContract.Find(il, out _);
            }
        }

        [TestMethod]
        public void AChangedSpeechSiteKeepsBothSfxAdaptersAvailable()
        {
            using (var method = new DynamicMethodDefinition(Render))
            using (var il = new ILContext(method.Definition))
            {
                var speech = UnitVoicePlaybackIlContract.Find(il).Call;
                var original = (MethodReference)speech.Operand;
                // A different mod has replaced only the speech call before our manipulator runs.
                var foreign = new MethodReference("ForeignSpeech", original.ReturnType, original.DeclaringType) { HasThis = true };
                foreach (var parameter in original.Parameters) foreign.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                speech.Operand = foreign;
                Assert.IsFalse(UnitVoicePlaybackIlContract.ApplyCombined(il, true, out string reason));
                Assert.IsFalse(string.IsNullOrEmpty(reason));
                Assert.AreEqual("ForeignSpeech", ((MethodReference)speech.Operand).Name);
                Assert.AreEqual(0, il.Body.Instructions.Count(x => x.Operand is MethodReference call &&
                    call.DeclaringType.FullName == "SFXManager" && call.Name == "playSound"));
                Assert.AreEqual(2, il.Body.Instructions.Count(x => x.Operand is MethodReference call &&
                    call.DeclaringType.FullName == typeof(SoundIlContract).FullName && call.Name == "Play"));
            }
        }

        [TestMethod]
        public void PlayStateBindingOccursAtEveryRealReturnWithoutCopyingTheNativeStruct()
        {
            var source = typeof(EngineInterface).GetMethod("CopyPlayStateStruct", BindingFlags.Public | BindingFlags.Static);
            UnitVoiceCopyIlContract.Validate(source);
            using (var method = new DynamicMethodDefinition(source))
            using (var il = new ILContext(method.Definition))
            {
                int returns = il.Body.Instructions.Count(x => x.OpCode == Mono.Cecil.Cil.OpCodes.Ret);
                UnitVoiceCopyIlContract.Apply(il);
                Assert.AreEqual(returns, il.Body.Instructions.Count(x => x.Operand is MethodReference call &&
                    call.DeclaringType.FullName == typeof(UnitVoiceTransport).FullName && call.Name == "BindPlayState"));
            }
        }
    }
}
