using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// Issue #28: a native call can reach a detour the moment its patch is written. The fixture backend
// runs the detour's callback synchronously inside Enable, as such a call would, and records whether
// the callback reached its original. No native memory is patched.
[TestClass]
public sealed class NativeHookPublicationTests
{
    internal delegate int UpdateFixture();
    private const ulong Target = 0x10000;
    private const int OriginalResult = 7;

    [TestMethod]
    public void PatchesWrittenByCommitRunBeforeTheOriginalIsAttached()
    {
        // The pre-#28 order: InstallOnCommit = true writes the patch, then attaches the handle.
        var backend = new LiveCallBackend();
        var handle = new DetourHandle<UpdateFixture>();
        HookTransaction transaction = Transaction(backend, installOnCommit: true);
        transaction.AddDetour(handle, HookTarget.FromAddress(Target), (UpdateFixture)(() => handle.Original()));
        transaction.Commit();
        Assert.AreEqual(1, backend.LiveCalls, "The fixture did not simulate a call at patch time.");
        Assert.IsInstanceOfType(backend.Failures.SingleOrDefault(), typeof(InvalidOperationException),
            "RedBird now attaches the original before writing the patch; NativeHookPublication may be unnecessary.");
    }

    [TestMethod]
    public void DeferredCommitPublishesTheOriginalBeforeThePatchIsWritten()
    {
        var backend = new LiveCallBackend();
        DetourHandle<UpdateFixture> published = null;
        var candidate = new DetourHandle<UpdateFixture>();
        HookTransaction transaction = Transaction(backend, installOnCommit: false);
        transaction.AddDetour(candidate, HookTarget.FromAddress(Target),
            (UpdateFixture)(() => Volatile.Read(ref published).Original()));
        CommitResult result = transaction.Commit();
        Assert.IsTrue(result.IsCompleteSuccess && candidate.Success, "Deferred commit did not attach the handle.");
        Assert.IsTrue(backend.LiveCalls == 0 && !candidate.IsInstalled, "Deferred commit wrote a patch.");

        Volatile.Write(ref published, candidate);
        NativeHookPublication.Enable(transaction, result);
        Assert.IsTrue(candidate.IsInstalled, "Enable did not write the patch.");
        Assert.AreEqual(1, backend.LiveCalls);
        Assert.AreEqual(0, backend.Failures.Count, "A call at patch time did not reach its original.");
        Assert.AreEqual(OriginalResult, backend.LastResult);
    }

    [TestMethod]
    public void AFailedPatchWriteDisablesEveryHookAndRejectsCommittedPatches()
    {
        var backend = new LiveCallBackend { FailingName = "second" };
        var first = new DetourHandle<UpdateFixture>();
        var second = new DetourHandle<UpdateFixture>();
        HookTransaction transaction = Transaction(backend, installOnCommit: false);
        transaction.AddDetour(first, HookTarget.FromAddress(Target), (UpdateFixture)(() => first.Original()), "first");
        transaction.AddDetour(second, HookTarget.FromAddress(Target + 16), (UpdateFixture)(() => second.Original()), "second");
        CommitResult result = transaction.Commit();
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeHookPublication.Enable(transaction, result));
        Assert.IsFalse(first.IsInstalled || second.IsInstalled, "A failed write left an earlier patch live.");

        HookTransaction written = Transaction(new LiveCallBackend(), installOnCommit: true);
        written.AddDetour(new DetourHandle<UpdateFixture>(), HookTarget.FromAddress(Target), (UpdateFixture)(() => 0), "written");
        CommitResult writtenResult = written.Commit();
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeHookPublication.Enable(written, writtenResult),
            "Enable accepted a transaction whose Commit already wrote the patches.");
    }

    [TestMethod]
    public void EveryDetourInstallationPublishesBeforeItsPatchesAreWritten()
    {
        // Each installation whose callbacks read a handle defers its patches (InstallOnCommit = false)
        // and writes them through NativeHookPublication.Enable after Commit. Where the callbacks read a
        // field the installation publishes, that Volatile.Write sits between Commit and Enable.
        var sites = new (string Type, string Method, bool PublishesField)[]
        {
            ("APIShared.AssassinAttackControlAPI", "EnsureInstalled", true),
            ("APIShared.AssassinPathAPI", "EnsureInstalled", true),
            ("APIShared.Economy.MarketPriceNativeRuntime", "EnsureInstalled", false),
            ("APIShared.AivBuildStepService", "TryCreate", false),
            ("APIShared.GateRouting.Native.GateRoutingNativeHooks", "InstallMovementHooks", true),
        };
        using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(NativeHookPublication).Assembly.Location))
        {
            foreach (var site in sites)
            {
                var type = assembly.MainModule.GetType(site.Type);
                Assert.IsNotNull(type, site.Type);
                var method = type.Methods.Single(m => m.HasBody && m.Name == site.Method &&
                    m.Body.Instructions.Any(i => Called(i, "HookTransaction", "Commit")));
                var code = method.Body.Instructions.ToList();
                int commit = code.FindIndex(i => Called(i, "HookTransaction", "Commit"));
                int enable = code.FindIndex(i => Called(i, "NativeHookPublication", "Enable"));
                bool deferred = code.Any(i => Called(i, "HookTransactionOptions", "set_InstallOnCommit") &&
                    i.Previous.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0);
                string name = site.Type + "." + site.Method;
                Assert.IsTrue(deferred, name + " lets Commit write its patches.");
                Assert.IsTrue(enable > commit, name + " does not write its patches through NativeHookPublication after Commit.");
                if (site.PublishesField)
                    Assert.IsTrue(code.Skip(commit).Take(enable - commit).Any(i => Called(i, "Volatile", "Write")),
                        name + " publishes its handle after the patches are written.");
            }
        }
    }

    private static bool Called(Mono.Cecil.Cil.Instruction instruction, string type, string method) =>
        (instruction.OpCode.Code == Mono.Cecil.Cil.Code.Call || instruction.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt) &&
        instruction.Operand is Mono.Cecil.MethodReference reference &&
        reference.Name == method && reference.DeclaringType.Name == type;

    private static HookTransaction Transaction(IHookBackend backend, bool installOnCommit) =>
        new HookTransaction(ScanRegion.FromMemory(new byte[64], Target, "NativeHookPublication fixture"), null,
            new HookTransactionOptions
            {
                FailureMode = TransactionFailureMode.RollbackAndThrow,
                OwnsHooks = true,
                InstallOnCommit = installOnCommit,
                Backend = backend
            });

    // Each detour's Enable invokes its callback once, as a native thread entering the freshly patched
    // function would, and records the outcome instead of letting it escape into the transaction.
    private sealed class LiveCallBackend : IHookBackend
    {
        internal readonly List<Exception> Failures = new List<Exception>();
        internal int LiveCalls, LastResult;
        internal string FailingName;
        public string Name => "NativeHookPublication fixture";

        public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate =>
            new LiveCallDetour<TFunction>(this, request.Name, request.TargetAddress, request.Callback);

        internal void Call(Delegate callback)
        {
            LiveCalls++;
            try { LastResult = (int)callback.DynamicInvoke(); }
            catch (System.Reflection.TargetInvocationException error) { Failures.Add(error.InnerException); }
        }
    }

    private sealed class LiveCallDetour<TFunction> : IDetour<TFunction> where TFunction : Delegate
    {
        private readonly LiveCallBackend backend;
        private readonly TFunction callback;

        internal LiveCallDetour(LiveCallBackend backend, string name, ulong target, TFunction callback)
        {
            this.backend = backend;
            this.callback = callback;
            Name = name;
            TargetAddress = target;
            Original = (TFunction)(Delegate)(UpdateFixture)(() => OriginalResult);
            State = HookState.Prepared;
        }

        public string Name { get; }
        public ulong TargetAddress { get; }
        public HookState State { get; private set; }
        public bool IsInstalled => State == HookState.Installed;
        public IntPtr TrampolineAddress => IntPtr.Zero;
        public TFunction Original { get; }

        public void Enable()
        {
            if (Name == backend.FailingName) throw new InvalidOperationException("Fixture patch write failed.");
            State = HookState.Installed;
            backend.Call(callback);
        }

        public void Disable()
        {
            if (State == HookState.Installed) State = HookState.Disabled;
        }

        public void Dispose() => State = HookState.Disposed;
    }
}
