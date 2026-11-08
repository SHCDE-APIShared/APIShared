using APIShared.Internal;
using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;

namespace LobbyModSettingsPresetTests
{
    // Stands in for the Unity frame scheduler: the earliest pending request wins and a due request
    // runs the callback once per rendered frame. Time only moves when a test advances it.
    internal sealed class ManualFlushFrames
    {
        internal TimeSpan Now;
        internal bool OnFlushThread = true;
        internal readonly Queue<Action> Posted = new Queue<Action>();
        private TimeSpan? due;

        internal static ManualFlushFrames Bind()
        {
            PersonalSettingsWriteQueue.ResetForTests();
            var frames = new ManualFlushFrames();
            PersonalSettingsWriteQueue.Bind(new PersonalSettingsWriteQueue.Binding(() => frames.Now, frames.Request,
                () => frames.OnFlushThread, action => { frames.Posted.Enqueue(action); return true; }));
            return frames;
        }

        internal bool HasDemand => due != null;

        private void Request(TimeSpan delay)
        {
            TimeSpan at = Now + delay;
            if (due == null || at < due) due = at;
        }

        internal void Advance(TimeSpan by)
        {
            Now += by;
            while (due != null && due <= Now)
            {
                due = null;
                PersonalSettingsWriteQueue.OnQuietPeriodElapsed();
            }
        }
    }

    public partial class LobbyPresetControllerTests
    {
        private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);
        private static TimeSpan Quiet => PersonalSettingsWriteQueue.QuietPeriod;

        private int StoredLocalAmount(string path = null) => MessagePackSerializer.Deserialize<int>(
            MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(File.ReadAllBytes(path ?? SettingsPath))[nameof(Settings.LocalAmount)]);

        private static int WorkingLocalAmount(LobbyPresetController controller) =>
            MessagePackSerializer.Deserialize<int>(controller.CreatePlayerMissionSnapshot()[nameof(Settings.LocalAmount)]);

        private LobbyPresetController StartSecondMod(Host host, out string settingsPath)
        {
            string assembly = Path.Combine(directory, "Second", "Second.dll");
            var controller = new LobbyPresetController(host, null, assembly, "Second", "Foreign.Author.Second", new Version(1, 0), false);
            host.Target.Changed = controller.AfterPropertyChanged;
            controller.CaptureDefaults();
            controller.Activate();
            settingsPath = new PersonalSettingsPaths(assembly, "Foreign.Author.Second").SettingsFile;
            return controller;
        }

        [TestMethod]
        public void RapidEditsWriteOnceAfterTheLastEditWithTheFinalValue()
        {
            var frames = ManualFlushFrames.Bind();
            var host = new Host();
            var controller = Start(host);
            int writes = controller.TestWriteCount;

            for (int value = 100; value <= 150; value++)
                host.Target.LocalAmount = value;

            Assert.AreEqual(writes, controller.TestWriteCount, "Single edits wrote the file before the quiet period.");
            Assert.AreEqual(150, WorkingLocalAmount(controller), "The working state lagged behind the edits.");
            Assert.AreEqual(5, StoredLocalAmount(), "The file changed before the quiet period.");

            frames.Advance(Quiet - Millisecond);
            host.Target.LocalAmount = 151;
            frames.Advance(Millisecond);
            Assert.AreEqual(writes, controller.TestWriteCount, "The write did not wait for the quiet period after the last edit.");
            frames.Advance(Quiet - Millisecond - Millisecond);
            Assert.AreEqual(writes, controller.TestWriteCount, "The write ran before the last edit's quiet period ended.");

            frames.Advance(Millisecond);
            Assert.AreEqual(writes + 1, controller.TestWriteCount, "The bundled edits were not written exactly once.");
            Assert.AreEqual(151, StoredLocalAmount(), "The file does not hold the final value.");
            Assert.IsFalse(frames.HasDemand, "The frame callback kept demand without a pending write.");
            frames.Advance(TimeSpan.FromSeconds(10));
            Assert.AreEqual(writes + 1, controller.TestWriteCount, "A clean controller wrote again.");

            var restarted = new Host();
            Start(restarted);
            Assert.AreEqual(151, restarted.Target.LocalAmount, "A restart did not load the bundled edit.");
        }

        [TestMethod]
        public void EditsWithoutAPauseAreWrittenWithinTheMaximumDelay()
        {
            var frames = ManualFlushFrames.Bind();
            var host = new Host();
            var controller = Start(host);
            int writes = controller.TestWriteCount;
            TimeSpan step = TimeSpan.FromMilliseconds(100), elapsed = TimeSpan.Zero;

            for (int value = 200; elapsed < PersonalSettingsWriteQueue.MaximumDelay; value++)
            {
                host.Target.LocalAmount = value;
                frames.Advance(step);
                elapsed += step;
            }

            Assert.AreEqual(writes + 1, controller.TestWriteCount, "A held slider postponed the write past the maximum delay.");
            Assert.AreEqual(WorkingLocalAmount(controller), StoredLocalAmount(), "The capped write did not store the latest edit.");
        }

        [TestMethod]
        public void MissionEntryWritesThePendingEditFirst()
        {
            ManualFlushFrames.Bind();
            var host = new Host();
            var controller = Start(host);
            host.Target.LocalAmount = 77;
            int writes = controller.TestWriteCount;

            host.HasMissionContext = true;
            controller.EnterMissionPreset(null, "Community mission", false);

            Assert.AreEqual(writes + 1, controller.TestWriteCount, "Entering a mission left a personal edit only in memory.");
            Assert.AreEqual(77, StoredLocalAmount());
            host.HasMissionContext = false;
            controller.ExitMissionPreset();
        }

        [TestMethod]
        public void LaunchPreparationAndExplicitSaveWritePendingEdits()
        {
            ManualFlushFrames.Bind();
            var host = new Host();
            var controller = Start(host);
            int writes = controller.TestWriteCount;

            host.Target.LocalAmount = 81;
            controller.PersistCurrentPersonalValues();
            Assert.AreEqual(writes + 1, controller.TestWriteCount, "Launch preparation did not write the pending edit.");
            Assert.AreEqual(81, StoredLocalAmount());

            host.Target.LocalAmount = 82;
            controller.SavePersonalPreset("bundle", "Bundle", string.Empty,
                new[] { new PresetSaveSelection { PropertyName = nameof(Settings.LocalAmount) } }, false);
            Assert.AreEqual(writes + 2, controller.TestWriteCount, "An explicit save did not write the pending edit.");
            Assert.AreEqual(82, StoredLocalAmount());

            controller.PersistCurrentPersonalValues();
            Assert.AreEqual(writes + 2, controller.TestWriteCount, "A flush point rewrote an unchanged file.");
        }

        [TestMethod]
        public void ExitAndMissionLifecycleFlushWriteEveryModsOwnPayload()
        {
            var frames = ManualFlushFrames.Bind();
            var firstHost = new Host();
            var first = Start(firstHost);
            var secondHost = new Host();
            var second = StartSecondMod(secondHost, out string secondPath);
            int firstWrites = first.TestWriteCount, secondWrites = second.TestWriteCount;
            firstHost.Target.LocalAmount = 91;
            secondHost.Target.LocalAmount = 92;

            // Application.quitting and every mission lifecycle notification run this on the Unity thread.
            PersonalSettingsWriteQueue.FlushAll();

            Assert.AreEqual(firstWrites + 1, first.TestWriteCount);
            Assert.AreEqual(secondWrites + 1, second.TestWriteCount);
            Assert.AreEqual(91, StoredLocalAmount());
            Assert.AreEqual(92, StoredLocalAmount(secondPath));
            frames.Advance(Quiet);
            Assert.AreEqual(firstWrites + 1, first.TestWriteCount, "The quiet period rewrote a flushed file.");

            firstHost.Target.LocalAmount = 93;
            frames.OnFlushThread = false;
            PersonalSettingsWriteQueue.FlushAll();
            Assert.AreEqual(firstWrites + 1, first.TestWriteCount, "A flush wrote off the Unity thread.");
            frames.OnFlushThread = true;
            Assert.AreEqual(1, frames.Posted.Count, "An off-thread flush was not routed to the Unity thread.");
            frames.Posted.Dequeue()();
            Assert.AreEqual(93, StoredLocalAmount());
            Assert.AreEqual(secondWrites + 1, second.TestWriteCount, "A clean mod wrote its file again.");
        }

        [TestMethod]
        public void FailedFlushKeepsTheEditForTheNextFlushPoint()
        {
            var frames = ManualFlushFrames.Bind();
            var host = new Host();
            var controller = Start(host);
            host.Target.LocalAmount = 61;
            int writes = controller.TestWriteCount;

            using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                frames.Advance(Quiet);

            Assert.AreEqual(writes, controller.TestWriteCount, "A blocked write was counted as published.");
            Assert.IsTrue(controller.HasPendingPersonalStorageWrite, "A failed write dropped the pending edit.");
            Assert.AreEqual(5, StoredLocalAmount(), "A failed write damaged the previous file.");
            Assert.IsFalse(frames.HasDemand, "A failed write kept polling instead of waiting for a flush point.");

            PersonalSettingsWriteQueue.FlushAll();
            Assert.AreEqual(writes + 1, controller.TestWriteCount);
            Assert.IsFalse(controller.HasPendingPersonalStorageWrite);
            Assert.AreEqual(61, StoredLocalAmount(), "The next flush point did not persist the edit.");
        }

        [TestMethod]
        public void FailedImmediateWriteIsRetriedAtTheNextFlushPoint()
        {
            ManualFlushFrames.Bind();
            var host = new Host();
            var controller = Start(host);
            int writes = controller.TestWriteCount;

            using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                controller.ApplyWorkingSnapshot(new Dictionary<string, byte[]>
                {
                    [nameof(Settings.LocalAmount)] = MessagePackSerializer.Serialize(44)
                });

            Assert.AreEqual(writes, controller.TestWriteCount);
            Assert.IsTrue(controller.HasPendingPersonalStorageWrite, "A failed immediate write was forgotten.");
            PersonalSettingsWriteQueue.FlushAll();
            Assert.AreEqual(44, StoredLocalAmount(), "The next flush point did not retry the failed write.");
        }

        [TestMethod]
        public void WithoutAFrameSchedulerEditsStillWriteImmediately()
        {
            PersonalSettingsWriteQueue.ResetForTests();
            var host = new Host();
            var controller = Start(host);
            int writes = controller.TestWriteCount;
            host.Target.LocalAmount = 33;
            Assert.AreEqual(writes + 1, controller.TestWriteCount);
            Assert.AreEqual(33, StoredLocalAmount());
            Assert.IsFalse(controller.HasPendingPersonalStorageWrite);
        }
    }

    public partial class PresetTests
    {
        [TestMethod]
        public void LaunchPreparationWritesThePendingEditOfEveryEndpoint()
        {
            ManualFlushFrames.Bind();
            Func<string> previousJournalDocuments = PersonalDataFile.TestDocumentsDirectory;
            PersonalDataFile.TestDocumentsDirectory = () => Path.Combine(testRoot, "JournalDocuments");
            ModSettingsApplication.ResetJournalStorageForTests(Path.Combine(testRoot, "LaunchConfig"));
            try
            {
                string assembly = Path.Combine(testRoot, "Launch", "PresetTest.dll");
                var settings = new FakeSettings();
                settings.PreparePresets(null, assembly, ModName);
                settings.LoadInitialPresetSettings();
                settings.ActivatePresets();
                ModSettingsApplication.Register(TargetGuid, settings, assembly);
                string file = new PersonalSettingsPaths(assembly, TargetGuid).SettingsFile;
                int StoredNumber() => MessagePackSerializer.Deserialize<int>(
                    MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(File.ReadAllBytes(file))[nameof(FakeSettings.Number)]);

                settings.Number = 77;
                Assert(StoredNumber() == 5, "The edit was written before the quiet period.");
                Assert(ModSettingsApplication.PrepareLaunch(), "The fixture launch was not ready.");
                Assert(StoredNumber() == 77, "Launch preparation left a personal edit only in memory.");
            }
            finally
            {
                ModSettingsApplication.ResetForTests(null);
                PersonalDataFile.TestDocumentsDirectory = previousJournalDocuments;
            }
        }
    }
}
