using System;
using System.Reflection;
using SHCDESE.Interop;

namespace APIShared.Units.AiMilitary.Native
{
    // Optional adapter to Fixes' existing 30-position owner. No reference to a foreign DLL,
    // no competing allocator hook, and never a vanilla slot calculation for slots >=10.
    internal sealed unsafe class AiExpandedDefenseStorage
    {
        private readonly MethodInfo capacity, count, supported, mobile, position;
        private readonly FieldInfo ids, globals;
        private AiExpandedDefenseStorage(Type type)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            capacity = type.GetMethod("GetCapacity", flags, null, new[] { typeof(int) }, null);
            count = type.GetMethod("GetPositionCount", flags, null, new[] { typeof(int), typeof(int), typeof(int) }, null);
            supported = type.GetMethod("IsSupportedClass", flags, null, new[] { typeof(int) }, null);
            mobile = type.GetMethod("IsMobileClass", flags, null, new[] { typeof(int) }, null);
            position = type.GetMethod("GetPositionByPopulatedOrdinal", flags, null, new[] { typeof(int), typeof(int), typeof(int), typeof(int) }, null);
            ids = type.GetField("_tribeIdsAddress", flags); globals = type.GetField("_tribeGlobalIdsAddress", flags);
        }
        internal static AiExpandedDefenseStorage Discover()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("Fixes.Detours.ExpandedGuardingSpotsDetours", false);
                if (type != null) return new AiExpandedDefenseStorage(type);
            }
            return null;
        }
        internal bool TryResolve(AiMilitaryNativeBackend backend, GamePlayerResources* resources,
            int owner, int cls, int ordinal, out AiNativeGroup group, out string reason)
        {
            group = default(AiNativeGroup); reason = string.Empty;
            if (capacity == null || count == null || supported == null || mobile == null || position == null || ids == null || globals == null ||
                capacity.ReturnType != typeof(int) || count.ReturnType != typeof(int) ||
                supported.ReturnType != typeof(bool) || mobile.ReturnType != typeof(bool) || position.ReturnType != typeof(int) ||
                ids.FieldType != typeof(IntPtr) || globals.FieldType != typeof(IntPtr))
            { reason = "Installed Fixes expanded-defense storage contract differs."; return false; }
            try
            {
                int cap = (int)capacity.Invoke(null, new object[] { owner });
                if (cap < 10 || cap > 30) { reason = "Invalid Fixes defensive capacity."; return false; }
                int positions = (int)count.Invoke(null, new object[] { owner, cls, cap });
                if (cap <= 10 || positions <= 10 || !(bool)supported.Invoke(null, new object[] { cls })) return false;
                if ((bool)mobile.Invoke(null, new object[] { cls }))
                    positions = Math.Min(positions, ((InternalAIC*)backend.LordManager)[resources->r_AILordMinusOne - 1].max_mobile_groups);
                if (ordinal < 0 || ordinal >= positions || ordinal >= cap)
                { reason = "Requested expanded defensive position is absent."; return false; }
                int roleBase = *(int*)((byte*)backend.Module + 0x2CEE80 + cls * 4);
                int slot = roleBase + ordinal;
                if (ordinal < 10 && slot >= 164 && slot <= 167)
                { reason = "Defensive slot overlaps a reserved native role."; return false; }
                IntPtr idAddress = (IntPtr)ids.GetValue(null), globalAddress = (IntPtr)globals.GetValue(null);
                if (idAddress == IntPtr.Zero || globalAddress == IntPtr.Zero)
                { reason = "Fixes expanded-defense storage is not initialized."; return false; }
                // This is Fixes' published source layout: 9 players * 30 classes * 30 ordinals.
                int index = checked(owner * 900 + cls * 30 + ordinal);
                ushort* idSlots = (ushort*)idAddress; uint* globalSlots = (uint*)globalAddress;
                if (backend.TryGetGroup(idSlots[index], owner, out group) && group.GlobalId == globalSlots[index]) return true;
                if (ordinal < 10 && backend.TryGetGroup(resources->r_AITribeIdsByStorageRole[slot], owner, out group) &&
                    group.GlobalId == resources->r_AITribeGlobalIdsByStorageRole[slot])
                { idSlots[index] = (ushort)group.Id; globalSlots[index] = group.GlobalId; return true; }
                if (!backend.TryAllocateGroup(owner, false, out group, out reason)) return false;
                idSlots[index] = (ushort)group.Id; globalSlots[index] = group.GlobalId;
                if (ordinal < 10)
                {
                    resources->r_AITribeIdsByStorageRole[slot] = (ushort)group.Id;
                    resources->r_AITribeGlobalIdsByStorageRole[slot] = group.GlobalId;
                }
                return true;
            }
            catch (Exception error) { reason = "Fixes defensive storage failed: " + error.Message; return false; }
        }

        internal bool TryPosition(int owner, int cls, int ordinal, out int tile)
        {
            tile = 0;
            if (capacity == null || supported == null || position == null ||
                capacity.ReturnType != typeof(int) || supported.ReturnType != typeof(bool) || position.ReturnType != typeof(int)) return false;
            try
            {
                int cap = (int)capacity.Invoke(null, new object[] { owner });
                if (cap <= 10 || cap > 30 || !(bool)supported.Invoke(null, new object[] { cls })) return false;
                tile = (int)position.Invoke(null, new object[] { owner, cls, ordinal, cap }); return tile != 0;
            }
            catch { return false; }
        }

        internal bool TryFindOrdinal(AiNativeGroup group, int cls, out int ordinal)
        {
            ordinal = -1;
            if (ids == null || globals == null || capacity == null) return false;
            try
            {
                int cap = (int)capacity.Invoke(null, new object[] { group.Owner });
                if (cap <= 10 || cap > 30) return false;
                IntPtr a = (IntPtr)ids.GetValue(null), b = (IntPtr)globals.GetValue(null);
                if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
                int first = group.Owner * 900 + cls * 30;
                for (int i = 0; i < cap; i++)
                    if (((ushort*)a)[first + i] == group.Id && ((uint*)b)[first + i] == group.GlobalId)
                    { ordinal = i; return true; }
                return false;
            }
            catch { return false; }
        }

        internal void ClearReference(AiNativeGroup group)
        {
            if (ids == null || globals == null || ids.FieldType != typeof(IntPtr) || globals.FieldType != typeof(IntPtr)) return;
            IntPtr a = (IntPtr)ids.GetValue(null), b = (IntPtr)globals.GetValue(null);
            if (a == IntPtr.Zero || b == IntPtr.Zero) return;
            int first = group.Owner * 900;
            for (int i = first; i < first + 900; i++)
                if (((ushort*)a)[i] == group.Id && ((uint*)b)[i] == group.GlobalId)
                { ((ushort*)a)[i] = 0; ((uint*)b)[i] = 0; }
        }
    }
}
