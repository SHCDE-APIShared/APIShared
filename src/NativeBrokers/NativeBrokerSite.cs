using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using APIShared.NativeSites;

namespace APIShared.NativeBrokers
{
    // One brokered native entry: its pristine-byte contract, the native demand byte and the process-lived
    // detour. Nothing is patched before EnsureInstalled, which the registry calls for the first
    // registration. Installation claims the entry in the native-site registry with the expected original
    // bytes, checks the live bytes itself as well (the registry checks them only once it is bound), and
    // refuses an occupied entry. A refusal or failure is terminal for the process and logged once:
    // no later registration retries a patch over the same bytes.
    internal sealed unsafe class NativeBrokerSite<TNative> where TNative : Delegate
    {
        private readonly object sync = new object();
        private readonly byte[] pristine;
        private readonly int displaced;
        private readonly byte* demand;
        private readonly TNative thunk;
        private readonly Action<TNative> publishTrampoline;
        private readonly int scanLimit;
        private NativeOwnershipRegistry ownership;
        private ManualLogSource log;
        private ulong address;
        private bool suspendThreads = true;
        private NativeBrokerState state = NativeBrokerState.Pending;
        private string diagnostic = "APIShared has not bound CrusaderDE.dll yet.";
        private TNative entry;
        private NativeDetour<TNative> hook;
        private NativeBrokerBridge bridge;
        private bool published;

        // Set once before the detour is published and never changed.
        internal TNative Trampoline;

        // thunk: the rooted managed entry the bridge tail-jumps into. publishTrampoline hands the
        // dispatcher its original before the detour is published.
        internal NativeBrokerSite(string name, int rva, byte[] pristine, int displaced, IntPtr demand, TNative thunk,
            Action<TNative> publishTrampoline, int scanLimit)
        {
            if (displaced < 14 || displaced > pristine.Length) throw new ArgumentOutOfRangeException(nameof(displaced));
            if (demand == IntPtr.Zero) throw new ArgumentException("A native demand byte is required.", nameof(demand));
            Name = name; Rva = rva; this.pristine = (byte[])pristine.Clone(); this.displaced = displaced;
            this.demand = (byte*)demand.ToPointer(); this.thunk = thunk ?? throw new ArgumentNullException(nameof(thunk));
            this.publishTrampoline = publishTrampoline ?? throw new ArgumentNullException(nameof(publishTrampoline));
            this.scanLimit = scanLimit;
        }

        internal string Name { get; }
        internal int Rva { get; }
        internal ulong Address { get { lock (sync) return address; } }
        internal NativeBrokerState State { get { lock (sync) return state; } }
        internal string Diagnostic { get { lock (sync) return diagnostic; } }
        internal byte DemandValue => Volatile.Read(ref *demand);
        internal NativeDetour<TNative> Hook => hook;

        // Vanilla without broker consumers: the trampoline once installed, otherwise the live entry
        // (which still runs a foreign owner's detour while the broker is refused).
        internal TNative Original
        {
            get
            {
                TNative trampoline = Volatile.Read(ref Trampoline);
                if (trampoline != null) return trampoline;
                return Volatile.Read(ref entry) ?? throw new InvalidOperationException(Name + " is not bound to CrusaderDE.dll yet.");
            }
        }

        // Exclusive over the displaced bytes with their expected hash, Observe over the continuation
        // instruction the trampoline resumes at.
        internal NativeSiteSpan[] Spans() => Spans(Rva, pristine, displaced);
        internal static NativeSiteSpan[] Spans(int rva, byte[] pristine, int displaced) => new[]
        {
            new NativeSiteSpan(rva, displaced, NativeSiteMode.Exclusive, Sha256(pristine, displaced)),
            new NativeSiteSpan(rva + displaced, pristine.Length - displaced, NativeSiteMode.Observe)
        };

        // Production binds to module + RVA after the hash check; tests bind to private code.
        // suspendThreads=false is only for private test code no other thread can execute.
        internal void Bind(ulong target, NativeOwnershipRegistry registry, ManualLogSource logger, bool suspend = true)
        {
            lock (sync)
            {
                if (state != NativeBrokerState.Pending) return;
                address = target; ownership = registry; log = logger; suspendThreads = suspend;
                Volatile.Write(ref entry, Marshal.GetDelegateForFunctionPointer<TNative>(new IntPtr(unchecked((long)target))));
                state = NativeBrokerState.Dormant;
                diagnostic = "Bound; the entry is patched at the first registration.";
            }
        }

        internal void MarkUnavailable(string reason)
        {
            lock (sync)
            {
                if (state != NativeBrokerState.Pending) return;
                state = NativeBrokerState.Unavailable; diagnostic = reason;
            }
            Log(LogLevel.Error, "NATIVE_BROKER_UNAVAILABLE: " + Name + ": " + reason + "; consumers keep their own path.");
        }

        // Native guard byte: nonzero lets the bridge enter managed code. Written only by the registry's
        // demand transitions; a pristine entry never reads it.
        internal void SetDemand(bool active) => Volatile.Write(ref *demand, (byte)(active ? 1 : 0));

        // Called under the registry lock. Null when the detour is published, otherwise the reason.
        internal string EnsureInstalled()
        {
            lock (sync)
            {
                if (state == NativeBrokerState.Installed) return null;
                if (state != NativeBrokerState.Dormant) return Name + " is " + state + ": " + diagnostic;
                string refusal = Install(out bool foreign);
                if (refusal == null)
                {
                    state = NativeBrokerState.Installed;
                    diagnostic = "Installed; NativeX64/Absolute, " + displaced + " displaced bytes, demand-guarded.";
                    Log(LogLevel.Info, "NATIVE_BROKER_READY: " + Name + " 0x" + Rva.ToString("X") + " installed for the process lifetime; " +
                        diagnostic + " Calls enter managed code only while a registration is active.");
                    return null;
                }
                state = foreign ? NativeBrokerState.ForeignOwner : NativeBrokerState.Unavailable;
                diagnostic = refusal;
                Log(foreign ? LogLevel.Warning : LogLevel.Error, (foreign ? "NATIVE_BROKER_REFUSED: " : "NATIVE_BROKER_UNAVAILABLE: ") +
                    Name + " 0x" + Rva.ToString("X") + ": " + refusal + "; the broker never patches over it and consumers keep their own path.");
                return Name + " is " + state + ": " + refusal;
            }
        }

        private string Install(out bool foreign)
        {
            foreign = false;
            if (!ApiSharedNativeSites.Claim(ownership, Name, Spans(), out NativeSiteRegistrationResult claim))
            {
                foreign = claim.Status == NativeSiteRegistrationStatus.Conflict || claim.Status == NativeSiteRegistrationStatus.UnexpectedBytes;
                return "Native site registry refused the entry: " + claim.Reason;
            }
            // The registry verifies expected bytes only once bound; this check always runs.
            for (int index = 0; index < pristine.Length; index++)
            {
                byte live = *(byte*)(address + (ulong)index);
                if (live != pristine[index])
                {
                    ApiSharedNativeSites.Release(ownership, Name);
                    foreign = true;
                    return "Live entry byte +" + index + " is 0x" + live.ToString("X2") + ", expected 0x" + pristine[index].ToString("X2") +
                        " (another detour or patch owns the entry).";
                }
            }
            try
            {
                bridge = new NativeBrokerBridge(thunk, new IntPtr(demand));
                var request = new DetourRequest<TNative> { Name = "APIShared." + Name, TargetAddress = address, Callback = thunk, IntermediaryFactory = bridge };
                hook = (NativeDetour<TNative>)Backend(scanLimit, suspendThreads).CreateDetour(in request);
                Validate();
                publishTrampoline(hook.Original);
                Volatile.Write(ref Trampoline, hook.Original);
                published = true;
                hook.Enable();
            }
            catch (Exception ex)
            {
                if (published) return "Published detour failed its contract and stays dormant: " + ex.Message;
                try { hook?.Dispose(); } catch { }
                hook = null;
                ApiSharedNativeSites.Release(ownership, Name);
                return "Installation failed before publication: " + ex.Message;
            }
            try
            {
                if (!hook.IsInstalled) throw new InvalidOperationException("The detour reported no installation.");
                Validate();
            }
            // Never unhooked once published: demand stays zero, so the guard keeps every call on the trampoline.
            catch (Exception ex) { return "Published detour failed its contract and stays dormant: " + ex.Message; }
            return null;
        }

        private static NativeDetourBackend Backend(int scanLimit, bool suspendThreads)
        {
            var options = new NativeDetourOptions
            {
                AllowedSchemes = DetourScheme.Absolute, FollowCalls = false, FollowJumps = false,
                Chaining = HookChainingPolicy.Refuse, FunctionScanLimit = scanLimit
            };
            return new NativeDetourBackend(suspendThreads ? options : options with { SuspendThreadsDuringPatch = false });
        }

        // Absolute contract: the trampoline replays the displaced prologue instruction by instruction (it
        // has no RIP operand or branch; the backend re-encodes it, so an encoding detail such as a
        // redundant REX prefix may change) and then resumes at the audited boundary. Once installed, the
        // entry holds `jmp [rip+0]; dq bridge` plus NOP padding, and the continuation is untouched.
        internal void Validate()
        {
            if (hook.Scheme != DetourScheme.Absolute || hook.DisplacedByteCount != displaced ||
                hook.TargetAddress != address || hook.PointerSlot != IntPtr.Zero ||
                hook.HookEntryPointAddress == IntPtr.Zero || hook.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("NativeX64 Absolute contract mismatch at " + Name + ".");
            var expected = Decoder.Create(64, new ByteArrayCodeReader(pristine, 0, displaced));
            expected.IP = address;
            var trampolineBytes = new byte[displaced + 32];
            Marshal.Copy(hook.TrampolineAddress, trampolineBytes, 0, trampolineBytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(trampolineBytes));
            decoder.IP = unchecked((ulong)hook.TrampolineAddress.ToInt64());
            int covered = 0;
            while (covered < displaced)
            {
                expected.Decode(out Instruction original);
                decoder.Decode(out Instruction replayed);
                if (original.IsInvalid || !SameInstruction(original, replayed))
                    throw new InvalidOperationException("Trampoline prologue changed at " + Name + ": " +
                        BitConverter.ToString(trampolineBytes, 0, displaced + 14) + ".");
                covered += original.Length;
            }
            if (covered != displaced) throw new InvalidOperationException("Displaced span ends inside an instruction at " + Name + ".");
            decoder.Decode(out Instruction jump);
            ulong continuation;
            if (jump.Mnemonic != Mnemonic.Jmp) throw new InvalidOperationException("Trampoline continuation is not a jump at " + Name + ".");
            if (jump.Op0Kind == OpKind.NearBranch64) continuation = jump.NearBranchTarget;
            else if (jump.Op0Kind == OpKind.Memory && jump.IsIPRelativeMemoryOperand)
                continuation = unchecked((ulong)Marshal.ReadInt64(new IntPtr(unchecked((long)jump.IPRelativeMemoryAddress))));
            else throw new InvalidOperationException("Unsupported trampoline jump at " + Name + ".");
            if (continuation != address + (uint)displaced) throw new InvalidOperationException("Trampoline resumes outside the audited boundary at " + Name + ".");
            bridge.Validate(hook);
            for (int index = displaced; index < pristine.Length; index++)
                if (*(byte*)(address + (ulong)index) != pristine[index])
                    throw new InvalidOperationException("Continuation changed at " + Name + ".");
            if (!hook.IsInstalled) return;
            var bytes = new byte[displaced];
            Marshal.Copy(new IntPtr(unchecked((long)address)), bytes, 0, displaced);
            if (bytes[0] != 0xFF || bytes[1] != 0x25 || BitConverter.ToInt32(bytes, 2) != 0 ||
                BitConverter.ToInt64(bytes, 6) != hook.HookEntryPointAddress.ToInt64())
                throw new InvalidOperationException("Published Absolute patch mismatch at " + Name + ".");
            for (int index = 14; index < displaced; index++)
                if (bytes[index] != 0x90) throw new InvalidOperationException("Published Absolute padding mismatch at " + Name + ".");
        }

        // Same operation and operands. Branches and RIP-relative operands never match: the audited
        // prologues have none, and a relocated one would need its own contract.
        private static bool SameInstruction(in Instruction left, in Instruction right)
        {
            if (left.Code != right.Code || left.OpCount != right.OpCount || left.IsIPRelativeMemoryOperand || right.IsIPRelativeMemoryOperand)
                return false;
            for (int operand = 0; operand < left.OpCount; operand++)
            {
                OpKind kind = left.GetOpKind(operand);
                if (kind != right.GetOpKind(operand)) return false;
                switch (kind)
                {
                    case OpKind.Register:
                        if (left.GetOpRegister(operand) != right.GetOpRegister(operand)) return false;
                        break;
                    case OpKind.Memory:
                        if (left.MemoryBase != right.MemoryBase || left.MemoryIndex != right.MemoryIndex ||
                            left.MemoryIndexScale != right.MemoryIndexScale || left.MemoryDisplacement64 != right.MemoryDisplacement64 ||
                            left.MemorySize != right.MemorySize || left.MemorySegment != right.MemorySegment)
                            return false;
                        break;
                    case OpKind.Immediate8: case OpKind.Immediate8_2nd: case OpKind.Immediate16: case OpKind.Immediate32:
                    case OpKind.Immediate64: case OpKind.Immediate8to16: case OpKind.Immediate8to32: case OpKind.Immediate8to64:
                    case OpKind.Immediate32to64:
                        if (left.GetImmediate(operand) != right.GetImmediate(operand)) return false;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private void Log(LogLevel level, string message)
        {
            try
            {
                if (level == LogLevel.Info) NativeApiLog.Info(log, message);
                else if (level == LogLevel.Warning) NativeApiLog.Warning(log, message);
                else NativeApiLog.Error(log, message);
            }
            catch { }
        }

        internal static string Sha256(byte[] bytes, int length)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes, 0, length)).Replace("-", string.Empty);
        }
    }
}
