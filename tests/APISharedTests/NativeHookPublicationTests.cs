using APIShared;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

// Issue #28: a native call can reach a detour the moment its patch is written. The fixture backend
// runs the detour's callback synchronously inside Enable, as such a call would, and records whether
// the callback reached its original. No native memory is patched.
[TestClass]
[DoNotParallelize]
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
    public void AFailedPatchWritePreservesAnAlreadyPublishedHook()
    {
        var backend = new LiveCallBackend { FailingName = "second" };
        var first = new DetourHandle<UpdateFixture>();
        var second = new DetourHandle<UpdateFixture>();
        HookTransaction transaction = Transaction(backend, installOnCommit: false);
        transaction.AddDetour(first, HookTarget.FromAddress(Target), (UpdateFixture)(() => first.Original()), "first");
        transaction.AddDetour(second, HookTarget.FromAddress(Target + 16), (UpdateFixture)(() => second.Original()), "second");
        CommitResult result = transaction.Commit();
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeHookPublication.Enable(transaction, result));
        // The first Enable has already invoked a callback. A later failure cannot
        // establish that no thread still uses that hook or its executable memory.
        Assert.AreEqual(1, backend.LiveCalls, "The failure must happen after a published callback.");
        Assert.IsTrue(first.IsInstalled, "A later publication failure disabled an already published hook.");
        Assert.IsFalse(second.IsInstalled, "The failing fixture hook must remain unpublished.");
    }

    [TestMethod]
    public void PublicationRejectsTransactionsWhoseCommitAlreadyWritesPatches()
    {
        HookTransaction written = Transaction(new LiveCallBackend(), installOnCommit: true);
        written.AddDetour(new DetourHandle<UpdateFixture>(), HookTarget.FromAddress(Target), (UpdateFixture)(() => 0), "written");
        CommitResult writtenResult = written.Commit();
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeHookPublication.Enable(written, writtenResult),
            "Enable accepted a transaction whose Commit already wrote the patches.");
    }

    [TestMethod]
    public void PreparedCandidateCanBeDisposedAndItsClaimReleased()
    {
        var backend = new LiveCallBackend();
        var handle = new DetourHandle<UpdateFixture>();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(handle, HookTarget.FromAddress(Target), (UpdateFixture)(() => handle.Original()));
        transaction.Commit();
        var publication = new NativeHookPublicationState();
        bool released = false;
        publication.RollbackUnpublished(transaction, () => released = true);
        Assert.IsFalse(publication.HasStarted);
        Assert.IsTrue(released);
        Assert.AreEqual(1, backend.Disposals);
        Assert.AreEqual(0, backend.EnableCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AThrowingFirstEnableIsNeverTreatedAsAnUnpublishedCandidate(bool throwsAfterWrite)
    {
        var backend = new LiveCallBackend { FailingName = "first", ThrowAfterWrite = throwsAfterWrite };
        var handle = new DetourHandle<UpdateFixture>();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(handle, HookTarget.FromAddress(Target), (UpdateFixture)(() => handle.Original()), "first");
        CommitResult result = transaction.Commit();
        var publication = new NativeHookPublicationState();
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeHookPublication.Enable(transaction, result, publication, handle, null));
        bool released = false;
        publication.RollbackUnpublished(transaction, () => released = true);
        Assert.IsTrue(publication.HasStarted);
        Assert.IsFalse(publication.IsReady);
        Assert.IsFalse(released);
        Assert.AreEqual(0, backend.Disables);
        Assert.AreEqual(0, backend.Disposals);
        Assert.AreEqual(throwsAfterWrite ? 1 : 0, backend.LiveCalls);
        Assert.AreEqual(throwsAfterWrite, handle.IsInstalled);
        Assert.AreEqual(OriginalResult, handle.Original());
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => publication.ThrowIfUnavailable()));
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeHookPublication.Enable(transaction, result, publication, handle, null)));
        Assert.AreEqual(1, backend.EnableCalls, "A failed publication was retried.");
    }

    [TestMethod]
    public void PostWriteValidationFailureRetainsHooksAndNeverPublishesReadiness()
    {
        var backend = new LiveCallBackend();
        var handle = new DetourHandle<UpdateFixture>();
        var publication = new NativeHookPublicationState();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(handle, HookTarget.FromAddress(Target), (UpdateFixture)(() =>
        {
            Assert.IsFalse(publication.IsReady, "Readiness preceded whole-feature validation.");
            return handle.Original();
        }));
        CommitResult result = transaction.Commit();
        var validationFailure = new InvalidOperationException("Fixture validation failed.");
        Assert.AreSame(validationFailure, Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeHookPublication.Enable(transaction, result, publication, handle, () =>
            {
                Assert.IsTrue(handle.IsInstalled);
                Assert.IsFalse(publication.IsReady);
                throw validationFailure;
            })));
        publication.RollbackUnpublished(transaction, () => Assert.Fail("A published claim was released."));
        Assert.IsTrue(handle.IsInstalled);
        Assert.IsFalse(publication.IsReady);
        Assert.AreEqual(0, backend.Failures.Count);
        Assert.AreEqual(0, backend.Disables + backend.Disposals);
        backend.Call(backend.Callbacks[0]);
        Assert.AreEqual(0, backend.Failures.Count, "A callback after validation failure lost its original.");
    }

    [TestMethod]
    public void ReadinessFollowsAllWritesAndValidation()
    {
        var backend = new LiveCallBackend();
        var handle = new DetourHandle<UpdateFixture>();
        var publication = new NativeHookPublicationState();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(handle, HookTarget.FromAddress(Target), (UpdateFixture)(() => handle.Original()));
        NativeHookPublication.Enable(transaction, transaction.Commit(), publication, handle, () =>
        {
            Assert.IsTrue(handle.IsInstalled);
            Assert.IsFalse(publication.IsReady);
        });
        Assert.IsTrue(publication.IsReady);
        publication.ThrowIfUnavailable();
    }

    [TestMethod]
    public void FailedPublicationRetainsTheTransactionAndCallbackOwnerAfterCollection()
    {
        WeakReference[] roots = FailedPublicationRoots();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsTrue(roots.All(root => root.IsAlive), "A published callback's state lost its process root.");
        var owner = (FixtureOwner)roots[0].Target;
        Assert.AreEqual(OriginalResult, owner.Invoke());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] FailedPublicationRoots()
    {
        var backend = new LiveCallBackend { FailingName = "second" };
        var owner = new FixtureOwner();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(owner.Handle, HookTarget.FromAddress(Target), (UpdateFixture)owner.Invoke, "first");
        transaction.AddDetour(new DetourHandle<UpdateFixture>(), HookTarget.FromAddress(Target + 16), (UpdateFixture)(() => 0), "second");
        var publication = new NativeHookPublicationState();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            NativeHookPublication.Enable(transaction, transaction.Commit(), publication, owner, null));
        return new[] { new WeakReference(owner), new WeakReference(transaction), new WeakReference(publication) };
    }

    private sealed class FixtureOwner
    {
        internal readonly DetourHandle<UpdateFixture> Handle = new DetourHandle<UpdateFixture>();
        internal int Invoke() => Handle.Original();
    }

    // Snapshot the real process-wide state for callback fixtures, restoring every instance field.
    // No transaction is published and no native address is accessed by these fixture transitions.
    internal sealed class PublicationFixture : IDisposable
    {
        internal readonly NativeHookPublicationState State;
        private readonly Dictionary<FieldInfo, object> saved;
        internal PublicationFixture(NativeHookPublicationState state)
        {
            State = state;
            saved = typeof(NativeHookPublicationState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .ToDictionary(field => field, field => field.GetValue(state));
        }
        public void Dispose()
        {
            foreach (var field in saved) field.Key.SetValue(State, field.Value);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedAssassinPublicationRejectsNewRegistrationsBeforeAnyNativeAccess(bool attack)
    {
        Type owner = attack ? typeof(AssassinAttackControlAPI) : typeof(AssassinPathAPI);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var fields = (attack ? new[] { "owner", "module", "supported" } : new[] { "weightedOwner", "gateOwner", "module", "supported" })
            .Select(name => owner.GetField(name, flags)).ToDictionary(field => field, field => field.GetValue(null));
        using (var fixture = new PublicationFixture((NativeHookPublicationState)owner.GetField("publication", flags).GetValue(null)))
        {
            try
            {
                foreach (var field in fields.Keys)
                    field.SetValue(null, field.FieldType == typeof(IntPtr) ? (object)IntPtr.Zero :
                        field.FieldType == typeof(bool) ? (object)false : null);
                var failure = new InvalidOperationException("Published Assassin fixture failed.");
                fixture.State.Fail(failure);
                if (attack)
                    Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                        AssassinAttackControlAPI.RegisterGuard("fixture", unit => false)));
                else
                {
                    Assert.IsFalse(AssassinPathAPI.IsInstalled);
                    Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                        AssassinPathAPI.RegisterWeightedBuilder("fixture", (context, sx, sy, tx, ty, nodes, continuation) => 0)));
                    Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                        AssassinPathAPI.SetDirectGatehouseClimbing("fixture", true)));
                }
            }
            finally { foreach (var field in fields) field.Key.SetValue(null, field.Value); }
        }
    }

    [TestMethod]
    public void FailedAivPublicationKeepsItsOwnerAcrossRepeatedCapabilityRequests()
    {
        var candidate = new AivBuildStepService(ApiSharedRuntime.SupportedHash, null);
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        var publication = (NativeHookPublicationState)typeof(AivBuildStepService).GetField("publication", instance).GetValue(candidate);
        publication.Fail(new InvalidOperationException("Published AIV fixture failed."));
        var processOwner = typeof(AivBuildStepService).GetField("publicationCandidate", BindingFlags.Static | BindingFlags.NonPublic);
        object previous = processOwner.GetValue(null);
        try
        {
            processOwner.SetValue(null, candidate);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Assert.IsFalse(AivBuildStepService.TryCreate(ApiSharedRuntime.SupportedHash, 0, ReadOnlySpan<byte>.Empty,
                    null, null, out var service, out var diagnostic));
                Assert.IsNull(service);
                StringAssert.Contains(diagnostic.Reason, "Published AIV fixture failed.");
                Assert.AreSame(candidate, processOwner.GetValue(null));
            }
        }
        finally { processOwner.SetValue(null, previous); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GateMovementCallbacksBypassTheProviderUntilReadyAndAfterFailure(bool fail)
    {
        Type type = typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks);
        // Only the managed dispatch lambdas are invoked. Constructor/image validation and native
        // pointers are deliberately absent; inject their delegate/state fields into an isolated owner.
        var owner = (APIShared.GateRouting.Native.GateRoutingNativeHooks)FormatterServices.GetUninitializedObject(type);
        var publication = new NativeHookPublicationState();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        type.GetField("movementPublication", fields).SetValue(owner, publication);
        int originals = 0, consumers = 0;
        owner.originalAlternateConsumer = (manager, tribe, variant) => originals++;
        owner.originalCandidateSearch = (manager, start, count, target, player, mode, candidateClass) => originals++;
        owner.originalAiTacticalTarget = (manager, tribe) => originals++;
        type.GetField("rootedAlternateConsumer", fields).SetValue(owner,
            (APIShared.GateRouting.Native.GateRoutingNativeHooks.BuildingConsumerDelegate)((manager, tribe, variant) => consumers++));
        type.GetField("rootedCandidateSearch", fields).SetValue(owner,
            (APIShared.GateRouting.Native.GateRoutingNativeHooks.CandidateSearchDelegate)((manager, start, count, target, player, mode, candidateClass) => consumers++));
        type.GetField("rootedAiTacticalTarget", fields).SetValue(owner,
            (APIShared.GateRouting.Native.GateRoutingNativeHooks.AiTacticalTargetDelegate)((manager, tribe) => consumers++));
        var wrappers = new List<Delegate>();
        using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(type.Assembly.Location))
        {
            foreach (string callback in new[] { "rootedAlternateConsumer", "rootedCandidateSearch", "rootedAiTacticalTarget" })
            {
                // Select by the dispatch contract, not compiler-generated lambda names.
                var method = WithNestedTypes(assembly.MainModule.GetType(type.FullName)).SelectMany(t => t.Methods).Single(m => m.HasBody &&
                    m.Body.Instructions.Any(i => Called(i, "NativeHookPublicationState", "get_IsReady")) &&
                    m.Body.Instructions.Any(i => i.Operand is Mono.Cecil.FieldReference field && field.Name == callback));
                Type declaring = type.Assembly.GetType(method.DeclaringType.FullName.Replace('/', '+'));
                object target = owner;
                if (declaring != type)
                {
                    target = FormatterServices.GetUninitializedObject(declaring);
                    foreach (var capturedOwner in declaring.GetFields(fields).Where(field => field.FieldType == type))
                        capturedOwner.SetValue(target, owner);
                }
                wrappers.Add(Delegate.CreateDelegate(type.GetField(callback, fields).FieldType, target, declaring.GetMethod(method.Name, fields)));
            }
        }
        Action call = () =>
        {
            wrappers[0].DynamicInvoke(IntPtr.Zero, 1, 0);
            wrappers[1].DynamicInvoke(IntPtr.Zero, 1, 1, 1, 1, 0, 0);
            wrappers[2].DynamicInvoke(IntPtr.Zero, 1);
        };
        call();
        Assert.AreEqual(3, originals);
        Assert.AreEqual(0, consumers);
        if (fail) publication.Fail(new InvalidOperationException("Gate fixture failed."));
        else publication.Complete();
        call();
        Assert.AreEqual(fail ? 6 : 3, originals);
        Assert.AreEqual(fail ? 0 : 3, consumers);
    }

    [TestMethod]
    public void MarketPreparationFailureDisposesOnlyAnUnpublishedCandidate()
    {
        var backend = new LiveCallBackend();
        var installation = new MarketPriceNativeRuntime.Installation();
        var buy = new DetourHandle<MarketPriceNativeRuntime.PriceCall>();
        var missingSell = new DetourHandle<MarketPriceNativeRuntime.PriceCall>();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(buy, HookTarget.FromAddress(Target), (MarketPriceNativeRuntime.PriceCall)installation.Buy, "buy");
        Assert.ThrowsExactly<InvalidOperationException>(() => installation.Install(transaction, buy, missingSell, null));
        bool released = false;
        installation.Publication.RollbackUnpublished(transaction, () => released = true);
        Assert.IsTrue(released);
        Assert.IsFalse(installation.Publication.HasStarted);
        Assert.AreEqual(0, backend.EnableCalls);
        Assert.AreEqual(1, backend.Disposals);
    }

    [TestMethod]
    public void MarketSuccessPublishesReadinessAfterBothCallbacksCanReachTheirOriginal()
    {
        var backend = new LiveCallBackend();
        var installation = new MarketPriceNativeRuntime.Installation();
        var buy = new DetourHandle<MarketPriceNativeRuntime.PriceCall>();
        var sell = new DetourHandle<MarketPriceNativeRuntime.PriceCall>();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(buy, HookTarget.FromAddress(Target), (MarketPriceNativeRuntime.PriceCall)installation.Buy, "buy");
        transaction.AddDetour(sell, HookTarget.FromAddress(Target + 16), (MarketPriceNativeRuntime.PriceCall)installation.Sell, "sell");
        installation.Install(transaction, buy, sell, () =>
        {
            Assert.AreEqual(2, backend.LiveCalls);
            Assert.IsFalse(installation.Publication.IsReady);
        });
        Assert.IsTrue(installation.Publication.IsReady);
        Assert.AreEqual(0, backend.Failures.Count);
        Assert.AreEqual(OriginalResult, backend.LastResult);
        Assert.AreEqual(0, backend.Disables + backend.Disposals);
    }

    [TestMethod]
    [DataRow("buy", false, false)]
    [DataRow("sell", false, false)]
    [DataRow("sell", true, false)]
    [DataRow(null, false, true)]
    public void MarketPriceFailurePreservesItsActualOwnerAndBothOriginals(string failingHook, bool afterWrite, bool invalidContract)
    {
        var backend = new LiveCallBackend { FailingName = failingHook, ThrowAfterWrite = afterWrite };
        var installation = new MarketPriceNativeRuntime.Installation();
        var buy = new DetourHandle<MarketPriceNativeRuntime.PriceCall>();
        var sell = new DetourHandle<MarketPriceNativeRuntime.PriceCall>();
        HookTransaction transaction = Transaction(backend, false);
        transaction.AddDetour(buy, HookTarget.FromAddress(Target), (MarketPriceNativeRuntime.PriceCall)installation.Buy, "buy");
        transaction.AddDetour(sell, HookTarget.FromAddress(Target + 16), (MarketPriceNativeRuntime.PriceCall)installation.Sell, "sell");
        var validationFailure = new InvalidOperationException("Market validation failed.");
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => installation.Install(transaction, buy, sell, () =>
        {
            Assert.IsFalse(installation.Publication.IsReady);
            if (invalidContract) throw validationFailure;
        }));
        // Mirrors EnsureInstalled's outer ownership cleanup after the real owner's failure path.
        installation.Publication.RollbackUnpublished(transaction, () => Assert.Fail("Market claim released after publication."));
        Assert.IsFalse(installation.Publication.IsReady);
        Assert.AreEqual(OriginalResult, installation.Buy(IntPtr.Zero, 1, 1, 5));
        Assert.AreEqual(OriginalResult, installation.Sell(IntPtr.Zero, 1, 1, 5));
        Assert.AreEqual(0, backend.Failures.Count);
        Assert.AreEqual(0, backend.Disables + backend.Disposals);
        if (backend.LiveCalls > 0)
        {
            backend.Call(backend.Callbacks[0]);
            Assert.AreEqual(OriginalResult, backend.LastResult);
            Assert.AreEqual(0, backend.Failures.Count, "The market callback failed after its owner reported installation failure.");
        }
        if (invalidContract) Assert.AreSame(validationFailure, failure);
        int writes = backend.EnableCalls;
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => installation.Install(transaction, buy, sell, null)));
        Assert.AreEqual(writes, backend.EnableCalls);
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
            ("APIShared.Economy.MarketPriceNativeRuntime/Installation", "Install", true),
            ("APIShared.Economy.AiMarketSellNativeRuntime", "Install", false),
            ("APIShared.AivBuildStepService", "TryCreate", false),
            ("APIShared.GateRouting.Native.GateRoutingNativeHooks", "InstallMovementHooks", true),
            ("APIShared.GateRouting.Native.GateRoutingNativeHooks", "InstallComparisonHooks", false),
            ("APIShared.GateRouting.Native.GateRoutingNativeHooks", "InstallDescent", false),
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
                if (type.DeclaringType?.FullName == "APIShared.Economy.MarketPriceNativeRuntime")
                    deferred = type.DeclaringType.Methods.Single(m => m.Name == "EnsureInstalled").Body.Instructions.Any(i =>
                        Called(i, "HookTransactionOptions", "set_InstallOnCommit") &&
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

    [TestMethod]
    public void PublicationCallersCannotDirectlyTearDownTransactions()
    {
        string[] owners =
        {
            "APIShared.Economy.MarketPriceNativeRuntime",
            "APIShared.Economy.AiMarketSellNativeRuntime",
            "APIShared.AivBuildStepService",
            "APIShared.AssassinAttackControlAPI",
            "APIShared.AssassinPathAPI",
            "APIShared.GateRouting.Native.GateRoutingNativeHooks"
        };
        using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(NativeHookPublication).Assembly.Location))
        {
            foreach (var owner in owners)
                foreach (var type in WithNestedTypes(assembly.MainModule.GetType(owner)))
                    foreach (var method in type.Methods.Where(m => m.HasBody))
                        Assert.IsFalse(method.Body.Instructions.Any(i =>
                            Called(i, "HookTransaction", "Dispose") || Called(i, "HookTransaction", "DisableAll")),
                            method.FullName + " bypasses the publication lifetime barrier.");
        }
    }

    private static IEnumerable<Mono.Cecil.TypeDefinition> WithNestedTypes(Mono.Cecil.TypeDefinition owner)
    {
        yield return owner;
        foreach (var nested in owner.NestedTypes)
            foreach (var type in WithNestedTypes(nested)) yield return type;
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
        internal readonly List<Delegate> Callbacks = new List<Delegate>();
        internal int LiveCalls, LastResult, EnableCalls, Disables, Disposals;
        internal string FailingName;
        internal bool ThrowAfterWrite;
        public string Name => "NativeHookPublication fixture";

        public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate
        {
            Callbacks.Add(request.Callback);
            return new LiveCallDetour<TFunction>(this, request.Name, request.TargetAddress, request.Callback);
        }

        internal void Call(Delegate callback)
        {
            LiveCalls++;
            try { LastResult = (int)callback.DynamicInvoke(callback is MarketPriceNativeRuntime.PriceCall ?
                new object[] { IntPtr.Zero, 1, 1, 5 } : Array.Empty<object>()); }
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
            Original = typeof(TFunction) == typeof(MarketPriceNativeRuntime.PriceCall)
                ? (TFunction)(Delegate)(MarketPriceNativeRuntime.PriceCall)((manager, player, good, amount) => OriginalResult)
                : (TFunction)(Delegate)(UpdateFixture)(() => OriginalResult);
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
            backend.EnableCalls++;
            if (Name == backend.FailingName && !backend.ThrowAfterWrite) throw new InvalidOperationException("Fixture patch write failed.");
            State = HookState.Installed;
            backend.Call(callback);
            if (Name == backend.FailingName) throw new InvalidOperationException("Fixture failure after patch publication.");
        }

        public void Disable()
        {
            backend.Disables++;
            if (State == HookState.Installed) State = HookState.Disabled;
        }

        public void Dispose() { backend.Disposals++; State = HookState.Disposed; }
    }
}
