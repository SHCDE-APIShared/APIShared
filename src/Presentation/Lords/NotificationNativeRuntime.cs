using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.Presentation
{
    internal static class NotificationNativeRuntime
    {
        private const string Feature = "Notification presentation identity";
        private const int ManagerRva = 0x1B62EE0;
        private const int RenderRva = 0xA98820;
        private const int StagingOffset = 0x10C9E8C;
        private const int CountOffset = 0x94C;
        private const int SlotSections = 0xDC;
        private const int SlotTextIndices = 0x104;
        private const int SlotVideos = 0x154;
        private const int SlotAudio = 0x53C;
        private const int QueueCapacity = 10;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PresentCall(IntPtr manager, IntPtr video, IntPtr audio);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ResetManagerCall(IntPtr manager);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ResetMapCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void TutorialCall(int action);

        // Each span ends after the complete instructions required by the six-byte Indirect jump.
        // The longest span leaves the existing Extender's FEF21 interior hook untouched.
        private static readonly Site Presentation = new Site(0xFEE50, 0x48, 0x89, 0x5C, 0x24, 0x18, 0x55);
        private static readonly Site QueueReset = new Site(0x102C30, 0x40, 0x53, 0x48, 0x83, 0xEC, 0x20);
        private static readonly Site MultiplayerReset = new Site(0x24220, 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10);
        private static readonly Site MapReset = new Site(0x87D10, 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57);
        private static readonly Site TutorialReset = new Site(0x104240, 0x48, 0x83, 0xEC, 0x28, 0x83, 0xF9, 0x02);

        private static readonly DetourHandle<PresentCall> present = new DetourHandle<PresentCall>();
        private static readonly DetourHandle<ResetManagerCall> queueReset = new DetourHandle<ResetManagerCall>();
        private static readonly DetourHandle<ResetManagerCall> multiplayerReset = new DetourHandle<ResetManagerCall>();
        private static readonly DetourHandle<ResetMapCall> mapReset = new DetourHandle<ResetMapCall>();
        private static readonly DetourHandle<TutorialCall> tutorialReset = new DetourHandle<TutorialCall>();
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();

        // Queue slots are touched only while holding the game's engine monitor. Reset writers
        // outside that monitor invalidate by changing the generation rather than editing the mirror.
        private static readonly Selection[] slots = new Selection[QueueCapacity];
        private static IntPtr module, manager, render, staging;
        private static object engineGate;
        private static ManualLogSource log;
        private static Patching.SafeIlHook tickHook;
        private static int installed, attempted, renderReady, epoch = 1, localEpoch, warned;
        private static long nextTicket, currentTicket;
        private static int currentEpoch;
        private static int activePresentation;
        private static int resetsInProgress;
        [ThreadStatic] private static int presentDepth;

        internal static bool IsReady => Volatile.Read(ref installed) != 0 && Volatile.Read(ref renderReady) != 0 &&
            Volatile.Read(ref resetsInProgress) == 0;
        internal static int Epoch => Volatile.Read(ref epoch);
        internal static bool OwnsEngine => engineGate != null && Monitor.IsEntered(engineGate);
        internal static bool HasActivePresentation => Volatile.Read(ref activePresentation) != 0;

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            log = logger;
            if (!string.Equals(hash, NativeLordMessageCatalog.NativeHash, StringComparison.OrdinalIgnoreCase))
            {
                NativeApiLog.Error(log, "NOTIFICATION_IDENTITY_UNAVAILABLE: unaudited native image.");
                return;
            }
            var ownership = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            HookTransaction transaction = null;
            bool claimed = false;
            try
            {
                if (!MarkedUnitSelectionAPI.HasRunBoundary || !NativeForeignPatchGuard.IsBound)
                    throw new InvalidOperationException("Render publication or native span guard is unavailable.");
                engineGate = EngineMonitor.Current ?? throw new InvalidOperationException("Engine monitor is unavailable.");
                module = context.ModuleHandle;
                manager = module + ManagerRva;
                render = module + RenderRva;
                staging = render + StagingOffset;
                if (context.Memory.Length <= RenderRva + StagingOffset || context.Memory.Length <= ManagerRva + CountOffset)
                    throw new InvalidOperationException("Notification data lies outside the native image.");

                var run = typeof(EngineInterface).GetMethod(nameof(EngineInterface.run), BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(bool) }, null);
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.NotificationRenderEpoch", run,
                    NotificationTransport.InstallTickEdit, log, out tickHook, out string reason))
                    throw new InvalidOperationException("Render boundaries unavailable: " + reason);
                tickHook.Rebuilt += OnTickRebuilt;
                Volatile.Write(ref renderReady, tickHook.IsApplied ? 1 : 0);

                var targets = new[] { Presentation, QueueReset, MultiplayerReset, MapReset, TutorialReset };
                var spans = new NativeSiteSpan[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    var target = targets[i];
                    if (!context.Memory.Slice(target.Rva, target.Bytes.Length).SequenceEqual(target.Bytes))
                        throw new InvalidOperationException("Notification prologue changed at 0x" + target.Rva.ToString("X") + ".");
                    spans[i] = new NativeSiteSpan(target.Rva, target.Bytes.Length, NativeSiteMode.Exclusive);
                }
                ApiSharedNativeSites.Claim(ownership, Feature, spans, reasonText => new InvalidOperationException(reasonText));
                claimed = true;
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions
                    {
                        FailureMode = TransactionFailureMode.RollbackAndThrow,
                        OwnsHooks = true,
                        InstallOnCommit = false,
                        Backend = NativeStartupPatchWindow.Backend(new NativeDetourBackend(new NativeDetourOptions
                        { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false }))
                    });
                transaction.AddDetour(present, Presentation.Target(module), (PresentCall)Present);
                transaction.AddDetour(queueReset, QueueReset.Target(module), (ResetManagerCall)ResetQueue);
                transaction.AddDetour(multiplayerReset, MultiplayerReset.Target(module), (ResetManagerCall)ResetMultiplayer);
                transaction.AddDetour(mapReset, MapReset.Target(module), (ResetMapCall)ResetMap);
                transaction.AddDetour(tutorialReset, TutorialReset.Target(module), (TutorialCall)Tutorial);
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess || !present.Success || !queueReset.Success || !multiplayerReset.Success || !mapReset.Success || !tutorialReset.Success)
                    throw new InvalidOperationException("Could not prepare every notification observer.");

                ValidateHooks(false);
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out var lifecycle, out var diagnostic) ||
                    !lifecycle.TryRegisterObserver("Presentation.NotificationIdentity", OnMissionStart, OnMissionBoundary, OnMissionBoundary, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Notification lifetime is unavailable.");
                NativeHookPublication.Enable(transaction, result, publication, typeof(NotificationNativeRuntime), () => ValidateHooks(true));
                Volatile.Write(ref installed, 1);
                NativeApiLog.Info(log, "NOTIFICATION_IDENTITY_READY: FEE50/6, 102C30/6, 24220/A, 87D10/6, 104240/7; notification observers installed for process lifetime.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref installed, 0);
                publication.RollbackUnpublished(transaction, () => { if (claimed) ApiSharedNativeSites.Release(ownership, Feature); });
                NativeApiLog.Error(log, "NOTIFICATION_IDENTITY_UNAVAILABLE: " + error);
            }
        }

        private static void ValidateHooks(bool installedHooks)
        {
            Presentation.Validate(present.Hook, module, installedHooks);
            QueueReset.Validate(queueReset.Hook, module, installedHooks);
            MultiplayerReset.Validate(multiplayerReset.Hook, module, installedHooks);
            MapReset.Validate(mapReset.Hook, module, installedHooks);
            TutorialReset.Validate(tutorialReset.Hook, module, installedHooks);
        }

        private static void OnMissionBoundary(MissionLifecycleNotification _) => Invalidate();
        private static void OnMissionStart(MissionLifecycleNotification _)
        {
            Invalidate();
            if (!IsReady) return;
            try { ValidateHooks(true); }
            catch (Exception error)
            {
                Volatile.Write(ref installed, 0);
                NativeApiLog.Error(log, "NOTIFICATION_IDENTITY_UNAVAILABLE: installed spans changed. " + error);
            }
        }

        private static void OnTickRebuilt(Patching.SafeIlHook hook)
        {
            Volatile.Write(ref renderReady, hook.IsApplied ? 1 : 0);
            Invalidate();
        }

        internal static void Invalidate() => Interlocked.Increment(ref epoch);

        internal static bool IsCurrent(ulong ticket) =>
            IsReady && OwnsEngine && ticket != 0 && unchecked((ulong)currentTicket) == ticket &&
            currentEpoch == Epoch && Marshal.ReadInt32(manager) != 0;

        private static int Synchronize()
        {
            int observed = Epoch;
            if (localEpoch != observed)
            {
                Array.Clear(slots, 0, slots.Length);
                localEpoch = observed;
            }
            return observed;
        }

        internal static ulong BeginEnqueue(ulong address)
        {
            if (!IsReady || address != unchecked((ulong)manager.ToInt64())) return 0;
            if (!OwnsEngine) { Invalidate(); return 0; }
            int observed = Synchronize();
            int count = Marshal.ReadInt32(manager, CountOffset);
            if (count < 0 || count > QueueCapacity) { Invalidate(); return 0; }
            int marker = Marshal.ReadInt32(manager) == 0 ? 1 : count + 2;
            return ((ulong)unchecked((uint)observed) << 32) | (uint)marker;
        }

        internal static void EndEnqueue(ulong address, ulong before, int section, int textIndex, string subtitle, bool known)
        {
            if (!IsReady || !OwnsEngine || before == 0 || address != unchecked((ulong)manager.ToInt64()) ||
                unchecked((int)(before >> 32)) != Epoch) return;
            int marker = unchecked((int)before);
            if (marker == 1) return; // Immediate presentation was observed synchronously.
            int slot = marker - 2;
            if (slot < 0 || slot > QueueCapacity) { Invalidate(); return; }
            int count = Marshal.ReadInt32(manager, CountOffset);
            if (slot == QueueCapacity && count == QueueCapacity) return; // Full queue.
            if (count != slot + 1 || count > QueueCapacity ||
                Marshal.ReadInt32(manager, SlotSections + slot * 4) != section ||
                Marshal.ReadInt32(manager, SlotTextIndices + slot * 4) != textIndex)
            { Invalidate(); return; }
            slots[slot] = new Selection(section, textIndex, subtitle, known);
        }

        private static void Present(IntPtr owner, IntPtr video, IntPtr audio)
        {
            var original = present.Original;
            if (!IsReady || owner != manager)
            {
                original(owner, video, audio);
                return;
            }
            if (!OwnsEngine || presentDepth != 0)
            {
                Invalidate();
                original(owner, video, audio);
                return;
            }

            presentDepth++;
            try
            {
                var observation = default(Observation);
                try { observation = BeforePresentation(owner, video, audio); }
                catch (Exception error) { FailObservation(error); }

                original(owner, video, audio);

                try
                {
                    if (!observation.Valid || observation.Epoch != Epoch) return;
                    int end = Marshal.ReadInt32(render, 8);
                    if (Marshal.ReadIntPtr(render) != observation.Destination || end <= observation.Start ||
                        end - observation.Start > 16 || end > NotificationTransport.MaxRecordCount)
                    {
                        Invalidate();
                        return;
                    }
                    ulong ticket = unchecked((ulong)Interlocked.Increment(ref nextTicket));
                    var record = new NotificationEmission(ticket, observation.Start, end, observation.Selection.Section,
                        observation.Selection.TextIndex, observation.Selection.Subtitle, observation.Selection.Known,
                        Marshal.ReadByte(owner, 0x70) != 0, Marshal.ReadByte(owner, 0x0C) != 0, observation.Epoch);
                    if (!NotificationTransport.Observe(observation.Epoch, observation.Destination, observation.Destination == staging, record))
                    {
                        Invalidate();
                        return;
                    }
                    currentTicket = unchecked((long)ticket);
                    currentEpoch = observation.Epoch;
                    Volatile.Write(ref activePresentation, 1);
                }
                catch (Exception error) { FailObservation(error); }
            }
            finally { presentDepth--; }
        }

        private static Observation BeforePresentation(IntPtr owner, IntPtr video, IntPtr audio)
        {
            int version = Synchronize();
            int section = Marshal.ReadInt32(owner, 4);
            int textIndex = Marshal.ReadInt32(owner, 8);
            var selection = new Selection(section, textIndex, null, false);
            bool promotedVideo = video == owner + SlotVideos;
            bool promotedAudio = audio == owner + SlotAudio;
            if (promotedVideo != promotedAudio)
            { Invalidate(); return default; }

            if (promotedVideo)
            {
                int count = Marshal.ReadInt32(owner, CountOffset);
                if (count < 1 || count > QueueCapacity) { Invalidate(); return default; }
                var first = slots[0];
                Array.Copy(slots, 1, slots, 0, count - 1);
                Array.Clear(slots, count - 1, QueueCapacity - count + 1);
                if (first.Known && first.Section == section && first.TextIndex == textIndex)
                    selection = first;
            }
            else if (NotificationEnqueueCapture.TryGetSubmitting(unchecked((ulong)owner.ToInt64()), section, textIndex, out string subtitle))
            {
                selection = new Selection(section, textIndex, subtitle, true);
            }

            // The audited generic enqueue always sets speaker to zero. The Lord wrapper
            // uses skirmish speech or a negative player selector for non-announcements.
            if (!selection.Known && section > 0 && section != (int)global::Enums.eTextSections.TEXT_SKIRMISH_SPEECH &&
                Marshal.ReadInt32(owner, 0xD4) == 0)
                selection = new Selection(section, textIndex, null, true);

            int start = Marshal.ReadInt32(render, 8);
            IntPtr destination = Marshal.ReadIntPtr(render);
            if (start < 0 || start >= NotificationTransport.MaxRecordCount || destination == IntPtr.Zero)
            { Invalidate(); return default; }
            return new Observation(version, destination, start, selection);
        }

        internal static bool IsStagingDestination() => IsReady && Marshal.ReadIntPtr(render) == staging;
        internal static int ReadStagingCount() => Marshal.ReadInt32(render, 8);
        internal static void ReconcileActivePresentation()
        {
            // Unity does not read the native manager. A finalizer with no successor
            // never calls Present, so refresh availability at the serialized tick boundary.
            if (HasActivePresentation && (currentEpoch != Epoch || Marshal.ReadInt32(manager) == 0))
                Volatile.Write(ref activePresentation, 0);
        }

        internal static void TransportFailed(Exception error) => FailObservation(error);

        private static void FailObservation(Exception error)
        {
            Invalidate();
            if (Interlocked.Exchange(ref warned, 1) == 0)
                NativeApiLog.Error(log, "NOTIFICATION_IDENTITY_OBSERVATION_FAILED: " + error);
        }

        private static void EnterReset()
        {
            Interlocked.Increment(ref resetsInProgress);
            Invalidate();
        }

        private static void ExitReset()
        {
            Invalidate();
            Interlocked.Decrement(ref resetsInProgress);
        }

        private static void ResetQueue(IntPtr owner)
        {
            if (owner != manager) { queueReset.Original(owner); return; }
            EnterReset();
            try { queueReset.Original(owner); }
            finally { ExitReset(); }
        }

        private static void ResetMultiplayer(IntPtr owner)
        {
            EnterReset();
            try { multiplayerReset.Original(owner); }
            finally { ExitReset(); }
        }

        private static void ResetMap()
        {
            EnterReset();
            try { mapReset.Original(); }
            finally { ExitReset(); }
        }

        private static void Tutorial(int action)
        {
            if (action != 2) { tutorialReset.Original(action); return; }
            EnterReset();
            try { tutorialReset.Original(action); }
            finally { ExitReset(); }
        }

        private readonly struct Observation
        {
            internal readonly int Epoch, Start;
            internal readonly IntPtr Destination;
            internal readonly Selection Selection;
            internal bool Valid => Destination != IntPtr.Zero;
            internal Observation(int epoch, IntPtr destination, int start, Selection selection)
            { Epoch = epoch; Destination = destination; Start = start; Selection = selection; }
        }

        private readonly struct Selection
        {
            internal readonly int Section, TextIndex;
            internal readonly string Subtitle;
            internal readonly bool Known;
            internal Selection(int section, int textIndex, string subtitle, bool known)
            { Section = section; TextIndex = textIndex; Subtitle = subtitle; Known = known; }
        }

        private sealed class Site
        {
            internal readonly int Rva;
            internal readonly byte[] Bytes;
            internal Site(int rva, params byte[] bytes) { Rva = rva; Bytes = bytes; }
            internal HookTarget Target(IntPtr moduleBase) => HookTarget.FromAddress(unchecked((ulong)(moduleBase + Rva).ToInt64()));
            internal void Validate<T>(IDetour<T> hook, IntPtr moduleBase, bool installedHook) where T : Delegate =>
                NativeIndirectEntry.Validate(hook, moduleBase + Rva, Bytes, Bytes.Length, installedHook);
        }
    }
}
