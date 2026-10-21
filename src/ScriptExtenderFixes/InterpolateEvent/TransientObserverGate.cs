using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace APIShared.ScriptExtenderFixes
{
    // Decides whether every current observer of one R3 Subject<T> provably reads T only synchronously.
    // Only plain Subscribe(Action<T>) observers (R3.AnonymousObserver<T>) whose handlers pass
    // TransientHandlerAnalysis qualify; operators, custom observers and unknown layouts refuse.
    // R3 appends each subscription at the tail of its linked observer list (root.Previous is the tail),
    // so comparing the (root, tail) references detects every addition without walking the list.
    // Removing an observer can leave both unchanged, which is safe: the remaining set was validated.
    // Not thread-safe; the owner calls it from one thread. Concurrent subscriptions on other threads
    // are detected by the next Changed/Refresh call because the R3 list writes are reference writes.
    internal sealed class TransientObserverGate
    {
        private const int MaximumObservers = 4096;
        private readonly object subject;
        private readonly Type argsType, handlerType, anonymousObserver;
        private readonly Func<MethodBase, bool> isAllowedGetter;
        private readonly Func<object, object> readRoot, readPrevious, readNext, readObserver;
        private readonly Dictionary<MethodInfo, bool> verdicts = new Dictionary<MethodInfo, bool>();
        private object validatedRoot, validatedTail;

        internal TransientObserverGate(object subject, Type argsType, Func<MethodBase, bool> isAllowedGetter)
        {
            this.subject = subject ?? throw new ArgumentNullException(nameof(subject));
            this.argsType = argsType ?? throw new ArgumentNullException(nameof(argsType));
            this.isAllowedGetter = isAllowedGetter ?? throw new ArgumentNullException(nameof(isAllowedGetter));
            Type subjectType = typeof(R3.Subject<>).MakeGenericType(argsType);
            if (subject.GetType() != subjectType) throw new ArgumentException("subject is not exactly " + subjectType, nameof(subject));
            FieldInfo root = subjectType.GetField("root", BindingFlags.Instance | BindingFlags.NonPublic);
            Type nodeType = root?.FieldType;
            if (nodeType == null || nodeType.DeclaringType == null || nodeType.Name != "ObserverNode")
                throw new MissingFieldException("R3.Subject<T>.root differs from the audit");
            FieldInfo previous = nodeType.GetField("<Previous>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo next = nodeType.GetField("<Next>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo observer = nodeType.GetField("Observer", BindingFlags.Instance | BindingFlags.Public);
            if (previous?.FieldType != nodeType || next?.FieldType != nodeType ||
                observer?.FieldType != typeof(R3.Observer<>).MakeGenericType(argsType))
                throw new MissingFieldException("R3.Subject<T>.ObserverNode differs from the audit");
            Type anonymousDefinition = typeof(R3.Observer<>).Assembly.GetType("R3.AnonymousObserver`1", false) ??
                throw new TypeLoadException("R3.AnonymousObserver`1 is missing");
            anonymousObserver = anonymousDefinition.MakeGenericType(argsType);
            handlerType = typeof(Action<>).MakeGenericType(argsType);
            readRoot = CompileReader(subjectType, root);
            readPrevious = CompileReader(nodeType, previous);
            readNext = CompileReader(nodeType, next);
            readObserver = CompileReader(nodeType, observer);
            Validate(readRoot(subject));
        }

        internal bool IsSafe { get; private set; }

        internal string LastRefusal { get; private set; }

        // Fast check: two field loads; true when an observer may have been added since the last validation.
        internal bool Changed()
        {
            object root = readRoot(subject);
            return !ReferenceEquals(root, validatedRoot) || !ReferenceEquals(Tail(root), validatedTail);
        }

        // Revalidates only after a change. Never throws: any failure refuses.
        internal bool Refresh()
        {
            object root = readRoot(subject);
            if (!ReferenceEquals(root, validatedRoot) || !ReferenceEquals(Tail(root), validatedTail)) Validate(root);
            return IsSafe;
        }

        private object Tail(object root) => root == null ? null : readPrevious(root);

        private void Validate(object root)
        {
            validatedRoot = root;
            validatedTail = Tail(root);
            string refusal = null;
            try
            {
                int observers = 0;
                for (object node = root; node != null && refusal == null; node = readNext(node))
                    refusal = ++observers > MaximumObservers ? "observer list too long" : Refusal(readObserver(node));
            }
            catch (Exception exception)
            {
                refusal = "validation failed: " + exception.GetType().Name + ": " + exception.Message;
            }
            LastRefusal = refusal;
            IsSafe = refusal == null;
        }

        private string Refusal(object observer)
        {
            Type type = observer?.GetType();
            if (type != anonymousObserver) return "observer " + (type?.FullName ?? "null");
            FieldInfo[] handlers = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(field => field.FieldType == handlerType).ToArray();
            if (handlers.Length != 1) return "unexpected AnonymousObserver layout";
            if (!(handlers[0].GetValue(observer) is Delegate handler)) return "observer without handler";
            foreach (Delegate single in handler.GetInvocationList())
            {
                MethodInfo method = single.Method;
                // A closed static delegate shifts the parameter positions; treat it as unknown.
                if (method == null || (method.IsStatic && single.Target != null)) return "closed static handler";
                if (!verdicts.TryGetValue(method, out bool transient))
                    verdicts[method] = transient = TransientHandlerAnalysis.IsTransient(method, argsType, isAllowedGetter);
                if (!transient) return "handler " + method.DeclaringType?.FullName + "." + method.Name;
            }
            return null;
        }

        // Called for every unit and frame; a compiled field load avoids reflection costs there.
        private static Func<object, object> CompileReader(Type owner, FieldInfo field)
        {
            var method = new DynamicMethod("APIShared.TransientObserverGate.Read." + field.Name, typeof(object),
                new[] { typeof(object) }, typeof(TransientObserverGate).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, object>)method.CreateDelegate(typeof(Func<object, object>));
        }
    }
}
