using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using APIShared;
using APIShared.Health;
using APIShared.NativeSites;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // Private executable fixtures only. No game code or process-wide observer is installed here.
    [TestClass, DoNotParallelize]
    public sealed class HealthNativeTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InitializeFixture(IntPtr manager, int unitId, int type);

        [TestMethod]
        public void CanonicalInitializerAndInstalledHealthInteropMatchAuditedContract()
        {
            string native = Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll");
            byte[] bytes = File.ReadAllBytes(native);
            using (var sha = SHA256.Create())
                Assert.AreEqual(ApiSharedRuntime.SupportedHash, BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""));
            CollectionAssert.AreEqual(HealthNativeObserver.Entry, AtRva(bytes, HealthNativeObserver.Rva, HealthNativeObserver.Entry.Length),
                "The complete initializer prefix, not only the ten displaced bytes, is native-hash-bound.");

            // Exercise the production guard against the real installed Extender assembly,
            // including every HP/cache/link field, field signedness and complete record stride.
            HealthNativeObserver.ValidateLayout();
        }

        [TestMethod]
        public void ActualObserverBackendDisplacesTenBytesExecutesOriginalAndValidatesFullContinuation()
        {
            using (var target = ExecutableFunction.Create(a => {
                a.db(HealthNativeObserver.Entry);
                // Harmless replacement for the native body after the exact complete prologue.
                a.mov(__dword_ptr[rcx], edx); a.mov(__dword_ptr[rcx + 4], r8d);
                a.inc(__dword_ptr[rcx + 8]);
                a.add(rsp, 32); a.pop(r14); a.ret();
            }))
            {
                IntPtr output = Marshal.AllocHGlobal(16);
                NativeDetour<InitializeFixture> hook = null;
                int calls = 0, observedId = 0, observedType = 0; IntPtr observedManager = IntPtr.Zero;
                var callback = new InitializeFixture((manager, id, type) => {
                    calls++; observedManager = manager; observedId = id; observedType = type;
                    hook.Original(manager, id, type);
                });
                try
                {
                    for (int i = 0; i < 16; i += 4) Marshal.WriteInt32(output, i, 0);
                    var request = new DetourRequest<InitializeFixture> {
                        Name = "Private HP initializer fixture", Callback = callback,
                        TargetAddress = unchecked((ulong)target.EntryPoint.ToInt64())
                    };
                    // Use the production backend's exact policy, adjusting only startup thread suspension.
                    // No other thread can execute this private fixture, so disposal below is safe.
                    var backend = new NativeDetourBackend(HealthNativeObserver.Backend.Options with { SuspendThreadsDuringPatch = false });
                    Assert.AreEqual(DetourScheme.Indirect, backend.Options.AllowedSchemes);
                    Assert.IsFalse(backend.Options.FollowJumps);
                    hook = (NativeDetour<InitializeFixture>)backend.CreateDetour(in request);
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, HealthNativeObserver.Entry, false);
                    Assert.AreEqual(10, hook.DisplacedByteCount);
                    var invoke = Marshal.GetDelegateForFunctionPointer<InitializeFixture>(target.EntryPoint);
                    invoke(output, 7, -3);
                    Assert.AreEqual(0, calls); Assert.AreEqual(1, Marshal.ReadInt32(output, 8));
                    hook.Enable(); NativeIndirectEntry.Validate(hook, target.EntryPoint, HealthNativeObserver.Entry);
                    Marshal.WriteInt32(output, 8, 0);
                    invoke(output, -123, 85);
                    Assert.AreEqual(1, calls); Assert.AreEqual(output, observedManager);
                    Assert.AreEqual(-123, observedId); Assert.AreEqual(85, observedType);
                    Assert.AreEqual(-123, Marshal.ReadInt32(output)); Assert.AreEqual(85, Marshal.ReadInt32(output, 4));
                    Assert.AreEqual(1, Marshal.ReadInt32(output, 8), "The callback must reach its original exactly once.");
                    for (int offset = 10; offset < HealthNativeObserver.Entry.Length; offset++)
                    {
                        byte original = Marshal.ReadByte(target.EntryPoint, offset);
                        try
                        {
                            Marshal.WriteByte(target.EntryPoint, offset, (byte)(original ^ 1));
                            Assert.ThrowsExactly<InvalidOperationException>(() =>
                                NativeIndirectEntry.Validate(hook, target.EntryPoint, HealthNativeObserver.Entry),
                                "An altered continuation byte must fail validation at offset " + offset);
                        }
                        finally { Marshal.WriteByte(target.EntryPoint, offset, original); }
                    }
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, HealthNativeObserver.Entry);
                    GC.KeepAlive(invoke); GC.KeepAlive(callback);
                }
                finally { hook?.Dispose(); Marshal.FreeHGlobal(output); }
            }
        }

        private static byte[] AtRva(byte[] image, int rva, int length)
        {
            int pe = BitConverter.ToInt32(image, 0x3C);
            int sectionTable = pe + 24 + BitConverter.ToUInt16(image, pe + 20);
            for (int i = 0; i < BitConverter.ToUInt16(image, pe + 6); i++)
            {
                int section = sectionTable + i * 40, start = BitConverter.ToInt32(image, section + 12);
                int size = BitConverter.ToInt32(image, section + 16);
                if (rva < start || rva + length > start + size) continue;
                var result = new byte[length];
                Array.Copy(image, BitConverter.ToInt32(image, section + 20) + rva - start, result, 0, length);
                return result;
            }
            throw new InvalidOperationException("The audited HP initializer is outside native file sections.");
        }
    }
}
