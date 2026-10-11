using System;

namespace APIShared.Workshop
{
    // The same publisher runs on Unity's dispatcher or the console's Steam callback
    // thread. Hosts configure once before publishing; the core owns all callbacks.
    internal static class WorkshopPublisherContext
    {
        internal static Action RequireSteam;
        internal static Func<Action, bool> Enqueue;
        internal static Action<string> Log;
        internal static string StateRoot;
    }
}
