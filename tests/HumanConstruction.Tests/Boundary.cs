using System;
using System.Collections.Generic;

// Doubles replace SDK lifecycle/save boundaries; all transaction, payment and binary-codec logic is production code.
namespace SHCDESE.Interop
{
    public enum eGoods { STORED_NULL, STORED_GOLD, STORED_BOWS, STORED_CROSSBOWS, STORED_SPEARS, STORED_PIKES,
        STORED_MACES, STORED_SWORDS, STORED_LEATHER_ARMOUR, STORED_METAL_ARMOUR, STORED_WOOD_PLANKS, STORED_RAW_HOPS,
        STORED_STONE_BLOCKS, STORED_IRON_INGOTS, STORED_PITCH_RAW, STORED_RAW_WHEAT, STORED_FLOUR, STORED_FOOD_BREAD,
        STORED_FOOD_CHEESE, STORED_FOOD_MEAT, STORED_FOOD_FRUIT, STORED_FOOD_ALE }
}
namespace APIShared.Internal { internal static class UnityMainThreadDispatch { internal static bool IsMainThread = true; } }
namespace APIShared.Buildings
{
    public readonly struct ConstructionCostTarget : IEquatable<ConstructionCostTarget>
    {
        public ConstructionCostTarget(int key) { Key = key; }
        internal int Key { get; }
        public bool IsValid => Key > 0;
        public bool SupportsHumanExtraCosts => IsValid;
        public bool Equals(ConstructionCostTarget other) => Key == other.Key;
        public override bool Equals(object value) => value is ConstructionCostTarget other && Equals(other);
        public override int GetHashCode() => Key;
    }
}
namespace APIShared
{
    internal static class APISharedPlugin { internal const string PluginGuid = "APIShared"; }
    internal sealed class NativeCapabilityDiagnostic { internal string Reason => "fake"; }
    internal enum MissionInitializationPhase { BeforeLoad }
    internal sealed class MissionLifecycleNotification { internal bool IsReplay; internal MissionInitializationPhase Phase; }
    internal interface IMissionLifecycleCapability
    {
        bool TryRegisterObserver(string id, Action<MissionLifecycleNotification> start, Action<MissionLifecycleNotification> end,
            Action<MissionLifecycleNotification> initialize, out NativeCapabilityDiagnostic diagnostic);
    }
    internal sealed class Lifecycle : IMissionLifecycleCapability
    {
        internal Action<MissionLifecycleNotification> Start, End, Initialize;
        public bool TryRegisterObserver(string id, Action<MissionLifecycleNotification> start, Action<MissionLifecycleNotification> end,
            Action<MissionLifecycleNotification> initialize, out NativeCapabilityDiagnostic diagnostic)
        { Start = start; End = end; Initialize = initialize; diagnostic = null; return true; }
        internal bool TryGetMissionLifecycle(out IMissionLifecycleCapability result, out NativeCapabilityDiagnostic diagnostic)
        { result = this; diagnostic = null; return true; }
    }
    internal static class ApiShared { internal static readonly Lifecycle Lifecycle = new Lifecycle(); internal static Lifecycle ForMod(string _) => Lifecycle; }
}
namespace APIShared.Savegames
{
    internal enum VersionedSaveLoadStatus { NoRecord, Restored, OutOfScope, Corrupt, TooNew, Unrecognized, MigrationFailed }
    internal sealed class VersionedSaveLoad
    {
        internal VersionedSaveLoadStatus Status;
        internal IReadOnlyDictionary<string, byte[]> Entries = new Dictionary<string, byte[]>();
    }
    internal sealed class VersionedSaveWriter
    {
        internal readonly Dictionary<string, byte[]> Entries = new Dictionary<string, byte[]>();
        internal void Write(string key, byte[] data) => Entries.Add(key, data);
    }
    internal sealed class VersionedSaveDataRegistration
    {
        internal readonly Func<VersionedSaveWriter, bool> Capture;
        internal readonly Action<VersionedSaveLoad> Loaded;
        internal bool SimulationRelevant;
        internal VersionedSaveDataRegistration(string owner, string id, int version, int oldest, string[] known,
            Func<VersionedSaveWriter, bool> capture, Action<VersionedSaveLoad> loaded) { Capture = capture; Loaded = loaded; }
    }
    internal static class VersionedSaveData
    {
        internal static readonly List<VersionedSaveDataRegistration> Saves = new List<VersionedSaveDataRegistration>();
        internal static bool TryRegister(VersionedSaveDataRegistration registration, out object result, out string reason)
        { Saves.Add(registration); result = null; reason = ""; return true; }
    }
}
