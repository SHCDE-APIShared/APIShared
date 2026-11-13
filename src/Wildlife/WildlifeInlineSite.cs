using System;
using APIShared.Internal;
using Iced.Intel;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;

namespace APIShared.Wildlife
{
    // Wildlife diagnostics (Hunter sites, owner gates, herd flight gates) over the shared inline-stub
    // checks of NativeInlineStub: every message names the feature label and the site RVA.
    internal static unsafe class WildlifeInlineSite
    {
        internal delegate void Generator(Assembler a, ReadOnlySpan<Instruction> original);

        // Kept verbatim from the decoder these sites used before; their constant bytes always decode.
        private const string BoundaryError = "Worker production native boundary differs.";

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
            if (!NativeInlineStub.AcceptsLiveBoundary(next, continuation))
                throw new InvalidOperationException(label + " continuation changed at 0x" + (rva + bytes.Length).ToString("X") + ".");
        }

        internal static void ProbeLength(ulong image, int rva, int length, string label)
        {
            int displaced = NativeInlineStub.ProbeDisplacement(image + (uint)rva, length);
            if (displaced != length)
                throw new InvalidOperationException(label + " span differs at 0x" + rva.ToString("X") + ": " + displaced + ".");
        }

        internal static void Verify(HookHandle<X64InlineHook> handle, ulong image, int rva, int length, byte[] bytes, Generator generate,
            bool installed, string label) =>
            NativeInlineStub.Verify(handle, image + (uint)rva, length, installed,
                stub => ExpectedStub(image, rva, length, bytes, generate, stub),
                check => label + Failure(check) + " differs at 0x" + rva.ToString("X") + ".");

        private static string Failure(NativeInlineStub.Check check)
        {
            switch (check)
            {
                case NativeInlineStub.Check.Backend: return " backend contract";
                case NativeInlineStub.Check.Stub: return " stub";
                case NativeInlineStub.Check.Jump: return " jump";
                default: return " padding";
            }
        }

        internal static byte[] ExpectedStub(ulong image, int rva, int length, byte[] bytes, Generator generate, ulong stub) =>
            NativeInlineStub.ExpectedStub(image + (uint)rva, bytes, length, (a, original, back) => generate(a, original), stub, BoundaryError);

        internal static byte[] Hex(string text) => NativeInlineStub.Hex(text.Replace(" ", ""));
    }
}
