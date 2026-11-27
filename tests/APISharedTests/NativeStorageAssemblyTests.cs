using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using APIShared.Compatibility;
using APIShared.Economy;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Validate installed file/layout bindings and decode the actual production emitter without
    // executable pages, hooks or native game calls. The path walker follows emitted demand
    // branches and inline literals. Execution coverage is in NativeStorageNativeExecutionTests.
    [TestClass]
    public sealed class NativeStorageAssemblyTests
    {
        private const ulong Image = 0x180000000;
        private const ulong Demand = 0x123456789ABC0000, Callback = 0x23456789ABCD0000;
        private const int Aperture = 0xBFB05, Continue = 0xBFB14, Recompute = 0xC05B0;
        // Independently pinned instructions: native arguments, original CALL, original CMP.
        private static readonly byte[] OriginalBytes = {
            0x8B, 0xD3, 0x48, 0x8B, 0xCE, 0xE8, 0xA1, 0x0A, 0x00, 0x00, 0x3D, 0xFA, 0x00, 0x00, 0x00 };

        [TestMethod]
        public void InstalledPristineImageAndSdkPassProductionStorageValidation()
        {
            string game = InstalledTestEnvironment.GameDirectory();
            string extender = Environment.GetEnvironmentVariable("SHCDESE_EXTENDER_DIR");
            if (string.IsNullOrWhiteSpace(extender)) extender = System.IO.Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            using (var installed = Mono.Cecil.AssemblyDefinition.ReadAssembly(System.IO.Path.Combine(extender, "SHCDESE.dll")))
                Assert.AreEqual(installed.MainModule.Mvid, typeof(SHCDESE.Interop.GameBuilding).Module.ModuleVersionId,
                    "Production layout checks must use the SDK module installed beside this game.");
            NativeStorageNativeRuntime.ValidateLayout();

            byte[] file = File.ReadAllBytes(System.IO.Path.Combine(game, "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
            using (var hash = SHA256.Create())
                Assert.AreEqual(NativeStorageNativeDefinition.NativeHash, BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""),
                    "The production storage admission supports one audited native file.");
            var pe = new FixesWheatAnalysis.PeImage(file);
            int[][] ranges = {
                new[] { NativeStorageNativeDefinition.ArmoryAdmissionRva, NativeStorageNativeDefinition.ArmoryAdmissionLength },
                new[] { NativeStorageNativeDefinition.ArmoryFreeRva, NativeStorageNativeDefinition.ArmoryFreeLength },
                new[] { NativeStorageNativeDefinition.GranaryFreeRva, NativeStorageNativeDefinition.GranaryFreeLength },
                new[] { NativeStorageNativeDefinition.GranaryFinderRva, NativeStorageNativeDefinition.GranaryFinderLength },
                new[] { NativeStorageNativeDefinition.GranaryRecomputeRva, NativeStorageNativeDefinition.GranaryRecomputeLength },
                new[] { NativeStorageNativeDefinition.AddGoodRva, NativeStorageNativeDefinition.AddGoodLength }
            };
            // Only these file-backed executable ranges are needed by ValidatePristine. Avoid
            // mapping the native module's large zero-filled data section or loading the DLL.
            var image = new byte[ranges.Max(range => checked(range[0] + range[1]))];
            foreach (int[] range in ranges)
            {
                Assert.IsTrue(pe.IsExecutable((uint)range[0], range[1]), "Storage binding must lie in executable file data.");
                Array.Copy(pe.Read((uint)range[0], range[1]), 0, image, range[0], range[1]);
            }
            NativeStorageNativeRuntime.ValidatePristine(image);
            image[NativeStorageNativeDefinition.GranaryFinderApertureRva] ^= 1;
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeStorageNativeRuntime.ValidatePristine(image),
                "A changed installed aperture must fail the production pristine-image gate.");
        }

        [TestMethod]
        public void RelocatedPathsCallNativeOnceAndContinueAfterTheOriginalComparison()
        {
            foreach (ulong image in new[] { Image, 0x700000000000UL })
            foreach (ulong stub in new[] { image + 0x20000000, 0x10000000UL, 0x7FFF00000000UL })
            {
                byte[] bytes = NativeStorageAssembly.ExpectedStub(image, stub, Demand, Callback);
                foreach (bool active in new[] { false, true })
                {
                    Path path = Walk(bytes, stub, image + Continue, active);
                    CollectionAssert.AreEqual(active ? new[] { image + Recompute, Callback } : new[] { image + Recompute },
                        path.Calls.Select(call => call.Target).ToArray(), "Exactly one original recomputation, followed only by the active filter.");
                    Assert.IsTrue(Move(path.Instructions[0], Register.EDX, Register.EBX));
                    Assert.IsTrue(Move(path.Instructions[1], Register.RCX, Register.RSI));
                    int compare = path.Instructions.FindLastIndex(IsOriginalComparison);
                    Assert.IsTrue(compare >= 0, "The result must be compared with 250 in this relocated body.");
                    Assert.AreEqual(1, path.Instructions.Count(IsOriginalComparison));
                    Assert.IsTrue(path.Instructions.Skip(compare + 1).All(i => i.Mnemonic == Mnemonic.Jmp),
                        "No instruction may overwrite the CMP flags before the original JGE continuation.");
                    Assert.IsTrue(path.Instructions.All(i => i.IP != image + Aperture && i.IP != image + 0xBFB0F),
                        "A relocated CALL must return into the stub, never overwritten native instructions.");
                }
            }
        }

        [TestMethod]
        public void DormantPathNeverBuildsTheFilterFrameOrTouchesSimd()
        {
            const ulong stub = Image + 0x20000000;
            Path path = Walk(NativeStorageAssembly.ExpectedStub(Image, stub, Demand, Callback), stub, Image + Continue, false);
            Assert.AreEqual(1, path.Calls.Count);
            Assert.AreEqual(1, path.Instructions.Count(i => i.Mnemonic == Mnemonic.Pushfq));
            Assert.AreEqual(1, path.Instructions.Count(i => i.Mnemonic == Mnemonic.Popfq));
            CollectionAssert.AreEqual(new[] { Register.RAX }, path.Instructions.Where(i => i.Mnemonic == Mnemonic.Push)
                .Select(i => i.Op0Register).ToArray());
            CollectionAssert.AreEqual(new[] { Register.RAX }, path.Instructions.Where(i => i.Mnemonic == Mnemonic.Pop)
                .Select(i => i.Op0Register).ToArray());
            Assert.IsFalse(path.Instructions.Any(i => i.Mnemonic == Mnemonic.And || i.Mnemonic == Mnemonic.Sub ||
                i.Mnemonic == Mnemonic.Movdqu || i.Mnemonic == Mnemonic.Cld), "Dormant execution must not enter callback preparation.");
        }

        [TestMethod]
        public void ActiveFramePreservesCallerStateAndReplacesOnlyTheSavedNativeReturn()
        {
            const ulong stub = Image + 0x20000000;
            Path path = Walk(NativeStorageAssembly.ExpectedStub(Image, stub, Demand, Callback), stub, Image + Continue, true);
            int callback = path.Calls.Single(call => call.Target == Callback).Index;
            Instruction[] before = path.Instructions.Take(callback).ToArray();
            Instruction[] after = path.Instructions.Skip(callback + 1).ToArray();
            var saved = new[] { Register.RAX, Register.RCX, Register.RDX, Register.R8, Register.R9, Register.R10, Register.R11 };
            CollectionAssert.AreEqual(saved, before.Where(i => i.Mnemonic == Mnemonic.Push).Select(i => i.Op0Register)
                .Reverse().Take(saved.Length).Reverse().ToArray(), "The live native volatile integer state must be saved before the filter.");
            CollectionAssert.AreEqual(saved.AsEnumerable().Reverse().ToArray(), after.Where(i => i.Mnemonic == Mnemonic.Pop).Select(i => i.Op0Register).ToArray());
            Assert.AreEqual(1, after.Count(i => i.Mnemonic == Mnemonic.Popfq));
            Assert.IsTrue(before.Any(i => i.Mnemonic == Mnemonic.Cld), "Win64 callbacks require DF clear; the saved native flags restore it afterwards.");
            Assert.IsTrue(before.Any(i => i.Mnemonic == Mnemonic.And && i.Op0Register == Register.RSP && i.Immediate8to64 == -16));

            Assert.IsTrue(Move(LastWrite(before, Register.RCX), Register.RCX, Register.RSI), "Callback manager must come from the finder's RSI.");
            Assert.IsTrue(Move(LastWrite(before, Register.EDX), Register.EDX, Register.EBX), "Callback slot must come from the finder's EBX.");
            Assert.IsTrue(Move(LastWrite(before, Register.R8D), Register.R8D, Register.EAX), "Callback amount must be the original native return, not a pre-call value.");

            var xmm = new[] { Register.XMM0, Register.XMM1, Register.XMM2, Register.XMM3, Register.XMM4, Register.XMM5 };
            for (int i = 0; i < xmm.Length; i++)
            {
                ulong offset = (ulong)(32 + i * 16);
                Assert.AreEqual(1, before.Count(instruction => instruction.Mnemonic == Mnemonic.Movdqu &&
                    instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == Register.RSP &&
                    instruction.MemoryDisplacement64 == offset && instruction.Op1Register == xmm[i]), "SIMD save " + xmm[i]);
                Assert.AreEqual(1, after.Count(instruction => instruction.Mnemonic == Mnemonic.Movdqu &&
                    instruction.Op0Register == xmm[i] && instruction.Op1Kind == OpKind.Memory &&
                    instruction.MemoryBase == Register.RSP && instruction.MemoryDisplacement64 == offset), "SIMD restore " + xmm[i]);
            }
            Assert.IsTrue(before.Any(i => Move(i, Register.R11, Register.RSP)), "Keep the pre-alignment stack pointer.");
            Assert.IsTrue(before.Any(i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory &&
                i.MemoryBase == Register.RSP && i.MemoryDisplacement64 == 128 && i.Op1Register == Register.R11));
            Assert.IsTrue(after.Any(i => i.Mnemonic == Mnemonic.Mov && i.Op0Register == Register.RSP &&
                i.Op1Kind == OpKind.Memory && i.MemoryBase == Register.RSP && i.MemoryDisplacement64 == 128));
            int zeroExtend = Array.FindIndex(after, i => Move(i, Register.EAX, Register.EAX));
            int replacement = Array.FindIndex(after, i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory &&
                i.MemoryBase == Register.R11 && i.MemoryDisplacement64 == 48 && i.Op1Register == Register.RAX);
            Assert.IsTrue(zeroExtend >= 0 && replacement > zeroExtend,
                "Store the zero-extended callback Int32 into the saved RAX slot before restoring volatile registers.");
            Assert.IsTrue(replacement < Array.FindIndex(after, i => i.Mnemonic == Mnemonic.Pop));
        }

        [TestMethod]
        public void ChangedApertureOrBindingIsRejectedBeforeAnyEmission()
        {
            Instruction[] original = Decode(OriginalBytes, Image + Aperture);
            Assert.AreEqual(4, original.Length);
            var mutations = new List<Action<Instruction[]>> {
                instructions => instructions[0].IP++,
                instructions => instructions[1].IP++,
                instructions => instructions[2].IP++,
                instructions => instructions[3].IP++,
                instructions => instructions[0].Length++,
                instructions => instructions[1].Length++,
                instructions => instructions[2].Length++,
                instructions => instructions[3].Length++,
                instructions => instructions[0].Code = Code.Xor_rm32_r32,
                instructions => instructions[0].Op0Register = Register.ECX,
                instructions => instructions[0].Op1Register = Register.ESI,
                instructions => instructions[1].Op0Register = Register.RDX,
                instructions => instructions[1].Op1Register = Register.RBX,
                instructions => instructions[2].NearBranch64++,
                instructions => instructions[3].Immediate32 = 249,
                instructions => instructions[0].HasLockPrefix = true,
                instructions => instructions[1].HasRepPrefix = true,
                instructions => instructions[2].HasRepnePrefix = true,
                instructions => instructions[3].SegmentPrefix = Register.FS
            };
            foreach (Action<Instruction[]> mutate in mutations)
            {
                var changed = (Instruction[])original.Clone(); mutate(changed);
                Reject(changed, Image + Continue, Image, Demand, Callback);
            }
            Reject(original.Take(3).ToArray(), Image + Continue, Image, Demand, Callback);
            Reject(original, Image + Continue + 1, Image, Demand, Callback);
            Reject(original, Image + Continue, Image + 1, Demand, Callback);
            Reject(original, Image + Continue, Image, 0, Callback);
            Reject(original, Image + Continue, Image, Demand, 0);
        }

        private static void Reject(Instruction[] original, ulong back, ulong image, ulong demand, ulong callback)
        {
            var assembler = new Assembler(64);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeStorageAssembly.Generate(assembler, original, back, image, demand, callback));
            Assert.AreEqual(0, Assemble(assembler, Image + 0x20000000).Length, "Refusal must precede all instruction emission.");
        }
        private static bool Move(Instruction instruction, Register target, Register source) =>
            instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
            instruction.Op0Register == target && instruction.Op1Register == source;
        private static Instruction LastWrite(Instruction[] instructions, Register target) =>
            instructions.Last(i => i.OpCount != 0 && i.Op0Kind == OpKind.Register && i.Op0Register == target && i.Mnemonic == Mnemonic.Mov);
        private static bool IsOriginalComparison(Instruction instruction) => instruction.Code == Code.Cmp_EAX_imm32 && instruction.Immediate32 == 250;
        private static byte[] Assemble(Assembler assembler, ulong address)
        { using (var stream = new MemoryStream()) { assembler.Assemble(new StreamCodeWriter(stream), address); return stream.ToArray(); } }
        private static Instruction[] Decode(byte[] bytes, ulong address)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
            var instructions = new List<Instruction>();
            while (decoder.IP < address + (ulong)bytes.Length) { Instruction instruction = decoder.Decode(); Assert.AreNotEqual(Code.INVALID, instruction.Code); instructions.Add(instruction); }
            return instructions.ToArray();
        }

        private sealed class Path
        {
            internal readonly List<Instruction> Instructions = new List<Instruction>();
            internal readonly List<Call> Calls = new List<Call>();
        }
        private readonly struct Call
        {
            internal readonly int Index; internal readonly ulong Target;
            internal Call(int index, ulong target) { Index = index; Target = target; }
        }
        private static Path Walk(byte[] bytes, ulong origin, ulong continuation, bool demand)
        {
            var path = new Path(); var literals = new Dictionary<Register, ulong>(); ulong ip = origin;
            for (int steps = 0; steps < 256; steps++)
            {
                if (ip == continuation) return path;
                Assert.IsTrue(ip >= origin && ip < origin + (ulong)bytes.Length, "Unexpected control-flow target " + ip.ToString("X"));
                int offset = checked((int)(ip - origin));
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes, offset, bytes.Length - offset)); decoder.IP = ip;
                Instruction instruction = decoder.Decode(); Assert.AreNotEqual(Code.INVALID, instruction.Code);
                path.Instructions.Add(instruction); ip = instruction.NextIP;
                if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Immediate64)
                    literals[instruction.Op0Register] = instruction.Immediate64;
                if (instruction.FlowControl == FlowControl.Call || instruction.FlowControl == FlowControl.IndirectCall)
                {
                    path.Calls.Add(new Call(path.Instructions.Count - 1, Target(instruction, bytes, origin, literals)));
                    literals.Remove(Register.RAX); // The native/filter result is not an address literal.
                }
                else if (instruction.FlowControl == FlowControl.UnconditionalBranch || instruction.FlowControl == FlowControl.IndirectBranch)
                    ip = Target(instruction, bytes, origin, literals);
                else if (instruction.FlowControl == FlowControl.ConditionalBranch)
                {
                    Assert.IsTrue(instruction.Mnemonic == Mnemonic.Je || instruction.Mnemonic == Mnemonic.Jne,
                        "Only the DWORD demand guard may branch inside this stub.");
                    if (instruction.Mnemonic == Mnemonic.Je ? !demand : demand) ip = instruction.NearBranchTarget;
                }
            }
            Assert.Fail("The emitted path did not reach the original continuation."); return path;
        }
        private static ulong Target(Instruction instruction, byte[] bytes, ulong origin, Dictionary<Register, ulong> literals)
        {
            if (instruction.Op0Kind == OpKind.NearBranch64) return instruction.NearBranchTarget;
            if (instruction.Op0Kind == OpKind.Register)
            { Assert.IsTrue(literals.TryGetValue(instruction.Op0Register, out ulong address), "Unresolved indirect address."); return address; }
            Assert.IsTrue(instruction.Op0Kind == OpKind.Memory && instruction.IsIPRelativeMemoryOperand, "Only rooted inline pointers are allowed.");
            ulong pointer = instruction.IPRelativeMemoryAddress;
            Assert.IsTrue(pointer >= origin && pointer <= origin + (ulong)bytes.Length - 8, "Inline pointer exceeds generated bytes.");
            return BitConverter.ToUInt64(bytes, checked((int)(pointer - origin)));
        }
    }
}
