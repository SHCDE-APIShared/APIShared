using APIShared;
using APIShared.Economy;
using APIShared.Goods;

namespace ThirdPartyMod
{
    // Compiled helpers only; ExamplePlugin does not activate an external producer.
    internal static class ExternalCargoExample
    {
        // Register on initialized Unity startup before the first world. This grants no
        // food/ration behavior, voyage policy or proof that a native producer completed.
        internal static bool Register(ModApiClient api, out NativeStoredGood output, out string reason) =>
            api.NativeStorage.TryRegisterGood("external-fish", NativeStoragePool.Granary, out output, out reason);

        // Add this key to the consumer's EXISTING actor/voyage save record. Framework cargo
        // owns quantity; the consumer saves custody and does not keep a second Cargo balance.
        internal sealed class ActorCargo
        {
            internal long CreationSequence;
        }

        // Caller has verified one new production result and owns the real engine monitor in
        // single-player/editor. Keep that lock through assignment to the actor's saved custody.
        internal static bool TryRecordVerifiedProduction(NativeStorageClient storage, NativeStoredGood output,
            int producingPlayer, int verifiedQuantity, ActorCargo custody, out NativeCargoSnapshot cargo,
            out GoodsTransactionStatus status, out string reason)
        {
            cargo = default; status = GoodsTransactionStatus.InvalidRequest;
            if (custody == null || custody.CreationSequence != 0)
            { reason = "This actor already names a lot; reacquire it instead of issuing another."; return false; }
            if (!storage.TryGetLastCargoSequence(out long last, out reason)) return false;
            if (last == long.MaxValue)
            { status = GoodsTransactionStatus.LimitReached; reason = "Cargo creation sequences are exhausted."; return false; }
            // Cargo creation has its own sequence stream; storage maintenance does not use it.
            if (!storage.TryCreateCargo(last + 1, output, producingPlayer, verifiedQuantity, out cargo, out status, out reason)) return false;
            custody.CreationSequence = cargo.CreationSequence;
            return true;
        }

        // After loading, discard every old opaque snapshot and recover the retained lot by key.
        // Failure never authorizes a fallback that recreates a cached integer quantity.
        internal static bool TryReacquire(NativeStorageClient storage, ActorCargo custody,
            out NativeCargoSnapshot cargo, out string reason)
        {
            cargo = default;
            if (custody == null || custody.CreationSequence <= 0)
            { reason = "The consumer actor has no saved cargo key."; return false; }
            return storage.TryGetCargo(custody.CreationSequence, out cargo, out reason);
        }

        // Caller has verified THIS carrier's custody and actual arrival at the building,
        // authorized the producing player, and still holds the engine monitor.
        internal static bool TryPrepareVerifiedArrival(NativeStorageClient storage, NativeCargoSnapshot cargo,
            int arrivedBuilding, int quantity, out DepositAttempt attempt, out string reason)
        {
            attempt = null;
            if (quantity <= 0 || quantity > cargo.Remaining)
            { reason = "Choose a positive quantity from the current cargo checkpoint."; return false; }
            if (!storage.TryObserve(arrivedBuilding, out var destination, out reason)) return false;
            if (destination.PlayerId != cargo.PlayerId || destination.Pool != cargo.Pool)
            { reason = "The destination must match this lot's producing player and pool."; return false; }
            attempt = new DepositAttempt(cargo, destination, quantity);
            return true;
        }

        internal sealed class DepositAttempt
        {
            private readonly NativeCargoSnapshot expected;
            private readonly NativeStorageSnapshot destination;
            private readonly int quantity;
            internal DepositAttempt(NativeCargoSnapshot expected, NativeStorageSnapshot destination, int quantity)
            { this.expected = expected; this.destination = destination; this.quantity = quantity; }

            // Keep THIS attempt for an in-world retry: changing either original snapshot
            // would request a different checkpoint. On success use updated.Remaining; do
            // not subtract quantity from another consumer/native cargo integer.
            internal bool TryApply(NativeStorageClient storage, out NativeCargoSnapshot updated,
                out GoodsTransactionStatus status, out string reason) =>
                storage.TryDepositCargo(expected, destination, quantity, out updated, out status, out reason);
        }

        // The consumer decides and proves loss/cancellation. Retain expected for a retry;
        // this removes only its undelivered remainder and never refunds deposited stock.
        internal static bool TryDiscardVerifiedLoss(NativeStorageClient storage, NativeCargoSnapshot expected,
            out NativeCargoSnapshot updated, out GoodsTransactionStatus status, out string reason) =>
            storage.TryDiscardCargo(expected, out updated, out status, out reason);
    }
}
