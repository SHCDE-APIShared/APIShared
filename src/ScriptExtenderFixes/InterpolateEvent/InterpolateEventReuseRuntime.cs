using BepInEx.Configuration;
using BepInEx.Logging;
using R3;
using HarmonyLib;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;
using SHCDESE.ManagedHooks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    // Guard the actual R3 observer call, not the subscriber-list shape. A subscriber added
    // after Rent can only receive freshly promoted args unless its handler provably cannot retain
    // them. R3 still owns iteration, subscription versions, ordering and exception handling.
    internal static class InterpolateEventReuseRuntime
    {
        private const string Prefix = "SE_INTERPOLATE_EVENT_REUSE: ";
        private static readonly string[] AllowedGetters = { "get_Chimp", "get_Phase", "get_SkipOriginalFunction" };
        private static readonly Harmony harmony = new Harmony("APIShared.ScriptExtenderFixes.InterpolateEventReuse");
        private static ManualLogSource logger;
        private static volatile InterpolateEventDelivery delivery;
        private static Action<R3EventHook<UnitUnityVisualInterpolateEventArgs>, UnitUnityVisualInterpolateEventArgs> raise;
        private static int attempted, guardInstalled;
        private static readonly object installation = new object();

        internal static void Initialize(ConfigFile config, ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            logger = log;
            bool enabled = config.Bind("ScriptExtenderFixes", "EnableInterpolateEventReuse", true,
                "Avoid per-unit interpolate allocations for provably transient observers; retainable or unknown observers " +
                "receive fresh arguments. Applies after restarting the game.").Value;
            if (!enabled) { Write("disabled by startup configuration"); return; }
            try
            {
                if (!Internal.UnityMainThreadDispatch.IsMainThread)
                    throw new InvalidOperationException("initialization must run on the validated Unity main thread");
                MethodInfo target = typeof(ManagedHookManager).GetMethod("GameMap_InterpChimpHook",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(GameMap), typeof(Chimp), typeof(bool) }, null);
                if (target == null || target.ReturnType != typeof(void))
                    throw new MissingMethodException("ManagedHookManager.GameMap_InterpChimpHook signature differs from the audit");
                if (Harmony.GetPatchInfo(target) != null)
                    throw new InvalidOperationException("Extender interpolate hook is already patched");
                ValidateHookBody(PatchProcessor.GetOriginalInstructions(target));
                ValidateGetters();
                ValidateArgumentConstruction();
                ValidateRaiseBody();
                var candidate = new InterpolateEventDelivery((Subject<UnitUnityVisualInterpolateEventArgs>)Subject(),
                    Thread.CurrentThread.ManagedThreadId);
                raise = CreateRaise();
                EnsureDeliveryGuard();
                delivery = candidate;
                // Install allocation reuse last. A partial initialization leaves original allocations intact.
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(InterpolateEventReuseRuntime), nameof(Transpiler)));
                Write("installed: per-observer delivery guard; unknown/retaining observers receive fresh event arguments");
            }
            catch (Exception exception)
            {
                delivery = null;
                Write("skipped: " + exception);
            }
        }

        internal static void ValidateHookBody(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo constructor = Constructor() ??
                throw new MissingMethodException("UnitUnityVisualInterpolateEventArgs(EventHookPhase, Chimp)");
            List<CodeInstruction> list = instructions.ToList();
            int allocations = list.Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, constructor));
            int raises = list.Count(IsRaise);
            int allocation = list.FindIndex(i => i.opcode == OpCodes.Newobj && Equals(i.operand, constructor));
            // The Extender must consume the args directly: no stored original reference may bypass
            // promotion when a callback changes properties on the freshly published object.
            if (allocations != 1 || raises != 1 || allocation + 1 >= list.Count || !IsRaise(list[allocation + 1]))
                throw new InvalidOperationException("expected one directly consumed event allocation and one Raise");
        }

        // Auto-property getters only return a field; they cannot publish the args object.
        internal static void ValidateGetters()
        {
            foreach (string getter in AllowedGetters)
            {
                Type declaring = getter == "get_Chimp" ? typeof(UnitUnityVisualInterpolateEventArgs) : typeof(EventHookBase);
                MethodInfo method = declaring.GetMethod(getter,
                    BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (method == null || !method.IsDefined(typeof(CompilerGeneratedAttribute), false) ||
                    Harmony.GetPatchInfo(method) != null)
                    throw new MissingMethodException("UnitUnityVisualInterpolateEventArgs." + getter + " is no auto-property getter");
                var body = PatchProcessor.GetOriginalInstructions(method).Where(i => i.opcode != OpCodes.Nop).ToArray();
                if (method.IsVirtual || body.Length != 3 || body[0].opcode != OpCodes.Ldarg_0 ||
                    body[1].opcode != OpCodes.Ldfld || !(body[1].operand is FieldInfo field) ||
                    field.FieldType != method.ReturnType || body[2].opcode != OpCodes.Ret)
                    throw new InvalidOperationException("Event getter differs from its non-publishing field-load contract");
            }
        }

        internal static object Subject()
        {
            FieldInfo field = typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>).GetField("_subject",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || !field.IsInitOnly || field.FieldType != typeof(R3.Subject<UnitUnityVisualInterpolateEventArgs>))
                throw new MissingFieldException("R3EventHook<T>._subject differs from the audit");
            return field.GetValue(UnitR3EventHooks.OnUnitUnityVisualInterpolate) ??
                throw new InvalidOperationException("the interpolate event subject is null");
        }

        // Reuse and promotion also require construction/reset to have no observable side effects.
        // Validate the constructor's direct argument assignments and the auto-property setters;
        // version labels and CompilerGenerated alone do not establish that contract.
        internal static void ValidateArgumentConstruction()
        {
            Type type = typeof(UnitUnityVisualInterpolateEventArgs);
            if (type.TypeInitializer != null || typeof(EventHookBase).TypeInitializer != null)
                throw new InvalidOperationException("Event argument initialization differs from the audit");
            foreach (string name in new[] { "set_Phase", "set_Chimp", "set_SkipOriginalFunction" })
            {
                Type declaring = name == "set_Chimp" ? type : typeof(EventHookBase);
                MethodInfo setter = declaring.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (setter == null) throw new MissingMethodException(type.FullName, name);
                var code = PatchProcessor.GetOriginalInstructions(setter).Where(i => i.opcode != OpCodes.Nop).ToArray();
                if (setter.IsVirtual || Harmony.GetPatchInfo(setter) != null || code.Length != 4 ||
                    code[0].opcode != OpCodes.Ldarg_0 || code[1].opcode != OpCodes.Ldarg_1 ||
                    code[2].opcode != OpCodes.Stfld || !(code[2].operand is FieldInfo field) ||
                    field.DeclaringType != setter.DeclaringType || code[3].opcode != OpCodes.Ret)
                    throw new InvalidOperationException("Event setter differs from its field-store contract");
            }
            ConstructorInfo baseConstructor = typeof(EventHookBase).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (baseConstructor == null) throw new MissingMethodException("EventHookBase constructor");
            if (Harmony.GetPatchInfo(Constructor()) != null || Harmony.GetPatchInfo(baseConstructor) != null)
                throw new InvalidOperationException("Event argument constructor is already patched");
            var body = PatchProcessor.GetOriginalInstructions(Constructor()).Where(i => i.opcode != OpCodes.Nop).ToArray();
            if (body.Length != 9 || body[0].opcode != OpCodes.Ldarg_0 || body[1].opcode != OpCodes.Call ||
                !Equals(body[1].operand, baseConstructor) || body[2].opcode != OpCodes.Ldarg_0 || body[3].opcode != OpCodes.Ldarg_1 ||
                body[4].opcode != OpCodes.Call || !Equals(body[4].operand, typeof(EventHookBase).GetMethod("set_Phase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) ||
                body[5].opcode != OpCodes.Ldarg_0 || body[6].opcode != OpCodes.Ldarg_2 || body[7].opcode != OpCodes.Call ||
                !Equals(body[7].operand, type.GetMethod("set_Chimp")) || body[8].opcode != OpCodes.Ret)
                throw new InvalidOperationException("Event argument constructor no longer directly assigns its inputs");
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(baseConstructor))
            {
                if (instruction.opcode == OpCodes.Nop || instruction.opcode == OpCodes.Ldarg_0 ||
                    instruction.opcode == OpCodes.Ldc_I4_0 || instruction.opcode == OpCodes.Ret) continue;
                if (instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field && field.DeclaringType == typeof(EventHookBase)) continue;
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, typeof(EventArgs).GetConstructor(Type.EmptyTypes))) continue;
                throw new InvalidOperationException("Event base constructor has unknown initialization work");
            }
        }

        internal static bool IsAllowedGetter(MethodBase method) =>
            (method.DeclaringType == typeof(UnitUnityVisualInterpolateEventArgs) || method.DeclaringType == typeof(EventHookBase)) &&
            Array.IndexOf(AllowedGetters, method.Name) >= 0;

        private static ConstructorInfo Constructor() =>
            typeof(UnitUnityVisualInterpolateEventArgs).GetConstructor(new[] { typeof(EventHookPhase), typeof(Chimp) });

        private static bool IsRaise(CodeInstruction instruction) =>
            (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
            instruction.operand is MethodInfo method && method.Name == "Raise" &&
            method.DeclaringType == typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>);

        internal static void EnsureDeliveryGuard()
        {
            lock (installation)
            {
                if (guardInstalled != 0) return;
                MethodInfo target = typeof(Subject<UnitUnityVisualInterpolateEventArgs>).GetMethod("OnNext");
                MethodInfo observerCall = typeof(Observer<UnitUnityVisualInterpolateEventArgs>).GetMethod("OnNext");
                if (Harmony.GetPatchInfo(target) != null || Harmony.GetPatchInfo(observerCall) != null)
                    throw new InvalidOperationException("R3 delivery is already patched; retaining original allocation");
                ValidateDeliveryBody(PatchProcessor.GetOriginalInstructions(target));
                MethodInfo core = typeof(Observer<UnitUnityVisualInterpolateEventArgs>).GetMethod("OnNextCore",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                ValidateForwardedArgument(PatchProcessor.GetOriginalInstructions(observerCall), core);
                // Copy the audited R3 loop into our own non-generic stand-in. Patching Subject<T>
                // itself could affect other T through Mono's shared generic machine code.
                var transforms = AccessTools.GetDeclaredMethods(typeof(InterpolateEventReuseRuntime)).Where(method =>
                    method.ReturnType == typeof(IEnumerable<CodeInstruction>) && method.Name.StartsWith("<DispatchCopy>", StringComparison.Ordinal)).ToArray();
                if (transforms.Length != 1 || !transforms[0].IsStatic || transforms[0].GetParameters().Length != 1 ||
                    transforms[0].GetParameters()[0].ParameterType != typeof(IEnumerable<CodeInstruction>))
                    throw new InvalidOperationException("Compiler-generated reverse-patch transform differs from the Harmony contract");
                harmony.CreateReversePatcher(target, new HarmonyMethod(typeof(InterpolateEventReuseRuntime), nameof(DispatchCopy)))
                    .Patch(HarmonyReversePatchType.Original);
                guardInstalled = 1;
            }
        }

        internal static void ValidateDeliveryBody(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> body = instructions.ToList();
            if (body.Any(i => i.blocks.Count != 0))
                throw new InvalidOperationException("R3 delivery exception regions differ from the audited loop");
            MethodInfo expected = typeof(Observer<UnitUnityVisualInterpolateEventArgs>).GetMethod("OnNext");
            ValidateForwardedArgument(body, expected);
        }

        internal static void ValidateForwardedArgument(IEnumerable<CodeInstruction> instructions, MethodInfo expected)
        {
            List<CodeInstruction> body = instructions.Where(i => i.opcode != OpCodes.Nop).ToList();
            int forwards = 0;
            for (int index = 0; index < body.Count; index++)
            {
                CodeInstruction instruction = body[index];
                bool address = instruction.opcode == OpCodes.Ldarga || instruction.opcode == OpCodes.Ldarga_S ||
                    instruction.opcode == OpCodes.Starg || instruction.opcode == OpCodes.Starg_S;
                bool general = instruction.opcode == OpCodes.Ldarg || instruction.opcode == OpCodes.Ldarg_S || address;
                if (general && Convert.ToInt32(instruction.operand) == 1 && address)
                    throw new InvalidOperationException("R3 delivery takes/stores its argument address");
                if (instruction.opcode != OpCodes.Ldarg_1 && !(general && Convert.ToInt32(instruction.operand) == 1)) continue;
                if (++forwards != 1 || index + 1 >= body.Count ||
                    (body[index + 1].opcode != OpCodes.Callvirt && body[index + 1].opcode != OpCodes.Call) ||
                    !Equals(body[index + 1].operand, expected))
                    throw new InvalidOperationException("R3 delivery forwards/retains args outside the audited callback");
            }
            if (forwards != 1) throw new InvalidOperationException("R3 delivery callback differs from the audit");
        }

        // A persistent reverse patch targets only this stand-in. R3's actual Subject<T>.OnNext
        // and all other payload types remain untouched. The compiler emits the local transpiler
        // with the name discovered by the installed Harmony ReversePatcher.GetTranspiler contract.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void DispatchCopy(Subject<UnitUnityVisualInterpolateEventArgs> publisher,
            UnitUnityVisualInterpolateEventArgs value)
        {
            IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => DeliveryTranspiler(instructions);
            GC.KeepAlive((Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>)Transpiler);
            throw new NotSupportedException("Interpolate delivery copy was not initialized");
        }

        private static IEnumerable<CodeInstruction> DeliveryTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo expected = typeof(Observer<UnitUnityVisualInterpolateEventArgs>).GetMethod("OnNext");
            MethodInfo replacement = AccessTools.Method(typeof(InterpolateEventReuseRuntime), nameof(Deliver));
            int replacements = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && Equals(instruction.operand, expected))
                {
                    replacements++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                }
                else yield return instruction;
            }
            if (replacements != 1) throw new InvalidOperationException("The private R3 loop must guard exactly one observer call");
        }

        private static void Deliver(Observer<UnitUnityVisualInterpolateEventArgs> observer,
            UnitUnityVisualInterpolateEventArgs value, Subject<UnitUnityVisualInterpolateEventArgs> publisher)
        {
            InterpolateEventDelivery current = delivery;
            if (current == null) observer.OnNext(value);
            else current.Deliver(publisher, observer, value);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo constructor = Constructor();
            MethodInfo rent = AccessTools.Method(typeof(InterpolateEventReuseRuntime), nameof(Rent));
            MethodInfo guardedRaise = AccessTools.Method(typeof(InterpolateEventReuseRuntime), nameof(RaiseAndRelease));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, constructor))
                    yield return new CodeInstruction(OpCodes.Call, rent).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                else if (IsRaise(instruction))
                    yield return new CodeInstruction(OpCodes.Call, guardedRaise).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                else yield return instruction;
            }
        }

        internal static void ValidateRaiseBody()
        {
            MethodInfo method = typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>).GetMethod("Raise",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (Harmony.GetPatchInfo(method) != null)
                throw new InvalidOperationException("The Extender Raise wrapper is already patched");
            MethodInfo expected = typeof(Subject<UnitUnityVisualInterpolateEventArgs>).GetMethod("OnNext");
            List<CodeInstruction> body = PatchProcessor.GetOriginalInstructions(method).Where(i => i.opcode != OpCodes.Nop).ToList();
            FieldInfo field = typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>).GetField("_subject",
                BindingFlags.Instance | BindingFlags.NonPublic);
            // A class-constrained generic null check contains a non-allocating box T.
            if (body.Count == 8 && body[1].opcode == OpCodes.Box &&
                Equals(body[1].operand, typeof(UnitUnityVisualInterpolateEventArgs))) body.RemoveAt(1);
            // Release IL: if (args != null) _subject.OnNext(args). Unknown wrappers decline reuse.
            if (body.Count != 7 || body[0].opcode != OpCodes.Ldarg_1 ||
                (body[1].opcode != OpCodes.Brfalse && body[1].opcode != OpCodes.Brfalse_S) ||
                body[2].opcode != OpCodes.Ldarg_0 || body[3].opcode != OpCodes.Ldfld || !Equals(body[3].operand, field) ||
                body[4].opcode != OpCodes.Ldarg_1 || body[5].opcode != OpCodes.Callvirt || !Equals(body[5].operand, expected) ||
                body[6].opcode != OpCodes.Ret)
                throw new InvalidOperationException("Extender Raise wrapper differs from the audit");
        }

        private static Action<R3EventHook<UnitUnityVisualInterpolateEventArgs>, UnitUnityVisualInterpolateEventArgs> CreateRaise()
        {
            MethodInfo method = typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>).GetMethod("Raise",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var wrapper = new DynamicMethod("APIShared.Interpolate.Raise", typeof(void),
                new[] { typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>), typeof(UnitUnityVisualInterpolateEventArgs) },
                typeof(InterpolateEventReuseRuntime).Module, true);
            ILGenerator il = wrapper.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, method); il.Emit(OpCodes.Ret);
            return (Action<R3EventHook<UnitUnityVisualInterpolateEventArgs>, UnitUnityVisualInterpolateEventArgs>)wrapper.CreateDelegate(
                typeof(Action<R3EventHook<UnitUnityVisualInterpolateEventArgs>, UnitUnityVisualInterpolateEventArgs>));
        }

        private static UnitUnityVisualInterpolateEventArgs Rent(EventHookPhase phase, Chimp chimp)
        {
            InterpolateEventDelivery current = delivery;
            return current == null ? new UnitUnityVisualInterpolateEventArgs(phase, chimp) : current.Rent(phase, chimp);
        }

        internal static void RaiseAndRelease(R3EventHook<UnitUnityVisualInterpolateEventArgs> hook, UnitUnityVisualInterpolateEventArgs args)
        {
            InterpolateEventDelivery current = delivery;
            try
            {
                if (current != null && hook != null && ReferenceEquals(hook.Observable, current.Subject))
                {
                    if (args != null) DispatchCopy(current.Subject, args);
                }
                else raise(hook, current == null ? args : current.Retainable(args));
            }
            finally { current?.Release(); }
        }

        private static void Write(string message)
        {
            try { logger?.LogInfo("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + Prefix + message); }
            catch { /* Logging must not interrupt rendering. */ }
        }
    }
}
