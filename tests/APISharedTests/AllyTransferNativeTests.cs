using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using APIShared.Economy;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APISharedTests
{
    [TestClass]
    public sealed class AllyTransferNativeTests
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);

        [TestMethod]
        public void CanonicalBodiesAndExactEntryBoundaryRemainTheAuditedContract()
        {
            // Exact native bytes are the supported binary contract, rather than mutable source wording.
            using (var sha = SHA256.Create())
            {
                Assert.AreEqual("206BDE68C11D963B744BF671B185935EC32EF05CB1EBB3A479E308095CC03612",
                    BitConverter.ToString(sha.ComputeHash(AllyTransfersNative.TransferBytes)).Replace("-", ""));
                Assert.AreEqual("978F663FC2E2F1FAF8CF89FE73BFD17ABD9FA728F13A57AF1325828DB9E1DDA9",
                    BitConverter.ToString(sha.ComputeHash(AllyTransfersNative.CapacityBytes)).Replace("-", ""));
            }
            byte[] image = new byte[AllyTransfersNative.TransferRva + AllyTransfersNative.TransferBytes.Length];
            Array.Copy(AllyTransfersNative.TransferBytes, 0, image, AllyTransfersNative.TransferRva, AllyTransfersNative.TransferBytes.Length);
            Array.Copy(AllyTransfersNative.CapacityBytes, 0, image, AllyTransfersNative.CapacityRva, AllyTransfersNative.CapacityBytes.Length);
            AllyTransfersNative.ValidatePristine(image);
            image[AllyTransfersNative.TransferRva + 9] ^= 1;
            Assert.ThrowsExactly<InvalidOperationException>(() => AllyTransfersNative.ValidatePristine(image));
            image[AllyTransfersNative.TransferRva + 9] ^= 1;
            image[AllyTransfersNative.CapacityRva + 70] ^= 1;
            Assert.ThrowsExactly<InvalidOperationException>(() => AllyTransfersNative.ValidatePristine(image));

            var decoder = Decoder.Create(64, new ByteArrayCodeReader(AllyTransfersNative.TransferBytes));
            ulong start = 0x180000000UL + AllyTransfersNative.TransferRva;
            decoder.IP = start;
            while (decoder.IP < start + (uint)AllyTransfersNative.TransferBytes.Length)
            {
                decoder.Decode(out Instruction instruction);
                Assert.AreNotEqual(Code.INVALID, instruction.Code);
                if (instruction.Op0Kind == OpKind.NearBranch64)
                    Assert.IsFalse(instruction.NearBranchTarget > start &&
                        instruction.NearBranchTarget < start + AllyTransfersNative.DisplacedLength,
                        "No original branch may enter the middle of the displaced prologue.");
            }
        }

        [TestMethod]
        public void ProductionBackendPreservesAllFourArgumentsAndTheNineByteContinuation()
        {
            IntPtr code = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
            IntPtr observed = Marshal.AllocHGlobal(16);
            Assert.AreNotEqual(IntPtr.Zero, code);
            NativeDetour<AllyTransfersNative.TransferCall> probe = null;
            AllyTransfersNative.TransferCall callback = null;
            try
            {
                // The actual four-instruction Vanilla prologue, followed by an isolated leaf
                // that writes every integer argument and balances its stack/nonvolatile pushes.
                var fixture = new List<byte>();
                for (int i = 0; i < AllyTransfersNative.DisplacedLength; i++) fixture.Add(AllyTransfersNative.TransferBytes[i]);
                fixture.AddRange(new byte[] { 0x48, 0xB8 });
                fixture.AddRange(BitConverter.GetBytes(observed.ToInt64()));
                fixture.AddRange(new byte[] {
                    0x89,0x08, 0x89,0x50,0x04, 0x44,0x89,0x40,0x08, 0x44,0x89,0x48,0x0C,
                    0x48,0x83,0xC4,0x40, 0x41,0x5C, 0x5E,0x5D,0xC3 });
                byte[] bytes = fixture.ToArray();
                Marshal.Copy(bytes, 0, code, bytes.Length);
                int callbacks = 0;
                callback = (recipient, good, amount, sender) => { callbacks++; probe.Original(recipient, good, amount, sender); };
                var request = new DetourRequest<AllyTransfersNative.TransferCall> {
                    Name = "Ally transfer private backend fixture", TargetAddress = unchecked((ulong)code.ToInt64()), Callback = callback };
                // Production installs in LibraryLoaded's no-suspension window. This private
                // buffer likewise has no concurrent executor, so its patch needs no suspension.
                var backend = new NativeDetourBackend(AllyTransfersNative.DetourBackend.Options with {
                    SuspendThreadsDuringPatch = false });
                probe = backend.CreateDetour(in request) as NativeDetour<AllyTransfersNative.TransferCall>;
                Assert.IsNotNull(probe); Assert.AreEqual(DetourScheme.Indirect, probe.Scheme);
                Assert.AreEqual(9, probe.DisplacedByteCount); Assert.IsFalse(probe.IsInstalled);
                probe.Enable();
                AllyTransfersNative.Validate(probe, code, bytes);
                var call = (AllyTransfersNative.TransferCall)Marshal.GetDelegateForFunctionPointer(code, typeof(AllyTransfersNative.TransferCall));
                foreach (int amount in new[] { 0, 1, short.MaxValue, int.MaxValue, int.MinValue })
                {
                    call(8, 15, amount, 3);
                    Assert.AreEqual(8, Marshal.ReadInt32(observed)); Assert.AreEqual(15, Marshal.ReadInt32(observed, 4));
                    Assert.AreEqual(amount, Marshal.ReadInt32(observed, 8)); Assert.AreEqual(3, Marshal.ReadInt32(observed, 12));
                }
                Assert.AreEqual(5, callbacks);
                Marshal.WriteByte(code, 9, 0x90);
                Assert.ThrowsExactly<InvalidOperationException>(() => AllyTransfersNative.Validate(probe, code, bytes));
                Marshal.WriteByte(code, 9, bytes[9]);
            }
            finally
            {
                // This fixture was never a live published game hook; no thread can execute it here.
                probe?.Dispose();
                VirtualFree(code, UIntPtr.Zero, 0x8000); Marshal.FreeHGlobal(observed); GC.KeepAlive(callback);
            }
        }
    }
}
