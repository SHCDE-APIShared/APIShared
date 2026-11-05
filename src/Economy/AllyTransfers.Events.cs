using System;
using APIShared.Events;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Inputs of the actual native gift operation, after any Chore delay. Only the complete-operation veto and owner-private State are mutable.</summary>
    public sealed class AllyTransferPreEventArgs : InterceptionPreEventArgs
    {
        internal AllyTransferPreEventArgs(int sender, int recipient, int good, int amount)
        { SenderPlayerId = sender; RecipientPlayerId = recipient; GoodId = good; Amount = amount; }
        /// <summary>Unmodified one-based native sender argument; callers must validate before indexing.</summary>
        public int SenderPlayerId { get; }
        /// <summary>Unmodified one-based native recipient argument.</summary>
        public int RecipientPlayerId { get; }
        /// <summary>Unmodified native goods index. Native callers are not restricted to the safe service's supported list.</summary>
        public int GoodId { get; }
        /// <summary>Unmodified native signed quantity. This is a request, not a successful debit or credit.</summary>
        public int Amount { get; }
        internal override object Capture() => null;
        internal override void Restore(object snapshot) { }
    }

    /// <summary>Immutable completion of the actual gift invocation, including veto, uncertain readback and partial failure.</summary>
    public sealed class AllyTransferPostEventArgs
    {
        internal AllyTransferPostEventArgs(AllyTransferResult result, int nativeGood, object state)
        { Result = result; GoodId = nativeGood; State = state; }
        /// <summary>Completion and separately measured source/recipient net deltas.</summary>
        public AllyTransferResult Result { get; }
        /// <summary>Unmodified native goods index, including an unsupported raw value when reported by another caller.</summary>
        public int GoodId { get; }
        /// <summary>This registration's own Pre state; null if absent or its Pre failed.</summary>
        public object State { get; }
    }

    /// <summary>Single owner of the actual native ally-gift execution boundary, shared by Vanilla Chores and the immediate transfer service.</summary>
    public static class AllyTransferEvents
    {
        internal delegate bool ReadBalance(int player, eGoods good, out int amount);
        private static readonly Func<Completion, object, AllyTransferPostEventArgs> PostFactory =
            (completion, state) => new AllyTransferPostEventArgs(completion.Result, completion.NativeGood, state);
        internal static readonly InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs> Registry =
            new InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs>(AllyTransfersNative.RequireReady);
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static bool nested;
        internal static bool IsDispatching => depth != 0;

        /// <summary>Registers ordered synchronous native Pre/Post callbacks after LibraryLoaded initialization, for the process lifetime.</summary>
        /// <remarks>Ordering is order, ordinal owner GUID, ordinal registration ID. Pre may only observe, retain private State or veto the complete native operation; it must not mutate the economy or invoke another transfer. Veto is sticky; a failing callback's veto/state roll back. Post runs after a normal return, veto or caught call-chain exception and reports net readbacks rather than an assumed amount. Native recursive calls retain original execution without recursive notifications and mark the outer quantity uncertain. The publisher does not distinguish a mouse click from code using Vanilla's command path. Callbacks run on the actual caller thread with no replay/Unity dispatch; multiplayer decisions must be identical on every peer. No observer changes Vanilla behavior by default.</remarks>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<AllyTransferPreEventArgs> pre,
            Action<AllyTransferPostEventArgs> post, out string reason, int order = 0) =>
            Registry.TryRegister(ownerGuid, registrationId, pre, post, out reason, order);
        /// <summary>Number of isolated observer callback failures.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;

        internal static AllyTransferResult Dispatch(int sender, int recipient, int good, int amount,
            AllyTransfersNative.TransferCall original, ReadBalance read, bool needsResult) =>
            DispatchObserved(Registry, sender, recipient, good, amount, original, read, needsResult);

        // Both the actual detour and the safe service use this production path. The unobserved
        // Vanilla path does no reads, allocations or callbacks; a service always needs readback.
        internal static AllyTransferResult DispatchObserved(
            InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs> registry,
            int sender, int recipient, int good, int amount, AllyTransfersNative.TransferCall original,
            ReadBalance read, bool needsResult, Func<bool> admit = null)
        {
            if (depth != 0)
            {
                nested = true;
                original(recipient, good, amount, sender);
                return null;
            }
            if (!needsResult && !registry.CanNotify)
            {
                original(recipient, good, amount, sender);
                return null;
            }
            return DispatchTracked(registry, sender, recipient, good, amount, original, read, admit);
        }

        private static AllyTransferResult DispatchTracked(
            InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs> registry,
            int sender, int recipient, int good, int amount, AllyTransfersNative.TransferCall original, ReadBalance read, Func<bool> admit)
        {
            depth++;
            nested = false;
            try
            {
                var invocation = registry.Begin(new AllyTransferPreEventArgs(sender, recipient, good, amount));
                bool veto = invocation.Pre.SkipOriginalFunction;
                long? debit = null, credit = null;
                bool originalCalled = false, faulted = false, rejected = false;
                if (veto) { debit = 0; credit = 0; }
                else if (admit != null && !admit()) rejected = true;
                else
                {
                    int? beforeSender = Read(read, sender, good), beforeRecipient = Read(read, recipient, good);
                    try
                    {
                        originalCalled = true;
                        original(recipient, good, amount, sender);
                    }
                    catch (Exception error)
                    {
                        faulted = true;
                        AllyTransfersNative.ReportCallFailure(error);
                    }
                    int? afterSender = Read(read, sender, good), afterRecipient = Read(read, recipient, good);
                    if (beforeSender.HasValue && afterSender.HasValue) debit = (long)beforeSender.Value - afterSender.Value;
                    if (beforeRecipient.HasValue && afterRecipient.HasValue) credit = (long)afterRecipient.Value - beforeRecipient.Value;
                }
                bool balanced = !nested && debit.HasValue && credit.HasValue && debit.Value == credit.Value &&
                    debit.Value >= 0 && debit.Value <= Math.Max(0L, amount);
                AllyTransferStatus status = veto ? AllyTransferStatus.Vetoed : rejected ? AllyTransferStatus.Rejected : faulted ? AllyTransferStatus.Faulted :
                    balanced ? AllyTransferStatus.Completed : AllyTransferStatus.Uncertain;
                var result = new AllyTransferResult(sender, recipient, (eGoods)good, amount,
                    originalCalled, status, debit, credit, nested);
                if (invocation.HasPostObservers) invocation.Complete(new Completion(result, good), PostFactory);
                return result;
            }
            finally { depth--; nested = false; }
        }

        private static int? Read(ReadBalance read, int player, int good)
        {
            if (!AllyTransfersEngine.ValidPlayer(player) || good <= (int)eGoods.STORED_NULL || good >= (int)eGoods.Count) return null;
            // Vanilla's ally dispatcher treats refined pitch as raw pitch for both balances.
            eGoods normalized = good == (int)eGoods.STORED_PITCH_REFINED ? eGoods.STORED_PITCH_RAW : (eGoods)good;
            try { return read(player, normalized, out int value) ? (int?)value : null; }
            catch { return null; }
        }
        private readonly struct Completion
        {
            internal readonly AllyTransferResult Result;
            internal readonly int NativeGood;
            internal Completion(AllyTransferResult result, int nativeGood) { Result = result; NativeGood = nativeGood; }
        }
    }
}
