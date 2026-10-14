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
        // frame: the wrapper's entry RSP. [frame] is the original return address; the bridge
        // spilled the caller's RBP at [frame-0x10] (SoundReturnAddressBridge.CallerRbpBelowFrame).
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void OriginWrapper(IntPtr unused, int x, int y, int sound, ulong frame);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Emit(IntPtr manager, short sound, short x, short y, short flag);
        [ThreadStatic] private static SoundSource scope;
        [ThreadStatic] private static int scopeSound, scopeX, scopeY;
        [ThreadStatic] private static bool scopeConsumed;
        private readonly ManualLogSource log;
        private NativeDetour<Wrapper> wrapperHook, flagWrapperHook;
        private SoundReturnAddressBridge wrapperBridge, flagWrapperBridge;
        private SoundEmitterBridge emitBridge;
        private ulong moduleBase;
        private NativeDetour<Emit> emitHook;
        
        private Wrapper wrapperFallback, flagWrapperFallback;
        private OriginWrapper wrapperCallback, flagWrapperCallback;
        private Emit emitCallback;
        private bool publicationStarted;
        private int* currentUnit;
        private int confirmed, killConfirmed, error, rejectionLogged, killRejectionLogged;
        private bool killReady;
        private long rejections;
        internal static long ContextRejections => processContext == null ? 0 : Interlocked.Read(ref processContext.rejections);
        // DLL_RunTick receives short[14,700,000]. The pending native buffer is
        // even larger; this conservative capacity covers every normal tick.
        internal const int MaxRecords = 14700000 / 21;

        private static SoundRuntime processContext;
        private static ILHook playbackHook;
        private static ManualLogSource processLog;
        private static int managedAttempted, nativeAttempted, playbackConfirmed;
        private static string playbackStatus = "Pending", contextStatus = "Pending", killStatus = "Pending";
        private static System.Reflection.FieldInfo effectList;
        private readonly NativeOwnershipRegistry ownership;
        private static void Info(ManualLogSource logger, string message)
        { try { NativeApiLog.Info(logger, message); } catch { } }
        private static void Error(ManualLogSource logger, string message)
        { try { NativeApiLog.Error(logger, message); } catch { } }
        internal static string PlaybackStatus => Volatile.Read(ref playbackStatus);
        internal static string ContextStatus => Volatile.Read(ref contextStatus);
        internal static string KillStatus => Volatile.Read(ref killStatus);

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
            if (Volatile.Read(ref playbackConfirmed) == 0 && Interlocked.Exchange(ref playbackConfirmed, 1) == 0)
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
            foreach (var spec in new[] { Tuple.Create("r_UnitChimp", 0x8A, "eChimps"), Tuple.Create("r_AIState", 0x2BC, "UInt16"),
                Tuple.Create("r_CurrentTilePositionX", 0xC0, "UInt16"), Tuple.Create("r_CurrentTilePositionY", 0xC2, "UInt16"),
                Tuple.Create("r_GlobalId", 0x94, "UInt32"), Tuple.Create("r_CurrentHealth", 0x3C4, "UInt32") })
            {
                var field = typeof(GameUnit).GetField(spec.Item1);
                if (field == null || !field.IsPublic || field.FieldType.Name != spec.Item3 || Marshal.OffsetOf(typeof(GameUnit), spec.Item1).ToInt32() != spec.Item2)
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
            // Melee kill cues are an independent optional part: a mismatch leaves the soldier catalog usable.
            try { ValidateKillSites(live); killReady = true; }
            catch (Exception ex) { Volatile.Write(ref killStatus, "Unavailable: " + ex.Message); Error(log, "AUDIO_KILL_CONTEXT_UNAVAILABLE: soldier sources remain available: " + ex.Message); }
            if (NativeContract.CurrentUnitRva > live.Length - sizeof(int)) throw new InvalidOperationException("Current-unit global is outside the loaded module.");
            currentUnit = (int*)((byte*)context.ModuleHandle.ToPointer() + NativeContract.CurrentUnitRva);
            if (!ownership.TryReserve(APISharedPlugin.PluginGuid, "Audio.UnitContext", NativeReservationMode.Exclusive,
                new[] { new NativeInterval((long)Address(context, NativeContract.WrapperRva), (long)Address(context, NativeContract.WrapperRva) + 15),
                        new NativeInterval((long)Address(context, NativeContract.FlagWrapperRva), (long)Address(context, NativeContract.FlagWrapperRva) + 15),
                        new NativeInterval((long)Address(context, NativeContract.EmitRva), (long)Address(context, NativeContract.EmitRva) + 14) }, out var conflict))
                throw new InvalidOperationException("Audio hook conflict: " + conflict);
            try
            {
                CreateHooks(Address(context, NativeContract.WrapperRva), Address(context, NativeContract.FlagWrapperRva), Address(context, NativeContract.EmitRva));
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
            if (killReady) Volatile.Write(ref killStatus, "Ready");
            Info(log, "AUDIO_CONTEXT_READY: 91 soldier source entries; NativeX64/Absolute; wrappers=0x2960..0x296F,0x2990..0x299F (4+3+8),next=mov r9d,r8d; emitter=0x1A41E0..0x1A41EE (4+3+4+3),next=write x; gameId=1-based,stride=0x490,slot1=manager+0xAEC,chimp=+0x8A,state=+0x2BC,tiles=+0xC0/+0xC2; rooted return-address bridges behind native demand guards (wrapper=" + SoundNativeDemand.WrapperValue + ",emitter=" + SoundNativeDemand.EmitterValue + "); melee kill sources=" + (killReady ? SoundSources.Kills.Length.ToString() : "unavailable") + "; inferred situations are not in-game confirmations.");
        }
        // Unpublished hook construction for the three targets. Install passes the module addresses;
        // copied-code tests pass private copies, so they execute the production bridges and callbacks.
        internal void CreateHooks(ulong wrapper, ulong flagWrapper, ulong emit, bool suspendThreads = true)
        {
            wrapperCallback = (unused, x, y, sound, frame) => OnWrapper(false, unused, x, y, sound, frame);
            flagWrapperCallback = (unused, x, y, sound, frame) => OnWrapper(true, unused, x, y, sound, frame);
            wrapperFallback = (unused, x, y, sound) => OnWrapper(false, unused, x, y, sound, 0);
            flagWrapperFallback = (unused, x, y, sound) => OnWrapper(true, unused, x, y, sound, 0);
            // The bridges test native demand bytes first; zero demand never enters managed code.
            wrapperBridge = new SoundReturnAddressBridge(wrapperCallback, SoundNativeDemand.Wrapper);
            flagWrapperBridge = new SoundReturnAddressBridge(flagWrapperCallback, SoundNativeDemand.Wrapper);
            emitCallback = OnEmit;
            emitBridge = new SoundEmitterBridge(emitCallback, SoundNativeDemand.Emitter);
            var first = new DetourRequest<Wrapper> { Name = "APIShared.Audio.Source", TargetAddress = wrapper, Callback = wrapperFallback, IntermediaryFactory = wrapperBridge };
            var second = new DetourRequest<Wrapper> { Name = "APIShared.Audio.FlagSource", TargetAddress = flagWrapper, Callback = flagWrapperFallback, IntermediaryFactory = flagWrapperBridge };
            var emitRequest = new DetourRequest<Emit> { Name = "APIShared.Audio.Emit", TargetAddress = emit, Callback = emitCallback, IntermediaryFactory = emitBridge };
            wrapperHook = (NativeDetour<Wrapper>)NativeContract.Backend(40, suspendThreads).CreateDetour(in first);
            flagWrapperHook = (NativeDetour<Wrapper>)NativeContract.Backend(40, suspendThreads).CreateDetour(in second);
            emitHook = (NativeDetour<Emit>)NativeContract.Backend(137, suspendThreads).CreateDetour(in emitRequest);
        }
        private static void ValidatePrologue(ReadOnlySpan<byte> image, int rva, byte[] expected)
        { if (rva < 0 || rva > image.Length - expected.Length || !image.Slice(rva, expected.Length).SequenceEqual(expected)) throw new InvalidOperationException("Audio prologue changed: 0x" + rva.ToString("X")); }
        // Live pins for kill attribution only: the frame setup that leaves the victim in RBP and the
        // attacker offset in [rsp+0x70], and the four kill CALLs. Entry detours and frame-preserving
        // hooks elsewhere in the body are tolerated (see NativeContract); the file hash pins the rest.
        internal static void ValidateKillSites(ReadOnlySpan<byte> image)
        {
            if (NativeContract.MeleeFrameRva < 0 || NativeContract.MeleeDamageEndRva > image.Length)
                throw new InvalidOperationException("Melee damage routine is outside the loaded module.");
            using (var sha = System.Security.Cryptography.SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(image.Slice(NativeContract.MeleeFrameRva, NativeContract.MeleeFrameLength).ToArray())).Replace("-", "") != NativeContract.MeleeFrameHash)
                    throw new InvalidOperationException("Melee damage frame setup changed or is patched (0x199124..0x1991D9).");
            foreach (var kill in SoundSources.Kills)
            {
                int call = kill.ReturnRva - 5;
                if (call < NativeContract.MeleeFrameRva + NativeContract.MeleeFrameLength || kill.ReturnRva > NativeContract.MeleeDamageEndRva || image[call] != 0xE8 ||
                    APIShared.Internal.NativePatternResolver.ResolveRelativeTarget(image, call + 1, kill.ReturnRva) != NativeContract.WrapperRva)
                    throw new InvalidOperationException("Melee kill callsite changed or is patched: " + kill.Key);
            }
        }
        private void ValidateHooks(CrusaderLibraryLoadContext context) =>
            ValidateHooks(Address(context, NativeContract.WrapperRva), Address(context, NativeContract.FlagWrapperRva), Address(context, NativeContract.EmitRva));
        internal void ValidateHooks(ulong wrapper, ulong flagWrapper, ulong emit)
        {
            NativeContract.Validate(wrapperHook, 15, wrapper, NativeContract.WrapperBytes);
            NativeContract.Validate(flagWrapperHook, 15, flagWrapper, NativeContract.FlagWrapperBytes);
            NativeContract.Validate(emitHook, 14, emit);
            wrapperBridge.Validate(wrapperHook); flagWrapperBridge.Validate(flagWrapperHook); emitBridge.Validate(emitHook);
        }
        internal NativeDetour<Wrapper> WrapperHook => wrapperHook;
        internal NativeDetour<Wrapper> FlagWrapperHook => flagWrapperHook;
        internal NativeDetour<Emit> EmitHook => emitHook;
        private static ulong Address(CrusaderLibraryLoadContext context, int rva) => unchecked((ulong)context.ModuleHandle.ToInt64() + (uint)rva);
        private void RollbackUnpublished()
        {
            if (publicationStarted) throw new InvalidOperationException("Published hook rollback forbidden.");
            if ((wrapperHook != null && wrapperHook.IsInstalled) || (flagWrapperHook != null && flagWrapperHook.IsInstalled) || (emitHook != null && emitHook.IsInstalled))
                throw new InvalidOperationException("Installed hook rollback forbidden.");
            emitHook?.Dispose(); flagWrapperHook?.Dispose(); wrapperHook?.Dispose();
        }
        // Persistent path for every positional wrapper call with native demand: the bridge
        // (native byte nonzero) -> here -> attribution/source events -> trampoline -> emitter.
        // The ThreadStatic scope is touched only around a matched soldier source with playback
        // demand: it is null outside that window, because the emitter is a leaf of the wrapper.
        private void OnWrapper(bool flagged, IntPtr unused, int x, int y, int sound, ulong frame)
        {
            var hook = flagged ? flagWrapperHook : wrapperHook;
            bool playback = SoundEvents.Demand.HasObservers, sources = SoundSourceEvents.Demand.HasObservers;
            // frame is zero only for the fallback delegates, which the bridges never call.
            ulong origin = frame == 0 ? 0 : *(ulong*)frame;
            if (!(playback || sources) || !SoundContextPolicy.Active || origin < moduleBase || origin - moduleBase > int.MaxValue)
            { hook.Original(unused, x, y, sound); return; }
            int rva = (int)(origin - moduleBase);
            if (SoundSources.TryGet(rva, out var source))
            {
                if (source.Flag == (flagged ? 1 : 0) && source.Situation(sound) != SoundSituation.Unknown)
                    AttributeSoldier(hook, source, flagged, unused, x, y, sound, playback, sources);
                else hook.Original(unused, x, y, sound);
                return;
            }
            hook.Original(unused, x, y, sources && !flagged && killReady && SoundSources.TryGetKill(rva, out var kill) && kill.Accepts(sound)
                ? AttributeKill(kill, x, y, sound, frame) : sound);
        }
        // Current-unit global attribution, validated by type, coordinates and (for 0x1700D2) state.
        // A source-event replacement leaves the record unmarked: its effect is no longer the audited one.
        private void AttributeSoldier(NativeDetour<Wrapper> hook, SoundSource source, bool flagged, IntPtr unused, int x, int y, int sound, bool playback, bool sources)
        {
            int final = sound; bool mark = false;
            try
            {
                int id = *currentUnit;
                bool found = APIShared.UnitAccess.TryGetById(id, out GameUnit* unit, out var lookupFailure);
                if (found && SoundContextPolicy.MatchesSource(source, (int)unit->r_UnitChimp, unit->r_AIState,
                    (short)unit->r_CurrentTilePositionX, (short)unit->r_CurrentTilePositionY, (short)x, (short)y, sound, flagged ? 1 : 0))
                {
                    if (Volatile.Read(ref confirmed) == 0 && Interlocked.Exchange(ref confirmed, 1) == 0) Info(log, "AUDIO_SOURCE_CONFIRMED: native callback executed after startup cleanup; source=" + source.Key + ",unitId=" + id + ",unitType=" + source.UnitType + ",state=" + unit->r_AIState + ",tiles=" + x + "," + y + "; action evidence=" + source.Evidence(sound));
                    if (sources)
                    {
                        var args = new SoundSourceArgs(sound, x, y, flagged, source.Situation(sound), source.Evidence(sound), source.Key,
                            id, unit->r_GlobalId, source.UnitType, null, null);
                        SoundSourceEvents.Dispatch(ref args);
                        final = args.SoundId;
                    }
                    mark = playback && final == sound;
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
            if (!mark) { hook.Original(unused, x, y, final); return; }
            // The emitter consumes this scope inside the original; restore it even if the original throws.
            var saved = scope; int oldSound = scopeSound, oldX = scopeX, oldY = scopeY; bool oldConsumed = scopeConsumed;
            scope = source; scopeSound = sound; scopeX = (short)x; scopeY = (short)y; scopeConsumed = false;
            try { hook.Original(unused, x, y, sound); }
            finally { scope = saved; scopeSound = oldSound; scopeX = oldX; scopeY = oldY; scopeConsumed = oldConsumed; }
        }
        // The melee damage routine's own arguments: victim = RBP (movsxd rbp,r8d at 0x199124),
        // attacker = [frame+0x78]/0x490 (spilled at 0x1991D5). Not the current-unit global.
        private int AttributeKill(SoundKillSource kill, int x, int y, int sound, ulong frame)
        {
            int final = sound;
            try
            {
                long offset = *(long*)(frame + NativeContract.MeleeAttackerFrameOffset);
                long victimValue = *(long*)(frame - SoundReturnAddressBridge.CallerRbpBelowFrame);
                int attackerId = offset > 0 && offset % 0x490 == 0 && offset / 0x490 <= int.MaxValue ? (int)(offset / 0x490) : 0;
                int victimId = victimValue > 0 && victimValue <= int.MaxValue ? (int)victimValue : 0;
                bool attackerFound = APIShared.UnitAccess.TryGetById(attackerId, out GameUnit* attacker, out var attackerFailure);
                bool victimFound = APIShared.UnitAccess.TryGetById(victimId, out GameUnit* victim, out var victimFailure);
                if (attackerFound && victimFound && kill.Matches(sound, (int)attacker->r_UnitChimp,
                    (short)attacker->r_CurrentTilePositionX, (short)attacker->r_CurrentTilePositionY,
                    (short)victim->r_CurrentTilePositionX, (short)victim->r_CurrentTilePositionY, victim->r_CurrentHealth, (short)x, (short)y))
                {
                    int type = (int)attacker->r_UnitChimp;
                    if (Volatile.Read(ref killConfirmed) == 0 && Interlocked.Exchange(ref killConfirmed, 1) == 0)
                        Info(log, "AUDIO_KILL_SOURCE_CONFIRMED: source=" + kill.Key + ",sound=" + sound + ",attackerId=" + attackerId + ",attackerType=" + type +
                            ",attackerGlobal=" + attacker->r_GlobalId + ",victimId=" + victimId + ",victimGlobal=" + victim->r_GlobalId + ",tiles=" + x + "," + y + "; evidence=NativeAudited");
                    var args = new SoundSourceArgs(sound, x, y, false, SoundSituation.MeleeKill, SituationEvidence.NativeAudited, kill.Key,
                        attackerId, attacker->r_GlobalId, type, victimId, victim->r_GlobalId);
                    SoundSourceEvents.Dispatch(ref args);
                    final = args.SoundId;
                }
                else {
                    Interlocked.Increment(ref rejections);
                    if (Interlocked.Exchange(ref killRejectionLogged, 1) == 0)
                        Error(log, "AUDIO_KILL_SOURCE_REJECTED: source=" + kill.Key + ",sound=" + sound + ",attackerId=" + attackerId + ",lookup=" + attackerFailure +
                            ",attackerType=" + (attackerFound ? (int)attacker->r_UnitChimp : -1) + ",victimId=" + victimId + ",lookup=" + victimFailure +
                            ",victimHealth=" + (victimFound ? (long)victim->r_CurrentHealth : -1) + ",soundTiles=" + x + "," + y + "; call forwarded unchanged.");
                }
            }
            catch (Exception ex) { Fail(ex); }
            return final;
        }
        private void OnEmit(IntPtr manager, short sound, short x, short y, short flag)
        {
            // Managed minimum behind the native guard: marking needs context and playback demand.
            if (!(SoundContextPolicy.Active && SoundEvents.Demand.HasObservers)) { emitHook.Original(manager, sound, x, y, flag); return; }
            var source = scope; bool eligible = false; short* buffer = null; int before = -1;
            try
            {
                eligible = source != null && !scopeConsumed && scopeSound == sound && scopeX == x && scopeY == y && source.Flag == flag;
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
            if (!eligible || !(SoundContextPolicy.Active && SoundEvents.Demand.HasObservers)) return;
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
