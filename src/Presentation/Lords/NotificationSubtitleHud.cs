using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using APIShared.Internal;
using APIShared.Patching;
using BepInEx.Logging;
using CrusaderDE;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using SHCDESE.API;

namespace APIShared.Presentation
{
    internal static class NotificationSubtitleHud
    {
        internal sealed class Assignment
        {
            internal readonly OnScreenText Owner;
            internal readonly OnScreenText.OST Slot;
            internal readonly string Subtitle;
            internal readonly long Epoch;
            internal readonly int NativeEpoch;

            internal Assignment(OnScreenText owner, OnScreenText.OST slot, NotificationPresentationRecord record)
            {
                Owner = owner;
                Slot = slot;
                Subtitle = record.Subtitle;
                Epoch = NotificationPresentations.Epoch;
                NativeEpoch = record.Epoch;
            }
        }

        private static SafeIlHook hook;
        private static Assignment assigned;
        private static ManualLogSource log;
        private static int confirmed, loggedFailure;
        [ThreadStatic] private static bool updating;
        [ThreadStatic] private static Assignment rendering;

        internal static void Install(ManualLogSource logger)
        {
            log = logger;
            try
            {
                MethodInfo target = FindDelegate();
                if (!SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.NotificationSubtitleHud", target, Apply, logger, out hook, out string reason))
                    throw new InvalidOperationException(reason);
            }
            catch (Exception error) { NativeApiLog.Error(logger, "NOTIFICATION_SUBTITLE_HUD_UNAVAILABLE: " + error.Message); }
        }

        internal static MethodInfo FindDelegate()
        {
            var extender = typeof(GameAIManagerAPI).Assembly;
            // The emitter references Cecil directly. Read its IL without loading the Extender's
            // Cecil version, and resolve the delegate against the same loaded module afterwards.
            using (var module = ModuleDefinition.ReadModule(extender.Location))
            {
                if (module.Mvid != extender.ManifestModule.ModuleVersionId)
                    throw new InvalidOperationException("Loaded Extender differs from the assembly used to inspect the subtitle delegate.");
                var manager = module.GetType("SHCDESE.ManagedHooks.ILHooks.ILHooksManager");
                var emitMethod = manager?.Methods.SingleOrDefault(method => method.Name == "OnScreenText_UpdateOST_ILHook" &&
                    method.HasBody && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.FullName == typeof(ILContext).FullName);
                if (emitMethod == null) throw new MissingMethodException("Extender message-bar IL emitter.");
                var bodies = emitMethod.Body.Instructions;
                var candidates = bodies.Where(i => i.OpCode == OpCodes.Ldftn && i.Operand is MethodReference method &&
                    method.ReturnType.MetadataType == MetadataType.Boolean && method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.FullName == "OnScreenText/OST").ToArray();
                var calls = bodies.Where(i => i.Operand is GenericInstanceMethod method &&
                    method.DeclaringType.FullName == typeof(ILCursor).FullName && method.Name == "EmitDelegate" &&
                    method.GenericArguments.Count == 1 && IsSubtitleDelegate(method.GenericArguments[0])).ToArray();
                if (candidates.Length != 1 || calls.Length != 1 || bodies.IndexOf(candidates[0]) >= bodies.IndexOf(calls[0]) ||
                    candidates[0].Next?.OpCode != OpCodes.Newobj || !(candidates[0].Next.Operand is MethodReference constructor) ||
                    !IsSubtitleDelegate(constructor.DeclaringType))
                    throw new InvalidOperationException("Extender subtitle delegate binding differs from the audited IL.");

                if (!(candidates[0].Operand is MethodDefinition reference) ||
                    !(extender.ManifestModule.ResolveMethod(reference.MetadataToken.ToInt32()) is MethodInfo target) ||
                    target.ReturnType != typeof(bool) || !target.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(OnScreenText.OST) }))
                    throw new MissingMethodException("Extender subtitle delegate target.");
                return target;
            }
        }

        private static bool IsSubtitleDelegate(TypeReference type) =>
            type is GenericInstanceType generic && generic.ElementType.FullName == "System.Func`2" &&
            generic.GenericArguments.Count == 2 && generic.GenericArguments[0].FullName == "OnScreenText/OST" &&
            generic.GenericArguments[1].MetadataType == MetadataType.Boolean;

        internal static void Apply(ILContext il)
        {
            var instructions = il.Body.Instructions;
            var parameters = il.Method.Parameters;
            var slot = parameters.LastOrDefault();
            if (il.Method.ReturnType.MetadataType != MetadataType.Boolean ||
                slot?.ParameterType.FullName != "OnScreenText/OST" ||
                parameters.Count != 1 && (il.Method.HasThis || parameters.Count != 2) ||
                UniqueCall(instructions, typeof(GameAIManagerAPI), "TryGetCurrentSpeechText") != 1 ||
                UniqueCall(instructions, typeof(GameAIManagerAPI), "TryGetMessageTypeFromIndex") != 1 ||
                UniqueCall(instructions, typeof(MainViewModel), "set_OST_Message_Bar_Text") != 1)
                throw new InvalidOperationException("Extender subtitle delegate differs from the audited IL.");

            // -1 falls through to the existing delegate, 0 selects the original translation,
            // and 1 tells updateOST that the exact message text has already been applied.
            var result = new VariableDefinition(il.Method.Module.TypeSystem.Int32);
            il.Body.Variables.Add(result);
            var cursor = new ILCursor(il);
            var original = cursor.DefineLabel();
            cursor.Emit(OpCodes.Ldarg, slot);
            cursor.EmitDelegate<Func<OnScreenText.OST, int>>(TryRender);
            cursor.Emit(OpCodes.Stloc, result);
            cursor.Emit(OpCodes.Ldloc, result);
            cursor.Emit(OpCodes.Ldc_I4_0);
            cursor.Emit(OpCodes.Blt, original);
            cursor.Emit(OpCodes.Ldloc, result);
            cursor.Emit(OpCodes.Ret);
            cursor.MarkLabel(original);
            cursor.Emit(OpCodes.Nop); // The original entry may also start a protected region.
        }

        private static int UniqueCall(System.Collections.Generic.IEnumerable<Instruction> instructions, Type owner, string name) =>
            instructions.Count(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference target && target.DeclaringType.FullName == owner.FullName && target.Name == name);

        internal static void Bind(OnScreenText self, OnScreenText.OST slot, NotificationPresentationRecord record)
        {
            if (record.SubtitleKnown && hook?.IsApplied == true && slot != null)
                Volatile.Write(ref assigned, new Assignment(self, slot, record));
        }

        internal static void Clear() => Volatile.Write(ref assigned, null);

        internal static Assignment Enter(OnScreenText self)
        {
            var previous = rendering;
            var current = Volatile.Read(ref assigned);
            rendering = !updating && current != null && current.Epoch == NotificationPresentations.Epoch &&
                ReferenceEquals(current.Owner, self) ? current : null;
            updating = true;
            return previous;
        }

        internal static void Exit(Assignment previous, bool nested)
        {
            rendering = previous;
            updating = nested;
        }

        internal static bool IsUpdating => updating;

        private static int TryRender(OnScreenText.OST slot)
        {
            var current = rendering;
            if (current == null || !ReferenceEquals(current.Slot, slot) || !ReferenceEquals(current, Volatile.Read(ref assigned)) ||
                current.Epoch != NotificationPresentations.Epoch || current.NativeEpoch != NotificationNativeRuntime.Epoch ||
                !NotificationPresentations.IsAvailable)
                return -1;
            try
            {
                string value = LordMessageRuntime.TextBindings.TryGet(slot, out var replacement)
                    ? replacement.Resolve() : current.Subtitle;
                if (Interlocked.CompareExchange(ref confirmed, 1, 0) == 0)
                    NativeApiLog.Info(log, "NOTIFICATION_SUBTITLE_HUD_CONFIRMED: bound OST callback after startup cleanup.");
                if (value == null || !MainViewModel.viewModelLoaded) return 0;
                var view = MainViewModel.Instance;
                if (view.OST_Message_Bar_Text != value) view.OST_Message_Bar_Text = value;
                return 1;
            }
            catch (Exception error)
            {
                if (Interlocked.CompareExchange(ref loggedFailure, 1, 0) == 0)
                    NativeApiLog.Error(log, "NOTIFICATION_SUBTITLE_HUD_FAILED: " + error);
                return 0;
            }
        }
    }
}
