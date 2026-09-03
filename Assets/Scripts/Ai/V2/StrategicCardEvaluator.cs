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
    //    Non-combat lane           — ScoreNonCombat: Aviation / Base / Facility / standalone
    //                                Equipment, same NetScore band, specialised executors.
    //
    //  Review follow-up invariants (AI-MGR-01, 2026-09-04):
    //   * P1.4 — the score is FINAL. Every factor (placement, AP/resource cost, extra-chain-step
    //     penalty, generation probability, garrison-surplus correction) is applied EXACTLY ONCE,
    //     inside this file. Breakdown.Total == the ranked value; callers do not re-adjust it.
    //   * P1.5 — one RoleFit path per role, called by both phases. The Hero card CLASS adds no flat
    //     bonus/penalty; "versatility" is derived from how many real viable roles the card has,
    //     not from being a Hero. Phase B Scout gets the SAME CapabilityQualityEvaluator profile as
    //     Phase A (via a neutral synthetic scout demand).
    //   * P1.6 — AlternativeUseValue is the real opportunity cost of using the card HERE instead of
    //     its best other role / Hold. NearTermExpectedDemand is a real Hold term.
    //   * P1.7 — BaselineForceReadiness.Need feeds ForceGrowthValue ONCE; it is not also folded
    //     into the demand's Value or CapabilityGapValue.
    //   * P2.8 / review-r4 finding 5 — no SYNTHETIC armour, but the cheat path is live: AntiAir and
    //     AntiArmor both take a directional ThreatResponseValue off omniscient TrueWorld composition
    //     (real IsAir / real Armored-tagged member). It never becomes normal AI intel.
    //
    //  BaselineForceReadiness is radar-DEMAND-INDEPENDENT: it gives ForceGrowthValue to an ordinary
    //  combat body even at AGG = 0 / DEF = 0. It only decides a card is worth MATERIALISING; which
    //  army/garrison it joins and stack composition stay a separate layer (Housekeeping).
    // ===========================================================================================

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
    // number the manager ranks on; every field is summed into Total exactly once.
    public sealed class StrategicUseScoreBreakdown
    {
        public float RoleFit;                 // how well the card's real characteristics fit this role
        public float ImmediateTempo;          // placement fit + trait match + recurring-AP income realised this turn
        public float NextTurnPotential;       // what the card practically opens next turn
        public float CapabilityGapValue;      // closes a Recon / AA / AT / air / combat-body deficit
        public float ForceGrowthValue;        // contribution to standing force (radar-independent, scaled by BaselineForceReadiness.Need)
        public float ThreatResponseValue;     // strategic enemy-composition bias (AntiAir today; AntiArmor pending composition data)
        public float ResourceEfficiency;      // negative — AP + resource cost + extra-chain-step penalty (the ONLY place these are charged)
        public float SynergyValue;            // equipment upgrade on a carrier, kept-combo value
        public float Deployability;           // negative — probabilistic deploy (generation success chance)
        public float ScarcityValue;           // this card carries a rare capability worth something
        public float RedundancyPenalty;       // negative — the capability is already saturated
        public float AlternativeUseValue;     // negative — opportunity cost of using this card HERE vs its best other role / Hold
        public float HoldValue;               // value of deliberately NOT playing it now (separate; NetScore subtracts it)
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

    public enum NonCombatRole { Aviation, Base, Facility, Equipment }

    public sealed class StrategicCardUseCandidate
    {
        public MaterializationPlan Plan;
        public IntendedRole IntendedRole;
        public HexCoord? TargetContext;
        public StrategicUseScoreBreakdown Breakdown;
        public float TotalUseScore;   // == Breakdown.Total
        public float HoldValue;       // scored separately (spec §3)
        public MaterializationQualityBreakdown QualityBreakdown;

        // Playing now vs leaving it in hand. The manager admits on this.
        public float NetScore => TotalUseScore - Mathf.Max(0f, HoldValue);
    }

    // Radar-demand-INDEPENDENT standing-force signal (spec §4). Need in [0..1]: high when the
    // fielded force / combat-actor count / capability coverage is thin for the game stage, economy
    // and known enemy. P1.7: HasScout now counts toward coverage, hand-ready bodies reduce the
    // gap, and Need feeds ForceGrowthValue only — never the demand Value or CapabilityGapValue.
    internal readonly struct BaselineForceReadiness
    {
        public readonly float Need;
        public readonly bool HasScout;
        public readonly bool HasFieldBody;
        public readonly bool HasHero;
        public readonly bool HasAir;
        // P1.7 review-r3 — capability coverage vector, derived DYNAMICALLY from what the AI's
        // deployed armies + hand actually contain (abilities, not card class). Consumed by
        // SurplusCapabilityGap so a card closing an AA / AT / Mobile / Support hole scores a real
        // bonus.
        public readonly bool HasAntiAir;
        public readonly bool HasAntiArmor;
        public readonly bool HasMobile;
        public readonly bool HasSupport;
        public readonly int CombatActors;
        public readonly float FreeFieldPower;

        public BaselineForceReadiness(float need, bool hasScout, bool hasFieldBody, bool hasHero,
            bool hasAir, bool hasAntiAir, bool hasAntiArmor, bool hasMobile, bool hasSupport,
            int combatActors, float freeFieldPower)
        {
            Need = need;
            HasScout = hasScout;
            HasFieldBody = hasFieldBody;
            HasHero = hasHero;
            HasAir = hasAir;
            HasAntiAir = hasAntiAir;
            HasAntiArmor = hasAntiArmor;
            HasMobile = hasMobile;
            HasSupport = hasSupport;
            CombatActors = combatActors;
            FreeFieldPower = freeFieldPower;
        }

        public static BaselineForceReadiness Evaluate(WorldSnapshot snap, CapabilityInventory inv)
            => Evaluate(snap, inv, (IReadOnlyList<CardData>)null);

        public static BaselineForceReadiness Evaluate(WorldSnapshot snap, CapabilityInventory inv,
            IReadOnlyList<CardData> hand)
        {
            if (snap?.Self == null)
                return new BaselineForceReadiness(0f, false, false, false, false, false, false, false, false, 0, 0f);

            int combatActors = 0;
            bool hasAirArmy = false, hasAntiAir = false, hasAntiArmor = false, hasMobile = false, hasSupport = false;
            if (snap.Self.Armies != null)
                foreach (ArmySnapshot a in snap.Self.Armies)
                {
                    if (a == null || a.MemberCount <= 0 || a.IsPrison)
                        continue;
                    if (a.HasAntiAir) hasAntiAir = true;
                    if (a.HasAntiArmorUnit) hasAntiArmor = true;
                    if (a.HasMobileUnit) hasMobile = true;
                    if (a.HasSupportUnit) hasSupport = true;
                    if (a.IsAir) { hasAirArmy = true; continue; }
                    if (!a.IsGarrison && !a.IsSoloRecce)
                        combatActors++;
                }

            // P1.7 — a strong combat body already sitting in hand is prepared force: it shrinks the
            // actor gap the same way a deployed one would. Uses EFFECTIVE abilities (card + any
            // attached equipment), not the bare CardDefinition — attached equipment can grant a
            // counter ability or push a body over the readiness power floor.
            int handReadyBodies = 0;
            if (hand != null)
                foreach (CardData c in hand)
                {
                    CardDefinition d = c?.Definition;
                    if (d == null || d.isAviation
                        || (d.cardType != CardType.Unit && d.cardType != CardType.Hero))
                        continue;
                    IReadOnlyList<string> eff = c.Equipment?.equipment != null
                        ? EquipmentSystem.EffectiveAbilities(
                            d.grantedAbilities != null ? new List<string>(d.grantedAbilities) : new List<string>(),
                            c.Equipment.equipment)
                        : (IReadOnlyList<string>)(d.grantedAbilities ?? (IReadOnlyList<string>)System.Array.Empty<string>());
                    bool cardRecce = AbilityParams.AbilitiesHaveAnyRecce(eff);
                    // review-r4 finding 8.1 — coverage flags are read BEFORE the recce short-circuit:
                    // DeriveRoles gives a Scout+AntiAir card BOTH the Scout AND the AntiAir role, so
                    // it must count toward AA coverage too. review-r4 finding 8.2 — power / moveMax
                    // come from the EFFECTIVE stat line (attached equipment folded in at the stats
                    // level, not just abilities), not the bare CardDefinition.
                    AiPower.EffectiveCardLine line = AiPower.EffectiveLine(d, c.Equipment?.equipment);
                    if (eff.Contains(UnitAbilities.AntiAir)) hasAntiAir = true;
                    if (eff.Contains(UnitAbilities.Hyperkinetic)) hasAntiArmor = true;
                    if (eff.Contains(UnitAbilities.ApBonus) || eff.Contains(UnitAbilities.Researcher)
                        || eff.Contains(UnitAbilities.Assembler)) hasSupport = true;
                    if (!cardRecce && line.MoveMax >= AiConfigV2.mobileCombatMoveMax) hasMobile = true;
                    if (cardRecce)
                        continue;   // a recce card is a scout, not standing combat mass
                    if (line.BasePower >= AiConfigV2.baselineReadinessHandBodyMinPower)
                        handReadyBodies++;
                }

            bool hasScout = inv != null && inv.TotalScouts > 0;
            bool hasHero = inv != null && (inv.AvailableHeroes + inv.CommittedHeroes) > 0;
            bool hasFieldBody = combatActors > 0
                || (inv != null && inv.FieldCombatPower > AiConfigV2.allocatorSliceEpsilon);
            bool hasAir = hasAirArmy
                || (snap.Self.AirborneReconWings + snap.Self.SpareAirObservationSorties) > 0;

            float fieldPower = Mathf.Max(0f, snap.Self.FieldPower);
            float freeFieldPower = inv != null ? Mathf.Max(0f, inv.RaidAvailableFieldPower) : fieldPower;
            float enemy = snap.Known != null ? Mathf.Max(0f, snap.Known.EnemyKnownStrength) : 0f;
            float eco = snap.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;

            float stage = Curves.Ramp(snap.TurnNumber,
                AiConfigV2.baselineReadinessStageRampLo, AiConfigV2.baselineReadinessStageRampHi);
            float targetPower = Mathf.Max(AiConfigV2.baselineReadinessBaseTargetPower,
                                          enemy * AiConfigV2.baselineReadinessEnemyMatchFrac)
                                * Mathf.Lerp(AiConfigV2.baselineReadinessEarlyTargetFrac, 1f, stage);

            float powerGap = Mathf.Clamp01(1f - fieldPower / Mathf.Max(1f, targetPower));
            float effectiveActors = combatActors + AiConfigV2.baselineReadinessHandBodyActorWeight * handReadyBodies;
            float actorGap = Mathf.Clamp01(
                1f - effectiveActors / Mathf.Max(1f, (float)AiConfigV2.baselineReadinessTargetActors));
            // P1.7 — HasScout now contributes to coverage (was computed but ignored).
            int coverMisses = (hasFieldBody ? 0 : 1) + (hasHero ? 0 : 1) + (hasScout ? 0 : 1);
            float coverGap = Mathf.Clamp01(coverMisses / 3f);

            float raw = AiConfigV2.baselineReadinessPowerGapWeight * powerGap
                        + AiConfigV2.baselineReadinessActorGapWeight * actorGap
                        + AiConfigV2.baselineReadinessCoverGapWeight * coverGap;
            float need = Mathf.Clamp01(raw) * Mathf.Lerp(1f, AiConfigV2.baselineReadinessSecureDamp, eco);
            return new BaselineForceReadiness(need, hasScout, hasFieldBody, hasHero, hasAir,
                hasAntiAir, hasAntiArmor, hasMobile, hasSupport, combatActors, freeFieldPower);
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
            // P1.7 review-r3 — Phase A reads the SAME hand-aware readiness signal as Phase B and
            // DemandLayer (was hand-blind, so a strong body already in hand did not damp Need here).
            BaselineForceReadiness baseline = BaselineForceReadiness.Evaluate(snap, inv, snap?.Self?.Hand);

            float fit = TargetFit(plan.Deploy.Hex, demand.TargetHex);         // [0.5 .. 1]
            float traitMatch = demand.Capability != CapabilityKind.ScoutCapability
                               && (demand.PreferredTraits & TraitPreference.Stealth) != 0
                               && (projected & TraitPreference.Stealth) != 0
                ? AiConfigV2.stratTraitMatchBonus : 0f;

            // P1.4/P1.5 review-r2 — RoleFit is the pure capability-fit from the ONE shared RoleFit
            // path (Scout quality profile / hero combat-leadership / AiPower marginal readiness /
            // equipment delta), multiplied by the target-hex fit. Cost, placement, chain-step and
            // generation chance live in their own single terms below.
            CardDefinition pdef = PlanBaseDef(plan);
            bool heroCard = pdef != null && pdef.cardType == CardType.Hero;
            bool recceCard = AbilityParams.AbilitiesHaveAnyRecce(plan.ProjectedAbilities ?? pdef?.grantedAbilities);
            float equipUpgrade = plan.UsesEquipment ? EquipmentUpgradeUtility(plan) : 0f;
            float roleFitCore = RoleFit(role, plan, inv, recceCard, heroCard,
                plan.ProjectedAbilities ?? pdef?.grantedAbilities, snap, 0f, equipUpgrade,
                demand, referenceMoveMax, hasCompetingHeroDemand,
                out MaterializationQualityBreakdown qbd);
            bd.RoleFit = fit * roleFitCore;

            bd.ImmediateTempo = traitMatch + PlacementBonus(plan.Deploy.Kind);
            bd.NextTurnPotential = NextTurnPotential(plan, role);
            bd.SynergyValue = SynergyValue(plan);
            bd.ForceGrowthValue = ForceGrowthValue(plan, demand.Capability, baseline);
            bd.CapabilityGapValue = CapabilityGapValue(demand.Capability, inv, baseline);
            bd.ThreatResponseValue = ThreatResponseValue(role, snap);

            float genChance = GenerationChance(plan);
            bd.Deployability = -(1f - genChance);
            bd.ResourceEfficiency = -ResourceCost(plan);

            bd.RedundancyPenalty = -(GarrisonSaturationPenalty(plan, demand, snap)
                                     + ScoutOversupplyPenalty(role, inv));
            // Phase A form of AlternativeUseValue: the scarcity opportunity cost of spending this
            // exact card body (a scarce hero on a non-hero demand, a unique stealth item on a
            // non-stealth demand) — the general "best other role" cost applies in Phase B.
            bd.AlternativeUseValue = -ScarcityOpportunityCost(plan, demand, inv);
            bd.ScarcityValue = 0f;               // closing an explicit demand — scarcity is a Hold concern
            bd.ResourcePressureBenefit = 0f;     // spends a ledger entitlement, not stranded AP
            bd.HandPressureBenefit = 0f;

            bd.Total = SumTotal(bd);

            // P1.6 review-r3 — card-level HoldValue: max reason-to-hold across ALL of the card's
            // viable roles, not just the role this demand pins (an AA-capable card serving a
            // FieldCombatPower demand still gets its "keep as a rare AA counter" hold value).
            IReadOnlyList<IntendedRole> viableRoles = DeriveRoles(pdef,
                plan.ProjectedAbilities ?? pdef?.grantedAbilities, plan, recceCard, heroCard);
            bd.HoldValue = CardHoldValue(plan, viableRoles, inv, snap, baseline, surplus: false);

            return new StrategicCardUseCandidate
            {
                Plan = plan,
                IntendedRole = role,
                TargetContext = demand.TargetHex,
                Breakdown = bd,
                TotalUseScore = bd.Total,
                HoldValue = bd.HoldValue,
                QualityBreakdown = qbd,
            };
        }

        // -----------------------------------------------------------------------------------------
        //  PHASE B — proactive surplus. One card -> several IntendedRole candidates; the best
        //  NetScore is returned. AlternativeUseValue on the winner is the real cost of NOT keeping
        //  it for its next-best role / Hold (P1.6).
        // -----------------------------------------------------------------------------------------
        public static StrategicCardUseCandidate ScoreSurplus(MaterializationPlan plan, CapabilityInventory inv,
            bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected, WorldSnapshot snap)
        {
            CardDefinition def = PlanBaseDef(plan);
            BaselineForceReadiness baseline = BaselineForceReadiness.Evaluate(snap, inv, hand?.Hand);
            IReadOnlyList<IntendedRole> roles = DeriveRoles(def, projected, plan, recce, hero);
            float versatility = RoleVersatility(roles);

            var scored = new List<StrategicCardUseCandidate>(roles.Count);
            foreach (IntendedRole role in roles)
                scored.Add(ScoreSurplusRole(plan, role, inv, recce, hero, hand, projected, snap,
                    baseline, versatility));

            scored.Sort((a, b) =>
            {
                int c = b.Breakdown.Total.CompareTo(a.Breakdown.Total);
                return c != 0 ? c : ((int)a.IntendedRole).CompareTo((int)b.IntendedRole);
            });

            StrategicCardUseCandidate win = scored[0];
            // P1.6 — AlternativeUseValue is the cost of the best foregone *other PLAY role* only;
            // Hold is priced exclusively in NetScore. Keep the scarce-body floor from the per-role
            // pass.
            float secondBestPlay = scored.Count > 1 ? scored[1].Breakdown.Total : 0f;
            float altCost = Mathf.Max(0f, secondBestPlay) * AiConfigV2.altUseForegoneFraction
                            + Mathf.Max(0f, -win.Breakdown.AlternativeUseValue);
            win.Breakdown.AlternativeUseValue = -altCost;
            win.Breakdown.Total = SumTotal(win.Breakdown);
            win.TotalUseScore = win.Breakdown.Total;
            // P1.6 review-r3 — HoldValue is a property of the CARD, evaluated across ALL its viable
            // roles (an AA-capable unit chosen as CombatBody still carries its "keep as a rare AA
            // counter" hold value).
            win.HoldValue = CardHoldValue(plan, roles, inv, snap, baseline, surplus: true);
            win.Breakdown.HoldValue = win.HoldValue;
            return win;
        }

        private static StrategicCardUseCandidate ScoreSurplusRole(MaterializationPlan plan, IntendedRole role,
            CapabilityInventory inv, bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected,
            WorldSnapshot snap, BaselineForceReadiness baseline, float versatility)
        {
            var bd = new StrategicUseScoreBreakdown();
            float scarcity = SurplusScarcity(inv, recce, hero);
            float traits = projected != null && AbilityParams.AbilitiesHaveAnyStealth(projected)
                ? AiConfigV2.stratTraitMatchBonus : 0f;
            float recurringAp = projected != null && projected.Contains(UnitAbilities.ApBonus)
                ? AiConfigV2.surplusRecurringApIncomeBonus : 0f;
            float equipmentUpgrade = plan.UsesEquipment ? EquipmentUpgradeUtility(plan) : 0f;

            bd.RoleFit = RoleFit(role, plan, inv, recce, hero, projected, snap, versatility,
                equipmentUpgrade, null, 0, false, out _);
            // P1.4 — placement counted once, here; the Phase-B garrison-surplus correction that used
            // to live in MaterializationPlan.Score is folded in via SurplusPlacementBonus.
            bd.ImmediateTempo = traits + recurringAp + SurplusPlacementBonus(plan.Deploy.Kind, role);
            bd.NextTurnPotential = NextTurnPotential(plan, role);
            bd.CapabilityGapValue = role == IntendedRole.Hold ? 0f
                : SurplusCapabilityGap(role, inv, baseline, snap);
            bd.ForceGrowthValue = role == IntendedRole.Scout || role == IntendedRole.Hold
                ? 0f : ForceGrowthValue(plan, plan.FinalCapability, baseline);
            bd.ThreatResponseValue = ThreatResponseValue(role, snap);
            bd.SynergyValue = traits * 0.5f + equipmentUpgrade;
            bd.Deployability = -(1f - GenerationChance(plan));
            bd.ResourceEfficiency = -ResourceCost(plan);
            bd.ScarcityValue = role == IntendedRole.Hold ? 0f : scarcity;
            bd.RedundancyPenalty = -ScoutOversupplyPenalty(role, inv);
            bd.AlternativeUseValue = -SurplusScarceBodyFloor(plan, role, inv, hero);
            bd.ResourcePressureBenefit = 0f;   // SurplusAdmissionPolicy owns the stranded-AP relaxation (single layer)
            bd.HandPressureBenefit = hand != null && !hand.HasFreeSlot ? AiConfigV2.surplusHandPressureBonus : 0f;
            bd.Total = SumTotal(bd);
            bd.HoldValue = HoldValue(plan, role, inv, snap, baseline, surplus: true);

            return new StrategicCardUseCandidate
            {
                Plan = plan,
                IntendedRole = role,
                Breakdown = bd,
                TotalUseScore = bd.Total,
                HoldValue = bd.HoldValue,
            };
        }

        // The ONE RoleFit path, used by BOTH phases (P1.5 review-r2). Characteristic-driven; the
        // Hero card class is never a term. For Scout it runs CapabilityQualityEvaluator against the
        // REAL demand when one exists (Phase A) or a neutral synthetic one (Phase B); for
        // Hero/CombatBody it returns the AiPower marginal readiness PLUS the canonical hero
        // combat-leadership fit — so a commandRating-10 hero now out-fits a commandRating-2 hero in
        // Phase A too (previously QualityMultiplier returned 1f for every non-Scout role).
        private static float RoleFit(IntendedRole role, MaterializationPlan plan, CapabilityInventory inv,
            bool recce, bool hero, IReadOnlyList<string> projected, WorldSnapshot snap, float versatility,
            float equipmentUpgrade, AxisDemand demand, int referenceMoveMax, bool hasCompetingHeroDemand,
            out MaterializationQualityBreakdown qbd)
        {
            qbd = MaterializationQualityBreakdown.Neutral();
            switch (role)
            {
                case IntendedRole.Scout:
                {
                    AxisDemand d = demand != null && demand.Capability == CapabilityKind.ScoutCapability
                        ? demand
                        : new AxisDemand { Capability = CapabilityKind.ScoutCapability };
                    float mult = CapabilityQualityEvaluator.QualityMultiplier(
                        plan, d, inv, referenceMoveMax, hasCompetingHeroDemand, out qbd);
                    return AiConfigV2.scoutBaseRoleFit * mult;
                }
                case IntendedRole.EquipmentUpgrade:
                    return equipmentUpgrade;
                case IntendedRole.Support:
                    return SupportRoleFit(projected) + HeroSupportFit(plan, hero);
                case IntendedRole.CombatBody:
                case IntendedRole.ForceGrowth:
                case IntendedRole.MobileCombat:
                case IntendedRole.AntiArmor:
                case IntendedRole.AntiAir:
                    return SurplusCombatReadinessUtility(plan) + HeroLeadershipFit(plan, hero);
                case IntendedRole.Hold:
                    return 0f;
                default:
                    return versatility;
            }
        }

        // -----------------------------------------------------------------------------------------
        //  NON-COMBAT CARDS  (Aviation / Base / Facility / standalone Equipment) — AI-MGR-01 P0.1.
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
                    bd.NextTurnPotential = AiConfigV2.nextTurnActorPotential;
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
                default:
                    role = IntendedRole.EquipmentUpgrade;
                    bd.RoleFit = Mathf.Max(AiConfigV2.nonCombatEquipmentValueFloor, bestEquipmentUpgrade);
                    break;
            }

            bd.HandPressureBenefit = hand != null && !hand.HasFreeSlot ? AiConfigV2.surplusHandPressureBonus : 0f;
            bd.ResourceEfficiency = -(AiConfigV2.surplusApCostWeight * apCost
                                      + AiConfigV2.surplusResourceCostWeight * resSum);
            bd.Total = SumTotal(bd);
            bd.HoldValue = hand != null && !hand.HasFreeSlot ? 0f : AiConfigV2.holdLostTempoPenalty * 0.5f;

            return new StrategicCardUseCandidate
            {
                Plan = null,
                IntendedRole = role,
                Breakdown = bd,
                TotalUseScore = bd.Total,
                HoldValue = bd.HoldValue,
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
        //  SINGLE-COUNT PRIMITIVES  (P1.4 — each factor priced exactly once)
        // =======================================================================================
        private static float SumTotal(StrategicUseScoreBreakdown b) =>
            b.RoleFit + b.ImmediateTempo + b.NextTurnPotential + b.CapabilityGapValue
            + b.ForceGrowthValue + b.ThreatResponseValue + b.ResourceEfficiency + b.SynergyValue
            + b.Deployability + b.ScarcityValue + b.RedundancyPenalty + b.AlternativeUseValue
            + b.ResourcePressureBenefit + b.HandPressureBenefit;

        // AP + resource cost + extra-chain-step penalty. The ONLY place a chain is charged for cost.
        private static float ResourceCost(MaterializationPlan plan)
        {
            if (plan == null) return 0f;
            return AiConfigV2.stratCardApCostWeight * plan.ApCost
                   + AiConfigV2.stratChainResCostWeight * ResourceCostSum(plan.ResCost)
                   + ChainStepPenalty(plan.Kind);
        }

        private static float GenerationChance(MaterializationPlan plan) =>
            plan?.Generation != null
                ? Mathf.Lerp(AiConfigV2.stratChainGenerationChanceFloor, 1f,
                    Mathf.Clamp01(plan.Generation.SuccessChance))
                : 1f;

        // =======================================================================================
        //  SPEC TERMS
        // =======================================================================================
        private static float ForceGrowthValue(MaterializationPlan plan, CapabilityKind cap,
            BaselineForceReadiness baseline)
        {
            if (cap != CapabilityKind.FieldCombatPower && cap != CapabilityKind.GarrisonCombatPower
                && cap != CapabilityKind.Hero)
                return 0f;
            float marginal = SurplusCombatReadinessUtility(plan);
            if (marginal <= 0f)
                return 0f;
            float scale = Mathf.Lerp(AiConfigV2.baselineReadinessGrowthFloor, 1f, Mathf.Clamp01(baseline.Need));
            return marginal * scale * AiConfigV2.forceGrowthValueWeight;
        }

        // P1.7 — binary "the AI lacks this capability class", NOT scaled by Need (Need is already
        // priced once, in ForceGrowthValue).
        private static float CapabilityGapValue(CapabilityKind cap, CapabilityInventory inv,
            BaselineForceReadiness baseline)
        {
            if (inv == null) return 0f;
            switch (cap)
            {
                case CapabilityKind.ScoutCapability:
                    return inv.TotalScouts <= 0 ? AiConfigV2.capabilityGapValue : 0f;
                case CapabilityKind.Hero:
                    return (inv.AvailableHeroes + inv.CommittedHeroes) <= 0 ? AiConfigV2.capabilityGapValue : 0f;
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    return baseline.HasFieldBody ? 0f : AiConfigV2.capabilityGapValue;
                default:
                    return 0f;
            }
        }

        // P1.7 review-r3 — uses the dynamically-derived coverage vector. A card closing a hole the
        // AI's deployed force + hand genuinely lack scores the gap bonus; an AA/AT gap only counts
        // when the matching enemy threat is actually present.
        private static float SurplusCapabilityGap(IntendedRole role, CapabilityInventory inv,
            BaselineForceReadiness baseline, WorldSnapshot snap)
        {
            switch (role)
            {
                case IntendedRole.Scout:
                    return inv != null && inv.TotalScouts <= 0 ? AiConfigV2.capabilityGapValue : 0f;
                case IntendedRole.AntiAir:
                    return !baseline.HasAntiAir && ThreatResponseValue(IntendedRole.AntiAir, snap) > 0f
                        ? AiConfigV2.capabilityGapValue : 0f;
                case IntendedRole.AntiArmor:
                    return !baseline.HasAntiArmor && ThreatResponseValue(IntendedRole.AntiArmor, snap) > 0f
                        ? AiConfigV2.capabilityGapValue : 0f;
                case IntendedRole.Support:
                    return baseline.HasSupport ? 0f : AiConfigV2.capabilityGapValue * 0.5f;
                case IntendedRole.MobileCombat:
                    if (!baseline.HasFieldBody) return AiConfigV2.capabilityGapValue;
                    return baseline.HasMobile ? 0f : AiConfigV2.capabilityGapValue * 0.5f;
                case IntendedRole.CombatBody:
                case IntendedRole.ForceGrowth:
                    return baseline.HasFieldBody ? 0f : AiConfigV2.capabilityGapValue;
                default:
                    return 0f;
            }
        }

        // review-r4 finding 5 — ONE strategic threat-response primitive for BOTH counter roles.
        // Cheat-biased DIRECTIONAL signal off omniscient enemy composition (TrueWorld); it never
        // becomes normal AI intel. AntiAir keys on the real IsAir classification; AntiArmor keys on
        // a real Armored-tagged member — the snapshot DOES carry enemy composition through TrueWorld,
        // so P2.8's "no data, contributes 0" caveat no longer applies to this cheat path.
        private static float ThreatResponseValue(IntendedRole role, WorldSnapshot snap)
        {
            if (snap == null || (role != IntendedRole.AntiAir && role != IntendedRole.AntiArmor))
                return 0f;
            IReadOnlyList<ArmySnapshot> enemies = snap.TrueWorld?.EnemyArmies;
            if (enemies == null || enemies.Count == 0)
                return 0f;
            float driver = 0f;
            foreach (ArmySnapshot a in enemies)
            {
                if (a == null) continue;
                if (role == IntendedRole.AntiAir)
                {
                    if (a.IsAir) driver += a.EffectiveArmyPower;
                }
                else if (ArmyHasArmoredMember(a))
                    driver += a.EffectiveArmyPower;
            }
            if (driver <= 0f)
                return 0f;
            return Mathf.Clamp(driver / Mathf.Max(1f, AiConfigV2.threatResponseNorm), 0f, 1f)
                   * AiConfigV2.threatResponseValueWeight;
        }

        private static bool ArmyHasArmoredMember(ArmySnapshot a)
        {
            if (a?.Members == null) return false;
            foreach (WorthIt.DefenderProfile m in a.Members)
                if (m.TypeTags != null && m.TypeTags.Contains(UnitTypeTag.Armored))
                    return true;
            return false;
        }

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
            return v;
        }

        // review-r3 — the ONE card-level HoldValue used by BOTH phases: the strongest reason to
        // hold the card across ALL of its viable roles (NearTermExpectedDemand is role-specific, so
        // a card whose best play is CombatBody but which is also a rare AntiAir counter still gets
        // that hold value). Hold is priced only here / in NetScore, never as a play role.
        internal static float CardHoldValue(MaterializationPlan plan, IReadOnlyList<IntendedRole> roles,
            CapabilityInventory inv, WorldSnapshot snap, BaselineForceReadiness baseline, bool surplus)
        {
            if (plan == null)
                return 0f;
            float best = 0f;
            if (roles != null)
                foreach (IntendedRole r in roles)
                    best = Mathf.Max(best, HoldValue(plan, r, inv, snap, baseline, surplus));
            // Also evaluate the card's base combat role even if it was not derived (covers a
            // plain body with no special abilities).
            best = Mathf.Max(best, HoldValue(plan, IntendedRole.CombatBody, inv, snap, baseline, surplus));
            return best;
        }

        // Spec §3 — HoldValue = UniqueFutureRole + NearTermExpectedDemand + ScarcityValue
        //                       - HandPressure - LostTempo   (per-role component)
        private static float HoldValue(MaterializationPlan plan, IntendedRole role, CapabilityInventory inv,
            WorldSnapshot snap, BaselineForceReadiness baseline, bool surplus)
        {
            if (plan == null)
                return 0f;
            // review-r4 finding 7 — a GENERATED deployable is not yet a card in hand. Declining the
            // chain preserves the GENERATOR option + its resources + this turn's generation attempt,
            // NOT a scarce physical card, and the play score already carries the generation step
            // penalty + success-chance discount. It has no physical-card hold value. (A dedicated
            // generator-option value is a later refinement; 0 is the conservative floor.)
            if (plan.GeneratedBaseDef != null && plan.BaseCardInHand == null)
                return 0f;
            CardDefinition def = PlanBaseDef(plan);

            float uniqueFutureRole = 0f;
            if ((plan.ExpectedTraits & TraitPreference.Stealth) != 0
                && inv != null && inv.StealthScouts <= AiConfigV2.stratChainStealthScarceAt)
                uniqueFutureRole += AiConfigV2.holdUniqueRoleValue;
            if (def != null && def.cardType == CardType.Hero && PlanHeroIsSupport(plan)
                && inv != null && inv.AvailableHeroes + inv.CommittedHeroes > 0)
                uniqueFutureRole += AiConfigV2.holdUniqueRoleValue;

            // P1.6 — real NearTermExpectedDemand: a specialist counter whose triggering threat is
            // already visible (enemy air for an AntiAir body) is worth keeping ready; a plain body
            // when standing-force need is low is NOT (you would rather deploy it, so 0).
            float nearTermDemand = 0f;
            if ((role == IntendedRole.AntiAir || role == IntendedRole.AntiArmor
                 || role == IntendedRole.CapabilitySpecialist)
                && ThreatResponseValue(role, snap) > 0f)
                nearTermDemand += AiConfigV2.holdNearTermDemandValue;

            bool recce = AbilityParams.AbilitiesHaveAnyRecce(plan.ProjectedAbilities ?? def?.grantedAbilities);
            float scarcityValue = inv != null
                && SurplusScarcity(inv, recce, def != null && def.cardType == CardType.Hero)
                   >= AiConfigV2.surplusScarcityMed
                ? AiConfigV2.holdScarcityValue : 0f;

            // review-r4 finding 6 — the two spec §3 Hold terms the impl still lacked.
            //  ComboPreservation: a still-unattached Equipment card in hand that legally fits this
            //  body — playing it bare NOW forecloses the stronger AttachDeploy combination (that
            //  combined chain is scored on its own; this only lifts the BARE variant's hold value).
            float comboPreservation =
                surplus && plan.Kind == MaterializationChainKind.Direct && def != null
                && (def.cardType == CardType.Unit || def.cardType == CardType.Hero)
                && HandHasEquipmentPartnerFor(def, snap)
                    ? AiConfigV2.holdComboPreservationValue : 0f;
            //  ResourcePressure: a SECURE economy (ample stockpile / strong income → resources at
            //  risk of capping or cheaply replenished) lowers the value of hoarding by holding the
            //  card; a fragile economy raises it. No per-resource cap signal on the snapshot yet —
            //  EconomicSecurity is the proxy.
            float eco = snap?.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;
            float resourcePressure = eco * AiConfigV2.holdResourcePressurePenalty;

            float handPressure = snap?.Self != null && !snap.Self.HasFreeHandSlot
                ? AiConfigV2.holdHandPressurePenalty : 0f;
            float lostTempo = surplus ? AiConfigV2.holdLostTempoPenalty : 0f;

            return Mathf.Max(0f,
                uniqueFutureRole + nearTermDemand + scarcityValue + comboPreservation
                - handPressure - resourcePressure - lostTempo);
        }

        // review-r4 finding 6 — any still-unattached Equipment card in hand whose grant would
        // legally accept this body as a host.
        private static bool HandHasEquipmentPartnerFor(CardDefinition hostDef, WorldSnapshot snap)
        {
            IReadOnlyList<CardData> hand = snap?.Self?.Hand;
            if (hand == null || hostDef == null)
                return false;
            EquipmentHostKind kind = hostDef.cardType == CardType.Hero
                ? EquipmentHostKind.Hero : EquipmentHostKind.Unit;
            foreach (CardData c in hand)
            {
                EquipmentGrant grant = c?.Definition != null
                    && c.Definition.cardType == CardType.Equipment ? c.Definition.equipment : null;
                if (grant?.hostKinds == null || !grant.hostKinds.Contains(kind))
                    continue;
                if (grant.hostTypeTags != null && grant.hostTypeTags.Count > 0
                    && (hostDef.unitTypeTags == null
                        || !grant.hostTypeTags.Any(t => hostDef.unitTypeTags.Contains(t))))
                    continue;
                return true;
            }
            return false;
        }

        // =======================================================================================
        //  ROLE DERIVATION + VERSATILITY  (P1.5 — versatility from real roles, not card class)
        // =======================================================================================
        private static IntendedRole RoleForCapability(CapabilityKind cap, CardDefinition def)
        {
            switch (cap)
            {
                case CapabilityKind.ScoutCapability: return IntendedRole.Scout;
                case CapabilityKind.EconomicInfrastructure: return IntendedRole.Economy;
                case CapabilityKind.DevelopmentInfrastructure: return IntendedRole.Development;
                default: return IntendedRole.CombatBody;
            }
        }

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
            // review-r3 — Hold is NOT a play role: it never goes through ScoreSurplusRole (which
            // would give it ImmediateTempo / NewArmy NextTurnPotential / etc. for an army that is
            // never created). It is a separate no-op scored by CardHoldValue.
            if (roles.Count == 0)
                roles.Add(IntendedRole.CombatBody); // a card with no derived role still has a generic use
            return roles.Distinct().ToList();
        }

        // Versatility = value of a card that fits several real roles (excludes Hold). NOT a Hero
        // class bonus — a Hero with one viable role scores the same as a Unit with one viable role.
        private static float RoleVersatility(IReadOnlyList<IntendedRole> roles)
        {
            int real = roles.Count(r => r != IntendedRole.Hold);
            return Mathf.Clamp((real - 1) * AiConfigV2.roleVersatilityPerExtraRole,
                0f, AiConfigV2.roleVersatilityCap);
        }

        private static float SupportRoleFit(IReadOnlyList<string> projected)
        {
            if (projected == null) return 0f;
            float v = 0f;
            if (projected.Contains(UnitAbilities.ApBonus)) v += AiConfigV2.surplusRecurringApIncomeBonus;
            if (projected.Contains(UnitAbilities.Researcher) || projected.Contains(UnitAbilities.Assembler))
                v += AiConfigV2.heroSupportFitValue;
            return v;
        }

        // Phase-B placement, garrison-surplus correction folded in (P1.4 — was in Plan.Score getter).
        private static float SurplusPlacementBonus(DeploymentKind k, IntendedRole role)
        {
            bool combat = role == IntendedRole.CombatBody || role == IntendedRole.ForceGrowth
                || role == IntendedRole.MobileCombat || role == IntendedRole.AntiArmor
                || role == IntendedRole.AntiAir;
            if (combat && k == DeploymentKind.Garrison)
                return -AiConfigV2.stratPlacementReusableShellBonus; // proactive field readiness prefers the field
            return PlacementBonus(k);
        }

        // =======================================================================================
        //  HERO FITNESS  — real characteristics only, no flat class bonus/penalty (P1.5)
        // =======================================================================================
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
            if (!hero) return 0f;
            return Mathf.Clamp(
                HeroLeadershipScore(PlanBaseDef(plan)) / Mathf.Max(1f, AiConfigV2.heroLeadershipFitNorm),
                0f, AiConfigV2.heroLeadershipFitCap);
        }

        private static float HeroSupportFit(MaterializationPlan plan, bool hero) =>
            hero && HeroHasSupportVocation(PlanBaseDef(plan)) ? AiConfigV2.heroSupportFitValue : 0f;

        private static bool PlanHeroIsSupport(MaterializationPlan plan)
        {
            CardDefinition def = PlanBaseDef(plan);
            return HeroHasSupportVocation(def)
                && HeroLeadershipScore(def) < AiConfigV2.heroRoleFlexibleCombatFloor;
        }

        // =======================================================================================
        //  PORTED PRIMITIVES  (scoring math lives with the evaluator; feasibility stays in builder)
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
            return ScoreEquipmentDelta(grant, before, host.grantedAbilities, host.cardType == CardType.Hero);
        }

        // P1(review-r2) — standalone Equipment scored by the REAL predicted before/after delta on a
        // concrete live host, not by the host's raw power. NonCombatCardPlayer picks the (equipment,
        // host) pair that maximises this.
        internal static float EquipmentUpgradeUtilityFor(CardDefinition equipDef, UnitData host)
        {
            EquipmentGrant grant = equipDef?.equipment;
            if (grant == null || host == null)
                return 0f;
            var before = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = host.Attack,
                [EquipmentStat.Defense] = host.Defense,
                [EquipmentStat.Resistance] = host.Resistance,
                [EquipmentStat.Range] = host.Range,
                [EquipmentStat.HitPoints] = host.HitPointsMax,
                [EquipmentStat.MoveMax] = host.MoveMax,
                [EquipmentStat.Initiative] = host.Initiative,
                [EquipmentStat.ActivationApCost] = host.ActivationApCost,
                [EquipmentStat.CommandRating] = host.CommandRating,
                [EquipmentStat.Fate] = host.Fate,
            };
            IReadOnlyList<string> ab = host.Abilities != null
                ? new List<string>(host.Abilities) : (IReadOnlyList<string>)System.Array.Empty<string>();
            return ScoreEquipmentDelta(grant, before, ab, host.IsHero);
        }

        private static float ScoreEquipmentDelta(EquipmentGrant grant, Dictionary<EquipmentStat, int> before,
            IReadOnlyList<string> hostAbilities, bool isHero)
        {
            PredictedEquipmentState predicted = EquipmentSystem.Predict(grant, before, hostAbilities);
            int After(EquipmentStat stat) =>
                predicted.Stats != null && predicted.Stats.TryGetValue(stat, out int value) ? value : before[stat];

            float combatDelta =
                Mathf.Max(0, After(EquipmentStat.Attack) - before[EquipmentStat.Attack]) * AiConfigV2.powerAttackWeight
                + Mathf.Max(0, After(EquipmentStat.Defense) - before[EquipmentStat.Defense]) * AiConfigV2.powerDefenseWeight
                + Mathf.Max(0, After(EquipmentStat.HitPoints) - before[EquipmentStat.HitPoints]) * AiConfigV2.powerHitPointsWeight
                + Mathf.Max(0, After(EquipmentStat.Initiative) - before[EquipmentStat.Initiative]) * AiConfigV2.powerInitiativeWeight
                + Mathf.Max(0, After(EquipmentStat.Resistance) - before[EquipmentStat.Resistance]) * AiConfigV2.powerResistanceWeight;
            if (isHero)
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
                    if (hostAbilities == null || !hostAbilities.Contains(a))
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

        // Phase-A opportunity cost of spending this exact card body off its best use.
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

        // Phase-B floor under AlternativeUseValue: a scarce hero committed to a plain body, or a
        // unique stealth item burned into a non-scout role, always costs at least this.
        private static float SurplusScarceBodyFloor(MaterializationPlan p, IntendedRole role,
            CapabilityInventory inv, bool hero)
        {
            float cost = 0f;
            if (hero && role != IntendedRole.Support && role != IntendedRole.Scout
                && inv != null && inv.AvailableHeroes <= AiConfigV2.stratChainHeroScarceAt)
                cost += AiConfigV2.stratChainHeroScarcityPenalty;
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
            if (!target.HasValue) return 0.75f;
            int d = HexGridMath.Distance(deployHex, target.Value);
            return 0.5f + 0.5f * Mathf.Clamp01(1f - d / Mathf.Max(1f, (float)AiConfigV2.stratTargetFitRange));
        }

        private static CardDefinition PlanBaseDef(MaterializationPlan p) =>
            p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
    }
}
