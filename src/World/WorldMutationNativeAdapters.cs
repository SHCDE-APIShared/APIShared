using System;
using System.Runtime.InteropServices;
using APIShared.NativeSites;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.WorldMutations
{
    internal static partial class WorldMutationRuntime
    {
        private static readonly NativeWorkSource dig = new NativeWorkSource(WorldMutationSource.MoatDig, 0x639C0, 323, 6,
            "7E25A104B7BCB3C8CE462AB396F637F052F548C8B03900C67E80DE974702445C",
            new byte[] { 0x48,0x89,0x5C,0x24,0x18,0x56 });
        private static readonly NativeWorkSource fill = new NativeWorkSource(WorldMutationSource.MoatFill, 0x69470, 237, 10,
            "F9E2EAEB9B1F1C53D33D402BF419D4459699BB2A3B3EB572F24A7404F1342AE5",
            new byte[] { 0x48,0x89,0x6C,0x24,0x10,0x48,0x89,0x74,0x24,0x18 });
        internal static NativeDetourOptions MoatOptions(int functionLength) => new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false, FunctionScanLimit = functionLength };
        internal static NativeSiteSpan[] MoatSpans(bool filling) => new[] { new NativeSiteSpan(filling ? fill.rva : dig.rva, filling ? fill.displaced : dig.displaced, NativeSiteMode.Exclusive) };
        private static void InitializeNative(CrusaderLibraryLoadContext context, string hash)
        {
            dig.Initialize(context, hash); fill.Initialize(context, hash);
        }
        private static void RecheckNative()
        {
            if (!dig.CheckIntegrity()) Registry.SetAvailable(Registry.AvailableSources & ~WorldMutationSource.MoatDig);
            if (!fill.CheckIntegrity()) Registry.SetAvailable(Registry.AvailableSources & ~WorldMutationSource.MoatFill);
        }
        private sealed class NativeWorkSource
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate long WorkCall(IntPtr manager, int taskSlot);
            private readonly WorldMutationSource source;
            internal readonly int rva, displaced;
            private readonly int size;
            private readonly string bodyHash;
            private readonly byte[] entry;
            private readonly NativeHookPublicationState publication = new NativeHookPublicationState();
            private readonly WorkCall callback;
            private DetourHandle<WorkCall> hook;
            private IntPtr module;
            private string site;
            private HookTransaction transaction;
            internal NativeWorkSource(WorldMutationSource source, int rva, int size, int displaced, string hash, byte[] entry)
            { this.source = source; this.rva = rva; this.size = size; this.displaced = displaced; bodyHash = hash; this.entry = entry; callback = OnCall; }
            internal void Initialize(CrusaderLibraryLoadContext context, string hash)
            {
                site = "WorldMutation." + source;
                HookTransaction candidate = null; bool claimed = false;
                var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
                try
                {
                    if (hash != ApiSharedRuntime.SupportedHash || rva > context.Memory.Length - size ||
                        ApiSharedRuntime.ComputeSha256(context.Memory.Slice(rva, size)) != bodyHash ||
                        !context.Memory.Slice(rva, entry.Length).SequenceEqual(entry))
                        throw new InvalidOperationException("Native moat-work function differs from the audited image.");
                    module = context.ModuleHandle;
                    // The continuation is an independently audited instruction boundary. A foreign
                    // hook beginning exactly there is allowed and will be entered by our trampoline.
                    var spans = MoatSpans(source == WorldMutationSource.MoatFill);
                    ApiSharedNativeSites.Claim(sites, site, spans, reason => new InvalidOperationException(reason)); claimed = true;
                    NativeForeignPatchGuard.RequireUnpatched(unchecked((ulong)(module + rva).ToInt64()), displaced, site, site);
                    NativeIndirectEntry.VerifyBytes(module + rva, entry);
                    candidate = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                        new HookTransactionOptions { InstallOnCommit = false, OwnsHooks = true,
                            FailureMode = TransactionFailureMode.RollbackAndThrow,
                            Backend = NativeStartupPatchWindow.Backend(new NativeDetourBackend(MoatOptions(size))) });
                    hook = new DetourHandle<WorkCall>();
                    candidate.AddDetour(hook, HookTarget.FromAddress(unchecked((ulong)(module + rva).ToInt64())), callback);
                    var result = candidate.Commit();
                    if (!result.IsCompleteSuccess || !hook.Success) throw new InvalidOperationException("Moat-work detour preparation failed.");
                    NativeIndirectEntry.Validate(hook.Hook, module + rva, entry, displaced, false);
                    transaction = candidate;
                    NativeHookPublication.Enable(candidate, result, publication, this,
                        () => NativeIndirectEntry.Validate(hook.Hook, module + rva, entry, displaced, true));
                    Available(source);
                    NativeApiLog.Info(log, "WORLD_MUTATION_NATIVE_READY: source=" + source + "; reference-rva=0x" + rva.ToString("X") +
                        "; displaced=" + displaced + "; active callbacks only on observer demand.");
                }
                catch (Exception error)
                {
                    publication.RollbackUnpublished(candidate, () => { if (claimed) ApiSharedNativeSites.Release(sites, site); });
                    try { NativeApiLog.Error(log, "WORLD_MUTATION_NATIVE_UNAVAILABLE: " + source + ": " + error); } catch { }
                }
            }
            internal bool CheckIntegrity()
            {
                if (!publication.IsReady) return false;
                try { NativeIndirectEntry.Validate(hook.Hook, module + rva, entry, displaced, true); return true; }
                catch (Exception error)
                {
                    NativeForeignPatchGuard.Recheck();
                    try { NativeApiLog.Error(log, "WORLD_MUTATION_NATIVE_CHANGED: " + source + ": " + error); } catch { }
                    return false;
                }
            }
            private long OnCall(IntPtr manager, int taskSlot)
            {
                if (!publication.IsReady || !HasDemand(WorldMutationSource.All)) return hook.Original(manager, taskSlot);
                return Observe(manager, taskSlot);
            }
            private long Observe(IntPtr manager, int taskSlot)
            {
                long token = Enter(source);
                var state = Begin(source, new[] { manager.ToInt64(), (long)taskSlot });
                bool returned = false; long result = 0; Exception failure = null;
                try { result = hook.Original(manager, taskSlot); returned = true; return result; }
                catch (Exception error) { failure = error; throw; }
                finally { Complete(state, true, returned, result, failure); Leave(token, returned); }
            }
        }
    }
}
