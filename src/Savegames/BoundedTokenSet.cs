using System;
using System.Collections.Generic;

namespace APIShared
{
    // The most recent transaction tokens, oldest evicted first, so late messages of a closed
    // readiness transaction stay ignored without the set growing for the whole process.
    internal sealed class BoundedTokenSet
    {
        private const int Capacity = 64;
        private readonly HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();

        internal bool Contains(string token) => tokens.Contains(token);

        internal void Add(string token)
        {
            if (!tokens.Add(token)) return;
            order.Enqueue(token);
            if (order.Count > Capacity) tokens.Remove(order.Dequeue());
        }
    }
}
