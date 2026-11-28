using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using APIShared;
using APIShared.WorldMutations;
using APIShared.NativeSites;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class WorldMutationNativeTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long Work(IntPtr manager, int slot);

        [TestMethod]
        public void BothCanonicalMoatBodiesMatchProductionContractsAndWholeEntryInstructions()
        {
            byte[] file = File.ReadAllBytes(Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
            using (var hash = SHA256.Create()) Assert.AreEqual(ApiSharedRuntime.SupportedHash, Hex(hash.ComputeHash(file)));
            foreach (string name in new[] { "dig", "fill" })
            {
                object contract = Contract(name); int rva = Field<int>(contract, "rva"), size = Field<int>(contract, "size");
                byte[] body = AtRva(file, rva, size), entry = Field<byte[]>(contract, "entry");
                using (var hash = SHA256.Create()) Assert.AreEqual(Field<string>(contract, "bodyHash"), Hex(hash.ComputeHash(body)));
                CollectionAssert.AreEqual(entry, body.Take(entry.Length).ToArray());
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(body)); decoder.IP = (ulong)rva;
                ulong boundary = (ulong)(rva + Field<int>(contract, "displaced")); bool sawBoundary = false;
                while (decoder.IP < (ulong)(rva + size))
                {
                    decoder.Decode(out Instruction instruction); Assert.IsFalse(instruction.IsInvalid);
                    if (decoder.IP == boundary) sawBoundary = true;
                    if (instruction.Op0Kind == OpKind.NearBranch64)
                        Assert.IsFalse(instruction.NearBranchTarget > (ulong)rva && instruction.NearBranchTarget < boundary);
                }
                Assert.IsTrue(sawBoundary); Assert.AreEqual((ulong)(rva + size), decoder.IP);
            }
        }

        [TestMethod]
        public void ProductionIndirectBackendPreservesBothDisplacedEntriesAndCallsOriginalOnce()
        {
            foreach (string name in new[] { "dig", "fill" })
            {
                object contract = Contract(name); byte[] entry = Field<byte[]>(contract, "entry");
                int displaced = Field<int>(contract, "displaced"), size = Field<int>(contract, "size");
                using (var target = ExecutableFunction.Create(a => {
                    a.db(entry); a.mov(eax, edx); if (name == "dig") a.pop(rsi); a.ret();
                }))
                {
                    NativeDetour<Work> hook = null; int calls = 0;
                    Work callback = (manager, slot) => { calls++; return hook.Original(manager, slot); };
                    try
                    {
                        var options = WorldMutationRuntime.MoatOptions(size) with { SuspendThreadsDuringPatch = false };
                        Assert.AreEqual(size, options.FunctionScanLimit); Assert.IsFalse(options.FollowJumps); Assert.IsFalse(options.FollowCalls);
                        Assert.AreEqual(DetourScheme.Indirect, options.AllowedSchemes);
                        var request = new DetourRequest<Work> { Name = "Private moat " + name, Callback = callback, TargetAddress = unchecked((ulong)target.EntryPoint.ToInt64()) };
                        hook = (NativeDetour<Work>)new NativeDetourBackend(options).CreateDetour(in request);
                        NativeIndirectEntry.Validate(hook, target.EntryPoint, entry, displaced, false);
                        var invoke = Marshal.GetDelegateForFunctionPointer<Work>(target.EntryPoint);
                        Assert.AreEqual(27L, invoke(IntPtr.Zero, 27)); Assert.AreEqual(0, calls);
                        hook.Enable(); NativeIndirectEntry.Validate(hook, target.EntryPoint, entry, displaced, true);
                        Assert.AreEqual(4294967289L, invoke(new IntPtr(123), -7)); Assert.AreEqual(1, calls);
                        Assert.AreEqual(98L, invoke(new IntPtr(456), 98)); Assert.AreEqual(2, calls);
                        GC.KeepAlive(callback); GC.KeepAlive(invoke);
                    }
                    finally { hook?.Dispose(); } // No other thread can execute this private fixture.
                }
            }
        }
        [TestMethod]
        public void NotificationProloguesAcceptActualRedBirdEncodingAndRejectForeignChanges()
        {
            byte[][] entries = {
                new byte[] { 0x48,0x89,0x5C,0x24,0x18,0x55 },
                new byte[] { 0x40,0x53,0x48,0x83,0xEC,0x20 },
                new byte[] { 0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x10 },
                new byte[] { 0x48,0x89,0x5C,0x24,0x08,0x57 },
                new byte[] { 0x48,0x83,0xEC,0x28,0x83,0xF9,0x02 }
            };
            byte[] image = File.ReadAllBytes(Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
            using (var hash = SHA256.Create()) Assert.AreEqual(ApiSharedRuntime.SupportedHash, Hex(hash.ComputeHash(image)));
            int[] rvas = { 0xFEE50, 0x102C30, 0x24220, 0x87D10, 0x104240 };
            for (int index = 0; index < entries.Length; index++)
            {
                byte[] entry = entries[index];
                CollectionAssert.AreEqual(entry, AtRva(image, rvas[index], entry.Length));
                // A private two-page fixture: the decoder has its full 256-byte scan
                // plus lookahead in the first page; the following page is inaccessible.
                IntPtr page = VirtualAlloc(IntPtr.Zero, new UIntPtr(8192), 0x3000, 0x40);
                Assert.AreNotEqual(IntPtr.Zero, page);
                Assert.IsTrue(VirtualProtect(page + 4096, new UIntPtr(4096), 1, out uint unused));
                IntPtr target = page + 4096 - (256 + 16);
                NativeDetour<Work> hook = null;
                Work callback = (manager, slot) => 0; // Never called: these are real prologues, not executable function bodies.
                try
                {
                    byte[] source = Enumerable.Repeat((byte)0x90, 256 + 16).ToArray();
                    Array.Copy(entry, source, entry.Length); source[entry.Length + 32] = 0xC3;
                    Marshal.Copy(source, 0, target, source.Length);
                    byte[] expected = source.Take(entry.Length + 8).ToArray();
                    var options = WorldMutationRuntime.MoatOptions(256) with { SuspendThreadsDuringPatch = false };
                    var request = new DetourRequest<Work> { Name = "Private notification " + index,
                        Callback = callback, TargetAddress = unchecked((ulong)target.ToInt64()) };
                    hook = (NativeDetour<Work>)new NativeDetourBackend(options).CreateDetour(in request);
                    Action prepared = () => NativeIndirectEntry.Validate(hook, target, expected, entry.Length, false);
                    prepared();
                    RejectChangedByte(target, 0, prepared);
                    RejectChangedByte(hook.TrampolineAddress, 0, prepared);
                    RejectChangedByte(hook.TrampolineAddress, hook.TrampolineSize - 1, prepared);
                    // Validate the actual continuation, independent of byte-identical source length.
                    var decoder = Decoder.Create(64, new ByteArrayCodeReader(ReadBytes(hook.TrampolineAddress, hook.TrampolineSize)));
                    decoder.IP = unchecked((ulong)hook.TrampolineAddress.ToInt64());
                    Instruction last = default;
                    while (decoder.IP < unchecked((ulong)hook.TrampolineAddress.ToInt64()) + (uint)hook.TrampolineSize) { decoder.Decode(out last); Assert.IsFalse(last.IsInvalid); }
                    Assert.AreEqual(Mnemonic.Jmp, last.Mnemonic);
                    Assert.AreEqual(unchecked((ulong)target.ToInt64()) + (uint)entry.Length, last.NearBranchTarget);
                    if (index == 1) Assert.AreEqual(10, hook.TrampolineSize, "REX40 PUSH becomes one byte; original resume remains +6.");
                    var sizeField = hook.GetType().GetField("<TrampolineSize>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.IsNotNull(sizeField);
                    int size = hook.TrampolineSize;
                    try { sizeField.SetValue(hook, size + 1); Assert.ThrowsExactly<InvalidOperationException>(prepared); }
                    finally { sizeField.SetValue(hook, size); }
                    hook.Enable();
                    Action published = () => NativeIndirectEntry.Validate(hook, target, expected, entry.Length, true);
                    published();
                    RejectChangedByte(target, 0, published);
                    RejectChangedByte(target, entry.Length, published);
                    RejectChangedByte(hook.PointerSlot, 0, published);
                    RejectChangedByte(hook.TrampolineAddress, 0, published);
                    RejectChangedByte(hook.TrampolineAddress, hook.TrampolineSize - 1, published);
                    if (entry.Length > 6) RejectChangedByte(target, 6, published);
                    published();
                    GC.KeepAlive(callback);
                }
                finally { hook?.Dispose(); Assert.IsTrue(VirtualFree(page, UIntPtr.Zero, 0x8000)); }
            }
        }
        private static byte[] ReadBytes(IntPtr address, int count)
        { var result = new byte[count]; Marshal.Copy(address, result, 0, count); return result; }
        private static void RejectChangedByte(IntPtr address, int offset, Action validate)
        {
            IntPtr at = address + offset; byte original = Marshal.ReadByte(at);
            Assert.IsTrue(VirtualProtect(at, new UIntPtr(1), 0x40, out uint protect));
            try { Marshal.WriteByte(at, (byte)(original ^ 1)); Assert.ThrowsExactly<InvalidOperationException>(validate); }
            finally { Marshal.WriteByte(at, original); Assert.IsTrue(VirtualProtect(at, new UIntPtr(1), protect, out uint ignored)); }
            validate();
        }
        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint oldProtection);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        private static object Contract(string name) => typeof(WorldMutationRuntime).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(value);
        private static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "");
        private static byte[] AtRva(byte[] file, int rva, int length)
        {
            int pe = BitConverter.ToInt32(file, 0x3C), sections = pe + 24 + BitConverter.ToUInt16(file, pe + 20);
            for (int index = 0; index < BitConverter.ToUInt16(file, pe + 6); index++)
            {
                int section = sections + index * 40, start = BitConverter.ToInt32(file, section + 12), size = BitConverter.ToInt32(file, section + 16);
                if (rva >= start && (long)rva + length <= (long)start + size)
                { var bytes = new byte[length]; Array.Copy(file, BitConverter.ToInt32(file, section + 20) + rva - start, bytes, 0, length); return bytes; }
            }
            throw new InvalidOperationException("Moat function outside canonical executable sections.");
        }
    }
}
