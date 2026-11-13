using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Networking;
using APIShared.Simulation;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionRuntime
    {
        internal static string AvailabilityReason(WeaponProductionOption option)
        {
            string native = WeaponProductionNativeRuntime.AvailabilityReason(option.BuildingType);
            if (!string.IsNullOrEmpty(native)) return native;
            if (!backendReady) return "Workshop HUD, transfer or save preparation is unavailable.";
            if (!cleanupReady) return "Workshop simulation lifecycle is unavailable.";
            if (option.StoredOutput != null && !NativeStorageRuntime.IsRegistrationReady(option.StoredOutput))
                return "The exact registered Armory output is unavailable.";
            if (option.StoredOutput != null && !WeaponProductionNativeRuntime.CanDeliverStoredGoods(option.BuildingType))
                return WeaponProductionNativeRuntime.StoredDeliveryAvailabilityReason(option.BuildingType);
            if (NeedsIngredientAdapter(option) && !WeaponProductionNativeRuntime.CanOverrideIngredients(option.BuildingType))
                return "The workshop's ingredient adapter is unavailable.";
            if (NeedsTannerIngredientAdapter(option) && !WeaponProductionNativeRuntime.CanOverrideTannerIngredients)
                return "The tanner's native cow yield is modified by another plugin. Custom costs above one hide require the original cow yield.";
            return "Shared worker-production protection is unavailable.";
        }

        private static bool NeedsTannerIngredientAdapter(WeaponProductionOption option) =>
            option.BuildingType == eStructs.STRUCT_TANNERS_WORKSHOP && option.IngredientAmount > 1;

        internal static bool NeedsIngredientAdapter(WeaponProductionOption option) =>
            option.IngredientAmount > 1 || option.IngredientAmount.HasValue &&
            (option.BuildingType == eStructs.STRUCT_FLETCHERS_WORKSHOP ||
                option.BuildingType == eStructs.STRUCT_POLETURNERS_WORKSHOP);

        internal static bool TryRegister(string owner, string id, WeaponProductionOptionDefinition definition,
            out WeaponProductionRegistration registration, out string reason)
        {
            registration = null;
            reason = "";
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Workshop registration requires the initialized Unity thread."; return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid))
            { reason = "A valid consumer GUID and option ID are required."; return false; }
            if (definition == null || string.IsNullOrWhiteSpace(definition.DisplayName) ||
                WeaponProductionCatalog.FindVanilla(definition.BuildingType, definition.TemplateGood) == null ||
                !WeaponProductionCatalog.IsWeapon(definition.OutputGood) ||
                definition.BaseAmount < 0 || definition.BaseAmount > 100 || definition.WorkTicks < 1 || definition.WorkTicks > ushort.MaxValue)
            { reason = "The workshop, template, weapon, base amount or work duration is invalid."; return false; }
            int maxIngredientAmount = WeaponProductionCatalog.IngredientLimit(definition.BuildingType);
            if (definition.IngredientAmount < 1 || definition.IngredientAmount > maxIngredientAmount)
            { reason = $"The ingredient amount must be between 1 and {maxIngredientAmount} for this workshop."; return false; }

            if (definition.StoredOutput != null &&
                (!ValidateStoredOutput(owner, definition.StoredOutput, NativeStorageRuntime.IsRegistrationReady, out reason) ||
                 !NativeStorageRuntime.TryPrepareRegisteredDelivery(definition.StoredOutput, out reason))) return false;

            var key = new WeaponProductionOptionId(owner, id);
            lock (RegistrationGate)
            {
                foreach (var old in registrations)
                    if (old.Option.Id.Equals(key))
                    { reason = "This owner already registered that workshop option ID."; return false; }
                registration = new WeaponProductionRegistration(new WeaponProductionOption(key, definition, false));
                var next = new WeaponProductionRegistration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = registration;
                registrations = next;
            }
            InitializeCleanup();
            WorkerProductionAgreement.RequestRefresh();
            return true;
        }

        // The injected predicate is only the process registration lookup; no consumer callback
        // is accepted by the public API. Tests can verify owner/pool/reference binding offline.
        internal static bool ValidateStoredOutput(string owner, NativeStoredGood output,
            Func<NativeStoredGood, bool> isRegistered, out string reason)
        {
            if (output == null) { reason = "A registered output handle is required."; return false; }
            if (!StringComparer.Ordinal.Equals(owner, output.OwnerGuid))
            { reason = "The recipe and stored output must belong to the same owner."; return false; }
            if (output.Pool != NativeStoragePool.Armory)
            { reason = "Weapon-workshop outputs require an Armory storage registration."; return false; }
            if (isRegistered == null || !isRegistered(output))
            { reason = "The exact stored-output handle is not registered and ready."; return false; }
            reason = string.Empty; return true;
        }

        internal static WeaponProductionCatalog PrepareCatalog()
        {
            lock (RegistrationGate)
            {
                bool ready = backendReady && cleanupReady && WorkerProductionRuntime.BaseAvailable;
                bool tannerIngredients = ready && Array.Exists(registrations, registration => NeedsTannerIngredientAdapter(registration.Option)) &&
                    WeaponProductionNativeRuntime.CanOverrideTannerIngredients;
                // Stored-output readiness belongs to independent process-owned adapters. Recheck
                // it at preparation; the established native-only catalogue cache remains intact.
                bool hasStoredOutputs = Array.Exists(registrations, entry => entry.Option.StoredOutput != null);
                if (hasStoredOutputs || !ReferenceEquals(preparedRegistrations, registrations) || preparedBackend != ready ||
                    preparedTannerIngredients != tannerIngredients)
                {
                    prepared = WeaponProductionCatalog.Compile(registrations,
                        type => ready && WeaponProductionNativeRuntime.IsAvailable(type),
                        option => (option.StoredOutput == null ||
                            NativeStorageRuntime.IsRegistrationReady(option.StoredOutput) &&
                            WeaponProductionNativeRuntime.CanDeliverStoredGoods(option.BuildingType)) &&
                            (!NeedsIngredientAdapter(option) ||
                            WeaponProductionNativeRuntime.CanOverrideIngredients(option.BuildingType) &&
                            (tannerIngredients || !NeedsTannerIngredientAdapter(option))));
                    preparedRegistrations = registrations;
                    preparedBackend = ready;
                    preparedTannerIngredients = tannerIngredients;
                }
                return prepared;
            }
        }

        internal static IReadOnlyList<WeaponProductionOption> GetOptions(eStructs type) => Volatile.Read(ref active).For(type);

        internal static bool IsOptionAllowed(WeaponProductionOption option)
        {
            if (option == null || availability == null) return false;
            switch (option.StoredOutput == null ? option.OutputGood : option.TemplateGood)
            {
                case eGoods.STORED_CROSSBOWS: return availability[0] != 0;
                case eGoods.STORED_PIKES: return availability[1] != 0;
                case eGoods.STORED_SWORDS: return availability[2] != 0;
                case eGoods.STORED_BOWS: return availability[3] != 0;
                case eGoods.STORED_SPEARS: return availability[4] != 0;
                case eGoods.STORED_MACES: return availability[5] != 0;
                case eGoods.STORED_LEATHER_ARMOUR:
                case eGoods.STORED_METAL_ARMOUR: return true;
                default: return false;
            }
        }
    }
}
