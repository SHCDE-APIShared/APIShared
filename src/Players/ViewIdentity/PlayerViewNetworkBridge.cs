using System;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.PlayerControl
{
    internal enum PlayerViewNetworkSite
    {
        Timing,
        HeartbeatProgress,
        HeartbeatAcknowledgement,
        HeartbeatTimestamp
    }

    // Substitute only the participant index consumed by network bookkeeping. The
    // selected native view stays untouched throughout input, simulation and rendering.
    internal sealed class PlayerViewNetworkBridge : IDetourIntermediaryFactory
    {
        private readonly PlayerViewNetworkSite site;
        private readonly ulong protectedPlayerCell, nativeView, continuation;

        internal PlayerViewNetworkBridge(PlayerViewNetworkSite site, ulong protectedPlayerCell,
            ulong nativeView, ulong continuation)
        {
            DisplacedLength(site); // Reject an unknown site before preparing executable code.
            if (protectedPlayerCell == 0 || nativeView == 0 || continuation == 0)
                throw new ArgumentException("Complete network identity bridge addresses are required.");
            this.site = site; this.protectedPlayerCell = protectedPlayerCell;
            this.nativeView = nativeView; this.continuation = continuation;
        }

        internal static int DisplacedLength(PlayerViewNetworkSite site)
        {
            switch (site)
            {
                case PlayerViewNetworkSite.Timing: return 6;
                case PlayerViewNetworkSite.HeartbeatProgress:
                case PlayerViewNetworkSite.HeartbeatAcknowledgement:
                case PlayerViewNetworkSite.HeartbeatTimestamp: return 7;
                default: throw new ArgumentOutOfRangeException(nameof(site));
            }
        }

        internal static int WindowLength(PlayerViewNetworkSite site) =>
            DisplacedLength(site) + (site == PlayerViewNetworkSite.Timing ? 3 :
                site == PlayerViewNetworkSite.HeartbeatTimestamp ? 7 : 6);

        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Network identity needs the audited Win64 trampoline contract.");
            ulong original = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(a => Emit(a, site, protectedPlayerCell, original, continuation));
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }

        internal void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected,
            bool installed = true) where T : Delegate
        {
            var native = hook as NativeDetour<T>;
            ulong address = unchecked((ulong)target.ToInt64());
            int displaced = DisplacedLength(site);
            if (expected == null || expected.Length != WindowLength(site) || native == null ||
                native.IsInstalled != installed || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != displaced || native.TargetAddress != address ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero ||
                native.TrampolineAddress == IntPtr.Zero || native.HookEntryPointAddress == IntPtr.Zero ||
                native.OriginalEntryPointAddress != native.TrampolineAddress ||
                native.TrampolineSize < displaced + 2 || continuation != address + (ulong)displaced)
                throw new InvalidOperationException("Network identity Indirect load boundary changed.");

            ValidateDisplaced(expected, address);
            ValidateContinuation(expected, address);
            if (!installed)
            {
                for (int index = 0; index < displaced; index++)
                    if (Marshal.ReadByte(target, index) != expected[index])
                        throw new InvalidOperationException("Network identity live load boundary is already occupied.");
            }
            else
            {
                if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                    target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                    Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                    throw new InvalidOperationException("Network identity patch destination changed.");
                for (int index = 6; index < displaced; index++)
                    if (Marshal.ReadByte(target, index) != 0x90)
                        throw new InvalidOperationException("Network identity patch padding changed.");
            }

            // Another owner may start exactly at the continuation, but may not alter
            // any byte inside this service's exclusive instruction span.
            byte first = Marshal.ReadByte(target, displaced);
            bool adjacentHook = first == 0xE9 || first == 0xFF && Marshal.ReadByte(target, displaced + 1) == 0x25;
            if (!adjacentHook)
                for (int index = displaced; index < expected.Length; index++)
                    if (Marshal.ReadByte(target, index) != expected[index])
                        throw new InvalidOperationException("Network identity continuation changed.");

            var relocated = new byte[Math.Min(native.TrampolineSize, displaced + 14)];
            Marshal.Copy(native.TrampolineAddress, relocated, 0, relocated.Length);
            ulong trampoline = unchecked((ulong)native.TrampolineAddress.ToInt64());
            ValidateDisplaced(relocated, trampoline);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(relocated)); decoder.IP = trampoline;
            decoder.Decode();
            var jump = decoder.Decode();
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 ||
                jump.NearBranchTarget != continuation)
                throw new InvalidOperationException("Network identity trampoline continuation changed.");

            var bridge = new Assembler(64);
            Emit(bridge, site, protectedPlayerCell, trampoline, continuation);
            using (var stream = new MemoryStream())
            {
                bridge.Assemble(new StreamCodeWriter(stream), unchecked((ulong)native.HookEntryPointAddress.ToInt64()));
                NativeSites.NativeIndirectEntry.VerifyBytes(native.HookEntryPointAddress, stream.ToArray());
            }
        }

        private void ValidateDisplaced(byte[] bytes, ulong ip)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = ip;
            var load = decoder.Decode();
            bool valid = site == PlayerViewNetworkSite.Timing
                ? load.Code == Code.Mov_r32_rm32 && load.Op0Register == Register.EDX &&
                  load.Op1Kind == OpKind.Memory && load.IsIPRelativeMemoryOperand &&
                  load.IPRelativeMemoryAddress == nativeView && load.Length == 6
                : load.Code == Code.Movsxd_r64_rm32 && load.Op0Register == Register.RCX &&
                  load.Op1Kind == OpKind.Memory && load.MemoryBase == Register.RBX &&
                  load.MemoryIndex == Register.None && load.MemoryDisplacement64 == 0x36FA50 && load.Length == 7;
            if (!valid) throw new InvalidOperationException("Network identity original player-index load changed.");
        }

        private void ValidateContinuation(byte[] expected, ulong ip)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(expected)); decoder.IP = ip;
            decoder.Decode();
            var next = decoder.Decode();
            bool valid;
            if (site == PlayerViewNetworkSite.Timing)
                valid = next.Code == Code.Mov_r64_rm64 && next.Op0Register == Register.RCX &&
                    next.Op1Register == Register.R14 && next.Length == 3;
            else if (site == PlayerViewNetworkSite.HeartbeatTimestamp)
                valid = next.Code == Code.Mov_rm32_r32 && next.Op0Kind == OpKind.Memory &&
                    next.MemoryBase == Register.RBX && next.MemoryIndex == Register.RCX &&
                    next.MemoryIndexScale == 4 && next.MemoryDisplacement64 == 0x14D078 &&
                    next.Op1Register == Register.EAX && next.Length == 7;
            else
                valid = next.Code == Code.Mov_r32_rm32 && next.Op0Register == Register.EAX &&
                    next.Op1Kind == OpKind.Memory && next.IsIPRelativeMemoryOperand && next.Length == 6;
            if (!valid) throw new InvalidOperationException("Network identity original continuation instruction changed.");
        }

        // The input MOV does not change flags. Save them before the demand test and
        // restore them on both branches; no calls, SIMD changes or native-view writes.
        internal static void Emit(Assembler a, PlayerViewNetworkSite site, ulong protectedPlayerCell,
            ulong original, ulong continuation)
        {
            DisplacedLength(site);
            var fallback = a.CreateLabel();
            a.pushfq(); a.push(r11);
            a.mov(r11, protectedPlayerCell);
            a.mov(r11d, __dword_ptr[r11]);
            a.test(r11d, r11d); a.je(fallback);
            if (site == PlayerViewNetworkSite.Timing) a.mov(edx, r11d);
            else a.mov(rcx, r11);
            a.pop(r11); a.popfq(); a.jmp(continuation);
            a.Label(ref fallback);
            a.pop(r11); a.popfq(); a.jmp(original);
        }
    }
}
