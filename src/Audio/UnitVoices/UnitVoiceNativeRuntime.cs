using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Audio
{
    // Internal capture only. Mod callbacks run later on Unity against the exact PlayState.
    // All native entry callbacks forward their original once, including capture failures.
    internal static unsafe class UnitVoiceNativeRuntime
    {
        internal const string SiteName = "Audio.UnitVoices";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void NativeCall(ulong a, ulong b, ulong c, ulong d);

        private static readonly IntPtr demand = AllocateDemand();
        private static readonly object demandGate = new object();
        private static readonly List<Entry> entries = new List<Entry>();
        private static IReadOnlyDictionary<string, string> sourceStatuses = InitialSourceStatuses();
        private static readonly IntPtr tokenBuffer = Marshal.AllocHGlobal(128);
        private static object engineGate;
        private static GameUnitManagerAPI units;
        private static ulong moduleBase;
        private static ManualLogSource log;
        private static string status = "Pending";
        private static int attempted, ready, confirmed;
        private static long failures;
        private static Session session = new Session(0, false);
        [ThreadStatic] private static CaptureFrame scope;
        [ThreadStatic] private static bool inOutput;

        internal static string Status => Volatile.Read(ref status);
        internal static bool Available => Volatile.Read(ref ready) != 0;
        internal static long CaptureFailures => Interlocked.Read(ref failures);
        internal static string GetSourceStatus(string key) => key != null && Volatile.Read(ref sourceStatuses).TryGetValue(key, out string value)
            ? value : "Unavailable: unknown unit voice source key.";
        private static IReadOnlyDictionary<string, string> InitialSourceStatuses()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in UnitVoiceCatalog.SourceKeys) values.Add(key, "Pending");
            return new ReadOnlyDictionary<string, string>(values);
        }
        private static void SetSourceStatus(string key, string value)
        {
            lock (demandGate)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in sourceStatuses) values.Add(row.Key, row.Value);
                if (values.ContainsKey(key)) values[key] = value;
                Volatile.Write(ref sourceStatuses, new ReadOnlyDictionary<string, string>(values));
            }
        }
        private static IntPtr AllocateDemand() { var value = Marshal.AllocHGlobal(1); Marshal.WriteByte(value, 0); return value; }

        internal static void RefreshDemand()
        {
            lock (demandGate) WriteDemand(Volatile.Read(ref session).Active);
        }

        private static void WriteDemand(bool active)
        {
            bool enabled = Available && UnitVoiceRuntime.ManagedReady && active && UnitVoiceEvents.Demand;
            Marshal.WriteByte(demand, enabled ? (byte)1 : (byte)0);
            foreach (var entry in entries)
                if (entry.Target.Kind != 4) Marshal.WriteByte(entry.Gate, enabled && entry.Ready ? (byte)1 : (byte)0);
        }

        internal static void SetSession(long sessionId, bool active)
        {
            lock (demandGate)
            {
                // Enabling raises the native byte before publishing the active session;
                // disabling retires the session before clearing the byte.
                if (active) WriteDemand(true);
                Volatile.Write(ref session, new Session(sessionId, active));
                if (!active) WriteDemand(false);
            }
            // Do not clear transport state: an already emitted token must still restore its
            // original filename, even when this session has ended before Unity consumes it.
        }

        internal static NativeSiteSpan[] Spans()
        {
            var values = new NativeSiteSpan[UnitVoiceNativeContract.Targets.Length];
            for (int i = 0; i < values.Length; i++)
            {
                var target = UnitVoiceNativeContract.Targets[i];
                values[i] = new NativeSiteSpan(target.Rva, target.Prologue.Length, NativeSiteMode.Exclusive);
            }
            return values;
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, ManualLogSource logger, NativeOwnershipRegistry ownership, string hash)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            log = logger;
            try
            {
                NativeContract.ValidateBackendVersion(typeof(NativeDetourBackend).Assembly.GetName().Version);
                var live = new ReadOnlySpan<byte>(context.ModuleHandle.ToPointer(), context.Memory.Length);
                UnitVoiceNativeContract.ValidateIdentity(context.Memory, hash);
                ValidateUnitLayout();
                engineGate = EngineMonitor.Current ?? throw new InvalidOperationException("Unit voice engine monitor unavailable.");
                // Instantiate the lazy manager outside every native source callback.
                units = GameUnitManagerAPI.Instance ?? throw new InvalidOperationException("Unit voice unit manager unavailable.");
                moduleBase = unchecked((ulong)context.ModuleHandle.ToInt64());
                // The common output contract is mandatory. Each source entry owns a separate
                // reservation and failure boundary, so one occupied recruitment entry cannot
                // remove healthy troop or civilian contexts.
                Entry output = null;
                foreach (var target in UnitVoiceNativeContract.Targets)
                {
                    var entry = new Entry(target);
                    lock (demandGate) entries.Add(entry);
                    if (target.Kind == 4) output = entry;
                }
                if (output == null) throw new InvalidOperationException("Unit voice output contract missing.");
                Prepare(output, context, live, ownership);
                foreach (var entry in entries)
                {
                    if (entry == output) continue;
                    try { Prepare(entry, context, live, ownership); }
                    catch (Exception error)
                    {
                        RetireUnpublished(entry, ownership);
                        SourceFailed(entry, error);
                    }
                }
                // Every valid candidate was checked before the first code publication.
                Publish(output);
                foreach (var entry in entries)
                {
                    if (entry == output || !entry.Prepared) continue;
                    try { Publish(entry); SetSourceStatus(entry.Target.Key, "Ready"); }
                    catch (Exception error)
                    {
                        // Publication may have begun: keep executable memory permanently rooted,
                        // and turn off this entry's private guard instead of undoing its patch.
                        entry.Ready = false; Marshal.WriteByte(entry.Gate, 0);
                        SourceFailed(entry, error);
                    }
                }
                Volatile.Write(ref ready, 1);
                Volatile.Write(ref status, "Ready");
                RefreshDemand();
                NativeApiLog.Info(log, "UNIT_VOICE_NATIVE_READY: independent source scopes and shared speech output, permanent NativeX64/Absolute guards; snapshots require the existing engine monitor.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref ready, 0); RefreshDemand();
                Volatile.Write(ref status, "Unavailable: " + error.Message);
                foreach (var entry in entries) RetireUnpublished(entry, ownership);
                foreach (string key in UnitVoiceCatalog.SourceKeys) SetSourceStatus(key, "Unavailable: common unit voice pipeline failed: " + error.Message);
                // Published hooks remain rooted and dormant; no executable code is undone.
                NativeApiLog.Error(log, "UNIT_VOICE_NATIVE_UNAVAILABLE: " + error);
            }
        }

        private static string Group(Entry entry) => SiteName + "." + entry.Target.Key;

        private static void Prepare(Entry entry, CrusaderLibraryLoadContext context, ReadOnlySpan<byte> live, NativeOwnershipRegistry ownership)
        {
            var target = entry.Target;
            UnitVoiceNativeContract.ValidateEntry(context.Memory, live, target);
            ApiSharedNativeSites.Claim(ownership, Group(entry), new[] { new NativeSiteSpan(target.Rva, target.Prologue.Length, NativeSiteMode.Exclusive) },
                reason => new InvalidOperationException(reason));
            entry.Claimed = true;
            entry.Callback = (a, b, c, d) => Invoke(entry, a, b, c, d);
            entry.Bridge = new UnitVoiceNativeGuardBridge(entry.Callback, entry.Gate);
            var request = new DetourRequest<NativeCall> {
                Name = "APIShared.UnitVoices." + target.Key, TargetAddress = moduleBase + (uint)target.Rva,
                Callback = entry.Callback, IntermediaryFactory = entry.Bridge
            };
            entry.Hook = (NativeDetour<NativeCall>)NativeContract.Backend(target.FunctionLength, NativeStartupPatchWindow.SuspendThreads).CreateDetour(in request);
            UnitVoiceNativeContract.ValidateHook(entry.Hook, target, request.TargetAddress);
            entry.Bridge.Validate(entry.Hook);
            entry.Prepared = true;
        }

        private static void Publish(Entry entry)
        {
            entry.Published = true;
            entry.Hook.Enable();
            UnitVoiceNativeContract.ValidateHook(entry.Hook, entry.Target, moduleBase + (uint)entry.Target.Rva);
            entry.Bridge.Validate(entry.Hook);
            if (!entry.Hook.IsInstalled) throw new InvalidOperationException("Unit voice hook publication incomplete.");
            entry.Ready = true;
        }

        private static void RetireUnpublished(Entry entry, NativeOwnershipRegistry ownership)
        {
            if (entry.Published) return;
            entry.Ready = false; entry.Prepared = false; Marshal.WriteByte(entry.Gate, 0);
            entry.Hook?.Dispose(); entry.Hook = null;
            if (entry.Claimed) { ApiSharedNativeSites.Release(ownership, Group(entry)); entry.Claimed = false; }
        }

        private static void SourceFailed(Entry entry, Exception error)
        {
            SetSourceStatus(entry.Target.Key, "Unavailable: " + error.Message);
            NativeApiLog.Error(log, "UNIT_VOICE_SOURCE_UNAVAILABLE: " + entry.Target.Key + ": " + error.Message);
        }

        private static void ValidateUnitLayout()
        {
            if (typeof(GameUnit).StructLayoutAttribute.Size != 0x490) throw new InvalidOperationException("Unit voice unit stride changed.");
            foreach (var spec in new[] { Tuple.Create("r_UnitChimp", 0x8A), Tuple.Create("r_ControllableForPlayerId", 0x92), Tuple.Create("r_GlobalId", 0x94) })
            {
                var field = typeof(GameUnit).GetField(spec.Item1);
                if (field == null || !field.IsPublic || Marshal.OffsetOf(typeof(GameUnit), spec.Item1).ToInt32() != spec.Item2)
                    throw new InvalidOperationException("Unit voice field layout changed: " + spec.Item1);
            }
        }

        private static bool CanCapture() => Available && UnitVoiceRuntime.ManagedReady && UnitVoiceEvents.Demand &&
            !UnitVoiceEvents.InCallback && Volatile.Read(ref session).Active && engineGate != null && Monitor.IsEntered(engineGate);

        private static void Invoke(Entry entry, ulong a, ulong b, ulong c, ulong d)
        {
            if (entry.Target.Kind == 4) { Output(entry, a, b, c, d); return; }
            if (!CanCapture()) { entry.Hook.Original(a, b, c, d); return; }
            CaptureFrame next;
            try { next = Capture(entry.Target, a, b, c); }
            catch (Exception error) { CaptureFailed(error); entry.Hook.Original(a, b, c, d); return; }
            var previous = scope;
            scope = next;
            try { entry.Hook.Original(a, b, c, d); }
            finally { scope = previous; }
        }

        private static CaptureFrame Capture(UnitVoiceNativeTarget target, ulong a, ulong b, ulong c)
        {
            var current = Volatile.Read(ref session);
            var next = new CaptureFrame {
                Valid = current.Active, Kind = target.Kind, Key = target.Key, SessionId = current.Id,
                RequestingPlayer = Player(Marshal.ReadInt32(new IntPtr(unchecked((long)(moduleBase + UnitVoiceNativeContract.NativePlayerRva)))))
            };
            if (target.Kind == 0)
            {
                int requested = unchecked((int)b);
                next.RequestedType = requested >= 0 && requested < (int)eChimps.CHIMP_NUM_TYPES ? (int?)requested : null;
                next.Situation = Situation(unchecked((int)c));
            }
            else if (target.Kind == 1)
            {
                if (scope.Valid && scope.Kind == 0)
                {
                    next.RequestedType = scope.RequestedType; next.Owner = scope.Owner;
                    next.RequestingPlayer = scope.RequestingPlayer; next.Situation = scope.Situation;
                }
                else next.Situation = UnitVoiceSituation.EngineerAction;
            }
            else if (target.Kind == 2)
            {
                int id = Marshal.ReadInt32(new IntPtr(unchecked((long)(moduleBase + UnitVoiceNativeContract.SelectedCivilianRva))));
                if (!UnitAccess.TryGetById(units, id, out GameUnit* unit, out _) || unit->r_GlobalId == 0 || unit->r_UnitChimp == eChimps.CHIMP_TYPE_NULL)
                    next.Valid = false;
                else
                {
                    next.RequestedType = (int)unit->r_UnitChimp;
                    next.Owner = Player(unit->r_ControllableForPlayerId);
                    next.Situation = UnitVoiceSituation.CivilianComment;
                }
            }
            else if (target.Kind == 3)
            {
                int requested = unchecked((int)a);
                next.RequestedType = requested >= 0 && requested < (int)eChimps.CHIMP_NUM_TYPES ? (int?)requested : null;
                next.Situation = UnitVoiceSituation.Recruitment;
            }
            return next;
        }

        private static int? Player(int value) => value >= 1 && value <= 8 ? (int?)value : null;
        private static UnitVoiceSituation Situation(int native)
        {
            // These are hash-bound dispatcher table positions, not unit IDs or game modes.
            switch (native)
            {
                case 0: return UnitVoiceSituation.Selection;
                case 6: return UnitVoiceSituation.Move;
                case 9: return UnitVoiceSituation.MoveUnreachable;
                case 10: return UnitVoiceSituation.MoveDistant;
                case 11: return UnitVoiceSituation.DigMoat;
                case 17: return UnitVoiceSituation.AttackStrong;
                case 21: case 42: return UnitVoiceSituation.AttackWeak;
                case 25: return UnitVoiceSituation.AttackMelee;
                case 41: return UnitVoiceSituation.Disband;
                default: return UnitVoiceSituation.Unknown;
            }
        }

        private static void Output(Entry entry, ulong manager, ulong path, ulong c, ulong d)
        {
            if (!CanCapture() || !scope.Valid || inOutput || manager != moduleBase + UnitVoiceNativeContract.SoundManagerRva)
            { entry.Hook.Original(manager, path, c, d); return; }
            string original, token;
            UnitVoiceCue cue;
            bool eligible;
            try
            {
                original = ReadAscii(new IntPtr(unchecked((long)path)), 64);
                eligible = UnitVoiceCatalog.TryResolveFileName(original, out cue) && MatchesScope(cue);
            }
            catch (Exception error) { CaptureFailed(error); entry.Hook.Original(manager, path, c, d); return; }
            if (!eligible) { entry.Hook.Original(manager, path, c, d); return; }
            UnitVoiceSource source;
            long emission;
            try
            {
                var situation = scope.Situation;
                if (cue.Situation == UnitVoiceSituation.Heal || cue.Situation == UnitVoiceSituation.Sap) situation = cue.Situation;
                if (situation == UnitVoiceSituation.Unknown) situation = cue.Situation;
                var evidence = situation == UnitVoiceSituation.AttackStrong || situation == UnitVoiceSituation.AttackWeak || situation == UnitVoiceSituation.AttackMelee
                    ? UnitVoiceEvidence.Inferred : UnitVoiceEvidence.NativeAudited;
                source = new UnitVoiceSource(scope.RequestingPlayer, scope.Owner, scope.RequestedType, cue.VoiceBankKey, cue.Key, situation, evidence, scope.SessionId, scope.Key);
                token = UnitVoiceTransport.CreateToken(cue, out emission);
                var bytes = Encoding.ASCII.GetBytes(token + "\0");
                if (bytes.Length > 128) throw new InvalidOperationException("Unit voice token exceeds the speech buffer.");
                Marshal.Copy(bytes, 0, tokenBuffer, bytes.Length);
            }
            catch (Exception error) { CaptureFailed(error); entry.Hook.Original(manager, path, c, d); return; }
            inOutput = true;
            try { entry.Hook.Original(manager, unchecked((ulong)tokenBuffer.ToInt64()), c, d); }
            finally { inOutput = false; }
            try
            {
                // EDF0 can reject before copying. Preserve an older pending snapshot in that
                // case, rather than attaching this rejected request to an earlier speech record.
                if (ReadAscii(new IntPtr(unchecked((long)(manager + UnitVoiceNativeContract.SpeechPathOffset))), 128) == token)
                {
                    UnitVoiceTransport.Publish(token, original, source, emission);
                    if (Volatile.Read(ref confirmed) == 0 && Interlocked.Exchange(ref confirmed, 1) == 0)
                        NativeApiLog.Info(log, "UNIT_VOICE_NATIVE_CAPTURE_CONFIRMED: an accepted source was transported after startup cleanup.");
                }
            }
            catch (Exception error) { CaptureFailed(error); } // Token ordinal still restores the original without a snapshot.
        }

        private static bool MatchesScope(UnitVoiceCue cue)
        {
            if (scope.Kind == 2) return cue.VoiceBankKey == "civilian-male" || cue.VoiceBankKey == "civilian-female";
            if (cue.VoiceBankKey == "civilian-male" || cue.VoiceBankKey == "civilian-female") return false;
            if (scope.Kind == 1) return cue.Situation == UnitVoiceSituation.EngineerAction &&
                (cue.VoiceBankKey == "engineer" || cue.VoiceBankKey == "arabian-engineer");
            // A failed nested engineer scope leaves its outer dispatcher frame active. Its
            // special-table cues must not be relabelled as healthy dispatcher sources.
            if (cue.Situation == UnitVoiceSituation.EngineerAction) return false;
            if (scope.Kind == 3) return cue.Situation == UnitVoiceSituation.Selection;
            return true;
        }

        internal static string ReadAscii(IntPtr pointer, int maximum)
        {
            if (pointer == IntPtr.Zero) return null;
            int length = 0;
            while (length < maximum && Marshal.ReadByte(pointer, length) != 0) length++;
            if (length == maximum) return null;
            var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
            for (int i = 0; i < bytes.Length; i++) if (bytes[i] > 0x7F) return null;
            return Encoding.ASCII.GetString(bytes);
        }

        private static void CaptureFailed(Exception error)
        {
            long count = Interlocked.Increment(ref failures);
            if (count == 1) try { NativeApiLog.Error(log, "UNIT_VOICE_CAPTURE_FAILURE: preserving Vanilla: " + error); } catch { }
        }

        private struct CaptureFrame
        {
            internal bool Valid;
            internal int Kind;
            internal string Key;
            internal long SessionId;
            internal int? RequestingPlayer, Owner, RequestedType;
            internal UnitVoiceSituation Situation;
        }
        private sealed class Session
        {
            internal readonly long Id; internal readonly bool Active;
            internal Session(long id, bool active) { Id = id; Active = active; }
        }
        private sealed class Entry
        {
            internal readonly UnitVoiceNativeTarget Target;
            internal readonly IntPtr Gate;
            internal volatile bool Ready;
            internal bool Prepared, Published, Claimed;
            internal NativeCall Callback;
            internal UnitVoiceNativeGuardBridge Bridge;
            internal NativeDetour<NativeCall> Hook;
            internal Entry(UnitVoiceNativeTarget target) { Target = target; Gate = target.Kind == 4 ? demand : AllocateDemand(); }
        }
    }

    // A demand-zero source returns through the original trampoline without a managed transition.
    // RAX is scratch: every audited entry writes it before reading it; incoming flags are not read.
    internal sealed class UnitVoiceNativeGuardBridge : IDetourIntermediaryFactory
    {
        private readonly ulong callback, demand;
        internal UnitVoiceNativeGuardBridge(Delegate callback, IntPtr demand)
        { this.callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(callback).ToInt64()); this.demand = unchecked((ulong)demand.ToInt64()); }
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Unit voice guard requires the audited Win64 backend.");
            ulong trampoline = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var function = ExecutableFunction.Create(asm => {
                var active = SoundBridgeCode.EmitGuard(asm, demand, trampoline);
                asm.Label(ref active); asm.mov(rax, callback); asm.jmp(rax);
            });
            return new DetourIntermediaryStubs(function.EntryPoint, context.TrampolineAddress, function);
        }
        internal void Validate(NativeDetour<UnitVoiceNativeRuntime.NativeCall> hook)
        {
            var decoder = SoundBridgeCode.Open(hook.HookEntryPointAddress);
            SoundBridgeCode.ValidateGuard(decoder, demand, unchecked((ulong)hook.TrampolineAddress.ToInt64()));
            var move = decoder.Decode(); var jump = decoder.Decode();
            if (move.Mnemonic != Mnemonic.Mov || move.Op0Register != Register.RAX || move.Immediate64 != callback ||
                jump.Mnemonic != Mnemonic.Jmp || jump.Op0Register != Register.RAX)
                throw new InvalidOperationException("Unit voice callback tail bridge changed.");
        }
    }
}
