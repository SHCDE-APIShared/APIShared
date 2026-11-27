using System;
using System.Collections;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace APIShared.Compatibility
{
    // Read-only lookup in the audited MonoMod ILHook backend. A generated method
    // name cannot prove that a frame belongs to the original loader operation.
    internal static class StartupMethodIdentity
    {
        internal static MethodBase ActiveBody(MethodBase original)
        {
            var map = typeof(ILHook).GetField("_Map", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
            if (map == null) throw new InvalidOperationException("MonoMod ILHook map contract unavailable");
            object context;
            lock (map) context = map[original];
            if (context == null) return null;
            var type = context.GetType();
            var source = type.GetField("Method", BindingFlags.Instance | BindingFlags.Public)?.GetValue(context) as MethodBase;
            var detour = type.GetField("Detour", BindingFlags.Instance | BindingFlags.Public)?.GetValue(context) as Detour;
            if (!Equals(source, original) || detour == null || !detour.IsValid || !detour.IsApplied ||
                !Equals(detour.Method, original))
                throw new InvalidOperationException("MonoMod loader body is not an active detour of the expected method");
            return detour.Target;
        }

        internal static bool Matches(MethodBase frame, MethodBase original, MethodBase activeBody)
        {
            if (Equals(frame, original)) return true;
            // GetIdentifiable resolves a stack-frame method through MonoMod's
            // pinned runtime handle registry, including Mono DynamicMethods.
            return frame != null && activeBody != null &&
                Equals(frame.GetIdentifiable(), activeBody.GetIdentifiable());
        }
    }
}
