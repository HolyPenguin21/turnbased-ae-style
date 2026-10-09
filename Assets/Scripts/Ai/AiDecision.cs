using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Ai
{
    // A single executable AI action, produced by the V2 pipeline's executors and handed to
    // AiTurnController.MoveArmyRoutine. Post-ARCH-01 this is the move-order data object and its
    // one factory the V2 executors use — no scoring, no legacy task references.
    public enum AiActionKind
    {
        MoveArmy,
        PlayCard,
        PlayFacilityCard,
        AttachEquipment,
        ReserveArmy,
        DrawCard,
        BuildFacility,
        RepairUnit,
        SplitGarrisonArmy,
        CollapseAssembly,
        ConsolidateUnits,
        ConsolidateSwap,
        DetachCollector,
        SpawnReconArmy,
        AssembleRecceScout,
        RequestRaidArmy,
        AssembleRaidForce,
        DispatchReinforcement,
        ReinforceSwap,
        RequestDefendArmy,
        ActiveDefenceForce,
        StrengthenDefenceForce,
        BuildBase,
        SeedNewBaseGarrison,
        DispatchBaseReinforcement,
        DepositReinforcement,
        LaunchAirStrike,
        LaunchAirRecon,
        ExecuteAirStrikeAtCurrentHex,
        RunResearchProduction,
        Wait,
        Pass,
    }

    // Typed authorization for the side effects a ground move is deliberately allowed to seek.
    // This is AI intent only: the gameplay movement owner still resolves surprise contacts found
    // after entering fog exactly as the normal game does. Never infer this from Reason text.
    public enum AiGroundMoveAuthority
    {
        Transit,
        // Walks on; may take over a KNOWN undefended structure standing in its way (the game rule
        // destroys/captures it on arrival anyway), never seeks a fight. Approach steps of the
        // combat lanes (Attack approach, gather/return legs).
        TransitCapture,
        Combat,
        CombatAndCapture,
    }

    // ATK §26/§84 — the ONE rule for which authority a single step of a deliberate
    // structure-capture operation carries. Only the TERMINAL step into the target may seek a
    // takeover. Approach steps are TransitCapture (2026-10-02, project owner): they never seek a
    // fight, but the game rule destroys/captures an undefended foreign structure the mover lands on,
    // so a known one on the way is no longer a route blocker (SafeRouteProfile.Combat). Kept beside the enum it decides
    // because it is part of that contract, not a per-lane preference.
    public static class GroundMoveAuthorityPolicy
    {
        public static AiGroundMoveAuthority ForStructureAssaultStep(
            Game.HexGrid.HexCoord step, Game.HexGrid.HexCoord target) =>
            step.Equals(target)
                ? AiGroundMoveAuthority.CombatAndCapture
                : AiGroundMoveAuthority.TransitCapture;

        // ATK §9/§26 — one step of an opportunistic side strike on an enemy FIELD army. Same shape
        // as the assault rule above with one deliberate difference: the terminal step gets Combat,
        // never CombatAndCapture. A diversion exists to destroy an army, so it must not be able to
        // take a structure over — deliberate structure capture belongs to the Attack operation's
        // own target and nothing else.
        public static AiGroundMoveAuthority ForTacticalStrikeStep(
            Game.HexGrid.HexCoord step, Game.HexGrid.HexCoord contact) =>
            step.Equals(contact)
                ? AiGroundMoveAuthority.Combat
                : AiGroundMoveAuthority.TransitCapture;
    }

    public class AiDecision
    {
        public AiActionKind Kind;
        public ArmyData ExistingArmy;
        public HexCoord TargetHex;
        public CardData Card;
        public string Reason;
        public ResourceType? EconomyResourceType;
        public IReadOnlyList<UnitData> AircraftToLaunch;
        public HexCoord AirActionHex;
        public HexCoord AirLandingHex;

        public float Score;

        // Recon may already have prepared visible combat or budgeted optional/required stealth.
        // The shared mover must not override that decision. Other callers retain their existing
        // automatic preparation unless they explicitly opt out.
        public bool AllowAutomaticStealth = true;

        // Transit is deliberately the safe default: a caller must opt into deliberate combat or
        // an undefended-structure takeover. Air movement ignores this ground-only authorization.
        // Every AI air step is transit unless its mission explicitly authorizes an endpoint strike.
        public Game.Aviation.AirStrikePolicy AirStrikePolicy = Game.Aviation.AirStrikePolicy.Transit;

        public AiGroundMoveAuthority GroundMoveAuthority = AiGroundMoveAuthority.Transit;
        public bool AllowsGroundCombat => GroundMoveAuthority == AiGroundMoveAuthority.Combat
            || GroundMoveAuthority == AiGroundMoveAuthority.CombatAndCapture;
        public bool AllowsStructureTakeover => GroundMoveAuthority == AiGroundMoveAuthority.CombatAndCapture
            || GroundMoveAuthority == AiGroundMoveAuthority.TransitCapture;

        public static AiDecision Move(ArmyData army, HexCoord hex, string reason, float score,
            AiGroundMoveAuthority groundMoveAuthority = AiGroundMoveAuthority.Transit) => new AiDecision
        {
            Kind = AiActionKind.MoveArmy, ExistingArmy = army, TargetHex = hex, Reason = reason, Score = score,
            GroundMoveAuthority = groundMoveAuthority,
        };
    }
}

