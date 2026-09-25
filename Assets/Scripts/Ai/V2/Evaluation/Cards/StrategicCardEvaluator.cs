using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC CARD EVALUATOR
    // ===========================================================================================
    //  The ONE shared answer to "how useful is it to play this card now, for what purpose, or is
    //  it better held?". Both StrategicManager phases route through it:
    //    Phase A (FulfillDemands)  — ScoreForDemand: a card chain closing an explicit AxisDemand.
    //    Phase B (UseSurplus)      — ScoreSurplus:  a card chain played proactively with genuinely
    //                                remaining resources.
    //    Non-combat lane           — ScoreNonCombat: Aviation / Base / Facility / standalone
    //                                Equipment, same NetScore band, specialised executors.
    //
    //  Invariants:
    //   * The score is FINAL. Every factor (placement, AP/resource cost, extra-chain-step
    //     penalty, generation probability, garrison-surplus correction) is applied EXACTLY ONCE,
    //     inside this file. Breakdown.Total == the ranked value; callers do not re-adjust it.
    //   * One RoleFit path per role, called by both phases. The Hero card CLASS adds no flat
    //     bonus/penalty; "versatility" is derived from how many real viable roles the card has,
    //     not from being a Hero. Phase B Scout gets the SAME CapabilityQualityEvaluator profile as
    //     Phase A (via a neutral synthetic scout demand).
    //   * AlternativeUseValue is the real opportunity cost of using the card HERE instead of
    //     its best other role / Hold. NearTermExpectedDemand is a real Hold term.
    //   * BaselineForceReadiness.Need feeds ForceGrowthValue ONCE; it is not also folded
    //     into the demand's Value or CapabilityGapValue.
    //   * No SYNTHETIC armour, but the cheat path is live: AntiAir and
    //     AntiArmor both take a directional ThreatResponseValue off omniscient TrueWorld composition
    //     (real IsAir / real Armored-tagged member). It never becomes normal AI intel.
    //
    //  Axis principle — BaselineForceReadiness.Need is radar-DEMAND-INDEPENDENT, but
    //  ForceGrowthValue itself is NOT: it is gated on BaselineForceReadiness.MilitaryWitnessed (a
    //  live neutral Raid target, or an asset threat at/above the reserved threatSeverityTrigger).
    //  Attack/Defence axes create demand; Production reinforces an already-justified demand — it
    //  must never manufacture its own reason to spend. It only decides a card is worth
    //  MATERIALISING; which army/garrison it joins and stack composition stay a separate layer
    //  (Housekeeping).
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
        // A card whose granted ability is a PlayerGlobal recurring-resource yield (ApBonus/
        // ProduceHuman/ProduceMaterials/...). Previously this had no role of its own — the
        // effect's value (ec.GlobalRoleFit) silently rode whatever OTHER role won the card's
        // internal contest (usually CombatBody), so a pure economy hero logged as role=CombatBody
        // with no visible reason. This role gives it its own RoleFitCore weight so it can win the
        // contest and be labelled as itself on a card with a weak/no combat body.
        ResourceGain,
        Hold,
    }

    // Diagnostic decomposition of one Card x IntendedUse score. Total is the single authoritative
    // number the manager ranks on; every field is summed into Total exactly once.
    public sealed class StrategicUseScoreBreakdown
    {
        public float RoleFit;                 // how well the card's real characteristics fit this role
        public float ImmediateTempo;          // Phase A only now — placement fit + trait match (Phase B stopped assigning this, see ScoreSurplusRole/ScoreNonCombat)
        public float NextTurnPotential;       // Phase A only now — what the card practically opens next turn (Phase B stopped assigning this)
        // Phase B: ONLY the AntiAir/AntiArmor enemy-composition matchup (Scout's "0 scouts" case
        // lives in RoleFit), i.e. a threat-counter value. CAUTION — Phase A (ScoreForDemand) writes
        // its broader "closes a Recon/Hero/combat-body capability the AI has ZERO of" meaning into
        // this SAME field; the name fits Phase B only.
        public float ThreatCounterValue;
        public float ForceGrowthValue;        // contribution to standing force (radar-independent, scaled by BaselineForceReadiness.Need); zeroed for hero cards in both phases — a hero's own body already prices into HeroLeadershipFit
        public float ResourceEfficiency;      // negative — AP + resource cost + extra-chain-step penalty (the ONLY place these are charged)
        public float SynergyValue;            // equipment upgrade on a carrier, kept-combo value
        public float GenerationRiskDiscount;  // negative — probabilistic deploy (generation success chance); was named Deployability
        public float RedundancyPenalty;       // negative — the capability is already saturated
        public float AlternativeUseValue;     // negative — opportunity cost of using this card HERE vs its best other role / Hold
        public float HoldValue;               // value of deliberately NOT playing it now (separate; NetScore subtracts it)
        public float ResourcePressureBenefit; // stranded AP / near-cap resource makes spending now better
        public float HandPressureBenefit;     // a full hand makes materialising now better
        // Canonical world-task value directly enabled by this exact non-combat placement. Only the
        // shared scorer may fold it into global card arbitration; callers never post-adjust Total.
        public float OperationalTaskValue;

        public float Total;

        // AI-MGR — human-readable decomposition of any DYNAMIC effect that fed this score
        // (PlayerGlobal recurring-resource value, hero Command marginal-capacity value). Null when
        // the card carries none. Surfaced in every AiDebug line that logs ToCompact().
        public string EffectDetail;

        public string ToCompact()
        {
            string F(float v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            return $"role {F(RoleFit)} tempo {F(ImmediateTempo)} next {F(NextTurnPotential)} "
                 + $"threat {F(ThreatCounterValue)} grow {F(ForceGrowthValue)} "
                 + $"res {F(ResourceEfficiency)} syn {F(SynergyValue)} deploy {F(GenerationRiskDiscount)} "
                 + $"redun {F(RedundancyPenalty)} alt {F(AlternativeUseValue)} "
                 + $"resP {F(ResourcePressureBenefit)} handP {F(HandPressureBenefit)} "
                 + $"task {F(OperationalTaskValue)} "
                 + $"hold {F(HoldValue)} "
                 + $"= {Total.ToString("0.00", CultureInfo.InvariantCulture)}"
                 + (string.IsNullOrEmpty(EffectDetail) ? "" : $"  || {EffectDetail}");
        }
    }

    public enum NonCombatRole { Aviation, Facility, Equipment }

    public sealed class StrategicCardUseCandidate
    {
        public MaterializationPlan Plan;
        public IntendedRole IntendedRole;
        public HexCoord? TargetContext;
        public StrategicUseScoreBreakdown Breakdown;
        public float TotalUseScore;   // == Breakdown.Total
        // Retained only so ToCompact()'s "hold" column and MaterializationCandidateBuilder's
        // Decide()/logging keep compiling against a stable shape — the Hold mechanic itself was
        // removed (user call: always play the best Total, never park a card "for later"). Always 0.
        public float HoldValue;
        public MaterializationQualityBreakdown QualityBreakdown;

        // The manager admits on this. No Hold counterweight any more — NetScore == TotalUseScore.
        public float NetScore => TotalUseScore;
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
        // P1.7 / review-r4 P1 ARCH — capability coverage, derived DYNAMICALLY from what the AI's
        // deployed armies + hand actually contain, now as a generic RoleCoverage from
        // StrategicEffectRegistry. A new counter/support/mobility role flows in with zero edits.
        public readonly RoleCoverage Coverage;
        public readonly int CombatActors;
        public readonly float FreeFieldPower;
        // Attack/Defence axes create demand; Production reinforces an already-justified demand and
        // must never self-originate a reason to spend. True only when a live snapshot fact PROVES a
        // military witness exists: a known neutral Raid target (Aggression) or an asset threat
        // at/above the shared threatSeverityTrigger. Need is still computed above for its own sake,
        // but ForceGrowthValue must not scale by it unless this is true.
        public readonly bool MilitaryWitnessed;

        public bool HasAntiAir  => Coverage.Has(IntendedRole.AntiAir);
        public bool HasAntiArmor => Coverage.Has(IntendedRole.AntiArmor);
        public bool HasMobile    => Coverage.Has(IntendedRole.MobileCombat);
        public bool HasSupport   => Coverage.Has(IntendedRole.Support);

        public BaselineForceReadiness(float need, bool hasScout, bool hasFieldBody, bool hasHero,
            bool hasAir, RoleCoverage coverage, int combatActors, float freeFieldPower,
            bool militaryWitnessed = false)
        {
            Need = need;
            HasScout = hasScout;
            HasFieldBody = hasFieldBody;
            HasHero = hasHero;
            HasAir = hasAir;
            Coverage = coverage;
            CombatActors = combatActors;
            FreeFieldPower = freeFieldPower;
            MilitaryWitnessed = militaryWitnessed;
        }

        public static BaselineForceReadiness Evaluate(WorldSnapshot snap, CapabilityInventory inv)
            => Evaluate(snap, inv, (IReadOnlyList<CardData>)null);

        public static BaselineForceReadiness Evaluate(WorldSnapshot snap, CapabilityInventory inv,
            IReadOnlyList<CardData> hand)
        {
            if (snap?.Self == null)
                return new BaselineForceReadiness(0f, false, false, false, false, RoleCoverage.None, 0, 0f);

            int combatActors = 0;
            bool hasAirArmy = false;
            RoleCoverage coverage = RoleCoverage.None;
            if (snap.Self.Armies != null)
                foreach (ArmySnapshot a in snap.Self.Armies)
                {
                    if (a == null || a.MemberCount <= 0 || a.IsPrison)
                        continue;
                    coverage = coverage.Union(a.StrategicCoverage);
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
                    // review-r4 finding 8.1 — coverage is read BEFORE the recce short-circuit:
                    // DeriveRoles gives a Scout+AntiAir card BOTH the Scout AND the AntiAir role, so
                    // it must count toward AA coverage too. finding 8.2 — power / moveMax come from
                    // the EFFECTIVE stat line (attached equipment folded in at the stats level).
                    // P1 ARCH — the coverage roles come from StrategicEffectRegistry, not a fixed
                    // ability list. (CoverageOf applies its own !recce gate to MobileCombat.)
                    AiPower.ProjectedStrategicLine line = AiPower.EffectiveLine(d, c.Equipment?.equipment);
                    coverage = coverage.Union(StrategicEffectRegistry.CoverageOf(eff, line.MoveMax));
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

            // A real, live military witness: a known neutral Raid target (Aggression), or an asset
            // threat at/above the shared threatSeverityTrigger. Neither requires durable intent
            // state — both are snapshot facts already computed elsewhere for the same purpose
            // (Aggression objective discovery / DemandLayer.Economy's threat-severity gate).
            bool militaryWitnessed =
                (snap.Known?.NeutralSightings != null && snap.Known.NeutralSightings.Count > 0)
                || (snap.Threat?.Threats != null && snap.Threat.Threats
                    .Any(t => t != null && t.Severity >= AiConfigV2.threatSeverityTrigger));

            return new BaselineForceReadiness(need, hasScout, hasFieldBody, hasHero, hasAir,
                coverage, combatActors, freeFieldPower, militaryWitnessed);
        }
    }

    internal static class StrategicCardEvaluator
    {
        // Generated Equipment strengthens an EXISTING host: price its marginal value through the
        // ONE EquipmentUpgradeValue, never the host's total combat power. Development PREPARE
        // prices its projected output through this same method; its Ev only adds the
        // prerequisite investment. WHEN Research/Production may spend at all is
        // DevelopmentInvestmentGate's decision (resources the main deck does not absorb), so no
        // displaced-alternative or economy multiplier is priced here a second time. Phase A and
        // other card chains share this evaluator's ONE AP/resource/chain cost function.
        // Research and Production labels never decide output type: use the actual card definition.
        internal static float ScoreGeneratedEquipmentUpgrade(DevelopmentOpportunity op,
            MaterializationPlan plan, WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx)
        {
            if (op == null || op.Card == null || op.Card.cardType != CardType.Equipment
                || plan == null || plan.Kind != MaterializationChainKind.GenerateAttachUpgrade
                || !object.ReferenceEquals(plan.GeneratedEquipmentDef, op.Card)
                || !object.ReferenceEquals(plan.Generation?.CardDef, op.Card))
                return float.NegativeInfinity;

            float marginalBenefit = op.SuccessChance * EquipmentUpgradeValue(op);
            System.Func<ResourceType, float> spendable = root == null ? null
                : (System.Func<ResourceType, float>)(type =>
                    StrategicSpendability.SpendableAmount(player, root, ctx, type));
            return marginalBenefit - ResourceCost(plan, snap, spendable, player);
        }

        // -----------------------------------------------------------------------------------------
        //  PHASE A — a chain closing an explicit AxisDemand. The demand pins the primary role.
        // -----------------------------------------------------------------------------------------
        // `witnessedUsefulApDemand` — the owner-witnessed AP workload StrategicManager Phase A
        // assembled for this scoring pass (legal card-AP workload + committed movers + witnessed
        // air). Null => the EffectEvaluationContext falls back to the discounted structural estimate.
        // `projectedLegalFillers` — for a HERO plan, the extra non-hero bodies that could ALSO
        // legally land in the same recipient this turn (Command 6-vs-7). 0 for a non-hero plan or a
        // call site with no candidate set.
        public static StrategicCardUseCandidate ScoreForDemand(MaterializationPlan plan, AxisDemand demand,
            TraitPreference projected, CapabilityInventory inv, int referenceMoveMax,
            bool hasCompetingHeroDemand, WorldSnapshot snap,
            float? witnessedUsefulApDemand = null, int projectedLegalFillers = 0,
            System.Func<ResourceType, float> spendableResource = null,
            PlayerSetupData player = null)
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
            IReadOnlyList<string> pabil = plan.ProjectedAbilities ?? pdef?.grantedAbilities;
            bool recceCard = AbilityParams.AbilitiesHaveAnyRecce(pabil);
            float equipUpgrade = plan.UsesEquipment ? EquipmentUpgradeValue(plan, snap, inv) : 0f;

            // Built before RoleFitCore so HeroLeadershipFit can read the destination army's capacity
            // off the SAME EffectEvaluationContext.ResolveDestination walk the ability registry uses
            // below, instead of re-walking snap.Self.Armies a second time (single-count invariant
            // extended to the destination-army lookup, not just the score terms).
            var ectx = new EffectEvaluationContext(snap, plan, witnessedUsefulApDemand);
            float roleFitCore = RoleFitCore(role, plan, inv, recceCard, heroCard,
                pabil, ectx, 0f, equipUpgrade,
                demand, referenceMoveMax, hasCompetingHeroDemand, projectedLegalFillers,
                out MaterializationQualityBreakdown qbd, out string heroCmdDetail);

            // P1 ARCH — ALL ability-derived value (AntiAir/AntiArmor/Support today; AoE/regen/aura/
            // summon later) comes from the registry as a per-axis EffectContribution, added to the
            // matching bd.* term exactly once. RoleFit is target-fit-scaled like the core; the
            // PlayerGlobal terms (ec.Global*) are target-INDEPENDENT — added AFTER the fit multiplier.
            EffectContribution ec = StrategicEffectRegistry.Contributions(
                role, pabil, EffectiveMoveMax(plan), ectx, out string effDetail);
            bd.EffectDetail = JoinDetail(effDetail, heroCmdDetail);

            bd.RoleFit = fit * (roleFitCore + ec.RoleFit) + ec.GlobalRoleFit;
            bd.ImmediateTempo = traitMatch + PlacementBonus(plan.Deploy.Kind)
                + ec.ImmediateTempo + ec.GlobalImmediateTempo;
            bd.NextTurnPotential = NextTurnPotential(plan, role);
            bd.SynergyValue = SynergyValue(plan, snap, inv) + ec.Synergy + ec.GlobalSynergy;
            // Zeroed for a hero card (both phases now) — a hero's own body already prices into
            // HeroLeadershipFit's combatPart; ForceGrowthValue's SurplusCombatReadinessUtility-based
            // marginal power would double-count the same Attack/Defense/HP line a second time.
            bd.ForceGrowthValue = (heroCard ? 0f : ForceGrowthValue(plan, demand.Capability, baseline))
                + ec.ForceGrowth + ec.GlobalForceGrowth;
            // The AntiAir/AntiArmor ec.ThreatResponse contribution is folded into this axis (same
            // as Phase B).
            bd.ThreatCounterValue = CapabilityGapValue(demand.Capability, inv, baseline, role, roleFitCore)
                + ec.CapabilityGap + ec.GlobalCapabilityGap + ec.ThreatResponse + ec.GlobalThreatResponse;

            bd.ResourceEfficiency = -ResourceCost(plan, snap, spendableResource, player);

            bd.RedundancyPenalty = -ScoutOversupplyPenalty(role, inv);
            // Phase A form of AlternativeUseValue: the scarcity opportunity cost of spending this
            // exact card body (a scarce hero on a non-hero demand, a unique stealth item on a
            // non-stealth demand) — the general "best other role" cost applies in Phase B.
            bd.AlternativeUseValue = -ScarcityOpportunityCost(plan, demand, inv);
            bd.ResourcePressureBenefit = 0f;     // spends a ledger entitlement, not stranded AP
            bd.HandPressureBenefit = 0f;
            bd.GenerationRiskDiscount = GenerationExpectedValueDiscount(bd, GenerationChance(plan));
            bd.Total = SumTotal(bd);
            bd.HoldValue = 0f;   // Hold mechanic removed — always play the best Total.

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
            bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected, WorldSnapshot snap,
            float? witnessedUsefulApDemand = null, int projectedLegalFillers = 0,
            System.Func<ResourceType, float> spendableResource = null, PlayerSetupData player = null)
        {
            CardDefinition def = PlanBaseDef(plan);
            BaselineForceReadiness baseline = BaselineForceReadiness.Evaluate(snap, inv, hand?.Hand);
            IReadOnlyList<IntendedRole> roles = DeriveRoles(def, projected, plan, recce, hero);
            float versatility = RoleVersatility(roles);

            var scored = new List<StrategicCardUseCandidate>(roles.Count);
            foreach (IntendedRole role in roles)
                scored.Add(ScoreSurplusRole(plan, role, inv, recce, hero, hand, projected, snap,
                    baseline, versatility, witnessedUsefulApDemand, projectedLegalFillers,
                    spendableResource, player));

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
            win.HoldValue = 0f;   // Hold mechanic removed — always play the best Total.
            win.Breakdown.HoldValue = 0f;
            return win;
        }

        private static StrategicCardUseCandidate ScoreSurplusRole(MaterializationPlan plan, IntendedRole role,
            CapabilityInventory inv, bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected,
            WorldSnapshot snap, BaselineForceReadiness baseline, float versatility,
            float? witnessedUsefulApDemand = null, int projectedLegalFillers = 0,
            System.Func<ResourceType, float> spendableResource = null, PlayerSetupData player = null)
        {
            var bd = new StrategicUseScoreBreakdown();
            float traits = projected != null && AbilityParams.AbilitiesHaveAnyStealth(projected)
                ? AiConfigV2.stratTraitMatchBonus : 0f;
            // final closure §4 — the recurring-AP immediate-tempo value is NO LONGER a special-case
            // here. It is a second explicit descriptor contribution (EffectField.ImmediateTempo on
            // the ApBonus row) and arrives below via ec.ImmediateTempo, counted exactly once and
            // identically in Phase A. The evaluator no longer knows RecurringResource is special.
            float equipmentUpgrade = plan.UsesEquipment ? EquipmentUpgradeValue(plan, snap, inv) : 0f;

            // Built before RoleFitCore — see ScoreForDemand's comment on the same ordering.
            var ectx = new EffectEvaluationContext(snap, plan, witnessedUsefulApDemand);
            float roleFitCore = RoleFitCore(role, plan, inv, recce, hero, projected, ectx, versatility,
                equipmentUpgrade, null, 0, false, projectedLegalFillers, out _, out string heroCmdDetail);
            // P1 ARCH — every ability-derived value comes from the registry as a per-axis
            // EffectContribution (see ScoreForDemand).
            EffectContribution ec = StrategicEffectRegistry.Contributions(
                role, projected, EffectiveMoveMax(plan), ectx, out string effDetail);
            bd.EffectDetail = JoinDetail(effDetail, heroCmdDetail);

            // Phase B has no target-fit multiplier, so local + PlayerGlobal (ec.Global*) simply add
            // into the same breakdown axis; the descriptor's EffectField still routes each.
            // ResourceGain excluded from ec.GlobalRoleFit: that dynamic GlobalRecurringValue figure
            // (yield x horizon x marginalApUtility x saturation) prices the SAME "this card produces
            // a resource" fact that ResourceGainRoleFit's own weight already exists to express —
            // adding both double-counts it under this one role's Total. Other roles scoring the SAME
            // card (e.g. it loses the contest and plays as CombatBody) still get ec.GlobalRoleFit as
            // before — this exclusion is scoped to the ResourceGain role only, not the ability.
            bd.RoleFit = roleFitCore + (role == IntendedRole.ResourceGain ? 0f : ec.RoleFit + ec.GlobalRoleFit);
            // Support and its Development facility are the same real thing (a Researcher/Assembler
            // hero needs a Lab/Factory to man, on the SAME hex; one without the other does
            // nothing), so Support's RoleFit uses the SAME formula ScoreNonCombat uses for a
            // Facility: full value when a facility to operate actually exists
            // (snap.Self.HasDevFacility), zero when it does not.
            if (role == IntendedRole.Support)
            {
                float supportEco = snap?.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;
                bool supportHasFacility = snap?.Self?.HasDevFacility == true;
                bd.RoleFit = supportHasFacility
                    ? AiConfigV2.nonCombatFacilityValue + (1f - supportEco) * AiConfigV2.nonCombatEconomyRunwayBonus
                    : 0f;
                bd.EffectDetail = JoinDetail(bd.EffectDetail,
                    $"support facilityGate hasFacility={supportHasFacility} "
                    + $"eco={supportEco.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"roleFit={bd.RoleFit.ToString("0.00", CultureInfo.InvariantCulture)}");
            }
            // ImmediateTempo/NextTurnPotential stay at their default (0) in Phase B: every Phase-B
            // candidate is already immediately playable, so a "now vs later" axis adds nothing
            // RoleFit/ThreatCounterValue do not already carry. They are live in Phase A's
            // ScoreForDemand.
            //
            // ThreatCounterValue: AntiAir/AntiArmor only (Scout's "0 scouts" case lives in
            // RoleFitCore). ec.ThreatResponse is folded in here rather than kept as a separate
            // field — it fires on the exact same ThreatPresent condition, so a separate field would
            // double-count. Unit-only: AntiAir/Hyperkinetic are unit abilities in practice;
            // excluding hero makes that explicit.
            bd.ThreatCounterValue = (role == IntendedRole.Hold ? 0f
                : SurplusThreatCounter(role, inv, baseline, snap, roleFitCore)) + ec.CapabilityGap + ec.GlobalCapabilityGap
                + (hero ? 0f : ec.ThreatResponse + ec.GlobalThreatResponse);
            // Zeroed for Scout/Hold/ResourceGain/Support: RoleFitCore is deliberately 0 or
            // role-specific for those roles, and ForceGrowthValue's SurplusCombatReadinessUtility
            // call ignores role — without this exclusion those roles would earn standing-force
            // credit their own RoleFitCore says they should not compete on. Also zeroed for ANY
            // hero card regardless of role: a hero's Attack/Defense/HP line already prices into
            // HeroLeadershipFit.combatPart inside RoleFitCore, so ForceGrowthValue would
            // double-count it.
            bd.ForceGrowthValue = (role == IntendedRole.Scout || role == IntendedRole.Hold
                || role == IntendedRole.ResourceGain || role == IntendedRole.Support || hero
                ? 0f : ForceGrowthValue(plan, plan.FinalCapability, baseline))
                + ec.ForceGrowth + ec.GlobalForceGrowth;
            // EquipmentUpgrade already prices this exact delta in RoleFitCore.
            bd.SynergyValue = traits * 0.5f
                + (role == IntendedRole.EquipmentUpgrade ? 0f : equipmentUpgrade)
                + ec.Synergy + ec.GlobalSynergy;
            bd.ResourceEfficiency = -ResourceCost(plan, snap, spendableResource, player);
            bd.RedundancyPenalty = -ScoutOversupplyPenalty(role, inv);
            bd.AlternativeUseValue = -SurplusScarceBodyFloor(plan, role, inv, hero);
            bd.ResourcePressureBenefit = 0f;   // no caller-side surplus correction; NetScore is final
            bd.HandPressureBenefit = hand != null && !hand.HasFreeSlot ? AiConfigV2.surplusHandPressureBonus : 0f;
            bd.GenerationRiskDiscount = GenerationExpectedValueDiscount(bd, GenerationChance(plan));
            bd.Total = SumTotal(bd);
            bd.HoldValue = 0f;   // Hold mechanic removed — always play the best Total.

            return new StrategicCardUseCandidate
            {
                Plan = plan,
                IntendedRole = role,
                Breakdown = bd,
                TotalUseScore = bd.Total,
                HoldValue = bd.HoldValue,
            };
        }

        // The CORE RoleFit — the non-ability, characteristic-driven part (Scout quality profile /
        // hero combat-leadership / AiPower marginal readiness / equipment delta). Ability-derived
        // value is added ONCE by the caller from StrategicEffectRegistry.Contributions(...).RoleFit
        // (P1 ARCH — so a new mechanic reaches every role, not just Support). The Hero card class is
        // never a flat term.
        private static float RoleFitCore(IntendedRole role, MaterializationPlan plan, CapabilityInventory inv,
            bool recce, bool hero, IReadOnlyList<string> projected, EffectEvaluationContext ectx, float versatility,
            float equipmentUpgrade, AxisDemand demand, int referenceMoveMax, bool hasCompetingHeroDemand,
            int projectedLegalFillers,
            out MaterializationQualityBreakdown qbd, out string heroCmdDetail)
        {
            qbd = MaterializationQualityBreakdown.Neutral();
            heroCmdDetail = null;
            switch (role)
            {
                case IntendedRole.Scout:
                {
                    AxisDemand d = demand != null && demand.Capability == CapabilityKind.ScoutCapability
                        ? demand
                        : new AxisDemand { Capability = CapabilityKind.ScoutCapability };
                    float mult = CapabilityQualityEvaluator.QualityMultiplier(
                        plan, d, inv, referenceMoveMax, hasCompetingHeroDemand, out qbd);
                    float fit = AiConfigV2.scoutBaseRoleFit * mult;
                    // Phase B only (demand == null — Phase A always passes a real AxisDemand): the
                    // "AI currently has zero scouts at all" signal is part of RoleFit. Phase A
                    // keeps its own separate CapabilityGapValue (ScoutCapability) term. q-scaled: a
                    // weak scout candidate gets proportionally less credit for closing the gap than
                    // a strong one.
                    if (demand == null && inv != null && inv.TotalScouts <= 0)
                        fit += AiConfigV2.capabilityGapValue * Mathf.Clamp01(mult);
                    return fit;
                }
                case IntendedRole.EquipmentUpgrade:
                    return equipmentUpgrade;
                case IntendedRole.Support:
                    // P2b — no separate hero-support term: the registry already prices Researcher /
                    // Assembler (for a Unit OR a Hero) exactly once. Core Support fit is 0.
                    return 0f;
                case IntendedRole.ResourceGain:
                    // A ResourceGain hero's Command (extra battle slots it unlocks elsewhere) is
                    // orthogonal to its recurring-resource specialty, so it is credited here. Only
                    // the COMMAND part, never combatPart: this role deliberately does not compete
                    // on the hero's own body strength (see ResourceGainRoleFit — ForceGrowthValue
                    // etc. are zeroed for the same reason).
                    return ResourceGainRoleFit(ectx)
                        + (hero ? HeroCommandMarginalValue(PlanBaseDef(plan), ectx, projectedLegalFillers,
                            out heroCmdDetail) : 0f);
                case IntendedRole.CombatBody:
                case IntendedRole.ForceGrowth:
                case IntendedRole.MobileCombat:
                case IntendedRole.AntiArmor:
                case IntendedRole.AntiAir:
                    return SurplusCombatReadinessUtility(plan)
                        + HeroLeadershipFit(plan, hero, ectx, projectedLegalFillers, out heroCmdDetail);
                case IntendedRole.Hold:
                    return 0f;
                default:
                    return versatility;
            }
        }

        // AI-MGR — ResourceGain's OWN weight. Before this role existed, a PlayerGlobal
        // recurring-resource card (ApBonus/ProduceHuman/ProduceMaterials/...) only ever won the
        // internal per-card role contest by accident, riding CombatBody's combat/command score
        // while the actual reason to play it sat unlabeled inside ec.GlobalRoleFit (added to
        // EVERY role identically — StrategicEffectRegistry.Contributions treats PlayerGlobal
        // effects as role-independent by design, and that stays true here too: this term does
        // NOT duplicate ec.GlobalRoleFit's yield×horizon×marginalUtility×saturation math, it only
        // gives the ResourceGain role enough of its own RoleFitCore to surface as itself in
        // role= logging and to win the contest on a card with a weak/no combat body.
        // "Earlier is better": expressed directly and strongly here — a fast turn-decay down to a
        // floor — rather than relying on EffectEvaluationContext.RecurringFutureOpportunity's
        // existing turn factor, which is deliberately WEAK (one of four blended inputs, ramped
        // over turns 6-40) because it also has to serve cards with no early-game urgency story.
        private static float ResourceGainRoleFit(EffectEvaluationContext ectx)
        {
            float earlyBias = Mathf.Lerp(1f, AiConfigV2.resourceGainEarlyTurnFloor,
                Curves.Ramp(ectx.TurnNumber,
                    AiConfigV2.resourceGainEarlyRampLo, AiConfigV2.resourceGainEarlyRampHi));
            return AiConfigV2.resourceGainRoleFitBase * earlyBias;
        }

        // Effective moveMax of the plan's END RESULT (base + already-attached + planned equipment)
        // — role derivation, coverage and scoring all read the SAME AiPower.ProjectMaterialization
        // line, so a +MoveMax trinket over the mobile threshold is seen everywhere.
        private static int EffectiveMoveMax(MaterializationPlan plan)
            => AiPower.ProjectMaterialization(plan).MoveMax;

        // -----------------------------------------------------------------------------------------
        //  NON-COMBAT CARDS  (Aviation / Facility / standalone Equipment)
        // -----------------------------------------------------------------------------------------
        // `generation` non-null: this play must first MINT its card through a Challenge. The
        // evaluator, not the caller, is the single authoritative score: the generation ResourceCost
        // (really paid by TryGenerate — the pre-mint stand-in's EffectivePlayResourceCost is null),
        // the success-chance discount and the generation step penalty are all folded into the
        // returned NetScore here. Callers do NOT post-multiply.
        public static StrategicCardUseCandidate ScoreNonCombat(NonCombatRole kind, CardData card,
            WorldSnapshot snap, CapabilityInventory inv, AiHandData hand, float bestEquipmentUpgrade,
            GenerationStep generation = null, float? witnessedUsefulApDemand = null,
            float? actualApCost = null, ResourceCost actualResourceCost = null,
            System.Func<ResourceType, float> spendableResource = null, PlayerSetupData player = null,
            TaskScore? operationalTask = null)
        {
            var bd = new StrategicUseScoreBreakdown();
            CardDefinition def = card?.Definition;
            IntendedRole role;
            float apCost = actualApCost ?? (card != null ? card.EffectivePlayApCost : 0f);
            if (!actualApCost.HasValue && generation != null)
                apCost += ResearchProductionSystem.AttemptApCost(generation.CardDef);
            ResourceCost pricedResources = actualResourceCost ?? card?.EffectivePlayResourceCost;
            if (actualResourceCost == null && generation?.CardDef?.resourceCost != null)
                pricedResources = AddResourceCosts(pricedResources, generation.CardDef.resourceCost);

            float eco = snap?.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;
            bool hasAirCapacity = snap?.Self != null
                && (snap.Self.AirborneReconWings + snap.Self.SpareAirObservationSorties) > 0;
            // Calibration detail for the Equipment devPipeline floor and the Aviation upkeep
            // penalty — merged into bd.EffectDetail below so both are visible on the SAME
            // strat.nonCombat AiDebug line, for tuning against a real game.
            string calibrationDetail = null;

            switch (kind)
            {
                case NonCombatRole.Aviation:
                    role = IntendedRole.Aviation;
                    // "No air observation capacity at all" is part of RoleFit: it is a real
                    // coverage gap like Scout's "0 scouts" case, not an AntiAir/AntiArmor
                    // enemy-composition matchup (ThreatCounterValue).
                    bd.RoleFit = AiConfigV2.nonCombatAviationBaseValue
                                 + (hasAirCapacity ? 0f : AiConfigV2.nonCombatAviationNoAirGap);
                    break;
                case NonCombatRole.Facility:
                    role = FacilityRole(def);
                    bd.RoleFit = AiConfigV2.nonCombatFacilityValue
                                 + (1f - eco) * AiConfigV2.nonCombatEconomyRunwayBonus;
                    break;
                default:
                    role = IntendedRole.EquipmentUpgrade;
                    // bestEquipmentUpgrade is EquipmentUpgradeValue for the chosen host — the ONE
                    // equipment value every other equipment path also prices.
                    bd.RoleFit = bestEquipmentUpgrade;
                    calibrationDetail = $"equip value={bestEquipmentUpgrade.ToString("0.00", CultureInfo.InvariantCulture)}";
                    break;
            }

            // AI-MGR §1 — close the Unit/Hero <-> Base/Facility gap: a non-combat card's abilities go
            // through the SAME StrategicEffectRegistry as a Unit/Hero chain. Today this carries the
            // PlayerGlobal ApBonus value (Ashen / Concord Base, an ApBonus Facility, a generated
            // Base) via the identical dynamic model — no FacilityApBonusScore / BaseAbilityEvaluator.
            IReadOnlyList<string> ncAbilities = def?.grantedAbilities;
            var ncCtx = new EffectEvaluationContext(snap, witnessedUsefulApDemand);
            EffectContribution ncEc = StrategicEffectRegistry.Contributions(
                role, ncAbilities, def != null ? def.moveMax : 0, ncCtx, out string ncEffDetail);
            // No target-fit multiplier in the non-combat lane — local and PlayerGlobal
            // (ncEc.Global*) add into the same axis, each routed by its descriptor's EffectField.
            // ImmediateTempo is not used in Phase B (see ScoreSurplusRole). ThreatCounterValue is
            // not used in the non-combat lane: Aviation/Facility/Equipment are never
            // AntiAir/AntiArmor counters, so ncEc.CapabilityGap/ncEc.ThreatResponse fold into
            // RoleFit instead.
            bd.RoleFit += ncEc.RoleFit + ncEc.GlobalRoleFit
                + ncEc.CapabilityGap + ncEc.GlobalCapabilityGap
                + ncEc.ThreatResponse + ncEc.GlobalThreatResponse;
            bd.ForceGrowthValue += ncEc.ForceGrowth + ncEc.GlobalForceGrowth;
            bd.SynergyValue += ncEc.Synergy + ncEc.GlobalSynergy;
            bd.EffectDetail = JoinDetail(ncEffDetail, calibrationDetail);

            bd.OperationalTaskValue = operationalTask?.Value ?? 0f;
            bd.HandPressureBenefit = hand != null && !hand.HasFreeSlot ? AiConfigV2.surplusHandPressureBonus : 0f;
            float genStepPenalty = generation != null ? AiConfigV2.stratChainGenerationStepPenalty : 0f;
            // Aviation sortie-upkeep penalty — a new wing does not just cost its own AP/resources
            // to deploy, it keeps drawing apAirSortieApProxy AP/turn to actually fly afterwards.
            // Dynamic, not flat: scaled by ncCtx.EffectiveMarginalApUtility (the SAME [0..1] "is AP
            // already the binding constraint right now" ramp every other dynamic effect in this
            // file uses) and by effectRecurringHorizonTurns (the SAME bounded pay-back horizon the
            // ApBonus/Produce recurring model uses), since upkeep recurs every future turn. At full
            // AP pressure this can fully offset nonCombatAviationNoAirGap — a wing you cannot
            // afford to fly is not a real capability gain.
            float aviationUpkeepPenalty = kind == NonCombatRole.Aviation
                ? AiConfigV2.apAirSortieApProxy * AiConfigV2.effectRecurringHorizonTurns
                  * AiConfigV2.stratCardApCostWeight * ncCtx.EffectiveMarginalApUtility
                : 0f;
            if (kind == NonCombatRole.Aviation)
                bd.EffectDetail = JoinDetail(bd.EffectDetail,
                    $"aviation upkeep marginalApUtil={ncCtx.EffectiveMarginalApUtility.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"penalty={(-aviationUpkeepPenalty).ToString("0.00", CultureInfo.InvariantCulture)}");
            bd.ResourceEfficiency = -(AiConfigV2.stratCardApCostWeight * apCost
                                      + StrategicResourceCostValue(pricedResources, snap, spendableResource, player)
                                      + genStepPenalty + aviationUpkeepPenalty);
            // Challenge cost is certain; every benefit of the minted card is success-contingent.
            bd.GenerationRiskDiscount = generation != null
                ? GenerationExpectedValueDiscount(bd, Mathf.Clamp01(generation.SuccessChance))
                : 0f;
            bd.Total = SumTotal(bd);
            bd.HoldValue = 0f;   // Hold mechanic removed — always play the best Total.

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
            b.RoleFit + b.ImmediateTempo + b.NextTurnPotential + b.ThreatCounterValue
            + b.ForceGrowthValue + b.ResourceEfficiency + b.SynergyValue
            + b.GenerationRiskDiscount + b.RedundancyPenalty + b.AlternativeUseValue
            + b.ResourcePressureBenefit + b.HandPressureBenefit
            + b.OperationalTaskValue;

        // AP + resource cost + extra-chain-step penalty. The ONLY place a chain is charged for cost.
        private static float ResourceCost(MaterializationPlan plan, WorldSnapshot snap,
            System.Func<ResourceType, float> spendableResource = null, PlayerSetupData player = null)
        {
            if (plan == null) return 0f;
            return AiConfigV2.stratCardApCostWeight * plan.ApCost
                   + StrategicResourceCostValue(plan.ResCost, snap, spendableResource, player)
                   + ChainStepPenalty(plan.Kind);
        }

        private static float GenerationChance(MaterializationPlan plan) =>
            plan?.Generation != null ? Mathf.Clamp01(plan.Generation.SuccessChance) : 1f;

        // Challenge AP/resources are paid with certainty. Every other term describes value that
        // exists only after a successful mint, so remove the failure share from that value.
        private static float GenerationExpectedValueDiscount(StrategicUseScoreBreakdown b, float chance)
        {
            if (b == null || chance >= 1f)
                return 0f;
            float contingent = b.RoleFit + b.ImmediateTempo + b.NextTurnPotential
                + b.ThreatCounterValue + b.ForceGrowthValue
                + b.SynergyValue + b.RedundancyPenalty
                + b.AlternativeUseValue + b.ResourcePressureBenefit + b.HandPressureBenefit
                + b.OperationalTaskValue;
            return -(1f - Mathf.Clamp01(chance)) * Mathf.Max(0f, contingent);
        }

        private static ResourceCost AddResourceCosts(ResourceCost a, ResourceCost b)
        {
            int h = (a?.human ?? 0) + (b?.human ?? 0);
            int e = (a?.energy ?? 0) + (b?.energy ?? 0);
            int m = (a?.materials ?? 0) + (b?.materials ?? 0);
            int t = (a?.tech ?? 0) + (b?.tech ?? 0);
            return (h | e | m | t) == 0
                ? null : new ResourceCost { human = h, energy = e, materials = m, tech = t };
        }

        // =======================================================================================
        //  SPEC TERMS
        // =======================================================================================
        private static float ForceGrowthValue(MaterializationPlan plan, CapabilityKind cap,
            BaselineForceReadiness baseline)
        {
            if (cap != CapabilityKind.FieldCombatPower && cap != CapabilityKind.Hero)
                return 0f;
            // Attack/Defence axes create demand; Production reinforces an already-justified demand.
            // Without a live military witness (see BaselineForceReadiness.MilitaryWitnessed),
            // standing-force Need must not self-originate a reason to build combat mass.
            if (!baseline.MilitaryWitnessed)
                return 0f;
            float marginal = SurplusCombatReadinessUtility(plan);
            if (marginal <= 0f)
                return 0f;
            float scale = Mathf.Lerp(AiConfigV2.baselineReadinessGrowthFloor, 1f, Mathf.Clamp01(baseline.Need));
            return marginal * scale * AiConfigV2.forceGrowthValueWeight;
        }

        // P0 — how much of THIS role's own established RoleFitCore ceiling this specific card
        // actually reaches, in [0..1]. Reuses the SAME scale RoleFit already established per role
        // (SurplusCombatReadinessUtility's own [0..2] clamp for combat roles, scoutBaseRoleFit for
        // Scout) instead of inventing a second quality metric — so a weak body closing a coverage
        // gap earns less credit than a strong one, and a flat "gap exists" toggle never fires on its
        // own. Support/EquipmentUpgrade/Hold/etc. have no ability-independent RoleFitCore to scale
        // by (it's 0 by design, P1.5) — CapabilityGapValue is 0 for those, deliberately: Support's
        // real demand gate lives in the Development axis (Phase A), not a flat Phase-B nudge.
        private static float GapQualityFraction(IntendedRole role, float roleFitCore)
        {
            switch (role)
            {
                case IntendedRole.Scout:
                    return Mathf.Clamp01(roleFitCore / Mathf.Max(0.01f, AiConfigV2.scoutBaseRoleFit));
                case IntendedRole.CombatBody:
                case IntendedRole.ForceGrowth:
                case IntendedRole.MobileCombat:
                case IntendedRole.AntiArmor:
                case IntendedRole.AntiAir:
                    return Mathf.Clamp01(roleFitCore / 2f);   // SurplusCombatReadinessUtility's own [0..2] ceiling
                default:
                    return 0f;
            }
        }

        // P1.7 — "the AI lacks this capability class", scaled by GapQualityFraction (was a flat
        // binary toggle, not scaled by Need — Need is already priced once, in ForceGrowthValue).
        private static float CapabilityGapValue(CapabilityKind cap, CapabilityInventory inv,
            BaselineForceReadiness baseline, IntendedRole role, float roleFitCore)
        {
            if (inv == null) return 0f;
            float q = GapQualityFraction(role, roleFitCore);
            switch (cap)
            {
                case CapabilityKind.ScoutCapability:
                    return inv.TotalScouts <= 0 ? AiConfigV2.capabilityGapValue * q : 0f;
                case CapabilityKind.Hero:
                    return (inv.AvailableHeroes + inv.CommittedHeroes) <= 0 ? AiConfigV2.capabilityGapValue * q : 0f;
                case CapabilityKind.FieldCombatPower:
                    return baseline.HasFieldBody ? 0f : AiConfigV2.capabilityGapValue * q;
                default:
                    return 0f;
            }
        }

        // An AA/AT counter only counts when the matching enemy threat is actually present (scaled
        // by GapQualityFraction). AntiAir/AntiArmor only — Scout's "0 scouts" case lives in
        // RoleFitCore.
        private static float SurplusThreatCounter(IntendedRole role, CapabilityInventory inv,
            BaselineForceReadiness baseline, WorldSnapshot snap, float roleFitCore)
        {
            float q = GapQualityFraction(role, roleFitCore);
            switch (role)
            {
                case IntendedRole.AntiAir:
                    return !baseline.HasAntiAir && EnemyThreatModel.ThreatPresent(IntendedRole.AntiAir, snap)
                        ? AiConfigV2.capabilityGapValue * q : 0f;
                case IntendedRole.AntiArmor:
                    return !baseline.HasAntiArmor && EnemyThreatModel.ThreatPresent(IntendedRole.AntiArmor, snap)
                        ? AiConfigV2.capabilityGapValue * q : 0f;
                case IntendedRole.Support:
                    // No ability-independent RoleFitCore to scale by (it's 0 for Support, P1.5) —
                    // Support's real demand gate lives in the Development axis (Phase A).
                    return 0f;
                // No MobileCombat case: mobility already has its own RoleFit signal —
                // StrategicEffectRegistry.Roles adds effectMobileBaseFit (0.20) to RoleFit for any
                // fast (moveMax>=5) non-Recce body, so a "the AI has none yet" gap bonus would
                // duplicate it.
                default:
                    return 0f;
            }
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

        private static float SynergyValue(MaterializationPlan plan, WorldSnapshot snap,
            CapabilityInventory inv)
        {
            if (plan == null)
                return 0f;
            float v = 0f;
            if (plan.UsesEquipment)
                v += EquipmentUpgradeValue(plan, snap, inv);
            if ((plan.ExpectedTraits & TraitPreference.Stealth) != 0)
                v += AiConfigV2.stratTraitMatchBonus * 0.5f;
            return v;
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
                case CapabilityKind.DevelopmentOperator: return IntendedRole.Development;
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
            if (abilities != null && (abilities.Contains(UnitAbilities.Researcher)
                                      || abilities.Contains(UnitAbilities.Assembler)))
                roles.Add(IntendedRole.Development);
            // A PlayerGlobal recurring-resource carrier (ApBonus/ProduceHuman/ProduceMaterials/...)
            // gets its own contestable role — see ResourceGainRoleFit.
            if (abilities != null && StrategicEffectRegistry.HasGlobalRecurringEffect(abilities))
                roles.Add(IntendedRole.ResourceGain);
            // P1 ARCH — every ability/stat-derived role (AntiAir / AntiArmor / Support / MobileCombat
            // today) comes from the registry, off the SAME effective moveMax role-fit / readiness use
            // (a +MoveMax trinket that crosses the mobile threshold is now seen here too).
            roles.AddRange(StrategicEffectRegistry.Roles(abilities, EffectiveMoveMax(plan)));
            if (plan != null && plan.UsesEquipment)
                roles.Add(IntendedRole.EquipmentUpgrade);
            // review-r3 — Hold is NOT a play role: it never goes through ScoreSurplusRole (which
            // would give it ImmediateTempo / NewArmy NextTurnPotential / etc. for an army that is
            // never created).
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


        // =======================================================================================
        //  HERO FITNESS  — real characteristics only, no flat class bonus/penalty (P1.5)
        // =======================================================================================
        // AI-MGR §11 — CommandRating is no longer an unconditional absolute bonus. The command part
        // of a hero's leadership fit is the MARGINAL usable capacity it unlocks: how many extra
        // battle slots its Command opens (canonical ArmyData.ComputeProjectedCapacity
        // over the projected deployment destination) that the AI actually has bodies to fill. The
        // combat-contribution part is unchanged. `detail` is the AiDebug decomposition (§15).
        private static float HeroLeadershipFit(MaterializationPlan plan, bool hero, EffectEvaluationContext ectx,
            int projectedLegalFillers, out string detail)
        {
            detail = null;
            if (!hero) return 0f;
            CardDefinition def = PlanBaseDef(plan);
            if (def == null) return 0f;

            float combatPart = AiPower.ToPowerUnit(def).BasePower * AiConfigV2.heroRoleCombatContributionWeight;
            float commandPart = HeroCommandMarginalValue(def, ectx, projectedLegalFillers, out detail);

            return Mathf.Clamp(
                (combatPart + commandPart) / Mathf.Max(1f, AiConfigV2.heroLeadershipFitNorm),
                0f, AiConfigV2.heroLeadershipFitCap);
        }

        // §11 / review-r3 P1.4 — the marginal value of THIS hero's Command in the projected
        // deployment context, measured against DESTINATION-LOCAL fillable capacity only. Global body
        // counts (armies elsewhere, hand) are NOT fillers: a rival hero with +1 Command scores
        // higher ONLY when THIS destination army already sits at its heroless capacity and the
        // hero's Command genuinely lifts the bottleneck.
        //
        // AI-MGR — Command 6-vs-7: `projectedLegalFillers` is how many extra non-hero bodies could
        // ALSO legally land in this recipient THIS turn (a JOINTLY-feasible count from the shared
        // portfolio solver — AP / H-E-M-T / generation / physical / recipient capacity), so a slot
        // the hero's Command unlocks is only "usable" if there is really a body to put in it. 0 for
        // any call with no candidate set keeps the conservative "hero itself only" behaviour.
        //
        // Strike force: whether the hero would LEAD the destination is HeroRoleEvaluator's call
        // (a second hero that beats the current commander is promoted by Housekeeping, and then
        // its Command does set capacity), and leading also prices the win-chance gain against
        // the command context (heroCommandWinGainValue).
        private static float HeroCommandMarginalValue(CardDefinition def, EffectEvaluationContext ectx,
            int projectedLegalFillers, out string detail)
        {
            detail = null;
            if (def == null || def.cardType != CardType.Hero)
                return 0f;

            // Who leads the destination after the hero joins — HeroRoleEvaluator's one commander
            // evaluation against the command context (the strongest known enemy field army),
            // exactly the choice Housekeeping's commander reorder will make. The destination's
            // bodies are known; its current commander is compared on the fight and capacity only
            // (its static role signals are not in the snapshot), so a tie keeps it.
            IReadOnlyList<WorthIt.DefendingArmy> context = HeroRoleEvaluator.CommandContext(ectx.Snap);
            IReadOnlyList<WorthIt.DefenderProfile> bodies = ectx.DestArmyMembers
                ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
            // "Before" = the army after the hero joins WITHOUT leading: the current commander keeps
            // capacity and every other hero, the newcomer included, takes a slot. A heroless
            // destination holds its nominal capacity with no commander slot.
            HeroRoleEvaluator.CommandProjection before = ectx.DestHasHero
                ? HeroRoleEvaluator.ProjectCommand(ectx.DestNominalCapacity, ectx.DestHeroCount,
                    ectx.DestCommander, bodies, context, 0f)
                : HeroRoleEvaluator.ProjectCommand(ectx.DestNominalCapacity + 1, 0, default,
                    bodies, context, 0f);
            HeroRoleEvaluator.CommandProjection led = HeroRoleEvaluator.ProjectCommand(def.commandRating,
                ectx.DestHeroCount, WorthIt.SideCommander.Of(def), bodies, context, 0f);
            bool leads = !ectx.DestHasHero || HeroRoleEvaluator.CompareCandidates(
                new HeroRoleEvaluator.CommandCandidate(led, 0, 0f, 0, 0, 1),
                new HeroRoleEvaluator.CommandCandidate(before, 0, 0f, 0, 0, 0)) < 0;

            // Command slots: the marginal usable capacity it unlocks — slots the AI actually has
            // bodies for (the destination's own plus `projectedLegalFillers`).
            int nominalCap = ectx.DestNominalCapacity;
            int projectedCap = leads ? def.commandRating : nominalCap;

            int occupiedBefore = ectx.DestOccupiedSlots;
            // The hero itself consumes one battle slot; plus the bodies that could jointly-legally
            // fill the slots its Command opens this turn.
            int requiredCapacity = occupiedBefore + 1 + Mathf.Max(0, projectedLegalFillers);

            int usableBefore = Mathf.Min(nominalCap, requiredCapacity);
            int usableAfter = Mathf.Min(projectedCap, requiredCapacity);
            int usableExtraSlots = Mathf.Clamp(
                usableAfter - usableBefore, 0, AiConfigV2.heroCommandMarginalMaxSlots);
            float slotValue = usableExtraSlots * AiConfigV2.heroCommandMarginalSlotValue;

            // Command in the fight: the win-chance gain when it leads.
            float winGain = leads ? Mathf.Max(0f, led.WinChance - before.WinChance) : 0f;
            float value = slotValue + winGain * AiConfigV2.heroCommandWinGainValue;

            detail = $"command={def.commandRating} nominalCap={nominalCap} projectedCap={projectedCap} "
                   + $"leads={(leads ? 1 : 0)} occupiedBefore={occupiedBefore} "
                   + $"legalFillers={Mathf.Max(0, projectedLegalFillers)} requiredCapacity={requiredCapacity} "
                   + $"usableExtraSlots={usableExtraSlots} "
                   + $"win={before.WinChance.ToString("0.00", CultureInfo.InvariantCulture)}->"
                   + $"{led.WinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                   + $"commandMarginalValue={value.ToString("0.00", CultureInfo.InvariantCulture)}";
            return value;
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
            return Mathf.Clamp(marginal / Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate), 0f, 2f);
        }

        // =======================================================================================
        //  EQUIPMENT UPGRADE VALUE — the ONE value of attaching equipment E to recipient R
        // =======================================================================================
        //  Every consumer reads this and nothing else: hand Equipment onto a deployed unit
        //  (NonCombatCardPlayer / ScoreNonCombat), equipment riding a deploy (AttachDeploy /
        //  GenerateAttachDeploy Synergy + EquipmentUpgrade RoleFit), a READY generated upgrade
        //  (ScoreGeneratedEquipmentUpgrade) and Development PREPARE (which prices its output through
        //  ScoreGeneratedEquipmentUpgrade). Costs stay with ResourceCost; this is value only.
        //    statDelta  — EquipmentUpgradeUtilityFor: signed predicted stat/ability delta, body units.
        //    matchupFit — EquipmentMatchupFit: share of known threats the upgrade turns.
        //  A ground-combat recipient is the one whose need the WorthIt matchup can witness:
        //  Production amplifies a justified need, so an upgrade that turns no known fight is worth
        //  nothing there. Heroes / non-ground bodies have no WorthIt witness (fit is 0 by design);
        //  their Fate / Command / tactical delta keeps its value without the matchup amplifier.
        //  Only the COMBAT part (what WorthIt witnesses) is matchup-amplified and matchup-gated;
        //  the TACTICAL part (Move/Range/AP/Command/roles) has no WorthIt witness and keeps its
        //  value. A loss (negative combat part) is never amplified or erased.
        internal static float EquipmentUpgradeValue(EquipmentDelta delta, float matchupFit, bool combatRecipient)
        {
            float fit = Mathf.Clamp01(matchupFit);
            float combat = delta.Combat <= 0f || !combatRecipient ? delta.Combat
                : fit > 0f ? delta.Combat * (1f + fit) : 0f;
            return (combat + delta.Tactical) * AiConfigV2.equipmentUpgradePersistence;
        }

        // Development's bound opportunity: ExpectedGain is the recipient's whole delta and
        // TacticalGain its tactical share, both in AiPower units.
        internal static float EquipmentUpgradeValue(DevelopmentOpportunity op)
        {
            if (op == null)
                return 0f;
            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            float tactical = op.TacticalGain / powerUnit;
            var delta = new EquipmentDelta(op.ExpectedGain / powerUnit - tactical, tactical);
            return EquipmentUpgradeValue(delta, op.MatchupFit, IsCombatRecipient(op.RecipientCard, op.RecipientUnit));
        }

        // A materialization chain carrying equipment onto the body it deploys.
        internal static float EquipmentUpgradeValue(MaterializationPlan p, WorldSnapshot snap = null,
            CapabilityInventory inv = null)
        {
            CardDefinition host = p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
            CardDefinition eq = p?.GeneratedEquipmentDef ?? p?.EquipmentInHand?.Definition;
            if (host == null || eq?.equipment == null)
                return 0f;
            EquipmentDelta delta = EquipmentDeltaParts(eq, p?.BaseCardInHand, host, snap, inv);
            float fit = HandCardMatchupFit(eq.equipment, host, p?.BaseCardInHand?.Equipment?.equipment, snap);
            return EquipmentUpgradeValue(delta, fit, host.cardType == CardType.Unit);
        }

        // A deployed unit inside `army` (hand Equipment played onto the map).
        internal static float EquipmentUpgradeValue(CardDefinition equipDef, UnitData host, ArmyData army,
            WorldSnapshot snap = null, CapabilityInventory inv = null)
        {
            if (equipDef?.equipment == null || host == null)
                return 0f;
            EquipmentDelta delta = EquipmentDeltaParts(equipDef, host, snap, inv);
            float fit = army?.Members != null
                ? UnitMatchupFit(equipDef.equipment, host, army.Members, snap) : 0f;
            return EquipmentUpgradeValue(delta, fit, IsCombatRecipient(null, host));
        }

        private static bool IsCombatRecipient(CardData card, UnitData unit) =>
            unit != null ? !unit.IsHero && unit.IsGroundCombatant
                : card?.Definition != null && card.Definition.cardType == CardType.Unit;

        // [0..1] share of known threats against which the upgrade improves the recipient's outcome.
        internal static float EquipmentMatchupFit(DevelopmentOpportunity cand, ArmyData army,
            WorldSnapshot snap)
        {
            EquipmentGrant grant = cand?.Card?.equipment;
            if (grant == null)
                return 0f;
            if (cand.RecipientUnit != null && army?.Members != null)
                return UnitMatchupFit(grant, cand.RecipientUnit, army.Members, snap);
            if (cand.RecipientKind == DevRecipientKind.HandCard)
                return HandCardMatchupFit(grant, cand.RecipientCard?.Definition,
                    cand.RecipientCard?.Equipment?.equipment, snap);
            return 0f;
        }

        private static float UnitMatchupFit(EquipmentGrant grant, UnitData recipient,
            IReadOnlyCollection<UnitData> members, WorldSnapshot snap)
        {
            List<WorthIt.DefendingArmy> threats = EquipmentValuationThreats(snap);
            int comparable = 0;
            int improved = 0;
            foreach (WorthIt.DefendingArmy threat in threats)
            {
                if (threat.Units == null || threat.Units.Count == 0)
                    continue;
                comparable++;
                if (ImprovesGroundCombatOutcome(recipient, members, grant, threat))
                    improved++;
            }
            return comparable > 0 ? (float)improved / comparable : 0f;
        }

        // Only a Unit card is a new WorthIt combat body; a hand Hero is not (fit 0 by design).
        private static float HandCardMatchupFit(EquipmentGrant grant, CardDefinition host,
            EquipmentGrant existing, WorldSnapshot snap)
        {
            if (grant == null || host == null || host.cardType != CardType.Unit)
                return 0f;
            List<WorthIt.DefendingArmy> threats = EquipmentValuationThreats(snap);
            if (threats.Count == 0)
                return 0f;
            AiPower.ProjectedStrategicLine before = AiPower.EffectiveLine(host, existing);
            AiPower.ProjectedStrategicLine after = AiPower.EffectiveLine(host, existing, grant);
            WorthIt.DefenderProfile Profile(AiPower.ProjectedStrategicLine line) =>
                new WorthIt.DefenderProfile(line.Defense,
                    line.EffectiveAbilities.Contains(UnitAbilities.CeramicArmor),
                    host.unitTypeTags, line.Attack, line.HitPoints, line.Initiative,
                    line.EffectiveAbilities);
            var beforeRoster = new[] { Profile(before) };
            var afterRoster = new[] { Profile(after) };

            int comparable = 0;
            int improved = 0;
            foreach (WorthIt.DefendingArmy threat in threats)
            {
                IReadOnlyCollection<WorthIt.DefenderProfile> defenders = threat.Units;
                if (defenders == null || defenders.Count == 0)
                    continue;
                comparable++;
                bool coversBefore = WorthIt.CanDamageAll(beforeRoster, defenders);
                bool coversAfter = WorthIt.CanDamageAll(afterRoster, defenders);
                if (!coversAfter)
                    continue;
                if (!coversBefore)
                {
                    improved++;
                    continue;
                }

                // If this card already has enough penetration, defensive HP/Defense/Initiative
                // changes can still be the real reason the attachment matters. Reuse the SAME
                // full-roster WorthIt read as deployed recipients; never fall back to a private
                // Attack+Defense heuristic.
                WorthIt.BattleEstimate previous = WorthIt.Estimate(beforeRoster, defenders, 0f,
                    default, threat.Commander);
                WorthIt.BattleEstimate next = WorthIt.Estimate(afterRoster, defenders, 0f,
                    default, threat.Commander);
                if (next.WinChance > previous.WinChance
                    || (next.WinChance == previous.WinChance
                        && (next.ExpectedSurvivingHpRatioOnWin > previous.ExpectedSurvivingHpRatioOnWin
                            || next.CriticalAfterBattleChance < previous.CriticalAfterBattleChance)))
                    improved++;
            }
            return comparable > 0 ? (float)improved / comparable : 0f;
        }

        // WorthIt owns combat rules and simulation. EquipmentSystem owns the exact stat/ability
        // projection. Compare the SAME army's roster before/after replacing only its recipient,
        // without mutating gameplay UnitData or pretending the grant created a new combat body.
        internal static bool ImprovesGroundCombatOutcome(UnitData recipient,
            IReadOnlyCollection<UnitData> members, EquipmentGrant grant,
            WorthIt.DefendingArmy threat, float hexBonus = 0f)
        {
            IReadOnlyCollection<WorthIt.DefenderProfile> defenders = threat.Units;
            if (recipient == null || recipient.IsHero || grant == null || members == null
                || defenders == null || defenders.Count == 0 || !members.Contains(recipient))
                return false;

            var before = new List<WorthIt.DefenderProfile>();
            var after = new List<WorthIt.DefenderProfile>();
            var stats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = recipient.Attack,
                [EquipmentStat.Defense] = recipient.Defense,
                [EquipmentStat.HitPoints] = recipient.HitPointsMax,
                [EquipmentStat.Initiative] = recipient.Initiative,
            };
            PredictedEquipmentState predicted = EquipmentSystem.Predict(grant, stats, recipient.Abilities);
            int attack = predicted.Stats.TryGetValue(EquipmentStat.Attack, out int atk)
                ? atk : recipient.Attack;
            int defense = predicted.Stats.TryGetValue(EquipmentStat.Defense, out int def)
                ? def : recipient.Defense;
            int maxHp = predicted.Stats.TryGetValue(EquipmentStat.HitPoints, out int hp)
                ? hp : recipient.HitPointsMax;
            int currentHp = Mathf.Clamp(recipient.HitPointsCurrent
                + Mathf.Max(0, maxHp - recipient.HitPointsMax), 1, maxHp);
            int initiative = predicted.Stats.TryGetValue(EquipmentStat.Initiative, out int init)
                ? init : recipient.Initiative;
            var projected = new WorthIt.DefenderProfile(defense,
                predicted.Abilities.Contains(UnitAbilities.CeramicArmor), recipient.TypeTags.ToList(),
                attack, currentHp, initiative, predicted.Abilities, maxHp);

            foreach (UnitData unit in members)
            {
                // A non-combatant recipient's projected profile is built by hand above and would
                // otherwise default to a combatant — skip it on the domain rule, not a hero check.
                if (unit == null || !unit.IsGroundCombatant)
                    continue;
                before.Add(WorthIt.FromLiveUnit(unit));
                after.Add(object.ReferenceEquals(unit, recipient) ? projected : WorthIt.FromLiveUnit(unit));
            }
            bool coversBefore = WorthIt.CanDamageAll(before, defenders, hexBonus);
            bool coversAfter = WorthIt.CanDamageAll(after, defenders, hexBonus);
            if (!coversAfter)
                return false;
            if (!coversBefore)
                return true;

            // Equipment never changes who leads: the same commanders on both sides of the compare.
            WorthIt.SideCommander ownCommander = WorthIt.SideCommander.Of(members);
            WorthIt.BattleEstimate previous = WorthIt.Estimate(before, defenders, hexBonus,
                ownCommander, threat.Commander);
            WorthIt.BattleEstimate improvedEstimate = WorthIt.Estimate(after, defenders, hexBonus,
                ownCommander, threat.Commander);
            return improvedEstimate.WinChance > previous.WinChance
                || (improvedEstimate.WinChance == previous.WinChance
                    && (improvedEstimate.ExpectedSurvivingHpRatioOnWin > previous.ExpectedSurvivingHpRatioOnWin
                        || improvedEstimate.CriticalAfterBattleChance < previous.CriticalAfterBattleChance));
        }

        private static List<WorthIt.DefendingArmy> EquipmentValuationThreats(WorldSnapshot snap)
        {
            var result = new List<WorthIt.DefendingArmy>();
            // Only composition crosses the TrueWorld boundary. Ground and aviation rosters are
            // both legitimate Production valuation inputs; neither hidden coordinates nor army
            // identity is passed to recipient selection or Mission planning.
            if (snap?.TrueWorld?.EnemyArmies != null)
                result.AddRange(snap.TrueWorld.EnemyArmies
                    .Where(a => a != null && a.Members != null && a.Members.Count > 0)
                    .Select(a => new WorthIt.DefendingArmy(a.Members, a.Commander)));

            // A neutral's last honestly observed defender profiles are the only permitted
            // composition witness. After it disappears into fog, its hidden live roster may
            // change; matching a known ArmyId back into TrueWorld would silently cheat.
            if (snap?.Known?.NeutralSightings != null)
                result.AddRange(snap.Known.NeutralSightings
                    .Where(s => s.Defenders != null && s.Defenders.Count > 0)
                    .Select(s => new WorthIt.DefendingArmy(s.Defenders, s.Commander)));

            // An event guard is not a live ArmyData until triggered. Its legitimately observed
            // defender profiles already belong to Known, so use those directly for WorthIt;
            // never invent a synthetic army or read hidden live event state/positions.
            if (snap?.Known?.EventGuards != null)
                result.AddRange(snap.Known.EventGuards
                    .Where(g => g.Defenders != null && g.Defenders.Count > 0)
                    .Select(g => new WorthIt.DefendingArmy(g.Defenders, g.Commander)));
            return result;
        }

        internal static float EquipmentUpgradeUtilityFor(CardDefinition equipDef, CardData host,
            WorldSnapshot snap = null, CapabilityInventory inv = null)
            => EquipmentUpgradeUtilityFor(equipDef, host, host?.Definition, snap, inv);

        internal static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, CardData host,
            WorldSnapshot snap = null, CapabilityInventory inv = null)
            => EquipmentDeltaParts(equipDef, host, host?.Definition, snap, inv);

        // A host that is still only a definition (a deck card): nothing is attached to it yet.
        internal static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, CardDefinition host)
            => EquipmentDeltaParts(equipDef, null, host, null, null);

        private static float EquipmentUpgradeUtilityFor(CardDefinition equipDef, CardData hostCard,
            CardDefinition host, WorldSnapshot snap, CapabilityInventory inv)
            => EquipmentDeltaParts(equipDef, hostCard, host, snap, inv).Total;

        private static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, CardData hostCard,
            CardDefinition host, WorldSnapshot snap, CapabilityInventory inv)
        {
            EquipmentGrant grant = equipDef?.equipment;
            if (grant == null || host == null)
                return default;
            var before = DefinitionStats(host);
            IReadOnlyList<string> abilities = host.grantedAbilities != null
                ? new List<string>(host.grantedAbilities)
                : (IReadOnlyList<string>)System.Array.Empty<string>();
            EquipmentGrant existing = hostCard?.Equipment?.equipment;
            if (existing != null)
            {
                PredictedEquipmentState current = EquipmentSystem.Predict(existing, before, abilities);
                if (current.Stats != null)
                    foreach (KeyValuePair<EquipmentStat, int> kv in current.Stats)
                        before[kv.Key] = kv.Value;
                abilities = current.Abilities;
            }
            return ScoreEquipmentDelta(grant, before, abilities, host.cardType == CardType.Hero, snap, inv);
        }

        private static Dictionary<EquipmentStat, int> DefinitionStats(CardDefinition host) =>
            new Dictionary<EquipmentStat, int>
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

        // P1(review-r2) — standalone Equipment scored by the REAL predicted before/after delta on a
        // concrete live host, not by the host's raw power. NonCombatCardPlayer picks the (equipment,
        // host) pair that maximises this.
        internal static float EquipmentUpgradeUtilityFor(CardDefinition equipDef, UnitData host,
            WorldSnapshot snap = null, CapabilityInventory inv = null)
            => EquipmentDeltaParts(equipDef, host, snap, inv).Total;

        internal static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, UnitData host,
            WorldSnapshot snap = null, CapabilityInventory inv = null)
        {
            EquipmentGrant grant = equipDef?.equipment;
            if (grant == null || host == null)
                return default;
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
            return ScoreEquipmentDelta(grant, before, ab, host.IsHero, snap, inv);
        }

        // Combat = what WorthIt can witness (Attack/Defense/HP/Initiative/Resistance, hero Fate,
        // added/lost damage abilities); Tactical = what it cannot (Move, Range, activation AP,
        // Command, strategic roles - roles already gate themselves on a present threat).
        private static EquipmentDelta ScoreEquipmentDelta(EquipmentGrant grant, Dictionary<EquipmentStat, int> before,
            IReadOnlyList<string> hostAbilities, bool isHero, WorldSnapshot snap, CapabilityInventory inv)
        {
            PredictedEquipmentState predicted = EquipmentSystem.Predict(grant, before, hostAbilities);
            int After(EquipmentStat stat) =>
                predicted.Stats != null && predicted.Stats.TryGetValue(stat, out int value) ? value : before[stat];

            // Signed deltas are essential: an override that gains Attack but destroys Defense,
            // movement or Fate is not a free upgrade.
            float combatDelta =
                (After(EquipmentStat.Attack) - before[EquipmentStat.Attack]) * AiConfigV2.powerAttackWeight
                + (After(EquipmentStat.Defense) - before[EquipmentStat.Defense]) * AiConfigV2.powerDefenseWeight
                + (After(EquipmentStat.HitPoints) - before[EquipmentStat.HitPoints]) * AiConfigV2.powerHitPointsWeight
                + (After(EquipmentStat.Initiative) - before[EquipmentStat.Initiative]) * AiConfigV2.powerInitiativeWeight
                + (After(EquipmentStat.Resistance) - before[EquipmentStat.Resistance]) * AiConfigV2.powerResistanceWeight;
            if (isHero)
                combatDelta += (After(EquipmentStat.Fate) - before[EquipmentStat.Fate])
                               * AiConfigV2.powerHeroFateWeight;

            float tactical = 0f;
            tactical += (After(EquipmentStat.MoveMax) - before[EquipmentStat.MoveMax]) * 0.20f;
            tactical += (After(EquipmentStat.Range) - before[EquipmentStat.Range]) * 0.15f;
            tactical += (before[EquipmentStat.ActivationApCost] - After(EquipmentStat.ActivationApCost)) * 0.25f;
            tactical += (After(EquipmentStat.CommandRating) - before[EquipmentStat.CommandRating]) * 0.15f;
            tactical += EquipmentRoleDelta(hostAbilities, predicted.Abilities,
                before[EquipmentStat.MoveMax], After(EquipmentStat.MoveMax), snap, inv);
            int addedAbilities = predicted.Abilities?.Count(a =>
                hostAbilities == null || !hostAbilities.Contains(a)) ?? 0;
            int lostAbilities = hostAbilities?.Count(a =>
                predicted.Abilities == null || !predicted.Abilities.Contains(a)) ?? 0;
            float combat = combatDelta / Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate)
                + (addedAbilities - lostAbilities) * 0.15f;

            // The whole delta keeps its [-1.5, 1.5] bound; both parts shrink proportionally.
            float raw = combat + tactical;
            float bounded = Mathf.Clamp(raw, -1.5f, 1.5f);
            float scale = Mathf.Abs(raw) > 1e-6f ? bounded / raw : 1f;
            return new EquipmentDelta(combat * scale, tactical * scale);
        }

        internal readonly struct EquipmentDelta
        {
            public readonly float Combat;
            public readonly float Tactical;
            public EquipmentDelta(float combat, float tactical) { Combat = combat; Tactical = tactical; }
            public float Total => Combat + Tactical;
        }

        private static float EquipmentRoleDelta(IReadOnlyList<string> beforeAbilities,
            IReadOnlyList<string> afterAbilities, int beforeMove, int afterMove,
            WorldSnapshot snap, CapabilityInventory inv)
        {
            var before = new HashSet<IntendedRole>(StrategicEffectRegistry.Roles(beforeAbilities, beforeMove));
            var after = new HashSet<IntendedRole>(StrategicEffectRegistry.Roles(afterAbilities, afterMove));
            if (AbilityParams.AbilitiesHaveAnyRecce(beforeAbilities)) before.Add(IntendedRole.Scout);
            if (AbilityParams.AbilitiesHaveAnyRecce(afterAbilities)) after.Add(IntendedRole.Scout);
            if (beforeAbilities != null && (beforeAbilities.Contains(UnitAbilities.Researcher)
                || beforeAbilities.Contains(UnitAbilities.Assembler))) before.Add(IntendedRole.Development);
            if (afterAbilities != null && (afterAbilities.Contains(UnitAbilities.Researcher)
                || afterAbilities.Contains(UnitAbilities.Assembler))) after.Add(IntendedRole.Development);

            float delta = 0f;
            foreach (IntendedRole role in before)
            {
                if (after.Contains(role))
                    continue;
                switch (role)
                {
                    case IntendedRole.AntiAir:
                    case IntendedRole.AntiArmor:
                        if (EnemyThreatModel.ThreatPresent(role, snap))
                            delta -= AiConfigV2.capabilityGapValue;
                        break;
                    case IntendedRole.Scout:
                        delta -= inv != null && inv.TotalScouts <= 1
                            ? AiConfigV2.capabilityGapValue : AiConfigV2.holdScarcityValue;
                        break;
                    case IntendedRole.Development:
                        if (snap?.Self?.HasDevFacility == true)
                            delta -= AiConfigV2.holdUniqueRoleValue;
                        break;
                    case IntendedRole.Support:
                    case IntendedRole.CapabilitySpecialist:
                        delta -= AiConfigV2.holdNearTermDemandValue * 0.5f;
                        break;
                }
            }
            foreach (IntendedRole role in after)
                if (!before.Contains(role))
                    delta += AiConfigV2.stratTraitMatchBonus;
            return delta / Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
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

        // Phase-B floor under AlternativeUseValue: a scarce hero committed to Support, or a
        // unique stealth item burned into a non-scout role, always costs at least this.
        private static float SurplusScarceBodyFloor(MaterializationPlan p, IntendedRole role,
            CapabilityInventory inv, bool hero)
        {
            float cost = 0f;
            // CombatBody/ForceGrowth/MobileCombat/AntiArmor/AntiAir/Scout/ResourceGain are a hero's
            // UNIVERSAL uses — a scarce hero playing any of them is not "misuse". Support is the
            // one genuinely NARROW specialisation, so it is the only role that pays the scarce-hero
            // floor.
            if (hero && role == IntendedRole.Support
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

        internal static float StrategicResourceCostValue(ResourceCost c) =>
            StrategicResourceCostValue(c, null);

        // Dynamic opportunity cost from all unplayed hand/deck costs versus current stock and
        // income over the existing economy horizon. No other spend demand => cheap resources.
        internal static float StrategicResourceCostValue(ResourceCost c, WorldSnapshot snap,
            System.Func<ResourceType, float> spendableResource = null, PlayerSetupData player = null)
        {
            if (c == null)
                return 0f;
            float total = 0f;
            float residualPreservation = 0f;
            foreach (ResourceType type in ResourceBundle.All)
            {
                int amount = c.Get(type);
                if (amount <= 0)
                    continue;
                float factor = 1f;
                if (snap?.Self != null)
                {
                    float demand = PendingCardResourceDemand(snap, type);
                    float availableNow = spendableResource != null
                        ? Mathf.Max(0f, spendableResource(type))
                        : snap.Self.Stockpile.Get(type);
                    float supply = availableNow + snap.Self.PerTurnIncome.Get(type)
                        * Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns);
                    float pressure = demand <= 0.0001f ? 0f
                        : demand / Mathf.Max(0.0001f, demand + supply);
                    factor = Mathf.Lerp(0.2f, 1.8f, Mathf.Clamp01(pressure));
                    residualPreservation += ResidualResourcePreservationValue(
                        player, snap, type, amount, availableNow);
                }
                total += amount * factor;
            }
            return AiConfigV2.stratChainResCostWeight * total + residualPreservation;
        }

        // Marginal value of keeping only the units this candidate consumes when current-turn,
        // actor-aware AGG/RCN demand was proven blocked by this exact resource. This remains a
        // scored opportunity cost: attainable near gaps are protected; remote gaps are not frozen.
        private static float ResidualResourcePreservationValue(PlayerSetupData player, WorldSnapshot snap,
            ResourceType type, int consumed, float availableNow)
        {
            if (player == null || snap == null || consumed <= 0
                || !ResourceStarvationRegistry.TryGetCurrentBlock(
                    player, type, snap.TurnNumber, out ResourceBlockEvidence block)
                || block.Required <= 0f)
                return 0f;

            float income = Mathf.Max(block.IncomePerTurn,
                snap.Self != null ? snap.Self.PerTurnIncome.Get(type) : 0f);
            float horizon = Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns);
            float attainableSupply = Mathf.Max(0f, availableNow) + income * horizon;
            float attainability = Mathf.Clamp01(attainableSupply / block.Required);
            float setback = Mathf.Clamp01(
                Mathf.Min(Mathf.Max(0f, availableNow), consumed) / block.Required);
            float urgency = DemandUrgencyPolicy.NormalizedWorldValue(block.DemandValue);

            return AiConfigV2.stratResidualResourcePreservationMax
                * urgency * attainability * setback;
        }

        private static float PendingCardResourceDemand(WorldSnapshot snap, ResourceType type)
        {
            float demand = 0f;
            if (snap?.Self?.Hand != null)
                foreach (CardData card in snap.Self.Hand)
                    demand += card?.EffectivePlayResourceCost?.Get(type) ?? 0;
            if (snap?.Self?.Deck != null)
                foreach (CardDefinition card in snap.Self.Deck)
                    demand += card?.resourceCost?.Get(type) ?? 0;
            return demand;
        }

        private static float ChainStepPenalty(MaterializationChainKind k)
        {
            switch (k)
            {
                case MaterializationChainKind.AttachDeploy: return AiConfigV2.stratChainAttachStepPenalty;
                case MaterializationChainKind.GenerateDeploy: return AiConfigV2.stratChainGenerationStepPenalty;
                case MaterializationChainKind.GenerateAttachDeploy:
                case MaterializationChainKind.GenerateAttachUpgrade:
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

        // AI-MGR — merge the dynamic-effect and hero-Command decomposition strings for the breakdown.
        private static string JoinDetail(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + " ; " + b;
        }

        // Base card facts only. DemandLayer builds the sole TaskScore; this method does NOT
        // calculate a second, bespoke strategic value or an imaginary extraction loss.
        internal readonly struct BaseSiteValue
        {
            internal readonly float HexYield;
            internal readonly float GlobalEffect;
            internal readonly float Airfield;
            internal readonly float Exposure;

            internal BaseSiteValue(float hexYield, float globalEffect, float airfield,
                float exposure)
            {
                HexYield = hexYield;
                GlobalEffect = globalEffect;
                Airfield = airfield;
                Exposure = exposure;
            }
        }

        internal static BaseSiteValue ScoreBaseSite(WorldSnapshot s, EconomyBaseOpportunity site,
            CardData card) => new BaseSiteValue(
                BaseCardMarginalYield(s, site, card.Definition),
                BaseGlobalEffectValue(s, card.Definition),
                BaseAirfieldValue(s, card.Definition, site.Hex),
                ThreatExposure(s, site.Hex));

        // One owner of the gameplay fact "what resource income can this exact Base card collect on
        // this hex?". This private helper is only the new card's additional Collect capacity;
        // actual OWNER gain below also accounts for existing army collection.
        private static int BaseCardAdditionalCollectCapacity(ResourceBundle yield,
            CardDefinition definition, ResourceType type)
        {
            if (definition?.grantedAbilities == null
                || !definition.grantedAbilities.Contains(UnitAbilities.CollectAbilityFor(type)))
                return 0;
            return Mathf.RoundToInt(Mathf.Max(0f, Mathf.Min(1f, yield.Get(type))));
        }

        // Net OWNER gain, not the gross Base collection. A Base takes the first cut,
        // and can displace our own army's collection without raising the owner's income.
        internal static float BaseCardMarginalYield(WorldSnapshot s, EconomyBaseOpportunity site,
            CardDefinition definition) => ResourceBundle.All.Sum(type =>
                BaseCardMarginalGain(s, site, definition, type));

        internal static float BaseCardMarginalGain(WorldSnapshot s, EconomyBaseOpportunity site,
            CardDefinition definition, ResourceType type)
        {
            int addedCapacity = BaseCardAdditionalCollectCapacity(site.HexYield, definition, type);
            if (addedCapacity <= 0)
                return 0f;
            int remainingYield = Mathf.RoundToInt(site.HexYield.Get(type));
            int ownArmyCollectors = Mathf.RoundToInt((s?.Self?.Armies
                ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.Hex.Equals(site.Hex))
                .Sum(a => a.CollectionCapacity.Get(type)));
            bool armiesCanCollect = !(s?.Known?.EnemySightings
                ?? System.Array.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>())
                .Any(enemy => enemy.Hex.Equals(site.Hex));
            // BaseUncollectedYield has already subtracted existing building collection:
            // work on the remainder to avoid charging carried-over facilities twice.
            return IncomeProjection.MarginalOwnerCollectionAtHex(
                remainingYield, 0, addedCapacity, ownArmyCollectors, armiesCanCollect);
        }

        private static float BaseGlobalEffectValue(WorldSnapshot s, CardDefinition definition)
        {
            if (definition?.grantedAbilities == null)
                return 0f;
            EffectContribution contribution = StrategicEffectRegistry.Contributions(
                IntendedRole.Economy, definition.grantedAbilities, 0,
                new EffectEvaluationContext(s));
            return contribution.GlobalRoleFit + contribution.GlobalImmediateTempo
                + contribution.GlobalThreatResponse + contribution.GlobalCapabilityGap
                + contribution.GlobalForceGrowth + contribution.GlobalSynergy;
        }

        private static float BaseAirfieldValue(WorldSnapshot s, CardDefinition definition,
            HexCoord target)
        {
            if (definition == null || definition.airfieldCapacity <= 0 || s?.Self == null)
                return 0f;
            bool aviationRelevant = (s.Self.Hand ?? System.Array.Empty<CardData>())
                    .Any(c => c?.Definition?.isAviation == true)
                || (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>()).Any(a => a != null && a.IsAir);
            if (!aviationRelevant)
                return 0f;
            List<ArmySnapshot> airfields = (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.IsAirfield).ToList();
            if (airfields.Count == 0)
                return 1f;
            int distance = airfields.Min(a => HexGridMath.Distance(a.Hex, target));
            return Mathf.Clamp01(distance / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
        }

        // Shared with Strategy/Demand's Extraction site scoring — generic map/resource-cost
        // primitives, not Base-specific, so DemandLayer calls back into this evaluator rather than
        // each side keeping its own copy.
        internal static float ThreatExposure(WorldSnapshot s, HexCoord target)
        {
            if (s?.Known?.EnemySightings == null)
                return 0f;
            float exposure = 0f;
            foreach (AiMapMemory.KnownEnemySighting enemy in s.Known.EnemySightings)
            {
                int distance = HexGridMath.Distance(target, enemy.Hex);
                if (distance <= 3)
                    exposure = Mathf.Max(exposure, 1f - distance / 4f);
            }
            return exposure;
        }

        internal static float ResourceCostSum(ResourceCost cost) => cost == null ? 0f
            : ResourceBundle.All.Sum(t => Mathf.Max(0, cost.Get(t)));
    }
}
