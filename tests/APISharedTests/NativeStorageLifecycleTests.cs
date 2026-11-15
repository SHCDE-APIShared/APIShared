using APIShared;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class NativeStorageLifecycleTests
    {
        [TestMethod]
        public void OnlyANewBlankEditorWorldMayInitializeWithoutAnArchiveCallback()
        {
            Assert.IsTrue(NativeStorageRuntime.CanCreateWithoutArchive(0, 1, MissionStartKind.EditorCreated));
            Assert.IsTrue(NativeStorageRuntime.CanCreateWithoutArchive(3, 4, MissionStartKind.EditorCreated));
            Assert.IsFalse(NativeStorageRuntime.CanCreateWithoutArchive(4, 4, MissionStartKind.EditorCreated));
            Assert.IsFalse(NativeStorageRuntime.CanCreateWithoutArchive(0, 0, MissionStartKind.EditorCreated));
            foreach (var kind in new[] { MissionStartKind.EditorLoaded, MissionStartKind.LoadedSave, MissionStartKind.NewGame })
                Assert.IsFalse(NativeStorageRuntime.CanCreateWithoutArchive(3, 4, kind),
                    "Missing archive preparation must not turn an existing map/save into an empty successful record.");
        }
    }
}
