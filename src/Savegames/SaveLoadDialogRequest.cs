using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared.Savegames
{
    // Game-independent request execution: gates precede mutations, and even a partially
    // throwing preparation is recorded before invocation. Production and tests use this runner.
    internal sealed class SaveLoadDialogRequest
    {
        internal sealed class Entry
        {
            internal string Owner, Id;
            internal int Order;
            internal readonly SaveLoadDialogCallbacks Callbacks;
            internal Entry(SaveLoadDialogCallbacks callbacks)
            {
                Callbacks = new SaveLoadDialogCallbacks { Opened = callbacks.Opened, Validate = callbacks.Validate,
                    Prepare = callbacks.Prepare, AttemptFinished = callbacks.AttemptFinished, Closed = callbacks.Closed };
            }
        }
        private sealed class Participant
        {
            internal Entry Entry;
            internal SaveLoadDialogContext Context;
            internal bool OpenFailed;
        }
        private readonly Participant[] participants;
        private readonly Action<Exception> report;
        private bool busy, closed;
        private SaveLoadDialogOutcome? pendingOutcome;
        internal bool IsClosed => closed;
        internal SaveLoadDialogRequest(long generation, Enums.RequesterTypes type, IEnumerable<Entry> entries, Action<Exception> report)
        {
            this.report = report;
            participants = entries.Select(e => new Participant { Entry = e, Context = new SaveLoadDialogContext(generation, type) }).ToArray();
        }
        internal void Open()
        {
            foreach (var p in participants)
            {
                if (closed) break;
                try { p.Entry.Callbacks.Opened?.Invoke(p.Context); }
                catch (Exception error) { p.OpenFailed = true; Report(error); }
            }
        }
        internal bool Confirm(string fileName, FileHeader header, Func<bool> prepare, Action original, Action rollback)
        {
            if (busy || closed) return false;
            busy = true;
            var attempted = new List<Participant>();
            bool completed = false, originalFailed = false;
            try
            {
                foreach (var p in participants)
                {
                    p.Context.FileName = fileName; p.Context.Header = header;
                    if (p.OpenFailed || closed) return false;
                    if (p.Entry.Callbacks.Validate?.Invoke(p.Context) == false)
                    {
                        Report(new InvalidOperationException("Load/save gate rejected: " + p.Entry.Owner + "/" + p.Entry.Id));
                        return false;
                    }
                    if (closed) return false;
                }
                foreach (var p in participants)
                {
                    attempted.Add(p);
                    p.Entry.Callbacks.Prepare?.Invoke(p.Context);
                    if (closed) return false;
                }
                if (!prepare()) return false;
                if (closed) return false;
                // Mark before invoking: nested input cannot call the original twice.
                closed = true;
                try { original(); }
                catch { originalFailed = true; pendingOutcome = SaveLoadDialogOutcome.Failed; throw; }
                completed = true;
                pendingOutcome = SaveLoadDialogOutcome.Confirmed;
                return true;
            }
            catch (Exception error) { Report(error); if (originalFailed) throw; return false; }
            finally
            {
                for (int i = attempted.Count - 1; i >= 0; i--)
                    try { attempted[i].Entry.Callbacks.AttemptFinished?.Invoke(attempted[i].Context); }
                    catch (Exception error) { Report(error); }
                // Accepted callbacks can have transferred preparation to a pending MP start.
                // An original exception still needs rollback, even though it was accepted.
                if (!completed)
                    try { rollback(); } catch (Exception error) { Report(error); }
                busy = false;
                // A Closed observer may open another dialog. Release this attempt's temporary
                // state before observers can publish preparation for the replacement.
                if (pendingOutcome.HasValue)
                {
                    var outcome = pendingOutcome.Value;
                    pendingOutcome = null;
                    NotifyClosed(outcome);
                }
            }
        }
        internal void Close(SaveLoadDialogOutcome outcome)
        {
            if (closed) return;
            closed = true;
            if (busy) pendingOutcome = outcome;
            else NotifyClosed(outcome);
        }
        private void NotifyClosed(SaveLoadDialogOutcome outcome)
        {
            foreach (var p in participants)
                try { p.Entry.Callbacks.Closed?.Invoke(p.Context, outcome); }
                catch (Exception error) { Report(error); }
        }
        private void Report(Exception error) { try { report?.Invoke(error); } catch { } }
    }
}
