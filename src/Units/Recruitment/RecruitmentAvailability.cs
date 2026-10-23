using System;
using System.Threading;
using APIShared.Events;
using APIShared.Presentation;
using CrusaderDE;
using SHCDESE.API;
using SHCDESE.Interop;

namespace APIShared.Recruitment
{
    /// <summary>Recruitment state shared by every availability predicate of one Vanilla GUI check.</summary>
    public readonly struct RecruitmentAvailabilityContext
    {
        internal RecruitmentAvailabilityContext(long sequence, int localPlayerId, bool localPlayerHuman,
            eChimps hoveredUnitType, int vanillaAmountToMake)
        {
            Sequence = sequence;
            LocalPlayerId = localPlayerId;
            IsLocalPlayerHuman = localPlayerHuman;
            HoveredUnitType = hoveredUnitType;
            VanillaAmountToMake = vanillaAmountToMake;
        }

        /// <summary>Increases once per evaluated GUI check; equal values mean the same check, so a
        /// predicate may reuse values it derived for other buttons of that check.</summary>
        public long Sequence { get; }
        /// <summary>The Script Extender local player ID read once for this check.</summary>
        public int LocalPlayerId { get; }
        /// <summary>True when <see cref="LocalPlayerId"/> is a valid, non-AI player.</summary>
        public bool IsLocalPlayerHuman { get; }
        /// <summary>Vanilla's last hovered recruit type (<c>MainViewModel.lastTroopBuildChimp</c>);
        /// <c>CHIMP_NUM_TYPES</c> when no recruit button is hovered.</summary>
        public eChimps HoveredUnitType { get; }
        /// <summary>Vanilla's requested amount for the hovered type (<c>MainViewModel.lastTroopsAmountToMake</c>), unclamped.</summary>
        public int VanillaAmountToMake { get; }
    }

    /// <summary>
    /// One shared pass that lets mods disable Vanilla recruit buttons (European barracks, mercenary post,
    /// guilds, cathedral and Bedouin stockade) after Vanilla's GUI check has set them. Native costs,
    /// limits, affordability and the actual recruitment stay the caller's responsibility.
    /// </summary>
    /// <remarks>
    /// The pass runs after <c>FatControler.NoesisGUIUpdateChecksInGame</c> completes (through
    /// <see cref="PresentationEvents"/> GuiChecks Post), on that Unity caller thread, and only while one of
    /// Vanilla's recruitment in-building modes is active: only then does Vanilla rewrite every recruit
    /// button's IsEnabled in the same check. A button that is already disabled (by Vanilla or anyone else)
    /// is never re-enabled and its predicates are not queried. For each remaining button the active
    /// predicates are combined by OR in ordinal owner/ID order; the first true result disables it.
    /// </remarks>
    public static class RecruitmentAvailability
    {
        private const string BrokerId = "RecruitmentAvailability";
        private static readonly object sync = new object();
        private static Registration[] registrations = Array.Empty<Registration>();
        private static bool installed;
        private static long failures;
        private static long sequence;
        [ThreadStatic] private static bool evaluating;
        // Active registrations of the current pass; reused because passes run every frame while a
        // recruitment panel is open. The evaluating guard keeps passes from overlapping on a thread.
        [ThreadStatic] private static Registration[] activeBuffer;

        private static readonly RecruitButton[] Buttons =
        {
            new RecruitButton(eChimps.CHIMP_TYPE_ARCHER, panel => panel.RefRecruitArcherButton),
            new RecruitButton(eChimps.CHIMP_TYPE_SPEARMAN, panel => panel.RefRecruitSpearmanButton),
            new RecruitButton(eChimps.CHIMP_TYPE_MACEMAN, panel => panel.RefRecruitMacemanButton),
            new RecruitButton(eChimps.CHIMP_TYPE_XBOWMAN, panel => panel.RefRecruitXBowmanButton),
            new RecruitButton(eChimps.CHIMP_TYPE_PIKEMAN, panel => panel.RefRecruitPikemanButton),
            new RecruitButton(eChimps.CHIMP_TYPE_SWORDSMAN, panel => panel.RefRecruitSwordsmanButton),
            new RecruitButton(eChimps.CHIMP_TYPE_KNIGHT, panel => panel.RefRecruitKnightButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ENGINEER, panel => panel.RefRecruitEngineerButton),
            new RecruitButton(eChimps.CHIMP_TYPE_LADDERMAN, panel => panel.RefRecruitLaddermanButton),
            new RecruitButton(eChimps.CHIMP_TYPE_TUNNELER, panel => panel.RefRecruitTunellerButton),
            new RecruitButton(eChimps.CHIMP_TYPE_MONK, panel => panel.RefRecruitMonkButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ARAB_BOW, panel => panel.RefRecruitArabBowButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ARAB_SLAVE, panel => panel.RefRecruitArabSlaveButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ARAB_SLINGER, panel => panel.RefRecruitArabSlingerButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ARAB_ASSASIN, panel => panel.RefRecruitArabAssassinButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ARAB_HORSEMAN, panel => panel.RefRecruitArabHorseArcherButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ARAB_SWORDSMAN, panel => panel.RefRecruitArabSwordsmanButton),
            new RecruitButton(eChimps.CHIMP_TYPE_ARAB_GRENADIER, panel => panel.RefRecruitArabGrenadierButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_CAMEL_LANCER, panel => panel.RefRecruitBedouinCamelLancerButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_HEALER, panel => panel.RefRecruitBedouinHealerButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_EUNUCH, panel => panel.RefRecruitBedouinEunuchButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_AMBUSHER, panel => panel.RefRecruitBedouinAmbusherButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER, panel => panel.RefRecruitBedouinSkirmisherButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL, panel => panel.RefRecruitBedouinHeavyCamelButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_SAPPER, panel => panel.RefRecruitBedouinSapperButton),
            new RecruitButton(eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER, panel => panel.RefRecruitBedouinDemolisherButton),
        };

        /// <summary>
        /// Registers a process-lifetime predicate that returns true to disable the recruit button of a unit type.
        /// </summary>
        /// <remarks>
        /// Call on the Unity startup thread. <paramref name="isActive"/> is evaluated once per pass before any
        /// button is examined; an inactive registration costs nothing else. <paramref name="isBlocked"/> is
        /// queried synchronously for each still-enabled recruit button and must be side-effect free: do not
        /// mutate UI or game state or issue commands. Exceptions mean false/inactive, are isolated, counted and
        /// reported. No replay, unregistration or hook teardown; duplicate identities are rejected.
        /// </remarks>
        /// <param name="ownerGuid">The registering mod's stable GUID.</param>
        /// <param name="registrationId">An owner-local stable ID.</param>
        /// <param name="isBlocked">Returns true when the button of that unit type must be disabled.</param>
        /// <param name="isActive">Returns false while the owner's feature is logically off.</param>
        /// <param name="reason">Empty on success; otherwise why nothing was registered.</param>
        /// <returns>True when the predicate was registered.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<RecruitmentAvailabilityContext, eChimps, bool> isBlocked, Func<bool> isActive, out string reason)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) ||
                isBlocked == null || isActive == null)
            { reason = "Owner, ID, predicate and activity callback are required."; return false; }
            lock (sync)
            {
                foreach (Registration entry in registrations)
                    if (entry.Owner == ownerGuid && entry.Id == registrationId)
                    { reason = "This owner already registered that ID."; return false; }
                if (!installed)
                {
                    if (!PresentationEvents.TryRegisterWhenActive(PresentationOperation.GuiChecks, APISharedPlugin.PluginGuid, BrokerId,
                        null, OnGuiChecksPost, out string registrationReason, 0, HasRegistrations))
                    { reason = "Recruitment availability unavailable: " + registrationReason; return false; }
                    installed = true;
                }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration(ownerGuid, registrationId, isBlocked, isActive);
                Array.Sort(next, (a, b) =>
                {
                    int comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
                });
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty;
            return true;
        }

        /// <summary>Total isolated predicate and activity-callback errors.</summary>
        public static long CallbackFailures => Interlocked.Read(ref failures);

        private static bool HasRegistrations() => Volatile.Read(ref registrations).Length != 0;

        private static void OnGuiChecksPost(PresentationPostEventArgs args)
        {
            if (!args.OriginalCompleted || evaluating || !IsVanillaRecruitmentPanelMode())
                return;
            Registration[] snapshot = Volatile.Read(ref registrations);
            evaluating = true;
            try
            {
                Registration[] active = activeBuffer;
                if (active == null || active.Length < snapshot.Length)
                    activeBuffer = active = new Registration[snapshot.Length];
                int activeCount = 0;
                for (int index = 0; index < snapshot.Length; index++)
                    if (IsActive(snapshot[index])) active[activeCount++] = snapshot[index];
                if (activeCount == 0) return;

                MainViewModel main = MainViewModel.Instance;
                HUD_Buildings panel = main?.HUDBuildingPanel;
                if (panel == null) return;
                RecruitmentAvailabilityContext context = CreateContext(main);
                foreach (RecruitButton button in Buttons)
                {
                    Noesis.UIElement element = button.Resolve(panel);
                    if (element == null || !element.IsEnabled) continue;
                    for (int index = 0; index < activeCount; index++)
                    {
                        if (!IsBlocked(active[index], context, button.UnitType)) continue;
                        element.IsEnabled = false;
                        break;
                    }
                }
            }
            finally
            {
                if (activeBuffer != null) Array.Clear(activeBuffer, 0, activeBuffer.Length);
                evaluating = false;
            }
        }

        private static RecruitmentAvailabilityContext CreateContext(MainViewModel main)
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int playerId = players.GetLocalPlayerId();
            bool human;
            try { human = players.IsPlayerIdValid(playerId) && !players.IsAIPlayer(playerId); }
            catch { human = false; }
            return new RecruitmentAvailabilityContext(Interlocked.Increment(ref sequence), playerId, human,
                (eChimps)(int)main.lastTroopBuildChimp, main.lastTroopsAmountToMake);
        }

        // Vanilla's FatControler.NoesisGUIUpdateChecksInGame rewrites every recruit button only while
        // one of these in-building modes is active, before GuiChecks Post. Outside them the buttons are
        // neither visible nor touched by Vanilla, so nothing needs disabling.
        internal static bool IsVanillaRecruitmentPanelMode()
        {
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            return state != null && IsRecruitmentSubMode(state.app_sub_mode);
        }

        internal static bool IsRecruitmentSubMode(int appSubMode)
        {
            switch ((Enums.InBuildingModes)appSubMode)
            {
                case Enums.InBuildingModes.INSIDE_BARRACKS:
                case Enums.InBuildingModes.INSIDE_ENGINEERS_GUILD:
                case Enums.InBuildingModes.INSIDE_TUNNELLERS_GUILD:
                case Enums.InBuildingModes.INSIDE_MERCPOST:
                case Enums.InBuildingModes.INSIDE_BEDOUIN_STOCKADE:
                case Enums.InBuildingModes.INSIDE_CATHEDRAL:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsActive(Registration registration)
        {
            try { return registration.IsActive(); }
            catch (Exception ex) { ReportFailure(registration, "activity", ex); return false; }
        }

        private static bool IsBlocked(Registration registration, RecruitmentAvailabilityContext context, eChimps unitType)
        {
            try { return registration.IsBlocked(context, unitType); }
            catch (Exception ex) { ReportFailure(registration, "predicate", ex); return false; }
        }

        private static void ReportFailure(Registration registration, string phase, Exception error)
        {
            long count = Interlocked.Increment(ref failures);
            if ((count & (count - 1)) == 0)
                InterceptionDiagnostics.Report(registration.Owner + "/" + registration.Id + " recruitment availability " + phase, error);
        }

        private readonly struct RecruitButton
        {
            internal RecruitButton(eChimps unitType, Func<HUD_Buildings, Noesis.UIElement> resolve)
            { UnitType = unitType; Resolve = resolve; }
            internal eChimps UnitType { get; }
            internal Func<HUD_Buildings, Noesis.UIElement> Resolve { get; }
        }

        private sealed class Registration
        {
            internal readonly string Owner, Id;
            internal readonly Func<RecruitmentAvailabilityContext, eChimps, bool> IsBlocked;
            internal readonly Func<bool> IsActive;
            internal Registration(string owner, string id, Func<RecruitmentAvailabilityContext, eChimps, bool> isBlocked, Func<bool> isActive)
            { Owner = owner; Id = id; IsBlocked = isBlocked; IsActive = isActive; }
        }
    }
}
