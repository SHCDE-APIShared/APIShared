using System;
using System.Collections.Generic;
using System.Reflection;
using CrusaderDE;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APIShared.Audio
{
    // One shared speech-record call site, cooperating with the Lord-message manipulator
    // in either composition order. Native record coalescing and the loop stay untouched.
    internal static class UnitVoicePlaybackIlContract
    {
        internal sealed class Site
        {
            internal Instruction Call;
            internal VariableDefinition Index;
            internal bool HasIndex;
            internal MethodReference Adapter;
        }
        internal static void Validate(MethodInfo method)
        {
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition)) Find(il);
        }
        internal static Site Find(ILContext il)
        {
            var site = new Site();
            foreach (var instruction in il.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
                if (!(instruction.Operand is MethodReference method)) continue;
                bool original = method.DeclaringType.FullName == "SFXManager" && method.Name == "playSpeech" && method.HasThis &&
                    method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 5 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
                    method.Parameters[1].ParameterType.MetadataType == MetadataType.String &&
                    method.Parameters[2].ParameterType.MetadataType == MetadataType.Single &&
                    method.Parameters[3].ParameterType.MetadataType == MetadataType.Boolean &&
                    method.Parameters[4].ParameterType.MetadataType == MetadataType.Boolean;
                bool lord = method.DeclaringType.FullName == "APIShared.Presentation.LordMessageRuntime" && method.Name == "Speech" &&
                    !method.HasThis && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 7 &&
                    method.Parameters[0].ParameterType.FullName == "SFXManager" &&
                    method.Parameters[6].ParameterType.MetadataType == MetadataType.Int32;
                if (!original && !lord) continue;
                if (site.Call != null) throw Invalid("ambiguous speech site");
                site.Call = instruction; site.HasIndex = lord;
            }
            var body = il.Body.Instructions;
            for (int i = 0; i + 6 < body.Count; i++)
            {
                if (body[i].MatchLdarg(1) && body[i + 1].MatchLdloc(out int index) && body[i + 2].MatchLdcI4(21) &&
                    body[i + 3].OpCode == OpCodes.Mul && body[i + 4].MatchLdcI4(11) &&
                    body[i + 5].OpCode == OpCodes.Add && body[i + 6].OpCode == OpCodes.Ldelem_I2)
                {
                    var variable = il.Body.Variables[index];
                    if (site.Index != null && site.Index != variable) throw Invalid("ambiguous record index");
                    site.Index = variable;
                }
            }
            if (site.Call == null || site.Index == null || site.Index.VariableType.MetadataType != MetadataType.Int32)
                throw Invalid("speech site or record index missing");
            ValidateBoundary(il, site.Call);
            site.Adapter = il.Import(typeof(AudioSpeechRecordAdapter).GetMethod(nameof(AudioSpeechRecordAdapter.Play), BindingFlags.Static | BindingFlags.NonPublic));
            return site;
        }
        internal static void ValidateBoundary(ILContext il, Instruction site)
        {
            foreach (var instruction in il.Body.Instructions)
            {
                if (instruction.Operand is Instruction target && target == site ||
                    instruction.Operand is ILLabel label && label.Target == site) throw Invalid("incoming branch");
                if (instruction.Operand is Instruction[] targets && Array.IndexOf(targets, site) >= 0) throw Invalid("incoming switch");
                if (instruction.Operand is ILLabel[] labels)
                    foreach (var value in labels) if (value.Target == site) throw Invalid("incoming switch label");
            }
            foreach (var handler in il.Body.ExceptionHandlers)
                if (handler.TryStart == site || handler.TryEnd == site || handler.HandlerStart == site ||
                    handler.HandlerEnd == site || handler.FilterStart == site) throw Invalid("exception boundary");
        }
        internal static void Apply(ILContext il)
        {
            Apply(il, Find(il));
        }
        private static void Apply(ILContext il, Site site)
        {
            var cursor = new ILCursor(il); cursor.Goto(site.Call, MoveType.Before);
            if (!site.HasIndex) cursor.Emit(OpCodes.Ldloc, site.Index);
            cursor.Emit(OpCodes.Ldarg_3);
            site.Call.OpCode = OpCodes.Call;
            site.Call.Operand = site.Adapter;
        }
        // Validate the actual composed IL before either service edits it. An unsupported speech
        // site must not prevent publication of the independently validated SFX transformation.
        internal static bool ApplyCombined(ILContext il, bool voices, out string reason)
        {
            Site site = null;
            reason = null;
            if (voices)
            {
                try { site = Find(il); }
                catch (Exception error) { reason = error.Message; }
            }
            SoundIlContract.Apply(il);
            // The speech edit is independent: its failure keeps the sound edits of this run.
            if (site != null && !Patching.SafeIlHook.TryEdit(il, value => Apply(value, site), out Exception speechError))
            { reason = speechError.Message; site = null; }
            return site != null;
        }
        private static InvalidOperationException Invalid(string reason) => new InvalidOperationException("Unit voice GameMap IL contract changed: " + reason);
    }

    internal static class AudioSpeechRecordAdapter
    {
        internal static void Play(SFXManager self, int channel, string path, float volume,
            bool ignoreMuting, bool ignorePause, int index, EngineInterface.PlayState state)
        {
            // A complete Lord message owns its grouped audio; ordinary unit voices have no group.
            if (Presentation.LordMessageRuntime.TryForwardSpeech(self, channel, path, volume, ignoreMuting, ignorePause, index)) return;
            UnitVoiceRuntime.Play(self, channel, path, volume, ignoreMuting, ignorePause, state);
        }
    }

    internal static class UnitVoiceCopyIlContract
    {
        internal static void Validate(MethodInfo method)
        {
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition)) Find(il);
        }
        private static Instruction[] Find(ILContext il)
        {
            if (il.Method.ReturnType.FullName != typeof(EngineInterface.PlayState).FullName.Replace('+', '/'))
                throw new InvalidOperationException("Unit voice PlayState return type changed.");
            bool speechStore = false;
            var returns = new List<Instruction>();
            foreach (var instruction in il.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field &&
                    field.DeclaringType.FullName == il.Method.ReturnType.FullName && field.Name == "speechFileName" &&
                    field.FieldType.MetadataType == MetadataType.String) speechStore = true;
                if (instruction.OpCode == OpCodes.Ret) { UnitVoicePlaybackIlContract.ValidateBoundary(il, instruction); returns.Add(instruction); }
            }
            if (!speechStore || returns.Count == 0) throw new InvalidOperationException("Unit voice PlayState copy contract changed.");
            return returns.ToArray();
        }
        internal static void Apply(ILContext il)
        {
            var returns = Find(il);
            var method = typeof(UnitVoiceTransport).GetMethod("BindPlayState", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var instruction in returns)
            {
                var cursor = new ILCursor(il); cursor.Goto(instruction, MoveType.Before);
                cursor.Emit(OpCodes.Dup); cursor.Emit(OpCodes.Call, il.Import(method));
            }
        }
    }
}
