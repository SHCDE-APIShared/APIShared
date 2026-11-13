using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using UnityEngine;

namespace APIShared.Audio
{
    // Installed-method contracts, not compiler-generated type-name or Assembly-CSharp file-hash pins.
    internal static class UnitVoiceSpeechIlContract
    {
        internal sealed class Methods
        {
            internal MethodInfo Play, Start, Path, MoveNext, Monitor, StopAll, StopSpeech;
            internal FieldInfo Self, Channel, Paused;
        }
        internal static Methods Validate()
        {
            var result = new Methods
            {
                Play = Method("PlaySpeech", true, typeof(void), typeof(int), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool)),
                Start = Method("LoadClip", false, typeof(void), typeof(int), typeof(string), typeof(string), typeof(bool), typeof(bool)),
                Path = Method("LoadClip", false, typeof(Task<AudioClip>), typeof(string)),
                Monitor = Method("MonitorLoadedSounds", false, typeof(void)),
                StopAll = Method("StopAllGameSounds", true, typeof(void), typeof(bool)),
                StopSpeech = Method("StopSpeech", true, typeof(void), typeof(int))
            };
            var state = result.Start.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            if (state == null || !typeof(IAsyncStateMachine).IsAssignableFrom(state)) throw Invalid("async state machine");
            result.MoveNext = state.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (result.MoveNext == null || result.MoveNext.ReturnType != typeof(void)) throw Invalid("async MoveNext");
            // Derive captured parameter fields from the actual wrapper assignments, not generated names.
            using (var copy = new DynamicMethodDefinition(result.Start))
            {
                var body = copy.Definition.Body.Instructions;
                result.Self = Captured(body, state, 0, typeof(MyAudioManager));
                result.Channel = Captured(body, state, 1, typeof(int));
            }
            foreach (var spec in new[] { Tuple.Create(result.Play, 3), Tuple.Create(result.Monitor, 3), Tuple.Create(result.StopAll, 3), Tuple.Create(result.StopSpeech, 1) })
                Inspect(spec.Item1, il => FindReleases(il, spec.Item2));
            foreach (var spec in new[] { Tuple.Create(result.Play, 3), Tuple.Create(result.StopAll, 3), Tuple.Create(result.StopSpeech, 1) })
                Inspect(spec.Item1, il => FindStops(il, spec.Item2));
            Inspect(result.Play, FindStarts);
            Inspect(result.MoveNext, il => FindAsync(il, result));
            Inspect(result.StopAll, FindStopAllReturns);
            foreach (string name in new[] { "speechClip1", "speechClip2", "speechClip3" })
            {
                var field = typeof(MyAudioManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null || !field.IsPrivate || field.FieldType != typeof(AudioClip)) throw Invalid("speech clip ownership field");
            }
            result.Paused = typeof(MyAudioManager).GetField("speechPaused", BindingFlags.Instance | BindingFlags.NonPublic);
            if (result.Paused == null || !result.Paused.IsPrivate || result.Paused.FieldType != typeof(bool)) throw Invalid("speech pause field");
            var play = typeof(AudioSource).GetMethod("PlayOneShot", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(AudioClip), typeof(float) }, null);
            var unload = typeof(AudioClip).GetMethod("UnloadAudioData", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            var stop = typeof(AudioSource).GetMethod("Stop", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            var playing = typeof(AudioSource).GetProperty("isPlaying", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod();
            if (play == null || play.ReturnType != typeof(void) || unload == null || unload.ReturnType != typeof(bool) || stop == null || stop.ReturnType != typeof(void) ||
                playing == null || playing.ReturnType != typeof(bool)) throw Invalid("Unity public playback/lifetime members");
            return result;
        }
        private static MethodInfo Method(string name, bool visible, Type returns, params Type[] parameters)
        {
            var method = typeof(MyAudioManager).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameters, null);
            if (method == null || method.IsStatic || method.IsPublic != visible || method.ReturnType != returns) throw Invalid("MyAudioManager." + name);
            return method;
        }
        private static FieldInfo Captured(Mono.Collections.Generic.Collection<Instruction> body, Type state, int argument, Type type)
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            for (int at = 0; at + 1 < body.Count; at++)
                if (body[at].MatchLdarg(argument) && body[at + 1].OpCode == OpCodes.Stfld && body[at + 1].Operand is FieldReference field &&
                    field.DeclaringType.FullName.Replace('/', '+') == state.FullName && field.FieldType.FullName == type.FullName)
                    candidates.Add(field.Name);
            if (candidates.Count != 1) throw Invalid("captured parameter " + argument);
            var result = state.GetField(candidates.Single(), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (result == null || result.IsStatic || result.FieldType != type) throw Invalid("captured field type");
            return result;
        }
        private static void Inspect(MethodInfo method, Func<ILContext, Instruction[]> check)
        {
            using (var copy = new DynamicMethodDefinition(method))
            using (var il = new ILContext(copy.Definition)) check(il);
        }
        internal static Instruction[] FindStarts(ILContext il)
        {
            var result = Calls(il, "MyAudioManager", "LoadClip", "System.Void", "System.Int32", "System.String", "System.String", "System.Boolean", "System.Boolean");
            if (result.Length != 3) throw Invalid("accepted speech load count");
            CheckBoundaries(il, result); return result;
        }
        internal static Instruction[] FindReleases(ILContext il, int expected)
        {
            var all = Calls(il, "UnityEngine.AudioClip", "UnloadAudioData", "System.Boolean");
            var result = all.Where(call => call.Previous != null && call.Previous.OpCode == OpCodes.Ldfld && call.Previous.Operand is FieldReference field &&
                field.DeclaringType.FullName == "MyAudioManager" && field.FieldType.FullName == "UnityEngine.AudioClip" &&
                (field.Name == "speechClip1" || field.Name == "speechClip2" || field.Name == "speechClip3")).ToArray();
            if (result.Length != expected) throw Invalid("speech release count");
            CheckBoundaries(il, result); return result;
        }
        internal static Instruction[] FindStops(ILContext il, int expected)
        {
            var result = Calls(il, "UnityEngine.AudioSource", "Stop", "System.Void").Where(call => call.Previous != null &&
                call.Previous.OpCode == OpCodes.Ldfld && call.Previous.Operand is FieldReference field &&
                field.DeclaringType.FullName == "MyAudioManager" && field.FieldType.FullName == "UnityEngine.AudioSource" &&
                (field.Name == "speechSource1" || field.Name == "speechSource2" || field.Name == "speechSource3")).ToArray();
            if (result.Length != expected) throw Invalid("speech source stop count");
            CheckBoundaries(il, result); return result;
        }
        internal static Instruction[] FindAsync(ILContext il, Methods methods)
        {
            var loads = Calls(il, "MyAudioManager", "LoadClip", "System.Threading.Tasks.Task`1<UnityEngine.AudioClip>", "System.String");
            var plays = Calls(il, "UnityEngine.AudioSource", "PlayOneShot", "System.Void", "UnityEngine.AudioClip");
            if (loads.Length != 3 || plays.Length != 3) throw Invalid("async speech load/play count");
            foreach (var load in loads)
                if (load.Next == null || !(load.Next.Operand is MethodReference next) || next.Name != "GetAwaiter" ||
                    !next.DeclaringType.FullName.StartsWith("System.Threading.Tasks.Task`1<", StringComparison.Ordinal)) throw Invalid("async load-to-await flow");
            // DMD copies turn instance methods into static methods on a synthetic holder. The
            // receiver and field operands, not that holder's generated name, express the contract.
            Type state = methods.MoveNext.DeclaringType;
            if (methods.Self.DeclaringType != state || methods.Channel.DeclaringType != state) throw Invalid("async captured-field owner");
            TypeReference receiver = il.Method.HasThis ? il.Method.DeclaringType : il.Method.Parameters.FirstOrDefault()?.ParameterType;
            if (receiver == null || receiver.GetElementType().FullName.Replace('/', '+') != state.FullName ||
                !il.Method.HasThis && state.IsValueType && !(receiver is ByReferenceType)) throw Invalid("async receiver type");
            foreach (var field in new[] { methods.Self, methods.Channel })
            {
                bool found = false;
                for (int at = 0; at + 1 < il.Body.Instructions.Count; at++)
                    if (il.Body.Instructions[at].MatchLdarg(0) && il.Body.Instructions[at + 1].OpCode == OpCodes.Ldfld &&
                        il.Body.Instructions[at + 1].Operand is FieldReference read && read.Name == field.Name &&
                        read.DeclaringType.FullName.Replace('/', '+') == state.FullName && read.FieldType.FullName == field.FieldType.FullName) found = true;
                if (!found) throw Invalid("async captured-field read");
            }
            CheckBoundaries(il, loads.Concat(plays)); return loads.Concat(plays).ToArray();
        }
        private static Instruction[] FindStopAllReturns(ILContext il)
        {
            var result = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (il.Method.ReturnType.MetadataType != MetadataType.Void || result.Length == 0) throw Invalid("StopAll normal return");
            foreach (var handler in il.Body.ExceptionHandlers)
                if (result.Contains(handler.TryStart) || result.Contains(handler.TryEnd) || result.Contains(handler.HandlerStart) ||
                    result.Contains(handler.HandlerEnd) || result.Contains(handler.FilterStart)) throw Invalid("StopAll return exception boundary");
            return result;
        }
        private static Instruction[] Calls(ILContext il, string owner, string name, string returns, params string[] parameters)
        {
            return il.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference method &&
                method.HasThis && method.DeclaringType.FullName == owner && method.Name == name && method.ReturnType.FullName == returns &&
                method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToArray();
        }
        private static void CheckBoundaries(ILContext il, IEnumerable<Instruction> calls)
        {
            var sites = new HashSet<Instruction>(calls);
            foreach (var instruction in il.Body.Instructions)
            {
                if (instruction.Operand is Instruction target && sites.Contains(target) || instruction.Operand is ILLabel label && sites.Contains(label.Target)) throw Invalid("incoming call branch");
                if (instruction.Operand is Instruction[] targets && targets.Any(sites.Contains) || instruction.Operand is ILLabel[] labels && labels.Any(l => sites.Contains(l.Target))) throw Invalid("incoming call switch");
            }
            foreach (var handler in il.Body.ExceptionHandlers)
                if (sites.Contains(handler.TryStart) || sites.Contains(handler.TryEnd) || sites.Contains(handler.HandlerStart) || sites.Contains(handler.HandlerEnd) || sites.Contains(handler.FilterStart))
                    throw Invalid("call exception boundary");
        }
        internal static void ApplyStarts(ILContext il)
        { foreach (var call in FindStarts(il)) Replace(il, call, "Start"); }
        internal static void ApplyReleases(ILContext il, int expected)
        { foreach (var call in FindReleases(il, expected)) Replace(il, call, "Unload"); }
        internal static void ApplyStops(ILContext il, int expected)
        { foreach (var call in FindStops(il, expected)) Replace(il, call, "Stop"); }
        internal static void ApplyMonitorSweep(ILContext il, Methods methods)
        {
            // The helper receives Vanilla's pause flag, because paused sources also report !isPlaying.
            var helper = typeof(UnitVoiceSpeechPipeline).GetMethod("Sweep", BindingFlags.NonPublic | BindingFlags.Static);
            var cursor = new ILCursor(il); cursor.Goto(0);
            cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldfld, il.Import(methods.Paused));
            cursor.Emit(OpCodes.Call, il.Import(helper));
        }
        internal static void ApplyStopAllCleanup(ILContext il)
        {
            var returns = FindStopAllReturns(il);
            var helper = typeof(UnitVoiceSpeechPipeline).GetMethod("StopAllCompleted", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var instruction in returns)
            {
                // Preserve the original return instruction as the branch target. All successful
                // paths, including branches to the old ret, therefore perform the same cleanup.
                instruction.OpCode = OpCodes.Ldarg_0; instruction.Operand = null;
                var cursor = new ILCursor(il); cursor.Goto(instruction, MoveType.After);
                cursor.Emit(OpCodes.Call, il.Import(helper)); cursor.Emit(OpCodes.Ret);
            }
        }
        internal static void ApplyAsync(ILContext il, Methods methods)
        {
            var calls = FindAsync(il, methods);
            foreach (var call in calls)
            {
                var original = (MethodReference)call.Operand;
                if (original.Name == "PlayOneShot")
                {
                    var cursor = new ILCursor(il); cursor.Goto(call, MoveType.Before);
                    cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldfld, il.Import(methods.Self));
                    cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldfld, il.Import(methods.Channel));
                    Replace(il, call, "PlayOneShot");
                }
                else Replace(il, call, "Load");
            }
        }
        private static void Replace(ILContext il, Instruction call, string name)
        {
            var helper = typeof(UnitVoiceSpeechPipeline).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (helper == null) throw new MissingMethodException(name);
            call.OpCode = OpCodes.Call; call.Operand = il.Import(helper);
        }
        private static InvalidOperationException Invalid(string detail) => new InvalidOperationException("Unit voice speech IL contract changed: " + detail);
    }
}
