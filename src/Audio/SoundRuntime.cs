using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared.Audio
{
    internal sealed unsafe class SoundRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Wrapper(IntPtr unused, int x, int y, int sound);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void OriginWrapper(IntPtr unused, int x, int y, int sound, ulong origin);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Emit(IntPtr manager, short sound, short x, short y, short flag);
        [ThreadStatic] private static SoundSource scope;
        [ThreadStatic] private static int scopeSound, scopeX, scopeY;
        [ThreadStatic] private static bool scopeConsumed;
        private readonly ManualLogSource log;
        private NativeDetour<Wrapper> wrapperHook, flagWrapperHook;
        private SoundReturnAddressBridge wrapperBridge, flagWrapperBridge;
        private ulong moduleBase;
        private NativeDetour<Emit> emitHook;
        
        private Wrapper wrapperFallback, flagWrapperFallback;
        private OriginWrapper wrapperCallback, flagWrapperCallback;
        private Emit emitCallback;
        private bool publicationStarted;
        private int* currentUnit;
        private int confirmed, error, rejectionLogged;
        private long rejections;
        internal static long ContextRejections => processContext == null ? 0 : Interlocked.Read(ref processContext.rejections);
        // DLL_RunTick receives short[14,700,000]. The pending native buffer is
        // even larger; this conservative capacity covers every normal tick.
        internal const int MaxRecords = 14700000 / 21;

        private static SoundRuntime processContext;
        private static ILHook playbackHook;
        private static ManualLogSource processLog;
        private static int managedAttempted, nativeAttempted, playbackConfirmed;
        private static string playbackStatus = "Pending", contextStatus = "Pending";
        private static System.Reflection.FieldInfo effectList;
        private readonly NativeOwnershipRegistry ownership;
        private static void Info(ManualLogSource logger, string message)
        { try { NativeApiLog.Info(logger, message); } catch { } }
        private static void Error(ManualLogSource logger, string message)
        { try { NativeApiLog.Error(logger, message); } catch { } }
        internal static string PlaybackStatus => Volatile.Read(ref playbackStatus);
        internal static string ContextStatus => Volatile.Read(ref contextStatus);

        // Managed and native availability are independent. Roots precede publication; no retry
        // can repatch a partially installed chain. Native failure leaves generic events usable.
        internal static void InitializeManaged(ManualLogSource logger)
        {
            if (Interlocked.Exchange(ref managedAttempted, 1) != 0) return;
            processLog = logger;
            try
            {
                var files = new InstalledAudioFiles(BepInEx.Paths.GameRootPath);
                InstalledAudioFiles.ValidateFile(files.Managed, NativeContract.ManagedHash);
                ValidateMembers();
                SoundClips.Initialize();
                var method = typeof(GameMap).GetMethod("processTestMap", new[] { typeof(short[]), typeof(int), typeof(EngineInterface.PlayState), typeof(byte[]) });
                if (method == null || !method.IsPublic) throw new InvalidOperationException("Public processTestMap signature changed.");
                SoundIlContract.Validate(method);
                playbackHook = new ILHook(method, SoundIlContract.Apply, new ILHookConfig { ManualApply = true, ID = "APIShared.Audio" });
                playbackHook.Apply();
                if (!playbackHook.IsApplied) throw new InvalidOperationException("Audio IL publication incomplete.");
                Volatile.Write(ref playbackStatus, "Ready");
                Info(logger, "AUDIO_PLAYBACK_READY: permanent GameMap sound events installed.");
            }
            catch (Exception ex) { Volatile.Write(ref playbackStatus, "Unavailable: " + ex.Message); Error(logger, "Audio playback interception unavailable: " + ex); }
        }
        internal static void InitializeNative(CrusaderLibraryLoadContext context, ManualLogSource logger, NativeOwnershipRegistry ownership)
        {
            if (Interlocked.Exchange(ref nativeAttempted, 1) != 0) return;
            processContext = new SoundRuntime(logger, ownership);
            try { processContext.Install(context); Volatile.Write(ref contextStatus, "Ready"); }
            catch (Exception ex) { SoundContextPolicy.SetReady(false); Volatile.Write(ref contextStatus, "Unavailable: " + ex.Message); Error(logger, "Audio unit context unavailable; generic playback remains independent: " + ex); }
        }
        internal static void ReportClipBoundaryFailure(Exception error)
        { Error(processLog, "AUDIO_MAP_CLIPS_DISABLED: lifetime publisher failed; process clips and generic sound events remain independent: " + error); }
        internal static void ConfirmPlayback()
        {
            if (Interlocked.Exchange(ref playbackConfirmed, 1) == 0)
                try { Info(processLog, "AUDIO_PLAYBACK_CONFIRMED: shared sound dispatch executed after startup cleanup."); } catch { }
        }
        // fx_list indexes are effect IDs; play_list indexes (including Extender custom clips)
        // are a different namespace. Reflection is scoped, exact-type validated, Unity-thread only.
        internal static bool IsVanillaEffect(int id)
        {
            if (id < 0 || id > 10000 || effectList == null || SFXManager.instance == null) return false;
            var list = effectList.GetValue(SFXManager.instance) as System.Collections.IList;
            return list != null && id < list.Count;
        }
        internal static void ValidateMembers()
        {
            effectList = typeof(SFXManager).GetField("fx_list", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (effectList == null || !effectList.FieldType.IsGenericType || effectList.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>) ||
                effectList.FieldType.GetGenericArguments()[0].FullName != "SFXManager+sh1_sound_effect")
                throw new InvalidOperationException("Private Vanilla effect table contract changed.");
            var instance = typeof(SFXManager).GetField("instance");
            if (instance == null || !instance.IsPublic || !instance.IsStatic || instance.FieldType != typeof(SFXManager)) throw new InvalidOperationException("Public SFXManager singleton changed.");
            var play = typeof(SFXManager).GetMethod("playSound", new[] { typeof(int), typeof(float), typeof(float), typeof(bool) });
            if (play == null || !play.IsPublic || play.ReturnType != typeof(void)) throw new InvalidOperationException("Public playback contract changed.");
        }
        // Native unit layout validation is independent of managed playback availability.
        internal static void ValidateUnitMembers()
        {
            foreach (var spec in new[] { Tuple.Create("r_UnitChimp", 0x8A), Tuple.Create("r_AIState", 0x2BC), Tuple.Create("r_CurrentTilePositionX", 0xC0), Tuple.Create("r_CurrentTilePositionY", 0xC2) })
            {
                var field = typeof(GameUnit).GetField(spec.Item1);
                if (field == null || !field.IsPublic || (spec.Item1 == "r_UnitChimp" ? field.FieldType.Name != "eChimps" : field.FieldType != typeof(ushort)) || Marshal.OffsetOf(typeof(GameUnit), spec.Item1).ToInt32() != spec.Item2)
                    throw new InvalidOperationException("Public unit audio field changed: " + spec.Item1);
            }
            if (typeof(GameUnit).StructLayoutAttribute.Size != 0x490) throw new InvalidOperationException("Unit stride changed.");
        }
        internal SoundRuntime(ManualLogSource log, NativeOwnershipRegistry ownership)
        { this.log = log; this.ownership = ownership ?? throw new ArgumentNullException(nameof(ownership)); }
        internal void Install(CrusaderLibraryLoadContext context)
        {
            var files = new InstalledAudioFiles(BepInEx.Paths.GameRootPath);
            files.Validate();
            ValidateUnitMembers();
            Info(log, "Installed audio binary identities verified from BepInEx game root: " + BepInEx.Paths.GameRootPath);
            var live = new ReadOnlySpan<byte>(context.ModuleHandle.ToPointer(), context.Memory.Length);
            // All three targets and source sites are bound to the installed native hash.
            // Exact wrapper bytes replace the old per-unit handler detour.
            moduleBase = unchecked((ulong)context.ModuleHandle.ToInt64());
            foreach (var spec in new[] {
                Tuple.Create(NativeContract.WrapperRva, NativeContract.WrapperPattern, "Soldier spatial wrapper"),
                Tuple.Create(NativeContract.FlagWrapperRva, NativeContract.FlagWrapperPattern, "Soldier flagged wrapper"),
                Tuple.Create(NativeContract.EmitRva, NativeContract.EmitPattern, "Spatial sound emitter") }) {
                var resolved = APIShared.Internal.NativePatternResolver.ResolveUnique(live, spec.Item2, spec.Item1, true, spec.Item3, log);
                Info(log, "Audio native resolution: target=" + spec.Item3 + ",method=" + resolved.Method + ",rva=0x" + resolved.Rva.ToString("X"));
                if (resolved.Rva != spec.Item1) throw new InvalidOperationException("Audio target moved outside hash-bound provenance: " + spec.Item3);
            }
            ValidatePrologue(live, NativeContract.WrapperRva, NativeContract.WrapperBytes);
            ValidatePrologue(live, NativeContract.FlagWrapperRva, NativeContract.FlagWrapperBytes);
            ValidatePrologue(live, NativeContract.EmitRva, NativeContract.EmitBytes);
            foreach (var source in SoundSources.All)
            {
                int call = source.ReturnRva - 5;
                if (call < 0 || source.ReturnRva > live.Length || live[call] != 0xE8 ||
                    APIShared.Internal.NativePatternResolver.ResolveRelativeTarget(live, call + 1, source.ReturnRva) !=
                    (source.Flag == 0 ? NativeContract.WrapperRva : NativeContract.FlagWrapperRva))
                    throw new InvalidOperationException("Catalog callsite changed: " + source.Key);
            }
            if (NativeContract.CurrentUnitRva > live.Length - sizeof(int)) throw new InvalidOperationException("Current-unit global is outside the loaded module.");
            currentUnit = (int*)((byte*)context.ModuleHandle.ToPointer() + NativeContract.CurrentUnitRva);
            if (!ownership.TryReserve(APISharedPlugin.PluginGuid, "Audio.UnitContext", NativeReservationMode.Exclusive,
                new[] { new NativeInterval((long)Address(context, NativeContract.WrapperRva), (long)Address(context, NativeContract.WrapperRva) + 15),
                        new NativeInterval((long)Address(context, NativeContract.FlagWrapperRva), (long)Address(context, NativeContract.FlagWrapperRva) + 15),
                        new NativeInterval((long)Address(context, NativeContract.EmitRva), (long)Address(context, NativeContract.EmitRva) + 14) }, out var conflict))
                throw new InvalidOperationException("Audio hook conflict: " + conflict);
            try
            {
                wrapperCallback = (unused, x, y, sound, origin) => OnWrapper(false, unused, x, y, sound, origin);
                flagWrapperCallback = (unused, x, y, sound, origin) => OnWrapper(true, unused, x, y, sound, origin);
                wrapperFallback = (unused, x, y, sound) => OnWrapper(false, unused, x, y, sound, 0);
                flagWrapperFallback = (unused, x, y, sound) => OnWrapper(true, unused, x, y, sound, 0);
                wrapperBridge = new SoundReturnAddressBridge(wrapperCallback);
                flagWrapperBridge = new SoundReturnAddressBridge(flagWrapperCallback);
                emitCallback = OnEmit;
                var first = new DetourRequest<Wrapper> { Name = "APIShared.Audio.Source", TargetAddress = Address(context, NativeContract.WrapperRva), Callback = wrapperFallback, IntermediaryFactory = wrapperBridge };
                var second = new DetourRequest<Wrapper> { Name = "APIShared.Audio.FlagSource", TargetAddress = Address(context, NativeContract.FlagWrapperRva), Callback = flagWrapperFallback, IntermediaryFactory = flagWrapperBridge };
                var emitRequest = new DetourRequest<Emit> { Name = "APIShared.Audio.Emit", TargetAddress = Address(context, NativeContract.EmitRva), Callback = emitCallback };
                wrapperHook = (NativeDetour<Wrapper>)NativeContract.Backend(40).CreateDetour(in first);
                flagWrapperHook = (NativeDetour<Wrapper>)NativeContract.Backend(40).CreateDetour(in second);
                emitHook = (NativeDetour<Emit>)NativeContract.Backend(137).CreateDetour(in emitRequest);
                ValidateHooks(context);
            }
            catch { RollbackUnpublished(); throw; }
            publicationStarted = true;
            wrapperHook.Enable();
            flagWrapperHook.Enable();
            emitHook.Enable();
            if (!wrapperHook.IsInstalled || !flagWrapperHook.IsInstalled || !emitHook.IsInstalled)
                throw new InvalidOperationException("Hook publication incomplete.");
            ValidateHooks(context);
            SoundContextPolicy.SetReady(true);
            Info(log, "AUDIO_CONTEXT_READY: 91 soldier source entries; NativeX64/Absolute; wrappers=0x2960..0x296F,0x2990..0x299F (4+3+8),next=mov r9d,r8d; emitter=0x1A41E0..0x1A41EE (4+3+4+3),next=write x; gameId=1-based,stride=0x490,slot1=manager+0xAEC,chimp=+0x8A,state=+0x2BC,tiles=+0xC0/+0xC2; rooted return-address bridges; inferred situations are not in-game confirmations.");
        }
        private static void ValidatePrologue(ReadOnlySpan<byte> image, int rva, byte[] expected)
        { if (rva < 0 || rva > image.Length - expected.Length || !image.Slice(rva, expected.Length).SequenceEqual(expected)) throw new InvalidOperationException("Audio prologue changed: 0x" + rva.ToString("X")); }
        private void ValidateHooks(CrusaderLibraryLoadContext context)
        {
            NativeContract.Validate(wrapperHook, 15, Address(context, NativeContract.WrapperRva), NativeContract.WrapperBytes);
            NativeContract.Validate(flagWrapperHook, 15, Address(context, NativeContract.FlagWrapperRva), NativeContract.FlagWrapperBytes);
            NativeContract.Validate(emitHook, 14, Address(context, NativeContract.EmitRva));
            wrapperBridge.Validate(wrapperHook); flagWrapperBridge.Validate(flagWrapperHook);
        }
        private static ulong Address(CrusaderLibraryLoadContext context, int rva) => unchecked((ulong)context.ModuleHandle.ToInt64() + (uint)rva);
        private void RollbackUnpublished()
        {
            if (publicationStarted) throw new InvalidOperationException("Published hook rollback forbidden.");
            if ((wrapperHook != null && wrapperHook.IsInstalled) || (flagWrapperHook != null && flagWrapperHook.IsInstalled) || (emitHook != null && emitHook.IsInstalled))
                throw new InvalidOperationException("Installed hook rollback forbidden.");
            emitHook?.Dispose(); flagWrapperHook?.Dispose(); wrapperHook?.Dispose();
        }
        private void OnWrapper(bool flagged, IntPtr unused, int x, int y, int sound, ulong origin)
        {
            var saved = scope; int oldSound = scopeSound, oldX = scopeX, oldY = scopeY; bool oldConsumed = scopeConsumed;
            scope = null; scopeConsumed = false;
            try
            {
                if (SoundContextPolicy.Active && SoundEvents.Registry.HasObservers && origin >= moduleBase && origin - moduleBase <= int.MaxValue &&
                    SoundSources.TryGet((int)(origin - moduleBase), out var source) && source.Flag == (flagged ? 1 : 0) && source.Situation(sound) != SoundSituation.Unknown)
                {
                    try
                    {
                        int id = *currentUnit;
                        bool found = APIShared.UnitAccess.TryGetById(id, out GameUnit* unit, out var lookupFailure);
                        if (found && SoundContextPolicy.MatchesSource(source, (int)unit->r_UnitChimp, unit->r_AIState,
                            (short)unit->r_CurrentTilePositionX, (short)unit->r_CurrentTilePositionY, (short)x, (short)y, sound, flagged ? 1 : 0))
                        {
                            scope = source; scopeSound = sound; scopeX = (short)x; scopeY = (short)y;
                            if (Interlocked.Exchange(ref confirmed, 1) == 0) Info(log, "AUDIO_SOURCE_CONFIRMED: native callback executed after startup cleanup; source=" + source.Key + ",unitId=" + id + ",unitType=" + source.UnitType + ",state=" + unit->r_AIState + ",tiles=" + x + "," + y + "; action evidence=" + source.Evidence(sound));
                        }
                        else {
                            Interlocked.Increment(ref rejections);
                            if (Interlocked.Exchange(ref rejectionLogged, 1) == 0)
                                Error(log, "AUDIO_SOURCE_REJECTED: source=" + source.Key + ",unitId=" + id + ",lookup=" + lookupFailure +
                                    ",actualType=" + (found ? (int)unit->r_UnitChimp : -1) + ",state=" + (found ? unit->r_AIState : -1) +
                                    ",soundTiles=" + x + "," + y + "; mismatched source remains Unknown; other catalog entries stay available.");
                        }
                    }
                    catch (Exception ex) { Fail(ex); }
                }
                (flagged ? flagWrapperHook : wrapperHook).Original(unused, x, y, sound);
            }
            finally { scope = saved; scopeSound = oldSound; scopeX = oldX; scopeY = oldY; scopeConsumed = oldConsumed; }
        }
        private void OnEmit(IntPtr manager, short sound, short x, short y, short flag)
        {
            var source = scope; bool eligible = false; short* buffer = null; int before = -1;
            try
            {
                eligible = SoundContextPolicy.Active && SoundEvents.Registry.HasObservers && source != null && !scopeConsumed &&
                    scopeSound == sound && scopeX == x && scopeY == y && source.Flag == flag;
                if (eligible)
                {
                    scopeConsumed = true;
                    if (manager == IntPtr.Zero) throw new InvalidOperationException("Null sound manager.");
                    buffer = *(short**)manager.ToPointer(); before = *(int*)((byte*)manager.ToPointer() + 8);
                    if (buffer == null || before < 0 || before >= MaxRecords) throw new InvalidOperationException("Sound buffer/count outside audited capacity.");
                }
            }
            catch (Exception ex) { eligible = false; Fail(ex); }
            emitHook.Original(manager, sound, x, y, flag);
            if (!eligible || !(SoundContextPolicy.Active && SoundEvents.Registry.HasObservers)) return;
            try
            {
                if (*(short**)manager.ToPointer() != buffer || *(int*)((byte*)manager.ToPointer() + 8) != before + 1)
                    throw new InvalidOperationException("Spatial emitter did not append exactly one record.");
                var record = new Span<short>(buffer + before * SoundContextPolicy.RecordWords, SoundContextPolicy.RecordWords);
                if (!SoundContextPolicy.TryMark(record, source)) throw new InvalidOperationException("Appended spatial record changed.");
            }
            catch (Exception ex) { Fail(ex); }
        }

        private void Fail(Exception ex)
        {
            SoundContextPolicy.SetReady(false);
            Volatile.Write(ref contextStatus, "Faulted: " + ex.Message);
            if (Interlocked.Exchange(ref error, 1) == 0)
                try { Error(log, "Soldier sound context failed; callbacks remain installed but inactive: " + ex); } catch { }
        }
    }
}
