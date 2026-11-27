using APIShared;
using APIShared.GameModes;
using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class SavegameRestartMetadataTests
    {
        [TestMethod]
        [DataRow(GameModeKind.CustomTrail, true)]
        [DataRow(GameModeKind.CustomTrail, false)]
        [DataRow(GameModeKind.CoopTrail, true)]
        [DataRow(GameModeKind.CoopTrail, false)]
        public void EmptyParticipantSaveRetainsRulesAcrossRestartsAndReleasesOnExit(GameModeKind kind, bool customize)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type type = typeof(SavegameModSettings);
            var previous = new Dictionary<FieldInfo, object>();
            foreach (string name in new[] { "activeCreatorRules", "activeTrailCustomizeAllowed", "creatorRulesProvider",
                "trailCustomizeProvider", "preparedApplied", "preparedTransferred" })
            {
                FieldInfo field = type.GetField(name, flags);
                previous.Add(field, field.GetValue(null));
            }
            var lease = (SavegameMissionPresetLease)type.GetField("ActivePreset", flags).GetValue(null);
            FieldInfo contextField = typeof(ModSettingsApplication).GetField("contextId", flags);
            object previousContext = contextField.GetValue(null);
            long id = 100001;
            try
            {
                Assert.AreEqual(0, ((Dictionary<string, IModSettingsPresetEndpoint>)
                    type.GetField("preparedEndpoints", flags).GetValue(null)).Count, "Fixture requires a completed load with no providers.");
                var rules = new Dictionary<string, Dictionary<string, TrailCreatorRule>>
                {
                    ["absent.provider"] = new Dictionary<string, TrailCreatorRule>
                    {
                        ["FixedOption"] = new TrailCreatorRule { Mode = 2, FixedValue = MessagePackSerializer.Serialize(37) }
                    }
                };
                type.GetField("activeCreatorRules", flags).SetValue(null, rules);
                type.GetField("activeTrailCustomizeAllowed", flags).SetValue(null, customize);
                type.GetField("creatorRulesProvider", flags).SetValue(null,
                    (Func<Dictionary<string, Dictionary<string, TrailCreatorRule>>>)(() => new Dictionary<string, Dictionary<string, TrailCreatorRule>>()));
                type.GetField("trailCustomizeProvider", flags).SetValue(null, (Func<bool>)(() => !customize));
                type.GetField("preparedApplied", flags).SetValue(null, true);
                contextField.SetValue(null, "save:metadata-fixture");
                var mode = new GameModeSnapshot(false, true, false, true, false, false, true, false, true,
                    false, false, 0, 0, 0, 0, 0, -1, -1, -1, 0, kind,
                    GameModeLaunchVariant.RestoredCustomizedSave, 0, -1, -1, -1, 0, false);
                var context = new MissionContext(id, MissionStartKind.LoadedSave, mode, "save", "save", false);
                type.GetMethod("ApplyLoadedSettings", flags).Invoke(null, new object[] { context });
                Assert.IsTrue(lease.Contains(id), "The save metadata has no owner when all providers are absent.");
                for (int restart = 0; restart < 3; restart++)
                {
                    var next = new MissionContext(id + 1, MissionStartKind.NewGame, mode, "map", "map", true).AsRestartOf(id);
                    type.GetMethod("OnMissionEnded", flags).Invoke(null, new object[] {
                        new MissionLifecycleNotification(context, MissionLifecycleKind.End, MissionInitializationPhase.ManagedReady,
                            MissionEndReason.Replaced, true, next) });
                    id++;
                    context = next;
                    var captured = (Dictionary<string, Dictionary<string, TrailCreatorRule>>)
                        type.GetMethod("CaptureCreatorRules", flags).Invoke(null, new object[] { context });
                    Assert.AreEqual(37, MessagePackSerializer.Deserialize<int>(captured["absent.provider"]["FixedOption"].FixedValue));
                    Assert.AreEqual(customize, type.GetMethod("CaptureTrailCustomizeAllowed", flags).Invoke(null, new object[] { context }));
                    Assert.IsTrue(lease.Contains(id));
                    Assert.IsFalse(lease.Contains(id - 1));
                    Assert.AreEqual("save:metadata-fixture", ModSettingsApplication.ContextId,
                        "Restart ended the inherited application context.");
                }
                foreach (var reason in new[] { MissionEndReason.Failed, MissionEndReason.SceneChanged })
                    type.GetMethod("OnMissionEnded", flags).Invoke(null, new object[] {
                        new MissionLifecycleNotification(context, MissionLifecycleKind.End, MissionInitializationPhase.ManagedReady, reason, true) });
                Assert.IsFalse(lease.Contains(id), "Ended metadata ownership leaked into the next mission.");
                Assert.AreEqual("", ModSettingsApplication.ContextId,
                    "APIShared alone did not release the save application context without providers.");
                Assert.AreEqual(0, ((Dictionary<string, Dictionary<string, TrailCreatorRule>>)
                    type.GetMethod("CaptureCreatorRules", flags).Invoke(null, new object[] { context })).Count);
                Assert.AreEqual(!customize, type.GetMethod("CaptureTrailCustomizeAllowed", flags).Invoke(null, new object[] { context }));
            }
            finally
            {
                for (long session = 100001; session <= 100004; session++) lease.ReleaseOnEnd(session, null);
                contextField.SetValue(null, previousContext);
                foreach (var item in previous) item.Key.SetValue(null, item.Value);
            }
        }

        [TestMethod]
        public void OldSaveEndDoesNotReleaseTheNextPreparedApplicationContext()
        {
            var lease = new SavegameMissionPresetLease();
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo field = typeof(ModSettingsApplication).GetField("contextId", flags);
            object previous = field.GetValue(null);
            try
            {
                field.SetValue(null, "save:first");
                lease.TrackSession(41, "save:first");
                lease.Transfer(41, 42);
                field.SetValue(null, "trail:next");
                lease.ReleaseOnEnd(42, null);
                Assert.AreEqual("trail:next", ModSettingsApplication.ContextId);
                Assert.IsFalse(lease.Contains(42));
            }
            finally { field.SetValue(null, previous); }
        }

        [TestMethod]
        public void ReloadingTheSameSaveDoesNotReleaseItsNewPreparation()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(SavegameModSettings);
            var lease = (SavegameMissionPresetLease)type.GetField("ActivePreset", flags).GetValue(null);
            FieldInfo context = typeof(ModSettingsApplication).GetField("contextId", flags);
            FieldInfo preparedContext = type.GetField("preparedApplicationContext", flags);
            object previousContext = context.GetValue(null), previousPrepared = preparedContext.GetValue(null);
            try
            {
                context.SetValue(null, "save:same-file|same-hash");
                lease.TrackSession(100041, ModSettingsApplication.ContextId);
                preparedContext.SetValue(null, ModSettingsApplication.ContextId);
                var source = new MissionContext(100041, MissionStartKind.LoadedSave, default, "save", "save", false);
                var next = new MissionContext(100042, MissionStartKind.LoadedSave, default, "save", "save", false);
                type.GetMethod("OnMissionEnded", flags).Invoke(null, new object[] {
                    new MissionLifecycleNotification(source, MissionLifecycleKind.End, MissionInitializationPhase.ManagedReady,
                        MissionEndReason.Replaced, true, next) });
                Assert.IsFalse(lease.Contains(source.SessionId));
                Assert.AreEqual("save:same-file|same-hash", ModSettingsApplication.ContextId);
            }
            finally
            {
                lease.ReleaseOnEnd(100041, null, false);
                preparedContext.SetValue(null, previousPrepared);
                context.SetValue(null, previousContext);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void EmptyFailedPreparationReleasesOnlyItsOwnApplicationContext(bool nextContextAlreadyPrepared)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type type = typeof(SavegameModSettings);
            var previous = new Dictionary<FieldInfo, object>();
            foreach (string name in new[] { "preparedApplied", "preparedTransferred", "preparedPath", "preparedRecord", "preparedApplicationContext" })
            {
                FieldInfo field = type.GetField(name, flags);
                previous.Add(field, field.GetValue(null));
            }
            FieldInfo context = typeof(ModSettingsApplication).GetField("contextId", flags);
            object previousContext = context.GetValue(null);
            try
            {
                Assert.AreEqual(0, ((Dictionary<string, IModSettingsPresetEndpoint>)type.GetField("preparedEndpoints", flags).GetValue(null)).Count);
                type.GetField("preparedTransferred", flags).SetValue(null, false);
                type.GetField("preparedApplicationContext", flags).SetValue(null, "save:failed");
                context.SetValue(null, nextContextAlreadyPrepared ? "trail:next" : "save:failed");
                SavegameModSettings.ReleaseUnstartedPreparation();
                Assert.AreEqual(nextContextAlreadyPrepared ? "trail:next" : "", ModSettingsApplication.ContextId);
                Assert.IsNull(type.GetField("preparedApplicationContext", flags).GetValue(null));
            }
            finally
            {
                context.SetValue(null, previousContext);
                foreach (var item in previous) item.Key.SetValue(null, item.Value);
            }
        }
    }
}
