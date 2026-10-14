using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Iced.Intel;
using MonoMod.RuntimeDetour;

namespace APIShared.Presentation
{
    // Install gate for managed paint sites. MonoMod hooks on one method compose (Script Extender already
    // hooks SetBodySprite and setTileColour), so a MonoMod chain is never "foreign". What the broker must not
    // do is install over an entry that something outside MonoMod redirected: MonoMod would overwrite that
    // jump. The gate also refuses a changed member contract.
    internal static class PaintSiteGuard
    {
        // Seam for the native-site registry (FW-09). When a registry can name an exclusive owner of a managed
        // site, it returns that refusal here; null means no objection. Unset until that registry ships.
        internal static Func<MethodBase, string> OwnershipVeto;

        // Production reader; tests replace it to present synthetic entries.
        internal static Func<MethodBase, ManagedEntry> ReadEntry = ManagedEntry.Read;

        // Returns null when the broker may hook target; otherwise a refusal for the registering consumer.
        // declaringType/name/isStatic are checked only for production sites; signature includes the receiver
        // of an instance method, so a static test target with the same parameters is accepted.
        internal static string Check(MethodInfo target, Type declaringType, string name, bool isStatic,
            Type returnType, Type[] signature, out string description)
        {
            description = null;
            if (target == null) return "target method not found (game update?)";
            if (declaringType != null && (target.DeclaringType != declaringType || target.Name != name || target.IsStatic != isStatic))
                return "target is " + target.DeclaringType?.FullName + "." + target.Name + ", expected " + declaringType.FullName + "." + name;
            if (target.ReturnType != returnType) return "return type changed to " + target.ReturnType.FullName;
            ParameterInfo[] parameters = target.GetParameters();
            int receiver = target.IsStatic ? 0 : 1;
            if (parameters.Length + receiver != signature.Length) return "parameter count changed";
            if (receiver == 1 && !signature[0].IsAssignableFrom(target.DeclaringType)) return "receiver type changed";
            for (int index = 0; index < parameters.Length; index++)
                if (parameters[index].ParameterType != signature[index + receiver])
                    return "parameter " + parameters[index].Name + " changed to " + parameters[index].ParameterType.FullName;
            string veto = OwnershipVeto?.Invoke(target);
            if (veto != null) return veto;
            ManagedEntry entry = ReadEntry(target);
            description = entry.Describe();
            return entry.Refusal();
        }
    }

    // What sits at a managed method's compiled entry before the broker hooks it.
    internal readonly struct ManagedEntry
    {
        internal ManagedEntry(int monoModDetours, bool monoRuntime, byte[] head, string readError = null)
        { MonoModDetours = monoModDetours; MonoRuntime = monoRuntime; Head = head; ReadError = readError; }
        // Applied MonoMod detours on the method (Hooks, and ILHooks including HarmonyX patches, each apply one);
        // -1 when MonoMod's registry could not be read.
        internal int MonoModDetours { get; }
        internal bool MonoRuntime { get; }
        internal byte[] Head { get; }
        internal string ReadError { get; }

        internal bool EntryJumps => Head != null && StartsWithJump(Head);

        // Decision table (tests cover each row):
        // - MonoMod has applied detours: its chain owns the entry; the new hook composes with them.
        // - Mono (the game) without MonoMod detours: a fresh JIT entry never starts with a jump, so one means
        //   another patcher redirected it. Refuse. An unreadable entry is refused too.
        // - .NET Framework/CoreCLR without MonoMod detours: the stable entry can be a precode jump, which
        //   says nothing about patching, so it is not inspected (test hosts only).
        internal string Refusal()
        {
            if (MonoModDetours > 0) return null;
            if (!MonoRuntime) return null;
            if (Head == null) return "cannot read the compiled entry: " + (ReadError ?? "unknown error");
            if (!EntryJumps) return null;
            return MonoModDetours < 0
                ? "the compiled entry is redirected and MonoMod's hook registry is unreadable; refusing to install over a possible foreign detour"
                : "the compiled entry is redirected outside MonoMod (foreign detour); refusing to install over it";
        }

        internal string Describe() =>
            "monoModDetours=" + (MonoModDetours < 0 ? "unknown" : MonoModDetours.ToString()) +
            ",runtime=" + (MonoRuntime ? "Mono" : "CLR") +
            ",entry=" + (Head == null ? "unreadable" : EntryJumps ? "jump" : "code");

        // x64 forms a detour writes at an entry: jmp rel8/rel32, jmp [mem], mov reg,imm64 + jmp reg,
        // push imm32 + ret.
        internal static bool StartsWithJump(byte[] head)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(head));
            decoder.IP = 0x10000;
            decoder.Decode(out Instruction first);
            if (first.IsInvalid) return false;
            if (first.Mnemonic == Mnemonic.Jmp) return true;
            if (decoder.IP - 0x10000 >= (ulong)head.Length) return false;
            decoder.Decode(out Instruction second);
            if (second.IsInvalid) return false;
            if (first.Mnemonic == Mnemonic.Mov && first.Op0Kind == OpKind.Register && first.Op1Kind == OpKind.Immediate64 &&
                second.Mnemonic == Mnemonic.Jmp && second.Op0Kind == OpKind.Register && second.Op0Register == first.Op0Register)
                return true;
            return first.Mnemonic == Mnemonic.Push && (first.Op0Kind == OpKind.Immediate32to64 || first.Op0Kind == OpKind.Immediate8to64) &&
                second.Mnemonic == Mnemonic.Ret;
        }

        private static FieldInfo detourMap;
        private static bool detourMapResolved;

        internal static ManagedEntry Read(MethodBase method)
        {
            bool mono = Type.GetType("Mono.Runtime") != null;
            int detours = CountMonoModDetours(method);
            try
            {
                IntPtr start = method.GetNativeStart();
                var head = new byte[16];
                Marshal.Copy(start, head, 0, head.Length);
                return new ManagedEntry(detours, mono, head);
            }
            catch (Exception ex) { return new ManagedEntry(detours, mono, null, ex.GetType().Name + ": " + ex.Message); }
        }

        // MonoMod.RuntimeDetour 22 (BepInEx 5.4 core) keeps every Detour, including the one each ILHook applies,
        // in the private static Detour._DetourMap, keyed by the pinned identifiable method and guarded by
        // locking the dictionary. Read once per install; -1 if the shape differs.
        internal static int CountMonoModDetours(MethodBase method)
        {
            try
            {
                if (!detourMapResolved)
                {
                    FieldInfo field = typeof(Detour).GetField("_DetourMap", BindingFlags.NonPublic | BindingFlags.Static);
                    detourMap = field != null && typeof(IDictionary).IsAssignableFrom(field.FieldType) ? field : null;
                    detourMapResolved = true;
                }
                if (detourMap == null) return -1;
                var map = (IDictionary)detourMap.GetValue(null);
                MethodBase identifiable = DetourHelper.Runtime.GetIdentifiable(method);
                lock (map)
                {
                    IList detours = (identifiable != null && map.Contains(identifiable) ? map[identifiable] : null) as IList
                        ?? (map.Contains(method) ? map[method] : null) as IList;
                    int count = 0;
                    if (detours != null)
                        foreach (object item in detours)
                            if (item is Detour detour && detour.IsValid && detour.IsApplied) count++;
                    return count;
                }
            }
            catch { return -1; }
        }
    }
}
