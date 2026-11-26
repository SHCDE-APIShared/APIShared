using System;
using APIShared;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class UnitIdentityLifecycleTests
    {
        [TestMethod]
        public void ReentrantReplacementDoesNotLetQueuedOldNotificationsEraseNewPreparedIdentities()
        {
            var state = new MissionLifecycleState(_ => { });
            var lifetime = new UnitIdentityLifetime();
            var registry = new UnitIdentityRegistry(slot => new UnitIdentityProbe { Alive = true,
                GlobalId = slot == 7 ? 42u : 88u, NativeType = 2 }, () => state.NativeClock?.World, () => true, 1,
                () => state.NativeClock?.World.Mission ?? 0);
            Assert.IsTrue(registry.Register("owner", "old", 2, out _, out _));
            Assert.IsTrue(registry.Register("owner", "new", 2, out _, out _));
            long replaced = 0, replacement = 0;
            MissionContext Seed() => new MissionContext(0, MissionStartKind.LoadedSave, default, null, null, false);
            state.Register("earlier-consumer", "replace", notification => {
                if (notification.Kind != MissionLifecycleKind.Initialization || notification.Phase != MissionInitializationPhase.NativeLoaded ||
                    notification.Context.SessionId != replaced) return;
                replacement = state.Begin(Seed());
                state.Checkpoint(replacement, Seed(), MissionInitializationPhase.BeforeLoad);
                // Archive Prepared is synchronous even though BeforeLoad and the old End are queued.
                registry.Clear(); lifetime.Prepared(replacement);
                registry.StageRestore(new[] { new UnitIdentityRow { Owner = "owner", Variant = "new", NativeType = 2, UnitId = 8, GlobalId = 88 } }, replacement);
                state.BeginNativeLoad(replacement); state.CompleteNativeLoad(replacement, true);
                state.Checkpoint(replacement, Seed(), MissionInitializationPhase.NativeLoaded);
                state.Ready(replacement, Seed());
            });
            state.Register("identity-service", "lifetime", notification => {
                long active = state.Context?.SessionId ?? 0;
                if (notification.Kind == MissionLifecycleKind.End)
                {
                    if (lifetime.ClearEnded(notification.Context.SessionId, active)) { registry.Clear(); lifetime.Clear(); }
                }
                else if (notification.Kind == MissionLifecycleKind.Initialization && notification.Phase == MissionInitializationPhase.BeforeLoad)
                {
                    if (lifetime.ClearBeforeLoad(notification.Context.SessionId, active, state.NativeClock?.World != null))
                    { registry.Clear(); lifetime.Clear(); }
                }
                else if ((notification.Kind == MissionLifecycleKind.Start || notification.Phase == MissionInitializationPhase.NativeLoaded) &&
                    UnitIdentityLifetime.CanApply(notification.Context.SessionId, active, state.NativeClock?.World.Mission ?? 0)) registry.ApplyPrepared();
            });
            replaced = state.Begin(Seed());
            state.Checkpoint(replaced, Seed(), MissionInitializationPhase.BeforeLoad);
            lifetime.Prepared(replaced);
            registry.StageRestore(new[] { new UnitIdentityRow { Owner = "owner", Variant = "old", NativeType = 2, UnitId = 7, GlobalId = 42 } }, replaced);
            state.BeginNativeLoad(replaced); state.CompleteNativeLoad(replaced, true);
            state.Checkpoint(replaced, Seed(), MissionInitializationPhase.NativeLoaded);
            Assert.AreNotEqual(0L, replacement);
            Assert.IsTrue(registry.TryGetLive("owner", 8, 88, out var identity));
            Assert.AreEqual("new", identity.VariantId);
            Assert.IsFalse(registry.TryGetLive("owner", 7, 42, out _));
            state.End(MissionEndReason.Unloaded);
            Assert.IsFalse(registry.TryGet("owner", 8, 88, out _));
        }
    }
}
