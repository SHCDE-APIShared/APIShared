using CrusaderDE;
using System.Reflection;

namespace APIShared.Internal
{
    // EngineInterface.threadLock: the managed monitor around DLL_RunTick, GameAction and
    // SetEditorPlayer. Resolved on demand (initialization paths only) against the audited
    // contract "private static object threadLock"; null when the installed game differs.
    internal static class EngineMonitor
    {
        internal static FieldInfo Field
        {
            get
            {
                FieldInfo monitor = typeof(EngineInterface).GetField("threadLock", BindingFlags.NonPublic | BindingFlags.Static);
                return monitor != null && monitor.IsPrivate && monitor.FieldType == typeof(object) ? monitor : null;
            }
        }

        // The current monitor object, or null when the field is missing or not yet assigned.
        internal static object Current => Field?.GetValue(null);
    }
}
