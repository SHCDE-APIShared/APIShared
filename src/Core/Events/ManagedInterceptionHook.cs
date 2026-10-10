using System;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace APIShared.Events
{
    // The static event publisher roots each installed hook. No release/disposal path is exposed.
    // The trampoline exists before Apply; only a failed unpublished candidate is rolled back.
    internal sealed class ManagedInterceptionHook<T> where T : Delegate
    {
        private Hook hook;
        internal T Original { get; private set; }
        internal void Install(Type type, string name, Type result, Type[] parameters, T callback, string id)
        {
            if (hook != null) return;
            MethodInfo target = type.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null, parameters, null);
            if (target == null || target.ReturnType != result)
                throw new MissingMethodException(type.FullName, name);
            Hook candidate = null;
            try
            {
                candidate = new Hook(target, callback, new HookConfig { ManualApply = true, ID = "APIShared.Events." + id });
                Original = candidate.GenerateTrampoline<T>();
                candidate.Apply();
                hook = candidate;
            }
            catch
            {
                candidate?.Dispose();
                Original = null;
                throw;
            }
        }
    }
}
