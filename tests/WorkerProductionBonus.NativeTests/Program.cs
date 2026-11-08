using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Iced.Intel;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using static Iced.Intel.AssemblerRegisters;

// Private executable fixtures only: this program never loads or patches the running game.
// Actual native yield bytes and production generators execute here. Native section copying
// is not a live save/load or multiplayer acceptance test.
internal static partial class Program
{
    private const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
    private const int YieldRva = 0x18D940, YieldLength = 0xA2;
    private const int UnitId = 1, UnitStride = 0x490, PlayerStride = 0x583C;
    private const int OwnerRva = 0x67E8AEE, RemainderRva = 0x67E8D5C, ExtraRva = 0x67E8D5E;
    private const int ProductivityRva = 0x379CEEC, ModeRva = 0x8574B90;
    private static int checks, nextCodeOffset;
    private static byte[] native;
    private static IntPtr image;
    private static int imageSize;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Yield(int unit, int amount, int eligible);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeChecksum(IntPtr context, int length, IntPtr bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Invoke();
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr p, UIntPtr n, uint allocation, uint protection);
    [DllImport("kernel32", SetLastError = true)] private static extern bool VirtualFree(IntPtr p, UIntPtr n, uint mode);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("Windows x64 native execution is required.");
            if (args.Length != 1) throw new ArgumentException("Supply the canonical installed CrusaderDE.dll path.");
            native = File.ReadAllBytes(args[0]);
            Check(Convert.ToHexString(SHA256.HashData(native)) == NativeHash, "Audited native identity mismatch.");
            TestRegistry();
            TestCompilerAndProfiles();
            TestStateContracts();
            MapPrivateImage();
            try
            {
                TestOriginalYield();
                TestRawRecordPersistence();
                TestProductionGenerators();
                TestStateChecksum();
                TestHotProductionCore();
                TestMillerOutput();
                TestHunterSites();
                TestHunterOwnerGates();
                TestWeaponProductionAdapters();
            }
            finally { VirtualFree(image, UIntPtr.Zero, 0x8000); }
            Console.WriteLine($"PASS: {checks} worker-production native assertions. Private native execution; actual game, save/restart and host/client acceptance remain separate.");
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
    }

    private static void MapPrivateImage()
    {
        int pe = BitConverter.ToInt32(native, 0x3C);
        imageSize = BitConverter.ToInt32(native, pe + 24 + 56);
        // Near fixture pages are outside the PE image but in the same reserved allocation.
        nextCodeOffset = (imageSize + 0xFFFF) & ~0xFFFF;
        image = VirtualAlloc(IntPtr.Zero, (UIntPtr)(nextCodeOffset + 0x400000), 0x3000, 0x40);
        if (image == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Copy(0, native.Take(Math.Min(native.Length, 0x1000)).ToArray());
        int sections = BitConverter.ToUInt16(native, pe + 6);
        int table = pe + 24 + BitConverter.ToUInt16(native, pe + 20);
        for (int index = 0; index < sections; index++)
        {
            int s = table + index * 40;
            int rva = BitConverter.ToInt32(native, s + 12), size = BitConverter.ToInt32(native, s + 16), offset = BitConverter.ToInt32(native, s + 20);
            if (size > 0) Copy(rva, native.Skip(offset).Take(size).ToArray());
        }
        // The copied yield helper has no imports/callees and calculates its image base with LEA.
        Check(At(YieldRva, YieldLength).SequenceEqual(Read(YieldRva, YieldLength)), "Exact original yield body copied.");
        FlushInstructionCache(GetCurrentProcess(), image, (UIntPtr)imageSize);
    }

    private static byte[] At(int rva, int length)
    {
        int pe = BitConverter.ToInt32(native, 0x3C), count = BitConverter.ToUInt16(native, pe + 6);
        int table = pe + 24 + BitConverter.ToUInt16(native, pe + 20);
        for (int index = 0; index < count; index++)
        {
            int s = table + index * 40, va = BitConverter.ToInt32(native, s + 12), size = BitConverter.ToInt32(native, s + 16);
            if (rva >= va && rva + length <= va + size)
                return native.Skip(BitConverter.ToInt32(native, s + 20) + rva - va).Take(length).ToArray();
        }
        throw new InvalidOperationException("RVA is outside file-backed PE sections.");
    }
    private static byte[] Read(int rva, int length) { var bytes = new byte[length]; Marshal.Copy(image + rva, bytes, 0, length); return bytes; }
    private static void Copy(int rva, byte[] bytes) => Marshal.Copy(bytes, 0, image + rva, bytes.Length);
    private static void Put(int rva, int value) => Marshal.WriteInt32(image, rva, value);
    private static void Word(int rva, int value) => Marshal.WriteInt16(image, rva, unchecked((short)value));
    private static short Word(int rva) => Marshal.ReadInt16(image, rva);
    private static Yield YieldFunction => Marshal.GetDelegateForFunctionPointer<Yield>(image + YieldRva);

    private static void ResetYield(int productivity, int mode, int remainder, int unitId = UnitId, int owner = 1)
    {
        Word(OwnerRva + unitId * UnitStride, owner);
        Put(ProductivityRva + owner * PlayerStride, productivity);
        Put(ModeRva, mode);
        Word(RemainderRva + unitId * UnitStride, remainder);
        Word(ExtraRva + unitId * UnitStride, 0x1234);
    }

    private static (int Amount, int Remainder, int Extra) ReferenceYield(int productivity, int mode, int eligible, int amount, int remainder, int percent)
    {
        // Independent specification, compared with the real machine body rather than a
        // reimplemented "native" function. C# integer division has the native truncation rule.
        int fractional = (Math.Max(100, productivity) - 100 + (mode != 0 && eligible != 0 ? percent : 0)) * amount + remainder;
        int extra = fractional / 100;
        return (amount + extra, fractional - extra * 100, extra);
    }

    private static void TestOriginalYield()
    {
        var yield = YieldFunction;
        foreach (int productivity in new[] { 50, 100, 110, 150 })
        foreach (int mode in new[] { 0, 1, 2, 99 })
        foreach (int eligible in new[] { 0, 1 })
        foreach (int amount in new[] { 1, 2, 3, 6, 8, 12, 100 })
        foreach (int remainder in new[] { -99, -50, 0, 50, 99 })
        {
            ResetYield(productivity, mode, remainder);
            var expected = ReferenceYield(productivity, mode, eligible, amount, remainder, 50);
            Check(yield(UnitId, amount, eligible) == expected.Amount, "Original yield result.");
            Check(Word(RemainderRva + UnitId * UnitStride) == expected.Remainder, "Original signed fractional remainder.");
            Check(Word(ExtraRva + UnitId * UnitStride) == expected.Extra, "Original signed extra output.");
        }
    }

    private static void TestRawRecordPersistence()
    {
        // Raw section 3015 and Chore 56 both copy this exact record. This verifies the
        // byte/offset contract and resumed native arithmetic, not file I/O or Steam delivery.
        const int wireOrigin = 0x67E8A58, wireRemainder = 0x304, wireExtra = 0x306;
        Check(wireOrigin + wireRemainder == RemainderRva && wireOrigin + wireExtra == ExtraRva, "Typed/wire field origins.");
        byte[] descriptor = At(0x337030, 24);
        Check(BitConverter.ToInt64(descriptor, 0) == 0x1867E8A58L && BitConverter.ToInt32(descriptor, 12) == 10000 * UnitStride && BitConverter.ToInt16(descriptor, 18) == 3015,
            "Actual current native save table stores all complete unit wire records.");
        foreach (int remainder in new[] { -99, -50, -1, 0, 1, 50, 99 })
        {
            ResetYield(110, 99, remainder);
            Word(ExtraRva + UnitId * UnitStride, -1);
            int origin = wireOrigin + UnitId * UnitStride;
            byte[] saved = Read(origin, UnitStride);
            var checksum = Marshal.GetDelegateForFunctionPointer<NativeChecksum>(image + 0x78E0);
            // The real MP unit checksum hashes wire +0x8C for 0x404 bytes. Its temporary
            // eight-byte mask is at wire +0x41C, outside both production fields.
            int beforeHash = checksum(IntPtr.Zero, 0x404, image + origin + 0x8C);
            Check(BitConverter.ToInt16(saved, wireRemainder) == remainder && BitConverter.ToInt16(saved, wireExtra) == -1, "Raw archive preserves signed production bits.");
            int first = YieldFunction(UnitId, 1, 1), nextRemainder = Word(RemainderRva + UnitId * UnitStride);
            Copy(origin, saved);
            Check(checksum(IntPtr.Zero, 0x404, image + origin + 0x8C) == beforeHash, "Native checksum unchanged by raw record roundtrip.");
            Check(YieldFunction(UnitId, 1, 1) == first && Word(RemainderRva + UnitId * UnitStride) == nextRemainder, "Resumed native yield after raw record roundtrip.");
        }
        ResetYield(100, 99, 0);
        Word(ExtraRva + UnitId * UnitStride, 0);
        var hash = Marshal.GetDelegateForFunctionPointer<NativeChecksum>(image + 0x78E0);
        IntPtr interval = image + wireOrigin + UnitId * UnitStride + 0x8C;
        int neutralHash = hash(IntPtr.Zero, 0x404, interval);
        Word(RemainderRva + UnitId * UnitStride, -50);
        Check(hash(IntPtr.Zero, 0x404, interval) != neutralHash, "Negative remainder contributes to the actual native checksum.");
    }

    private static partial void TestProductionGenerators();

    private sealed class Code : IDisposable
    {
        internal readonly IntPtr Address;
        internal readonly byte[] Bytes;
        internal Code(Action<Assembler> generate)
        {
            Address = image + nextCodeOffset;
            nextCodeOffset += 4096;
            var assembler = new Assembler(64);
            generate(assembler);
            using var stream = new MemoryStream();
            assembler.Assemble(new StreamCodeWriter(stream), unchecked((ulong)Address.ToInt64()));
            Bytes = stream.ToArray();
            Check(Bytes.Length <= 4096, "Fixture page capacity.");
            Marshal.Copy(Bytes, 0, Address, Bytes.Length);
            if (!FlushInstructionCache(GetCurrentProcess(), Address, (UIntPtr)Bytes.Length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { } // Main frees the entire private allocation after all hooks are disposed.
    }
}
