using System;
using System.Runtime.ExceptionServices;
using APIShared.Events;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal delegate int AllyTransferSeEntry(int recipient, eGoods good, int amount, int sender);
    internal delegate int AllyTransferSeForward(Delegate original, int recipient, eGoods good, int amount, int sender);

    // Around SE's one Original.Invoke only. Its mutable Pre/skip and legacy Post stay in SE.
    internal sealed class AllyTransfersSeBridge
    {
        private readonly AllyTransferSeEntry entry;
        private readonly AllyTransferSeForward forward;
        private readonly AllyTransferEvents.ReadBalance read;
        private readonly Func<AllyTransferRequest, int, bool> admissible;
        private readonly InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs> events;
        [ThreadStatic] private static Frame current;
        [ThreadStatic] private static int wrapperDepth;
        internal static bool HasServiceFrame => current != null;
        internal static bool IsInsideWrapper => wrapperDepth != 0;
        internal void EnterWrapper() => wrapperDepth++;
        internal void LeaveWrapper() => wrapperDepth--;

        internal AllyTransfersSeBridge(AllyTransferSeEntry entry, AllyTransferSeForward forward,
            AllyTransferEvents.ReadBalance read, Func<AllyTransferRequest, int, bool> admissible,
            InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs> events)
        { this.entry = entry; this.forward = forward; this.read = read; this.admissible = admissible; this.events = events; }

        internal AllyTransferResult Execute(AllyTransferRequest request, int amount)
        {
            if (current != null) return Result(request, amount, AllyTransferStatus.Rejected, false, null, null);
            var frame = new Frame(this, request, amount, wrapperDepth + 1);
            Frame previous = current;
            current = frame;
            try
            {
                entry(request.RecipientPlayerId, request.Good, amount, request.SenderPlayerId);
                // SE's own Pre veto bypasses Original.Invoke and its Post. Its bool/int return
                // can be supplied by an observer and is intentionally not used as an ACK.
                var result = frame.Result ?? Result(request, amount, AllyTransferStatus.Vetoed, false, 0, 0);
                return frame.NestedObserved ? new AllyTransferResult(request.SenderPlayerId, request.RecipientPlayerId,
                    request.Good, amount, result.OriginalWasCalled, AllyTransferStatus.Uncertain,
                    result.SenderDebit, result.RecipientCredit, true) : result;
            }
            catch (Exception error)
            {
                if (frame.Result?.Status != AllyTransferStatus.Faulted) AllyTransfersNative.ReportCallFailure(error);
                var observed = frame.Result;
                return new AllyTransferResult(request.SenderPlayerId, request.RecipientPlayerId, request.Good,
                    amount, observed?.OriginalWasCalled ?? false, AllyTransferStatus.Faulted,
                    observed?.SenderDebit, observed?.RecipientCredit,
                    frame.NestedObserved || observed?.HasNestedTransfer == true);
            }
            finally { current = previous; }
        }

        // The signature consumes exactly the old delegate receiver plus its four final arguments.
        internal int Invoke(Delegate original, int recipient, eGoods good, int amount, int sender)
        {
            Frame frame = current;
            Frame surrounding = frame != null && ReferenceEquals(frame.Owner, this) ? frame : null;
            if (surrounding != null && wrapperDepth == frame.RootWrapperDepth)
            {
                frame.Invocations++;
                if (frame.Invocations != 1 || recipient != frame.Request.RecipientPlayerId ||
                    sender != frame.Request.SenderPlayerId || good != frame.Request.Good || amount != frame.Amount)
                {
                    frame.Result = Result(frame.Request, frame.Amount, frame.Invocations == 1 ? AllyTransferStatus.Rejected :
                        AllyTransferStatus.Uncertain, frame.Result?.OriginalWasCalled ?? false,
                        frame.Result?.SenderDebit, frame.Result?.RecipientCredit);
                    return 0;
                }
            }
            else frame = null;
            // No closure/receipt/readback in a dormant Vanilla call. The original opaque int
            // is preserved, including values other than one or zero.
            if (frame == null && surrounding == null && !events.CanNotify) return forward(original, recipient, good, amount, sender);
            return InvokeObserved(original, recipient, good, amount, sender, frame, surrounding);
        }

        private int InvokeObserved(Delegate original, int recipient, eGoods good, int amount, int sender, Frame frame, Frame surrounding)
        {
            var call = new NativeInvocation(forward, original);
            Func<bool> admission = frame == null ? (Func<bool>)null : () => admissible(frame.Request, frame.Amount);
            AllyTransferResult result = AllyTransferEvents.DispatchObserved(events, sender, recipient, (int)good,
                amount, call.Call, read, frame != null || surrounding != null, admission);
            if (frame != null) frame.Result = result;
            else if (surrounding != null && (result == null || result.OriginalWasCalled)) surrounding.NestedObserved = true;
            // APIShared already published the partial/faulted receipt. Preserve SE's original
            // exception path: a failed native delegate must not fall through to legacy SE Post.
            call.Failure?.Throw();
            return call.ReturnValue;
        }

        private static AllyTransferResult Result(AllyTransferRequest request, int amount, AllyTransferStatus status,
            bool called, long? debit, long? credit) => new AllyTransferResult(request.SenderPlayerId,
                request.RecipientPlayerId, request.Good, amount, called, status, debit, credit, false);

        private sealed class NativeInvocation
        {
            private readonly AllyTransferSeForward forward;
            private readonly Delegate original;
            internal readonly AllyTransfersNative.TransferCall Call;
            internal int ReturnValue;
            internal ExceptionDispatchInfo Failure;
            internal NativeInvocation(AllyTransferSeForward forward, Delegate original)
            { this.forward = forward; this.original = original; Call = Invoke; }
            private void Invoke(int recipient, int good, int amount, int sender)
            {
                try { ReturnValue = forward(original, recipient, (eGoods)good, amount, sender); }
                catch (Exception error) { Failure = ExceptionDispatchInfo.Capture(error); throw; }
            }
        }
        private sealed class Frame
        {
            internal readonly AllyTransfersSeBridge Owner;
            internal readonly AllyTransferRequest Request;
            internal readonly int Amount;
            internal readonly int RootWrapperDepth;
            internal bool NestedObserved;
            internal int Invocations;
            internal AllyTransferResult Result;
            internal Frame(AllyTransfersSeBridge owner, AllyTransferRequest request, int amount, int depth)
            { Owner = owner; Request = request; Amount = amount; RootWrapperDepth = depth; }
        }
    }
}
