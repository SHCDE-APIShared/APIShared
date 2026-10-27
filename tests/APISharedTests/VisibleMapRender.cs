using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass]
    public sealed class VisibleMapRenderTests
    {
        // Published native hooks and their callbacks must remain rooted even in a fixture.
        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        private static readonly List<object> roots = new List<object>();

        [TestMethod]
        public void RenderRestoresTemporaryStateAndIsolatesBothCallbackPhases()
        {
            var trace = new List<string>();
            int flag = 7, originals = 0;
            var first = new VisibleMapRenderRegistration("a", "first", 0,
                e => { e.State = flag; flag = 1; trace.Add("a+"); throw new Exception("prepare"); },
                e => { flag = (int)e.State; trace.Add("a-"); });
            var second = new VisibleMapRenderRegistration("b", "second", 1,
                e => { Assert.AreEqual(null, e.State); trace.Add("b+"); first.Enabled = false; },
                e => { trace.Add("b-"); throw new Exception("cleanup"); });
            first.Enabled = second.Enabled = true;
            VisibleMapRenderEvents.Dispatch(new[] { first, second }, new IntPtr(123), -2, 3, -4, 5, 6,
                (map,l,r,t,b,z) => { originals++; Assert.AreEqual(1, flag); Assert.AreEqual(new IntPtr(123), map);
                    CollectionAssert.AreEqual(new[] { -2,3,-4,5,6 }, new[] { l,r,t,b,z }); trace.Add("original"); });
            Assert.AreEqual(7, flag); Assert.AreEqual(1, originals);
            CollectionAssert.AreEqual(new[] { "a+", "b+", "original", "b-", "a-" }, trace);
            first.Enabled = true;
            Assert.Throws<InvalidOperationException>(() => VisibleMapRenderEvents.Dispatch(new[] { first },
                IntPtr.Zero, 0,0,0,0,0, (m,l,r,t,b,z) => { throw new InvalidOperationException("original"); }));
            Assert.AreEqual(7, flag);
        }

        [TestMethod]
        public void ExecutedRenderBridgePreservesSixArgumentsAndDormantBypass()
        {
            int callbacks = 0, originals = 0;
            VisibleMapRenderEvents.RenderCall observer = (m,l,r,t,b,z) => {
                Assert.AreEqual(new IntPtr(123), m);
                CollectionAssert.AreEqual(new[] { -17,300,-9,700,2 }, new[] { l,r,t,b,z }); originals++;
            };
            var original = ExecutableFunction.Create(a => {
                a.db(VisibleMapRenderEvents.Entry);
                a.mov(rax, unchecked((ulong)Marshal.GetFunctionPointerForDelegate(observer).ToInt64())); a.jmp(rax);
            });
            // Model a process-lived module, not a finalizable executable buffer underneath a published hook.
            IntPtr target = VirtualAlloc(IntPtr.Zero, new UIntPtr(4096), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, target);
            var bytes = new byte[22]; Marshal.Copy(original.EntryPoint, bytes, 0, bytes.Length); Marshal.Copy(bytes, 0, target, bytes.Length);
            IntPtr demand = Marshal.AllocHGlobal(1); Marshal.WriteByte(demand, 0);
            NativeDetour<VisibleMapRenderEvents.RenderCall> hook = null;
            VisibleMapRenderEvents.RenderCall callback = (m,l,r,t,b,z) => { callbacks++; hook.Original(m,l,r,t,b,z); };
            var bridge = new VisibleMapRenderBridge(callback, demand);
            var request = new DetourRequest<VisibleMapRenderEvents.RenderCall> {
                Name = "private visible map fixture", TargetAddress = unchecked((ulong)target.ToInt64()), Callback = callback, IntermediaryFactory = bridge };
            hook = (NativeDetour<VisibleMapRenderEvents.RenderCall>)new NativeDetourBackend(
                new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false, FunctionScanLimit = 22, SuspendThreadsDuringPatch = false }).CreateDetour(in request);
            roots.AddRange(new object[] { original, observer, callback, hook, bridge, demand });
            Assert.AreEqual(10, hook.DisplacedByteCount);
            hook.Enable(); bridge.Validate(hook);
            APIShared.NativeSites.NativeIndirectEntry.Validate(hook, target, VisibleMapRenderEvents.Entry);
            var invoke = Marshal.GetDelegateForFunctionPointer<VisibleMapRenderEvents.RenderCall>(target); roots.Add(invoke);
            int expected = 0;
            foreach (byte active in new byte[] { 0,1,1,0,255,0 }) {
                Marshal.WriteByte(demand, active); invoke(new IntPtr(123), -17,300,-9,700,2);
                if (active != 0) expected++;
                Assert.AreEqual(expected, callbacks);
            }
            Assert.AreEqual(6, originals);
        }
    }
}
