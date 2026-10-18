using APIShared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using SHCDESE.API;
using SHCDESE.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[TestClass]
public sealed unsafe class AudioNativeContractTests
{
    private static string game;
    private static readonly List<object> roots = new List<object>();
    private static int checks;
    [TestMethod]
    public void AuditedBinaryPathsMembersMarkerTransportIlAndNativeBackend()
    {
        game = APISharedTests.InstalledTestEnvironment.GameDirectory();
        Run();
    }
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException(message);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var files = new InstalledAudioFiles(game);
        files.Validate();
        Check(NativeContract.HashFile(files.Native) == NativeContract.NativeHash, "native identity");
        Check(NativeContract.HashFile(files.Managed) == NativeContract.ManagedHash, "managed identity");
        Check(NativeContract.HashFile(files.Backend) == NativeContract.BackendHash, "installed backend identity");
        Paths(files);
        SoundRuntime.ValidateMembers();
        SoundRuntime.ValidateUnitMembers();
        Members();
        Policy();
        Il();
        byte[] image = File.ReadAllBytes(files.Native);
        Probe(image, NativeContract.WrapperRva, 40, NativeContract.WrapperBytes);
        Probe(image, NativeContract.FlagWrapperRva, 40, NativeContract.FlagWrapperBytes);
        Probe(image, NativeContract.EmitRva, 137, NativeContract.EmitBytes);
        KillSites(image);
    }
    private static void Paths(InstalledAudioFiles files)
    {
        SoundContextPolicy.SetReady(false);
        var fromBytes = Assembly.Load(File.ReadAllBytes(files.Managed));
        Check(fromBytes.Location == "", "real byte-loaded assembly has empty Location like Unity");
        files.Validate();
        Check(File.Exists(files.Managed) && File.Exists(files.Native) && File.Exists(files.Backend), "canonical files resolve independently of empty Location");
        bool missing = false;
        try { InstalledAudioFiles.ValidateFile(files.Managed + ".missing-unit-audio", NativeContract.ManagedHash); }
        catch (FileNotFoundException) { missing = true; }
        Check(missing && !SoundContextPolicy.Active, "missing file rejected before activation");
        bool changed = false;
        try { InstalledAudioFiles.ValidateFile(files.Managed, new string('0', 64)); }
        catch (InvalidOperationException) { changed = true; }
        Check(changed && !SoundContextPolicy.Active, "hash mismatch rejected before activation");
        bool empty = false;
        try { new InstalledAudioFiles(""); }
        catch (ArgumentException) { empty = true; }
        Check(empty, "missing game root fails clearly");
    }
    private static void Members()
    {
        foreach (var spec in new[] { Tuple.Create("r_UnitChimp", 0x8A, "eChimps"), Tuple.Create("r_AIState", 0x2BC, "UInt16"),
            Tuple.Create("r_CurrentTilePositionX", 0xC0, "UInt16"), Tuple.Create("r_CurrentTilePositionY", 0xC2, "UInt16"),
            Tuple.Create("r_GlobalId", 0x94, "UInt32"), Tuple.Create("r_CurrentHealth", 0x3C4, "UInt32") })
        {
            var field = typeof(GameUnit).GetField(spec.Item1);
            Check(field != null && field.IsPublic && field.FieldType.Name == spec.Item3 &&
                Marshal.OffsetOf(typeof(GameUnit), spec.Item1).ToInt32() == spec.Item2, "installed GameUnit member " + spec.Item1);
        }
        Check(typeof(GameUnit).StructLayoutAttribute.Size == 0x490, "unit stride");
        var lookup = typeof(GameUnitManagerAPI).GetMethod("TryGetUnitById", new[] { typeof(int), typeof(GameUnit*).MakeByRefType() });
        Check(lookup != null && lookup.IsPublic && lookup.ReturnType == typeof(bool), "installed pointer lookup");
        Check(typeof(GameUnitManagerAPI).GetProperty("Instance").GetMethod.IsPublic, "public manager singleton");
        var access = typeof(APIShared.UnitAccess).GetMethod("TryGetById", new[] { typeof(int), typeof(GameUnit*).MakeByRefType(), typeof(APIShared.UnitLookupFailure).MakeByRefType(), typeof(string), typeof(string), typeof(int) });
        Check(access != null && access.IsPublic && access.IsStatic && access.ReturnType == typeof(bool), "public UnitAccess signature from fresh package");
        Check(!APIShared.UnitAccess.TryGetById(null, 1, out GameUnit* absent, out var failure) && absent == null && failure == APIShared.UnitLookupFailure.ManagerUnavailable, "central manager-unavailable rejection");
        Check(typeof(BepInEx.Paths).GetProperty("GameRootPath").GetMethod.IsPublic, "public BepInEx game root");
        var play = typeof(SFXManager).GetMethod("playSound", new[] { typeof(int), typeof(float), typeof(float), typeof(bool) });
        Check(play != null && play.IsPublic && !play.IsStatic && play.ReturnType == typeof(void), "real playSound access");
    }
    private static short[] Record()
    {
        var record = new short[21]; record[0] = 16; record[3] = 180; record[11] = record[12] = -1; return record;
    }
    private static void Policy()
    {
        SoundContextPolicy.SetReady(true);
        Check(SoundSources.TryGet(0x1700D2, out var draw), "source-specific draw origin");
        Check(!SoundSources.TryGet(0x1700D3, out _), "unknown original return address");
        Check(SoundSources.All.Length == 91, "complete source inventory");
        var markers = new HashSet<short>();
        foreach (var source in SoundSources.All) foreach (int effect in source.Effects) {
            if (source.Situation(effect) == SoundSituation.Unknown) continue;
            int state = source.ReturnRva == 0x1700D2 ? 1 : 101;
            Check(SoundContextPolicy.MatchesSource(source, source.UnitType, state, -17, 300, -17, 300, effect, source.Flag), "state changes before emission do not erase source");
            Check(!SoundContextPolicy.MatchesSource(source, 999, state, -17, 300, -17, 300, effect, source.Flag), "wrong unit type rejected");
            Check(!SoundContextPolicy.MatchesSource(source, source.UnitType, state, -17, 300, -16, 300, effect, source.Flag), "wrong coordinates rejected");
            Check(!SoundContextPolicy.MatchesSource(source, source.UnitType, state, -17, 300, -17, 300, effect, 1-source.Flag), "wrong wrapper flag rejected");
            Check(!SoundContextPolicy.MatchesSource(source, source.UnitType, state, -17, 300, -17, 300, 329, source.Flag), "unaudited effect rejected");
            var payload = Record(); payload[3] = (short)effect; payload[13] = (short)source.Flag;
            Check(SoundContextPolicy.TryMark(payload, source), "catalog record " + source.Key);
            Check(markers.Add(payload[11]), "unique negative source+effect marker");
            var value = SoundContextPolicy.Read(payload, 0, effect, 1, 0);
            Check(value.SourceKey == source.Key && value.UnitType == source.UnitType && value.Situation == source.Situation(effect), "source marker roundtrip");
            Check(value.SituationConfirmed == (source.ReturnRva == 0x1700D2 && effect == 180), "confirmation does not leak to another source/type");
            for (int pool = 0; pool < 6; pool++) Check(SoundContextPolicy.Read((short[])payload.Clone(), 0, effect, 1, 0).SourceKey == source.Key, "all pool/copy representations");
        }
        var record = Record(); var original = (short[])record.Clone();
        Check(SoundContextPolicy.TryMark(record, draw), "mark eligible record");
        Check(record[11] < 0 && record[12] < 0, "Vanilla spatial branch retained");
        for (int i=0;i<21;i++) Check(i==11 || record[i]==original[i], "only marker changed");
        Check(SoundContextPolicy.Read(record,0,180,0.8f,0).Situation==SoundSituation.ArabianIdleSwordDraw,"known context");
        Check(SoundContextPolicy.Read(original,0,180,0.8f,0).Situation==SoundSituation.Unknown,"same sound unrelated context");
        var combined=new short[63]; Array.Copy(original,0,combined,0,21); Array.Copy(record,0,combined,21,21); Array.Copy(original,0,combined,42,21);
        for(int i=0;i<6;i++) { var buffer=(short[])combined.Clone(); Check(SoundContextPolicy.Read(buffer,1,180,1,0).Situation==SoundSituation.ArabianIdleSwordDraw,"pending copy/pool transport"); }
        Array.Copy(original,0,combined,21,21); Check(SoundContextPolicy.Read(combined,1,180,1,0).Situation==SoundSituation.Unknown,"slot reuse");
        Check(SoundContextPolicy.Read(record,-1,180,1,0).Kind==SoundPlaybackKind.Unknown && SoundContextPolicy.Read(record,int.MaxValue,180,1,0).Kind==SoundPlaybackKind.Unknown,"bounds");
        foreach(int field in new[]{0,3,11,12,13}) { var bad=Record();bad[field]=9;Check(!SoundContextPolicy.TryMark(bad,draw),"malformed record "+field); }
        SoundContextPolicy.SetReady(false); Check(SoundContextPolicy.Read(record,0,180,1,0).Situation==SoundSituation.Unknown,"failed native context leaves generic playback");
        Check(!SoundContextPolicy.TryMark(Record(),draw),"inactive capture");
        SoundContextPolicy.SetReady(true);
    }
    private static void Il()
    {
        var method=typeof(GameMap).GetMethod("processTestMap",new[]{typeof(short[]),typeof(int),typeof(EngineInterface.PlayState),typeof(byte[])});
        Check(method!=null && method.IsPublic && method.ReturnType==typeof(void),"real processTestMap access");
        SoundIlContract.Validate(method);
        using(var definition=new DynamicMethodDefinition(method))
        using(var context=new ILContext(definition.Definition))
        {
            var calls=SoundIlContract.Find(context,out var index); Check(calls.Length==2 && index.VariableType.FullName=="System.Int32","two call sites and loop local");
            SoundIlContract.Apply(context);
            Check(!context.Body.Instructions.Any(i=>calls.Contains(i)),"both original IL calls replaced");
            Check(context.Body.Instructions.Count(i=>i.OpCode==OpCodes.Ldarg_1)>=2,"buffer argument emitted");
            var generated=definition.Generate();
            var executable=generated.CreateDelegate(typeof(Action<GameMap,short[],int,EngineInterface.PlayState,byte[]>));
            // Unity's native ECalls cannot be JIT-compiled by desktop CLR.
            // Generate the real patched body/delegate; actual execution is an
            // explicit in-game acceptance check, not a fake Unity replacement.
            Check(executable!=null,"patched real GameMap IL generates a CLR delegate");
            bool rejected=false;try{SoundIlContract.Find(context,out _);}catch(InvalidOperationException){rejected=true;}Check(rejected,"second patch rejected");
        }
        foreach (bool useSwitch in new[] { false, true })
        using(var definition=new DynamicMethodDefinition(method))
        using(var context=new ILContext(definition.Definition))
        {
            var calls=SoundIlContract.Find(context,out _);
            context.Body.Instructions.Insert(0, useSwitch ? Instruction.Create(OpCodes.Switch, new[] { calls[0] }) : Instruction.Create(OpCodes.Br, calls[0]));
            int count=context.Body.Instructions.Count;
            bool rejected=false;try{SoundIlContract.Apply(context);}catch(InvalidOperationException){rejected=true;}
            Check(rejected && context.Body.Instructions.Count==count,"incoming branch/switch rejected without mutation");
        }
        using(var definition=new DynamicMethodDefinition(method))
        using(var context=new ILContext(definition.Definition))
        {
            var calls=SoundIlContract.Find(context,out _);context.Body.Instructions.Remove(calls[0]);
            bool rejected=false;try{SoundIlContract.Apply(context);}catch(InvalidOperationException){rejected=true;}Check(rejected,"changed IL rejected before mutation");
        }
    }
    private static int Offset(byte[] image,int rva)
    {
        int pe=BitConverter.ToInt32(image,0x3C),count=BitConverter.ToUInt16(image,pe+6),optional=BitConverter.ToUInt16(image,pe+20);
        for(int i=0;i<count;i++){int h=pe+24+optional+i*40,start=BitConverter.ToInt32(image,h+12),size=BitConverter.ToInt32(image,h+16);
            if(rva>=start && rva<start+size)return BitConverter.ToInt32(image,h+20)+rva-start;}
        throw new InvalidOperationException("Unmapped test RVA");
    }
    private static void Probe(byte[] image,int rva,int size,byte[] entry)
    {
        // This separate process owns private executable copies. Published probes
        // and their memory remain rooted until process exit, like the real hooks.
        IntPtr copy=VirtualAlloc(IntPtr.Zero,new UIntPtr((uint)size),0x3000,0x40);
        Check(copy!=IntPtr.Zero,"private executable copy");Marshal.Copy(image,Offset(image,rva),copy,size);
        for(int i=0;i<entry.Length;i++)Check(Marshal.ReadByte(copy,i)==entry[i],"copied prologue");
        if (rva == NativeContract.EmitRva)
        {
            ProbeEmitterBridge(copy, size);
            return;
        }
        ProbeBridge(copy, size, entry);
    }
    private static void ProbeEmitterBridge(IntPtr copy, int size)
    {
        // Execute the copied emitter (no RIP operands) against a private manager: the dormant
        // guard must reach the original without the managed callback, the active path must
        // enter it once, and both must append exactly one record.
        IntPtr demand = Marshal.AllocHGlobal(8); Marshal.WriteInt64(demand, 0);
        IntPtr manager = Marshal.AllocHGlobal(16), records = Marshal.AllocHGlobal(42 * 4);
        Marshal.WriteIntPtr(manager, records); Marshal.WriteInt32(manager, 8, 0);
        NativeDetour<SoundRuntime.Emit> hook = null; int callbacks = 0;
        SoundRuntime.Emit callback = (m, sound, x, y, flag) => { callbacks++; hook.Original(m, sound, x, y, flag); };
        var bridge = new SoundEmitterBridge(callback, demand);
        var request = new DetourRequest<SoundRuntime.Emit> { Name = "private emitter bridge", TargetAddress = unchecked((ulong)copy.ToInt64()), Callback = callback, IntermediaryFactory = bridge };
        hook = (NativeDetour<SoundRuntime.Emit>)NativeContract.Backend(size, false).CreateDetour(in request);
        roots.Add(demand); roots.Add(manager); roots.Add(records); roots.Add(callback); roots.Add(bridge); roots.Add(hook); roots.Add(copy);
        NativeContract.Validate(hook, 14, request.TargetAddress, NativeContract.EmitBytes); bridge.Validate(hook);
        hook.Enable(); NativeContract.Validate(hook, 14, request.TargetAddress, NativeContract.EmitBytes); bridge.Validate(hook);
        Check(hook.IsInstalled, "copied emitter hook installed");
        var invoke = Marshal.GetDelegateForFunctionPointer<SoundRuntime.Emit>(copy); roots.Add(invoke);
        invoke(manager, 180, -17, 300, 1);
        Check(callbacks == 0 && Marshal.ReadInt32(manager, 8) == 1, "dormant emitter guard skips managed code and runs the original once");
        Marshal.WriteByte(demand, 1);
        invoke(manager, 181, -18, 301, 0);
        Check(callbacks == 1 && Marshal.ReadInt32(manager, 8) == 2, "active emitter guard enters managed code once and runs the original once");
        short Word(int record, int word) => Marshal.ReadInt16(records, record * 42 + word * 2);
        Check(Word(0, 0) == 16 && Word(0, 1) == -17 && Word(0, 2) == 300 && Word(0, 3) == 180 && Word(0, 11) == -1 && Word(0, 12) == -1 && Word(0, 13) == 1, "dormant record");
        Check(Word(1, 0) == 16 && Word(1, 1) == -18 && Word(1, 2) == 301 && Word(1, 3) == 181 && Word(1, 13) == 0, "active record");
    }
    private static void ProbeBridge(IntPtr copy, int size, byte[] expected)
    {
        // Execute the complete copied 40-byte wrapper. Relocate only its RIP manager
        // reference and CALL operand into private observer storage/code.
        int emissions = 0; bool flagged = expected[11] != 0;
        IntPtr manager = VirtualAlloc(IntPtr.Zero,new UIntPtr(64),0x3000,0x40);
        SoundRuntime.Emit observer = (m,sound,x,y,flag) => {
            Check(m == manager && sound == 180 && x == -17 && y == 300 && flag == (flagged ? 1 : 0), "complete copied wrapper forwards effect/coords/flag once");
            emissions++;
        };
        var emitEntry = RedBird.X64.Assembly.ExecutableFunction.Create(asm => {
            asm.mov(Iced.Intel.AssemblerRegisters.rax, unchecked((ulong)Marshal.GetFunctionPointerForDelegate(observer).ToInt64()));
            asm.jmp(Iced.Intel.AssemblerRegisters.rax);
        });
        roots.Add(manager);roots.Add(observer);roots.Add(emitEntry);
        // Relative targets need to be within signed rel32 distance. Move both tiny
        // endpoints into the wrapper's private page; VirtualAlloc rounds 40 to one page.
        var observerCode = Read(emitEntry.EntryPoint,12);Marshal.Copy(observerCode,0,copy+128,observerCode.Length);
        Marshal.WriteInt32(copy,21,256-25);Marshal.WriteInt32(copy,31,128-35);
        manager = copy+256;
        IntPtr demand = Marshal.AllocHGlobal(8); Marshal.WriteInt64(demand, 0); roots.Add(demand);
        NativeDetour<SoundRuntime.Wrapper> hook = null;
        int callbacks = 0; ulong captured = 0, frame = 0, rbp = 0; long slot = 0;
        SoundRuntime.OriginWrapper callback = (unused,x,y,sound,entry) => {
            Check(unused == new IntPtr(123) && x == -17 && y == 300 && sound == 180, "bridge preserves four Win64 arguments");
            captured = *(ulong*)entry; frame = entry; rbp = *(ulong*)(entry - SoundReturnAddressBridge.CallerRbpBelowFrame);
            slot = *(long*)(entry + NativeContract.MeleeAttackerFrameOffset);
            callbacks++; hook.Original(unused,x,y,sound);
        };
        SoundRuntime.Wrapper fallback = (unused,x,y,sound) => { throw new InvalidOperationException("bridge must capture origin"); };
        var bridge = new SoundReturnAddressBridge(callback, demand);
        var request = new DetourRequest<SoundRuntime.Wrapper> { Name="private source bridge",TargetAddress=unchecked((ulong)copy.ToInt64()),Callback=fallback,IntermediaryFactory=bridge };
        hook = (NativeDetour<SoundRuntime.Wrapper>)NativeContract.Backend(size, false).CreateDetour(in request);
        NativeContract.Validate(hook,15,request.TargetAddress,expected); bridge.Validate(hook);
        roots.Add(copy);roots.Add(hook);roots.Add(callback);roots.Add(fallback);roots.Add(bridge);
        hook.Enable();NativeContract.Validate(hook,15,request.TargetAddress,expected);bridge.Validate(hook);
        // Native caller fixes the expected return PC, RBP and a home-slot value at caller RSP+0x70,
        // the melee damage routine's attacker spill, rather than depending on CLR stub details.
        const ulong RbpSentinel = 0x1122334455667788; const long SlotSentinel = 0x490L * 1234;
        var caller = RedBird.X64.Assembly.ExecutableFunction.Create(asm => {
            asm.push(Iced.Intel.AssemblerRegisters.rbp);
            asm.sub(Iced.Intel.AssemblerRegisters.rsp, 0x80);
            asm.mov(Iced.Intel.AssemblerRegisters.rbp, RbpSentinel);
            asm.mov(Iced.Intel.AssemblerRegisters.rax, unchecked((ulong)SlotSentinel));
            asm.mov(Iced.Intel.AssemblerRegisters.__qword_ptr[Iced.Intel.AssemblerRegisters.rsp + 0x70], Iced.Intel.AssemblerRegisters.rax);
            asm.mov(Iced.Intel.AssemblerRegisters.rax, unchecked((ulong)copy.ToInt64()));
            asm.call(Iced.Intel.AssemblerRegisters.rax);
            asm.add(Iced.Intel.AssemblerRegisters.rsp, 0x80);
            asm.pop(Iced.Intel.AssemblerRegisters.rbp); asm.ret();
        });
        roots.Add(caller);
        var decoder = Iced.Intel.Decoder.Create(64,new Iced.Intel.ByteArrayCodeReader(Read(caller.EntryPoint,48)));
        decoder.IP = unchecked((ulong)caller.EntryPoint.ToInt64());
        Iced.Intel.Instruction call;
        do call = decoder.Decode(); while (call.Mnemonic != Iced.Intel.Mnemonic.Call);
        var invoke = Marshal.GetDelegateForFunctionPointer<SoundRuntime.Wrapper>(caller.EntryPoint);roots.Add(invoke);
        invoke(new IntPtr(123), -17, 300, 180);
        Check(callbacks == 0 && emissions == 1, "dormant demand guard runs the displaced prologue and original once without managed code");
        Marshal.WriteByte(demand, 1);
        invoke(new IntPtr(123), -17, 300, 180);
        Check(callbacks == 1 && emissions == 2 && captured == call.NextIP, "executed intermediary captures original native return address and resumes displaced prologue");
        Check((ulong)Marshal.ReadInt64(new IntPtr(unchecked((long)frame))) == captured && rbp == RbpSentinel && slot == SlotSentinel,
            "entry RSP points at the return address; spilled caller RBP and caller RSP+0x70 reach the managed callback unchanged");
        Marshal.WriteByte(demand, 0);
        invoke(new IntPtr(123), -17, 300, 180);
        Check(callbacks == 1 && emissions == 3, "guard returns to the dormant path");
    }
    // Melee kill catalog against the real image: pinned frame setup, frame instructions, CALL targets,
    // the attacker-type groups decoded from the routine's own jump tables, and the file-level proof
    // that nothing else in the body changes RBP, RSP or the attacker spill slot.
    private static void KillSites(byte[] image)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            Check(BitConverter.ToString(sha.ComputeHash(image, Offset(image, NativeContract.MeleeFrameRva), NativeContract.MeleeFrameLength)).Replace("-", "") == NativeContract.MeleeFrameHash, "melee frame setup hash");
        MeleeFrameInvariants(image);
        bool Bytes(int rva, byte[] expected) { int at = Offset(image, rva); for (int i = 0; i < expected.Length; i++) if (image[at + i] != expected[i]) return false; return true; }
        Check(Bytes(NativeContract.MeleeDamageRva, NativeContract.MeleePrologueBytes), "melee prologue: rbx home, seven pushes, sub rsp,30h");
        Check(Bytes(NativeContract.MeleeVictimRbpRva, NativeContract.MeleeVictimRbpBytes) && Bytes(NativeContract.MeleeAttackerSlotRva, NativeContract.MeleeAttackerSlotBytes), "victim RBP and attacker spill");
        Check(Bytes(0x1991CE, new byte[] { 0x4D, 0x69, 0xD6, 0x90, 0x04, 0x00, 0x00 }) && Bytes(0x19913C, new byte[] { 0x4D, 0x8B, 0xF4 }) && Bytes(0x199131, new byte[] { 0x4C, 0x63, 0xE2 }), "spill = attacker(edx)*0x490");
        Check(SoundSources.Kills.Length == 4, "kill source inventory");
        foreach (var kill in SoundSources.Kills)
        {
            int call = Offset(image, kill.ReturnRva - 5);
            Check(image[call] == 0xE8 && kill.ReturnRva + BitConverter.ToInt32(image, call + 1) == NativeContract.WrapperRva, "kill CALL targets the spatial wrapper: " + kill.Key);
        }
        // Effect immediates selected on each branch (mov edi/r9d, imm32).
        foreach (var spec in new[] { Tuple.Create(0x199686, 0xBF, 240), Tuple.Create(0x1996C0, 0xBF, 241), Tuple.Create(0x1996CB, 0xBF, 240), Tuple.Create(0x1992BB, 0xBF, 242),
            Tuple.Create(0x199771, 0xBF, 240), Tuple.Create(0x199783, 0xBF, 241) })
            Check(image[Offset(image, spec.Item1)] == spec.Item2 && BitConverter.ToInt32(image, Offset(image, spec.Item1) + 1) == spec.Item3, "edi effect at 0x" + spec.Item1.ToString("X"));
        foreach (var spec in new[] { Tuple.Create(0x199704, 187), Tuple.Create(0x19971F, 118), Tuple.Create(0x199732, 153), Tuple.Create(0x199756, 186),
            Tuple.Create(0x1997A9, 185), Tuple.Create(0x1997C4, 116), Tuple.Create(0x1997D7, 151), Tuple.Create(0x199803, 188), Tuple.Create(0x19982C, 154),
            Tuple.Create(0x1993E3, 187), Tuple.Create(0x1993EE, 186), Tuple.Create(0x1993F9, 185), Tuple.Create(0x199404, 188) })
            Check(image[Offset(image, spec.Item1)] == 0x41 && image[Offset(image, spec.Item1) + 1] == 0xB9 && BitConverter.ToInt32(image, Offset(image, spec.Item1) + 2) == spec.Item2, "r9d effect at 0x" + spec.Item1.ToString("X"));
        Check(image[Offset(image, 0x19981A)] == 0x45 && image[Offset(image, 0x19981D)] == 0x77, "lea r9d,[r14+0x77] with r14d=0 selects 119");
        Dictionary<int, List<int>> Groups(int index, int table, int count)
        {
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++) {
                int target = BitConverter.ToInt32(image, Offset(image, table) + image[Offset(image, index) + i] * 4);
                if (!groups.TryGetValue(target, out var list)) groups[target] = list = new List<int>();
                list.Add(i + 3);
            }
            return groups;
        }
        var killSwitch = Groups(0x199AF0, 0x199AD4, 0x53); var special = Groups(0x1999B8, 0x1999A0, 0x53);
        // Effects each branch can select (immediates checked above); the accepted attacker types per
        // effect must equal the union of the jump-table groups reaching those branches, nothing more.
        void Expect(int source, Dictionary<int, List<int>> table, params Tuple<int, int[]>[] producers)
        {
            var cues = SoundSources.Kills[source].Cues.ToDictionary(cue => cue.Key, cue => cue.Value);
            Check(cues.Count == producers.Length, "kill cue count " + SoundSources.Kills[source].Key);
            foreach (var producer in producers)
            {
                var expected = producer.Item2.SelectMany(branch => table[branch]).Distinct().OrderBy(type => type).ToArray();
                Check(cues.TryGetValue(producer.Item1, out var types) && types.SequenceEqual(expected), "attacker types for " + SoundSources.Kills[source].Key + " effect " + producer.Item1);
            }
        }
        Expect(0, killSwitch, Tuple.Create(240, new[] { 0x19967F, 0x1996AA, 0x199742 }), Tuple.Create(241, new[] { 0x1996AA, 0x199742 }),
            Tuple.Create(242, new[] { 0x19967F, 0x1996AA }), Tuple.Create(186, new[] { 0x199742 }), Tuple.Create(187, new[] { 0x1996F0 }),
            Tuple.Create(118, new[] { 0x1996F0 }), Tuple.Create(153, new[] { 0x1996F0 }), Tuple.Create(185, new[] { 0x199795 }),
            Tuple.Create(116, new[] { 0x199795 }), Tuple.Create(151, new[] { 0x199795 }), Tuple.Create(188, new[] { 0x1997E7 }));
        Expect(1, killSwitch, Tuple.Create(119, new[] { 0x1997E7 }));
        Expect(2, killSwitch, Tuple.Create(154, new[] { 0x1997E7 }));
        Expect(3, special, Tuple.Create(242, new[] { 0x1992F8 }), Tuple.Create(187, new[] { 0x1993E3 }), Tuple.Create(186, new[] { 0x1993EE }),
            Tuple.Create(185, new[] { 0x1993F9 }), Tuple.Create(188, new[] { 0x199404 }));
        Check(killSwitch[0x199841].Count == 62 && !killSwitch[0x199841].Any(type => SoundSources.Kills[0].Cues.Any(cue => SoundSources.Kills[0].Accepts(cue.Key, type))), "default-branch attackers are never attributed");
        Check(!SoundSources.Kills[0].Accepts(246, 23) && !SoundSources.Kills[0].Accepts(120, 25), "victim voices and siege cues are not kill cues");
        // Matching: victim tile and zero health, or attacker tile for the special-victim CALL.
        Check(SoundSources.Kills[0].Matches(240, 25, 1, 1, 5, 6, 0, 5, 6) && !SoundSources.Kills[0].Matches(240, 25, 1, 1, 5, 6, 1, 5, 6) &&
            !SoundSources.Kills[0].Matches(240, 25, 5, 6, 1, 1, 0, 5, 6) && !SoundSources.Kills[0].Matches(240, 24, 1, 1, 5, 6, 0, 5, 6), "victim-tile kill matching");
        Check(SoundSources.Kills[3].Matches(186, 25, 5, 6, 1, 1, 0, 5, 6) && !SoundSources.Kills[3].Matches(186, 25, 1, 1, 5, 6, 0, 5, 6), "attacker-tile special-victim matching");
    }
    // Decodes the hash-pinned file body after the prologue. RBP is written only by movsxd rbp,r8d (and
    // the epilogue pop), [rsp+0x70] only by the attacker spill, and RSP changes only in the epilogue,
    // so both values hold at every kill CALL. The routine itself reads them again after the cue choice.
    private static void MeleeFrameInvariants(byte[] image)
    {
        int start = NativeContract.MeleeFrameRva, end = NativeContract.MeleeDamageEndRva;
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(image, Offset(image, start), end - start));
        decoder.IP = (ulong)start;
        var factory = new Iced.Intel.InstructionInfoFactory();
        var rbpWriters = new List<ulong>(); var slotWriters = new List<ulong>(); var rspWriters = new List<ulong>();
        while (decoder.IP < (ulong)end)
        {
            var instruction = decoder.Decode();
            Check(instruction.Code != Iced.Intel.Code.INVALID, "melee body decodes linearly at 0x" + instruction.IP.ToString("X"));
            var info = factory.GetInfo(instruction);
            foreach (var used in info.GetUsedRegisters())
            {
                bool write = used.Access == Iced.Intel.OpAccess.Write || used.Access == Iced.Intel.OpAccess.CondWrite ||
                    used.Access == Iced.Intel.OpAccess.ReadWrite || used.Access == Iced.Intel.OpAccess.ReadCondWrite;
                if (write && Iced.Intel.RegisterExtensions.GetFullRegister(used.Register) == Iced.Intel.Register.RBP) rbpWriters.Add(instruction.IP);
                if (write && Iced.Intel.RegisterExtensions.GetFullRegister(used.Register) == Iced.Intel.Register.RSP && instruction.FlowControl != Iced.Intel.FlowControl.Call) rspWriters.Add(instruction.IP);
            }
            foreach (var memory in info.GetUsedMemory())
                if (memory.Base == Iced.Intel.Register.RSP && memory.Displacement == 0x70 &&
                    (memory.Access == Iced.Intel.OpAccess.Write || memory.Access == Iced.Intel.OpAccess.CondWrite || memory.Access == Iced.Intel.OpAccess.ReadWrite))
                    slotWriters.Add(instruction.IP);
        }
        Check(rbpWriters.SequenceEqual(new ulong[] { 0x199124, 0x19999B }), "RBP written only by movsxd rbp,r8d and the epilogue pop: " + string.Join(",", rbpWriters.Select(ip => ip.ToString("X"))));
        Check(slotWriters.SequenceEqual(new ulong[] { 0x1991D5 }), "[rsp+0x70] written only by the attacker spill: " + string.Join(",", slotWriters.Select(ip => ip.ToString("X"))));
        Check(rspWriters.All(ip => ip >= 0x19998D), "RSP changes only in the epilogue: " + string.Join(",", rspWriters.Select(ip => ip.ToString("X"))));
        bool Bytes(int rva, params byte[] expected) { int at = Offset(image, rva); for (int i = 0; i < expected.Length; i++) if (image[at + i] != expected[i]) return false; return true; }
        Check(Bytes(0x19964D, 0x4C, 0x8B, 0x54, 0x24, 0x70) && Bytes(0x199742, 0x48, 0x69, 0xC5, 0x90, 0x04, 0x00, 0x00),
            "the routine itself reads [rsp+0x70] (0x19964D) and RBP (0x199742) when it chooses the kill cue");
    }

    // Script Extender detours the routine entry for its melee-damage event and context-hooks
    // 0x199444 for its melee-kill event. Reproduce both on a private copy with SE 2.14.1's backend
    // options (BulkUnitDetours.cs:42-44, :86-96; DetourManager.cs:35): kill attribution must stay
    // valid, while a change to anything it reads must still fail.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long MeleeDamage(IntPtr manager, int attacker, int victim, int damage);
    [TestMethod]
    public void ScriptExtenderMeleeHooksKeepKillAttributionButPinnedChangesFail()
    {
        game = APISharedTests.InstalledTestEnvironment.GameDirectory();
        byte[] file = File.ReadAllBytes(new InstalledAudioFiles(game).Native);
        const int start = NativeContract.MeleeDamageRva, length = 0x199B43 - NativeContract.MeleeDamageRva;
        byte[] Mapped(byte[] code) { var mapped = new byte[start + length + 0x100]; Array.Copy(code, 0, mapped, start, length); return mapped; }
        byte[] pristine = new byte[length]; Array.Copy(file, Offset(file, start), pristine, 0, length);
        SoundRuntime.ValidateKillSites(Mapped(pristine));

        IntPtr page = VirtualAlloc(IntPtr.Zero, new UIntPtr(0x2000), 0x3000, 0x40); roots.Add(page);
        Marshal.Copy(pristine, 0, page, length);
        var backend = new NativeDetourBackend(new NativeDetourOptions { SuspendThreadsDuringPatch = false });
        var transaction = new RedBird.X64.Hooks.Transaction.HookTransaction(RedBird.Core.Memory.ScanRegion.FromPointer(page, length, "melee copy"),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            new RedBird.X64.Hooks.Transaction.HookTransactionOptions { FailureMode = RedBird.Abstractions.Hooks.Transaction.TransactionFailureMode.ContinueOnError, Backend = backend });
        var detour = new RedBird.Abstractions.Hooks.Transaction.DetourHandle<MeleeDamage>();
        MeleeDamage impl = (manager, attacker, victim, damage) => detour.Original(manager, attacker, victim, damage);
        transaction.AddDetour(detour, "48 89 5C 24 18 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 30 49", impl);
        // SE's context hook is an X64InlineHook of ContextHookOptions.HookSize (14). The desktop CLR cannot
        // marshal its NativePointer<T> callback, so an inline hook of the same size and site stands in.
        var context = new RedBird.Abstractions.Hooks.Transaction.HookHandle<RedBird.X64.Hooks.X64InlineHook>();
        RedBird.X64.Hooks.InlineHookGenerator stub = (asm, displaced, address) => asm.nop();
        transaction.AddInline(context, "66 41 89 84 34 ?? ?? ?? ?? 66 45 89 B4 34", stub, new RedBird.X64.Hooks.Context.ContextHookOptions().HookSize);
        var result = transaction.Commit();
        roots.Add(transaction); roots.Add(detour); roots.Add(impl); roots.Add(context); roots.Add(stub); roots.Add(backend); roots.Add(result);
        Check(result.IsCompleteSuccess && result.Installed.Count == 2, "SE-equivalent entry detour and context-size hook installed on the copy");

        byte[] patchedCode = new byte[length]; Marshal.Copy(page, patchedCode, 0, length);
        var changed = Enumerable.Range(0, length).Where(i => patchedCode[i] != pristine[i]).Select(i => start + i).ToArray();
        Check(changed.Length > 0 && changed.All(rva => rva >= 0x199110 && rva < 0x199116 || rva >= 0x199444 && rva < 0x199456),
            "SE patches only the entry (0x199110..0x199115) and the context site (0x199444..0x199455): " + string.Join(",", changed.Select(rva => rva.ToString("X"))));
        Check(changed.Any(rva => rva >= 0x199444), "the context hook lies inside the body that the old full-body hash covered");
        var entry = (NativeDetour<MeleeDamage>)result.Installed.First(hook => hook is NativeDetour<MeleeDamage>);
        var trampoline = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(Read(entry.TrampolineAddress, 32)));
        trampoline.IP = unchecked((ulong)entry.TrampolineAddress.ToInt64());
        var first = trampoline.Decode(); var second = trampoline.Decode(); var back = trampoline.Decode();
        Check(first.Code == Iced.Intel.Code.Mov_rm64_r64 && first.MemoryBase == Iced.Intel.Register.RSP && first.MemoryDisplacement64 == 0x18 && first.Op1Register == Iced.Intel.Register.RBX &&
            second.Code == Iced.Intel.Code.Push_r64 && second.Op0Register == Iced.Intel.Register.RBP && back.Mnemonic == Iced.Intel.Mnemonic.Jmp &&
            back.NearBranchTarget == unchecked((ulong)page.ToInt64()) + (ulong)entry.DisplacedByteCount,
            "SE's trampoline replays the displaced prologue verbatim and resumes in the body, so the frame layout is unchanged");

        byte[] patched = Mapped(patchedCode);
        SoundRuntime.ValidateKillSites(patched);
        foreach (int rva in new[] { 0x199126, 0x199133, 0x19913E, 0x1991D1, 0x1991D9, 0x199953, 0x199315, 0x19981F, 0x199833, 0x199124 + 0x50 })
        {
            var mutated = (byte[])patched.Clone(); mutated[rva] ^= 0x08;
            bool rejected = false;
            try { SoundRuntime.ValidateKillSites(mutated); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "pinned frame setup or kill CALL change rejected at 0x" + rva.ToString("X"));
        }
        var longEntry = (byte[])patched.Clone(); for (int rva = 0x199110; rva < 0x199127; rva++) longEntry[rva] = 0x90;
        bool entryRejected = false;
        try { SoundRuntime.ValidateKillSites(longEntry); } catch (InvalidOperationException) { entryRejected = true; }
        Check(entryRejected, "an entry patch longer than the 20-byte prologue reaches the pinned frame setup and fails closed");
    }

    private static byte[] Read(IntPtr pointer, int length) { var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return bytes; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint allocationType,uint protection);
}
