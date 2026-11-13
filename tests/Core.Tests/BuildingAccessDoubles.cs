using SHCDESE.Interop;

// Only the external SDK boundary is doubled. Tests compile the production access helpers.
namespace SHCDESE.Interop
{
    public struct GameBuilding { public int GlobalId, Owner; public SHCDESE.Interop.Enums.AliveState Life; }
}
namespace SHCDESE.API
{
    public unsafe class GameBuildingManagerAPI
    {
        public static GameBuildingManagerAPI Current;
        public static int InstanceReads;
        public static GameBuildingManagerAPI Instance { get { InstanceReads++; return Current!; } }
        public GameBuilding* Pointer;
        public int Capacity = 8, Reads, Validations, LastId;
        public bool ReturnFalse;
        public bool IsValidId(int id) { Validations++; return id > 0 && id <= Capacity; }
        public bool TryGetBuildingById(int id, out GameBuilding* building)
        { Reads++; LastId = id; building = Pointer; return !ReturnFalse; }
    }
}
