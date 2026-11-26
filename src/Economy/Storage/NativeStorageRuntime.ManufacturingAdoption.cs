using System;
using System.Collections.Generic;
using APIShared.Savegames;

namespace APIShared.Economy
{
    internal sealed unsafe partial class NativeStorageRuntime
    {
        internal bool TryPrepareManufacturingAdoption(string owner, CriticalSaveParticipant participant,
            VersionedSaveLoad load, NativeStorageImportPlan storageImport,
            IReadOnlyList<WeaponProductionPaidManufacturing> actors,
            out WeaponProductionManufacturingAdoption adoption, out string reason)
        {
            adoption = null;
            if (storageImport == null || participant == null || !ReferenceEquals(storageImport.Participant, participant) ||
                !StringComparer.Ordinal.Equals(owner, storageImport.OwnerGuid) || load == null ||
                load.MissionSessionId != storageImport.MissionSessionId ||
                !participant.TryGetPreparedOrigin(owner, load, out var origin, out reason) || !ReferenceEquals(origin, storageImport.Origin))
            { reason = "The exact participant, current Prepared receiver and staged storage original are required."; return false; }
            if (!WeaponProductionRuntime.TryCopyManufacturingDeclarations(owner, actors, out var copied, out reason)) return false;
            var handles = System.Threading.Volatile.Read(ref registeredHandles);
            foreach (var actor in copied)
                if (!ImportGood(owner, actor.Recipe.Option.StoredOutput, handles))
                { reason = "Every recipe must use this storage owner's exact registered Armory output."; return false; }
            // Caller collection access may reenter a replacement load. Never attach its copied
            // declarations to a different original, even with identical bytes/session/owner.
            if (!participant.TryGetPreparedOrigin(owner, load, out var after, out reason) || !ReferenceEquals(after, origin)) return false;
            var candidate = new WeaponProductionManufacturingAdoption(storageImport, copied);
            lock (importSync)
            {
                if (importGeneration != origin.Generation || !participant.TryGetPreparedOrigin(owner, load, out after, out reason) ||
                    !ReferenceEquals(after, origin))
                { reason = "The exact Prepared generation changed before attachment."; return false; }
                bool found = false;
                foreach (var pending in pendingImports) if (ReferenceEquals(pending, storageImport)) { found = true; break; }
                if (!found) { reason = "This exact storage import is not pending in this load."; return false; }
                var existing = storageImport.ManufacturingAdoption;
                if (existing != null)
                {
                    if (!WeaponProductionRuntime.SameManufacturingDeclarations(existing.Values, copied))
                    { reason = "A different manufacturing adoption already belongs to this import."; return false; }
                    adoption = existing; reason = string.Empty; return true;
                }
                storageImport.ManufacturingAdoption = candidate;
                adoption = candidate;
            }
            reason = string.Empty; return true;
        }
    }
}
