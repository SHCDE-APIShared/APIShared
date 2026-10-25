using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LobbyModSettingsPresetTests
{
    public partial class LobbyPresetControllerTests
    {
        [TestMethod]
        public void DefaultsAndWorkingSnapshotsDeferSearchUntilTheWholeApplicationCompletes()
        {
            var host = new Host();
            var controller = Start(host);
            host.Target.HostAmount = 99;
            host.Target.LocalAmount = 42;
            int refreshes = 0;
            bool allDeferred = true;
            Action refresh = () => refreshes++;
            host.Target.Changed = name => {
                controller.AfterPropertyChanged(name);
                allDeferred &= ModSettingsSearchUpdateScope.TryDefer(host.SettingsTarget, refresh);
            };
            int writes = controller.TestWriteCount;
            controller.ApplyDefaultsAsWorkingCopy();
            Assert.IsTrue(allDeferred, "Every reflected setter must stay inside the search update scope.");
            Assert.AreEqual(1, refreshes);
            Assert.AreEqual(17, host.Target.HostAmount);
            Assert.AreEqual(5, host.Target.LocalAmount);
            Assert.AreEqual(writes + 1, controller.TestWriteCount, "Search batching must not change persistence.");
            controller.ApplyWorkingSnapshot(Values(31, 32, 43));
            Assert.AreEqual(2, refreshes);
            Assert.AreEqual(31, host.Target.HostAmount);
            Assert.AreEqual(43, host.Target.LocalAmount);
        }
        public sealed class OptionalSettings
        {
            [PresetLocal] public string Name { get; set; }
            [PresetLocal] public int? Count { get; set; }
            [SyncHostOnly] public int Amount { get; set; } = 17;
        }

        [TestMethod]
        public void OmittedNullDefaultsDoNotBlockOtherSnapshotValues()
        {
            var settings = new OptionalSettings();
            var controller = Start(new Host { SettingsTargetOverride = settings });
            controller.ApplyWorkingSnapshot(new Dictionary<string, byte[]> {
                [nameof(OptionalSettings.Amount)] = MessagePackSerializer.Serialize(31)
            });
            Assert.AreEqual(31, settings.Amount);
            Assert.IsNull(settings.Name);
            Assert.IsNull(settings.Count);
            var restarted = new OptionalSettings();
            Start(new Host { SettingsTargetOverride = restarted });
            Assert.AreEqual(31, restarted.Amount);
        }

        [TestMethod]
        public void SerializedNullDefaultsRetainTheExistingOmissionSemantics()
        {
            var settings = new OptionalSettings();
            var controller = Start(new Host { SettingsTargetOverride = settings });
            controller.SetDefaultValue<string>(nameof(OptionalSettings.Name), null);
            controller.SetDefaultValue<int?>(nameof(OptionalSettings.Count), null);
            settings.Name = "Keep current optional value";
            settings.Count = 7;
            controller.ApplyDefaultsAsWorkingCopy();
            Assert.AreEqual("Keep current optional value", settings.Name);
            Assert.AreEqual(7, settings.Count);
            Assert.AreEqual(17, settings.Amount);
        }

        private static Dictionary<string, byte[]> Values(int first, int second, int local) =>
            new Dictionary<string, byte[]> {
                [nameof(Settings.HostAmount)] = MessagePackSerializer.Serialize(first),
                [nameof(Settings.SecondHostAmount)] = MessagePackSerializer.Serialize(second),
                [nameof(Settings.LocalAmount)] = MessagePackSerializer.Serialize(local)
            };

        [TestMethod]
        public void RestoreCompletesWhenFirstNotificationChangesTheLiveRole()
        {
            var host = new Host();
            var controller = Start(host);
            byte[] personal = File.ReadAllBytes(SettingsPath);
            host.HasMissionContext = host.MissionPresetEditable = true;
            controller.EnterMissionPreset(Values(91, 92, 93), "Trail", true);
            host.Publications.Clear();
            var origins = new List<LobbyModSettingsChangeOrigin>();
            host.Target.Changed = name => {
                origins.Add(GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin);
                host.IsLocalHost = false;
                controller.AfterPropertyChanged(name);
            };
            host.HasMissionContext = false;
            controller.ExitMissionPreset();
            Assert.AreEqual(17, host.Target.HostAmount);
            Assert.AreEqual(19, host.Target.SecondHostAmount);
            Assert.AreEqual(5, host.Target.LocalAmount);
            Assert.IsTrue(origins.Count >= 3 && origins.All(origin => origin == LobbyModSettingsChangeOrigin.InitialLoad));
            Assert.IsFalse(host.Publications.Any(name => name == nameof(Settings.HostAmount) || name == nameof(Settings.SecondHostAmount)));
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(SettingsPath));
        }

        [TestMethod]
        public void ClientKeepsHostValuesUntilItsLobbyHasActuallyEnded()
        {
            var host = new Host();
            var controller = Start(host);
            byte[] personal = File.ReadAllBytes(SettingsPath);
            host.IsLocalHost = host.CanRestorePersonalHostSettings = false;
            host.HasMissionContext = true;
            controller.EnterMissionPreset(Values(91, 92, 93), "Trail", false);
            host.HasMissionContext = false;
            controller.ExitMissionPreset();
            Assert.AreEqual(91, host.Target.HostAmount);
            Assert.AreEqual(92, host.Target.SecondHostAmount);
            Assert.AreEqual(5, host.Target.LocalAmount);
            controller.RestorePersonalHostIfPending();
            Assert.AreEqual(91, host.Target.HostAmount);
            host.CanRestorePersonalHostSettings = true;
            controller.RestorePersonalHostIfPending();
            Assert.AreEqual(17, host.Target.HostAmount);
            Assert.AreEqual(19, host.Target.SecondHostAmount);
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(SettingsPath));
        }

        [TestMethod]
        public void SetterFailureRetainsMissionMetadataAndCleanupCanBeRetried()
        {
            var host = new Host();
            var controller = Start(host);
            byte[] personal = File.ReadAllBytes(SettingsPath);
            host.HasMissionContext = true;
            controller.EnterMissionPreset(Values(91, 92, 93), "Authored Trail", false);
            host.Target.FailSecondHostSetter = true;
            host.Publications.Clear();
            host.HasMissionContext = false;
            Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => controller.ExitMissionPreset());
            host.HasMissionContext = true;
            Assert.AreEqual("Authored Trail", controller.GetStatusText("Based on", "modified", "Personal", "Bundled", "External"));
            Assert.AreEqual(92, MessagePackSerializer.Deserialize<int>(controller.CreateCurrentMissionSnapshot()[nameof(Settings.SecondHostAmount)]));
            Assert.AreEqual(0, host.Publications.Count);
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(SettingsPath));
            host.Target.FailSecondHostSetter = false;
            host.HasMissionContext = false;
            controller.ExitMissionPreset();
            Assert.AreEqual(19, host.Target.SecondHostAmount);
            Assert.IsFalse(controller.IsApplyingSnapshot);
        }

        [TestMethod]
        public void RejectedValueFallsBackToItsDefaultWithoutBlockingOtherSettings()
        {
            var host = new Host();
            var controller = Start(host);
            int notifications = host.Notifications;
            host.Target.RejectSecondHostValue = 77;
            controller.ApplyWorkingSnapshot(Values(31, 77, 33));
            Assert.AreEqual(31, host.Target.HostAmount);
            Assert.AreEqual(19, host.Target.SecondHostAmount, "The code default replaces the rejected value.");
            Assert.AreEqual(33, host.Target.LocalAmount, "Settings after the rejected one are still applied.");
            Assert.AreEqual(notifications + 1, host.Notifications);
            Assert.AreEqual(0, host.SelectedPreset);
            Assert.AreEqual(19, MessagePackSerializer.Deserialize<int>(Read()[nameof(Settings.SecondHostAmount)]),
                "The working copy records the value the setting actually holds.");
        }

        [TestMethod]
        public void SetterRejectingEvenItsDefaultNoLongerBlocksTheSnapshot()
        {
            var host = new Host();
            var controller = Start(host);
            host.Target.FailSecondHostSetter = true;
            controller.ApplyWorkingSnapshot(Values(31, 32, 33));
            Assert.AreEqual(31, host.Target.HostAmount);
            Assert.AreEqual(19, host.Target.SecondHostAmount);
            Assert.AreEqual(33, host.Target.LocalAmount);
            Assert.IsFalse(controller.IsApplyingSnapshot);
            Assert.AreEqual(32, MessagePackSerializer.Deserialize<int>(Read()[nameof(Settings.SecondHostAmount)]),
                "A value that could not be applied stays requested for the next load.");
        }

        [TestMethod]
        public void PermanentSetterFailureLeavesTheMissionAfterBoundedRetries()
        {
            var host = new Host();
            var controller = Start(host);
            host.HasMissionContext = true;
            controller.EnterMissionPreset(Values(91, 92, 93), "Stuck Trail", false);
            host.Target.FailSecondHostSetter = true;
            host.Publications.Clear();
            for (int attempt = 1; attempt < LobbyPresetController.MaximumRestoreAttempts; attempt++)
            {
                host.HasMissionContext = false;
                Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => controller.ExitMissionPreset());
                host.HasMissionContext = true;
                Assert.IsTrue(host.IsMissionPresetSelected, "A retryable failure keeps the mission.");
            }
            host.HasMissionContext = false;
            controller.ExitMissionPreset();
            Assert.AreEqual(0, host.SelectedPreset);
            Assert.AreEqual(17, host.Target.HostAmount);
            Assert.AreEqual(5, host.Target.LocalAmount);
            Assert.AreEqual(92, host.Target.SecondHostAmount, "The failing setting keeps its current value.");
            Assert.IsTrue(host.Publications.Count > 0);
            Assert.IsFalse(controller.IsApplyingSnapshot);
        }

        [TestMethod]
        public void SnapshotPublishesOnlyAfterAllValuesAndRestoresOuterOriginOnFailure()
        {
            var host = new Host();
            var controller = Start(host);
            host.Target.Changed = name => {
                Assert.AreEqual(LobbyModSettingsChangeOrigin.InitialLoad, GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin);
                controller.AfterPropertyChanged(name);
            };
            controller.ApplyWorkingSnapshot(Values(31, 32, 33));
            Assert.AreEqual(31, host.Target.HostAmount);
            Assert.AreEqual(32, host.Target.SecondHostAmount);
            Assert.AreEqual(33, host.Target.LocalAmount);
            SetOrigin(LobbyModSettingsChangeOrigin.IncomingNetwork);
            Assert.ThrowsExactly<InvalidOperationException>(() => LobbySettingsRestoreScope.Execute(() => {
                Assert.AreEqual(LobbyModSettingsChangeOrigin.InitialLoad, GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin);
                LobbySettingsRestoreScope.Execute(() => { });
                throw new InvalidOperationException("scope failure");
            }));
            Assert.AreEqual(LobbyModSettingsChangeOrigin.IncomingNetwork, GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin);
        }

        [TestMethod]
        public void CallbackFailureAfterSelectionCommitKeepsMissionReachableForCleanup()
        {
            var host = new Host();
            var controller = Start(host);
            byte[] personal = File.ReadAllBytes(SettingsPath);
            host.HasMissionContext = true;
            controller.EnterMissionPreset(Values(91, 92, 93), "Retry Trail", false);
            host.Publications.Clear();
            host.SnapshotApplied = () => { throw new InvalidOperationException("Callback failure"); };
            host.HasMissionContext = false;
            Assert.ThrowsExactly<InvalidOperationException>(() => controller.ExitMissionPreset());
            host.HasMissionContext = true;
            Assert.IsTrue(host.IsMissionPresetSelected);
            Assert.AreEqual(92, MessagePackSerializer.Deserialize<int>(controller.CreateCurrentMissionSnapshot()[nameof(Settings.SecondHostAmount)]));
            Assert.AreEqual(0, host.Publications.Count);
            CollectionAssert.AreEqual(personal, File.ReadAllBytes(SettingsPath));
            host.SnapshotApplied = null;
            host.HasMissionContext = false;
            controller.ExitMissionPreset();
            Assert.AreEqual(0, host.SelectedPreset);
            Assert.AreEqual(19, host.Target.SecondHostAmount);
        }

        [TestMethod]
        public void ApplyingAClientWorkingCopyCannotReplaceItsOwnedHostPreset()
        {
            var host = new Host();
            var controller = Start(host);
            host.IsLocalHost = false;
            controller.ApplyWorkingSnapshot(Values(91, 92, 43));
            Assert.AreEqual(17, host.Target.HostAmount);
            Assert.AreEqual(19, MessagePackSerializer.Deserialize<int>(Read()[nameof(Settings.SecondHostAmount)]));
            Assert.AreEqual(43, host.Target.LocalAmount);
        }
    }
}
