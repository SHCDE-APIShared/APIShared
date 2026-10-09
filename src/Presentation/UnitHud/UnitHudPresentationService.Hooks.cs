using APIShared.GameModes;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace APIShared
{
    internal sealed unsafe partial class UnitHudPresentationService
    {
        private delegate void SetupTroopsDelegate(HUD_Troops self);
        private delegate void TroopClickDelegate(MainViewModel self, object parameter);
        private delegate void PopulateGroupsDelegate(HUD_ControlGroups self);
        private delegate void GameActionDelegate(Enums.KeyFunctions command, int value1, int value2, int value3);
        private delegate void UpdateSpritesDelegate(MainViewModel self, int colour, bool arabic);
        private delegate void CreateTroopDelegate(MainViewModel self, object parameter);
        private delegate void EnterCreateTroopDelegate(MainViewModel self, object parameter);
        private delegate int RecruitmentGameActionDelegate(Enums.GameActionCommand command, int structureId, int state, int value2);

        internal static bool TryCreate(
            string hash,
            long moduleBase,
            ReadOnlySpan<byte> memory,
            ManualLogSource log,
            out UnitHudPresentationService service,
            out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            var installed = new List<Hook>();
            UnitHudPresentationService candidate = null;
            try
            {
                int* records = null;
                bool recordAccess = false;
                string groupReason = "Native control-group records unavailable; that surface remains Vanilla.";
                if (string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase) && moduleBase != 0)
                {
                    int match = CompiledControlGroupStoragePattern.FindUnique(memory);
                    if (match == ControlGroupStoragePatternRva)
                    {
                        int displacement = BitConverter.ToInt32(memory.Slice(match + ControlGroupStorageDisplacementOffset, sizeof(int)).ToArray(), 0);
                        int target = checked(match + ControlGroupStorageNextInstructionOffset + displacement);
                        long required = (long)target + GroupCount * GroupCapacity * GroupRecordWidth * sizeof(int);
                        if (target == ControlGroupStorageRva && required <= memory.Length)
                        {
                            records = (int*)(moduleBase + target);
                            recordAccess = true;
                            groupReason = "Control-group record layout validated.";
                        }
                    }
                }

                candidate = new UnitHudPresentationService(hash, log, records, recordAccess);
                candidate.Install(installed);
                service = candidate;
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.UnitHudPresentation,
                    NativeCapabilityState.Available,
                    hash,
                    groupReason);
                try { NativeApiLog.Debug(log, $"Unit HUD presentation installed; controlGroups={recordAccess}, build={hash}."); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                service = null;
                if (candidate != null)
                {
                    UnityEngine.Application.onBeforeRender -= candidate.OnBeforeRender;
                    candidate.mapUnloadSubscription?.Dispose();
                }
                for (int i = installed.Count - 1; i >= 0; i--)
                {
                    try { installed[i].Undo(); } catch { }
                    try { installed[i].Dispose(); } catch { }
                }
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.UnitHudPresentation,
                    NativeCapabilityState.Faulted,
                    hash,
                    ex.Message);
                NativeApiLog.Error(log, $"Unit HUD presentation failed before publication; Vanilla remains active: {ex}");
                return false;
            }
        }

        internal IUnitHudPresentationCapability Bind(string ownerGuid) => new Binding(this, ownerGuid);

        private void Install(List<Hook> installed)
        {
            setupTroopsHook = PrepareHook(RequireMethod(typeof(HUD_Troops), "SetupSelectedTroops", Type.EmptyTypes), (SetupTroopsDelegate)SetupTroopsHook, "APIShared.UnitHud.SetupSelectedTroops", installed);
            setupTroopsOriginal = setupTroopsHook.GenerateTrampoline<SetupTroopsDelegate>();
            setupTroopsHook.Apply();
            leftClickHook = PrepareHook(RequireMethod(typeof(MainViewModel), "TroopsLeftClickCommand", new[] { typeof(object) }), (TroopClickDelegate)LeftClickHook, "APIShared.UnitHud.TroopsLeftClick", installed);
            leftClickOriginal = leftClickHook.GenerateTrampoline<TroopClickDelegate>();
            leftClickHook.Apply();
            rightClickHook = PrepareHook(RequireMethod(typeof(MainViewModel), "TroopsRightClickCommand", new[] { typeof(object) }), (TroopClickDelegate)RightClickHook, "APIShared.UnitHud.TroopsRightClick", installed);
            rightClickOriginal = rightClickHook.GenerateTrampoline<TroopClickDelegate>();
            rightClickHook.Apply();
            populateGroupsHook = PrepareHook(RequireMethod(typeof(HUD_ControlGroups), "populate", Type.EmptyTypes), (PopulateGroupsDelegate)PopulateGroupsHook, "APIShared.UnitHud.PopulateControlGroups", installed);
            populateGroupsOriginal = populateGroupsHook.GenerateTrampoline<PopulateGroupsDelegate>();
            populateGroupsHook.Apply();
            gameActionHook = PrepareHook(RequireMethod(typeof(EngineInterface), "GameAction", new[] { typeof(Enums.KeyFunctions), typeof(int), typeof(int), typeof(int) }), (GameActionDelegate)GameActionHook, "APIShared.UnitHud.ControlGroupGameAction", installed);
            gameActionOriginal = gameActionHook.GenerateTrampoline<GameActionDelegate>();
            gameActionHook.Apply();
            updateSpritesHook = PrepareHook(RequireMethod(typeof(MainViewModel), "UpdateUITroopSprites", new[] { typeof(int), typeof(bool) }), (UpdateSpritesDelegate)UpdateSpritesHook, "APIShared.UnitHud.UpdateUITroopSprites", installed);
            updateSpritesOriginal = updateSpritesHook.GenerateTrampoline<UpdateSpritesDelegate>();
            updateSpritesHook.Apply();
            createTroopHook = PrepareHook(RequireMethod(typeof(MainViewModel), "ButtonCreateTroop", new[] { typeof(object) }), (CreateTroopDelegate)CreateTroopHook, "APIShared.UnitHud.ButtonCreateTroop", installed);
            createTroopOriginal = createTroopHook.GenerateTrampoline<CreateTroopDelegate>();
            createTroopHook.Apply();
            enterCreateTroopHook = PrepareHook(RequireMethod(typeof(MainViewModel), "ButtonEnterCreateTroop", new[] { typeof(object) }), (EnterCreateTroopDelegate)EnterCreateTroopHook, "APIShared.UnitHud.ButtonEnterCreateTroop", installed);
            enterCreateTroopOriginal = enterCreateTroopHook.GenerateTrampoline<EnterCreateTroopDelegate>();
            enterCreateTroopHook.Apply();
            recruitmentGameActionHook = PrepareHook(RequireMethod(typeof(EngineInterface), "GameAction", new[] { typeof(Enums.GameActionCommand), typeof(int), typeof(int), typeof(int) }), (RecruitmentGameActionDelegate)RecruitmentGameActionHook, "APIShared.UnitHud.RecruitmentGameAction", installed);
            recruitmentGameActionOriginal = recruitmentGameActionHook.GenerateTrampoline<RecruitmentGameActionDelegate>();
            recruitmentGameActionHook.Apply();
            APIShared.Internal.MissionEvents.SetOwner("APIShared_Serp");
            mapUnloadSubscription = APIShared.Internal.MissionEvents.Ended.Subscribe(_ => {
                ResetRecruitment();
                ResetActionButtonPresentation();
            });
            UnityEngine.Application.onBeforeRender += OnBeforeRender;
        }

        private static Hook PrepareHook(MethodInfo method, Delegate callback, string id, List<Hook> installed)
        {
            var config = new HookConfig { ManualApply = true, ID = id };
            var hook = new Hook(method, callback, config);
            installed.Add(hook);
            return hook;
        }

        private void SetupTroopsHook(HUD_Troops panel)
        {
            setupTroopsOriginal(panel);
            if (!HasCategories(UnitHudSurface.TroopSelection))
                return;
            try { RenderTroopCategories(panel); }
            catch (Exception ex)
            {
                HideCategoryButtons();
                lock (sync) visibleSlots.Clear();
                LogCallbackFailure("troop HUD", ex);
            }
        }

        private void LeftClickHook(MainViewModel self, object parameter) => HandleVanillaClick(self, parameter, true);
        private void RightClickHook(MainViewModel self, object parameter) => HandleVanillaClick(self, parameter, false);

        private void HandleVanillaClick(MainViewModel self, object parameter, bool left)
        {
            if (!HasCategories(UnitHudSurface.TroopSelection))
            {
                if (left) leftClickOriginal(self, parameter); else rightClickOriginal(self, parameter);
                return;
            }
            int type;
            try { type = (int)self.getChimpEnum(parameter as string); }
            catch
            {
                if (left) leftClickOriginal(self, parameter);
                else rightClickOriginal(self, parameter);
                return;
            }
            if (!HasDerivedCategory(type))
            {
                if (left) leftClickOriginal(self, parameter);
                else rightClickOriginal(self, parameter);
                return;
            }
            List<UnitHudUnitSnapshot> selected = CaptureSelectedUnits();
            if (left)
                SetSelection(selected.Where(x => x.VanillaType == type && !IsClaimed(x, UnitHudSurface.TroopSelection)).Select(x => x.GameId));
            else
                SetSelection(selected.Where(x => x.VanillaType != type || IsClaimed(x, UnitHudSurface.TroopSelection)).Select(x => x.GameId));
        }

        private void PopulateGroupsHook(HUD_ControlGroups panel)
        {
            populateGroupsOriginal(panel);
            if (!groupRecordsAvailable || !HasCategories(UnitHudSurface.ControlGroups)) return;
            try { RenderGroups(panel); }
            catch (Exception ex) { LogCallbackFailure("control-group HUD", ex); }
        }

        private void GameActionHook(Enums.KeyFunctions command, int value1, int value2, int value3)
        {
            gameActionOriginal(command, value1, value2, value3);
            if (!HasCategories(UnitHudSurface.ControlGroups)) return;
            if (command < Enums.KeyFunctions.GroupTroops0 || command > Enums.KeyFunctions.GroupTroops9 ||
                !MainViewModel.viewModelLoaded) return;
            try
            {
                MainViewModel main = MainViewModel.Instance;
                if (main?.Show_HUD_ControlGroups == true) main.HUDControlGroups?.Update();
            }
            catch (Exception ex) { LogCallbackFailure("control-group refresh", ex); }
        }

        private void CreateTroopHook(MainViewModel self, object parameter)
        {
            if (!activeRecruitmentHandlers && recruitmentLease == null) { createTroopOriginal(self, parameter); return; }
            int type;
            try { type = (int)self.getChimpEnum(parameter as string); }
            catch { createTroopOriginal(self, parameter); return; }
            lock (sync)
            {
                if (recruitmentLease != null && recruitmentLease.Ticket.BaseUnitType == type)
                {
                    refreshRequested = true;
                    return;
                }
            }
            int previous = createTroopContextType;
            createTroopContextType = type;
            try { createTroopOriginal(self, parameter); }
            finally { createTroopContextType = previous; }
        }

        private void EnterCreateTroopHook(MainViewModel self, object parameter)
        {
            enterCreateTroopOriginal(self, parameter);
            if (!activeRecruitmentHandlers) return;
            try
            {
                int type = (int)self.getChimpEnum("CHIMP_TYPE_" + ((parameter as string) ?? string.Empty).ToUpperInvariant());
                ApplyRecruitmentText(self, GetActiveRecruitment(type));
            }
            catch (Exception ex) { LogCallbackFailure("recruitment tooltip", ex); }
        }

        private int RecruitmentGameActionHook(Enums.GameActionCommand command, int structureId, int state, int value2)
        {
            if (activeRecruitmentHandlers && command == Enums.GameActionCommand.MakeTroop && createTroopContextType == state)
                TryBeginRecruitment(state, structureId);
            return recruitmentGameActionOriginal(command, structureId, state, value2);
        }

        private void UpdateSpritesHook(MainViewModel self, int colour, bool arabic)
        {
            if (updateSpritesActive)
                return;
            updateSpritesActive = true;
            try
            {
                updateSpritesOriginal(self, colour, arabic);
                lastSpriteColour = colour;
                lastSpriteArabic = arabic;
                hasSpriteContext = true;
                if (activeImages && IsImageOverrideContextReady()) ApplyImageOverrides(self, colour, arabic);
            }
            finally { updateSpritesActive = false; }
        }

        private static FieldInfo RequireField(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingFieldException(type.FullName, name);
        private static MethodInfo RequireMethod(Type type, string name, Type[] parameters) => type.GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
    }
}
