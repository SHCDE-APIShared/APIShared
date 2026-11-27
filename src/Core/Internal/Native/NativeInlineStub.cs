using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;

namespace APIShared.Internal
{
    // Shared checks of the purely native X64InlineHook stubs (production, AI sale, weapon workshops,
    // wildlife). The checked RedBird backend writes `jmp [rip+0]` (FF 25 00000000) plus the 8-byte stub
    // address at the span start and pads the rest of the span with NOPs; the stub is the custom generator
    // followed by the backend's own continuation jump. Every caller keeps its own check order and
    // diagnostics: these helpers return results or take the caller's message text.
    internal static unsafe class NativeInlineStub
    {
        internal const int JumpLength = 14;

        // Generators are assembled once before any claim or transaction exists, at this offset from the
        // image base: an address near the image like the stubs the backend allocates later, so encoding
        // failures stop the installation before anything is patched. The result is discarded.
        internal const ulong PreflightStubOffset = 0x20000000;

        internal delegate void Generator(Assembler assembler, ReadOnlySpan<Instruction> original, ulong back);

        internal enum Check { Backend, Stub, Jump, Padding }

        internal static byte[] Hex(string text)
        {
            if (text == null || (text.Length & 1) != 0) throw new ArgumentException("Invalid native hex.");
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return bytes;
        }

        // Complete instructions only; boundaryError keeps each feature's own diagnostic.
        internal static Instruction[] Decode(byte[] bytes, ulong address, string boundaryError)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
            var instructions = new List<Instruction>();
            while (decoder.IP < address + (uint)bytes.Length)
            {
                decoder.Decode(out var instruction);
                if (instruction.IsInvalid || instruction.Length <= 0 || decoder.IP > address + (uint)bytes.Length)
                    throw new InvalidOperationException(boundaryError);
                instructions.Add(instruction);
            }
            return instructions.ToArray();
        }

        internal static byte[] Assemble(Assembler assembler, ulong address)
        { using (var stream = new MemoryStream()) { assembler.Assemble(new StreamCodeWriter(stream), address); return stream.ToArray(); } }

        // The backend's stub for a span at entry: the generator over the decoded original instructions,
        // then X64InlineHook's own continuation jump (unreachable where the generator exits explicitly).
        internal static byte[] ExpectedStub(ulong entry, byte[] original, int length, Generator generate, ulong stub, string boundaryError)
        {
            var assembler = new Assembler(64);
            ulong back = entry + (uint)length;
            generate(assembler, Decode(original, entry, boundaryError), back);
            assembler.AddUnrestrictedJmp(back);
            return Assemble(assembler, stub);
        }

        internal static bool SnapshotMatches(ReadOnlySpan<byte> memory, int rva, ReadOnlySpan<byte> expected) =>
            rva >= 0 && rva <= memory.Length - expected.Length && memory.Slice(rva, expected.Length).SequenceEqual(expected);

        internal static byte[] ReadLive(IntPtr module, int rva, int length)
        { var bytes = new byte[length]; Marshal.Copy(module + rva, bytes, 0, length); return bytes; }

        // Live at a boundary proved on the load image another owner's hook may already begin (jmp rel32 or
        // jmp [rip+disp32]); our code only jumps to that boundary and so enters it unchanged.
        // live may extend past the original boundary instruction; only its first bytes are compared.
        internal static bool AcceptsLiveBoundary(ReadOnlySpan<byte> live, ReadOnlySpan<byte> original) =>
            live.Length >= original.Length && live.Slice(0, original.Length).SequenceEqual(original) || StartsWithForeignJump(live);

        internal static bool StartsWithForeignJump(ReadOnlySpan<byte> live) =>
            live.Length >= 1 && live[0] == 0xE9 || live.Length >= 2 && live[0] == 0xFF && live[1] == 0x25;

        // HookSize is a minimum: the backend's own length probe, created and disposed without patching.
        internal static int ProbeDisplacement(ulong entry, int length)
        { using (var probe = new X64InlineHook(entry, length)) return probe.DisplacedByteCount; }

        internal static bool IsPrepared(HookHandle<X64InlineHook> handle, bool installed, ulong entry, int length)
        {
            var hook = handle.Hook;
            return handle.Success && hook.IsInstalled == installed && hook.TargetAddress == entry &&
                hook.DisplacedByteCount == length && hook.StubAddress != IntPtr.Zero;
        }

        internal static bool StubMatches(IntPtr stub, byte[] expected)
        {
            var actual = new byte[expected.Length];
            Marshal.Copy(stub, actual, 0, actual.Length);
            return actual.AsSpan().SequenceEqual(expected);
        }

        internal static bool JumpMatches(IntPtr entry, IntPtr stub) =>
            Marshal.ReadByte(entry) == 0xFF && Marshal.ReadByte(entry, 1) == 0x25 && Marshal.ReadInt32(entry, 2) == 0 &&
            Marshal.ReadIntPtr(entry, 6) == stub;

        internal static bool PaddingMatches(IntPtr entry, int length)
        {
            for (int i = JumpLength; i < length; i++)
                if (Marshal.ReadByte(entry, i) != 0x90) return false;
            return true;
        }

        // Prepared (installed == false) or published state of one span, in the order backend contract,
        // generated stub, installed jump, NOP padding. expectedStub receives the actual stub address.
        internal static void Verify(HookHandle<X64InlineHook> handle, ulong entry, int length, bool installed,
            Func<ulong, byte[]> expectedStub, Func<Check, string> failure)
        {
            if (!IsPrepared(handle, installed, entry, length)) throw new InvalidOperationException(failure(Check.Backend));
            var hook = handle.Hook;
            if (!StubMatches(hook.StubAddress, expectedStub(unchecked((ulong)hook.StubAddress.ToInt64()))))
                throw new InvalidOperationException(failure(Check.Stub));
            if (!installed) return;
            IntPtr target = (IntPtr)hook.TargetAddress;
            if (!JumpMatches(target, hook.StubAddress)) throw new InvalidOperationException(failure(Check.Jump));
            if (!PaddingMatches(target, length)) throw new InvalidOperationException(failure(Check.Padding));
        }
    }
}
