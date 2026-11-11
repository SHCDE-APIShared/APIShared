using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using CallSite = Mono.Cecil.CallSite;

namespace APIShared.ScriptExtenderFixes
{
    // Feature check of the installed MonoMod instead of file fingerprints. Before any UU-ImGUI
    // method is hooked, a private probe must show that the installed MonoMod generates
    // both the edited ILHook body and Detour's original backup through the routed
    // DynamicMethodDefinition.Generate(object), and that the resulting thiscall calli
    // delivers every native argument and the return value unchanged.
    internal static class ImGuiShutdownProbe
    {
        private const int ProbeResult = 30507;
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int Native(IntPtr self, uint first, uint second);
        // Rooted for the process lifetime: the published probe body embeds this pointer.
        private static readonly Native native = (self, first, second) => (int)self.ToInt64() * 10000 + (int)first * 100 + (int)second;
        private static readonly long nativePointer = Marshal.GetFunctionPointerForDelegate(native).ToInt64();

        internal static readonly MethodInfo Target = typeof(ImGuiShutdownProbe).GetMethod(nameof(Probe),
            BindingFlags.NonPublic | BindingFlags.Static);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Probe(IntPtr self, uint first, uint second) => -1;

        // The probe hook is added to candidates before Apply, so a failure rolls it back
        // together with the other unpublished candidates. On success it stays applied.
        internal static void Verify(ImGuiShutdownCompiler compiler, MethodInfo target, List<ILHook> candidates)
        {
            int before = compiler.RoutedCount(target);
            var hook = new ILHook(target, EmitNativeCall,
                new ILHookConfig { ManualApply = true, ID = "APIShared.ScriptExtenderFixes.ImGuiShutdown.Probe" });
            candidates.Add(hook);
            hook.Apply();
            if (compiler.RoutedCount(target) - before < 2)
                throw new InvalidOperationException("MonoMod no longer generates hooked bodies and original backups through the routed compiler");
            object result = target.Invoke(null, new object[] { new IntPtr(3), 5u, 7u });
            if (!(result is int value) || value != ProbeResult)
                throw new InvalidOperationException("routed thiscall calli did not preserve native arguments/result: " + result);
        }

        private static void EmitNativeCall(ILContext il)
        {
            var types = il.Module.TypeSystem;
            var site = new CallSite(types.Int32) { CallingConvention = MethodCallingConvention.ThisCall };
            site.Parameters.Add(new ParameterDefinition(types.IntPtr));
            site.Parameters.Add(new ParameterDefinition(types.UInt32));
            site.Parameters.Add(new ParameterDefinition(types.UInt32));
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Ldarg_2);
            // A constant avoids member access from MonoMod's generated assembly.
            cursor.Emit(OpCodes.Ldc_I8, nativePointer);
            cursor.Emit(OpCodes.Conv_I);
            cursor.Emit(OpCodes.Calli, site);
            cursor.Emit(OpCodes.Ret);
        }
    }
}
