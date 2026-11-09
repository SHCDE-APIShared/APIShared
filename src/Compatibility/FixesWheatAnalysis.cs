using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Compatibility
{
    // Version-independent machine contract of the Fixes wheat-sale correction. Nothing here depends on a
    // file hash, a version or a fixed RVA: the hook site comes from Fixes' own hook handle, the seller frame
    // from the game's PE unwind data and decoded prolog. Entire supported prefixes are rebuilt from
    // those derived values and compared before any write; an old byte-template allowlist is not used.
    // Every derivation is fail-closed; an unexpected shape refuses the correction instead of guessing.
    // No compound assignments: the wheat source guardrail treats every += as a later callback subscription.
    internal static partial class FixesWheatAnalysis
    {
        // Entry-relative home slot of the second integer argument (RDX) in the Windows x64 ABI.
        internal const int SecondArgumentHome = 0x10;
        private const int MaximumStubLength = 256;

        internal sealed class SellerFrame
        {
            internal uint FunctionBegin, PrologLength, SiteRva;
            // Stack bytes the seller's prolog allocates and pushes (fixed for the whole body: no frame register).
            internal int Allocation;
            // RSP displacement of the player argument's home slot at the hook site.
            internal int PlayerHome;
        }

        // Derives the seller's player-argument slot at the hook site from unwind data and the decoded prolog.
        internal static SellerFrame AnalyzeSeller(PeImage image, uint siteRva, byte[] displaced)
        {
            Require(displaced != null && displaced.Length >= 14, "Displaced byte count is below the 14-byte entry jump");
            Require(image.IsExecutable(siteRva, displaced.Length), "Hook site is not in an executable section");
            RequireEqual(image.Read(siteRva, displaced.Length), displaced, "disk bytes at the hook site");
            ValidateDisplaced(displaced, siteRva);

            RuntimeFunction containing = FindContaining(image, siteRva);
            UnwindInfo fragment = ReadUnwind(image, containing.Unwind);
            Require(siteRva >= containing.Begin + (uint)fragment.PrologLength, "Hook site lies inside a prolog");
            RuntimeFunction primary = containing;
            UnwindInfo primaryInfo = fragment;
            for (int depth = 0; primaryInfo.Chained.HasValue; depth++)
            {
                Require(depth < 8, "Unwind chain too deep");
                RequireNoStackChange(primaryInfo, "chained fragment");
                primary = primaryInfo.Chained.Value;
                primaryInfo = ReadUnwind(image, primary.Unwind);
            }
            int allocation = StackAllocation(primaryInfo);
            Require(allocation > 0 && allocation % 8 == 0, "Unexpected seller stack allocation");

            // The prolog must store the untouched second argument at its entry home slot before any RSP change
            // relevant to that store, and its decoded stack changes must match the unwind description.
            byte[] prolog = image.Read(primary.Begin, primaryInfo.PrologLength);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(prolog), primary.Begin, DecoderOptions.None);
            var info = new InstructionInfoFactory();
            int lowered = 0, homeStores = 0;
            bool rdxWritten = false;
            while (decoder.IP < primary.Begin + (ulong)prolog.Length)
            {
                Instruction instruction = decoder.Decode();
                Require(!instruction.IsInvalid, "Undecodable seller prolog");
                if (IsExplicitStackStore(instruction, out long displacement) &&
                    (instruction.Op1Kind == OpKind.Register &&
                     (instruction.Op1Register == Register.EDX || instruction.Op1Register == Register.RDX)))
                {
                    Require(!rdxWritten && displacement - lowered == SecondArgumentHome,
                        "Seller prolog stores RDX outside its entry home slot");
                    homeStores++;
                }
                foreach (UsedRegister used in info.GetInfo(instruction).GetUsedRegisters())
                    if (used.Register.GetFullRegister() == Register.RDX && IsWrite(used.Access))
                        rdxWritten = true;
                lowered = lowered + StackLowering(instruction);
            }
            Require(decoder.IP == primary.Begin + (ulong)prolog.Length, "Seller prolog ends inside an instruction");
            Require(homeStores == 1, "Seller prolog does not save the player argument exactly once");
            Require(lowered == allocation, "Decoded seller prolog differs from its unwind data");

            int home = allocation + SecondArgumentHome;
            foreach (RuntimeFunction range in FunctionRanges(image, primary))
            {
                // Every fragment shares the primary frame only while it keeps RSP unchanged.
                if (range.Begin != primary.Begin) RequireNoStackChange(ReadUnwind(image, range.Unwind), "seller fragment");
                RequireHomeUntouched(image, range, range.Begin == primary.Begin ? (uint)prolog.Length : 0u, home);
            }
            return new SellerFrame { FunctionBegin = primary.Begin, PrologLength = (uint)prolog.Length,
                SiteRva = siteRva, Allocation = allocation, PlayerHome = home };
        }

        // The writer owns page protection and cache synchronization. No rollback is allowed.
        internal static bool Correct(Func<byte[]> read, Action<int, byte[], byte[]> write, Func<byte[], WheatStub> analyze)
        {
            byte[] before = read();
            WheatStub stub = analyze(before);
            if (stub.IsCorrect) return false;
            RequireEqual(read(), before, "stub changed before the startup write");
            write(stub.WriteOffset, stub.Slice(stub.Before), stub.Slice(stub.After));
            byte[] after = read();
            byte[] expected = (byte[])before.Clone();
            Array.Copy(stub.After, stub.WriteOffset, expected, stub.WriteOffset, stub.WriteLength);
            RequireEqual(after, expected, "post-write bytes outside the approved correction range");
            Require(analyze(after).IsCorrect, "Post-write stub does not match the corrected contract");
            return true;
        }

        // Live entry written by the backend: jmp qword [rip+0], the stub address, NOP padding.
        internal static byte[] ExpectedEntry(ulong stub, int displacedLength)
        {
            byte[] result = new byte[displacedLength];
            result[0] = 0xFF; result[1] = 0x25;
            Array.Copy(BitConverter.GetBytes(stub), 0, result, 6, 8);
            for (int i = 14; i < result.Length; i++) result[i] = 0x90;
            return result;
        }

        internal static void RequireEqual(byte[] actual, byte[] expected, string description)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
                throw new InvalidOperationException(description + ": length mismatch");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i])
                    throw new InvalidOperationException(description + ": mismatch at +0x" + i.ToString("X"));
        }

        private static void ValidateDisplaced(byte[] displaced, uint siteRva)
        {
            // Re-executed verbatim in the stub: complete instructions without branches, RIP-relative operands
            // or stack changes, so neither the frame nor the copy can differ from the original.
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(displaced), siteRva, DecoderOptions.None);
            while (decoder.IP < siteRva + (ulong)displaced.Length)
            {
                Instruction instruction = decoder.Decode();
                Require(!instruction.IsInvalid && instruction.FlowControl == FlowControl.Next &&
                    !instruction.IsIPRelativeMemoryOperand && !WritesRsp(instruction) && StackLowering(instruction) == 0,
                    "Displaced instructions are not position- and stack-neutral");
            }
            Require(decoder.IP == siteRva + (ulong)displaced.Length, "Displaced bytes end inside an instruction");
        }

        private static int StackLowering(Instruction instruction)
        {
            if (instruction.Mnemonic == Mnemonic.Push) return 8;
            if (instruction.Mnemonic == Mnemonic.Sub && instruction.Op0Kind == OpKind.Register &&
                instruction.Op0Register == Register.RSP && instruction.Op1Kind != OpKind.Register)
                return (int)instruction.GetImmediate(1);
            Require(!WritesRsp(instruction), "Unexpected RSP change in prolog or displaced code");
            return 0;
        }

        private static bool IsExplicitStackStore(Instruction instruction, out long displacement)
        {
            displacement = (long)instruction.MemoryDisplacement64;
            return instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Memory &&
                instruction.MemoryBase == Register.RSP && instruction.MemoryIndex == Register.None;
        }

        private static bool UsesExplicitStackMemory(Instruction instruction)
        {
            for (int operand = 0; operand < instruction.OpCount; operand++)
                if (instruction.GetOpKind(operand) == OpKind.Memory &&
                    (instruction.MemoryBase == Register.RSP || instruction.MemoryIndex == Register.RSP))
                    return true;
            return false;
        }

        private static bool WritesRsp(Instruction instruction) =>
            instruction.Mnemonic != Mnemonic.Push && instruction.Mnemonic != Mnemonic.Pop &&
            instruction.Mnemonic != Mnemonic.Call && instruction.Mnemonic != Mnemonic.Ret &&
            WritesRegister(instruction, Register.RSP);

        private static bool WritesRegister(Instruction instruction, Register full)
        {
            foreach (UsedRegister used in new InstructionInfoFactory().GetInfo(instruction).GetUsedRegisters())
                if (used.Register.GetFullRegister() == full && IsWrite(used.Access))
                    return true;
            return false;
        }

        private static bool IsWrite(OpAccess access) =>
            access == OpAccess.Write || access == OpAccess.ReadWrite || access == OpAccess.CondWrite ||
            access == OpAccess.ReadCondWrite;

        private static void Require(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException(reason);
        }
    }
}
