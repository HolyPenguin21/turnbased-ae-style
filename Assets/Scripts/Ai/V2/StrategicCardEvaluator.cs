using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC CARD EVALUATOR  (Strategy V2 — AI-MGR-01, the key manager task)
    // ===========================================================================================
    //  The ONE shared answer to "how useful is it to play this card now, for what purpose, or is
    //  it better held?". Both StrategicManager phases route through it:
    //    Phase A (FulfillDemands)  — ScoreForDemand: a card chain closing an explicit AxisDemand.
    //    Phase B (UseSurplus)      — ScoreSurplus:  a card chain played proactively with genuinely
    //                                remaining resources.
    //
    //  It replaces the two parallel term sets that used to live in
    //  MaterializationCandidateBuilder (ScorePlanA's cost/fit/quality product and SurplusUtility's
    //  additive sum). One card yields SEVERAL StrategicCardUseCandidate rows — one per reasonable
    //  IntendedRole (Nora x Scout, Nora x CombatBody, Nora x Hold) — never "Nora = hero -> keep".
    //  The Hero card CLASS by itself contributes neither a bonus nor a penalty: a hero's fitness
    //  for a role is read from its real characteristics (HeroRoleEvaluator + capabilities), and
    //  the only hero-specific cost is AlternativeUseValue when a genuinely scarce hero would be
    //  spent on a role that is not its best use.
    //
    //  A radar-DEMAND-INDEPENDENT signal, BaselineForceReadiness, gives ForceGrowthValue to an
    //  ordinary combat body even at AGG = 0 / DEF = 0 — the AI must continuously keep a reasonable
    //  potential for future tasks. It only decides a card is worth MATERIALISING; which army /
    //  garrison it then joins, and stack composition, stay a separate layer (Housekeeping / the
    //  reorg planner), untouched here.
    // ===========================================================================================

    // Derived from capabilities, NOT hard-bound to card class. The full set the spec names; the
    // ones the current capability model can actually distinguish carry a real RoleFit, the rest
    // are declared extension points that resolve to a neutral RoleFit until their mechanics exist
    // (same pattern as CapabilityQualityEvaluator's per-capability switch).
    public enum IntendedRole
    {
        Scout,
        CombatBody,
        MobileCombat,
        AntiArmor,
        AntiAir,
        Aviation,
        Support,
        CapabilitySpecialist,
        Economy,
        Development,
        EquipmentUpgrade,
        ForceGrowth,
        Hold,
    }

    // Diagnostic decomposition of one Card x IntendedUse score. Total is the single authoritative
    // number (the score the manager ranks on); the fields exist so the per-card "why" log line is
    // legible. Never persisted or fed back into a decision.
    public sealed class StrategicUseScoreBreakdown
    {
        public float RoleFit;                 // how well the card's real characteristics fit this role
        public float ImmediateTempo;          // what the AI gains before end of turn
        public float NextTurnPotential;       // what the card practically opens next turn
        public float CapabilityGapValue;      // closes a Recon / AA / AT / air / combat-body deficit
        public float ForceGrowthValue;        // contribution to standing force / future mission capacity (radar-independent)
        public float ThreatResponseValue;     // strategic (optionally omniscient) enemy-composition bias
        public float ResourceEfficiency;      // negative — AP / resource / extra-chain-step drag
        public float SynergyValue;            // equipment on a carrier, trait match, played-unit combination
        public float Deployability;           // negative — cannot really act now / probabilistic deploy
        public float ScarcityValue;           // this card carries a rare capability worth something
        public float RedundancyPenalty;       // negative — the capability is already saturated
        public float AlternativeUseValue;     // negative — opportunity cost of using a versatile card HERE
        public float HoldValue;               // value of deliberately NOT playing it now (informational)
        public float ResourcePressureBenefit; // stranded AP / near-cap resource makes spending now better
        public float HandPressureBenefit;     // a full hand makes materialising now better

        public float Total;

        public string ToCompact()
        {
            string F(float v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            return $"role {F(RoleFit)} tempo {F(ImmediateTempo)} next {F(NextTurnPotential)} "
                 + $"gap {F(CapabilityGapValue)} grow {F(ForceGrowthValue)} threat {F(ThreatResponseValue)} "
                 + $"res {F(ResourceEfficiency)} syn {F(SynergyValue)} deploy {F(Deployability)} "
                 + $"scarce {F(ScarcityValue)} redun {F(RedundancyPenalty)} alt {F(AlternativeUseValue)} "
                 + $"resP {F(ResourcePressureBenefit)} handP {F(HandPressureBenefit)} hold {F(HoldValue)} "
                 + $"= {Total.ToString("0.00", CultureInfo.InvariantCulture)}";
        }
    }

    // Which specialised non-combat executor a card routes to (AI-MGR-01 P0.1). Scoring is shared;
    // dispatch stays specialised.
    public enum NonCombatRole { Aviation, Base, Facility, Equipment }

    public sealed class StrategicCardUseCandidate
    {
        public MaterializationPlan Plan;
        public IntendedRole IntendedRole;
        public HexCoord? TargetContext;
        public StrategicUseScoreBreakdown Breakdown;
        public float TotalUseScore;   // Breakdown.Total
        public float HoldValue;       // scored separately (spec §3)
        public MaterializationQualityBreakdown QualityBreakdown; // scout capability-quality diag, carried through

        // Phase B net: playing now vs leaving it in hand. The manager admits on this.
        public float NetScore => TotalUseScore - Mathf.Max(0f, HoldValue);
    }

    // Radar-demand-INDEPENDENT standing-force signal (spec §4). "The AI state must continuously
    // maintain a reasonable potential for future tasks" — NOT "prepare to attack". High when the
    // fielded force / combat-actor count / capability coverage is thin for the game stage, the
    // economy and the known enemy. Consumed by ForceGrowthValue and by DemandLayer to raise one
    // low-priority FieldCombatPower demand so an ordinary unit gets Phase-A pull, not only surplus.
    internal readonly struct BaselineForceReadiness
    {
        public readonly float Need;          // [0..1]
        public readonly bool HasScout;
        public readonly bool HasFieldBody;
        public readonly bool HasHero;
        public readonly int CombatActors;
        public readonly float FreeFieldPower;

        public BaselineForceReadiness(float need, bool hasScout, bool hasFieldBody, bool hasHero,
            int combatActors, float freeFieldPower)
        {
            Need = need;
            HasScout = hasScout;
            HasFieldBody = hasFieldBody;
            HasHero = hasHero;
            CombatActors = combatActors;
            FreeFieldPower = freeFieldPower;
        }

        public static BaselineForceReadiness Evaluate(WorldSnapshot snap, CapabilityInventory inv)
        {
            if (snap?.Self == null)
                return new BaselineForceReadiness(0f, false, false, false, 0, 0f);

            int combatActors = 0;
            if (snap.Self.Armies != null)
                foreach (ArmySnapshot a in snap.Self.Armies)
                    if (a != null && a.MemberCount > 0 && !a.IsGarrison && !a.IsAir && !a.IsPrison
                        && !a.IsSoloRecce)
                        combatActors++;

            bool hasScout = inv != null && inv.TotalScouts > 0;
            bool hasHero = inv != null && (inv.AvailableHeroes + inv.CommittedHeroes) > 0;
            bool hasFieldBody = combatActors > 0
                || (inv != null && inv.FieldCombatPower > AiConfigV2.allocatorSliceEpsilon);

            float fieldPower = Mathf.Max(0f, snap.Self.FieldPower);
            float freeFieldPower = inv != null ? Mathf.Max(0f, inv.RaidAvailableFieldPower) : fieldPower;
            float enemy = snap.Known != null ? Mathf.Max(0f, snap.Known.EnemyKnownStrength) : 0f;
            float eco = snap.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;

            // "Game stage" — a coarse ramp; more standing force is expected as the game develops.
            float stage = Curves.Ramp(snap.TurnNumber,
                AiConfigV2.baselineReadinessStageRampLo, AiConfigV2.baselineReadinessStageRampHi);

            float targetPower = Mathf.Max(AiConfigV2.baselineReadinessBaseTargetPower,
                                          enemy * AiConfigV2.baselineReadinessEnemyMatchFrac)
                                * Mathf.Lerp(AiConfigV2.baselineReadinessEarlyTargetFrac, 1f, stage);

            float powerGap = Mathf.Clamp01(1f - fieldPower / Mathf.Max(1f, targetPower));
            float actorGap = Mathf.Clamp01(
                1f - combatActors / Mathf.Max(1f, (float)AiConfigV2.baselineReadinessTargetActors));
            int coverMisses = (hasFieldBody ? 0 : 1) + (hasHero ? 0 : 1);
            float coverGap = Mathf.Clamp01(coverMisses / 2f);

            float raw = AiConfigV2.baselineReadinessPowerGapWeight * powerGap
                        + AiConfigV2.baselineReadinessActorGapWeight * actorGap
                        + AiConfigV2.baselineReadinessCoverGapWeight * coverGap;

            // A healthy economy that is not being pressed can afford to run a leaner standing force.
            float need = Mathf.Clamp01(raw) * Mathf.Lerp(1f, AiConfigV2.baselineReadinessSecureDamp, eco);
            return new BaselineForceReadiness(need, hasScout, hasFieldBody, hasHero, combatActors, freeFieldPower);
        }
    }

    internal static class StrategicCardEvaluator
    {
        // -----------------------------------------------------------------------------------------
        //  PHASE A — a chain closing an explicit AxisDemand. The demand pins the primary role.
        // -----------------------------------------------------------------------------------------
        public static StrategicCardUseCandidate ScoreForDemand(MaterializationPlan plan, AxisDemand demand,
            TraitPreference projected, CapabilityInventory inv, int referenceMoveMax,
            bool hasCompetingHeroDemand, WorldSnapshot snap)
        {
            var bd = new StrategicUseScoreBreakdown();
            IntendedRole role = RoleForCapability(demand.Capability, PlanBaseDef(plan));
            BaselineForceReadiness baseline = BaselineForceReadiness.Evaluate(snap, inv);

            // --- backbone: the proven cost / fit / capability-quality product (was ScorePlanA) ---
            float fit = TargetFit(plan.Deploy.Hex, demand.TargetHex);
            float resSum = ResourceCostSum(plan.ResCost);
            float costFactor = 1f + AiConfigV2.stratCardApCostWeight * plan.ApCost
                                  + AiConfigV2.stratChainResCostWeight * resSum;
            float traitMatch = demand.Capability != CapabilityKind.ScoutCapability
                               && (demand.PreferredTraits & TraitPreference.Stealth) != 0
                               && (projected & TraitPreference.Stealth) != 0
                ? AiConfigV2.stratTraitMatchBonus : 0f;

            float core = (1f + traitMatch) * (0.5f + 0.5f * fit) / Mathf.Max(0.0001f, costFactor);
            core += PlacementBonus(plan.Deploy.Kind);

            float genChance = plan.Generation != null
                ? Mathf.Lerp(AiConfigV2.stratChainGenerationChanceFloor, 1f,
                    Mathf.Clamp01(plan.Generation.SuccessChance))
                : 1f;
            core *= genChance;

            float roleMult = CapabilityQualityEvaluator.QualityMultiplier(
                plan, demand, inv, referenceMoveMax, hasCompetingHeroDemand,
                out MaterializationQualityBreakdown qbd);
            core *= roleMult;

            bd.RoleFit = core;
            bd.ImmediateTempo = traitMatch + PlacementBonus(plan.Deploy.Kind);
            bd.Deployability = -(1f - genChance);
            bd.ResourceEfficiency = -(AiConfigV2.stratCardApCostWeight * plan.ApCost
                                      + AiConfigV2.stratChainResCostWeight * resSum) - ChainStepPenalty(plan.Kind);

            // --- spec additive terms on top of the backbone ---
            bd.ForceGrowthValue = ForceGrowthValue(plan, demand.Capability, baseline);
            bd.CapabilityGapValue = CapabilityGapValue(role, demand.Capability, inv, baseline);
            bd.ThreatResponseValue = ThreatResponseValue(role, snap);
            bd.NextTurnPotential = NextTurnPotential(plan, role);
            bd.SynergyValue = SynergyValue(plan);
            bd.RedundancyPenalty = -(GarrisonSaturationPenalty(plan, demand, snap)
                                     + ScoutOversupplyPenalty(role, inv));
            bd.AlternativeUseValue = -ScarcityOpportunityCost(plan, demand, inv);
            bd.ScarcityValue = 0f;                 // Phase A is closing an explicit demand
            bd.ResourcePressureBenefit = 0f;       // Phase A spends a ledger entitlement, not stranded AP
            bd.HandPressureBenefit = 0f;

            float total = bd.RoleFit
                          + bd.ForceGrowthValue + bd.CapabilityGapValue + bd.ThreatResponseValue
                          + bd.NextTurnPotential + bd.SynergyValue
                          - ChainStepPenalty(plan.Kind)
                          + bd.RedundancyPenalty + bd.AlternativeUseValue;
            bd.Total = total;

            float hold = HoldValue(plan, role, inv, snap, baseline, surplus: false);
            bd.HoldValue = hold;

            return new StrategicCardUseCandidate
            {
                Plan = plan,
                IntendedRole = role,
                TargetContext = demand.TargetHex,
                Breakdown = bd,
                TotalUseScore = total,
                HoldValue = hold,           // informational in Phase A (a live demand outranks holding)
                QualityBreakdown = qbd,
            };
        }

        // -----------------------------------------------------------------------------------------
        //  PHASE B — proactive surplus. One card -> several IntendedRole candidates; the best
        //  NetScore (play value minus hold value) is returned, with the winning role.
        // -----------------------------------------------------------------------------------------
        public static StrategicCardUseCandidate ScoreSurplus(MaterializationPlan plan, CapabilityInventory inv,
            bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected, WorldSnapshot snap)
        {
            CardDefinition def = PlanBaseDef(plan);
            BaselineForceReadiness baseline = BaselineForceReadiness.Evaluate(snap, inv);
            IReadOnlyList<IntendedRole> roles = DeriveRoles(def, projected, plan, recce, hero);

            StrategicCardUseCandidate best = null;
            foreach (IntendedRole role in roles)
            {
                StrategicCardUseCandidate c = ScoreSurplusRole(plan, role, inv, recce, hero, hand,
                    projected, snap, baseline);
                if (best == null || c.NetScore > best.NetScore
                    || (Mathf.Approximately(c.NetScore, best.NetScore) && (int)c.IntendedRole < (int)best.IntendedRole))
                    best = c;
            }
            return best ?? ScoreSurplusRole(plan, IntendedRole.CombatBody, inv, recce, hero, hand,
                projected, snap, baseline);
        }

        private static StrategicCardUseCandidate ScoreSurplusRole(MaterializationPlan plan, IntendedRole role,
            CapabilityInventory inv, bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected,
            WorldSnapshot snap, BaselineForceReadiness baseline)
        {
            var bd = new StrategicUseScoreBreakdown();
            float resSum = ResourceCostSum(plan.ResCost);

            float scarcity = SurplusScarcity(inv, recce, hero);
            float versatility = hero ? AiConfigV2.surplusHeroVersatility : AiConfigV2.surplusUnitVersatility;
            float traits = projected != null && AbilityParams.AbilitiesHaveAnyStealth(projected)
                ? AiConfigV2.stratTraitMatchBonus : 0f;
            float recurringAp = projected != null && projected.Contains(UnitAbilities.ApBonus)
                ? AiConfigV2.surplusRecurringApIncomeBonus : 0f;
            float handPressure = hand != null && !hand.HasFreeSlot ? AiConfigV2.surplusHandPressureBonus : 0f;
            float equipmentUpgrade = plan.UsesEquipment ? EquipmentUpgradeUtility(plan) : 0f;
            float readiness = recce ? 0f : SurplusCombatReadinessUtility(plan);

            // RoleFit: a real, characteristic-driven number per role. Scout uses the wired capability
            // -quality profile; EquipmentUpgrade uses the projected stat delta; Hero uses the
            // canonical combat-leadership score (NOT a flat hero bonus); others fall back to the
            // generic versatility of the card class.
            float roleFit;
            switch (role)
            {
                case IntendedRole.Scout:
                    roleFit = versatility + scarcity;
                    break;
                case IntendedRole.EquipmentUpgrade:
                    roleFit = equipmentUpgrade;
                    break;
                case IntendedRole.CombatBody:
                case IntendedRole.ForceGrowth:
                case IntendedRole.MobileCombat:
                case IntendedRole.AntiArmor:
                case IntendedRole.AntiAir:
                    roleFit = readiness + HeroLeadershipFit(plan, hero);
                    break;
                case IntendedRole.Support:
                    roleFit = recurringAp + HeroSupportFit(plan, hero);
                    break;
                case IntendedRole.Hold:
                    roleFit = 0f;
                    break;
                default:
                    roleFit = versatility;
                    break;
            }
            bd.RoleFit = roleFit;

            bd.ImmediateTempo = traits + recurringAp + PlacementBonus(plan.Deploy.Kind);
            bd.NextTurnPotential = NextTurnPotential(plan, role);
            bd.CapabilityGapValue = scarcity;
            bd.ForceGrowthValue = role == IntendedRole.Scout || role == IntendedRole.Hold
                ? 0f
                : ForceGrowthValue(plan, plan.FinalCapability, baseline);
            bd.ThreatResponseValue = ThreatResponseValue(role, snap);
            bd.SynergyValue = traits + equipmentUpgrade
                              - (plan.Kind == MaterializationChainKind.AttachDeploy
                                 ? AiConfigV2.stratChainAttachStepPenalty : 0f);
            bd.Deployability = plan.Generation != null
                ? -(1f - Mathf.Lerp(AiConfigV2.stratChainGenerationChanceFloor, 1f,
                    Mathf.Clamp01(plan.Generation.SuccessChance)))
                : 0f;
            bd.ScarcityValue = scarcity;
            bd.RedundancyPenalty = -ScoutOversupplyPenalty(role, inv);
            bd.AlternativeUseValue = -SurplusAlternativeUseCost(plan, role, inv, hero);
            bd.ResourceEfficiency = -(AiConfigV2.surplusApCostWeight * plan.ApCost
                                      + AiConfigV2.surplusResourceCostWeight * resSum)
                                    - ChainStepPenalty(plan.Kind);
            bd.ResourcePressureBenefit = 0f;   // SurplusAdmissionPolicy owns the real stranded-AP relaxation
            bd.HandPressureBenefit = handPressure;

            float total = bd.RoleFit
                          + bd.ImmediateTempo + bd.NextTurnPotential + bd.CapabilityGapValue
                          + bd.ForceGrowthValue + bd.ThreatResponseValue + bd.SynergyValue
                          + bd.HandPressureBenefit
                          + bd.Deployability + bd.RedundancyPenalty + bd.AlternativeUseValue
                          + bd.ResourceEfficiency
                          + PlacementBonus(plan.Deploy.Kind);
            bd.Total = total;

            float hold = HoldValue(plan, role, inv, snap, baseline, surplus: true);
            bd.HoldValue = hold;

            return new StrategicCardUseCandidate
            {
                Plan = plan,
                IntendedRole = role,
                Breakdown = bd,
                TotalUseScore = total,
                HoldValue = hold,
            };
        }

        // -----------------------------------------------------------------------------------------
        //  NON-COMBAT CARDS  (Aviation / Base / Facility / standalone Equipment) — AI-MGR-01 P0.1.
        //  These used to rank on NonCombatCardPlayer's own fixed 55/45/40/24 scale, incomparable
        //  with every Unit/Hero chain. They now produce a StrategicCardUseCandidate on the SAME
        //  breakdown / NetScore as everything else; the specialised executors (BuildingPlayExecutor
        //  / AviationActions / EquipmentSystem) are unchanged. Demand-independent by nature — value
        //  comes from capability coverage, economy standing and hand/AP pressure, not a live demand.
        // -----------------------------------------------------------------------------------------
        public static StrategicCardUseCandidate ScoreNonCombat(NonCombatRole kind, CardData card,
            WorldSnapshot snap, CapabilityInventory inv, AiHandData hand, float bestEquipmentUpgrade)
        {
            var bd = new StrategicUseScoreBreakdown();
            CardDefinition def = card?.Definition;
            IntendedRole role;
            float apCost = card != null ? card.EffectivePlayApCost : 0f;
            float resSum = card != null ? ResourceCostSum(card.EffectivePlayResourceCost) : 0f;

            float eco = snap?.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;
            int ownBases = snap?.Self?.BaseHexes != null ? snap.Self.BaseHexes.Count : 1;
            bool hasAirCapacity = snap?.Self != null
                && (snap.Self.AirborneReconWings + snap.Self.SpareAirObservationSorties) > 0;

            switch (kind)
            {
                case NonCombatRole.Aviation:
                    role = IntendedRole.Aviation;
                    bd.RoleFit = AiConfigV2.nonCombatAviationBaseValue;
                    bd.CapabilityGapValue = hasAirCapacity ? 0f : AiConfigV2.nonCombatAviationNoAirGap;
                    bd.NextTurnPotential = AiConfigV2.nextTurnActorPotential; // a stored aircraft is launchable next turn
                    break;
                case NonCombatRole.Base:
                    role = IntendedRole.Economy;
                    bd.RoleFit = AiConfigV2.nonCombatBaseValue
                                 + (1f - eco) * AiConfigV2.nonCombatEconomyRunwayBonus;
                    bd.CapabilityGapValue = ownBases <= 1 ? AiConfigV2.nonCombatFewBasesGap : 0f;
                    bd.NextTurnPotential = AiConfigV2.nextTurnActorPotential;
                    break;
                case NonCombatRole.Facility:
                    role = FacilityRole(def);
                    bd.RoleFit = AiConfigV2.nonCombatFacilityValue
                                 + (1f - eco) * AiConfigV2.nonCombatEconomyRunwayBonus;
                    break;
                default: // Equipment (standalone)
                    role = IntendedRole.EquipmentUpgrade;
                    bd.RoleFit = Mathf.Max(AiConfigV2.nonCombatEquipmentValueFloor, bestEquipmentUpgrade);
                    break;
            }

            bd.HandPressureBenefit = hand != null && !hand.HasFreeSlot ? AiConfigV2.surplusHandPressureBonus : 0f;
            bd.ResourceEfficiency = -(AiConfigV2.surplusApCostWeight * apCost
                                      + AiConfigV2.surplusResourceCostWeight * resSum);

            float total = bd.RoleFit + bd.CapabilityGapValue + bd.NextTurnPotential
                          + bd.HandPressureBenefit + bd.ResourceEfficiency;
            bd.Total = total;

            // Non-combat cards carry little unique-future-role value (a facility / aircraft is as
            // playable next turn); a full hand still argues against holding.
            float hold = hand != null && !hand.HasFreeSlot ? 0f : AiConfigV2.holdLostTempoPenalty * 0.5f;
            bd.HoldValue = hold;

            return new StrategicCardUseCandidate
            {
                Plan = null,
                IntendedRole = role,
                Breakdown = bd,
                TotalUseScore = total,
                HoldValue = hold,
            };
        }

        private static IntendedRole FacilityRole(CardDefinition def)
        {
            IReadOnlyList<string> ab = def?.grantedAbilities;
            if (ab != null && (ab.Contains(UnitAbilities.Research) || ab.Contains(UnitAbilities.Production)))
                return IntendedRole.Development;
            return IntendedRole.Economy;
        }

        // =======================================================================================
        //  SPEC TERMS
        // =======================================================================================

        // Even at AGG = 0 / DEF = 0 an ordinary combat body is worth materialising as standing
        // force. Marginal AiPower contribution, scaled by BaselineForceReadiness.Need so it fades
        // once the force is deep enough for the stage. Never applies to Scout / infra capabilities.
        private static float ForceGrowthValue(MaterializationPlan plan, CapabilityKind cap,
            BaselineForceReadiness baseline)
        {
            if (cap != CapabilityKind.FieldCombatPower && cap != CapabilityKind.GarrisonCombatPower
                && cap != CapabilityKind.Hero)
                return 0f;
            float marginal = SurplusCombatReadinessUtility(plan);
            if (marginal <= 0f)
                return 0f;
            float scale = Mathf.Lerp(AiConfigV2.baselineReadinessGrowthFloor, 1f,
                Mathf.Clamp01(baseline.Need));
            return marginal * scale * AiConfigV2.forceGrowthValueWeight;
        }

        // A card closing a capability the AI currently lacks entirely is worth a substantial bonus.
        private static float CapabilityGapValue(IntendedRole role, CapabilityKind cap,
            CapabilityInventory inv, BaselineForceReadiness baseline)
        {
            if (inv == null)
                return 0f;
            switch (cap)
            {
                case CapabilityKind.ScoutCapability:
                    return inv.TotalScouts <= 0 ? AiConfigV2.capabilityGapValue : 0f;
                case CapabilityKind.Hero:
                    return (inv.AvailableHeroes + inv.CommittedHeroes) <= 0
                        ? AiConfigV2.capabilityGapValue : 0f;
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    if (!baseline.HasFieldBody)
                        return AiConfigV2.capabilityGapValue;
                    return baseline.Need >= AiConfigV2.baselineReadinessDemandMinNeed
                        ? AiConfigV2.capabilityGapValue * 0.5f * baseline.Need : 0f;
                default:
                    return 0f;
            }
        }

        // Strategic bias from (optionally omniscient) enemy composition — a large enemy armour
        // group raises the value of an AntiArmor body; a strong enemy air arm raises AntiAir. A
        // hidden army is a DIRECTIONAL bias only; it never becomes normal AI intel here.
        private static float ThreatResponseValue(IntendedRole role, WorldSnapshot snap)
        {
            if (snap == null || (role != IntendedRole.AntiArmor && role != IntendedRole.AntiAir))
                return 0f;
            IReadOnlyList<ArmySnapshot> enemies = snap.TrueWorld?.EnemyArmies;
            if (enemies == null || enemies.Count == 0)
                return 0f;

            float armour = 0f, air = 0f;
            foreach (ArmySnapshot a in enemies)
            {
                if (a == null)
                    continue;
                if (a.IsAir)
                    air += a.EffectiveArmyPower;
                else
                    armour += a.EffectiveArmyPower * 0.35f; // coarse — no per-unit type in the snapshot
            }
            float driver = role == IntendedRole.AntiAir ? air : armour;
            return Mathf.Clamp(driver / Mathf.Max(1f, AiConfigV2.threatResponseNorm), 0f, 1f)
                   * AiConfigV2.threatResponseValueWeight;
        }

        // What the card practically opens NEXT turn — a fresh independent actor (new army / shell),
        // a prepared body that still needs an escort, aviation readiness.
        private static float NextTurnPotential(MaterializationPlan plan, IntendedRole role)
        {
            if (plan == null)
                return 0f;
            float v = 0f;
            if (plan.Deploy.Kind == DeploymentKind.NewArmy || plan.Deploy.Kind == DeploymentKind.ReusableShell)
                v += AiConfigV2.nextTurnActorPotential;
            if (role == IntendedRole.Scout && plan.Deploy.Kind != DeploymentKind.Garrison)
                v += AiConfigV2.nextTurnActorPotential * 0.5f;
            return v;
        }

        private static float SynergyValue(MaterializationPlan plan)
        {
            if (plan == null)
                return 0f;
            float v = 0f;
            if (plan.UsesEquipment)
                v += EquipmentUpgradeUtility(plan);
            if ((plan.ExpectedTraits & TraitPreference.Stealth) != 0)
                v += AiConfigV2.stratTraitMatchBonus * 0.5f;
            if (plan.Deploy.Kind == DeploymentKind.ExistingArmy)
                v += AiConfigV2.stratPlacementExistingArmyBonus * 0.5f;
            return v;
        }

        // Spec §3 — scored SEPARATELY. The AI may deliberately keep a card, but as the RESULT of
        // an evaluation, not the absence of a current demand.
        //   HoldValue = UniqueFutureRole + NearTermExpectedDemand + ScarcityValue
        //             - HandPressure - ResourcePressure - LostTempo
        private static float HoldValue(MaterializationPlan plan, IntendedRole role, CapabilityInventory inv,
            WorldSnapshot snap, BaselineForceReadiness baseline, bool surplus)
        {
            if (plan == null)
                return 0f;
            CardDefinition def = PlanBaseDef(plan);

            float uniqueFutureRole = 0f;
            // A stealth-capable body is a rare option worth keeping when we hold at most one.
            if ((plan.ExpectedTraits & TraitPreference.Stealth) != 0
                && inv != null && inv.StealthScouts <= AiConfigV2.stratChainStealthScarceAt)
                uniqueFutureRole += AiConfigV2.holdUniqueRoleValue;
            // A hero whose real vocation is support, while we already field a combat leader.
            if (def != null && def.cardType == CardType.Hero && PlanHeroIsSupport(plan)
                && inv != null && inv.AvailableHeroes + inv.CommittedHeroes > 0)
                uniqueFutureRole += AiConfigV2.holdUniqueRoleValue;

            float nearTermDemand = 0f;
            if ((role == IntendedRole.CombatBody || role == IntendedRole.ForceGrowth
                 || role == IntendedRole.MobileCombat || role == IntendedRole.Support)
                && baseline.Need < AiConfigV2.baselineReadinessDemandMinNeed)
                nearTermDemand -= 0f; // no pending readiness need -> nothing extra to hold FOR

            float scarcityValue = inv != null && SurplusScarcity(inv,
                    AbilityParams.AbilitiesHaveAnyRecce(plan.ProjectedAbilities ?? def?.grantedAbilities),
                    def != null && def.cardType == CardType.Hero) >= AiConfigV2.surplusScarcityMed
                ? AiConfigV2.holdScarcityValue : 0f;

            float handPressure = 0f;
            if (snap?.Self != null && !snap.Self.HasFreeHandSlot)
                handPressure = AiConfigV2.holdHandPressurePenalty;

            float resourcePressure = 0f; // Phase B already runs after mission spend; stranded AP is a play signal, not a hold one
            float lostTempo = surplus ? AiConfigV2.holdLostTempoPenalty : 0f;

            return Mathf.Max(0f,
                uniqueFutureRole + nearTermDemand + scarcityValue
                - handPressure - resourcePressure - lostTempo);
        }

        // =======================================================================================
        //  ROLE DERIVATION
        // =======================================================================================
        private static IntendedRole RoleForCapability(CapabilityKind cap, CardDefinition def)
        {
            switch (cap)
            {
                case CapabilityKind.ScoutCapability: return IntendedRole.Scout;
                case CapabilityKind.Hero: return IntendedRole.CombatBody;
                case CapabilityKind.GarrisonCombatPower: return IntendedRole.CombatBody;
                case CapabilityKind.FieldCombatPower: return IntendedRole.CombatBody;
                case CapabilityKind.EconomicInfrastructure: return IntendedRole.Economy;
                case CapabilityKind.DevelopmentInfrastructure: return IntendedRole.Development;
                default: return IntendedRole.CombatBody;
            }
        }

        // Not a switch over card names — roles come from capabilities / abilities / chain shape.
        private static IReadOnlyList<IntendedRole> DeriveRoles(CardDefinition def,
            IReadOnlyList<string> abilities, MaterializationPlan plan, bool recce, bool hero)
        {
            var roles = new List<IntendedRole>();
            if (recce)
                roles.Add(IntendedRole.Scout);
            if (hero || (def != null && def.cardType == CardType.Unit))
            {
                roles.Add(IntendedRole.CombatBody);
                roles.Add(IntendedRole.ForceGrowth);
            }
            if (abilities != null)
            {
                if (abilities.Contains(UnitAbilities.AntiAir))
                    roles.Add(IntendedRole.AntiAir);
                if (abilities.Contains(UnitAbilities.Hyperkinetic))
                    roles.Add(IntendedRole.AntiArmor);
                if (abilities.Contains(UnitAbilities.ApBonus)
                    || abilities.Contains(UnitAbilities.Researcher)
                    || abilities.Contains(UnitAbilities.Assembler))
                    roles.Add(IntendedRole.Support);
            }
            if (def != null && def.moveMax >= AiConfigV2.mobileCombatMoveMax && !recce)
                roles.Add(IntendedRole.MobileCombat);
            if (plan != null && plan.UsesEquipment)
                roles.Add(IntendedRole.EquipmentUpgrade);
            roles.Add(IntendedRole.Hold);
            return roles.Distinct().ToList();
        }

        // =======================================================================================
        //  HERO FITNESS  — real characteristics only, no flat class bonus/penalty (spec §2 RoleFit)
        // =======================================================================================
        // A hand card is a CardDefinition, not a spawned UnitData, so the combat-leadership merit is
        // read straight off the definition with the SAME formula HeroRoleEvaluator.CombatLeadershipScore
        // uses on a live hero: CommandRating (leadership capacity) + the hero's own AiPower
        // contribution. No flat hero class bonus — a weak hero scores low here.
        private static float HeroLeadershipScore(CardDefinition def)
        {
            if (def == null || def.cardType != CardType.Hero)
                return 0f;
            return def.commandRating * AiConfigV2.heroRoleCommandWeight
                 + AiPower.ToPowerUnit(def).BasePower * AiConfigV2.heroRoleCombatContributionWeight;
        }

        private static bool HeroHasSupportVocation(CardDefinition def) =>
            def != null && def.cardType == CardType.Hero && def.grantedAbilities != null
            && (def.grantedAbilities.Contains(UnitAbilities.Researcher)
                || def.grantedAbilities.Contains(UnitAbilities.Assembler));

        private static float HeroLeadershipFit(MaterializationPlan plan, bool hero)
        {
            if (!hero)
                return 0f;
            CardDefinition def = PlanBaseDef(plan);
            return Mathf.Clamp(
                HeroLeadershipScore(def) / Mathf.Max(1f, AiConfigV2.heroLeadershipFitNorm),
                0f, AiConfigV2.heroLeadershipFitCap);
        }

        private static float HeroSupportFit(MaterializationPlan plan, bool hero)
        {
            if (!hero)
                return 0f;
            return HeroHasSupportVocation(PlanBaseDef(plan)) ? AiConfigV2.heroSupportFitValue : 0f;
        }

        // SupportOperator == a support vocation AND combat-leadership below the flexible floor
        // (parity with HeroRoleEvaluator.Classify, evaluated off the definition).
        private static bool PlanHeroIsSupport(MaterializationPlan plan)
        {
            CardDefinition def = PlanBaseDef(plan);
            return HeroHasSupportVocation(def)
                && HeroLeadershipScore(def) < AiConfigV2.heroRoleFlexibleCombatFloor;
        }

        // =======================================================================================
        //  PORTED PRIMITIVES  (moved here from MaterializationCandidateBuilder so the scoring math
        //  lives with the evaluator; feasibility / chain-construction stays in the builder).
        // =======================================================================================
        internal static float SurplusCombatReadinessUtility(MaterializationPlan p)
        {
            CardDefinition def = p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
            if (def == null || def.isAviation
                || (def.cardType != CardType.Unit && def.cardType != CardType.Hero))
                return 0f;

            AiPower.PowerUnit incoming = AiPower.ToPowerUnit(def);
            float marginal = incoming.BasePower;
            ArmyData dest = p.Deploy.Army;
            if (p.Deploy.Kind == DeploymentKind.ExistingArmy
                && dest != null && !dest.IsGarrison && dest.Members != null && dest.Members.Count > 0)
            {
                List<AiPower.PowerUnit> before = dest.Members
                    .Where(u => u != null && !u.IsAviation)
                    .Select(AiPower.ToPowerUnit)
                    .ToList();
                float oldPower = AiPower.EffectiveArmyPower(before);
                before.Add(incoming);
                float newPower = AiPower.EffectiveArmyPower(before);
                marginal = Mathf.Max(incoming.BasePower * 0.25f, newPower - oldPower);
            }
            return Mathf.Clamp(marginal / Mathf.Max(1f, AiConfigV2.defencePerBodyPowerEstimate), 0f, 2f);
        }

        internal static float EquipmentUpgradeUtility(MaterializationPlan p)
        {
            CardDefinition host = p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
            CardDefinition eq = p?.GeneratedEquipmentDef ?? p?.EquipmentInHand?.Definition;
            EquipmentGrant grant = eq?.equipment;
            if (host == null || grant == null)
                return 0f;

            var before = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = host.attack,
                [EquipmentStat.Defense] = host.defenseRating,
                [EquipmentStat.Resistance] = host.resistanceRating,
                [EquipmentStat.Range] = host.range,
                [EquipmentStat.HitPoints] = host.hitPoints,
                [EquipmentStat.MoveMax] = host.moveMax,
                [EquipmentStat.Initiative] = host.initiative,
                [EquipmentStat.ActivationApCost] = host.activationApCost,
                [EquipmentStat.CommandRating] = host.commandRating,
                [EquipmentStat.Fate] = host.fate,
            };
            PredictedEquipmentState predicted = EquipmentSystem.Predict(grant, before, host.grantedAbilities);

            int After(EquipmentStat stat) =>
                predicted.Stats != null && predicted.Stats.TryGetValue(stat, out int value) ? value : before[stat];

            float combatDelta =
                Mathf.Max(0, After(EquipmentStat.Attack) - before[EquipmentStat.Attack]) * AiConfigV2.powerAttackWeight
                + Mathf.Max(0, After(EquipmentStat.Defense) - before[EquipmentStat.Defense]) * AiConfigV2.powerDefenseWeight
                + Mathf.Max(0, After(EquipmentStat.HitPoints) - before[EquipmentStat.HitPoints]) * AiConfigV2.powerHitPointsWeight
                + Mathf.Max(0, After(EquipmentStat.Initiative) - before[EquipmentStat.Initiative]) * AiConfigV2.powerInitiativeWeight
                + Mathf.Max(0, After(EquipmentStat.Resistance) - before[EquipmentStat.Resistance]) * AiConfigV2.powerResistanceWeight;
            if (host.cardType == CardType.Hero)
                combatDelta += Mathf.Max(0, After(EquipmentStat.Fate) - before[EquipmentStat.Fate])
                               * AiConfigV2.powerHeroFateWeight;

            float tactical = 0f;
            tactical += Mathf.Max(0, After(EquipmentStat.MoveMax) - before[EquipmentStat.MoveMax]) * 0.20f;
            tactical += Mathf.Max(0, After(EquipmentStat.Range) - before[EquipmentStat.Range]) * 0.15f;
            tactical += Mathf.Max(0, before[EquipmentStat.ActivationApCost] - After(EquipmentStat.ActivationApCost)) * 0.25f;
            tactical += Mathf.Max(0, After(EquipmentStat.CommandRating) - before[EquipmentStat.CommandRating]) * 0.15f;

            int addedAbilities = 0;
            if (predicted.Abilities != null)
                foreach (string a in predicted.Abilities)
                    if (host.grantedAbilities == null || !host.grantedAbilities.Contains(a))
                        addedAbilities++;
            tactical += addedAbilities * 0.15f;

            return Mathf.Clamp(combatDelta / Mathf.Max(1f, AiConfigV2.defencePerBodyPowerEstimate) + tactical,
                0f, 1.5f);
        }

        internal static float SurplusScarcity(CapabilityInventory inv, bool recce, bool hero)
        {
            if (inv == null) return AiConfigV2.surplusScarcityLow;
            if (recce)
            {
                if (inv.TotalScouts <= 0) return AiConfigV2.surplusScarcityHigh;
                if (inv.ReadyScouts + inv.ReserveScouts <= 1) return AiConfigV2.surplusScarcityMed;
                return AiConfigV2.surplusScarcityLow;
            }
            if (hero) return inv.AvailableHeroes <= 0 ? AiConfigV2.surplusScarcityMed : AiConfigV2.surplusScarcityLow;
            return AiConfigV2.surplusScarcityLow;
        }

        // Deterministic garrison saturation / composition-diversity penalty for a GarrisonCombatPower
        // demand landing in an EXISTING garrison / defensive army (spec §9, §10).
        internal static float GarrisonSaturationPenalty(MaterializationPlan p, AxisDemand demand, WorldSnapshot snap)
        {
            if (demand == null || demand.Capability != CapabilityKind.GarrisonCombatPower)
                return 0f;
            ArmyData dest = p?.Deploy.Army;
            if (dest == null)
                return 0f;

            int members = dest.Members?.Count ?? 0;
            float penalty = AiConfigV2.garrisonCrowdingPenaltyPerMember * members;

            float destPower = 0f;
            if (snap?.Self?.Armies != null)
                foreach (ArmySnapshot a in snap.Self.Armies)
                    if (a != null && a.ArmyId == dest.Id) { destPower = a.EffectiveArmyPower; break; }
            if (demand.RequiredCapabilityPower > 0f && destPower >= demand.RequiredCapabilityPower)
                penalty += AiConfigV2.garrisonSaturatedPenalty;

            if (PrimaryTypeDominates(p, dest))
                penalty += AiConfigV2.garrisonDuplicateTypePenalty;

            return penalty;
        }

        private static bool PrimaryTypeDominates(MaterializationPlan p, ArmyData dest)
        {
            CardDefinition def = p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
            if (def?.unitTypeTags == null || def.unitTypeTags.Count == 0 || dest?.Members == null)
                return false;
            UnitTypeTag primary = def.unitTypeTags[0];
            int nonHero = 0, sharing = 0;
            foreach (UnitData m in dest.Members)
            {
                if (m == null || m.IsHero) continue;
                nonHero++;
                if (m.TypeTags != null && m.TypeTags.Contains(primary)) sharing++;
            }
            return nonHero > 0 && sharing * 2 >= nonHero;
        }

        // Opportunity cost of spending a card whose real value is in a DIFFERENT direction — a
        // scarce stealth item on a non-stealth demand, or a genuinely scarce hero body on a
        // non-hero, non-scout demand. NOT a flat hero penalty (spec §2).
        internal static float ScarcityOpportunityCost(MaterializationPlan p, AxisDemand demand, CapabilityInventory inv)
        {
            float cost = 0f;
            if (demand.Capability != CapabilityKind.ScoutCapability
                && (demand.RequiredTraits & TraitPreference.Stealth) == 0)
            {
                bool consumesExistingStealth =
                    (p.BaseCardInHand != null && CardCarriesStealth(p.BaseCardInHand))
                    || (p.EquipmentInHand?.Definition?.equipment != null
                        && GrantAddsStealth(p.EquipmentInHand.Definition.equipment));
                if (consumesExistingStealth
                    && !(inv != null && inv.StealthScouts > AiConfigV2.stratChainStealthScarceAt))
                    cost += AiConfigV2.stratChainScarcityPenalty;
            }
            if (demand.Capability != CapabilityKind.Hero && demand.Capability != CapabilityKind.ScoutCapability)
            {
                CardDefinition baseDef = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                if (baseDef != null && baseDef.cardType == CardType.Hero
                    && inv != null && inv.AvailableHeroes <= AiConfigV2.stratChainHeroScarceAt)
                    cost += AiConfigV2.stratChainHeroScarcityPenalty;
            }
            return cost;
        }

        private static float SurplusAlternativeUseCost(MaterializationPlan p, IntendedRole role,
            CapabilityInventory inv, bool hero)
        {
            float cost = 0f;
            // A scarce hero spent on a plain combat body while none is free elsewhere.
            if (hero && role != IntendedRole.Support && role != IntendedRole.Scout
                && inv != null && inv.AvailableHeroes <= AiConfigV2.stratChainHeroScarceAt)
                cost += AiConfigV2.stratChainHeroScarcityPenalty;
            // A stealth-capable body burned into a non-scout combat role while stealth is scarce.
            if (role != IntendedRole.Scout
                && (p.ExpectedTraits & TraitPreference.Stealth) != 0
                && inv != null && inv.StealthScouts <= AiConfigV2.stratChainStealthScarceAt)
                cost += AiConfigV2.stratChainScarcityPenalty;
            return cost;
        }

        private static float ScoutOversupplyPenalty(IntendedRole role, CapabilityInventory inv)
        {
            if (role != IntendedRole.Scout || inv == null)
                return 0f;
            return inv.ReadyScouts + inv.ReserveScouts >= AiConfigV2.surplusScoutOversupplyAt
                ? AiConfigV2.surplusOversupplyPenalty : 0f;
        }

        private static bool CardCarriesStealth(CardData c) =>
            c?.Definition != null
            && AbilityParams.AbilitiesHaveAnyStealth(EffAbilities(c.Definition, c.Equipment));

        private static bool GrantAddsStealth(EquipmentGrant grant) =>
            grant?.addAbilities != null && grant.addAbilities.Any(a => AbilityParams.TryGetStealthLevel(a, out _));

        private static IReadOnlyList<string> EffAbilities(CardDefinition def, CardDefinition attachedEquipment)
        {
            var baseList = def?.grantedAbilities != null ? new List<string>(def.grantedAbilities) : new List<string>();
            if (attachedEquipment?.equipment == null) return baseList;
            return EquipmentSystem.EffectiveAbilities(baseList, attachedEquipment.equipment);
        }

        // --- tiny shared helpers (copied verbatim from the builder's private set) ---
        private static float ResourceCostSum(ResourceCost c) => c == null
            ? 0f : c.human + c.energy + c.materials + c.tech;

        private static float ChainStepPenalty(MaterializationChainKind k)
        {
            switch (k)
            {
                case MaterializationChainKind.AttachDeploy: return AiConfigV2.stratChainAttachStepPenalty;
                case MaterializationChainKind.GenerateDeploy: return AiConfigV2.stratChainGenerationStepPenalty;
                case MaterializationChainKind.GenerateAttachDeploy:
                    return AiConfigV2.stratChainAttachStepPenalty + AiConfigV2.stratChainGenerationStepPenalty;
                default: return 0f;
            }
        }

        private static float PlacementBonus(DeploymentKind k)
        {
            switch (k)
            {
                case DeploymentKind.Garrison: return AiConfigV2.stratPlacementGarrisonBonus;
                case DeploymentKind.ExistingArmy: return AiConfigV2.stratPlacementExistingArmyBonus;
                case DeploymentKind.ReusableShell: return AiConfigV2.stratPlacementReusableShellBonus;
                default: return 0f;
            }
        }

        private static float TargetFit(HexCoord deployHex, HexCoord? target)
        {
            if (!target.HasValue) return 0.5f;
            int d = HexGridMath.Distance(deployHex, target.Value);
            return Mathf.Clamp01(1f - d / Mathf.Max(1f, (float)AiConfigV2.stratTargetFitRange));
        }

        private static CardDefinition PlanBaseDef(MaterializationPlan p) =>
            p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
    }
}
