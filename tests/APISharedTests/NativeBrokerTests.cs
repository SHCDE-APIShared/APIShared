using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared;
using APIShared.NativeBrokers;
using APIShared.NativeSites;
using BepInEx.Logging;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // Production broker cores (real bridges, demand bytes, RedBird Absolute detours, dispatch and the
    // native-site registry) on private executable pages. Each page holds two small functions whose first
    // bytes are the audited prologue and continuation of 0xE2610 and 0xD90D0, followed by a body that
    // counts calls and returns a value built from every argument, including stack argument five.
    // Desktop CLR on the test host, not Unity Mono: transition costs differ in game.
    [TestClass]
    [DoNotParallelize]
    public sealed unsafe class NativeBrokerTests
    {
        public TestContext TestContext { get; set; }
        private static readonly List<object> roots = new List<object>();
        private const int PclOffset = 0x100, TileOffset = 0x300, CounterOffset = 0xF00, ScanLimit = 0x80;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int PclLoop(int count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TileLoop(int count);
        private static volatile bool never;

        private sealed class Page
        {
            internal IntPtr Base;
            internal ulong Pcl, Tile;
            internal int* PclCalls, TileCalls, TileYSum, TileIdSum;
            internal PclLoop PclLoop;
            internal TileLoop TileLoop;
        }

        // Manager 7, player 1, PCLs 2 and 3, mode 4.
        private const int Vanilla = 1 * 1000 + 2 * 100 + 3 * 10 + 4 + 7;

        private static Page CreatePage()
        {
            IntPtr memory = VirtualAlloc(IntPtr.Zero, new UIntPtr(4096), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, memory);
            roots.Add(memory);
            var page = new Page { Base = memory, Pcl = Address(memory, PclOffset), Tile = Address(memory, TileOffset) };
            ulong counters = Address(memory, CounterOffset);
            page.PclCalls = (int*)counters; page.TileCalls = (int*)(counters + 4); page.TileYSum = (int*)(counters + 8); page.TileIdSum = (int*)(counters + 12);

            var pcl = new Assembler(64);
            pcl.db(NativeBrokerRuntime.PclReachabilityBytes);
            pcl.mov(r10, counters);
            pcl.inc(__dword_ptr[r10]);
            pcl.imul(eax, edx, 1000);
            pcl.imul(r11d, r8d, 100); pcl.add(eax, r11d);
            pcl.imul(r11d, r9d, 10); pcl.add(eax, r11d);
            pcl.add(eax, __dword_ptr[rsp + 0x988 + 0x20 + 0x28]);
            pcl.add(eax, ecx);
            pcl.add(rsp, 0x988);
            pcl.pop(r14); pcl.pop(r13); pcl.pop(r12); pcl.pop(rbp);
            pcl.ret();
            Write(pcl, page.Pcl);

            var tile = new Assembler(64);
            tile.db(NativeBrokerRuntime.TileRefreshBytes);
            tile.mov(r10, counters);
            tile.inc(__dword_ptr[r10 + 4]);
            tile.add(__dword_ptr[r10 + 8], edx);
            tile.add(__dword_ptr[r10 + 12], r8d);
            tile.pop(rdi);
            tile.mov(rbx, __qword_ptr[rsp + 8]); tile.mov(rbp, __qword_ptr[rsp + 0x10]); tile.mov(rsi, __qword_ptr[rsp + 0x18]);
            tile.ret();
            Write(tile, page.Tile);

            var pclLoop = RedBird.X64.Assembly.ExecutableFunction.Create(asm => {
                var next = asm.CreateLabel();
                asm.push(rbx); asm.sub(rsp, 0x30);
                asm.mov(ebx, ecx);
                asm.Label(ref next);
                asm.mov(ecx, 7); asm.mov(edx, 1); asm.mov(r8d, 2); asm.mov(r9d, 3); asm.mov(__dword_ptr[rsp + 0x20], 4);
                asm.mov(rax, page.Pcl); asm.call(rax);
                asm.dec(ebx); asm.jne(next);
                asm.add(rsp, 0x30); asm.pop(rbx); asm.ret();
            });
            var tileLoop = RedBird.X64.Assembly.ExecutableFunction.Create(asm => {
                var next = asm.CreateLabel();
                asm.push(rbx); asm.sub(rsp, 0x20);
                asm.mov(ebx, ecx);
                asm.Label(ref next);
                asm.mov(ecx, 7); asm.mov(edx, 12); asm.mov(r8d, 345);
                asm.mov(rax, page.Tile); asm.call(rax);
                asm.dec(ebx); asm.jne(next);
                asm.add(rsp, 0x20); asm.pop(rbx); asm.ret();
            });
            page.PclLoop = Marshal.GetDelegateForFunctionPointer<PclLoop>(pclLoop.EntryPoint);
            page.TileLoop = Marshal.GetDelegateForFunctionPointer<TileLoop>(tileLoop.EntryPoint);
            roots.Add(pclLoop); roots.Add(tileLoop); roots.Add(page.PclLoop); roots.Add(page.TileLoop);
            return page;
        }

        private static void Write(Assembler asm, ulong address)
        {
            using (var stream = new MemoryStream())
            {
                asm.Assemble(new StreamCodeWriter(stream), address);
                byte[] code = stream.ToArray();
                Assert.IsTrue(code.Length < 0x180, "synthetic function fits its slot");
                Marshal.Copy(code, 0, new IntPtr(unchecked((long)address)), code.Length);
            }
        }

        private static ulong Address(IntPtr memory, int offset) => unchecked((ulong)memory.ToInt64() + (uint)offset);

        // A registry bound so that the broker's production RVA maps onto the private function.
        private static NativeOwnershipRegistry Registry(ulong function, int rva)
        {
            var registry = new NativeOwnershipRegistry();
            Assert.IsTrue(registry.BindModule(unchecked((long)function) - rva, rva + 0x100, address => Marshal.ReadByte(new IntPtr(address))));
            return registry;
        }

        private static IntPtr Demand()
        {
            IntPtr demand = Marshal.AllocHGlobal(1); Marshal.WriteByte(demand, 0); roots.Add(demand);
            return demand;
        }

        private static PclReachabilityBrokerCore PclBroker(Page page, NativeOwnershipRegistry registry, ManualLogSource log = null)
        {
            var core = new PclReachabilityBrokerCore(Demand(), ScanLimit);
            core.Site.Bind(page.Pcl, registry, log ?? new ManualLogSource("broker-test"), suspend: false);
            roots.Add(core);
            return core;
        }

        private static TileRefreshBrokerCore TileBroker(Page page, NativeOwnershipRegistry registry)
        {
            var core = new TileRefreshBrokerCore(Demand(), ScanLimit);
            core.Site.Bind(page.Tile, registry, new ManualLogSource("broker-test"), suspend: false);
            roots.Add(core);
            return core;
        }

        private static NativeBrokerRegistration Register(PclReachabilityBrokerCore core, string id, int order, PclReachabilityHandler pre, PclReachabilityHandler post)
        {
            if (!core.Dispatcher.Registry.TryAdd("broker.test", id, order, pre, post, out NativeBrokerRegistration handle, out string reason))
                Assert.Fail("Registration refused: " + reason);
            return handle;
        }

        private static byte[] Read(ulong address, int length) { var bytes = new byte[length]; Marshal.Copy(new IntPtr(unchecked((long)address)), bytes, 0, length); return bytes; }

        [TestMethod]
        public void ProcessBrokersStayPendingUntilAPISharedBindsTheImage()
        {
            Assert.AreEqual(NativeBrokerState.Pending, PclReachabilityBroker.State);
            Assert.AreEqual(NativeBrokerState.Pending, TileRefreshBroker.State);
            Assert.IsFalse(PclReachabilityBroker.TryRegister("broker.test", "pending", 0, (ref PclReachabilityCall call) => { }, null, out var handle, out string reason));
            Assert.IsNull(handle);
            StringAssert.Contains(reason, "Pending");
            Assert.IsFalse(TileRefreshBroker.TryRegister("broker.test", "pending", 0, call => { }, out _, out _));
            bool thrown = false;
            try { PclReachabilityBroker.CallOriginal(IntPtr.Zero, 0, 0, 0, 0); }
            catch (InvalidOperationException) { thrown = true; }
            Assert.IsTrue(thrown, "CallOriginal before binding throws instead of calling an unknown address");
            Assert.AreEqual(0xE2610, PclReachabilityBroker.EntryRva);
            Assert.AreEqual(0xD90D0, TileRefreshBroker.EntryRva);
        }

        [TestMethod]
        public void BrokerPatchesOnlyAtFirstRegistrationAndDispatchesThroughTheNativeEntry()
        {
            Page page = CreatePage();
            NativeOwnershipRegistry registry = Registry(page.Pcl, NativeBrokerRuntime.PclReachabilityRva);
            PclReachabilityBrokerCore core = PclBroker(page, registry);
            byte[] pristine = Read(page.Pcl, NativeBrokerRuntime.PclReachabilityBytes.Length);
            CollectionAssert.AreEqual(NativeBrokerRuntime.PclReachabilityBytes, pristine);
            Assert.AreEqual(NativeBrokerState.Dormant, core.Site.State);
            Assert.AreEqual(Vanilla, page.PclLoop(3));
            Assert.AreEqual(3, *page.PclCalls);
            Assert.AreEqual(Vanilla, core.Site.Original(new IntPtr(7), 1, 2, 3, 4), "before installation CallOriginal is the live entry");
            CollectionAssert.AreEqual(pristine, Read(page.Pcl, pristine.Length), "dormant broker patches nothing");
            Assert.AreEqual(0, registry.Registrations.Count);

            int overrideValue = 0, posts = 0, postResult = 0; bool postOriginal = false;
            NativeBrokerRegistration pre = Register(core, "pre", 0, (ref PclReachabilityCall call) =>
            {
                Assert.AreEqual((7, 1, 2, 3, 4), ((int)call.Manager, call.Player, call.FirstPcl, call.SecondPcl, call.Mode));
                if (overrideValue != 0) call.TryOverride(overrideValue);
            }, null);
            NativeBrokerRegistration post = Register(core, "post", 1, null, (ref PclReachabilityCall call) =>
            {
                posts++; postResult = call.Result; postOriginal = call.OriginalCalled;
                if (call.Result == 77) call.TrySetResult(78);
            });
            Assert.AreEqual(NativeBrokerState.Installed, core.Site.State, core.Site.Diagnostic);
            Assert.AreEqual(1, core.Site.DemandValue);
            byte[] patched = Read(page.Pcl, 16);
            Assert.AreEqual(0xFF, patched[0]); Assert.AreEqual(0x25, patched[1]);
            Assert.IsTrue(registry.TryGetWriter(NativeBrokerRuntime.PclReachabilityRva, out NativeSiteRegistration writer));
            Assert.AreEqual((APISharedPlugin.PluginGuid, ApiSharedNativeSites.BrokerPclReachability, 16, NativeSiteMode.Exclusive, true),
                (writer.OwnerGuid, writer.Name, writer.Length, writer.Mode, writer.OriginalBytesVerified));

            *page.PclCalls = 0;
            Assert.AreEqual(Vanilla, page.PclLoop(2));
            Assert.AreEqual((2, 2, Vanilla, true), (*page.PclCalls, posts, postResult, postOriginal));

            overrideValue = 77;
            Assert.AreEqual(78, page.PclLoop(1), "Pre override, then Post replacement");
            Assert.AreEqual((2, 3, 77, false), (*page.PclCalls, posts, postResult, postOriginal), "the override skipped the original");
            overrideValue = 0;

            Assert.AreEqual(Vanilla, core.Site.Original(new IntPtr(7), 1, 2, 3, 4), "CallOriginal bypasses consumers");
            Assert.AreEqual((3, 3), (*page.PclCalls, posts));

            // The guard reads the demand byte natively: at zero, managed code is never entered.
            core.Site.SetDemand(false);
            Assert.AreEqual(Vanilla, page.PclLoop(2));
            Assert.AreEqual((5, 3), (*page.PclCalls, posts));
            core.Site.SetDemand(true);

            pre.Dispose(); post.Dispose();
            Assert.AreEqual(0, core.Site.DemandValue, "the last disposal restores the zero-registration path");
            Assert.AreEqual(Vanilla, page.PclLoop(2));
            Assert.AreEqual((7, 3), (*page.PclCalls, posts));
            Assert.AreEqual(NativeBrokerState.Installed, core.Site.State, "the detour stays for the process lifetime");

            NativeBrokerRegistration again = Register(core, "again", 0, null, (ref PclReachabilityCall call) => posts++);
            Assert.AreEqual(Vanilla, page.PclLoop(1));
            Assert.AreEqual(4, posts);
            again.Dispose();
            Assert.AreEqual(0, core.Dispatcher.Registry.Failures, "callback assertions held");
        }

        [TestMethod]
        public void UnregisterDuringNativeDispatchSkipsThatRegistrationsRemainingCallbacks()
        {
            Page page = CreatePage();
            PclReachabilityBrokerCore core = PclBroker(page, Registry(page.Pcl, NativeBrokerRuntime.PclReachabilityRva));
            var calls = new List<string>();
            NativeBrokerRegistration victim = null;
            Register(core, "remover", 0, (ref PclReachabilityCall call) => { calls.Add("remover"); victim?.Dispose(); }, null);
            victim = Register(core, "victim", 1, (ref PclReachabilityCall call) => calls.Add("victim pre"), (ref PclReachabilityCall call) => calls.Add("victim post"));
            Register(core, "observer", 2, null, (ref PclReachabilityCall call) => calls.Add("observer " + call.Result));
            Assert.AreEqual(Vanilla, page.PclLoop(1));
            CollectionAssert.AreEqual(new[] { "remover", "observer " + Vanilla }, calls);
            Assert.AreEqual(2, core.Dispatcher.Registry.Count);
        }

        [TestMethod]
        public void ThrowingConsumersAreIsolatedFromEachOtherAndTheNativeFrame()
        {
            Page page = CreatePage();
            PclReachabilityBrokerCore core = PclBroker(page, Registry(page.Pcl, NativeBrokerRuntime.PclReachabilityRva));
            Register(core, "bad-pre", 0, (ref PclReachabilityCall call) => { call.TryOverride(5); throw new InvalidOperationException("pre"); }, null);
            Register(core, "bad-post", 1, null, (ref PclReachabilityCall call) => { call.TrySetResult(6); throw new InvalidOperationException("post"); });
            int seen = 0;
            Register(core, "good", 2, null, (ref PclReachabilityCall call) => seen = call.Result);
            Assert.AreEqual(Vanilla, page.PclLoop(4));
            Assert.AreEqual(Vanilla, seen);
            Assert.AreEqual(4, *page.PclCalls);
            Assert.AreEqual(8, core.Dispatcher.Registry.Failures);
        }

        [TestMethod]
        public void ForeignDetourIsRefusedLoggedOnceAndRecordedWhileItKeepsWorking()
        {
            Page page = CreatePage();
            NativeOwnershipRegistry registry = Registry(page.Pcl, NativeBrokerRuntime.PclReachabilityRva);
            // A consumer's own entry detour, as BugfixesAndQoL or the naval plugin install it today.
            PclReachabilityNative foreignCallback = null; NativeDetour<PclReachabilityNative> foreign = null;
            foreignCallback = (manager, player, first, second, mode) => foreign.Original(manager, player, first, second, mode) + 1000000;
            var request = new DetourRequest<PclReachabilityNative> { Name = "foreign", TargetAddress = page.Pcl, Callback = foreignCallback };
            foreign = (NativeDetour<PclReachabilityNative>)new NativeDetourBackend(new NativeDetourOptions
            { SuspendThreadsDuringPatch = false, FunctionScanLimit = ScanLimit, FollowJumps = false }).CreateDetour(in request);
            foreign.Enable();
            roots.Add(foreign); roots.Add(foreignCallback);
            byte[] foreignBytes = Read(page.Pcl, 23);
            Assert.AreEqual(Vanilla + 1000000, page.PclLoop(1));

            var log = new ManualLogSource("broker-foreign");
            var refusals = new List<string>();
            log.LogEvent += (sender, args) => { if (args.Data.ToString().Contains("NATIVE_BROKER_REFUSED")) refusals.Add(args.Data.ToString()); };
            {
                PclReachabilityBrokerCore core = PclBroker(page, registry, log);
                Assert.IsFalse(core.Dispatcher.Registry.TryAdd("naval", "reach", 0, (ref PclReachabilityCall call) => { }, null, out var handle, out string reason));
                Assert.IsNull(handle);
                Assert.AreEqual(NativeBrokerState.ForeignOwner, core.Site.State);
                StringAssert.Contains(reason, "ForeignOwner");
                Assert.IsFalse(core.Dispatcher.Registry.TryAdd("hybrid", "query", 0, null, (ref PclReachabilityCall call) => { }, out _, out _));
                Assert.AreEqual(1, refusals.Count, "logged once: " + string.Join(" | ", refusals));
                NativeSiteRegistrationResult refusal = registry.Refusals.Single();
                Assert.AreEqual((NativeSiteRegistrationStatus.UnexpectedBytes, APISharedPlugin.PluginGuid, ApiSharedNativeSites.BrokerPclReachability),
                    (refusal.Status, refusal.OwnerGuid, refusal.Name));
                Assert.AreEqual(0, registry.Registrations.Count, "nothing is claimed");
                CollectionAssert.AreEqual(foreignBytes, Read(page.Pcl, 23), "the foreign detour is untouched");
                Assert.AreEqual(Vanilla + 1000000, page.PclLoop(1));
                Assert.AreEqual(Vanilla + 1000000, core.Site.Original(new IntPtr(7), 1, 2, 3, 4), "CallOriginal uses the live entry while refused");
                Assert.AreEqual(0, core.Site.DemandValue);
            }

            // The same refusal without a bound registry comes from the broker's own live-byte check.
            Page unbound = CreatePage();
            byte* entry = (byte*)unbound.Pcl;
            entry[0] = 0xE9; *(int*)(entry + 1) = 0; // jmp +0: any patch counts
            PclReachabilityBrokerCore second = PclBroker(unbound, null);
            Assert.IsFalse(second.Dispatcher.Registry.TryAdd("naval", "reach", 0, (ref PclReachabilityCall call) => { }, null, out _, out string unboundReason));
            Assert.AreEqual(NativeBrokerState.ForeignOwner, second.Site.State);
            StringAssert.Contains(unboundReason, "Live entry byte +0");
        }

        [TestMethod]
        public void RegisteredForeignWriterIsRefusedWithoutTouchingTheEntry()
        {
            Page page = CreatePage();
            NativeOwnershipRegistry registry = Registry(page.Pcl, NativeBrokerRuntime.PclReachabilityRva);
            Assert.IsTrue(registry.RegisterSites("naval", "E2610", new[] { new NativeSiteSpan(NativeBrokerRuntime.PclReachabilityRva, 16, NativeSiteMode.Exclusive) }).Succeeded);
            PclReachabilityBrokerCore core = PclBroker(page, registry);
            Assert.IsFalse(core.Dispatcher.Registry.TryAdd("bugfixes", "e2610", 0, null, (ref PclReachabilityCall call) => { }, out _, out string reason));
            Assert.AreEqual(NativeBrokerState.ForeignOwner, core.Site.State);
            StringAssert.Contains(reason, "naval");
            Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, registry.Refusals.Single().Status);
            CollectionAssert.AreEqual(NativeBrokerRuntime.PclReachabilityBytes, Read(page.Pcl, 23));
        }

        [TestMethod]
        public void InstalledBrokerSharesItsEntryOnlyWithCallOnlyRegistrations()
        {
            Page page = CreatePage();
            NativeOwnershipRegistry registry = Registry(page.Pcl, NativeBrokerRuntime.PclReachabilityRva);
            PclReachabilityBrokerCore core = PclBroker(page, registry);
            Register(core, "observer", 0, null, (ref PclReachabilityCall call) => { });
            int rva = NativeBrokerRuntime.PclReachabilityRva;
            Assert.IsTrue(registry.RegisterSites("hybrid", "calls E2610", new[] { new NativeSiteSpan(rva, 1, NativeSiteMode.CallOnly) }).Succeeded);
            NativeSiteRegistrationResult detour = registry.RegisterSites("naval", "E2610", new[] { new NativeSiteSpan(rva, 16, NativeSiteMode.Exclusive) });
            Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, detour.Status);
            Assert.AreEqual(APISharedPlugin.PluginGuid, detour.Conflict.OwnerGuid);
            NativeSiteRegistrationResult pristineCheck = registry.RegisterSites("hybrid", "pins E2610", new[] { new NativeSiteSpan(rva, 16, NativeSiteMode.Observe) });
            Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, pristineCheck.Status, "a pristine-byte pin of the entry conflicts with the broker; pin the body instead");
        }

        [TestMethod]
        public void TileRefreshPostsRunAfterTheOriginalWithItsArguments()
        {
            Page page = CreatePage();
            NativeOwnershipRegistry registry = Registry(page.Tile, NativeBrokerRuntime.TileRefreshRva);
            TileRefreshBrokerCore core = TileBroker(page, registry);
            page.TileLoop(1);
            Assert.AreEqual(1, *page.TileCalls);
            Assert.AreEqual(NativeBrokerState.Dormant, core.Site.State);
            var seen = new List<string>();
            Assert.IsTrue(core.Dispatcher.Registry.TryAdd("landscape", "cells", 0, null,
                call => seen.Add(*page.TileCalls + ":" + (int)call.Manager + "/" + call.TileY + "/" + call.TileId), out var first, out string reason), reason);
            Assert.IsTrue(core.Dispatcher.Registry.TryAdd("bugfixes", "connectivity", -1, null, call => seen.Add("bugfixes"), out var second, out reason), reason);
            Assert.AreEqual(NativeBrokerState.Installed, core.Site.State, core.Site.Diagnostic);
            Assert.IsTrue(registry.TryGetWriter(NativeBrokerRuntime.TileRefreshRva, out NativeSiteRegistration writer));
            Assert.AreEqual((ApiSharedNativeSites.BrokerTileRefresh, 15, true), (writer.Name, writer.Length, writer.OriginalBytesVerified));
            page.TileLoop(2);
            CollectionAssert.AreEqual(new[] { "bugfixes", "2:7/12/345", "bugfixes", "3:7/12/345" }, seen);
            Assert.AreEqual((3, 36, 1035), (*page.TileCalls, *page.TileYSum, *page.TileIdSum));
            first.Dispose(); second.Dispose();
            Assert.AreEqual(0, core.Site.DemandValue);
            page.TileLoop(1);
            Assert.AreEqual(4, seen.Count);
            Assert.AreEqual(4, *page.TileCalls);
        }

        // The production detour contract on the real CrusaderDE.dll bytes: .text and .rdata mapped into
        // private memory at their RVAs, production scan limit, patched but never executed.
        [TestMethod]
        public void ProductionContractHoldsOnTheInstalledImage()
        {
            string path = Path.Combine(GameDirectory(), "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll");
            byte[] file = File.ReadAllBytes(path);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                Assert.AreEqual(NativeBrokerRuntime.AuditedHash, BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""));
            const int mapped = 0x240000;
            IntPtr image = VirtualAlloc(IntPtr.Zero, new UIntPtr(mapped), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, image);
            roots.Add(image);
            int pe = BitConverter.ToInt32(file, 0x3C), count = BitConverter.ToUInt16(file, pe + 6), optional = BitConverter.ToUInt16(file, pe + 20);
            for (int index = 0; index < count; index++)
            {
                int header = pe + 24 + optional + index * 40, va = BitConverter.ToInt32(file, header + 12),
                    raw = BitConverter.ToInt32(file, header + 16), offset = BitConverter.ToInt32(file, header + 20);
                if (va < mapped) Marshal.Copy(file, offset, image + va, Math.Min(raw, mapped - va));
            }
            var registry = new NativeOwnershipRegistry();
            Assert.IsTrue(registry.BindModule(image.ToInt64(), mapped, address => Marshal.ReadByte(new IntPtr(address))));

            var pcl = new PclReachabilityBrokerCore(Demand(), NativeBrokerRuntime.ScanLimit);
            var tile = new TileRefreshBrokerCore(Demand(), NativeBrokerRuntime.ScanLimit);
            roots.Add(pcl); roots.Add(tile);
            pcl.Site.Bind(Address(image, NativeBrokerRuntime.PclReachabilityRva), registry, new ManualLogSource("broker-image"), suspend: false);
            tile.Site.Bind(Address(image, NativeBrokerRuntime.TileRefreshRva), registry, new ManualLogSource("broker-image"), suspend: false);
            Assert.IsTrue(pcl.Dispatcher.Registry.TryAdd("broker.test", "image", 0, null, (ref PclReachabilityCall call) => { }, out var first, out string reason), reason);
            Assert.IsTrue(tile.Dispatcher.Registry.TryAdd("broker.test", "image", 0, null, call => { }, out var second, out reason), reason);
            Assert.AreEqual((NativeBrokerState.Installed, NativeBrokerState.Installed), (pcl.Site.State, tile.Site.State));
            Assert.AreEqual((16, 15), (pcl.Site.Hook.DisplacedByteCount, tile.Site.Hook.DisplacedByteCount));
            pcl.Site.Validate(); tile.Site.Validate();
            foreach (var group in new[] { ApiSharedNativeSites.BrokerPclReachability, ApiSharedNativeSites.BrokerTileRefresh })
                Assert.IsTrue(registry.Registrations.Any(r => r.Name == group && r.Mode == NativeSiteMode.Exclusive && r.OriginalBytesVerified), group);
            // The registry spans are the catalog's spans.
            var catalog = ApiSharedNativeSites.All().ToDictionary(g => g.Key, g => g.Value);
            foreach (var group in new[] { ApiSharedNativeSites.BrokerPclReachability, ApiSharedNativeSites.BrokerTileRefresh })
                CollectionAssert.AreEqual(catalog[group].Select(s => (s.Rva, s.Length, s.Mode)).ToArray(),
                    registry.Registrations.Where(r => r.Name == group).Select(r => (r.Rva, r.Length, r.Mode)).ToArray(), group);
            TestContext.WriteLine("NATIVE_BROKER_IMAGE_CONTRACT e2610={0} d90d0={1}", pcl.Site.Diagnostic, tile.Site.Diagnostic);
        }

        // One sample: one managed->native transition for Calls native calls of the target.
        private const int Calls = 20000, Rounds = 31;

        [TestMethod]
        public void BrokerCostAgainstAHandWrittenDetourOnCopiedCode()
        {
            Page vanilla = CreatePage(), hand = CreatePage(), brokered = CreatePage();
            // The hand-written detours: one managed callback with one cheap scope check, as consumers
            // write them today (naval Pre-style for 0xE2610, Post-style observer for 0xD90D0).
            var backend = new NativeDetourBackend(new NativeDetourOptions
            { AllowedSchemes = DetourScheme.Absolute, SuspendThreadsDuringPatch = false, FunctionScanLimit = ScanLimit, FollowJumps = false });
            NativeDetour<PclReachabilityNative> handPcl = null; NativeDetour<TileRefreshNative> handTile = null;
            PclReachabilityNative handPclCallback = (manager, player, first, second, mode) => never ? 1 : handPcl.Original(manager, player, first, second, mode);
            TileRefreshNative handTileCallback = (manager, y, tile) => { handTile.Original(manager, y, tile); if (never) y++; };
            var pclRequest = new DetourRequest<PclReachabilityNative> { Name = "hand", TargetAddress = hand.Pcl, Callback = handPclCallback };
            var tileRequest = new DetourRequest<TileRefreshNative> { Name = "hand", TargetAddress = hand.Tile, Callback = handTileCallback };
            handPcl = (NativeDetour<PclReachabilityNative>)backend.CreateDetour(in pclRequest); handPcl.Enable();
            handTile = (NativeDetour<TileRefreshNative>)backend.CreateDetour(in tileRequest); handTile.Enable();
            roots.Add(handPcl); roots.Add(handTile); roots.Add(handPclCallback); roots.Add(handTileCallback);

            PclReachabilityBrokerCore pcl = PclBroker(brokered, Registry(brokered.Pcl, NativeBrokerRuntime.PclReachabilityRva));
            TileRefreshBrokerCore tile = TileBroker(brokered, Registry(brokered.Tile, NativeBrokerRuntime.TileRefreshRva));
            PclReachabilityHandler pre = (ref PclReachabilityCall call) => { if (never) call.TryOverride(1); };
            PclReachabilityHandler post = (ref PclReachabilityCall call) => { if (never) call.TrySetResult(1); };
            TileRefreshHandler observe = call => { if (never) never = false; };
            var handles = new List<NativeBrokerRegistration>();
            Action clear = () => { foreach (var handle in handles) handle.Dispose(); handles.Clear(); };
            Func<int, bool, Action> pclConsumers = (count, withPost) => () => {
                clear();
                for (int index = 0; index < count; index++)
                {
                    Assert.IsTrue(pcl.Dispatcher.Registry.TryAdd("bench", "c" + index, index, pre, withPost ? post : null, out var handle, out string reason), reason);
                    handles.Add(handle);
                }
            };
            Func<int, Action> tileConsumers = count => () => {
                clear();
                for (int index = 0; index < count; index++)
                {
                    Assert.IsTrue(tile.Dispatcher.Registry.TryAdd("bench", "c" + index, index, null, observe, out var handle, out string reason), reason);
                    handles.Add(handle);
                }
            };
            // Install both brokers, then return to zero registrations.
            pclConsumers(1, false)(); tileConsumers(1)(); clear();
            Assert.AreEqual((NativeBrokerState.Installed, NativeBrokerState.Installed), (pcl.Site.State, tile.Site.State));

            var modes = new (string Name, Action Apply, Func<long> Run)[]
            {
                ("e2610 vanilla-unhooked", clear, () => vanilla.PclLoop(Calls)),
                ("e2610 hand-written-detour", clear, () => hand.PclLoop(Calls)),
                ("e2610 broker-0", clear, () => brokered.PclLoop(Calls)),
                ("e2610 broker-1-pre", pclConsumers(1, false), () => brokered.PclLoop(Calls)),
                ("e2610 broker-2-pre", pclConsumers(2, false), () => brokered.PclLoop(Calls)),
                ("e2610 broker-8-pre", pclConsumers(8, false), () => brokered.PclLoop(Calls)),
                ("e2610 broker-8-pre+post", pclConsumers(8, true), () => brokered.PclLoop(Calls)),
                ("d90d0 vanilla-unhooked", clear, () => { vanilla.TileLoop(Calls); return 0; }),
                ("d90d0 hand-written-detour", clear, () => { hand.TileLoop(Calls); return 0; }),
                ("d90d0 broker-0", clear, () => { brokered.TileLoop(Calls); return 0; }),
                ("d90d0 broker-1-post", tileConsumers(1), () => { brokered.TileLoop(Calls); return 0; }),
                ("d90d0 broker-2-post", tileConsumers(2), () => { brokered.TileLoop(Calls); return 0; }),
                ("d90d0 broker-8-post", tileConsumers(8), () => { brokered.TileLoop(Calls); return 0; }),
            };
            var samples = modes.Select(_ => new List<double>()).ToArray();
            var bytes = new long[modes.Length];
            var process = Process.GetCurrentProcess(); var priority = process.PriorityClass; var threadPriority = Thread.CurrentThread.Priority;
            try
            {
                // Raised priority reduces scheduler noise on a shared machine; restored below.
                process.PriorityClass = ProcessPriorityClass.High; Thread.CurrentThread.Priority = ThreadPriority.Highest;
                for (int round = -2; round < Rounds; round++)
                    for (int index = 0; index < modes.Length; index++)
                    {
                        // Alternate the mode order each round so drift affects every mode alike.
                        int mode = (round & 1) == 0 ? index : modes.Length - 1 - index;
                        modes[mode].Apply();
                        long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                        long result = modes[mode].Run();
                        long elapsed = Stopwatch.GetTimestamp() - start, allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                        if (modes[mode].Name.StartsWith("e2610")) Assert.AreEqual(Vanilla, result, modes[mode].Name);
                        if (round < 0) continue;
                        samples[mode].Add(elapsed * (1e9 / Stopwatch.Frequency) / Calls);
                        bytes[mode] += allocated;
                    }
            }
            finally { process.PriorityClass = priority; Thread.CurrentThread.Priority = threadPriority; clear(); }
            for (int mode = 0; mode < modes.Length; mode++)
            {
                var sorted = samples[mode].OrderBy(value => value).ToArray();
                TestContext.WriteLine("NATIVE_BROKER_BENCH mode={0},bytesPerCall={1:F2},nsPerCallMin={2:F2},nsPerCallMedian={3:F2},samples={4}x{5}",
                    modes[mode].Name, bytes[mode] / (double)(Rounds * Calls), sorted[0], sorted[sorted.Length / 2], Rounds, Calls);
                Assert.AreEqual(0L, bytes[mode], modes[mode].Name + " allocates.");
            }
        }

        private static string GameDirectory()
        {
            string configuration = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt");
            if (File.Exists(configuration)) return File.ReadAllText(configuration).Trim();
            return Environment.GetEnvironmentVariable("SHCDE_GAME_DIR") ?? throw new InvalidOperationException("Set SHCDE_GAME_DIR.");
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protection);
    }
}
