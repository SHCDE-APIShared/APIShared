using BepInEx.Configuration;
using BepInEx.Logging;
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
    // The Script Extender detours GameMap.interpChimp with ManagedHookManager.GameMap_InterpChimpHook,
    // which allocates a new UnitUnityVisualInterpolateEventArgs for every visible unit and frame
    // (about 450 000 objects or 10-14 MB per second in large battles) before raising
    // UnitR3EventHooks.OnUnitUnityVisualInterpolate. A transpiler replaces that one allocation with
    // Rent and calls Release right after Raise; interpChimp itself is not hooked.
    //
    // Reusing an event object is only safe when no receiver can keep it. Rent therefore reuses the
    // instance only while TransientObserverGate proves that every observer of the Extender's subject
    // reads the args only synchronously; any other subscriber (R3 operators, custom observers, handlers
    // that store or pass the args) keeps the original allocation for every call. Release abandons the
    // shared instance when the observer list changed during Raise, because an unvalidated observer
    // may have received it. Only the outermost raise on the Unity main thread reuses; nested raises
    // and other threads allocate as before. A subscriber exception skips Release; the next frame then
    // abandons the instance and resets the depth.
    internal static class InterpolateEventReuseRuntime
    {
        private const string Prefix = "SE_INTERPOLATE_EVENT_REUSE: ";
        private static readonly string[] AllowedGetters = { "get_Chimp", "get_Phase", "get_SkipOriginalFunction" };
        private static readonly Harmony harmony = new Harmony("APIShared.ScriptExtenderFixes.InterpolateEventReuse");
        private static ManualLogSource logger;
        private static TransientObserverGate gate;
        private static UnitUnityVisualInterpolateEventArgs reusable;
        private static bool outerReused, wasSafe = true;
        private static int mainThreadId = -1, depth, attempted, confirmed;

        internal static void Initialize(ConfigFile config, ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            logger = log;
            bool enabled = config.Bind("ScriptExtenderFixes", "EnableInterpolateEventReuse", true,
                "Reuse the Script Extender's per-unit interpolate event object while every subscriber provably reads it only " +
                "synchronously; otherwise the original allocation is kept. Applies after restarting the game.").Value;
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
                ValidateHookBody(PatchProcessor.GetOriginalInstructions(target));
                ValidateGetters();
                gate = new TransientObserverGate(Subject(), typeof(UnitUnityVisualInterpolateEventArgs), IsAllowedGetter);
                mainThreadId = Thread.CurrentThread.ManagedThreadId;
                // Persistent per-frame recovery after a subscriber exception skipped Release.
                UnityEngine.Application.onBeforeRender += ResetPerFrame;
                // The transpiler is the last step: nothing is published before every check passed.
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(InterpolateEventReuseRuntime), nameof(Transpiler)));
                Write("installed on ManagedHookManager.GameMap_InterpChimpHook (transpiler only); reuse requires provably transient subscribers");
            }
            catch (Exception exception)
            {
                mainThreadId = -1;
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
            if (allocations != 1 || raises != 1)
                throw new InvalidOperationException("expected one event allocation and one Raise in the Extender hook, found " +
                    allocations + "/" + raises);
        }

        // Auto-property getters only return a field; they cannot publish the args object.
        internal static void ValidateGetters()
        {
            foreach (string getter in AllowedGetters)
            {
                MethodInfo method = typeof(UnitUnityVisualInterpolateEventArgs).GetMethod(getter,
                    BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (method == null || !method.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    throw new MissingMethodException("UnitUnityVisualInterpolateEventArgs." + getter + " is no auto-property getter");
            }
        }

        internal static object Subject()
        {
            FieldInfo field = typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>).GetField("_subject",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(R3.Subject<UnitUnityVisualInterpolateEventArgs>))
                throw new MissingFieldException("R3EventHook<T>._subject differs from the audit");
            return field.GetValue(UnitR3EventHooks.OnUnitUnityVisualInterpolate) ??
                throw new InvalidOperationException("the interpolate event subject is null");
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

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo constructor = Constructor();
            MethodInfo rent = AccessTools.Method(typeof(InterpolateEventReuseRuntime), nameof(Rent));
            MethodInfo release = AccessTools.Method(typeof(InterpolateEventReuseRuntime), nameof(Release));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, constructor))
                {
                    // Same stack contract: (EventHookPhase, Chimp) -> UnitUnityVisualInterpolateEventArgs.
                    yield return new CodeInstruction(OpCodes.Call, rent).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    continue;
                }
                yield return instruction;
                // Raise returns void and Release takes nothing: the evaluation stack is unchanged.
                if (IsRaise(instruction)) yield return new CodeInstruction(OpCodes.Call, release);
            }
        }

        // Runs inside the Extender's try block before Raise: it must not throw (the gate never does).
        private static UnitUnityVisualInterpolateEventArgs Rent(EventHookPhase phase, Chimp chimp)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return new UnitUnityVisualInterpolateEventArgs(phase, chimp);
            if (depth++ > 0)
                return new UnitUnityVisualInterpolateEventArgs(phase, chimp);
            outerReused = gate.Refresh();
            if (outerReused != wasSafe) ReportVerdict(outerReused);
            if (!outerReused)
                return new UnitUnityVisualInterpolateEventArgs(phase, chimp);
            UnitUnityVisualInterpolateEventArgs args = reusable;
            if (args == null || args.Phase != phase) reusable = args = new UnitUnityVisualInterpolateEventArgs(phase, chimp);
            // Reset everything a subscriber may have changed on the shared instance.
            args.Chimp = chimp;
            args.SkipOriginalFunction = false;
            if (confirmed == 0)
            {
                confirmed = 1;
                Write("confirmed: first interpolate event served from the reused instance");
            }
            return args;
        }

        private static void Release()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId || depth == 0) return;
            if (--depth != 0 || !outerReused) return;
            outerReused = false;
            // A subscription added during Raise may have received the shared instance: never reuse it again.
            if (gate.Changed()) reusable = null;
        }

        private static void ResetPerFrame()
        {
            if (depth == 0) return;
            depth = 0;
            outerReused = false;
            reusable = null;
        }

        // Logged on each transition only (subscriptions change rarely).
        private static void ReportVerdict(bool safe)
        {
            wasSafe = safe;
            Write(safe
                ? "all interpolate subscribers are transient again; reuse resumed"
                : "allocation kept: a subscriber may retain the event object (" + gate.LastRefusal + ")");
        }

        private static void Write(string message)
        {
            try { logger?.LogInfo("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + Prefix + message); }
            catch { /* Logging must not interrupt rendering. */ }
        }
    }
}
