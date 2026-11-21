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
    [DoNotParallelize]
    public sealed class UnitVoiceIntegrationTests
    {
        private static int forwarded;
        private static string forwardedPath;
        private static bool RejectBindingRead(EngineInterface.PlayState state, out UnitVoiceBinding binding)
        { binding = null; Assert.Fail("Recovered transport must return before source attribution."); return false; }
        public static void RecordSpeech(SFXManager self, int channel, string path, float volume, bool muted, bool paused)
        {
            Assert.IsNull(self);
            Assert.AreEqual(3, channel); Assert.AreEqual(0.75f, volume);
            Assert.IsTrue(muted); Assert.IsFalse(paused);
            forwarded++; forwardedPath = path;
        }

        [TestMethod]
        public void UnboundAcceptedTokensRecoverThroughTheActualDormantPlaybackWithoutAttribution()
        {
            var ready = typeof(UnitVoiceRuntime).GetField("copyReady", BindingFlags.NonPublic | BindingFlags.Static);
            object previous = ready.GetValue(null);
            UnitVoiceTransport.ResetForTest();
            try
            {
                ready.SetValue(null, 0);
                var source = typeof(UnitVoiceRuntime).GetMethod("Play", BindingFlags.Static | BindingFlags.NonPublic);
                using (var copy = new DynamicMethodDefinition(source))
                {
                    // Replace only the external Unity leaf; execute the real production guards and recovery.
                    var recorder = copy.Definition.Module.ImportReference(typeof(UnitVoiceIntegrationTests).GetMethod(nameof(RecordSpeech)));
                    foreach (var instruction in copy.Definition.Body.Instructions)
                        if (instruction.Operand is MethodReference call && call.DeclaringType.FullName == "SFXManager" && call.Name == "playSpeech")
                        { instruction.OpCode = Mono.Cecil.Cil.OpCodes.Call; instruction.Operand = recorder; }
                    var play = (Action<SFXManager, int, string, float, bool, bool, EngineInterface.PlayState>)copy.Generate().CreateDelegate(
                        typeof(Action<SFXManager, int, string, float, bool, bool, EngineInterface.PlayState>));
                    Assert.IsTrue(UnitVoiceCatalog.TryResolveFileName("Bedouin_M1.wav", out var cue));
                    string token = UnitVoiceTransport.CreateToken(cue, out long id);
                    var origin = new UnitVoiceSource(1, null, null, cue.VoiceBankKey, cue.Key, UnitVoiceSituation.Move, UnitVoiceEvidence.NativeAudited, 17, "Troop.Dispatcher");
                    UnitVoiceTransport.Publish(token, "fx\\speech\\Bedouin_M1.wav", origin, id);
                    var state = new EngineInterface.PlayState { speechFileName = token };
                    foreach (bool snapshot in new[] { true, false })
                    {
                        if (!snapshot) UnitVoiceTransport.ResetForTest();
                        forwarded = 0;
                        play(null, 3, token, 0.75f, true, false, state);
                        Assert.AreEqual(1, forwarded); Assert.AreEqual("fx\\speech\\" + cue.FileName, forwardedPath);
                        Assert.AreEqual(token, state.speechFileName, "Published state is never mutated by playback recovery.");
                        Assert.IsFalse(UnitVoiceTransport.TryGetBinding(state, out _));
                    }
                    foreach (string unchanged in new[] { "fx\\speech\\ordinary.wav", "fx\\speech\\__apiv_FFFF_0000000000000001.wav", "fx\\speech\\__apiv_bad.wav" })
                    {
                        forwarded = 0; play(null, 3, unchanged, 0.75f, true, false, state);
                        Assert.AreEqual(1, forwarded); Assert.AreEqual(unchanged, forwardedPath);
                    }
                    // A recovered token must not reuse even an existing binding on the same mutable state.
                    UnitVoiceTransport.Publish(token, "fx\\speech\\Bedouin_M1.wav", origin, id);
                    UnitVoiceTransport.BindPlayState(state);
                    Assert.IsTrue(UnitVoiceTransport.TryGetBinding(state, out var binding));
                    state.speechFileName = token;
                    forwarded = 0; play(null, 3, token, 0.75f, true, false, state);
                    Assert.AreEqual(1, forwarded); Assert.AreEqual(binding.OriginalPath, forwardedPath);
                    Assert.AreEqual(token, state.speechFileName);
                    // Open every external activation predicate, then fail if the real Play body
                    // reaches attribution. Merely normalizing and continuing would fail here.
                    foreach (var instruction in copy.Definition.Body.Instructions)
                    {
                        if (!(instruction.Operand is MethodReference call)) continue;
                        bool positive = call.DeclaringType.FullName == typeof(UnitVoiceRuntime).FullName && call.Name == "get_ManagedReady" ||
                            call.DeclaringType.FullName == typeof(UnitVoiceEvents).FullName && call.Name == "get_Demand" ||
                            call.DeclaringType.FullName == "APIShared.Internal.UnityMainThreadDispatch" && call.Name == "get_IsMainThread";
                        bool negative = call.DeclaringType.FullName == typeof(UnitVoiceEvents).FullName && call.Name == "get_InCallback" ||
                            call.DeclaringType.FullName == "System.Threading.ExecutionContext" && call.Name == "IsFlowSuppressed";
                        if (positive || negative)
                        { instruction.OpCode = positive ? Mono.Cecil.Cil.OpCodes.Ldc_I4_1 : Mono.Cecil.Cil.OpCodes.Ldc_I4_0; instruction.Operand = null; }
                        else if (call.DeclaringType.FullName == typeof(UnitVoiceTransport).FullName && call.Name == "TryGetBinding")
                            instruction.Operand = copy.Definition.Module.ImportReference(typeof(UnitVoiceIntegrationTests).GetMethod(nameof(RejectBindingRead), BindingFlags.Static | BindingFlags.NonPublic));
                    }
                    var activePlay = (Action<SFXManager, int, string, float, bool, bool, EngineInterface.PlayState>)copy.Generate().CreateDelegate(
                        typeof(Action<SFXManager, int, string, float, bool, bool, EngineInterface.PlayState>));
                    forwarded = 0; activePlay(null, 3, token, 0.75f, true, false, state);
                    Assert.AreEqual(1, forwarded); Assert.AreEqual(binding.OriginalPath, forwardedPath);
                }
            }
            finally { ready.SetValue(null, previous); UnitVoiceTransport.ResetForTest(); }
        }

        [TestMethod]
        public void AForeignBranchToCopyReturnKeepsItsEditWhenBindingRollsBack()
        {
            var source = typeof(EngineInterface).GetMethod("CopyPlayStateStruct", BindingFlags.Public | BindingFlags.Static);
            using (var method = new DynamicMethodDefinition(source))
            using (var il = new ILContext(method.Definition))
            {
                var ret = il.Body.Instructions.First(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ret);
                var cursor = new ILCursor(il); cursor.Goto(ret, MoveType.Before);
                var target = cursor.DefineLabel(); target.Target = ret;
                cursor.Emit(Mono.Cecil.Cil.OpCodes.Br, target);
                var branch = ret.Previous;
                var guard = APIShared.Patching.SafeIlHook.Wrap("test", "voice-copy-rebuild", source, UnitVoiceCopyIlContract.Apply, null);
                il.Invoke(guard.Manipulator);
                Assert.IsFalse(guard.IsApplied);
                Assert.AreSame(branch, ret.Previous, "The neutral foreign edit survives our failed rebuild.");
                Assert.AreEqual(0, il.Body.Instructions.Count(i => i.Operand is MethodReference call && call.Name == "BindPlayState"));
            }
        }
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
