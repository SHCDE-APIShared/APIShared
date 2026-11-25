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
