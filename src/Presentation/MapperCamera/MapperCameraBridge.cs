using System;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Presentation
{
    // Function entry, not a CALL-site detour: only the matching mapper caller may
    // omit this void camera function. Every other call follows the full trampoline.
    internal sealed class MapperCameraBridge : IDetourIntermediaryFactory
    {
        private readonly ulong demand, mapperReturn;
        internal MapperCameraBridge(ulong demand, ulong mapperReturn)
        {
            if (demand == 0 || mapperReturn == 0) throw new ArgumentException("Complete mapper camera addresses are required.");
            this.demand = demand; this.mapperReturn = mapperReturn;
        }
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Mapper camera requires the audited Win64 trampoline contract.");
            ulong original = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(a => Emit(a, demand, mapperReturn, original));
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }
        internal static void Emit(Assembler a, ulong demand, ulong mapperReturn, ulong original)
        {
            var fallback = a.CreateLabel();
            a.pushfq(); a.push(r11);
            a.mov(r11, demand); a.cmp(__dword_ptr[r11], 0); a.je(fallback);
            a.mov(r11, mapperReturn); a.cmp(__qword_ptr[rsp + 16], r11); a.jne(fallback);
            a.pop(r11); a.popfq(); a.ret();
            a.Label(ref fallback); a.pop(r11); a.popfq(); a.jmp(original);
        }
        internal void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected, bool installed) where T : Delegate
        {
            var native = hook as NativeDetour<T>;
            if (expected == null || expected.Length < MapperCameraNative.DisplacedLength || native == null ||
                native.IsInstalled != installed || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != MapperCameraNative.DisplacedLength ||
                native.TargetAddress != unchecked((ulong)target.ToInt64()) || native.ChainDepth != 1 ||
                native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero ||
                native.HookEntryPointAddress == IntPtr.Zero || native.OriginalEntryPointAddress != native.TrampolineAddress ||
                native.TrampolineSize < MapperCameraNative.DisplacedLength + 2)
                throw new InvalidOperationException("Mapper camera Indirect/7 boundary changed.");
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(expected));
            decoder.IP = unchecked((ulong)target.ToInt64());
            var first = decoder.Decode(); var second = decoder.Decode();
            if (first.Code != Code.Movsxd_r64_rm32 || first.Op0Register != Register.RAX ||
                first.Op1Register != Register.R8D || first.Length != 3 ||
                second.Code != Code.Lea_r64_m || second.Op0Register != Register.R8 ||
                second.MemoryBase != Register.RAX || second.MemoryIndex != Register.RAX ||
                second.MemoryIndexScale != 2 || second.MemoryDisplacement64 != 0 || second.Length != 4)
                throw new InvalidOperationException("Mapper camera original entry instructions changed.");
            for (int i = 0; i < MapperCameraNative.DisplacedLength; i++)
            {
                if (Marshal.ReadByte(native.TrampolineAddress, i) != expected[i])
                    throw new InvalidOperationException("Mapper camera displaced entry changed.");
                if (!installed && Marshal.ReadByte(target, i) != expected[i])
                    throw new InvalidOperationException("Mapper camera entry is already occupied.");
            }
            if (installed && (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress || Marshal.ReadByte(target, 6) != 0x90))
                throw new InvalidOperationException("Mapper camera published entry changed.");
            // Preserve an adjacent hook at the continuation; the pristine full function
            // is validated separately before preparation.
            int displaced = MapperCameraNative.DisplacedLength;
            bool adjacent = Marshal.ReadByte(target, displaced) == 0xE9 ||
                Marshal.ReadByte(target, displaced) == 0xFF && Marshal.ReadByte(target, displaced + 1) == 0x25;
            if (!adjacent)
                for (int i = displaced; i < expected.Length; i++)
                    if (Marshal.ReadByte(target, i) != expected[i])
                        throw new InvalidOperationException("Mapper camera continuation changed.");
            var bytes = new byte[Math.Min(14, native.TrampolineSize - displaced)];
            Marshal.Copy(native.TrampolineAddress + displaced, bytes, 0, bytes.Length);
            decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = unchecked((ulong)(native.TrampolineAddress + displaced).ToInt64());
            var jump = decoder.Decode();
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 ||
                jump.NearBranchTarget != unchecked((ulong)(target + displaced).ToInt64()))
                throw new InvalidOperationException("Mapper camera trampoline continuation changed.");
            var a = new Assembler(64); Emit(a, demand, mapperReturn, unchecked((ulong)native.TrampolineAddress.ToInt64()));
            using (var stream = new MemoryStream())
            {
                a.Assemble(new StreamCodeWriter(stream), unchecked((ulong)native.HookEntryPointAddress.ToInt64()));
                NativeSites.NativeIndirectEntry.VerifyBytes(native.HookEntryPointAddress, stream.ToArray());
            }
        }
    }
}
