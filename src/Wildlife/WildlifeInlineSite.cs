using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.Wildlife
{
    // Checks shared by the wildlife inline stubs (Hunter sites, owner gates, herd flight gates).
    // X64InlineHook writes jmp [rip+0] with the stub address behind it and pads the rest of the span
    // with NOPs; it appends its continuation jump after the custom generator.
    internal static unsafe class WildlifeInlineSite
    {
        internal delegate void Generator(Assembler a, ReadOnlySpan<Instruction> original);

        // Signature, boundary and span checks run against the unmodified load image; live only the own
        // span must be unchanged, while a foreign hook may already start at the following boundary.
        internal static void CheckSite(CrusaderLibraryLoadContext context, ulong image, int rva, byte[] bytes, byte[] continuation, string label)
        {
            ReadOnlySpan<byte> memory = context.Memory;
            if (rva + bytes.Length + continuation.Length > memory.Length ||
                !memory.Slice(rva, bytes.Length).SequenceEqual(new ReadOnlySpan<byte>(bytes)) ||
                !memory.Slice(rva + bytes.Length, continuation.Length).SequenceEqual(new ReadOnlySpan<byte>(continuation)))
                throw new InvalidOperationException(label + " snapshot differs at 0x" + rva.ToString("X") + ".");
            var live = new ReadOnlySpan<byte>((void*)(image + (uint)rva), bytes.Length);
            if (!live.SequenceEqual(new ReadOnlySpan<byte>(bytes)))
                throw new InvalidOperationException(label + " is already patched at 0x" + rva.ToString("X") + ".");
            var next = new ReadOnlySpan<byte>((void*)(image + (uint)(rva + bytes.Length)), Math.Max(continuation.Length, 6));
            bool foreignHook = next[0] == 0xE9 || next[0] == 0xFF && next[1] == 0x25;
            if (!foreignHook && !next.Slice(0, continuation.Length).SequenceEqual(new ReadOnlySpan<byte>(continuation)))
                throw new InvalidOperationException(label + " continuation changed at 0x" + (rva + bytes.Length).ToString("X") + ".");
        }

        internal static void ProbeLength(ulong image, int rva, int length, string label)
        {
            using (var probe = new X64InlineHook(image + (uint)rva, length))
                if (probe.DisplacedByteCount != length)
                    throw new InvalidOperationException(label + " span differs at 0x" + rva.ToString("X") + ": " + probe.DisplacedByteCount + ".");
        }

        internal static void Verify(HookHandle<X64InlineHook> handle, ulong image, int rva, int length, byte[] bytes, Generator generate,
            bool installed, string label)
        {
            var hook = handle.Hook;
            if (!handle.Success || hook == null || hook.IsInstalled != installed || hook.TargetAddress != image + (uint)rva ||
                hook.DisplacedByteCount != length || hook.StubAddress == IntPtr.Zero)
                throw new InvalidOperationException(label + " backend contract differs at 0x" + rva.ToString("X") + ".");
            byte[] expected = ExpectedStub(image, rva, length, bytes, generate, unchecked((ulong)hook.StubAddress.ToInt64()));
            var actual = new byte[expected.Length];
            Marshal.Copy(hook.StubAddress, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException(label + " stub differs at 0x" + rva.ToString("X") + ".");
            if (!installed) return;
            IntPtr target = (IntPtr)hook.TargetAddress;
            if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 || Marshal.ReadInt32(target, 2) != 0 ||
                Marshal.ReadIntPtr(target, 6) != hook.StubAddress)
                throw new InvalidOperationException(label + " jump differs at 0x" + rva.ToString("X") + ".");
            for (int i = 14; i < length; i++)
                if (Marshal.ReadByte(target, i) != 0x90)
                    throw new InvalidOperationException(label + " padding differs at 0x" + rva.ToString("X") + ".");
        }

        internal static byte[] ExpectedStub(ulong image, int rva, int length, byte[] bytes, Generator generate, ulong stub)
        {
            var a = new Assembler(64);
            var original = Economy.WorkerProductionBonusNativeDefinition.Decode(bytes, image + (uint)rva);
            generate(a, original);
            a.AddUnrestrictedJmp(image + (uint)(rva + length));
            return Economy.WorkerProductionBonusNativeAssembly.Assemble(a, stub);
        }

        internal static byte[] Hex(string text) => Economy.WorkerProductionBonusNativeDefinition.Hex(text.Replace(" ", ""));
    }
}
