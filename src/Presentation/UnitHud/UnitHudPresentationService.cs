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
        private const int TroopSlotCount = 8;
        private const int GroupCount = 10;
        private const int GroupCapacity = 10000;
        private const int GroupRecordWidth = 2;
        private const int GroupVisibleSlots = 4;
        private const int ControlGroupStoragePatternRva = 0x186338;
        private const int ControlGroupStorageDisplacementOffset = 0x10;
        private const int ControlGroupStorageNextInstructionOffset = 0x14;
        private const int ControlGroupStorageRva = 0x36D78D0;
        private const int TunnelSummaryType = 33;
        private const int EuropeanTroopSummaryStart = 0;
        private const int MonkSummaryType = 9;
        private const int SiegeEngineSummaryStart = 10;
        private const int PortableSiegeSummaryStart = 13;
        private const int ArabicTroopSummaryStart = 17;
        private const string ControlGroupStoragePattern =
            "48 8D 1D ? ? ? ? 48 8B F8 48 8B E9 48 8D 05 ? ? ? ? BE 0A 00 00 00 45 33 F6";
        private static readonly CompiledBytePattern CompiledControlGroupStoragePattern =
            CompiledBytePattern.Parse(ControlGroupStoragePattern);

        private static readonly FieldInfo SelectedCountsField = RequireField(typeof(HUD_Troops), "SelectedChimpArray");
        private static readonly FieldInfo SelectedTypeCountField = RequireField(typeof(HUD_Troops), "NoSelectedChimpTypes");
        private static readonly FieldInfo CurrentPageField = RequireField(typeof(HUD_Troops), "currentPage");
        private static readonly FieldInfo PagesField = RequireField(typeof(HUD_Troops), "pages");
        private static readonly FieldInfo TroopPositionsField = RequireField(typeof(HUD_Troops), "SelTroopPositions");
        private static readonly FieldInfo GroupImagesField = RequireField(typeof(HUD_ControlGroups), "RefTroopImages");
        private static readonly FieldInfo GroupValuesField = RequireField(typeof(HUD_ControlGroups), "RefTroopValues");
        private static readonly FieldInfo GroupExtraField = RequireField(typeof(HUD_ControlGroups), "RefTroopExtraValues");
        private static readonly MethodInfo GroupSpriteMethod = RequireMethod(typeof(HUD_ControlGroups), "GetTroopSprite", new[] { typeof(int) });

        private readonly object sync = new object();
        private readonly Stack<HudWorkBuffers> hudBufferPool = new Stack<HudWorkBuffers>();
        private static readonly UnitHudImageSlot[] ImageSlots = (UnitHudImageSlot[])Enum.GetValues(typeof(UnitHudImageSlot));
        private readonly List<CategoryRegistration> categories = new List<CategoryRegistration>();
        private readonly List<InteractionRegistration> interactions = new List<InteractionRegistration>();
        private readonly List<ImageRegistration> imageOverrides = new List<ImageRegistration>();
        private readonly List<RecruitmentRegistration> recruitment = new List<RecruitmentRegistration>();
        // Immutable internal views are replaced only while holding sync; consumers never receive them.
        private CategoryRegistration[] categoryView = Array.Empty<CategoryRegistration>();
        private InteractionRegistration[] interactionView = Array.Empty<InteractionRegistration>();
        private ImageRegistration[] imageView = Array.Empty<ImageRegistration>();
        private readonly Dictionary<int, RecruitmentRegistration[]> recruitmentViews =
            new Dictionary<int, RecruitmentRegistration[]>();
        private readonly HashSet<string> loggedCategoryConflicts = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> loggedCallbackFailures = new HashSet<string>(StringComparer.Ordinal);
        private readonly ManualLogSource log;
        private readonly string binaryHash;
        private readonly int* groupRecords;
        private readonly bool groupRecordsAvailable;
        private readonly List<UnitHudSlotSnapshot> visibleSlots = new List<UnitHudSlotSnapshot>();
        private Hook setupTroopsHook;
        private Hook leftClickHook;
        private Hook rightClickHook;
        private Hook populateGroupsHook;
        private Hook gameActionHook;
        private Hook updateSpritesHook;
        private Hook createTroopHook;
        private Hook enterCreateTroopHook;
        private Hook recruitmentGameActionHook;
        private SetupTroopsDelegate setupTroopsOriginal;
        private TroopClickDelegate leftClickOriginal;
        private TroopClickDelegate rightClickOriginal;
        private PopulateGroupsDelegate populateGroupsOriginal;
        private GameActionDelegate gameActionOriginal;
        private UpdateSpritesDelegate updateSpritesOriginal;
        private CreateTroopDelegate createTroopOriginal;
        private EnterCreateTroopDelegate enterCreateTroopOriginal;
        private RecruitmentGameActionDelegate recruitmentGameActionOriginal;
        private IDisposable mapUnloadSubscription;
        private HUD_Troops activeTroopPanel;
        private Grid[] categoryHosts;
        private Button[] categoryButtons;
        private Border[] categoryTints;
        private HUD_ControlGroups activeGroupPanel;
        private Border[,] groupTints;
        private volatile bool refreshRequested;
        private readonly Dictionary<string, bool> ownerActivation = new Dictionary<string, bool>(StringComparer.Ordinal);
        private volatile UnitHudSurface activeSurfaces;
        private volatile bool activeImages;
        private volatile bool activeRecruitmentHandlers;
        private volatile bool pendingPresentation;
        private UnitHudSurface restoreSurfaces;
        private bool restoreImages;
        private readonly HashSet<int> writtenArmyIndices = new HashSet<int>();
        private readonly Dictionary<UnitHudImageSlot, ImageSource> originalImages = new Dictionary<UnitHudImageSlot, ImageSource>();
        private readonly Dictionary<UnitHudImageSlot, ImageSource> appliedImages = new Dictionary<UnitHudImageSlot, ImageSource>();
        private MainViewModel hoverMain;
        private string hoverOriginal, hoverApplied;
        private MainViewModel recruitmentTextMain;
        private string recruitmentTextOriginal, recruitmentTextApplied;
        private HUD_Buildings recruitmentPanel, detailPanel, armyPanel;
        private Panel armyHost;
        private static readonly ConditionalWeakTable<Border, TintCache> TintCaches = new ConditionalWeakTable<Border, TintCache>();
        private int lastFrame = -1;
        private int[] lastTroopSelectionIds = Array.Empty<int>();
        private int[] lastTroopSelectionTypes = Array.Empty<int>();
        private bool hasRenderedTroopSelection;
        private int lastSpriteColour;
        private bool lastSpriteArabic;
        private bool hasSpriteContext;
        private readonly Dictionary<int, string> activeRecruitment = new Dictionary<int, string>();
        private volatile RecruitmentLease recruitmentLease;
        private long nextRecruitmentTicketId;
        private Grid archerVariantHost;
        private Button archerVariantPrevious;
        private Button archerVariantNext;
        private Border archerVariantTint;
        private bool recruitmentControlsLogged;
        private Grid unitDetailHost;
        private Image unitDetailImage;
        private Border unitDetailTint;
        private TextBlock unitDetailDescription;
        [ThreadStatic]
        private static bool updateSpritesActive;
        [ThreadStatic]
        private static int createTroopContextType;

        private UnitHudPresentationService(string hash, ManualLogSource logger, int* records, bool recordsAvailable)
        {
            binaryHash = hash ?? string.Empty;
            log = logger;
            groupRecords = records;
            groupRecordsAvailable = recordsAvailable;
        }

        private static void SetSelection(IEnumerable<int> ids) => EngineInterface.TroopSelectionChanged(ids.Where(x => x > 0).Distinct().ToArray());

        private readonly Stack<ArmyWorkBuffers> armyBufferPool = new Stack<ArmyWorkBuffers>();

        private readonly Dictionary<string, Noesis.Grid> armyEntries = new Dictionary<string, Noesis.Grid>(StringComparer.Ordinal);

        private void LogCallbackFailure(string area, Exception ex)
        {
            lock (sync)
                if (!loggedCallbackFailures.Add(area)) return;
            NativeApiLog.Error(log, $"Unit HUD {area} failed closed; unaffected Vanilla presentation remains active: {ex}");
        }
    }
}
