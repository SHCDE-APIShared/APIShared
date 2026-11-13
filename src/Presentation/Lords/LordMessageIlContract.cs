using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APIShared.Presentation
{
    // Only three presentation call kinds change. Native buffers, state strings and loop control remain untouched.
    internal static class LordMessageIlContract
    {
        internal sealed class Sites
        {
            internal VariableDefinition Index;
            internal Instruction[] Text;
            internal Instruction Speech, Video;
            internal bool SharedSpeech;
        }
        internal static void Validate(MethodInfo method)
        {
            using (var copy = new DynamicMethodDefinition(method))
            using (var il = new ILContext(copy.Definition)) Find(il);
        }
        internal static Sites Find(ILContext il)
        {
            var body = il.Body.Instructions;
            var result = new Sites();
            for (int at = 0; at + 6 < body.Count; at++)
            {
                if (!body[at].MatchLdarg(1) || !body[at + 1].MatchLdloc(out int local) || !body[at + 2].MatchLdcI4(21) ||
                    body[at + 3].OpCode != OpCodes.Mul || !body[at + 4].MatchLdcI4(11) || body[at + 5].OpCode != OpCodes.Add ||
                    body[at + 6].OpCode != OpCodes.Ldelem_I2) continue;
                if (result.Index != null && result.Index != il.Body.Variables[local]) throw Invalid("ambiguous record index");
                result.Index = il.Body.Variables[local];
            }
            if (result.Index == null || result.Index.VariableType.MetadataType != MetadataType.Int32) throw Invalid("record index");
            var text = new List<Instruction>();
            foreach (var instruction in body)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
                if (!(instruction.Operand is MethodReference method) || method.ReturnType.MetadataType != MetadataType.Void) continue;
                if (!method.HasThis && method.DeclaringType.FullName == "APIShared.Audio.AudioSpeechRecordAdapter" && method.Name == "Play")
                {
                    if (result.Speech != null || method.Parameters.Count != 8 || method.Parameters[0].ParameterType.FullName != "SFXManager" ||
                        method.Parameters[1].ParameterType.MetadataType != MetadataType.Int32 ||
                        method.Parameters[2].ParameterType.MetadataType != MetadataType.String ||
                        method.Parameters[3].ParameterType.MetadataType != MetadataType.Single ||
                        method.Parameters[4].ParameterType.MetadataType != MetadataType.Boolean ||
                        method.Parameters[5].ParameterType.MetadataType != MetadataType.Boolean ||
                        method.Parameters[6].ParameterType.MetadataType != MetadataType.Int32 ||
                        method.Parameters[7].ParameterType.FullName != typeof(EngineInterface.PlayState).FullName.Replace('+', '/')) throw Invalid("shared speech signature/count");
                    result.Speech = instruction; result.SharedSpeech = true;
                    continue;
                }
                if (!method.HasThis) continue;
                if (method.DeclaringType.FullName == "OnScreenText" && method.Name == "addOSTEntry")
                {
                    if (method.Parameters.Count != 6 || method.Parameters[0].ParameterType.FullName != "Enums/eOnScreenText" ||
                        method.Parameters.Skip(1).Any(p => p.ParameterType.MetadataType != MetadataType.Int32)) throw Invalid("text signature");
                    text.Add(instruction);
                }
                else if (method.DeclaringType.FullName == "SFXManager" && method.Name == "playSpeech")
                {
                    if (result.Speech != null || !Parameters(method, MetadataType.Int32, MetadataType.String, MetadataType.Single, MetadataType.Boolean, MetadataType.Boolean)) throw Invalid("speech signature/count");
                    result.Speech = instruction;
                }
                else if (method.DeclaringType.FullName == "SFXManager" && method.Name == "playBink")
                {
                    if (result.Video != null || !Parameters(method, MetadataType.String, MetadataType.Boolean, MetadataType.Boolean)) throw Invalid("video signature/count");
                    result.Video = instruction;
                }
            }
            if (text.Count != 2 || result.Speech == null || result.Video == null) throw Invalid("presentation call count");
            result.Text = text.ToArray();
            var sites = new HashSet<Instruction>(text) { result.Speech, result.Video };
            foreach (var instruction in body)
            {
                if (instruction.Operand is Instruction target && sites.Contains(target) ||
                    instruction.Operand is ILLabel label && sites.Contains(label.Target)) throw Invalid("incoming call branch");
                if (instruction.Operand is Instruction[] targets && targets.Any(sites.Contains) ||
                    instruction.Operand is ILLabel[] labels && labels.Any(l => sites.Contains(l.Target))) throw Invalid("incoming call switch");
            }
            foreach (var handler in il.Body.ExceptionHandlers)
                if (sites.Contains(handler.TryStart) || sites.Contains(handler.TryEnd) || sites.Contains(handler.HandlerStart) ||
                    sites.Contains(handler.HandlerEnd) || sites.Contains(handler.FilterStart)) throw Invalid("call exception boundary");
            return result;
        }
        private static bool Parameters(MethodReference method, params MetadataType[] types) =>
            method.Parameters.Count == types.Length && method.Parameters.Select(p => p.ParameterType.MetadataType).SequenceEqual(types);
        internal static void Apply(ILContext il)
        {
            Sites sites = Find(il); // All validation precedes the first instruction edit.
            foreach (var text in sites.Text) Replace(il, text, sites.Index, "Text");
            if (!sites.SharedSpeech) Replace(il, sites.Speech, sites.Index, "Speech");
            Replace(il, sites.Video, sites.Index, "Video");
        }
        private static void Replace(ILContext il, Instruction call, VariableDefinition index, string helper)
        {
            var method = typeof(LordMessageRuntime).GetMethod(helper, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(helper);
            var cursor = new ILCursor(il); cursor.Goto(call, MoveType.Before); cursor.Emit(OpCodes.Ldloc, index);
            call.OpCode = OpCodes.Call; call.Operand = il.Import(method);
        }
        private static InvalidOperationException Invalid(string reason) =>
            new InvalidOperationException("Lord message GameMap IL contract changed: " + reason);
    }
}
