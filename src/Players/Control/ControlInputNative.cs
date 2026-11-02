using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using SHCDESE.API.LowLevel;

namespace APIShared.PlayerControl
{
    // Both spectator-input boundaries, installed once in APIShared's startup window and
    // published together. With actor zero both stubs follow the complete original blocks;
    // LocalInputControl is the only writer of the actor cell. Hash-bound: the short
    // CMOVNE/store pairs cannot identify the surrounding input lifetime on another image.
    internal static class ControlInputNative
    {
        internal const int RunTickRva = 0x868F6, DispatcherRva = 0x8D128, ViewRva = 0x88E3D70;
        // CMOVNE EAX,R12D; MOV [view],EAX; continuation MOVZX R8D,R14B.
        internal static readonly byte[] RunTickWindow = { 0x41, 0x0F, 0x45, 0xC4, 0x89, 0x05, 0x70, 0xD4, 0x85, 0x08,
            0x45, 0x0F, 0xB6, 0xC6 };
        // CMOVNE EAX,EDI; XOR R9D,R9D; continuation MOV [view],EAX.
        internal static readonly byte[] DispatcherWindow = { 0x0F, 0x45, 0xC7, 0x45, 0x33, 0xC9,
            0x89, 0x05, 0x3C, 0x6C, 0x85, 0x08 };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void InputBoundaryCall();
        // The intermediary stubs never call managed code; this root only satisfies the backend.
        private static readonly InputBoundaryCall unreachable = Unreachable;
        private static readonly IntPtr actorCell = AllocateActor();
        private static NativeDetour<InputBoundaryCall> runTickHook, dispatcherHook;
        private static ControlInputBridge runTickBridge, dispatcherBridge;
        private static bool publicationStarted;
        private static volatile bool ready;

        internal static bool IsReady => ready;
        internal static int Actor => Marshal.ReadInt32(actorCell);

        private static IntPtr AllocateActor() { var cell = Marshal.AllocHGlobal(sizeof(int)); Marshal.WriteInt32(cell, 0); return cell; }
        private static void Unreachable() { }

        // Aligned four-byte store; each stub reads the cell once per decision. Callers hold the
        // engine monitor, which also serializes DLL_RunTick, so no input pass sees a transition.
        internal static void WriteActor(int value)
        {
            if (value != 0 && !ready) throw new InvalidOperationException("Player-control input boundaries are not installed.");
            Marshal.WriteInt32(actorCell, value);
        }

        internal static NativeDetourBackend CreateBackend(int scanLimit, bool suspendThreads) =>
            new NativeDetourBackend(new NativeDetourOptions {
                AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false,
                Chaining = HookChainingPolicy.Refuse, FunctionScanLimit = scanLimit, SuspendThreadsDuringPatch = suspendThreads
            });

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (ready || publicationStarted) return;
            // The view-identity step already reports an unsupported image; one cause, one failure.
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
            {
                NativeApiLog.Info(log, "PLAYER_CONTROL_INPUT: skipped; spectator input control requires the audited native image.");
                return;
            }
            if (!NativeStartupPatchWindow.IsOpen)
                throw new InvalidOperationException("PLAYER_CONTROL_INPUT: initialize only in the startup patch window; no late native installation is allowed.");
            ulong module = unchecked((ulong)context.ModuleHandle.ToInt64());
            if (SHCDESE.GameGlobals.GameGlobalsManager.Instance.LocalPlayerIdVA != module + ViewRva ||
                (ulong)ViewRva + 4 > (ulong)context.Memory.Length)
                throw new InvalidOperationException("PLAYER_CONTROL_INPUT: native view cell differs from the audited image.");
            VerifyPristine(context.Memory, RunTickRva, RunTickWindow);
            VerifyPristine(context.Memory, DispatcherRva, DispatcherWindow);
            IntPtr runTick = context.ModuleHandle + RunTickRva, dispatcher = context.ModuleHandle + DispatcherRva;
            NativeSites.NativeIndirectEntry.VerifyBytes(runTick, RunTickWindow);
            NativeSites.NativeIndirectEntry.VerifyBytes(dispatcher, DispatcherWindow);
            var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(sites, ApiSharedNativeSites.PlayerControlInput, ApiSharedNativeSites.PlayerControlInputSpans(),
                reason => new InvalidOperationException(reason));
            ulong view = module + ViewRva, actor = unchecked((ulong)actorCell.ToInt64());
            try
            {
                runTickBridge = new ControlInputBridge(ControlInputSite.RunTick, actor, view,
                    unchecked((ulong)(runTick + ControlInputBridge.DisplacedLength(ControlInputSite.RunTick)).ToInt64()));
                dispatcherBridge = new ControlInputBridge(ControlInputSite.Dispatcher, actor, view,
                    unchecked((ulong)(dispatcher + ControlInputBridge.DisplacedLength(ControlInputSite.Dispatcher)).ToInt64()));
                runTickHook = Create(runTick, runTickBridge, RunTickWindow.Length, "APIShared player-control RunTick input");
                dispatcherHook = Create(dispatcher, dispatcherBridge, DispatcherWindow.Length, "APIShared player-control dispatcher input");
                runTickBridge.Validate(runTickHook, runTick, RunTickWindow, false);
                dispatcherBridge.Validate(dispatcherHook, dispatcher, DispatcherWindow, false);
            }
            catch
            {
                // Only the unpublished candidates may be disposed; the claim follows them.
                dispatcherHook?.Dispose(); runTickHook?.Dispose(); dispatcherHook = runTickHook = null;
                ApiSharedNativeSites.Release(sites, ApiSharedNativeSites.PlayerControlInput);
                throw;
            }
            // From here both stubs stay rooted forever with actor zero (complete original paths).
            publicationStarted = true;
            runTickHook.Enable();
            dispatcherHook.Enable();
            runTickBridge.Validate(runTickHook, runTick, RunTickWindow);
            dispatcherBridge.Validate(dispatcherHook, dispatcher, DispatcherWindow);
            ready = true;
            NativeApiLog.Info(log, "PLAYER_CONTROL_INPUT: method=reference-rva; RunTick span=[0x868F6,0x86900) Indirect/10, " +
                "continuation=movzx r8d,r14b; dispatcher span=[0x8D128,0x8D12E) Indirect/6, continuation=mov [view],eax; " +
                "native view=reference-rva 0x88E3D70; actor zero follows Vanilla; process lifetime.");
        }

        private static NativeDetour<InputBoundaryCall> Create(IntPtr target, ControlInputBridge bridge, int window, string name)
        {
            var request = new DetourRequest<InputBoundaryCall> {
                Name = name, TargetAddress = unchecked((ulong)target.ToInt64()), Callback = unreachable, IntermediaryFactory = bridge
            };
            var hook = CreateBackend(window, NativeStartupPatchWindow.SuspendThreads).CreateDetour(in request) as NativeDetour<InputBoundaryCall>;
            if (hook == null || hook.IsInstalled) throw new InvalidOperationException("PLAYER_CONTROL_INPUT: detour candidate contract changed.");
            return hook;
        }

        private static void VerifyPristine(ReadOnlySpan<byte> image, int rva, byte[] window)
        {
            if (rva < 0 || rva > image.Length - window.Length || !image.Slice(rva, window.Length).SequenceEqual(window))
                throw new InvalidOperationException("PLAYER_CONTROL_INPUT: pristine input block differs at RVA 0x" + rva.ToString("X") + ".");
        }
    }
}
