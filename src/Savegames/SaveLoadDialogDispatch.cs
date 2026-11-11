using System;
using System.Collections.Generic;

namespace APIShared.Savegames
{
    // Requester callbacks may synchronously open another requester. Delay only those opens
    // until the entire outer game callback and its cleanup/bookkeeping have returned.
    internal sealed class SaveLoadDialogDispatch
    {
        private readonly Queue<Action> pending = new Queue<Action>();
        private readonly Action<Exception> report;
        private int depth;
        private bool draining;

        internal SaveLoadDialogDispatch(Action<Exception> report) { this.report = report; }

        internal void Open(Action action, Action supersede = null)
        {
            if (depth != 0 || draining)
            {
                // Logical replacement is immediate, so a gate/preparation that opens another
                // requester cannot still execute the old action. Only the UI opening waits.
                pending.Enqueue(action);
                supersede?.Invoke();
            }
            else Run(action);
        }

        internal void Run(Action action)
        {
            depth++;
            try { action(); }
            finally
            {
                depth--;
                if (depth == 0 && !draining)
                {
                    draining = true;
                    try
                    {
                        while (pending.Count != 0)
                        {
                            try { Run(pending.Dequeue()); }
                            catch (Exception error) { try { report?.Invoke(error); } catch { } }
                        }
                    }
                    finally { draining = false; }
                }
            }
        }
    }
}
