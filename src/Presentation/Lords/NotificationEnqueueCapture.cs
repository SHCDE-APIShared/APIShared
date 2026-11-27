using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using APIShared.Patching;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using SHCDESE.API;
using SHCDESE.Detours;
using SHCDESE.Interop.Enums;

namespace APIShared.Presentation
{
    // Selection belongs to the current Extender enqueue, including its precise Original call.
    internal static class NotificationEnqueueCapture
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        internal enum CapturePhase { None, AwaitingResolver, Resolving, Complete }
        private static SafeIlHook callbackHook, selectionHook;
        private static ManualLogSource log;
        private static int resolverAvailable;
        private static int failedObservation;
        private static int confirmed;

        [ThreadStatic] private static CaptureFrame capture;
        [ThreadStatic] private static NativeSubmission submitting;

        internal struct CaptureFrame
        {
            internal CapturePhase Phase;
            internal bool Active, Known;
            internal string Subtitle;
        }

        private struct NativeSubmission
        {
            internal ulong Manager, Token;
            internal int Section, TextIndex;
            internal bool Known;
            internal string Subtitle;
        }

        private struct CallbackScope
        {
            internal CaptureFrame Capture;
            internal NativeSubmission Submission;
        }

        internal static bool IsReady => Volatile.Read(ref resolverAvailable) != 0 && callbackHook?.IsApplied == true;

        internal static void Install(MethodInfo resolver, ManualLogSource logger)
        {
            log = logger;
            MethodInfo callback = typeof(BulkAIDetours).GetMethod(nameof(BulkAIDetours.c_game_ai_enqueue_message_hook_impl),
                BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(ulong), typeof(ulong), typeof(ulong),
                    typeof(ulong), typeof(ulong), typeof(int) }, null);
            if (!SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.LordNotification.Enqueue", callback,
                EditCallback, logger, out callbackHook, out string reason))
            {
                NativeApiLog.Warning(logger, "LORD_NOTIFICATION_ENQUEUE: " + reason);
                return;
            }
            callbackHook.Rebuilt += _ => NotificationNativeRuntime.Invalidate();
            if (!SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.LordNotification.Clip", resolver,
                EditSelection, logger, out selectionHook, out reason))
                NativeApiLog.Warning(logger, "LORD_NOTIFICATION_CLIP: " + reason);
            else
                selectionHook.Rebuilt += _ => NotificationNativeRuntime.Invalidate();
        }

        internal static void ResolverPublished() => Volatile.Write(ref resolverAvailable, 1);

        private static CallbackScope EnterCallback()
        {
            var previous = new CallbackScope { Capture = capture, Submission = submitting };
            capture = new CaptureFrame { Active = IsReady && NotificationNativeRuntime.IsReady };
            submitting = default;
            return previous;
        }

        private static void LeaveCallback(CallbackScope previous)
        {
            capture = previous.Capture;
            submitting = previous.Submission;
        }

        private static void AwaitResolver()
        {
            if (capture.Active) capture.Phase = CapturePhase.AwaitingResolver;
        }

        internal static bool IsResolverScopeActive => capture.Active;

        internal static CaptureFrame EnterResolver()
        {
            CaptureFrame previous = capture;
            if (capture.Phase == CapturePhase.AwaitingResolver)
                capture.Phase = CapturePhase.Resolving;
            else
                capture = default; // A nested resolver may not write into its caller's selection.
            return previous;
        }

        internal static void LeaveResolver(CaptureFrame previous, bool completed)
        {
            CaptureFrame selection = capture;
            capture = previous;
            if (capture.Phase != CapturePhase.AwaitingResolver) return;
            capture.Phase = CapturePhase.Complete;
            capture.Known = completed && selection.Known;
            capture.Subtitle = capture.Known ? selection.Subtitle : null;
        }

        internal static void RecordSelection(string subtitle)
        {
            if (capture.Phase != CapturePhase.Resolving) return;
            capture.Known = true;
            capture.Subtitle = string.IsNullOrEmpty(subtitle) ? null : subtitle;
        }

        private static void RecordLocalSelection(string subtitle)
        {
            if (selectionHook?.IsApplied == true) RecordSelection(subtitle);
        }

        internal static bool TryGetSubmitting(ulong manager, int section, int textIndex, out string subtitle)
        {
            subtitle = null;
            if (!submitting.Known || submitting.Manager != manager || unchecked((int)(submitting.Token >> 32)) != NotificationNativeRuntime.Epoch ||
                submitting.Section != section ||
                submitting.TextIndex != textIndex) return false;
            subtitle = submitting.Subtitle;
            return true;
        }

        private static ulong Submit(BulkAIDetours.c_game_ai_enqueue_message_delegate original, ulong manager, ulong section,
            ulong textIndex, ulong video, ulong audio, int player, bool normal)
        {
            if (!NotificationNativeRuntime.IsReady) return original(manager, section, textIndex, video, audio, player);

            ulong before = 0;
            try { before = NotificationNativeRuntime.BeginEnqueue(manager); }
            catch (Exception error) { ReportObservationFailure(error); }
            if (before == 0) return original(manager, section, textIndex, video, audio, player);

            NativeSubmission previous = submitting;
            bool known = normal && capture.Active &&
                (capture.Phase == CapturePhase.None || capture.Phase == CapturePhase.Complete && capture.Known);
            int code = unchecked((int)section);
            int index = unchecked((int)textIndex);
            string text = known ? capture.Subtitle : null;
            submitting = new NativeSubmission { Manager = manager, Token = before, Section = code, TextIndex = index,
                Known = known && unchecked((uint)before) == 1, Subtitle = text };
            try
            {
                ulong result = original(manager, section, textIndex, video, audio, player);
                try
                {
                    NotificationNativeRuntime.EndEnqueue(manager, before, code, index, text, known);
                    if (Volatile.Read(ref confirmed) == 0 && Interlocked.Exchange(ref confirmed, 1) == 0)
                        NativeApiLog.Info(log, "LORD_NOTIFICATION_ENQUEUE_CONFIRMED: observed a native enqueue through the Extender callback.");
                }
                catch (Exception error) { ReportObservationFailure(error); }
                return result;
            }
            catch
            {
                NotificationNativeRuntime.Invalidate();
                throw; // The original Extender callback owns its retry behavior.
            }
            finally { submitting = previous; }
        }

        private static void ReportObservationFailure(Exception error)
        {
            // Observations may never enter the Extender's catch-and-retry path.
            NotificationNativeRuntime.Invalidate();
            if (Interlocked.Exchange(ref failedObservation, 1) != 0) return;
            try { NativeApiLog.Error(log, "LORD_NOTIFICATION_ENQUEUE: " + error); }
            catch { }
        }

        internal static void EditCallback(ILContext il)
        {
            var body = il.Body;
            string delegateType = typeof(BulkAIDetours.c_game_ai_enqueue_message_delegate).FullName.Replace('+', '/');
            string[] arguments = { "System.UInt64", "System.UInt64", "System.UInt64", "System.UInt64", "System.UInt64", "System.Int32" };
            var forwards = body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt &&
                i.Operand is MethodReference method && method.Name == "Invoke" && method.DeclaringType.FullName == delegateType &&
                method.ReturnType.FullName == "System.UInt64" && MatchesParameters(method, arguments)).ToArray();
            var resolver = body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt &&
                i.Operand is MethodReference method && IsResolver(method)).ToArray();
            var returns = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (il.Method.HasThis || il.Method.ReturnType.FullName != "System.UInt64" ||
                !MatchesParameters(il.Method, arguments) || forwards.Length != 2 || resolver.Length != 1 ||
                returns.Length != 1 || body.ExceptionHandlers.Count != 2 ||
                body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Catch ||
                body.ExceptionHandlers[0].CatchType?.FullName != typeof(Exception).FullName ||
                body.ExceptionHandlers[1].HandlerType != ExceptionHandlerType.Finally ||
                !Inside(body, resolver[0], body.ExceptionHandlers[0].TryStart, body.ExceptionHandlers[0].TryEnd) ||
                !Inside(body, forwards[0], body.ExceptionHandlers[0].TryStart, body.ExceptionHandlers[0].TryEnd) ||
                !Inside(body, forwards[1], body.ExceptionHandlers[0].HandlerStart, body.ExceptionHandlers[0].HandlerEnd) ||
                body.Instructions.IndexOf(resolver[0]) >= body.Instructions.IndexOf(forwards[0]) ||
                Inside(body, returns[0], body.ExceptionHandlers[1].HandlerStart, body.ExceptionHandlers[1].HandlerEnd))
                throw new InvalidOperationException("Extender enqueue resolver or native forwarding paths differ.");
            foreach (Instruction forward in forwards)
                ValidateForward(forward, delegateType, arguments.Length);

            AddCallbackScope(il, returns[0]);
            var cursor = new ILCursor(il);
            cursor.Goto(resolver[0], MoveType.Before);
            cursor.MoveAfterLabels();
            cursor.Emit(OpCodes.Call, PrivateMethod(nameof(AwaitResolver)));
            for (int index = 0; index < forwards.Length; index++)
            {
                cursor.Goto(forwards[index], MoveType.Before);
                cursor.MoveAfterLabels();
                cursor.Emit(index == 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                forwards[index].OpCode = OpCodes.Call;
                forwards[index].Operand = il.Import(PrivateMethod(nameof(Submit)));
            }
        }

        private static bool IsResolver(MethodReference method) =>
            method.DeclaringType.FullName == typeof(GameAIManagerAPI).FullName && method.Name == "TryGetVideoAndAudio" &&
            method.ReturnType.FullName == "System.Boolean" && MatchesParameters(method, new[] { "System.String",
                typeof(AILordMessageType).FullName, "System.String&", "System.String&" });

        private static bool MatchesParameters(MethodReference method, string[] expected) =>
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(expected);

        private static bool Inside(Mono.Cecil.Cil.MethodBody body, Instruction site, Instruction start, Instruction end) =>
            start != null && end != null && body.Instructions.IndexOf(site) >= body.Instructions.IndexOf(start) &&
            body.Instructions.IndexOf(site) < body.Instructions.IndexOf(end);

        private static void ValidateForward(Instruction forward, string delegateType, int count)
        {
            Instruction next = forward.Previous;
            for (int index = count - 1; index >= 0; index--, next = next?.Previous)
                if (!LoadsArgument(next, index)) throw new InvalidOperationException("Extender enqueue native arguments differ.");
            if (next?.OpCode != OpCodes.Callvirt || !(next.Operand is MethodReference getter) ||
                getter.Name != "get_Original" || getter.HasParameters ||
                !(getter.DeclaringType is GenericInstanceType handle) || handle.GenericArguments.Count != 1 ||
                handle.ElementType.FullName != "RedBird.Abstractions.Hooks.Transaction.DetourHandle`1" ||
                handle.GenericArguments[0].FullName != delegateType ||
                !(getter.ReturnType.FullName == delegateType || getter.ReturnType is GenericParameter parameter &&
                    parameter.Type == GenericParameterType.Type && parameter.Position == 0) ||
                next.Previous?.OpCode != OpCodes.Ldsfld || !(next.Previous.Operand is FieldReference field) ||
                field.Name != "c_game_ai_enqueue_message_hook" || field.DeclaringType.FullName != typeof(BulkAIDetours).FullName ||
                field.FieldType.FullName != getter.DeclaringType.FullName)
                throw new InvalidOperationException("Extender enqueue Original accessor differs.");
        }

        private static bool LoadsArgument(Instruction instruction, int index)
        {
            if (instruction == null) return false;
            return index == 0 && instruction.OpCode == OpCodes.Ldarg_0 || index == 1 && instruction.OpCode == OpCodes.Ldarg_1 ||
                index == 2 && instruction.OpCode == OpCodes.Ldarg_2 || index == 3 && instruction.OpCode == OpCodes.Ldarg_3 ||
                (instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S) &&
                instruction.Operand is ParameterDefinition parameter && parameter.Index == index;
        }

        private static MethodInfo PrivateMethod(string name) => typeof(NotificationEnqueueCapture).GetMethod(name, StaticPrivate);

        private static void AddCallbackScope(ILContext il, Instruction originalReturn)
        {
            var body = il.Body;
            var previous = new VariableDefinition(il.Import(typeof(CallbackScope)));
            var result = new VariableDefinition(il.Method.Module.TypeSystem.UInt64);
            body.Variables.Add(previous);
            body.Variables.Add(result);
            body.InitLocals = true;
            Instruction first = body.Instructions[0];
            Instruction cleanup = Instruction.Create(OpCodes.Ldloc, previous);
            Instruction finish = Instruction.Create(OpCodes.Ldloc, result);
            originalReturn.OpCode = OpCodes.Stloc;
            originalReturn.Operand = result;
            body.Instructions.Add(Instruction.Create(OpCodes.Leave, finish));
            body.Instructions.Add(cleanup);
            body.Instructions.Add(Instruction.Create(OpCodes.Call, il.Import(PrivateMethod(nameof(LeaveCallback)))));
            body.Instructions.Add(Instruction.Create(OpCodes.Endfinally));
            body.Instructions.Add(finish);
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                { TryStart = first, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = finish });
            var cursor = new ILCursor(il);
            cursor.Goto(first, MoveType.Before);
            cursor.Emit(OpCodes.Call, PrivateMethod(nameof(EnterCallback)));
            cursor.Emit(OpCodes.Stloc, previous);
        }

        internal static void EditSelection(ILContext il)
        {
            var body = il.Body;
            var localized = body.Instructions.Where(i => i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference method && method.Name == "GetLocalizedText" &&
                method.DeclaringType.FullName == typeof(GameAIManagerAPI).FullName && method.ReturnType.FullName == "System.String" &&
                method.Parameters.Count == 1 && i.Previous?.Operand is MethodReference getter && getter.Name == "get_LocalizedText" &&
                getter.DeclaringType.FullName == "SHCDESE.API.Components.AI.LordMessageClip" &&
                getter.ReturnType.FullName == method.Parameters[0].ParameterType.FullName).ToArray();
            var returns = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            TypeReference receiver = il.Method.HasThis ? il.Method.DeclaringType : il.Method.Parameters.FirstOrDefault()?.ParameterType;
            string[] actualParameters = il.Method.Parameters.Skip(il.Method.HasThis ? 0 : 1)
                .Select(parameter => parameter.ParameterType.FullName).ToArray();
            if (receiver?.FullName != typeof(GameAIManagerAPI).FullName || il.Method.ReturnType.FullName != "System.Boolean" ||
                !actualParameters.SequenceEqual(new[] { "System.String", typeof(AILordMessageType).FullName, "System.String&", "System.String&" }) ||
                localized.Length != 1 || returns.Length != 3 || body.ExceptionHandlers.Count != 0 ||
                returns[0].Previous?.OpCode != OpCodes.Ldc_I4_0 || returns[1].Previous?.OpCode != OpCodes.Ldc_I4_0 ||
                returns[2].Previous?.OpCode != OpCodes.Ldc_I4_1 ||
                body.Instructions.IndexOf(localized[0]) <= body.Instructions.IndexOf(returns[1]))
                throw new InvalidOperationException("Extender's selected localized clip/return contract differs.");
            var cursor = new ILCursor(il);
            cursor.Goto(localized[0], MoveType.After);
            cursor.Emit(OpCodes.Dup);
            cursor.Emit(OpCodes.Call, PrivateMethod(nameof(RecordLocalSelection)));
            for (int index = 0; index < 2; index++)
            {
                cursor.Goto(returns[index], MoveType.Before);
                cursor.MoveAfterLabels();
                cursor.Emit(OpCodes.Ldnull);
                cursor.Emit(OpCodes.Call, PrivateMethod(nameof(RecordLocalSelection)));
            }
        }
    }
}
