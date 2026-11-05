using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using APIShared.Units.AiMilitary.Quota;
using Iced.Intel;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.Abstractions.Hooks;
using APIShared.Units.AiMilitary.Engineering;
using APIShared.Units.AiMilitary.Native;
using APIShared.NativeSites;
using static Iced.Intel.AssemblerRegisters;

// Executes the production emitted bridge in private memory. It never opens the
// game process. This establishes backend/ABI behavior, not in-game acceptance.
internal static unsafe class Program
{
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
    [DllImport("kernel32", SetLastError = true)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint mode);
    [DllImport("kernel32")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Run();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(int index, IntPtr context);
    private static readonly Callback Managed = Call;
    private static readonly AssemblerRegister64[] Regs = { rax, rbx, rcx, rdx, rsi, rdi, rbp, rsp, r8, r9, r10, r11, r12, r13, r14, r15 };
    private const int Size = 0xA000000;
    private static IntPtr image, state, capture;
    private static int checks, page, callbacks;
    private static bool adjust;
    private static readonly Dictionary<(int Site, int Stack), byte[]> Baselines = new Dictionary<(int Site, int Stack), byte[]>();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static ulong M => unchecked((ulong)image.ToInt64());
    private static ulong S => unchecked((ulong)state.ToInt64());
    private static ulong C => unchecked((ulong)capture.ToInt64());
    private static ulong CallbackPointer => unchecked((ulong)Marshal.GetFunctionPointerForDelegate(Managed).ToInt64());
    private static void Main(string[] args)
    {
        SnapshotTests();
        ProvenanceTests();
        byte[] file = File.ReadAllBytes(args[0]);
        Check(Convert.ToHexString(SHA256.HashData(file)) == AiQuotaNativeContract.Hash, "canonical DLL identity");
        image = VirtualAlloc(IntPtr.Zero, (UIntPtr)Size, 0x3000, 0x40);
        state = Marshal.AllocHGlobal(64 + 40000); capture = Marshal.AllocHGlobal(256);
        if (image == IntPtr.Zero) throw new Exception("Private native fixture allocation failed.");
        try
        {
            foreach (var site in AiQuotaNativeContract.Sites)
            {
                byte[] actual = ReadRva(file, site.Rva, site.Bytes.Length);
                Check(actual.SequenceEqual(site.Bytes), "canonical instruction span " + site.Rva.ToString("X"));
                Marshal.Copy(actual, 0, image + site.Rva, actual.Length);
            }
            AiQuotaNativeContract.Validate(new ReadOnlySpan<byte>((void*)image, Size));
            for (int i = 0; i < AiQuotaNativeContract.Sites.Length; i++)
            {
                int index = i; var site = AiQuotaNativeContract.Sites[i];
                using var hook = new X64InlineHook(M + (uint)site.Rva, site.Bytes.Length);
                Check(hook.DisplacedByteCount == site.Bytes.Length, "actual RedBird span " + i);
                hook.Generate((a, original, back) => AiQuotaAssembly.Generate(a, original, back, index, M, S, CallbackPointer));
                byte[] expected = AiQuotaAssembly.Expected(i, M, S, CallbackPointer, unchecked((ulong)hook.StubAddress.ToInt64()));
                byte[] actual = new byte[expected.Length]; Marshal.Copy(hook.StubAddress, actual, 0, actual.Length);
                Check(actual.SequenceEqual(expected), "actual RedBird stub " + i);
                hook.Enable(); Check(hook.IsInstalled, "private backend enabled " + i);
            }
            // Test every span on both possible stack alignments; observe branch
            // side effects as well as the complete inactive/active CPU state.
            for (int index = 0; index < 23; index++)
            foreach (int mode in new[] { 0, 1, 2 })
            foreach (int alignment in new[] { 32, 40 })
                Execute(index, mode, alignment);
            foreach (int alignment in new[] { 32, 40 })
            { Execute(22, 1, alignment, true); Execute(1, 1, alignment, false, true); }
            CompareFlagTests();
            EngineeringGateTests(file);
            ObserverEntryTests(file);
            RestoreFailureBranchTests(file);
            Console.WriteLine("PASS: " + checks + " assertions; all 23 canonical spans and actual RedBird stubs; dormant/active/adjusted CPU paths, both stack alignments, GPR/XMM/flags preservation, shadow snapshot semantics.");
        }
        finally { Marshal.FreeHGlobal(state); Marshal.FreeHGlobal(capture); VirtualFree(image, UIntPtr.Zero, 0x8000); }
    }
    private static void SnapshotTests()
    {
        var snapshot = new AiQuotaSnapshot(); snapshot.BeginFold();
        snapshot.Count(1, 22, 1); snapshot.Count(1, 22, 4); snapshot.Count(1, 22, 2);
        snapshot.Count(1, 30, 10); snapshot.Count(1, 30, 11); snapshot.Count(1, 55, 2);
        for (int role = 11; role <= 20; role++) snapshot.Count(8, 22, role);
        Check(snapshot.Regular(5, 1, AiQuotaSnapshot.Defender) == 3, "both defender roles counted");
        Check(snapshot.Regular(3, 1, AiQuotaSnapshot.Harasser) == 2, "lord excluded");
        Check(snapshot.GetExtra(1, 6) == 1 && snapshot.GetExtra(1, 7) == 0, "engineer native fold exception");
        Check(snapshot.GetExtra(8, AiQuotaSnapshot.Sieger) == 10, "all siege roles contribute samefold");
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) snapshot.Write(writer);
        stream.Position = 0; using var reader = new BinaryReader(stream); var loaded = AiQuotaSnapshot.Read(reader);
        Check(loaded.Regular(10, 8, AiQuotaSnapshot.Sieger) == 0, "loaded fold preserved");
        snapshot.BeginFold(); Check(snapshot.GetExtra(8, AiQuotaSnapshot.Sieger) == 0, "fold clears old contributions");
        Check(loaded.Regular(2, 1, AiQuotaSnapshot.Defender) == 0, "samefold clamping");
    }
    private static void ProvenanceTests()
    {
        var build = new AiBuildProvenance(39, true);
        Check(!build.IsAdditional, "empty contributor set is not vacuously additional");
        build.Observe(true, true); build.Observe(true, true);
        Check(build.IsAdditional, "all real contributors additional");
        build.Observe(true, false); build.Observe(true, true);
        Check(!build.IsAdditional, "earlier regular work survives later all-additional crew");
        var unknown = new AiBuildProvenance(39, false); unknown.Observe(true, true);
        Check(!unknown.IsAdditional, "unobserved progressed tent is conservative");
        var recycled = new AiBuildProvenance(39, true); recycled.Observe(false, true); recycled.Observe(true, true);
        Check(!recycled.IsAdditional, "recycled/invalid contributor identity cannot produce additional product");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) build.Observe(true, true);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "bounded contribution fold allocates no per-worker/per-tick history");
    }
    private static void Execute(int index, int mode, int stack, bool staleGroup = false, bool staleMarker = false)
    {
        new Span<byte>((void*)state, 40064).Clear(); new Span<byte>((void*)capture, 256).Clear();
        callbacks = 0; adjust = mode == 2; Marshal.WriteInt32(state, mode == 0 ? 0 : 1);
        int stride = AiQuotaNativeContract.PlayerStride, group = 0x688;
        foreach (int field in AiQuotaNativeContract.Fields) Marshal.WriteInt32(image, field + stride, 5);
        Marshal.WriteInt32(image, 0x67E8400, 4); Marshal.WriteInt32(image, 0x9302C4, 1);
        Marshal.WriteInt32(image, 0x67E8400 + 0x490 + 0x6F0, 123);
        Marshal.WriteInt16(image, 0x67E8400 + 0x490 + 0xA82, 10);
        if (index == 1 && mode != 0) Marshal.WriteInt32(state, 64 + 4, staleMarker ? 124 : 123);
        Marshal.WriteInt32(image, 0x100000 + 0x160, 4);
        Marshal.WriteInt32(image, 0x100000 + 0x174, 4);
        Marshal.WriteInt32(image, 0x7CC6720 + group + 0x34, 123);
        Marshal.WriteInt16(image, 0x7CC6720 + group + 0x5C, 5);
        // The helper at the end of one original span is a return-only external
        // boundary. Its inputs still execute; its unrelated classifier is out of scope.
        Marshal.WriteByte(image, 0x29430, 0xC3);
        var site = AiQuotaNativeContract.Sites[index];
        ulong back = M + (uint)site.Rva + (uint)site.Bytes.Length;
        var original = AiQuotaNativeContract.Decode(site.Bytes, M + (uint)site.Rva);
        var endpoints = new HashSet<ulong> { back };
        foreach (var instruction in original)
            if (instruction.FlowControl == FlowControl.ConditionalBranch || instruction.FlowControl == FlowControl.UnconditionalBranch)
                if (instruction.NearBranchTarget < M + (uint)site.Rva || instruction.NearBranchTarget >= back) endpoints.Add(instruction.NearBranchTarget);
        ulong done = Code(a => {
            a.pushfq(); a.push(rax); a.mov(rax, C);
            for (int r = 1; r < Regs.Length; r++) if (r != 7) a.mov(__qword_ptr[rax + r * 8], Regs[r]);
            a.mov(rdx, __qword_ptr[rsp]); a.mov(__qword_ptr[rax], rdx);
            a.lea(rdx, __qword_ptr[rsp + 16]); a.mov(__qword_ptr[rax + 56], rdx);
            a.mov(rdx, __qword_ptr[rsp + 8]); a.mov(__qword_ptr[rax + 128], rdx);
            a.movdqu(__xmmword_ptr[rax + 144], xmm0); a.movdqu(__xmmword_ptr[rax + 160], xmm1);
            a.movdqu(__xmmword_ptr[rax + 176], xmm2); a.movdqu(__xmmword_ptr[rax + 192], xmm3);
            a.movdqu(__xmmword_ptr[rax + 208], xmm4); a.movdqu(__xmmword_ptr[rax + 224], xmm5);
            a.pop(rax); a.popfq(); a.add(rsp, stack); a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12);
            a.pop(rdi); a.pop(rsi); a.pop(rbp); a.pop(rbx); a.mov(eax, 1); a.ret();
        });
        var saved = new Dictionary<ulong, byte[]>();
        foreach (ulong endpoint in endpoints)
        {
            var previous = new byte[32]; Marshal.Copy((IntPtr)unchecked((long)endpoint), previous, 0, previous.Length); saved.Add(endpoint, previous);
            WriteCode(endpoint, a => a.jmp(done));
        }
        if (index == 6) WriteCode(back, a => { a.nop(); a.nop(); a.jmp(done); });
        ulong stub = Code(a => AiQuotaAssembly.Generate(a, original, back, index, M, S, CallbackPointer));
        ulong[] regs = Enumerable.Range(0, 16).Select(i => 0x11110000UL + (uint)i).ToArray();
        regs[0] = 4; regs[1] = M + 0x100000; regs[2] = 0; regs[3] = M; regs[4] = (uint)stride; regs[5] = 1;
        regs[6] = (uint)stride; regs[8] = 0; regs[9] = 1; regs[10] = M; regs[11] = M;
        regs[13] = 0; regs[14] = M + 0x366C210;
        switch (index)
        {
            case 0: regs[1] = M + 0x67E8400; regs[3] = 1; regs[13] = 1; break;
            case 1: regs[11] = M + 0x67E8400 + 0x490; break;
            case 3: regs[5] = M + 0x100000; break;
            case 4: regs[3] = 7; break;
            case 7: regs[2] = (uint)group; break;
            case 8: case 9: regs[9] = M + 0x366C210; regs[13] = (uint)stride; break;
            case 10: regs[1] = (uint)stride; regs[13] = M; regs[2] = 4; break;
            case 11: regs[2] = 4; break;
            case 22: regs[0] = staleGroup ? 124U : 123U; regs[2] = M + 0x7CC6720; regs[3] = 1; regs[4] = 1; break;
        }
        ulong wrapper = Code(a => {
            a.push(rbx); a.push(rbp); a.push(rsi); a.push(rdi); a.push(r12); a.push(r13); a.push(r14); a.push(r15); a.sub(rsp, stack);
            a.mov(rax, C); a.mov(__qword_ptr[rax + 248], rsp);
            a.mov(rax, 0xBABAUL); a.movq(xmm0, rax); a.movq(xmm1, rax); a.movq(xmm2, rax);
            a.movq(xmm3, rax); a.movq(xmm4, rax); a.movq(xmm5, rax);
            a.mov(rax, 0x246UL); a.push(rax); a.popfq();
            for (int r = 0; r < Regs.Length; r++) if (r != 7) a.mov(Regs[r], regs[r]);
            a.AddUnrestrictedJmp(stub);
        });
        try
        {
            Check(Marshal.GetDelegateForFunctionPointer<Run>((IntPtr)unchecked((long)wrapper))() == 1, "CPU returned " + index);
            Check(Marshal.ReadInt64(capture, 56) == Marshal.ReadInt64(capture, 248), "exact RSP " + index);
            Check(callbacks == (mode == 0 || staleGroup || staleMarker ? 0 : 1), "callback demand " + index + "/" + mode);
            for (int x = 144; x <= 224; x += 16) Check(Marshal.ReadInt64(capture, x) == 0xBABA && Marshal.ReadInt64(capture, x + 8) == 0, "XMM preserved " + index);
            foreach (int r in new[] { 12, 15 }) Check(unchecked((ulong)Marshal.ReadInt64(capture, r * 8)) == regs[r], "nonvolatile sentinel " + index);
            if (index == 4) Check(Marshal.ReadInt64(capture, 16) == (adjust ? 2 : 5), "adjusted MOV ECX");
            if (index == 7) Check(Marshal.ReadInt64(capture) == (adjust ? 2 : 5), "adjusted MOVSX EAX");
            if (index == 0) Check(Marshal.ReadInt32(state, 4) == 1, "natural fold serial advances even without demand");
            if (adjust)
            {
                if (index == 2) Check(unchecked((ulong)Marshal.ReadInt64(capture, 8)) == M + 0x100000, "adjusted harassment branch skips replacement mode");
                if (index == 3) Check(Marshal.ReadInt64(capture, 8) == 0, "adjusted defender branch preserves recruit mode");
                if (index == 11 || index >= 13 && index <= 21)
                    Check(Marshal.ReadInt64(capture, 64) == 1, "adjusted siege branch recruits and updates candidate total");
                if (index == 5 || index == 6 || index == 8 || index == 9 || index == 10)
                    Check((unchecked((ulong)Marshal.ReadInt64(capture, 128)) & 0x8D5) == (AiQuotaNativeContract.CompareFlags(0, 2, 4) & 0x8D5), "adjusted native comparison flags");
                if (index == 22) Check(unchecked((ulong)Marshal.ReadInt64(capture, 112)) == M + 0x7CC6720, "additional-only engineer pool chooses native recruit branch");
            }
            byte[] observed = new byte[240]; Marshal.Copy(capture, observed, 0, observed.Length);
            if (mode == 0) Baselines[(index, stack)] = observed;
            else if (mode == 1 && !staleGroup && !staleMarker)
            {
                byte[] baseline = Baselines[(index, stack)];
                for (int r = 0; r < 16; r++) if (r != 7)
                    Check(BitConverter.ToUInt64(baseline, r * 8) == BitConverter.ToUInt64(observed, r * 8), "every GPR equals dormant native semantics " + index + "/" + r);
                Check(BitConverter.ToUInt64(baseline, 128) == BitConverter.ToUInt64(observed, 128), "complete native status flags preserved " + index);
            }
            if (staleGroup)
            {
                Check(Marshal.ReadInt64(capture, 8) == AiQuotaNativeContract.PlayerStride, "stale group path retains native RBX player offset");
                Check(unchecked((ulong)Marshal.ReadInt64(capture, 112)) == M + 0x7CC6720, "stale group path enters original recruitment manager");
            }
        }
        finally { foreach (var pair in saved) Marshal.Copy(pair.Value, 0, (IntPtr)unchecked((long)pair.Key), pair.Value.Length); }
    }
    private static void Call(int index, IntPtr context)
    {
        callbacks++; ulong* c = (ulong*)context;
        if (index < 2) return;
        if (index == 22) { c[16] = AiQuotaNativeContract.CompareFlags(c[16], adjust ? 1U : 5U, 2); return; }
        var code = AiQuotaNativeContract.Decode(AiQuotaNativeContract.Sites[index].Bytes, M + (uint)AiQuotaNativeContract.Sites[index].Rva);
        Instruction operand = code[index == 3 ? 5 : 0];
        if (index == 4) c[2] = adjust ? 2U : 5U;
        else if (index == 7) c[0] = adjust ? 2U : 5U;
        else c[16] = AiQuotaNativeContract.CompareFlags(c[16], adjust ? 2U : 5U, (uint)c[RegIndex(operand.Op1Register)]);
    }
    private static int RegIndex(Register r) => r == Register.EAX ? 0 : r == Register.ECX ? 2 : throw new Exception("Unexpected fixture compare register");
    private static void CompareFlagTests()
    {
        // Compare the production arithmetic emulator with real x64 CMP on signed
        // extremes and carry/auxiliary/parity boundaries, preserving all other flags.
        foreach (uint left in new uint[] { 0, 1, 2, 15, 16, 255, 256, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF })
        foreach (uint right in new uint[] { 0, 1, 2, 15, 16, 255, 256, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF })
        {
            ulong address = Code(a => { a.mov(eax, left); a.cmp(eax, right); a.pushfq(); a.pop(rax); a.ret(); });
            var function = Marshal.GetDelegateForFunctionPointer<Flags>((IntPtr)unchecked((long)address));
            Check((function() & 0x8D5) == (AiQuotaNativeContract.CompareFlags(0, left, right) & 0x8D5), "exact CMP arithmetic flags");
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Flags();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RestoreFixture(IntPtr archive, IntPtr sections);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long StrategicFixture(IntPtr manager, int tribe, int action);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GlobalTaskFixture(IntPtr manager, int owner, int target);
    private static void ObserverEntryTests(byte[] file)
    {
        // These exact entry bytes are a native ABI contract, not an assertion about
        // folder names or implementation wording. The test executes the installed
        // NativeX64 backend and production demand intermediary against private code.
        byte[] restoreBytes = AiQuotaNativeContract.Hex("48895C24204889542410"), strategicBytes = AiQuotaNativeContract.Hex("448944241848894C2408");
        Check(ReadRva(file, 0x87D0, 10).SequenceEqual(restoreBytes), "canonical raw-restore entry");
        Check(ReadRva(file, 0x122B40, 10).SequenceEqual(strategicBytes), "canonical strategic entry");
        var backend = new NativeDetourBackend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, SuspendThreadsDuringPatch = false });
        ulong restoreTarget = Code(a => { a.db(restoreBytes); a.mov(eax, 1); a.ret(); });
        NativeDetour<RestoreFixture> restoreHook = null; int restored = 0;
        RestoreFixture restoreCallback = (archive, sections) => {
            Check(archive == (IntPtr)0x12345678 && sections == (IntPtr)0x23456789, "restore arguments survive backend");
            int result = restoreHook.Original(archive, sections); if (result == 1) restored++; return result;
        };
        var restoreRequest = new DetourRequest<RestoreFixture> { Name = "Private raw restore observer", TargetAddress = restoreTarget, Callback = restoreCallback };
        using (restoreHook = (NativeDetour<RestoreFixture>)backend.CreateDetour(in restoreRequest))
        {
            NativeIndirectEntry.Validate(restoreHook, (IntPtr)unchecked((long)restoreTarget), restoreBytes, false); checks++;
            restoreHook.Enable(); NativeIndirectEntry.Validate(restoreHook, (IntPtr)unchecked((long)restoreTarget), restoreBytes, true); checks++;
            var call = Marshal.GetDelegateForFunctionPointer<RestoreFixture>((IntPtr)unchecked((long)restoreTarget));
            Check(call((IntPtr)0x12345678, (IntPtr)0x23456789) == 1 && restored == 1, "raw restore original executes once before observer");
        }
        ulong strategicTarget = Code(a => { a.db(strategicBytes); a.mov(eax, 1); a.ret(); });
        NativeDetour<StrategicFixture> strategicHook = null; int ordered = 0;
        StrategicFixture strategicCallback = (manager, tribe, action) => {
            Check(manager == (IntPtr)0x34567890 && tribe == 4499 && action == 0x3F7, "strategic effective arguments survive intermediary");
            long result = strategicHook.Original(manager, tribe, action); if (result == 1) ordered++; return result;
        };
        var gate = new AiObserverDemandGate(state, strategicCallback);
        var strategicRequest = new DetourRequest<StrategicFixture> { Name = "Private strategic observer", TargetAddress = strategicTarget, Callback = strategicCallback, IntermediaryFactory = gate };
        using (strategicHook = (NativeDetour<StrategicFixture>)backend.CreateDetour(in strategicRequest))
        {
            NativeIndirectEntry.Validate(strategicHook, (IntPtr)unchecked((long)strategicTarget), strategicBytes, false); checks++;
            var assembler = new Assembler(64); AiObserverDemandGate.Generate(assembler, S, unchecked((ulong)strategicHook.TrampolineAddress.ToInt64()),
                unchecked((ulong)Marshal.GetFunctionPointerForDelegate(strategicCallback).ToInt64()));
            using (var stream = new MemoryStream())
            {
                assembler.Assemble(new StreamCodeWriter(stream), unchecked((ulong)strategicHook.HookEntryPointAddress.ToInt64()));
                NativeIndirectEntry.VerifyBytes(strategicHook.HookEntryPointAddress, stream.ToArray()); checks++;
            }
            strategicHook.Enable(); NativeIndirectEntry.Validate(strategicHook, (IntPtr)unchecked((long)strategicTarget), strategicBytes, true); checks++;
            var call = Marshal.GetDelegateForFunctionPointer<StrategicFixture>((IntPtr)unchecked((long)strategicTarget));
            Marshal.WriteInt32(state, 0);
            Check(call((IntPtr)0x34567890, 4499, 0x3F7) == 1 && ordered == 0, "dormant strategic call remains entirely native");
            Marshal.WriteInt32(state, 1);
            Check(call((IntPtr)0x34567890, 4499, 0x3F7) == 1 && ordered == 1, "active strategic original and observer execute once");
        }
        byte[] taskBytes = AiQuotaNativeContract.Hex("48895C24084489442418");
        Check(ReadRva(file, 0x10DF60, 10).SequenceEqual(taskBytes), "canonical global-task-builder entry");
        ulong taskTarget = Code(a => { a.db(taskBytes); a.inc(__dword_ptr[rcx]); a.ret(); });
        NativeDetour<GlobalTaskFixture> taskHook = null; int built = 0;
        GlobalTaskFixture taskCallback = (manager, owner, target) => {
            Check(manager == capture && owner == 8 && target == 319999, "global-task effective arguments survive intermediary");
            taskHook.Original(manager, owner, target); built++;
        };
        var taskGate = new AiObserverDemandGate(state, taskCallback);
        var taskRequest = new DetourRequest<GlobalTaskFixture> { Name = "Private global-task observer", TargetAddress = taskTarget, Callback = taskCallback, IntermediaryFactory = taskGate };
        using (taskHook = (NativeDetour<GlobalTaskFixture>)backend.CreateDetour(in taskRequest))
        {
            NativeIndirectEntry.Validate(taskHook, (IntPtr)unchecked((long)taskTarget), taskBytes, false); checks++;
            taskHook.Enable(); NativeIndirectEntry.Validate(taskHook, (IntPtr)unchecked((long)taskTarget), taskBytes, true); checks++;
            var call = Marshal.GetDelegateForFunctionPointer<GlobalTaskFixture>((IntPtr)unchecked((long)taskTarget));
            Marshal.WriteInt32(capture, 0); Marshal.WriteInt32(state, 0); call(capture, 8, 319999);
            Check(Marshal.ReadInt32(capture) == 1 && built == 0, "dormant global-task call remains entirely native");
            Marshal.WriteInt32(state, 1); call(capture, 8, 319999);
            Check(Marshal.ReadInt32(capture) == 2 && built == 1, "active global-task original executes once before observer");
        }
    }
    private static void RestoreFailureBranchTests(byte[] file)
    {
        foreach (var path in new[] {
            (Call:0x85496, Failure:0x85B4D, End:0x85B75, Condition:Mnemonic.Jle),
            (Call:0x94B15, Failure:0x96EBE, End:0x96F20, Condition:Mnemonic.Je),
            (Call:0x1006D7, Failure:0x102550, End:0x10256B, Condition:Mnemonic.Je) })
        {
            byte[] head = ReadRva(file, path.Call, 13);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(head)); decoder.IP = (uint)path.Call;
            var call = decoder.Decode(); var test = decoder.Decode(); var branch = decoder.Decode();
            Check(call.Mnemonic == Mnemonic.Call && call.NearBranchTarget == 0x87D0 && test.Mnemonic == Mnemonic.Test &&
                test.Op0Register == Register.EAX && test.Op1Register == Register.EAX && branch.Mnemonic == path.Condition &&
                branch.NearBranchTarget == (uint)path.Failure, "canonical raw-restore failure gate " + path.Call.ToString("X"));
            byte[] tail = ReadRva(file, path.Failure, path.End - path.Failure);
            var epilogue = AiQuotaNativeContract.Decode(tail, (uint)path.Failure);
            Check(epilogue.Last().Mnemonic == Mnemonic.Ret && epilogue.All(i => i.Mnemonic != Mnemonic.Jmp &&
                i.FlowControl != FlowControl.ConditionalBranch && (i.Mnemonic != Mnemonic.Call || i.NearBranchTarget == 0x1A64D0)),
                "failure path contains only original return epilogue/security-cookie check; no AI/fold continuation");
            Marshal.Copy(head, 0, image + path.Call, head.Length);
            WriteCode(M + (uint)path.Call + (uint)(call.Length + test.Length + branch.Length), a => { a.mov(eax, 1); a.add(rsp, 40); a.ret(); });
            WriteCode(M + (uint)path.Failure, a => { a.xor(eax, eax); a.add(rsp, 40); a.ret(); });
            ulong wrapper = Code(a => { a.sub(rsp, 40); a.AddUnrestrictedJmp(M + (uint)path.Call); });
            var invoke = Marshal.GetDelegateForFunctionPointer<Run>((IntPtr)unchecked((long)wrapper));
            foreach (int outcome in new[] { 0, 1 })
            {
                WriteCode(M + 0x87D0, a => { a.mov(eax, outcome); a.ret(); });
                Check(invoke() == outcome, "actual original caller branch retains native zero failure / one success " + path.Call.ToString("X"));
            }
        }
        // Standard security-cookie success path preserves RAX, including the zero
        // return selected by the original map/save callers' epilogues above.
        var cookie = AiQuotaNativeContract.Decode(ReadRva(file, 0x1A64D0, 21), 0x1A64D0);
        Check(cookie.Last().Mnemonic == Mnemonic.Ret && cookie.All(i => i.Op0Register != Register.RAX && i.Op0Register != Register.EAX), "cookie success path preserves native failure result");
    }
    private static void EngineeringGateTests(byte[] file)
    {
        var tracked = Marshal.AllocHGlobal(4000 * 4);
        try
        {
            int[] rvas = { 0xA65A0, 0xA8E40, 0xA93C0, 0xA9A30, 0xAA030, 0xAA630 };
            string[] hex = { "40535641544157", "40535641544157", "405356574155", "48895C242056", "48895C242056", "48895C242056" };
            for (int index = 0; index < rvas.Length; index++)
            {
                byte[] prefix = AiQuotaNativeContract.Hex(hex[index]);
                Check(ReadRva(file, rvas[index], prefix.Length).SequenceEqual(prefix), "engineering canonical entry " + index);
                // Execute precisely the displaced original prologue and a private tail
                // that reverses its saves. There is no unrelated game function body.
                ulong target = Code(a => {
                    a.db(prefix);
                    if (index < 2) { a.pop(r15); a.pop(r12); a.pop(rsi); a.pop(rbx); }
                    else if (index == 2) { a.pop(r13); a.pop(rdi); a.pop(rsi); a.pop(rbx); }
                    else { a.pop(rsi); a.mov(rbx, __qword_ptr[rsp + 32]); }
                    a.mov(eax, 17); a.ret();
                });
                Run callback = () => 29;
                var gate = new AiEngineeringNativeGate(M, S, unchecked((ulong)tracked.ToInt64()), unchecked((ulong)Marshal.GetFunctionPointerForDelegate(callback).ToInt64()));
                var request = new DetourRequest<Run> { Name = "Private engineering builder gate", TargetAddress = target, Callback = callback, IntermediaryFactory = gate };
                var backend = new NativeDetourBackend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, SuspendThreadsDuringPatch = false });
                using var hook = (NativeDetour<Run>)backend.CreateDetour(in request);
                Check(hook.DisplacedByteCount == prefix.Length && hook.Scheme == DetourScheme.Indirect, "engineering actual native backend span " + index);
                Check(new ReadOnlySpan<byte>((void*)unchecked((long)target), prefix.Length).SequenceEqual(prefix), "private original retains canonical redundant REX " + index);
                AiEngineeringNativeGate.ValidateEntry(hook, (IntPtr)unchecked((long)target), prefix, false); checks++;
                Check(Marshal.ReadByte(hook.TrampolineAddress) == (index < 3 ? 0x53 : 0x48), "actual Iced relocation normalizes only redundant REX " + index);
                // Both executable-source and full-trampoline corruption must still fail closed.
                byte source = Marshal.ReadByte((IntPtr)unchecked((long)target));
                Marshal.WriteByte((IntPtr)unchecked((long)target), unchecked((byte)(source ^ 1)));
                bool refusedSource = false;
                try { AiEngineeringNativeGate.ValidateEntry(hook, (IntPtr)unchecked((long)target), prefix, false); }
                catch (InvalidOperationException) { refusedSource = true; }
                finally { Marshal.WriteByte((IntPtr)unchecked((long)target), source); }
                Check(refusedSource, "engineering canonical source corruption rejected " + index);
                foreach (int position in new[] { 0, hook.TrampolineSize - 1 })
                {
                    byte value = Marshal.ReadByte(hook.TrampolineAddress, position);
                    Marshal.WriteByte(hook.TrampolineAddress, position, unchecked((byte)(value ^ 1)));
                    bool refused = false;
                    try { AiEngineeringNativeGate.ValidateEntry(hook, (IntPtr)unchecked((long)target), prefix, false); }
                    catch (InvalidOperationException) { refused = true; }
                    finally { Marshal.WriteByte(hook.TrampolineAddress, position, value); }
                    Check(refused, "engineering relocated prologue/resume corruption rejected " + index + "/" + position);
                }
                gate.Validate(hook.HookEntryPointAddress, unchecked((ulong)hook.TrampolineAddress.ToInt64()));
                hook.Enable(); AiEngineeringNativeGate.ValidateEntry(hook, (IntPtr)unchecked((long)target), prefix, true); checks++;
                var call = Marshal.GetDelegateForFunctionPointer<Run>((IntPtr)unchecked((long)target));
                new Span<byte>((void*)state, 40064).Clear(); new Span<byte>((void*)tracked, 16000).Clear();
                int building = 0x64CCBB0 + 0x32C;
                Marshal.WriteInt32(image, 0x8F9BE4, 1); Marshal.WriteInt32(image, building + 0x134, 123);
                Marshal.WriteInt16(image, building + 0x1F6, 0);
                Check(call() == 17, "engineering demand-zero original without callback " + index);
                Marshal.WriteInt32(state, 1);
                Check(call() == 17, "engineering untracked no-worker original " + index);
                Marshal.WriteInt32(tracked, 4, 123);
                Check(call() == 29, "engineering tracked identity callback " + index);
                Marshal.WriteInt32(tracked, 4, 124);
                Check(call() == 17, "engineering stale building identity original " + index);
                for (int slot = 0; slot < 4; slot++)
                {
                    new Span<byte>((void*)state, 40064).Clear(); Marshal.WriteInt32(state, 1);
                    Marshal.WriteInt16(image, building + 0x1F6, (short)(slot + 1));
                    for (int w = 0; w < 4; w++) { Marshal.WriteInt16(image, building + 0x1FA + w * 2, (short)(w + 1)); Marshal.WriteInt32(image, building + 0x204 + w * 4, 200 + w); }
                    Marshal.WriteInt32(state, 64 + (slot + 1) * 4, 200 + slot);
                    Check(call() == 29, "engineering worker slot/GID callback " + index + "/" + slot);
                    Marshal.WriteInt32(state, 64 + (slot + 1) * 4, 300 + slot);
                    Check(call() == 17, "engineering stale worker GID original " + index + "/" + slot);
                }
            }
        }
        finally { Marshal.FreeHGlobal(tracked); }
    }
    private static ulong Code(Action<Assembler> generate)
    { ulong address = M + 0x9400000 + (uint)(page++ * 4096); if (address + 4096 > M + Size) throw new Exception("Code arena exhausted"); WriteCode(address, generate); return address; }
    private static void WriteCode(ulong address, Action<Assembler> generate)
    { var a = new Assembler(64); generate(a); using var stream = new MemoryStream(); a.Assemble(new StreamCodeWriter(stream), address); byte[] bytes = stream.ToArray();
        Marshal.Copy(bytes, 0, (IntPtr)unchecked((long)address), bytes.Length); FlushInstructionCache(GetCurrentProcess(), (IntPtr)unchecked((long)address), (UIntPtr)bytes.Length); }
    private static byte[] ReadRva(byte[] file, int rva, int length)
    {
        int pe = BitConverter.ToInt32(file, 0x3C), count = BitConverter.ToUInt16(file, pe + 6), table = pe + 24 + BitConverter.ToUInt16(file, pe + 20);
        for (int i = 0; i < count; i++)
        { int row = table + i * 40, va = BitConverter.ToInt32(file, row + 12), size = BitConverter.ToInt32(file, row + 16);
            if (rva >= va && rva + length <= va + size) return file.Skip(BitConverter.ToInt32(file, row + 20) + rva - va).Take(length).ToArray(); }
        throw new Exception("Native RVA outside file");
    }
}
