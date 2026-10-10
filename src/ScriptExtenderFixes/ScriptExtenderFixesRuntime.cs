using BepInEx.Configuration;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    // Optional, binary-bound fixes. No new public API or dependency on the author's mods.
    internal static class ScriptExtenderFixesRuntime
    {
        internal const string ManagedHash = "D98415528BA84F0DFF17F283F72D0003D65ECA8F30993E5BAFE1D425BA8CE9B7";
        internal const string NativeHash = "BF43F0854C7CA51C33862D092E53E39FAE8FC6528C8103E4563D000EAF507E80";
        private const uint WindowClose = 0x0010;
        private static readonly ImGuiShutdownState state = new ImGuiShutdownState();
        private static ILHook[] hooks;
        private static ImGuiShutdownCompiler compiler;
        private static ManualLogSource logger;
        private static IntPtr ownWindowProc;
        private static int active, renderMarker;

        internal static void Initialize(ConfigFile config, ManualLogSource log)
        {
            if (Volatile.Read(ref active) != 0) return;
            logger = log;
            bool enabled = config.Bind("ScriptExtenderFixes", "EnableImGuiShutdownFix", true,
                "Protect the audited bundled UU-ImGUI shutdown paths. Applies after restarting the game; unknown binaries are skipped.").Value;
            if (!enabled) { Write("disabled by startup configuration"); return; }
            var candidates = new List<ILHook>();
            ImGuiShutdownCompiler compilerCandidate = null;
            try
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a =>
                    a.GetType("UUIMGUI.Core.InitializationManager", false) != null);
                string nativePath = Path.Combine(Path.GetDirectoryName(assembly.Location), "x86_64", "cimguiaio.dll");
                string loadedNative;
                using (var process = Process.GetCurrentProcess())
                    loadedNative = process.Modules.Cast<ProcessModule>().Single(m =>
                        string.Equals(m.ModuleName, "cimguiaio.dll", StringComparison.OrdinalIgnoreCase)).FileName;
                if (IntPtr.Size != 8 || !Supports(Hash(assembly.Location), Hash(nativePath)) ||
                    !string.Equals(Hash(loadedNative), NativeHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("unsupported UU-ImGUI managed/native fingerprints");
                if (Hash(typeof(DynamicMethodDefinition).Assembly.Location) != "9D1495F147AC93C4F81F84538C1A326E8F8A6AEFC78D6289D798F3CE1162C5E9" ||
                    Hash(typeof(ILHook).Assembly.Location) != "40E49BB314391CD7BDDC2644F8553EEBA92C194B940836B103DF16955C464E0C")
                    throw new InvalidOperationException("unsupported MonoMod compiler/hook fingerprints");

                Type windows = assembly.GetType("UUIMGUI.Hooks.Windows.LibWindows", true);
                Type graphics = assembly.GetType("UUIMGUI.Hooks.D3D11.LibD3D11", true);
                Type globals = assembly.GetType("UUIMGUI.Core.SharedGlobals", true);
                Type initialization = assembly.GetType("UUIMGUI.Core.InitializationManager", true);
                object instance = globals.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                ownWindowProc = Marshal.GetFunctionPointerForDelegate((Delegate)windows.GetField("win_wndproc", BindingFlags.Public | BindingFlags.Static).GetValue(null));
                var previous = globals.GetProperty("HGameWindowProc");
                IntPtr saved = (IntPtr)previous.GetValue(instance, null);
                IntPtr hwnd = (IntPtr)globals.GetProperty("GameWindow").GetValue(instance, null);
                if (saved == IntPtr.Zero || saved == ownWindowProc) saved = GetWindowLongPtrW(hwnd, -4);
                if (state.RememberWindowProc(saved, ownWindowProc) == IntPtr.Zero)
                    throw new InvalidOperationException("no valid original WndProc; refusing unsafe forwarding");

                MethodInfo present = Method(graphics, "idxgiswapchain_present_hook_impl", true, typeof(int), typeof(IntPtr), typeof(uint), typeof(uint));
                MethodInfo resize = Method(graphics, "idxgiswapchain_resizebuffers_hook_impl", true, typeof(int), typeof(IntPtr), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(uint));
                MethodInfo window = Method(windows, "win_wndproc_hook_impl", true, typeof(IntPtr), typeof(IntPtr), typeof(uint), typeof(IntPtr), typeof(IntPtr));
                MethodInfo setter = Method(globals, "set_HGameWindowProc", false, typeof(void), typeof(IntPtr));
                MethodInfo unhook = Method(graphics, "UnHookSafely", true, typeof(void), typeof(bool));
                MethodInfo disable = Method(initialization, "Disable", true, typeof(void));
                MethodInfo processExit = Method(initialization, "CurrentDomain_ProcessExit", true, typeof(void), typeof(object), typeof(EventArgs));
                // Resolve only the real public game member; no publicized compilation reference.
                MethodInfo exitApp = typeof(FatControler).GetMethod("ExitApp", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (exitApp == null || exitApp.ReturnType != typeof(void)) throw new MissingMethodException("FatControler.ExitApp");
                using (var definition = new DynamicMethodDefinition(exitApp))
                {
                    var calls = definition.Definition.Body.Instructions.Where(i =>
                        i.Operand is Mono.Cecil.MethodReference).Select(i => (Mono.Cecil.MethodReference)i.Operand).ToArray();
                    if (calls.Length != 1 || calls[0].DeclaringType.FullName != "UnityEngine.Application" ||
                        calls[0].Name != "Quit" || calls[0].Parameters.Count != 0)
                        throw new InvalidOperationException("menu ExitApp no longer has the audited unconditional Quit contract");
                }

                // Dry-run every manipulator before the first hook is applied.
                var rawEdits = new List<Tuple<MethodInfo, MonoMod.Cil.ILContext.Manipulator>> {
                    Tuple.Create(present, (MonoMod.Cil.ILContext.Manipulator)(il => {
                        ImGuiShutdownIl.GuardRender(il, BypassPresent, "idxgiswapchain_present_trampoline_fp", 3);
                    })),
                    Tuple.Create(resize, (MonoMod.Cil.ILContext.Manipulator)(il => {
                        ImGuiShutdownIl.GuardRender(il, Retain, "idxgiswapchain_resizebuffers_trampoline_fp", 6);
                    })),
                    Tuple.Create(window, (MonoMod.Cil.ILContext.Manipulator)(il => ImGuiShutdownIl.GuardWindow(il, ForwardWindow, DispatchWindow))),
                    Tuple.Create(setter, (MonoMod.Cil.ILContext.Manipulator)(il => ImGuiShutdownIl.GuardSetter(il, RememberWindow))),
                    Tuple.Create(unhook, (MonoMod.Cil.ILContext.Manipulator)(il => ImGuiShutdownIl.GuardUnhook(il, BypassUnhook))),
                    Tuple.Create(disable, (MonoMod.Cil.ILContext.Manipulator)(il => ImGuiShutdownIl.GuardDisable(il, Retain))),
                    Tuple.Create(processExit, (MonoMod.Cil.ILContext.Manipulator)(il => ImGuiShutdownIl.Begin(il, () => BeginShutdown("ProcessExit")))),
                    Tuple.Create(exitApp, (MonoMod.Cil.ILContext.Manipulator)(il => ImGuiShutdownIl.Begin(il, () => BeginShutdown("menu ExitApp"))))
                };
                var edits = rawEdits.Select(edit => Tuple.Create(edit.Item1,
                    (MonoMod.Cil.ILContext.Manipulator)(il => {
                        ImGuiShutdownIl.NormalizeNativePointerLocals(il, edit.Item1);
                        edit.Item2(il);
                    }))).ToList();
                foreach (var edit in edits)
                {
                    using (var definition = new DynamicMethodDefinition(edit.Item1))
                    using (var context = new MonoMod.Cil.ILContext(definition.Definition))
                    {
                        context.ReferenceBag = MonoMod.Cil.RuntimeILReferenceBag.Instance;
                        context.Invoke(edit.Item2);
                        // Actually generate the edited body, including calli and exception regions.
                        ImGuiShutdownCompiler.GenerateAudited(definition, edit.Item1).MethodHandle.GetFunctionPointer();
                    }
                    // Detour also compiles an unedited original backup. Validate it too.
                    using (var backup = new DynamicMethodDefinition(edit.Item1))
                        ImGuiShutdownCompiler.GenerateAudited(backup, edit.Item1).MethodHandle.GetFunctionPointer();
                }
                compilerCandidate = new ImGuiShutdownCompiler(edits.Select(edit => edit.Item1));
                compilerCandidate.Hook.Apply();
                foreach (var edit in edits)
                    candidates.Add(new ILHook(edit.Item1, edit.Item2,
                        new ILHookConfig { ManualApply = true, ID = "APIShared.ScriptExtenderFixes.ImGuiShutdown." + edit.Item1.Name }));
                foreach (var candidate in candidates) candidate.Apply();
                hooks = candidates.ToArray();
                compiler = compilerCandidate;
                UnityEngine.Application.quitting += OnQuitting;
                Volatile.Write(ref active, 1);
                Write("installed; UU-ImGUI 1.6.7 and MonoMod fingerprints, eight managed dispatch contracts and original backups validated; scoped MethodBuilder compiler; resources retained until process exit");
            }
            catch (Exception ex)
            {
                // Only an unpublished candidate can reach this rollback. Active hooks stay rooted.
                if (Volatile.Read(ref active) == 0) RollbackUnpublished(candidates, compilerCandidate);
                Write("skipped: " + ex);
            }
        }

        internal static bool Supports(string managed, string native) =>
            string.Equals(managed, ManagedHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(native, NativeHash, StringComparison.OrdinalIgnoreCase);

        private static MethodInfo Method(Type type, string name, bool isStatic, Type result, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, parameters, null);
            if (method == null || method.ReturnType != result || method.GetMethodBody() == null)
                throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static bool Retain() => Volatile.Read(ref active) != 0 && state.IsShutdown;
        private static bool BypassPresent()
        {
            if (Volatile.Read(ref active) != 0 && Volatile.Read(ref renderMarker) == 0 &&
                Interlocked.Exchange(ref renderMarker, 1) == 0)
                Write("persistent Present callback confirmed after startup; no plugin component callback required");
            return Retain();
        }
        private static void OnQuitting() => BeginShutdown("Application.quitting");
        private static void BeginShutdown(string source)
        {
            if (Volatile.Read(ref active) != 0 && state.BeginShutdown())
                Write("shutdown latched: " + source + "; native teardown suppressed, new render callbacks use original trampolines");
        }
        private static bool ForwardWindow(uint message)
        {
            if (Volatile.Read(ref active) == 0) return false;
            if (message == WindowClose) BeginShutdown("WM_CLOSE");
            return state.IsShutdown;
        }
        private static IntPtr DispatchWindow(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
            CallWindowProcW(state.PreviousWindowProc, hwnd, message, wParam, lParam);
        private static IntPtr RememberWindow(IntPtr incoming) => Volatile.Read(ref active) == 0 ? incoming :
            state.RememberWindowProc(incoming, ownWindowProc);
        private static bool BypassUnhook(bool exit)
        {
            if (exit) BeginShutdown("UU-ImGUI UnHookSafely(exit)");
            return Retain();
        }
        private static void RollbackUnpublished(List<ILHook> candidates, ImGuiShutdownCompiler compilerCandidate)
        {
            for (int index = candidates.Count - 1; index >= 0; index--)
                candidates[index].Dispose();
            compilerCandidate?.Hook.Dispose();
        }
        private static void Write(string message)
        {
            try { logger?.LogInfo("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] SE_IMGUI_SHUTDOWN_FIX: " + message); }
            catch { /* Logging must not interrupt native message forwarding or quit. */ }
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProcW(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
