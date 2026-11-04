using BepInEx.Logging;
using SHCDESE.API.LowLevel;

namespace APIShared.Networking
{
    // Startup ordering of the authenticated Chore path, owned here instead of in the plugin:
    // issuer reader (context), then raw submission (transport); the dispatcher scope follows
    // after the player-view identity step. No native hook is installed by these steps.
    internal static class NativeChoreRuntime
    {
        internal static void InitializeSubmission(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            NativeChoreContext.Initialize(context.ModuleHandle.ToInt64(), context.Memory.Length, hash);
            NativeApiLog.Info(log, "AUTHENTICATED_CHORE_CONTEXT: hash-bound pending-slot and executing-issuer reader ready; no native hook installed.");
            if (!NativeChoreContext.IsReady) return;
            NativeChoreTransport.Initialize(context.ModuleHandle.ToInt64());
            NativeApiLog.Info(log, "AUTHENTICATED_CHORE_SEND: method=validated-managed-IL; native queue=reference-rva 0x23990; Chore only, no Steam fallback.");
        }

        internal static void InitializeDispatch(ManualLogSource log)
        {
            // The issuer reader already reported an unsupported image; one cause, one failure.
            if (!NativeChoreContext.IsReady)
            {
                NativeApiLog.Info(log, "AUTHENTICATED_CHORE_DISPATCH: skipped; native issuer reader unavailable.");
                return;
            }
            NativeChoreDispatch.Initialize();
            NativeApiLog.Info(log, "AUTHENTICATED_CHORE_DISPATCH: method=validated-managed-IL; native blob/packet/issuer scope; process lifetime.");
        }
    }
}
