using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks.Transaction;

namespace APIShared
{
    // Install order for hooks whose callbacks read a handle (issue #28). With InstallOnCommit = true,
    // RedBird's commit writes every patch first and attaches the handles afterwards, and
    // DetourHandle.Original throws until that attach, so a native call landing in between finds no
    // original. Such transactions are created with InstallOnCommit = false: Commit then only prepares
    // the hooks (each detour trampoline is built during preparation) and attaches the handles. The
    // caller publishes what its callbacks read, and Enable writes the patches last.
    internal static class NativeHookPublication
    {
        internal static void Enable(HookTransaction transaction, CommitResult result) =>
            Enable(transaction, result, new NativeHookPublicationState(), transaction, null);

        // A backend may publish executable code even when Enable throws. Root the entire candidate
        // before calling it, and never infer rollback safety from IsInstalled or successful returns.
        internal static void Enable(HookTransaction transaction, CommitResult result,
            NativeHookPublicationState publication, object callbackOwner, Action validate)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (transaction.Options.InstallOnCommit)
                throw new InvalidOperationException("Commit already wrote these patches; create the transaction with InstallOnCommit = false.");
            if (publication == null) throw new ArgumentNullException(nameof(publication));
            publication.Begin(transaction, result, callbackOwner);
            try
            {
                foreach (IHook hook in result.Installed)
                    hook.Enable();
                validate?.Invoke();
                publication.Complete();
            }
            catch (Exception error)
            {
                publication.Fail(error);
                throw;
            }
        }
    }

    // Prepared -> Publishing -> Ready or Failed. Ready means the whole feature passed its
    // post-write validation, not merely that a handle reports IsInstalled. Publication roots are
    // intentionally never removed, including failed/partially installed candidates.
    internal sealed class NativeHookPublicationState
    {
        private static readonly List<NativeHookPublicationState> Roots = new List<NativeHookPublicationState>();
        private int state;
        private ExceptionDispatchInfo failure;
        private HookTransaction transaction;
        private CommitResult result;
        private object callbackOwner;

        internal bool HasStarted => Volatile.Read(ref state) != 0;
        internal bool IsReady => Volatile.Read(ref state) == 2;

        internal void ThrowIfUnavailable()
        {
            Volatile.Read(ref failure)?.Throw();
            if (HasStarted && !IsReady)
                throw new InvalidOperationException("Native hook publication is already in progress.");
        }

        internal void Begin(HookTransaction pending, CommitResult committed, object owner)
        {
            lock (Roots)
            {
                ThrowIfUnavailable();
                if (HasStarted) throw new InvalidOperationException("Native hooks have already been published.");
                transaction = pending;
                result = committed;
                callbackOwner = owner;
                Roots.Add(this);
                Volatile.Write(ref state, 1);
            }
        }

        internal void Complete() => Volatile.Write(ref state, 2);
        internal void Fail(Exception error)
        {
            Volatile.Write(ref failure, ExceptionDispatchInfo.Capture(error));
            Volatile.Write(ref state, 3);
        }

        // This is the sole cleanup gate for these candidates. It also guards caller-owned handles,
        // unmanaged data and site claims; a catch block alone proves nothing about publication.
        internal void RollbackUnpublished(HookTransaction pending, Action clearCandidate)
        {
            if (HasStarted) return;
            pending?.Dispose();
            clearCandidate?.Invoke();
        }
    }
}
