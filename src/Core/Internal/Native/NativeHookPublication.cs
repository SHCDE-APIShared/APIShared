using System;
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
        // Writes the patches of a committed InstallOnCommit = false transaction in registration order.
        // If a write throws, every hook of the transaction is disabled again before the exception
        // propagates; the caller still unpublishes and disposes, as for any failed installation.
        // Installation path only; nothing here runs on a dispatch path.
        internal static void Enable(HookTransaction transaction, CommitResult result)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (transaction.Options.InstallOnCommit)
                throw new InvalidOperationException("Commit already wrote these patches; create the transaction with InstallOnCommit = false.");
            try
            {
                foreach (IHook hook in result.Installed)
                    hook.Enable();
            }
            catch
            {
                transaction.DisableAll();
                throw;
            }
        }
    }
}
