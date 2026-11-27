using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Iced.Intel;
using APIShared.Buildings;
using APIShared.Simulation;
using APIShared.GameModes;
using System.Collections.Generic;
using System.Security.Cryptography;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    internal static class EnemyProximityNativeTests
    {
        [DllImport("kernel32", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr p, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32")] private static extern bool VirtualFree(IntPtr p, UIntPtr size, uint type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PeaceUpdater();
        private static IntPtr data;
        private const int Player = 1, X = 100, Y = 100, Unit = 0x67E8A5C + 0x490;
        private static void Commit(int offset)
        {
            if (VirtualAlloc(IntPtr.Add(data, offset & ~4095), (UIntPtr)4096, 0x1000, 4) == IntPtr.Zero)
                throw new InvalidOperationException("Native proximity fixture page allocation failed.");
        }
        private static void Word(int offset, int value) { Commit(offset); Marshal.WriteInt16(data, offset, unchecked((short)value)); }
        private static void Dword(int offset, int value) { Commit(offset); Marshal.WriteInt32(data, offset, value); }
        private static void Byte(int offset, byte value) { Commit(offset); Marshal.WriteByte(data, offset, value); }
        private static void Reset(eChimps type, int dx = 10, int dy = 0)
        {
            Byte(0x3A11EA4 + Y * 800 + X, 1);
            Dword(0x379CE58 + Player * 0x583C, 1);
            Word(0x38C19E8 + Player * 20000, 1);
            Dword(0x38ED908 + Player * 40000, 123);
            Word(Unit + 0x8A, (int)type); Word(Unit + 0x90, 0); Word(Unit + 0x92, 2);
            Dword(Unit + 0x94, 123);
            Word(Unit + 0xC0, X + dx); Word(Unit + 0xC2, Y + dy);
            Word(Unit + 0x29C, 0); Word(Unit + 0x354, 0); Byte(Unit + 0x328, 0);
            Word(Unit + 0x2BC, 0); Dword(Unit + 0x434, 0); Word(Unit + 0x440, 0);
            Dword(0x8574B90, 0);
        }
        private static byte[] Assemble(Assembler a, ulong address)
        {
            using (var stream = new MemoryStream()) { a.Assemble(new StreamCodeWriter(stream), address); return stream.ToArray(); }
        }
        // Call the actual generated predicate while checking Win64 preserved registers,
        // including XMM6/XMM15. No managed callback stands in for the native function.
        private static EnemyProximityQuery.QueryDelegate Guard(ulong destination, IntPtr code)
        {
            var a = new Assembler(64); var bad = a.CreateLabel(); var done = a.CreateLabel();
            a.mov(r10d, __dword_ptr[rsp + 0x28]); a.movzx(r11d, __byte_ptr[rsp + 0x30]); a.mov(eax, __dword_ptr[rsp + 0x38]);
            a.push(rbx); a.push(rbp); a.push(rsi); a.push(rdi); a.push(r12); a.push(r13); a.push(r14); a.push(r15);
            a.sub(rsp, 120);
            a.mov(__dword_ptr[rsp + 0x20], r10d); a.mov(__byte_ptr[rsp + 0x28], r11b); a.mov(__dword_ptr[rsp + 0x30], eax);
            a.movdqu(__xmmword_ptr[rsp + 0x40], xmm6); a.movdqu(__xmmword_ptr[rsp + 0x50], xmm15);
            a.pcmpeqd(xmm6, xmm6); a.pxor(xmm15, xmm15);
            a.mov(rbx, 11); a.mov(rbp, 12); a.mov(rsi, 13); a.mov(rdi, 14); a.mov(r12, 15); a.mov(r13, 16); a.mov(r14, 17); a.mov(r15, 18);
            a.mov(rax, destination); a.call(rax);
            a.cmp(rbx, 11); a.jne(bad); a.cmp(rbp, 12); a.jne(bad); a.cmp(rsi, 13); a.jne(bad); a.cmp(rdi, 14); a.jne(bad);
            a.cmp(r12, 15); a.jne(bad); a.cmp(r13, 16); a.jne(bad); a.cmp(r14, 17); a.jne(bad); a.cmp(r15, 18); a.jne(bad);
            a.pmovmskb(edx, xmm6); a.cmp(edx, 65535); a.jne(bad); a.pmovmskb(edx, xmm15); a.test(edx, edx); a.jne(bad);
            a.jmp(done); a.Label(ref bad); a.mov(eax, -12345);
            a.Label(ref done); a.movdqu(xmm6, __xmmword_ptr[rsp + 0x40]); a.movdqu(xmm15, __xmmword_ptr[rsp + 0x50]);
            a.add(rsp, 120); a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12); a.pop(rdi); a.pop(rsi); a.pop(rbp); a.pop(rbx); a.ret();
            byte[] bytes = Assemble(a, unchecked((ulong)code.ToInt64())); Marshal.Copy(bytes, 0, code, bytes.Length);
            return (EnemyProximityQuery.QueryDelegate)Marshal.GetDelegateForFunctionPointer(code, typeof(EnemyProximityQuery.QueryDelegate));
        }
        internal static void Run(byte[] image, Action<bool, string> check)
        {
            byte[] body = image.Skip(EnemyProximityQuery.Rva).Take(EnemyProximityQuery.Length).ToArray();
            EnemyProximityQuery.Validate(body);
            byte[] corrupt = (byte[])body.Clone(); corrupt[20] ^= 1;
            bool rejected = false; try { EnemyProximityQuery.Validate(corrupt); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Changed complete proximity body is rejected");
            data = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x9000000, 0x2000, 4);
            IntPtr code = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x4000, 0x3000, 0x40);
            if (data == IntPtr.Zero || code == IntPtr.Zero) throw new InvalidOperationException("Native fixture allocation failed.");
            try
            {
                ulong start = unchecked((ulong)code.ToInt64()), module = 0x180000000, dataBase = unchecked((ulong)data.ToInt64());
                byte[] original = EnemyProximityQuery.Build(body, module, start, dataBase, false);
                byte[] split = EnemyProximityQuery.Build(body, module, start + 4096, dataBase, true);
                Marshal.Copy(original, 0, code, original.Length); Marshal.Copy(split, 0, IntPtr.Add(code, 4096), split.Length);
                byte[] table = EnemyProximityQuery.Categories(); Marshal.Copy(table, 0, IntPtr.Add(code, 4096 + 3072), table.Length);
                var vanilla = Guard(start, IntPtr.Add(code, 8192)); var variant = Guard(start + 4096, IntPtr.Add(code, 12288));
                Func<int, int, byte, long> run = (military, civilian, unknown) => variant(IntPtr.Zero, Player, X, Y, military, unknown, civilian * civilian);
                foreach (eChimps type in Enum.GetValues(typeof(eChimps)))
                foreach (int radius in new[] { 0, 1, 3, 5, 15, 30, 100, 250, 46340 })
                foreach (int dx in new[] { 0, radius - 1, radius, radius + 1 })
                {
                    Reset(type, dx, dx / 2);
                    long reference = vanilla(IntPtr.Zero, Player, X, Y, radius, 0, 0);
                    check(run(radius, radius, 0) == reference, "Original native equivalence, including ABI: " + type + "/" + radius + "/" + dx);
                }
                foreach (eChimps type in Enum.GetValues(typeof(eChimps)))
                {
                    Reset(type, 10);
                    bool civilian = EnemyProximityQuery.IsCivilian(type);
                    check(run(30, 0, 0) == (civilian ? 0 : 1), "Civilian suppression preserves military: " + type);
                    check(run(0, 30, 0) == (civilian ? 1 : 0), "Independent reverse category: " + type);
                }
                foreach (int dx in new[] { 0, 1, 2, 3, 4, 10 })
                {
                    Reset(eChimps.CHIMP_TYPE_WOODCUTTER, dx);
                    check(run(30, 3, 0) == (dx < 3 ? 1 : 0), "Peace civilian radius uses exact strict boundary");
                    Reset(eChimps.CHIMP_TYPE_ARCHER, dx);
                    check(run(30, 3, 0) == 1, "Peace civilian cap does not reduce soldier radius");
                }
                foreach (eChimps type in new[] { eChimps.CHIMP_TYPE_PEASANT, eChimps.CHIMP_TYPE_ARCHER })
                {
                    Reset(type, 3, 4); check(run(5, 5, 0) == 0, "Strict Euclidean radius boundary");
                    check(run(6, 6, 0) == 1, "Interior diagonal accepted as threat");
                    Reset(type); Dword(Unit + 0x94, 124); check(run(30, 30, 0) == 0, "Reused global identity excluded");
                    Reset(type); Word(Unit + 0x29C, 1); check(run(30, 30, 0) == 0, "Death marker excluded");
                    Reset(type); Word(Unit + 0x354, 1); check(run(30, 30, 0) == 0, "Original exclusion word");
                    Reset(type); Word(Unit + 0x90, 1); check(run(30, 30, 0) == 0, "Original state exclusion");
                    Reset(type); Word(Unit + 0x92, 0); check(run(30, 30, 0) == 0, "Uncontrolled neutral excluded");
                    Reset(type); Byte(Unit + 0x328, 1); check(run(30, 30, 0) == 0 && run(30, 30, 1) == 1, "Sixth argument filter preserved");
                    Reset(type); Dword(0x379CE58 + Player * 0x583C, 0); check(run(30, 30, 0) == 0, "Empty enemy list");
                    Reset(type); Byte(0x3A11EA4 + Y * 800 + X, 0); check(run(30, 30, 0) == 0, "Invalid map cell");
                }
                foreach (eChimps civilian in new[] { eChimps.CHIMP_TYPE_MINER1, eChimps.CHIMP_TYPE_MINER2,
                    eChimps.CHIMP_TYPE_QUARRY_OX, eChimps.CHIMP_TYPE_TRADER_HORSE, eChimps.CHIMP_TYPE_PRIEST })
                    check(EnemyProximityQuery.IsCivilian(civilian), "Civilian policy includes workers/transports: " + civilian);
                foreach (eChimps soldier in new[] { eChimps.CHIMP_TYPE_ENGINEER, eChimps.CHIMP_TYPE_TUNNELER,
                    eChimps.CHIMP_TYPE_MONK, eChimps.CHIMP_TYPE_ARAB_SLAVE, eChimps.CHIMP_TYPE_BEDOUIN_HEALER })
                    check(!EnemyProximityQuery.IsCivilian(soldier), "Military exceptions retain military radius: " + soldier);
                Reset(eChimps.CHIMP_TYPE_WOODCUTTER);
                var prepared = new EnemyProximityQuery(image, dataBase);
                check(prepared.Invoke(Player, X, Y, 30, 0, 0) == 0, "Actual runtime allocator/delegate ignores civilian");
                Reset(eChimps.CHIMP_TYPE_ARCHER);
                check(prepared.Invoke(Player, X, Y, 30, 0, 0) == 1, "Actual runtime allocator/delegate preserves soldier");
                // First candidate is an ignored worker; a later soldier must still veto.
                Reset(eChimps.CHIMP_TYPE_WOODCUTTER);
                Dword(0x379CE58 + Player * 0x583C, 2); Word(0x38C19E8 + Player * 20000 + 2, 2); Dword(0x38ED908 + Player * 40000 + 4, 456);
                int second = Unit + 0x490;
                Word(second + 0x8A, (int)eChimps.CHIMP_TYPE_ENGINEER); Word(second + 0x92, 2); Dword(second + 0x94, 456);
                Word(second + 0xC0, X + 10); Word(second + 0xC2, Y);
                check(run(30, 0, 0) == 1, "Later engineer still blocks after ignored civilian");
                check(run(30, 3, 0) == 1, "Later engineer still blocks after peace-limited civilian");
                foreach (eChimps type in new[] { eChimps.CHIMP_TYPE_ARAB_ASSASIN, eChimps.CHIMP_TYPE_BEDOUIN_AMBUSHER })
                foreach (int timer in new[] { 0, 80, 81, 120, 121, 160, 161, 2400, 2401 })
                foreach (int state in new[] { 0, 106, 107, 108 })
                foreach (int mode in new[] { 0, 1 })
                {
                    Reset(type); Dword(Unit + 0x434, timer); Word(Unit + 0x440, timer); Word(Unit + 0x2BC, state); Dword(0x8574B90, mode);
                    check(run(30, 0, 0) == vanilla(IntPtr.Zero, Player, X, Y, 30, 0, 0), "Stealth special branches and thresholds preserved");
                }
                CheckActualPeaceExpiry(image, check);
            }
            finally { VirtualFree(data, UIntPtr.Zero, 0x8000); VirtualFree(code, UIntPtr.Zero, 0x8000); }
        }

        private static void CheckActualPeaceExpiry(byte[] image, Action<bool, string> check)
        {
            const int updaterRva = 0xCA870, updaterLength = 138;
            const int flagRva = 0x38722DC, endRva = 0x38722E0, startRva = 0x38722E8, tickRva = 0x3665F58;
            byte[] body = image.Skip(updaterRva).Take(updaterLength).ToArray();
            using (var sha = SHA256.Create())
                check(BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "") ==
                    "FF0C43799C8C79DAB6071437A44818457D5D42BC341CF448B59CA657DF8EB19B", "Complete native peace updater matches audit");
            IntPtr executable = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
            if (executable == IntPtr.Zero) throw new InvalidOperationException("Peace updater fixture allocation failed.");
            try
            {
                ulong address = unchecked((ulong)executable.ToInt64()), memory = unchecked((ulong)data.ToInt64());
                // The only substitutions are external presentation edges. All timer arithmetic,
                // signed comparisons, short-circuits and the native flag write execute unchanged.
                // Original uses no R11; it is a volatile relocation scratch register.
                Marshal.WriteByte(executable, 3072, 0xC3);
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(body)); decoder.IP = updaterRva;
                var instructions = new List<Instruction>(); ulong synthetic = 0x7000000000000000;
                int externalEdges = 0;
                while (decoder.IP < updaterRva + updaterLength)
                {
                    Instruction original = decoder.Decode();
                    if (original.IsInvalid) throw new InvalidOperationException("Native peace updater no longer decodes.");
                    if (original.Op0Register == Register.R11 || original.Op1Register == Register.R11 || original.MemoryBase == Register.R11)
                        throw new InvalidOperationException("Peace relocation scratch register is now live.");
                    if ((original.Mnemonic == Mnemonic.Call || original.Mnemonic == Mnemonic.Jmp) &&
                        (original.NearBranchTarget < updaterRva || original.NearBranchTarget >= updaterRva + updaterLength))
                    {
                        ulong target = original.NearBranchTarget;
                        if (target != 0x1A2980 && target != 0x1A42B0 && target != 0x2940)
                            throw new InvalidOperationException("Unexpected peace updater external edge.");
                        Instruction replacement = Instruction.CreateBranch(original.Mnemonic == Mnemonic.Call ?
                            Code.Call_rel32_64 : Code.Jmp_rel32_64, address + 3072);
                        replacement.IP = original.IP; instructions.Add(replacement); externalEdges++;
                    }
                    else if (original.IsIPRelativeMemoryOperand)
                    {
                        ulong target = checked(memory + original.IPRelativeMemoryAddress);
                        Instruction load = Instruction.Create(Code.Mov_r64_imm64,
                            original.Mnemonic == Mnemonic.Lea ? original.Op0Register : Register.R11, target);
                        load.IP = original.IP; instructions.Add(load);
                        if (original.Mnemonic != Mnemonic.Lea)
                        {
                            original.MemoryBase = Register.R11; original.MemoryIndex = Register.None;
                            original.MemoryDisplacement64 = 0; original.MemoryDisplSize = 0;
                            original.IP = synthetic++; instructions.Add(original);
                        }
                    }
                    else instructions.Add(original);
                }
                check(externalEdges == 3, "Only display, removal and sound-tail edges substituted");
                using (var stream = new MemoryStream())
                {
                    if (!BlockEncoder.TryEncode(64, new InstructionBlock(new StreamCodeWriter(stream), instructions, address),
                        out string error, out BlockEncoderResult _)) throw new InvalidOperationException(error);
                    byte[] relocated = stream.ToArray();
                    if (relocated.Length >= 3072) throw new InvalidOperationException("Relocated peace updater overlaps its presentation stub.");
                    Marshal.Copy(relocated, 0, executable, relocated.Length);
                }
                var update = (PeaceUpdater)Marshal.GetDelegateForFunctionPointer(executable, typeof(PeaceUpdater));
                var lifecycle = new APIShared.MissionLifecycleState(error => { throw error; });
                long id = lifecycle.Begin(new APIShared.MissionContext(0, APIShared.MissionStartKind.NewGame,
                    default(GameModeSnapshot), null, "native peace expiry fixture", false));
                lifecycle.BeginNativeLoad(id); lifecycle.CompleteNativeLoad(id, true); lifecycle.Ready(id, lifecycle.Context);
                var reader = new PeaceTimeReader(() => lifecycle.Current, () => lifecycle.NativeClock,
                    () => Marshal.ReadByte(data, flagRva));
                foreach (int start in new[] { 0, 100 })
                foreach (int tick in new[] { 199, 200, 201 })
                foreach (byte initialFlag in new byte[] { 0, 1 })
                {
                    Byte(flagRva, initialFlag); Dword(startRva, start); Dword(endRva, 200); Dword(tickRva, tick);
                    update();
                    byte expected = initialFlag != 0 && start > 0 && tick > 200 ? (byte)0 : initialFlag;
                    check(Marshal.ReadByte(data, flagRva) == expected,
                        "Executed native expiry preserves equality and clears only after end, start=" + start + ", tick=" + tick + ", flag=" + initialFlag);
                    check(reader.TryRead(out bool active) && active == (expected != 0), "Real peace reader observes native updater result");
                }
                // The same prepared function must resume normal active state after a restored save.
                Byte(flagRva, 1); Dword(startRva, 100); Dword(endRva, 200); Dword(tickRva, 200);
                update(); check(reader.TryRead(out bool restored) && restored, "Loaded active state survives exact expiry tick");
                Dword(tickRva, 201); update(); check(reader.TryRead(out restored) && !restored, "Next native update ends peace without reinstalling code");
                GC.KeepAlive(update);
            }
            finally { VirtualFree(executable, UIntPtr.Zero, 0x8000); }
        }
    }
}
