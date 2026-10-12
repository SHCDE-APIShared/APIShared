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
        game = Environment.GetEnvironmentVariable("SHCDE_GAME_DIR") ?? @"E:\ProgrammeE\Steam\steamapps\common\Stronghold Crusader Definitive Edition";
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
            Tuple.Create("r_CurrentTilePositionX", 0xC0, "UInt16"), Tuple.Create("r_CurrentTilePositionY", 0xC2, "UInt16") })
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
            ProbeHook(copy, size, entry.Length, (SoundRuntime.Emit)((manager, sound, x, y, flag) => {}));
            return;
        }
        ProbeBridge(copy, size, entry);
    }
    private static void ProbeHook<T>(IntPtr copy, int size, int length, T callback) where T : Delegate
    {
        var request=new DetourRequest<T>{Name="private idle-sound probe",TargetAddress=unchecked((ulong)copy.ToInt64()),Callback=callback};
        var hook=(NativeDetour<T>)NativeContract.Backend(size).CreateDetour(in request);
        NativeContract.Validate(hook,length,request.TargetAddress, length == 14 ? NativeContract.EmitBytes : NativeContract.WrapperBytes);checks++;
        roots.Add(callback);roots.Add(hook);roots.Add(copy);
        hook.Enable();NativeContract.Validate(hook,length,request.TargetAddress, length == 14 ? NativeContract.EmitBytes : NativeContract.WrapperBytes);checks++;
        Check(hook.IsInstalled,"copied hook installed");
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
        NativeDetour<SoundRuntime.Wrapper> hook = null;
        int callbacks = 0; ulong captured = 0;
        SoundRuntime.OriginWrapper callback = (unused,x,y,sound,origin) => {
            Check(unused == new IntPtr(123) && x == -17 && y == 300 && sound == 180, "bridge preserves four Win64 arguments");
            captured = origin; callbacks++; hook.Original(unused,x,y,sound);
        };
        SoundRuntime.Wrapper fallback = (unused,x,y,sound) => { throw new InvalidOperationException("bridge must capture origin"); };
        var bridge = new SoundReturnAddressBridge(callback);
        var request = new DetourRequest<SoundRuntime.Wrapper> { Name="private source bridge",TargetAddress=unchecked((ulong)copy.ToInt64()),Callback=fallback,IntermediaryFactory=bridge };
        hook = (NativeDetour<SoundRuntime.Wrapper>)NativeContract.Backend(size).CreateDetour(in request);
        NativeContract.Validate(hook,15,request.TargetAddress,expected); bridge.Validate(hook);
        roots.Add(copy);roots.Add(hook);roots.Add(callback);roots.Add(fallback);roots.Add(bridge);
        hook.Enable();NativeContract.Validate(hook,15,request.TargetAddress,expected);bridge.Validate(hook);
        // Native caller fixes the expected return PC, rather than depending on CLR stub details.
        var caller = RedBird.X64.Assembly.ExecutableFunction.Create(asm => {
            asm.sub(Iced.Intel.AssemblerRegisters.rsp, 0x28);
            asm.mov(Iced.Intel.AssemblerRegisters.rax, unchecked((ulong)copy.ToInt64()));
            asm.call(Iced.Intel.AssemblerRegisters.rax);
            asm.add(Iced.Intel.AssemblerRegisters.rsp, 0x28); asm.ret();
        });
        roots.Add(caller);
        var decoder = Iced.Intel.Decoder.Create(64,new Iced.Intel.ByteArrayCodeReader(Read(caller.EntryPoint,32)));
        decoder.IP = unchecked((ulong)caller.EntryPoint.ToInt64());
        decoder.Decode();decoder.Decode();var call = decoder.Decode();
        var invoke = Marshal.GetDelegateForFunctionPointer<SoundRuntime.Wrapper>(caller.EntryPoint);roots.Add(invoke);
        invoke(new IntPtr(123), -17, 300, 180);
        Check(callbacks == 1 && emissions == 1 && captured == call.NextIP, "executed intermediary captures original native return address and resumes displaced prologue");
    }
    private static byte[] Read(IntPtr pointer, int length) { var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return bytes; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint allocationType,uint protection);
}
