using APIShared.ScriptExtenderFixes;
using BepInEx.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public unsafe class ImGuiShutdownTests
    {
        private static readonly List<ILContext> generatedContexts = new List<ILContext>();
        [TestCleanup]
        public void ReleaseGeneratedTestReferences()
        {
            foreach (var context in generatedContexts) context.Dispose();
            generatedContexts.Clear();
        }
        [TestMethod]
        public void ShutdownIsTerminalAndCannotEraseTheOriginalWindowProc()
        {
            var state = new ImGuiShutdownState();
            var original = new IntPtr(123);
            var own = new IntPtr(456);
            Assert.AreEqual(original, state.RememberWindowProc(original, own));
            Assert.AreEqual(original, state.RememberWindowProc(own, own));
            Assert.AreEqual(original, state.RememberWindowProc(IntPtr.Zero, own));
            Assert.IsTrue(state.BeginShutdown());
            Assert.IsFalse(state.BeginShutdown());
            Assert.IsTrue(state.IsShutdown);
        }

        [TestMethod]
        public void UnknownOrPartiallyMatchingBinariesAreRejected()
        {
            Assert.IsTrue(ScriptExtenderFixesRuntime.Supports(ScriptExtenderFixesRuntime.ManagedHash, ScriptExtenderFixesRuntime.NativeHash));
            Assert.IsFalse(ScriptExtenderFixesRuntime.Supports("unknown", ScriptExtenderFixesRuntime.NativeHash));
            Assert.IsFalse(ScriptExtenderFixesRuntime.Supports(ScriptExtenderFixesRuntime.ManagedHash, "unknown"));
            Assert.IsFalse(ScriptExtenderFixesRuntime.Supports(null, null));
        }

        [TestMethod]
        public void DisabledConfigurationDoesNotResolveOrPatchDependencies()
        {
            string path = Path.Combine(Path.GetTempPath(), "APIShared-Shutdown-" + Guid.NewGuid() + ".cfg");
            var config = new ConfigFile(path, false);
            config.Bind("ScriptExtenderFixes", "EnableImGuiShutdownFix", false).Value = false;
            ScriptExtenderFixesRuntime.Initialize(config, null);
            Assert.IsFalse(config.Bind("ScriptExtenderFixes", "EnableImGuiShutdownFix", true).Value);
        }

        [TestMethod]
        public void CloseDuringRenderingRetainsResourcesAndForwardsRepeatedMessages()
        {
            var state = new ImGuiShutdownState();
            int originalWindows = 0;
            int destructiveWindows = 0;
            Func<uint, bool> forward = message => {
                if (message == 16) state.BeginShutdown();
                return state.IsShutdown;
            };
            Fixture.DestructiveWindow = () => Interlocked.Increment(ref destructiveWindows);
            var wnd = Generate<Func<IntPtr, uint, IntPtr, IntPtr, IntPtr>>(nameof(Fixture.Window),
                il => ImGuiShutdownIl.GuardWindow(il, forward, (h, m, w, l) => {
                    Interlocked.Increment(ref originalWindows); return new IntPtr(77);
                }));
            using (var admitted = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Fixture.Render = () => { admitted.Set(); Assert.IsTrue(release.Wait(5000)); };
                Fixture.NativeCalls = 0;
                var present = Generate<Func<IntPtr, uint, uint, int>>(nameof(Fixture.Present),
                    il => ImGuiShutdownIl.GuardRender(il, () => state.IsShutdown, "idxgiswapchain_present_trampoline_fp", 3));
                var running = Task.Run(() => present(new IntPtr(9), 2, 3));
                Assert.IsTrue(admitted.Wait(5000));
                try {
                    Assert.AreEqual(new IntPtr(77), wnd(IntPtr.Zero, 16, IntPtr.Zero, IntPtr.Zero));
                    Assert.AreEqual(new IntPtr(77), wnd(IntPtr.Zero, 16, IntPtr.Zero, IntPtr.Zero));
                    Assert.AreEqual(new IntPtr(77), wnd(IntPtr.Zero, 256, IntPtr.Zero, IntPtr.Zero));
                    // A new Present uses the native-only path and never waits on the render callback.
                    Assert.AreEqual(14, present(new IntPtr(9), 2, 3));
                } finally { release.Set(); }
                Assert.IsTrue(running.Wait(5000));
                Assert.AreEqual(14, running.Result);
                Assert.AreEqual(2, Fixture.NativeCalls);
            }
            Assert.AreEqual(0, destructiveWindows);
            Assert.AreEqual(3, originalWindows);
        }

        [TestMethod]
        public void ShutdownResizePreservesAllNativeArgumentsAndReturnValue()
        {
            Fixture.Render = () => Assert.Fail("Shutdown must not touch cached render resources.");
            var resize = Generate<Func<IntPtr, uint, uint, uint, uint, uint, int>>(nameof(Fixture.Resize),
                il => ImGuiShutdownIl.GuardRender(il, () => true, "idxgiswapchain_resizebuffers_trampoline_fp", 6));
            Assert.AreEqual(21, resize(new IntPtr(1), 2, 3, 4, 5, 6));
        }

        [TestMethod]
        public void ProcessExitKeepsManagedNotificationButSkipsNativeTeardown()
        {
            int notifications = 0, releases = 0;
            Fixture.Global.currentContext = new Context(() => notifications++);
            Fixture.NativeCleanup = () => releases++;
            var disable = Generate<Action>(nameof(Fixture.Disable), il => ImGuiShutdownIl.GuardDisable(il, () => true));
            disable(); disable();
            Assert.AreEqual(1, notifications);
            Assert.AreEqual(0, releases);
            Assert.IsNull(Fixture.Global.currentContext);
            Fixture.Global.currentContext = new Context(() => notifications++);
            var normal = Generate<Action>(nameof(Fixture.Disable), il => ImGuiShutdownIl.GuardDisable(il, () => false));
            normal();
            Assert.AreEqual(2, notifications);
            Assert.AreEqual(1, releases);
        }

        [TestMethod]
        public void WindowSetterAndUnhookGuardsPreserveNormalBehavior()
        {
            var state = new ImGuiShutdownState();
            state.RememberWindowProc(new IntPtr(123), new IntPtr(456));
            var setter = Generate<Action<Fixture, IntPtr>>("set_Previous",
                il => ImGuiShutdownIl.GuardSetter(il, incoming => state.RememberWindowProc(incoming, new IntPtr(456))));
            var instance = new Fixture();
            setter(instance, new IntPtr(456));
            Assert.AreEqual(new IntPtr(123), instance.Previous);
            int releases = 0;
            Fixture.NativeCleanup = () => releases++;
            var unhook = Generate<Action<bool>>(nameof(Fixture.Unhook), il => ImGuiShutdownIl.GuardUnhook(il, exit => {
                if (exit) state.BeginShutdown(); return state.IsShutdown;
            }));
            unhook(false); unhook(true); unhook(false);
            Assert.AreEqual(1, releases);
            int quitCalls = 0;
            var quit = Generate<Action>(nameof(Fixture.Quit), il => ImGuiShutdownIl.Begin(il, () => quitCalls++));
            quit();
            Assert.AreEqual(1, quitCalls);
        }

        [TestMethod]
        public void InstalledManagedBodiesAcceptEveryEditBeforeInstallation()
        {
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('\uFEFF');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            ResolveEventHandler resolve = (sender, args) => {
                string path = Path.Combine(extender, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try
            {
                var assembly = Assembly.LoadFrom(Path.Combine(extender, "universal-unity-imgui-api.dll"));
                CheckBody(assembly, "UUIMGUI.Hooks.D3D11.LibD3D11", "idxgiswapchain_present_hook_impl",
                    il => ImGuiShutdownIl.GuardRender(il, () => false, "idxgiswapchain_present_trampoline_fp", 3));
                CheckBody(assembly, "UUIMGUI.Hooks.D3D11.LibD3D11", "idxgiswapchain_resizebuffers_hook_impl",
                    il => ImGuiShutdownIl.GuardRender(il, () => false, "idxgiswapchain_resizebuffers_trampoline_fp", 6));
                CheckBody(assembly, "UUIMGUI.Hooks.Windows.LibWindows", "win_wndproc_hook_impl",
                    il => ImGuiShutdownIl.GuardWindow(il, m => false, (h, m, w, l) => IntPtr.Zero));
                CheckBody(assembly, "UUIMGUI.Core.SharedGlobals", "set_HGameWindowProc", il => ImGuiShutdownIl.GuardSetter(il, p => p));
                CheckBody(assembly, "UUIMGUI.Hooks.D3D11.LibD3D11", "UnHookSafely", il => ImGuiShutdownIl.GuardUnhook(il, exit => false));
                CheckBody(assembly, "UUIMGUI.Core.InitializationManager", "Disable", il => ImGuiShutdownIl.GuardDisable(il, () => false));
                CheckBody(assembly, "UUIMGUI.Core.InitializationManager", "CurrentDomain_ProcessExit", il => ImGuiShutdownIl.Begin(il, () => { }));
                CheckBody(typeof(FatControler).Assembly, "FatControler", "ExitApp", il => ImGuiShutdownIl.Begin(il, () => { }));
            }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }

        private static void CheckBody(Assembly assembly, string type, string name, ILContext.Manipulator edit)
        {
            var method = assembly.GetType(type, true).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            Assert.IsNotNull(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                {
                    using (var metadata = Mono.Cecil.ModuleDefinition.ReadModule(method.Module.FullyQualifiedName))
                    {
                        var original = (Mono.Cecil.MethodDefinition)metadata.LookupToken(method.MetadataToken);
                        for (int index = 0; index < original.Body.Variables.Count; index++)
                            if (original.Body.Variables[index].VariableType is Mono.Cecil.FunctionPointerType)
                                definition.Definition.Body.Variables[index].VariableType =
                                    new Mono.Cecil.TypeReference("System", "MonoFNPtrFakeClass", definition.Definition.Module,
                                        definition.Definition.Module.TypeSystem.CoreLibrary);
                    }
                    ImGuiShutdownIl.NormalizeNativePointerLocals(context, method);
                }
                // Force the Cecil type that Unity Mono's SRE cannot resolve, even
                // when the test runner's CLR imports native-pointer locals differently.
                if (name.Contains("present_hook_impl") || name.Contains("resizebuffers_hook_impl"))
                    definition.Definition.Body.Variables.Add(new Mono.Cecil.Cil.VariableDefinition(
                        new Mono.Cecil.FunctionPointerType { ReturnType = definition.Definition.Module.TypeSystem.Void }));
                context.Invoke(edit);
                Assert.AreNotEqual(IntPtr.Zero,
                    ImGuiShutdownCompiler.GenerateAudited(definition, method).MethodHandle.GetFunctionPointer());
                if (name.Contains("present_hook_impl") || name.Contains("resizebuffers_hook_impl"))
                    Assert.IsFalse(definition.Definition.Body.Variables.Any(v => v.VariableType is Mono.Cecil.FunctionPointerType),
                        "Unity Mono SRE must never receive a function-pointer local type.");
            }
        }

        private static T Generate<T>(string name, ILContext.Manipulator edit) where T : Delegate
        {
            var method = typeof(Fixture).GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            using (var definition = new DynamicMethodDefinition(method))
            {
                // The reference bag must outlive execution of the generated delegate.
                var context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance };
                generatedContexts.Add(context);
                context.Invoke(edit);
                return (T)definition.Generate().CreateDelegate(typeof(T));
            }
        }

        [TestMethod]
        public void OrdinaryLocalsInMemoryLoadedAssemblyNeedNoMetadataFile()
        {
            var memoryAssembly = Assembly.Load(File.ReadAllBytes(typeof(ImGuiShutdownTests).Assembly.Location));
            Assert.AreEqual(string.Empty, memoryAssembly.Location);
            var method = memoryAssembly.GetType(typeof(Fixture).FullName, true).GetMethod("Unhook");
            Assert.IsNotNull(method);
            using (var definition = new DynamicMethodDefinition(method))
                Assert.AreNotEqual(IntPtr.Zero,
                    ImGuiShutdownCompiler.GenerateAudited(definition, method).MethodHandle.GetFunctionPointer());
        }

        public sealed class Context
        {
            private readonly Action callback;
            public Context(Action callback) { this.callback = callback; }
            public void OnDestroyed() => callback();
        }
        public sealed class Fixture
        {
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
            public delegate int PresentNative(IntPtr h, uint a, uint b);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
            public delegate int ResizeNative(IntPtr h, uint a, uint b, uint c, uint d, uint e);
            private static readonly PresentNative presentNative = (h, a, b) => { Interlocked.Increment(ref NativeCalls); return (int)h + (int)a + (int)b; };
            private static readonly ResizeNative resizeNative = (h, a, b, c, d, e) => (int)h + (int)a + (int)b + (int)c + (int)d + (int)e;
            public static delegate* unmanaged[Thiscall]<IntPtr, uint, uint, int> idxgiswapchain_present_trampoline_fp =
                (delegate* unmanaged[Thiscall]<IntPtr, uint, uint, int>)(void*)Marshal.GetFunctionPointerForDelegate(presentNative);
            public static delegate* unmanaged[Thiscall]<IntPtr, uint, uint, uint, uint, uint, int> idxgiswapchain_resizebuffers_trampoline_fp =
                (delegate* unmanaged[Thiscall]<IntPtr, uint, uint, uint, uint, uint, int>)(void*)Marshal.GetFunctionPointerForDelegate(resizeNative);
            public static Action Render, NativeCleanup, DestructiveWindow;
            public static readonly Fixture Global = new Fixture();
            public Context currentContext;
            public static int NativeCalls;
            public IntPtr Previous { get; set; }
            public static int Present(IntPtr h, uint a, uint b) { try { Render(); } catch { throw; } return idxgiswapchain_present_trampoline_fp(h, a, b); }
            public static int Resize(IntPtr h, uint a, uint b, uint c, uint d, uint e) { Render(); return idxgiswapchain_resizebuffers_trampoline_fp(h, a, b, c, d, e); }
            public static IntPtr Window(IntPtr h, uint m, IntPtr w, IntPtr l) { DestructiveWindow(); return new IntPtr(88); }
            public static void Disable() { Global.currentContext?.OnDestroyed(); Global.currentContext = null; NativeCleanup(); }
            public static void Unhook(bool exit) { NativeCleanup(); }
            public static void Quit() { }
        }
    }
}
