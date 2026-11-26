using System;
using System.Runtime.InteropServices;
using APIShared.Multiplayer;
using APIShared.NativeSites;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class MultiplayerResyncNativeMachineTests
    {
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);

        [TestMethod]
        public void ReceiveDetourPreservesTheIssuerTickAndBothStackArguments()
        {
            var options = new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false,
                SuspendThreadsDuringPatch = false };
            IntPtr target = VirtualAlloc(IntPtr.Zero, (UIntPtr)(options.FunctionScanLimit + 64), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, target);
            NativeDetour<MultiplayerResyncDataNativeRuntime.ReceiveCall> hook = null;
            int intercepted = 0;
            (long, byte, int, int, int, IntPtr) captured = default;
            MultiplayerResyncDataNativeRuntime.ReceiveCall original = (manager, opcode, sender, tick, id, body) =>
                captured = (manager, opcode, sender, tick, id, body);
            MultiplayerResyncDataNativeRuntime.ReceiveCall callback = (manager, opcode, sender, tick, id, body) =>
            {
                intercepted++;
                hook.Original(manager, opcode, sender, tick, id, body);
            };
            try
            {
                byte[] entry = MultiplayerResyncDataNativeRuntime.ReceiveEntry;
                Marshal.Copy(entry, 0, target, entry.Length);
                // Restore R15, R14 and RDI pushed by the real prologue, then tail-call the
                // recording fixture using the intact Win64 stack and register arguments.
                byte[] pops = { 0x41, 0x5F, 0x41, 0x5E, 0x5F };
                Marshal.Copy(pops, 0, target + entry.Length, pops.Length);
                byte[] jump = CodePatch.BuildAbsoluteJump(Address(Marshal.GetFunctionPointerForDelegate(original)));
                Marshal.Copy(jump, 0, target + entry.Length + pops.Length, jump.Length);
                var request = new DetourRequest<MultiplayerResyncDataNativeRuntime.ReceiveCall>
                {
                    Name = "private resync receive fixture", TargetAddress = Address(target), Callback = callback
                };
                hook = (NativeDetour<MultiplayerResyncDataNativeRuntime.ReceiveCall>)new NativeDetourBackend(options).CreateDetour(in request);
                NativeIndirectEntry.Validate(hook, target, entry, MultiplayerResyncDataNativeRuntime.ReceiveDisplaced, false);
                hook.Enable();
                NativeIndirectEntry.Validate(hook, target, entry, MultiplayerResyncDataNativeRuntime.ReceiveDisplaced, true);
                var expected = (0x12345678_98765432L, (byte)67, 3, 469, -17, target + 40);
                Marshal.GetDelegateForFunctionPointer<MultiplayerResyncDataNativeRuntime.ReceiveCall>(target)
                    (expected.Item1, expected.Item2, expected.Item3, expected.Item4, expected.Item5, expected.Item6);
                Assert.AreEqual(1, intercepted);
                Assert.AreEqual(expected, captured);
            }
            finally
            {
                hook?.Dispose();
                GC.KeepAlive(callback);
                GC.KeepAlive(original);
                Assert.IsTrue(VirtualFree(target, UIntPtr.Zero, 0x8000));
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int HostFixture(long manager, byte opcode);

        [TestMethod]
        public void HostGateCallKeepsTheArgumentsAndReturnsToTheNextInstruction()
        {
            IntPtr caller = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, caller);
            (long, byte) captured = default;
            MultiplayerResyncDataNativeRuntime.QueueCall callback = (manager, opcode) => captured = (manager, opcode);
            try
            {
                byte[] prefix = { 0x48, 0x83, 0xEC, 0x28 }; // Win64 alignment and shadow space.
                IntPtr call = caller + prefix.Length, relay = caller + 64;
                byte[] encoded = MultiplayerResyncDataNativeRuntime.EncodeCall(Address(call), Address(relay));
                byte[] continuation = { 0x48, 0x83, 0xC4, 0x28, 0xB8, 0x42, 0, 0, 0, 0xC3 };
                byte[] jump = CodePatch.BuildAbsoluteJump(Address(Marshal.GetFunctionPointerForDelegate(callback)));
                Marshal.Copy(prefix, 0, caller, prefix.Length);
                Marshal.Copy(encoded, 0, call, encoded.Length);
                Marshal.Copy(continuation, 0, call + encoded.Length, continuation.Length);
                Marshal.Copy(jump, 0, relay, jump.Length);
                var invoke = Marshal.GetDelegateForFunctionPointer<HostFixture>(caller);
                Assert.AreEqual(0x42, invoke(0x12345678_98765432L, 67));
                Assert.AreEqual((0x12345678_98765432L, (byte)67), captured);
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    MultiplayerResyncDataNativeRuntime.EncodeCall(0x10000, 0x100000000));
            }
            finally
            {
                GC.KeepAlive(callback);
                Assert.IsTrue(VirtualFree(caller, UIntPtr.Zero, 0x8000));
            }
        }

        private static ulong Address(IntPtr value) => unchecked((ulong)value.ToInt64());
    }
}
