using System;

namespace APIShared.Networking
{
    // Transport classification precedes all runtime checks. Control-plane callbacks
    // only copy payload/identity and post; validation is intentionally inside the post.
    internal sealed class PacketReceivePolicy<T> where T : class
    {
        private readonly Func<bool> enabled;
        private readonly Action<string, Exception> report;
        internal PacketReceivePolicy(Func<bool> enabled, Action<string, Exception> report)
        { this.enabled = enabled; this.report = report; }

        internal void Chore(T packet, ulong? steamSender, Func<T, bool> validate, Action<T> execute)
        {
            if (steamSender.HasValue) { Report("Steam delivery rejected by Chore-only receiver.", null); return; }
            try
            {
                if (packet == null || (enabled != null && !enabled()) || (validate != null && !validate(packet)))
                    return;
                execute(packet);
            }
            catch (Exception error) { Report("Chore handler or validation failed.", error); }
        }

        internal void Message(T packet, ulong? steamSender, Func<T, T> snapshot,
            Func<Action, bool> enqueue, Func<NetworkMessage<T>, bool> validate, Action<NetworkMessage<T>> receive)
        {
            if (packet == null || !steamSender.HasValue)
                return;
            try
            {
                T copy = snapshot(packet);
                if (copy == null) { Report("Message snapshot returned null.", null); return; }
                var message = new NetworkMessage<T>(copy, steamSender.Value);
                if (!enqueue(() =>
                {
                    try
                    {
                        if ((enabled == null || enabled()) && (validate == null || validate(message)))
                            receive(message);
                    }
                    catch (Exception error) { Report("Message handler or validation failed.", error); }
                }))
                    Report("Unity dispatcher is unavailable; message was dropped.", null);
            }
            catch (Exception error) { Report("Message snapshot or dispatch failed.", error); }
        }

        private void Report(string reason, Exception error)
        {
            try { report?.Invoke(reason, error); }
            catch { /* Failure reporting must never escape through a native callback. */ }
        }
    }
}
