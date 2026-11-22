using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Multiplayer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class MultiplayerResyncTests
    {
        [TestMethod]
        public void OnlyExecutedEntryPhaseStartsAndCompletesAndCompletionCanRearmConsumers()
        {
            var events = new List<string>();
            var registry = new MultiplayerResyncRegistry(e => Assert.Fail(e.ToString()));
            Assert.IsTrue(registry.TryRegister("z", "later", () => events.Add("z-start"), () => {
                Assert.IsFalse(registry.IsInProgress); events.Add("z-end");
            }, out _, 1));
            Assert.IsTrue(registry.TryRegister("a", "first", () => {
                Assert.IsTrue(registry.IsInProgress); events.Add("a-start");
            }, () => events.Add("a-end"), out _, 0));
            registry.BeginWorld(10);
            registry.Start(1); registry.Start(2); registry.Complete(0);
            Assert.AreEqual(0, events.Count);
            registry.Start(0); registry.Start(0);
            Assert.IsTrue(registry.IsInProgress);
            registry.Complete(1); // Original67 PACK changes its global phase to0: ENTRY phase still forbids completion.
            registry.Complete(2);
            Assert.IsTrue(registry.IsInProgress);
            registry.Complete(0); registry.Complete(0);
            CollectionAssert.AreEqual(new[] { "a-start", "z-start", "a-end", "z-end" }, events);
            Assert.IsFalse(registry.IsInProgress);
            Assert.IsFalse(registry.TryRegister("late", "x", () => { }, () => { }, out _, 0));
        }

        [TestMethod]
        public void CriticalFailureFencesUntilWorldRetirementAndNeverManufacturesAnAbortCompletion()
        {
            int faults = 0, completed = 0; bool fail = true;
            var registry = new MultiplayerResyncRegistry(e => faults++);
            Assert.IsTrue(registry.TryRegister("a", "critical", () => { }, () => {
                completed++; if (fail) throw new InvalidOperationException("critical completion");
            }, out _, 0));
            registry.BeginWorld(1); registry.Start(0); registry.Complete(0);
            Assert.IsTrue(registry.IsInProgress); Assert.AreEqual(1, faults);
            registry.Complete(0); registry.ValidateNativeState(0);
            Assert.AreEqual(1, completed); Assert.AreEqual(1, faults);
            registry.EndWorld(2); Assert.IsTrue(registry.IsInProgress, "An old mission cannot retire this world.");
            registry.EndWorld(1); Assert.IsFalse(registry.IsInProgress);
            fail = false; registry.BeginWorld(3); registry.Start(0); registry.Complete(0);
            Assert.IsFalse(registry.IsInProgress); Assert.AreEqual(2, completed);
        }

        [TestMethod]
        public void LocalNativeEscapeIsTerminalRatherThanAOneSidedResume()
        {
            int failures = 0, ends = 0;
            var registry = new MultiplayerResyncRegistry(e => failures++);
            Assert.IsTrue(registry.TryRegister("owner", "id", () => { }, () => ends++, out _, 0));
            registry.BeginWorld(1); registry.Start(0);
            Assert.IsTrue(registry.ValidateNativeState(10));
            Assert.IsFalse(registry.ValidateNativeState(0));
            registry.Complete(0);
            Assert.IsTrue(registry.IsInProgress); Assert.AreEqual(0, ends); Assert.AreEqual(1, failures);
            registry.EndWorld(1); Assert.IsFalse(registry.IsInProgress); Assert.AreEqual(0, ends);
        }

        [TestMethod]
        public void BothPeersForgetOnlyTheirLedgerAtTheCommonBoundaries()
        {
            int hostLedger = 4, clientLedger = 3, hostPending = 4, clientPending = 4;
            var host = new MultiplayerResyncRegistry(e => Assert.Fail(e.Message));
            var client = new MultiplayerResyncRegistry(e => Assert.Fail(e.Message));
            Assert.IsTrue(host.TryRegister("fix", "ledger", () => hostLedger = 0, () => hostLedger = 0, out _, 0));
            Assert.IsTrue(client.TryRegister("fix", "ledger", () => clientLedger = 0, () => clientLedger = 0, out _, 0));
            host.BeginWorld(1); client.BeginWorld(99); // Process-local mission IDs need not match.
            host.Start(0); client.Start(0);
            Assert.AreEqual(hostLedger, clientLedger); Assert.AreEqual(4, hostPending); Assert.AreEqual(4, clientPending);
            hostLedger = 7; clientLedger = 2; // Repair-time scratch must never leak into the resumed ledger.
            host.Complete(0); client.Complete(0);
            Assert.AreEqual(0, hostLedger); Assert.AreEqual(0, clientLedger);
            Assert.AreEqual(hostPending, clientPending);
        }

        [TestMethod]
        public void InvalidRegistrationAndDormantRepairDoNotAllocateAHistory()
        {
            var registry = new MultiplayerResyncRegistry(e => { });
            Assert.IsFalse(registry.TryRegister("", "id", () => { }, () => { }, out _, 0));
            Assert.IsFalse(registry.TryRegister("a", "id", null, () => { }, out _, 0));
            registry.Start(0); Assert.IsFalse(registry.IsInProgress);
            Assert.IsTrue(registry.TryRegister("a", "id", () => { }, () => { }, out _, 0));
            Assert.IsFalse(registry.TryRegister("a", "id", () => { }, () => { }, out _, 0));
        }

        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);
        [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);

        [TestMethod]
        public void ActualBackendPreservesTheRipRelativePhaseReadAndBothEntryContinuations()
        {
            for (int kind = 0; kind < 2; kind++)
            {
                var options = new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false,
                    SuspendThreadsDuringPatch = false };
                int window = options.FunctionScanLimit + 16;
                int pages = (window + 512 + 4095) / 4096;
                IntPtr allocation = VirtualAlloc(IntPtr.Zero, (UIntPtr)((pages + 1) * 4096), 0x3000, 0x40);
                Assert.AreNotEqual(IntPtr.Zero, allocation);
                IntPtr guard = allocation + pages * 4096, target = guard - window;
                NativeDetour<MultiplayerResyncNativeRuntime.ChoreCall> hook = null;
                int calls = 0;
                MultiplayerResyncNativeRuntime.ChoreCall callback = () => { calls++; hook.Original(); };
                try
                {
                    Assert.IsTrue(VirtualProtect(guard, (UIntPtr)4096, 1, out _));
                    byte[] entry = (byte[])(kind == 0 ? MultiplayerResyncNativeRuntime.StartEntry : MultiplayerResyncNativeRuntime.CompleteEntry).Clone();
                    if (kind == 0)
                    {
                        // Relocate this PRIVATE function's RIP operand to a known readable cell.
                        Buffer.BlockCopy(BitConverter.GetBytes(-32 - entry.Length), 0, entry, 6, 4);
                        Marshal.WriteInt32(target - 32, 0x12345678);
                        byte[] body = { 0x89, 0x05, 0, 0, 0, 0, 0x48, 0x83, 0xC4, 0x28, 0xC3 };
                        Buffer.BlockCopy(BitConverter.GetBytes(-28 - entry.Length - 6), 0, body, 2, 4);
                        Marshal.Copy(body, 0, target + entry.Length, body.Length);
                    }
                    else
                    {
                        byte[] body = { 0xC7, 0x05, 0, 0, 0, 0, 0x77, 0, 0, 0, 0x5D, 0xC3 };
                        Buffer.BlockCopy(BitConverter.GetBytes(-28 - entry.Length - 10), 0, body, 2, 4);
                        Marshal.Copy(body, 0, target + entry.Length, body.Length);
                    }
                    Marshal.Copy(entry, 0, target, entry.Length);
                    var request = new DetourRequest<MultiplayerResyncNativeRuntime.ChoreCall> {
                        Name = "private resync boundary fixture", TargetAddress = unchecked((ulong)target.ToInt64()), Callback = callback
                    };
                    hook = (NativeDetour<MultiplayerResyncNativeRuntime.ChoreCall>)new NativeDetourBackend(options).CreateDetour(in request);
                    MultiplayerResyncNativeRuntime.ValidateEntry(hook, target, entry, false);
                    hook.Enable(); MultiplayerResyncNativeRuntime.ValidateEntry(hook, target, entry, true);
                    Marshal.GetDelegateForFunctionPointer<MultiplayerResyncNativeRuntime.ChoreCall>(target)();
                    Assert.AreEqual(1, calls); Assert.AreEqual(kind == 0 ? 0x12345678 : 0x77, Marshal.ReadInt32(target - 28));
                    Marshal.WriteByte(target, 0x90);
                    Assert.ThrowsExactly<InvalidOperationException>(() => MultiplayerResyncNativeRuntime.ValidateEntry(hook, target, entry, true));
                    Marshal.WriteByte(target, 0xFF);
                }
                finally { hook?.Dispose(); GC.KeepAlive(callback); Assert.IsTrue(VirtualFree(allocation, UIntPtr.Zero, 0x8000)); }
            }
        }
    }
}
