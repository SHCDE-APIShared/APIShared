using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // Process-owned attempts remain identifiable after failure. Only the attempt
    // that acquired the gate may release it; successful handoff holds it until exit.
    internal sealed class WorkshopApplyCoordinator
    {
        private readonly Dictionary<string, WorkshopOperation> operations = new Dictionary<string, WorkshopOperation>();
        private int applying;

        internal async Task<WorkshopOperation> ExecuteAsync(string operationId,
            Func<WorkshopOperation, Task<WorkshopOperation>> apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            if (Interlocked.CompareExchange(ref applying, 1, 0) != 0)
                throw new InvalidOperationException("Another transaction is active.");
            bool awaitingRestart = false;
            try
            {
                var operation = new WorkshopOperation(operationId);
                operations.Add(operationId, operation);
                WorkshopOperation result = await apply(operation);
                awaitingRestart = result != null && result.State == WorkshopOperationState.AwaitingRestart;
                return result;
            }
            finally
            {
                if (!awaitingRestart) Interlocked.Exchange(ref applying, 0);
            }
        }
    }
}
