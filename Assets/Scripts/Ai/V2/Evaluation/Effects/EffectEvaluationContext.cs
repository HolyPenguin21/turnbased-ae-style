using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // The world context every contextual scaler reads. Built once per Card x IntendedUse
    // evaluation, ENTIRELY from the captured WorldSnapshot (or a deterministic projection for a
    // synthetic NewArmy / ReusableShell destination) — never live ArmyData, so one Materialization
    // Plan.Score can't mix two world states. DESTINATION-LOCAL where it matters.
    //
    //   RecurringIncomeWeight  0..1, high when the economy is INSECURE (recurring income worth more)
    //   LocalEnemyArmies       KNOWN enemy sightings within effectTargetDensityRadius of the deploy
    //                          hex (fog-respecting — see below; never a global or cheat count)
    //   LocalEnemyBodies       KNOWN enemy unit count in that radius — the AoE affected-bodies signal
    //   ExpectedCombatRounds   battle-duration proxy at the deploy from KNOWN enemy strength vs the
    //                          candidate's projected power (>= 1)
    //   ProjectedLine          the body's PROJECTED stat line (card + attached equipment) — the same
    //                          line readiness / role derivation use; ExpectedSustain reads HitPoints
    //   DestArmyMembers        snapshot member profiles already in the destination army INCLUDING
    //                          heroes (empty for a fresh NewArmy / empty ReusableShell) — an aura's
    //                          EligiblePredicate counts the ones IT benefits
    //
    //   FOG POLICY: the enemy-density / combat-duration signals read snap.Known (honest sightings),
    //   NOT snap.TrueWorld. A never-scouted enemy army must not move a Splash / Regeneration score.
    //   TrueWorld cheat reads stay confined to the places that explicitly sanction them
    //   (EnemyThreatModel's directional counter-demand, WorldAnalysis threat cheat loop).
    //   FreeBattleSlots        battle cells free in the destination AFTER the plan's own primary body
    //                          takes its slot — the capacity a Summon would ACTUALLY have. Projected
    //                          for NewArmy / ReusableShell (nominal capacity − primary body), never
    //                          the pre-materialization count. -1 only when the dest army is unknown.
    internal readonly struct EffectEvaluationContext
    {
        public readonly WorldSnapshot Snap;
        public readonly MaterializationPlan Plan;
        public readonly float RecurringIncomeWeight;
        public readonly int LocalEnemyArmies;
        // final closure §3.1 — expected AFFECTED ENEMY BODIES (sum of KNOWN enemy unit counts) within
        // the AoE radius of the deploy hex. This, not the army count, is the AoE-value driver.
        public readonly int LocalEnemyBodies;
        // §3.1 follow-up — the KNOWN enemy DEFENDER PROFILES in that radius, so an AoE effect with a
        // TargetFilter (EligiblePredicate) counts only the bodies it can actually hit (Armored splash
        // vs a Bio stack), not the raw member total.
        public readonly IReadOnlyList<WorthIt.DefenderProfile> LocalEnemyProfiles;
        // final closure §3.2 — proxy for how many combat rounds a fight at the deploy would last
        // (>= 1). Drives how many regen ticks a Regenerate effect would actually get to use.
        public readonly float ExpectedCombatRounds;
        public readonly AiPower.ProjectedStrategicLine ProjectedLine;
        public readonly IReadOnlyList<WorthIt.DefenderProfile> DestArmyMembers;
        public readonly float TransportMovementGain;
        public readonly int FreeBattleSlots;
        // AI-MGR — the destination army's capacity/occupancy INPUTS behind FreeBattleSlots, exposed
        // so a caller needing the marginal effect of ITS OWN primary body's Command (StrategicCardEvaluator's
        // HeroCommandMarginalValue) can price it off THIS SAME ResolveDestination walk instead of
        // re-resolving the destination army from snap.Self.Armies a second time. DestNominalCapacity
        // is the destination's capacity BEFORE the plan's primary body is applied; DestHasHero is
        // whether it already has a commander (a heroless synthetic destination reports capacity from
        // ArmyData.ComputeCapacity and DestHasHero=false).
        public readonly int DestNominalCapacity;
        public readonly int DestOccupiedSlots;
        public readonly bool DestHasHero;
        // The destination army's commander and hero count (0 / default for a new or heroless
        // destination) — what a hero card's Command is compared against (HeroCommandMarginalValue).
        public readonly WorthIt.SideCommander DestCommander;
        public readonly int DestHeroCount;
        // final closure §3.3 (army -> candidate direction) — the value the DEST ARMY's already-
        // present auras add specifically to THIS incoming candidate's projected profile. The
        // candidate does not carry the aura ability itself, so this cannot come from Resolve(card);
        // it is folded into EffectContribution.Synergy once, for every role.
        public readonly float IncomingAuraSynergy;

        // AI-MGR — snapshot-pure inputs for a PlayerGlobal recurring-resource effect (GlobalRecurringResource
        // context). ApEconomy may be null on a bare/partial snapshot; RecurringFutureOpportunity is
        // in [effectRecurringOpportunityFloor .. 1] and is state-driven (NOT rising with TurnNumber —
        // an early persistent source keeps at least as much future value as a late one).
        public readonly ApActionEconomySnapshot ApEconomy;
        // AI-MGR — the AP action-economy read every consumer prices the recurring-AP effect off.
        // `witnessedUsefulApDemand` (constructor arg) is the owner-witnessed workload assembled at
        // evaluation time by StrategicManager Phase A/B from the REAL candidate set
        // (MaterializationPortfolioSolver.EstimateLegalApWorkload + committed movers + witnessed
        // air). When it is absent (sims, bare snapshots, a call site that has no witnessed set) the
        // resolver falls back to the deliberately-discounted structural estimate
        // (StructuralFallbackApDemand) — never dressed up as authoritative.
        public readonly float EffectiveMarginalApUtility;   // [0..1] raw ramp — consumers apply their own floor
        public readonly float EffectiveUsefulApDemand;      // AP the AI could still usefully spend this turn
        public readonly float RecurringFutureOpportunity;
        public readonly int TurnNumber;

        public float ProjectedHitPoints => ProjectedLine.HitPoints;
        public bool ProjectedIsHero => (Plan?.BaseCardInHand?.Definition ?? Plan?.GeneratedBaseDef)
            ?.cardType == CardType.Hero;
        public bool ProjectedIsBio => (Plan?.BaseCardInHand?.Definition ?? Plan?.GeneratedBaseDef)
            ?.unitTypeTags.Contains(UnitTypeTag.Bio) == true;

        public float MobileAirfieldService
        {
            get
            {
                CardDefinition card = Plan?.BaseCardInHand?.Definition ?? Plan?.GeneratedBaseDef;
                if (card == null || card.isAviation || card.cardType != CardType.Unit
                    || card.airfieldCapacity <= 0) return 0f;
                int aircraft = (Snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && (a.IsAir || a.IsAirfield)).Sum(a => a.MemberCount)
                    + (Snap?.Self?.Hand ?? System.Array.Empty<CardData>())
                        .Count(c => c?.Definition?.isAviation == true);
                int capacity = (Snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null).Sum(a => a.MobileAirfieldCapacity);
                return Mathf.Clamp01((aircraft - capacity) / (float)card.airfieldCapacity);
            }
        }

        // The structure defence a Disruption carrier deployed by this plan would remove: the known
        // site of the Attack its destination army serves (an Attack refit's fight first). Zero for
        // any other destination — a carrier in a garrison or an economy army removes nothing.
        public float DisruptionSiteDefense
        {
            get
            {
                HexCoord? site = Plan?.AttackRefitTargetHex
                    ?? (Plan?.Deploy.Army != null
                        ? ReconObjectiveEvaluator.AttackSiteOf(Snap, Plan.Deploy.Army.Id) : null);
                return site.HasValue ? ReconObjectiveEvaluator.HostileStructureDefense(Snap, site.Value) : 0f;
            }
        }

        public float MobileRepairService
        {
            get
            {
                CardDefinition card = Plan?.BaseCardInHand?.Definition ?? Plan?.GeneratedBaseDef;
                return card == null || card.isAviation ? 0f : RepairServiceAt(Snap, Plan.Deploy.Hex);
            }
        }

        // Only frozen own facts. A Base or another mechanic already provides this service;
        // no battle-power bonus, invented healing, remote rendezvous or gameplay RNG read.
        internal static float RepairServiceAt(WorldSnapshot snap, HexCoord hex,
            int? replacingSourceArmyId = null)
        {
            if (snap?.Self?.Armies == null || snap.Self.ActionPoints < 1
                || snap.Self.BaseHexes?.Contains(hex) == true)
                return 0f;
            var local = snap.Self.Armies.Where(a => a != null && !a.IsPrison
                && a.Hex.Equals(hex) && a.Owner == snap.Observer).ToList();
            if (local.Any(a => a.RepairSourceCount
                - (a.ArmyId == replacingSourceArmyId ? 1 : 0) > 0))
                return 0f;
            ResourceBundle stock = snap.Economy != null
                ? snap.Economy.SpendableStockpile : snap.Self.Stockpile;
            float service = 0f;
            foreach (RaidRecoveryMemberSnapshot member in local.SelectMany(a => a.RecoveryMembers
                ?? System.Array.Empty<RaidRecoveryMemberSnapshot>()))
            {
                var profile = member.CurrentProfile;
                ResourceVector cost = member.RepairCost;
                if (member.IsPrisoner || !member.RepairCostInitialized || profile.HitPoints <= 0
                    || profile.HitPoints >= member.FullHealthProfile.MaxHitPoints
                    || stock.Human < cost.Human || stock.Energy < cost.Energy
                    || stock.Materials < cost.Materials || stock.Tech < cost.Tech)
                    continue;
                service += 1f - profile.HitPoints / Mathf.Max(1f, member.FullHealthProfile.MaxHitPoints);
            }
            return Mathf.Clamp01(service);
        }

        // Plan-less overloads — the non-combat lane (Base / Facility / Aviation / Equipment) has no
        // MaterializationPlan but still needs the PlayerGlobal effect priced identically.
        public EffectEvaluationContext(WorldSnapshot snap) : this(snap, null, null) { }
        public EffectEvaluationContext(WorldSnapshot snap, float? witnessedUsefulApDemand)
            : this(snap, null, witnessedUsefulApDemand) { }

        public EffectEvaluationContext(WorldSnapshot snap, MaterializationPlan plan,
            float? witnessedUsefulApDemand = null)
        {
            Snap = snap;
            Plan = plan;
            TurnNumber = snap?.TurnNumber ?? 0;
            ApEconomy = snap?.Self?.ApEconomy;
            EffectiveUsefulApDemand = ResolveUsefulApDemand(snap, witnessedUsefulApDemand);
            EffectiveMarginalApUtility = ResolveMarginalApUtility(snap, witnessedUsefulApDemand);
            RecurringIncomeWeight = snap?.Economy != null
                ? 1f - Mathf.Clamp01(snap.Economy.EconomicSecurity)
                : 0.5f;
            RecurringFutureOpportunity = ComputeRecurringFutureOpportunity(snap, witnessedUsefulApDemand);

            // The projected END RESULT — base def + already-attached equipment + plan equipment.
            ProjectedLine = plan != null ? AiPower.ProjectMaterialization(plan) : default;

            // FOG-RESPECTING enemy density / strength near the deploy — KNOWN sightings only.
            LocalEnemyArmies = 0;
            LocalEnemyBodies = 0;
            float enemyPowerNear = 0f;
            List<WorthIt.DefenderProfile> enemyProfiles = null;
            IReadOnlyList<AiMapMemory.KnownEnemySighting> sightings = snap?.Known?.EnemySightings;
            if (sightings != null && plan != null)
            {
                HexCoord at = plan.Deploy.Hex;
                foreach (AiMapMemory.KnownEnemySighting s in sightings)
                {
                    if (HexGridMath.Distance(s.Hex, at) > AiConfigV2.effectTargetDensityRadius)
                        continue;
                    LocalEnemyArmies++;
                    LocalEnemyBodies += System.Math.Max(1, s.MemberCount);
                    enemyPowerNear += AiPower.SightingPower(s);
                    if (s.Defenders != null && s.Defenders.Count > 0)
                        (enemyProfiles ??= new List<WorthIt.DefenderProfile>()).AddRange(s.Defenders);
                }
            }
            LocalEnemyProfiles = enemyProfiles
                ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();

            float ownPower = Mathf.Max(1f, ProjectedLine.BasePower);
            ExpectedCombatRounds = enemyPowerNear <= 0f
                ? 1f
                : Mathf.Clamp(
                    1f + enemyPowerNear / (ownPower * Mathf.Max(0.01f, AiConfigV2.effectCombatRoundsPowerRatio)),
                    1f, AiConfigV2.effectCombatRoundsMax);

            ResolveDestination(snap, plan, out FreeBattleSlots, out DestArmyMembers,
                out ArmySnapshot destArmy, out DestNominalCapacity, out DestOccupiedSlots, out DestHasHero);
            TransportMovementGain = ExpectedTransportGain(destArmy, ProjectedLine.MoveMax);

            DestCommander = destArmy?.Commander ?? default;
            DestHeroCount = destArmy?.HeroCount ?? 0;

            IncomingAuraSynergy = ComputeIncomingAuraSynergy(
                destArmy, ProjectedLine, plan, ExpectedCombatRounds);
        }

        private static float ExpectedTransportGain(ArmySnapshot army, int carrierMove)
        {
            if (army?.Members == null || army.NonHeroMoveMax == null || army.IsAir || army.IsPrison) return 0f;
            // Gameplay gives the carrier the slowest eligible Infantry (ArmyData.ProjectTransport).
            int chosen = -1;
            for (int i = 0; i < army.Members.Count && i < army.NonHeroMoveMax.Count; i++)
                if (army.Members[i].TypeTags.Contains(UnitTypeTag.Infantry) && army.NonHeroMoveMax[i] < carrierMove
                    && (chosen < 0 || army.NonHeroMoveMax[i] < army.NonHeroMoveMax[chosen]))
                    chosen = i;
            if (chosen < 0) return 0f;
            int before = Mathf.Min(carrierMove, army.MaxMovement);
            int after = carrierMove;
            for (int i = 0; i < army.NonHeroMoveMax.Count; i++)
                after = Mathf.Min(after, i == chosen ? carrierMove : army.NonHeroMoveMax[i]);
            if (army.HasHero) after = Mathf.Min(after, army.HeroMoveMax);
            return Mathf.Max(0, after - before);
        }

        // AI-MGR — the ONE resolution point for "how much AP could the AI still USEFULLY spend this
        // turn". `witnessed` is the owner-witnessed workload the evaluation-time caller assembled
        // from the real candidate set (StrategicManager Phase A/B). When it is absent the fallback is
        // the raw STRUCTURAL demand components, scaled by apStructuralDemandConfidence because
        // structural workload is only an upper bound, never proof every action is useful.
        //
        // 2026-10-01 — the AP the last turns really wanted (ApTurnPressure, spent + unmet) is a
        // witnessed fact too, and a broader one: the evaluation-time workload covers card plays
        // only, never draws or budget-deferred missions. It replaces the structural guess
        // outright and is a floor under the evaluation-time figure.
        internal static float ResolveUsefulApDemand(WorldSnapshot snap, float? witnessed)
        {
            ApActionEconomySnapshot ape = snap?.Self?.ApEconomy;
            float? history = ape?.WitnessedApDemand;
            if (witnessed.HasValue)
                return Mathf.Max(Mathf.Max(0f, witnessed.Value), history ?? 0f);
            if (history.HasValue)
                return Mathf.Max(0f, history.Value);
            return ape == null ? 0f : StructuralFallbackApDemand(ape);
        }

        // Marginal value of one more AP/turn RIGHT NOW — the same ramp for the witnessed and the
        // fallback path; only the demand feeding it differs (the confidence discount lives in
        // StructuralFallbackApDemand, so a witnessed figure is never re-discounted).
        internal static float ResolveMarginalApUtility(WorldSnapshot snap, float? witnessed)
        {
            ApActionEconomySnapshot ape = snap?.Self?.ApEconomy;
            if (ape == null && !witnessed.HasValue)
                return 0.5f;
            float baseAp = Mathf.Max(1f, ape != null ? ape.BaseActionPoints : 1);
            float ramp = Curves.Ramp(ResolveUsefulApDemand(snap, witnessed) / baseAp,
                AiConfigV2.apMarginalUtilRampLo, AiConfigV2.apMarginalUtilRampHi);
            return Mathf.Clamp01(ramp);
        }

        // Discounted structural fallback — the deliberately-conservative estimate used only when no
        // owner-witnessed workload was assembled for the call. Structural data is NEVER named
        // authoritative/useful workload anywhere else.
        private static float StructuralFallbackApDemand(ApActionEconomySnapshot ape)
        {
            if (ape == null)
                return 0f;
            return (ape.EstimatedArmyApDemand + ape.EstimatedCardApDemand
                    + ape.EstimatedDevelopmentApDemand + ape.EstimatedAirApDemand
                    + ape.EstimatedDrawApDemand)
                   * AiConfigV2.apStructuralDemandConfidence;
        }

        // AI-MGR §5 — how much of the bounded recurring horizon a persistent source deployed NOW can
        // still realistically pay back. Driven by WORLD STATE (room to grow force, map left to
        // discover, whether AP is usable at all) with only a WEAK turn-number fallback — it must NOT
        // rise with TurnNumber, so an early persistent AP source is worth at least as much future
        // opportunity as the same source deployed late. Result in [floor .. 1].
        private static float ComputeRecurringFutureOpportunity(WorldSnapshot snap, float? witnessed)
        {
            float floor = AiConfigV2.effectRecurringOpportunityFloor;
            if (snap?.Self == null)
                return Mathf.Lerp(floor, 1f, 0.5f);

            float potential = Mathf.Max(1f, snap.Self.TotalMilitaryPotential);
            float forceRoom = Mathf.Clamp01(1f - snap.Self.TotalPower / potential);
            float mapRoom = snap.MapKnowledge != null
                ? Mathf.Clamp01(snap.MapKnowledge.ExplorableUnknownFrac)
                : 0.5f;
            float actionRoom = ResolveMarginalApUtility(snap, witnessed);
            float lateFallback = 1f - AiConfigV2.effectRecurringLateStageWeakWeight
                * Curves.Ramp(snap.TurnNumber,
                    AiConfigV2.effectRecurringStageRampLo, AiConfigV2.effectRecurringStageRampHi);

            float blended =
                AiConfigV2.effectRecurringOppForceRoomWeight * forceRoom
                + AiConfigV2.effectRecurringOppMapRoomWeight * mapRoom
                + AiConfigV2.effectRecurringOppActionRoomWeight * actionRoom
                + AiConfigV2.effectRecurringOppLateFallbackWeight * lateFallback;

            return Mathf.Clamp(Mathf.Lerp(floor, 1f, Mathf.Clamp01(blended)), floor, 1f);
        }

        // §3.3 army -> candidate — each aura standing in the dest army that the incoming candidate's
        // PROJECTED profile (card + equipment, not the bare CardDefinition) satisfies adds its
        // marginal value for one more eligible body (BaseFit / auraNorm × Magnitude × Probability ×
        // Timing). Each aura is checked against the candidate WITH ITS OWN predicate (an Armored aura
        // and a Bio aura never merge); copies that share a StackingKey are reduced by Stacking.
        private static float ComputeIncomingAuraSynergy(ArmySnapshot destArmy,
            AiPower.ProjectedStrategicLine projected, MaterializationPlan plan, float expectedCombatRounds)
        {
            if (destArmy?.AllyAuraEffects == null || destArmy.AllyAuraEffects.Count == 0)
                return 0f;
            CardDefinition baseDef = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            if (baseDef == null)
                return 0f;
            // Projected profile: equipment-adjusted stats + effective abilities; type tags come from
            // the base def (equipment does not re-tag a unit's type).
            var candidate = new WorthIt.DefenderProfile(
                projected.Defense,
                projected.EffectiveAbilities != null
                    && projected.EffectiveAbilities.Contains(UnitAbilities.CeramicArmor),
                baseDef.unitTypeTags,
                projected.Attack, projected.HitPoints, projected.Initiative,
                projected.EffectiveAbilities);

            float perBody = 1f / Mathf.Max(1f, AiConfigV2.effectAuraAllyNorm);

            float PerAura(StrategicEffect a)
            {
                float timing = a.Timing == EffectTiming.DuringCombat && expectedCombatRounds <= 1f
                    ? AiConfigV2.effectNoCombatTimingFloor : 1f;
                return a.BaseFit * a.Magnitude * a.Probability * perBody * timing;
            }

            List<StrategicEffect> eligible = destArmy.AllyAuraEffects
                .Where(a => a.EligiblePredicate == null || a.EligiblePredicate(candidate))
                .ToList();

            float total = 0f;
            foreach (StrategicEffect a in eligible)          // unkeyed auras stand alone
                if (a.StackingKey == null)
                    total += PerAura(a);
            foreach (IGrouping<string, StrategicEffect> g in eligible  // keyed copies -> Stacking
                         .Where(a => a.StackingKey != null)
                         .GroupBy(a => a.StackingKey))
            {
                StrategicEffect rep = g.First();
                total += StackedTotal(rep.Stacking, PerAura(rep), g.Count());
            }
            return total;
        }

        // Stacking reducer: a homogeneous group of `count` identical effects each worth `per`.
        internal static float StackedTotal(EffectStacking policy, float per, int count)
        {
            if (count <= 0) return 0f;
            switch (policy)
            {
                case EffectStacking.Unique:
                    return per;
                case EffectStacking.Diminishing:
                {
                    float total = 0f, w = 1f;
                    for (int i = 0; i < count; i++)
                    {
                        total += per * w;
                        w *= Mathf.Clamp01(AiConfigV2.effectStackingDiminishFactor);
                    }
                    return total;
                }
                default:
                    return per * count;
            }
        }

        // Snapshot-only. For a real recipient army (ExistingArmy / Garrison) the occupancy comes
        // from the captured ArmySnapshot by id; for a synthetic NewArmy / ReusableShell the capacity
        // is projected from the deployment rules (hero primary -> its CommandRating, else the field/
        // garrison base). Always minus 1 for the plan's own primary body.
        //
        // `nominalCapacity`/`occupiedSlots`/`destHasHero` are the PRE-primary-body inputs behind
        // `freeSlots` — the single resolved destination-army read, exposed so a caller pricing the
        // marginal effect of the primary body's OWN Command (a Hero's HeroCommandMarginalValue) does
        // not have to re-resolve the same destination army a second time.
        private static void ResolveDestination(WorldSnapshot snap, MaterializationPlan plan,
            out int freeSlots, out IReadOnlyList<WorthIt.DefenderProfile> members,
            out ArmySnapshot destArmy, out int nominalCapacity, out int occupiedSlots, out bool destHasHero)
        {
            members = System.Array.Empty<WorthIt.DefenderProfile>();
            freeSlots = -1;
            destArmy = null;
            nominalCapacity = ArmyData.ComputeCapacity(System.Array.Empty<UnitData>(), isGarrison: false);
            occupiedSlots = 0;
            destHasHero = false;
            if (plan == null)
                return;

            // The plan's own primary body (real card in hand or a generated base def). Its hero /
            // CommandRating handling is entirely inside ArmyData.ComputeProjectedCapacity.
            CardDefinition primary = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
            const int primaryBodySlots = 1;

            switch (plan.AttackRefitPrimaryId.HasValue ? DeploymentKind.ExistingArmy : plan.Deploy.Kind)
            {
                case DeploymentKind.ExistingArmy:
                case DeploymentKind.Garrison:
                {
                    int wantId = plan.AttackRefitPrimaryId
                        ?? (plan.Deploy.Army != null ? plan.Deploy.Army.Id : -1);
                    ArmySnapshot a = null;
                    if (snap?.Self?.Armies != null)
                        foreach (ArmySnapshot s in snap.Self.Armies)
                            if (s != null && s.ArmyId == wantId) { a = s; break; }
                    if (a == null)
                        return;                       // stale plan — leave -1 / heroless defaults
                    destArmy = a;
                    // §3.3 P2 — an aura's EligiblePredicate must be able to see hero allies too.
                    members = a.MembersWithHeroes != null && a.MembersWithHeroes.Count > 0
                        ? a.MembersWithHeroes
                        : (a.Members ?? (IReadOnlyList<WorthIt.DefenderProfile>)members);
                    nominalCapacity = a.Capacity;
                    occupiedSlots = a.OccupiedBattleSlots;
                    destHasHero = a.HasHero;
                    // Mirror the ArmyData domain rule: in a FIELD army a hero rewrites capacity to
                    // its CommandRating ONLY as the FIRST hero — a second hero is appended after the
                    // existing commander and does NOT raise capacity (no auto TryReorderCommander).
                    // A GARRISON is kept ordered by CommandRating, so its best hero governs.
                    bool incomingHero = primary != null && primary.cardType == CardType.Hero;
                    int cap = plan.AttackRefitPromotesCommander
                        ? AiPower.ProjectMaterialization(plan).CommandRating
                        : a.IsGarrison && incomingHero
                            ? ArmyData.ComputeProjectedGarrisonCapacity(nominalCapacity, a.HeroCount,
                                a.BestHeroCommandRating, 1, primary.commandRating)
                            : ArmyData.ComputeProjectedCapacity(nominalCapacity, destHasHero, primary);
                    freeSlots = System.Math.Max(0, cap - occupiedSlots - primaryBodySlots);
                    return;
                }
                case DeploymentKind.NewArmy:
                case DeploymentKind.ReusableShell:
                {
                    // Nominal capacity of the (heroless, empty) destination base: a ReusableShell's
                    // own snapshot capacity when present, else the freshly-created field-army value
                    // (already the default `nominalCapacity` set above). occupiedSlots/destHasHero
                    // stay at their defaults — a reusable shell is definitionally empty and heroless
                    // (ReusableArmySelector.IsReusableShell requires Members.Count == 0).
                    if (plan.Deploy.Kind == DeploymentKind.ReusableShell && plan.Deploy.Army != null
                        && snap?.Self?.Armies != null)
                        foreach (ArmySnapshot s in snap.Self.Armies)
                            if (s != null && s.ArmyId == plan.Deploy.Army.Id) { nominalCapacity = s.Capacity; break; }

                    // Same canonical rule as a real recipient: a hero primary sets capacity to its
                    // CommandRating (first hero into an empty base), a non-hero keeps nominalCap —
                    // NOT Math.Max(heroCr, nominalCap), which is where phantom slots came from.
                    int cap = ArmyData.ComputeProjectedCapacity(nominalCapacity, hasExistingHero: false, incoming: primary);
                    freeSlots = System.Math.Max(0, cap - primaryBodySlots);
                    return;
                }
            }
        }

        public int CountEligibleAllies(System.Func<WorthIt.DefenderProfile, bool> predicate)
        {
            int n = 0;
            foreach (WorthIt.DefenderProfile m in DestArmyMembers)
                if (predicate == null || predicate(m))
                    n++;
            return n;
        }

        // §3.1 follow-up — KNOWN enemy bodies near the deploy that `predicate` (an AoE effect's
        // TargetFilter) would actually hit. null predicate => every body (the LocalEnemyBodies
        // count, which also covers sightings that carry no per-unit profiles).
        public int CountEligibleEnemies(System.Func<WorthIt.DefenderProfile, bool> predicate)
        {
            if (predicate == null)
                return LocalEnemyBodies;
            int n = 0;
            foreach (WorthIt.DefenderProfile m in LocalEnemyProfiles)
                if (predicate(m))
                    n++;
            return n;
        }
    }
}
