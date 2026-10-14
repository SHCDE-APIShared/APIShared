using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;

namespace APISharedTests
{
    // Exercises the binding against the installed Script Extender assembly. The MapLoader events are
    // fed to the binding directly rather than raised on the Extender's process-wide subjects, so other
    // suites observing those subjects are unaffected.
    [TestClass, DoNotParallelize]
    public sealed class VersionedSaveDataRuntimeTests
    {
        [TestMethod]
        public void ExtenderHandlerAndLoadEventsReportEachLoadOnce()
        {
            string id = "APIShared.Tests.VersionedSave." + Guid.NewGuid().ToString("N");
            var loads = new List<VersionedSaveLoad>();
            var registration = new VersionedSaveDataRegistration("APIShared.Tests", id, 1, 1, new[] { "value" },
                writer => { writer.Write("value", new byte[] { 7 }); return true; }, loads.Add);
            Assert.IsTrue(VersionedSaveData.TryRegister(registration, out VersionedSaveDataHandle handle, out string reason), reason);
            Assert.IsFalse(ModSaveDataAPI.Instance.RegisterModDataHandler(id, _ => null, (_, __) => { }),
                "The record owns its Extender identifier.");
            Assert.IsFalse(VersionedSaveData.TryRegister(new VersionedSaveDataRegistration("other", id.ToUpperInvariant(), 1, 1,
                new[] { "value" }, _ => true, _ => { }), out _, out _));

            Handler handler = Handler.Find(id);
            byte[] saved = handler.Save(new SaveContext(true, false, "test.sav"));
            Assert.AreEqual(VersionedSaveWriteStatus.Written, handle.LastWriteStatus);
            Assert.IsNull(handler.Save(new SaveContext(true, true, "test.map")), "Map editor saves are out of scope.");

            // Script Extender 2.14.1 delivers stored data in both phases of one load.
            VersionedSaveDataRuntime.OnLoadSave(new LoadSaveGameEventArgs(EventHookPhase.Pre, "test.sav", IntPtr.Zero, false));
            handler.Load(saved, new LoadContext(true, "test.sav"));
            VersionedSaveDataRuntime.OnLoadSave(new LoadSaveGameEventArgs(EventHookPhase.Post, "test.sav", IntPtr.Zero, false));
            handler.Load(saved, new LoadContext(true, "test.sav"));
            Assert.AreEqual(1, loads.Count);
            Assert.AreEqual(VersionedSaveLoadStatus.Restored, loads[0].Status);
            Assert.AreEqual(VersionedSaveLoadSource.SaveGame, loads[0].Source);
            Assert.AreEqual("test.sav", loads[0].FilePath);
            CollectionAssert.AreEqual(new byte[] { 7 }, loads[0].Entries["value"]);

            VersionedSaveDataRuntime.OnLoadMap(new MapLoadEventArgs(EventHookPhase.Pre, 0, "mp.sav", IntPtr.Zero, "mp", 1, 0, 0, 0));
            VersionedSaveDataRuntime.OnUnloadMap(new MapUnloadEventArgs(EventHookPhase.Pre, IntPtr.Zero));
            handler.Load(saved, new LoadContext(false, "mp.sav"));
            VersionedSaveDataRuntime.OnLoadMap(new MapLoadEventArgs(EventHookPhase.Post, 0, "mp.sav", IntPtr.Zero, "mp", 1, 0, 0, 0));
            Assert.AreEqual(2, loads.Count);
            Assert.AreEqual(VersionedSaveLoadSource.MultiplayerSave, loads[1].Source);
            Assert.AreEqual(VersionedSaveLoadStatus.Restored, loads[1].Status);

            VersionedSaveDataRuntime.OnLoadSave(new LoadSaveGameEventArgs(EventHookPhase.Pre, "edit.map", IntPtr.Zero, true));
            handler.Load(saved, new LoadContext(true, "edit.map"));
            VersionedSaveDataRuntime.OnLoadSave(new LoadSaveGameEventArgs(EventHookPhase.Post, "edit.map", IntPtr.Zero, true));
            Assert.AreEqual(VersionedSaveLoadSource.EditorMap, loads[2].Source);
            Assert.AreEqual(VersionedSaveLoadStatus.OutOfScope, loads[2].Status);

            VersionedSaveDataRuntime.OnLoadMap(new MapLoadEventArgs(EventHookPhase.Pre, 0, "new.map", IntPtr.Zero, "new", 0, 0, 0, 0));
            VersionedSaveDataRuntime.OnLoadMap(new MapLoadEventArgs(EventHookPhase.Post, 0, "new.map", IntPtr.Zero, "new", 0, 0, 0, 0));
            Assert.AreEqual(VersionedSaveLoadSource.MapToPlay, loads[3].Source);
            Assert.AreEqual(VersionedSaveLoadStatus.OutOfScope, loads[3].Status);
            Assert.AreEqual(4, loads.Count);

            // A confirmed multiplayer save load (SavegameLoadCoordinator) is a save even without the native flag.
            FieldInfo loading = typeof(APIShared.SavegameLoadCoordinator).GetField("loading", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(loading);
            Assert.IsFalse(APIShared.SavegameLoadCoordinator.IsLoadingMultiplayerSave);
            loading.SetValue(null, true);
            try
            {
                VersionedSaveDataRuntime.OnLoadMap(new MapLoadEventArgs(EventHookPhase.Pre, 0, "mp.sav", IntPtr.Zero, "mp", 0, 0, 0, 0));
                handler.Load(saved, new LoadContext(false, "mp.sav"));
                VersionedSaveDataRuntime.OnLoadMap(new MapLoadEventArgs(EventHookPhase.Post, 0, "mp.sav", IntPtr.Zero, "mp", 0, 0, 0, 0));
            }
            finally { loading.SetValue(null, false); }
            Assert.AreEqual(VersionedSaveLoadSource.MultiplayerSave, loads[4].Source);
            Assert.AreEqual(VersionedSaveLoadStatus.Restored, loads[4].Status);
        }

        [TestMethod]
        public void LoadEventHandlersNeverThrowIntoTheExtender()
        {
            VersionedSaveDataRuntime.OnLoadSave(null);
            VersionedSaveDataRuntime.OnLoadMap(null);
            VersionedSaveDataRuntime.OnUnloadMap(null);
        }

        private sealed class Handler
        {
            private readonly Func<SaveContext, byte[]> save;
            private readonly Action<byte[], LoadContext> load;

            private Handler(object entry)
            {
                save = (Func<SaveContext, byte[]>)entry.GetType().GetProperty("SaveCallback").GetValue(entry);
                load = (Action<byte[], LoadContext>)entry.GetType().GetProperty("LoadCallback").GetValue(entry);
            }

            // ModSaveDataAPI keeps one private handler per identifier (API/ModSaveDataAPI.cs:72, :135-143).
            internal static Handler Find(string identifier)
            {
                FieldInfo field = typeof(ModSaveDataAPI).GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, "Script Extender handler table changed.");
                var handlers = (IDictionary)field.GetValue(ModSaveDataAPI.Instance);
                return new Handler(handlers[identifier]);
            }

            internal byte[] Save(SaveContext context) => save(context);
            internal void Load(byte[] data, LoadContext context) => load(data, context);
        }
    }
}
