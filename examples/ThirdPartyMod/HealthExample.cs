using System;
using APIShared.Health;
using APIShared.Modifiers;

namespace ThirdPartyMod
{
    internal static class HealthExample
    {
        // Register once after LibraryLoaded. Real mods derive deterministic participation
        // and settings from their supported lifecycle/mode permissions on every peer.
        internal static void Register(Action<string> log)
        {
            if (!HealthModifiers.TryRegister(ExamplePlugin.Guid, "health/example", context =>
                ValueModifierContribution.Factor(1m), out var handle, out var reason))
            { log(reason); return; }
            // The example contributes a neutral normal-stage factor. An opt-in final
            // factor uses Factor(2m, ValueModifierStage.AfterFixedValue) instead.
            // FixedValue(60000m) replaces the baseline and all normal factors.
            // The last fixed value in order/GUID/local-ID order wins, with final
            // domain safety still enforced. Initialized maxima remain frozen.
            handle.SetEnabled(false);
        }
    }
}
