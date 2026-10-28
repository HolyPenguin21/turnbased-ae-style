// Managed adapters for focused owner-state tests. Compiled only by run.py, never by Unity.
// Native visuals, AI stats, carrier creation, authored catalogs and initiative rules are adapters;
// the state owners, reference validation, movement/activation data and registry code are real.
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.HexGrid;
namespace UnityEngine
{
    public class Object { public static void Destroy(object value) {} public static void DestroyImmediate(object value) {} }
    public class MonoBehaviour : Object {
        public GameObject gameObject = new GameObject("adapter"); public Transform transform = new Transform();
        public readonly Dictionary<Type, object> Components = new Dictionary<Type, object>();
        public T GetComponent<T>() => Components.TryGetValue(typeof(T), out var value) ? (T)value : default;
        protected static T Instantiate<T>(T prefab, Transform parent) where T : new() => new T();
    }
    public class Transform { public Vector3 position; }
    public static class Time { public static int frameCount = 73; }
    public static class Application { public static bool isPlaying = true; }
    public class SerializeField : Attribute {} public class RequireComponent : Attribute { public RequireComponent(Type type) {} }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) {} }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public static class Debug { public static void LogWarning(object value) { throw new InvalidOperationException("Unexpected new deck preparation"); } public static void LogWarning(object value, object context) { throw new InvalidOperationException("Unexpected authored starter warning"); } public static void LogException(Exception ex) {} }
    public static class Random { public static int Range(int min, int max) => throw new InvalidOperationException("FORBIDDEN random draw/deal"); }
    public class ScriptableObject : Object {}
    public class GameObject { public bool active; public GameObject(string name) {} public void SetActive(bool value) { active=value; } public T AddComponent<T>() where T : new() => new T(); }
    public class Sprite {}
    public struct Color { public static Color grey => default; public static Color white => default; }
    public struct Vector2 { public float x, y; }
    public struct Vector3 { public float x, z; public Vector3(float x, float y, float z) { this.x = x; this.z = z; }
        public static Vector3 operator +(Vector3 a, Vector3 b) => a; public static Vector3 operator *(Vector3 a,float b) => a;
    }
    public static class Mathf
    {
        public static float Sqrt(float a) => (float)Math.Sqrt(a);
        public static float Round(float a) => (float)Math.Round(a);
        public static int RoundToInt(float a) => (int)Math.Round(a);
        public static float Abs(float a) => Math.Abs(a);
        public static int Abs(int a) => Math.Abs(a);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b); public static int Clamp(int a,int b,int c)=>Math.Clamp(a,b,c);
    }
    public class MinAttribute : Attribute { public MinAttribute(float value) {} }
    public class TextAreaAttribute : Attribute {}
    public class HideInInspector : Attribute {}
    public class CreateAssetMenuAttribute : Attribute { public string fileName, menuName; }
}

namespace Game.Cards
{
    public enum CardType { Hero, Unit, Base, Facility, Equipment, Tactic }
    public enum AttachmentSlot { None, Equipment, Mutator }
    public enum ResearchProductionMode { Production, Research }
    public class CardDefinition { public string authoredKey,displayName; public int deckCopyLimit=4,deckPointCost,fate; public bool deckBuilderExcluded; public AttachmentSlot attachmentSlot; public Game.Players.Faction faction; public CardType cardType; public int commandRating, activationApCost, apCost, moveMax, tacticAmount;
        public bool isAviation; public List<string> grantedAbilities = new List<string>(); public TacticEffect tacticEffect; public ResourceCost resourceCost; }
    public class ResourceCost { public int human, energy, materials, tech; }
    public class FactionCardCatalog { public Game.Players.Faction faction; public List<CardDefinition> cards=new List<CardDefinition>(); public static CardDefinition ResolveAcross(object catalogs, string key, bool warn, object owner) => null; }
    public class ArmyTagAttribute : Attribute {}
    public struct PredictedEquipmentState {}
    public struct AttachmentResourceState {}
    public enum EquipmentStat { MoveMax, Fate }
    public static class EquipmentSystem { public static void ReconcileResourceRefill(UnitData unit, EquipmentStat stat) => throw new Exception("FORBIDDEN movement/fate refill"); }
    public static class UnitAbilities { public const string Transport = "Transport", Barracks = "Barracks", RapidReaction = "RapidReaction"; }
    public static class AbilityParams
    {
        public static int GetBestRecceRadius(object source) => 0;
        public static int GetBestRecceSpotStrength(object source) => 0;
        public static int GetStealthLevel(UnitData unit) => 1;
    }
}
namespace Game.Map
{
    public class MapObjectVisual : UnityEngine.MonoBehaviour { public bool IsFrozenRememberedAppearance; public string Appearance;
        public void SetColor(UnityEngine.Color color) {} public void SetVisible(bool value) { gameObject.SetActive(value); }
        public MapObjectVisual CreateFrozenSnapshot(UnityEngine.Transform parent) {
            if (!IsFrozenRememberedAppearance) throw new Exception("Expected detached frozen native appearance adapter");
            return new MapObjectVisual { IsFrozenRememberedAppearance=true, Appearance=Appearance };
        }
    }
    public class ArmyController { public bool IsMoving; public HexCoord CurrentHex; public MapObjectVisual Visual; }
    
    public class FacilityData
    {
        public string Name;
        public int StructurePointsCurrent, StructurePointsMax, Defense, Fate, Attack, AntiAirRadius;
        public UnitData AntiAirGunner;
        public HashSet<string> Abilities = new HashSet<string>();
    }
    public class BuildingData
    {
        public string Name; public HexCoord Hex; public PlayerSetupData Owner;
        public bool IsBase, IsStartingCitadel; public MapObjectVisual Visual;
        public FacilityData[] FacilitySlots = new FacilityData[5];
        public HashSet<string> Abilities = new HashSet<string>();
        public bool HasAbility(string ability) => Abilities.Contains(ability);
    }
}
namespace Game.Aviation
{
    public enum AirStrikePolicy {}
    public static class AviationRules
    {
        public static bool IsAirArmy(ArmyData army) => army.IsAirArmy;
        public static bool IsAirfield(ArmyData army) => army.IsAirfield;
        public static int EffectiveMoveCurrent(UnitData unit) => unit.MoveCurrent;
    }
    public static class AviationActions
    {
        // A visible adapter side effect lets the restore-scope test detect an unwanted call.
        public static void EnsureCarrierContainers(ArmyData army) => throw new Exception("FORBIDDEN carrier allocation during restore");
        public static void RemoveCarrierContainers(UnitData unit) {}
        public static void LandWingsBeforeCarrierDeparts(ArmyData army) {}
    }
}
namespace Game.Ai
{
    public static class AiMatchStats { public static void EndMatch() {} public static void RecordBuilt(BuildingData building) {} public static void RecordBuildingTaken(bool captured, PlayerSetupData oldOwner, PlayerSetupData newOwner) {} }
    public static class AiDebugLog { public static void Write(string line) {} }
}
namespace Game.Combat
{
    public struct ChallengeResult { public int AttackerSuccesses, DefenderSuccesses; }
    public static class ChallengeResolver { public static ChallengeResult Resolve(int attack, int defense) => throw new Exception("FORBIDDEN fresh detection/combat challenge"); }
    public static class BattleInitiator { public static bool CanInitiateContact(ArmyData army) => true; public static bool DefendsBuildingAgainst(ArmyData army, PlayerSetupData mover) => false; }
}

namespace Game.Styles
{
    public static class PlayerColorPalette { public static UnityEngine.Color[] Colors = new UnityEngine.Color[10]; }
    public static class MapFlagAnimator { public static void ApplyFaction(object visual, object faction) {} }
}
namespace Game.Turns
{
    public static class InitiativeRules
    {
        public const int MaxBonusTokens = 9;
        public static int CumulativeUnitsForTokenCount(int count) => count * (count + 1) / 2;
        public static int NextBonusTokenCost(int count) => count + 1;
    }
}
namespace Game.Terrain { public class TerrainTypeEntry { public bool blocksGroundMovement; public Game.Economy.ResourceYields resourceYields; } }

// Remaining scene/native/catalog surfaces only. Restore, reset and owner mutations are in real sources.
namespace Game.Core
{
    public sealed class ProfileScope : IDisposable { public ProfileScope(string name) {} public void Dispose() {} }
    public class GameConfig
    {
        public int armyVisionRadius, buildingVisionRadius;
        public Game.Cards.StartingDeckCatalog collectionDeckCatalog;
        public EventMarkerVisual eventMarkerPrefab = new EventMarkerVisual();
        public ResourceIconVisual resourceIconPrefab = new ResourceIconVisual();
        public UnityEngine.Vector2 eventIconOffset;
        public float resourceIconSpacing, resourceRowOffset;
    }
}
namespace Game.Cards
{
    public class DeckCardEntry { public string cardKey; public int count; }
    public sealed class StartingDeckDefinition { public string deckName; public List<DeckCardEntry> cards=new List<DeckCardEntry>(); }
    public sealed class ResearchProductionCatalog {
        public List<FactionCardCatalog> cardCatalogs=new List<FactionCardCatalog>();
        public CardDefinition ResolveCard(string key)=>cardCatalogs.SelectMany(c=>c.cards).FirstOrDefault(c=>c.authoredKey==key);
        public IEnumerable<CardDefinition> ResolveFor(ResearchProductionMode mode,Game.Players.Faction faction)=>Enumerable.Empty<CardDefinition>();
    }
    public sealed class StartingDeckCatalog
    {
        // Authored content is supplied as explicit test values; collection rules/transactions are real.
        public List<FactionCardCatalog> catalogs=new List<FactionCardCatalog>();
        public FactionCardCatalog GetCatalog(Game.Players.Faction faction)=>catalogs.FirstOrDefault(c=>c.faction==faction);
        public StartingDeckDefinition GetDeck(Game.Players.Faction faction)=>null;
        public CardDefinition ResolveCard(string key)=>catalogs.SelectMany(c=>c.cards).FirstOrDefault(c=>c.authoredKey==key);
        public sealed class AiDeck { public string deckName; }
        public IEnumerable<AiDeck> GetAiDecks(Game.Players.Faction faction) => throw new Exception("FORBIDDEN new deck");
        public string PickAiDeckName(Game.Players.Faction faction) => throw new Exception("FORBIDDEN new deck");
        public HashSet<string> BuildAiBlueprintKeys(Game.Players.Faction faction,string name) => throw new Exception("FORBIDDEN new deck");
        public List<CardDefinition> BuildAiDeckPool(Game.Players.Faction f,string name,string key) => throw new Exception("FORBIDDEN new deck");
        public bool TryBuildDeckPool(List<DeckCardEntry> entries,out List<CardDefinition> pool,out string error) => throw new Exception("FORBIDDEN new deck");
    }
    public static class ResearchProductionSystem { public static CardData MintCard(CardDefinition d) => throw new Exception("FORBIDDEN production"); }
}
namespace Game.Progression
{
    public static class ProgressionContext
    {
        public static string Error => "FORBIDDEN profile initialization";
        // Actual service object wired only by the combined receipt/tactical restore check.
        public static CollectionService Collection {get;set;}
        public static bool Initialize(Game.Core.GameConfig config) => throw new Exception("FORBIDDEN profile initialization");
    }
}
namespace Game.Campaign
{
    public sealed class CampaignOperation {}
    public class CampaignMatchContext { public string MatchId; }
    public struct CompletedMatchResult { public string MatchId; }
    public static class CampaignMatchBridge
    {
        // This harness deliberately tests a non-campaign match; a campaign context may not silently pass.
        public static bool CanRestoreContext(CampaignMatchContext context,out string error) {
            if (context!=null) throw new NotSupportedException("Campaign store validation belongs to campaign checks"); error=null; return true;
        }
    }
}
namespace Game.Terrain
{
    public enum MapSize { Small = 3 } public enum Biome { Arid }
    public struct RegionResourceConditions {}
}
namespace Game.Map
{
    public static class ArmyActions { public static int EffectiveDeployApCost(Game.Cards.CardDefinition card)=>throw new Exception("FORBIDDEN opening deal"); }
    public static class HexMapGenerator
    {
        // Authored terrain/texture reconstruction is separately exercised by terrain/Unity checks.
        public static void ValidateLayout(HexMapLayout layout,Game.Core.GameConfig config) {
            if (layout==null || layout.Version!=1 || layout.Cells==null) throw new ArgumentException("Invalid portable layout boundary");
        }
    }
    public class HexMap : UnityEngine.MonoBehaviour
    {
        public float OuterRadius=1;
        public IEnumerable<HexCoord> AllCoords { get { yield return new HexCoord(0,0); yield return new HexCoord(1,0); } }
        public bool TryGetTerrainAt(HexCoord hex,out Game.Terrain.TerrainTypeEntry terrain) {
            terrain=new Game.Terrain.TerrainTypeEntry { resourceYields=new Game.Economy.ResourceYields {human=1} }; return true;
        }
        public UnityEngine.Vector3 HexToWorld(HexCoord hex)=>default;
    }
    public class EventMarkerVisual : UnityEngine.MonoBehaviour { public void SetSortingOrder(int order) {} }
    public class ResourceIconVisual : UnityEngine.MonoBehaviour {
        public int Amount; public void SetResource(Game.Economy.ResourceType type,int amount,bool known) { Amount=amount; } public void SetAmountKnown(bool value) {}
    }
    public static class MapSortingOrder { public const int EventIcon=1; }
    public partial class HexSelectionController : UnityEngine.MonoBehaviour
    {
        public bool IsBattleActive, HasProductionTransaction;
        private readonly Dictionary<PlayerSetupData,Dictionary<HexCoord,MapObjectVisual>> _rememberedBuildingVisuals=new Dictionary<PlayerSetupData,Dictionary<HexCoord,MapObjectVisual>>();
        private readonly Dictionary<MapObjectVisual,MapObjectVisual> _rememberedBuildingSources=new Dictionary<MapObjectVisual,MapObjectVisual>();
        private sealed class RememberedArmyVisual { public HexCoord Hex; public ArmyData Snapshot; public MapObjectVisual Visual; }
        private readonly Dictionary<PlayerSetupData,Dictionary<int,RememberedArmyVisual>> _rememberedArmyVisuals=new Dictionary<PlayerSetupData,Dictionary<int,RememberedArmyVisual>>();
        private readonly Dictionary<PlayerSetupData,Dictionary<HexCoord,List<RememberedArmyVisual>>> _rememberedArmyVisualsByHex=new Dictionary<PlayerSetupData,Dictionary<HexCoord,List<RememberedArmyVisual>>>();
        private void SetRememberedBuildingVisualsVisible(bool visible) { foreach(var p in _rememberedBuildingVisuals.Values) foreach(var v in p.Values) v.SetVisible(visible); }
        private void SetRememberedArmyVisualsVisible(bool visible) { foreach(var p in _rememberedArmyVisuals.Values) foreach(var v in p.Values) v.Visual.SetVisible(visible); }
        private void AddRememberedArmyToHexIndex(PlayerSetupData viewer,RememberedArmyVisual visual) {
            if (!_rememberedArmyVisualsByHex.TryGetValue(viewer,out var values)) _rememberedArmyVisualsByHex.Add(viewer,values=new Dictionary<HexCoord,List<RememberedArmyVisual>>());
            if (!values.TryGetValue(visual.Hex,out var list)) values.Add(visual.Hex,list=new List<RememberedArmyVisual>()); list.Add(visual);
        }
        private void ReapplyRememberedLayout(PlayerSetupData viewer,HexCoord hex) {} // native marker positioning only
        public void RestackArmiesOn(HexCoord hex,object context) {} public void CreateArmyMarker(ArmyData army) {}
    }
}
namespace Game.UI
{
    public static class UIFocusUtility { public static bool HasOverlay; public static bool IsTextFieldFocused()=>false; }
    public class RestoreUiAdapter { public bool IsShowing,HasPendingCardOperation; public void HideAiHandDebug(){} public void HideRootDebug(){} }
}
namespace Game.Turns
{
    public static class InitiativePublicHistory { public static void Clear(){} }
    public partial class GameTurnController
    {
        // Serialized scene wiring only; capture/restore logic remains the Assets partial.
        private Game.Core.GameConfig gameConfig;
        public int TurnNumber {get;private set;}
        public List<PlayerSetupData> CurrentTurnOrder {get;private set;}=new List<PlayerSetupData>();
        public PlayerSetupData CurrentPlayer {get;private set;}
        public bool TurnConfirmed {get;private set;}
        private bool _turnConfirmationInProgress,_gameOver,_otherTurnPopupSuspended,_attachModeActive;
        private PlayerSetupData _suspendedOtherTurnPlayer;
        private int _currentPlayerIndex,_turnConfirmedFrame;
        private readonly Dictionary<PlayerSetupData,long> _completedTurns=new Dictionary<PlayerSetupData,long>();
        private readonly HashSet<PlayerSetupData> _matchGreetingsShown=new HashSet<PlayerSetupData>();
        private readonly Dictionary<PlayerSetupData,List<string>> _pendingAviationMessages=new Dictionary<PlayerSetupData,List<string>>();
        private readonly Queue<string> _aviationMessageQueue=new Queue<string>();
        public bool IsCombatPresentationActive;
        public HexSelectionController hexSelectionController;
        public Game.UI.RestoreUiAdapter popupPanel,armyViewerModal,baseViewerModal,researchProductionModal,eventChoicePopup,eventRewardPopup,cardHand,resourceBar;
        public int PresentationBinds,ExitCalls;
        public event Action TurnStateChanged;
        public long CompletedTurnsFor(PlayerSetupData player)=>_completedTurns.TryGetValue(player,out var count)?count:0;
        private void ConfigureStealthTurnProviders() {
            StealthSystem.CompletedTurnsProvider=CompletedTurnsFor;
            StealthSystem.ObserverTakingTurnProvider=player=>player==CurrentPlayer && TurnConfirmed;
        }
        private void InitializeMatchPresentation() { if (Game.Setup.MatchRestoreScope.IsRestoring) throw new Exception("Presentation bind inside quiet scope"); PresentationBinds++; }
        private void RecomputeBlockedState() {}
        public void PrepareForExit() { ExitCalls++; }
        public void BeginGame()=>throw new Exception("FORBIDDEN BeginGame");
        private void BeginPlayerTurn()=>throw new Exception("FORBIDDEN BeginPlayerTurn");
        private void BeginNewTurn()=>throw new Exception("FORBIDDEN BeginNewTurn");
        private void AllocateActionPoints()=>throw new Exception("FORBIDDEN AP refill");
        private void GrantIncome()=>throw new Exception("FORBIDDEN income");
        private void RefillMovement()=>throw new Exception("FORBIDDEN refill");
    }
}
namespace Game.Setup
{
    public partial class CitadelSetupController
    {
        public Game.Core.GameConfig gameConfig;
        public HexMap map;
        public Game.Turns.GameTurnController turnController;
        public HexSelectionController hexSelectionController;
        public int ConfigureCalls;
        // Scene's native wiring is adapted; the actual inventory reset is retained.
        private void ConfigureWorldSystems() {
            ConfigureCalls++; MatchStateInventory.ResetForNewMatch();
            VisionSystem.Configure(gameConfig);
        }
        public bool RunPreparedRestore()=>TryRestorePreparedMatch();
    }
}
namespace Game.Combat { public static class WorthIt { public static void EndEstimateCacheScope() {} } }
namespace Game.Ai { public static class AiMapMemory { public static void Clear(){} } }

namespace Game.Ai.V2 {
public static class AiAllocatorStateRegistry { public static void Clear(){} }
public static class AiRadarStateRegistry { public static void Clear(){} }
public static class AiReconMemory { public static void Clear(){} }
public static class AiV2Trace { public static void Clear(){} }
public static class AirSortieRegistry { public static void Clear(){} }
public static class ApBudgetTelemetry { public static void ClearAll(){} }
public static class ApTurnPressure { public static void ClearAll(){} }
public static class AviationObligationStallRegistry { public static void Clear(){} }
public static class CapabilityPoolExhaustionRegistry { public static void Clear(){} }
public static class DevelopmentDiversity { public static void ClearAll(){} }
public static class DevelopmentInvestmentGate { public static void Clear(){} }
public static class DevelopmentOpportunityEvaluator { public static void ClearRecipientEvaluationCache(){} }
public static class DevelopmentOutcomeTelemetry { public static void ClearAll(){} }
public static class EconomyReservationLifecycle { public static void ClearAll(){} }
public static class ForceBaselineRegistry { public static void Clear(){} }
public static class LifecycleReturnPolicy { public static void ClearAll(){} }
public static class MissionIntentRegistry { public static void Clear(){} }
public static class OperationContinuationWindow { public static void ClearAll(){} }
public static class PreparationDeliveryMemory { public static void Clear(){} }
public static class ReservationInvariants { public static void ClearAll(){} }
public static class ResourceStarvationRegistry { public static void Clear(){} }
public static class StrategicCapabilityLeaseRegistry { public static void ClearAll(){} }
public static class StrategicInterruptRegistry { public static void ClearAll(){} }
public static class StrategicResourceReservationLedger { public static void ClearAll(){} }
public static class StrategicTempoBudget { public static void ClearAll(){} }
public static class V2TurnActivityTelemetry { public static void Clear(){} }
}
namespace Game.Ai.V2.Initiative { public static class InitiativeAnalyticsHistory { public static void Clear(){} } }
