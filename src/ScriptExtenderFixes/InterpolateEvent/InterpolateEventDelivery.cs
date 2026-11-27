using R3;
using HarmonyLib;
using System.Linq;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    // This proof controls an individual delivery, never a snapshot of a mutable subscriber list.
    // Unknown observers receive an independently owned event object before any callback runs.
    internal sealed class TransientObserverPolicy
    {
        private sealed class Verdict { internal bool Safe; }
        private readonly ConditionalWeakTable<object, Verdict> verdicts = new ConditionalWeakTable<object, Verdict>();
        private readonly Type anonymousObserver;
        private readonly Func<object, Action<UnitUnityVisualInterpolateEventArgs>> readHandler;
        private object lastObserver;
        private bool lastSafe;

        internal TransientObserverPolicy()
        {
            anonymousObserver = typeof(Observer<>).Assembly.GetType("R3.AnonymousObserver`1", true)
                .MakeGenericType(typeof(UnitUnityVisualInterpolateEventArgs));
            FieldInfo handler = null;
            foreach (FieldInfo field in anonymousObserver.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.FieldType == typeof(Action<UnitUnityVisualInterpolateEventArgs>))
                {
                    if (handler != null) throw new MissingFieldException("Ambiguous R3 AnonymousObserver handler");
                    handler = field;
                }
            if (handler == null) throw new MissingFieldException("R3 AnonymousObserver handler");
            MethodInfo core = anonymousObserver.GetMethod("OnNextCore", BindingFlags.Instance | BindingFlags.NonPublic);
            var body = PatchProcessor.GetOriginalInstructions(core).Where(i => i.opcode != OpCodes.Nop).ToArray();
            MethodInfo invoke = typeof(Action<UnitUnityVisualInterpolateEventArgs>).GetMethod("Invoke");
            if (Harmony.GetPatchInfo(core) != null || body.Length != 5 || body[0].opcode != OpCodes.Ldarg_0 ||
                body[1].opcode != OpCodes.Ldfld || !Equals(body[1].operand, handler) || body[2].opcode != OpCodes.Ldarg_1 ||
                body[3].opcode != OpCodes.Callvirt || !Equals(body[3].operand, invoke) || body[4].opcode != OpCodes.Ret)
                throw new InvalidOperationException("R3 AnonymousObserver forwarding differs from the audit");
            var reader = new DynamicMethod("APIShared.Interpolate.ReadHandler", typeof(Action<UnitUnityVisualInterpolateEventArgs>),
                new[] { typeof(object) }, typeof(TransientObserverPolicy).Module, true);
            ILGenerator il = reader.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, anonymousObserver); il.Emit(OpCodes.Ldfld, handler); il.Emit(OpCodes.Ret);
            readHandler = (Func<object, Action<UnitUnityVisualInterpolateEventArgs>>)reader.CreateDelegate(
                typeof(Func<object, Action<UnitUnityVisualInterpolateEventArgs>>));
        }

        // Prewarm already registered receivers during initialization. This list walk is only a
        // startup optimization; actual-delivery checks remain the safety boundary if it changes.
        internal void Warm(Subject<UnitUnityVisualInterpolateEventArgs> subject)
        {
            try
            {
                FieldInfo root = subject.GetType().GetField("root", BindingFlags.Instance | BindingFlags.NonPublic);
                Type nodeType = root.FieldType;
                FieldInfo observer = nodeType.GetField("Observer", BindingFlags.Instance | BindingFlags.Public);
                PropertyInfo next = nodeType.GetProperty("Next");
                object node = root.GetValue(subject);
                for (int count = 0; node != null && count < 4096; count++, node = next.GetValue(node, null))
                    IsTransient((Observer<UnitUnityVisualInterpolateEventArgs>)observer.GetValue(node));
            }
            catch { /* Unrecognized/changing layout only skips warming, never delivery validation. */ }
        }

        internal bool IsTransient(Observer<UnitUnityVisualInterpolateEventArgs> observer)
        {
            if (ReferenceEquals(observer, lastObserver)) return lastSafe;
            bool safe = false;
            try
            {
                if (observer != null && observer.GetType() == anonymousObserver)
                {
                    if (!verdicts.TryGetValue(observer, out Verdict verdict))
                    {
                        safe = ProveHandler(readHandler(observer));
                        verdicts.Add(observer, new Verdict { Safe = safe });
                    }
                    else safe = verdict.Safe;
                }
            }
            catch { safe = false; }
            lastObserver = observer;
            lastSafe = safe;
            return safe;
        }

        private static bool ProveHandler(Delegate handler)
        {
            if (handler == null) return false;
            foreach (Delegate single in handler.GetInvocationList())
            {
                MethodInfo method = single.Method;
                if (method == null || (method.IsStatic && single.Target != null) ||
                    !TransientHandlerAnalysis.IsTransient(method, typeof(UnitUnityVisualInterpolateEventArgs),
                        InterpolateEventReuseRuntime.IsAllowedGetter)) return false;
            }
            return true;
        }
    }

    // Owned by the validated Unity main thread. Other threads and nested hook calls use fresh args.
    // The pooled instance never reaches an observer that could retain it. Promotion is per complete
    // notification, so later observers see changes made by earlier receivers to the same fresh args.
    internal sealed class InterpolateEventDelivery
    {
        private readonly Subject<UnitUnityVisualInterpolateEventArgs> subject;
        private readonly int mainThreadId;
        private readonly TransientObserverPolicy policy;
        private UnitUnityVisualInterpolateEventArgs reusable, rented, published;
        private int depth;

        internal InterpolateEventDelivery(Subject<UnitUnityVisualInterpolateEventArgs> subject, int mainThreadId)
        {
            this.subject = subject ?? throw new ArgumentNullException(nameof(subject));
            this.mainThreadId = mainThreadId;
            policy = new TransientObserverPolicy();
            policy.Warm(subject);
        }

        internal Subject<UnitUnityVisualInterpolateEventArgs> Subject => subject;

        internal UnitUnityVisualInterpolateEventArgs Rent(EventHookPhase phase, Chimp chimp)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return new UnitUnityVisualInterpolateEventArgs(phase, chimp);
            int nesting = depth++;
            try
            {
                if (nesting != 0) return new UnitUnityVisualInterpolateEventArgs(phase, chimp);
                if (reusable == null || reusable.Phase != phase)
                    reusable = new UnitUnityVisualInterpolateEventArgs(phase, chimp);
                reusable.Chimp = chimp;
                reusable.SkipOriginalFunction = false;
                rented = published = reusable;
                return rented;
            }
            catch { Release(); throw; }
        }

        internal void Deliver(Subject<UnitUnityVisualInterpolateEventArgs> publisher,
            Observer<UnitUnityVisualInterpolateEventArgs> observer, UnitUnityVisualInterpolateEventArgs value)
        {
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId && depth == 1 &&
                ReferenceEquals(publisher, subject) && ReferenceEquals(value, rented))
            {
                if (ReferenceEquals(published, rented) && !policy.IsTransient(observer))
                    published = new UnitUnityVisualInterpolateEventArgs(rented.Phase, rented.Chimp)
                    { SkipOriginalFunction = rented.SkipOriginalFunction };
                value = published;
            }
            // R3 retains disposal checks and its independent callback/error handling.
            observer.OnNext(value);
        }

        // If a different hook reaches the wrapper, it must not receive an unguarded pooled value.
        internal UnitUnityVisualInterpolateEventArgs Retainable(UnitUnityVisualInterpolateEventArgs args) =>
            ReferenceEquals(args, rented) && args != null
                ? new UnitUnityVisualInterpolateEventArgs(args.Phase, args.Chimp) { SkipOriginalFunction = args.SkipOriginalFunction }
                : args;

        internal void Release()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId || depth == 0) return;
            if (--depth == 0)
            {
                rented = published = null;
                // The private pool must not keep the last unit (and its scene) alive between maps.
                // Retainable receivers own a different object, whose Chimp reference stays intact.
                if (reusable != null) reusable.Chimp = null;
            }
        }
    }
}
