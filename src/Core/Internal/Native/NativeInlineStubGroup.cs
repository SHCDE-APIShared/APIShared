using System;
using System.Runtime.CompilerServices;
using APIShared.Internal;
using APIShared.NativeSites;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;

namespace APIShared
{
    // Installation of one APIShared site group of X64InlineHook stubs. The owner runs the steps in its
    // own order, with its own checks in between: Claim -> Begin -> Add... -> Commit -> (prepared-state
    // checks) -> Publish. Commit only prepares the stubs (InstallOnCommit = false); Publish writes every
    // span through NativeHookPublication and then runs the owner's installed-state validation. On any
    // failure the owner calls RollbackUnpublished from its catch block: only a candidate whose
    // publication has not started is disposed and its claim released. Published stubs stay for the
    // process lifetime.
    internal sealed class NativeInlineStubGroup
    {
        private readonly string siteGroup;
        private readonly NativeHookPublicationState publication;
        private HookTransaction transaction;
        private bool claimed;

        internal NativeInlineStubGroup(string siteGroup, NativeHookPublicationState publication)
        {
            this.siteGroup = siteGroup;
            this.publication = publication;
        }

        internal void Claim(NativeSiteSpan[] spans)
        {
            ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership, siteGroup, spans,
                reason => new InvalidOperationException(reason));
            claimed = true;
        }

        // The transaction name (RedBird's logger category) stays the owner's member name.
        internal void Begin(ScanRegion region, [CallerMemberName] string name = "") =>
            transaction = new HookTransaction(region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                new HookTransactionOptions { InstallOnCommit = false, FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true },
                name);

        // The hook name stays the owner's handle expression, as with a direct AddInline call.
        internal void Add(HookHandle<X64InlineHook> handle, ulong entry, NativeInlineStub.Generator generate, int length,
            [CallerArgumentExpression("handle")] string name = "") =>
            transaction.AddInline(handle, HookTarget.FromAddress(entry), (a, original, back) => generate(a, original, back),
                hookSize: length, name: name);

        internal CommitResult Commit(string incomplete)
        {
            var result = transaction.Commit();
            if (!result.IsCompleteSuccess) throw new InvalidOperationException(incomplete);
            return result;
        }

        internal void Publish(CommitResult result, object callbackOwner, Action validateInstalled) =>
            NativeHookPublication.Enable(transaction, result, publication, callbackOwner, validateInstalled);

        // Disposes the unpublished transaction, releases the claim, then clears the owner's candidate.
        internal void RollbackUnpublished(Action clearCandidate) =>
            publication.RollbackUnpublished(transaction, () =>
            {
                if (claimed) ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership, siteGroup);
                clearCandidate?.Invoke();
            });
    }
}
