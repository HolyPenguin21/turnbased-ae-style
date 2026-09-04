using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ---- tempo candidate model ------------------------------------------------------------
    internal enum TempoKind { PlayMat, PlayNonCombat, Draw, MaintenanceSpend, PressureSpend, Hold, EndTurn }

    internal sealed class TempoCandidate
    {
        public TempoKind Kind;
        public string ActionKey;
        public float Utility;
        public float ApCost;              // spec §6 — must fit SPENDABLE (not raw) AP
        public ResourceCost ResCost;      // spec §6 — full persistent-resource cost vector (null = none)
        public bool ConsumesGeneration;   // spec §P0 — shared maxGenerationActionsPerTurn budget
        public bool CountsAsSurplusCardPlay; // spec §P0 — MGR-01 maxSurplusActionsPerTurn sub-cap
        public bool CountsAsTerminalDraw;    // spec §P0 — maxTerminalDrawsPerTurn sub-cap
        public string Label;
        public string DrawDiag;   // Draw only — preformatted valuation breakdown for the log
        public MatSurplusDecision Mat;
        public NonCombatCardPlayer.NonCombatPlay Nc;
        public StrategicPressurePlan Pressure;
        public StrategicSpendCandidate Spend;   // non-card strategic spend — executed verbatim
    }

    // AI-MGR-01 review-r4 finding 9a — the materialization-surplus lane's per-iteration decision.
    internal struct MatSurplusDecision
    {
        public bool Admissible;
        public MaterializationPlan Plan;
        public float Utility;
        public AxisDemand Residual;      // non-null => operational strategic residual
        public CapabilityInventory Inv;
        public string DeferLog;
    }

    // ARCH-02 §8/§39/§42 — the Phase-B tempo candidate provider. Builds the ONE comparable
    // candidate space (PlayCard mat / PlayCard non-combat / Draw / non-card strategic spend /
    // pressure advance / Hold / EndTurn) for the arbiter loop in StrategicPhaseB. It scores each
    // candidate with the canonical owners (StrategicCardEvaluator via the builders, HoldEvaluator,
    // the Draw expected-deck-value model) and applies only STRUCTURAL admission guards; it does not
    // select or execute. Extracted verbatim from StrategicManager.
    internal static class TempoCandidateProvider
    {
        internal static bool IsSpend(TempoKind k) =>
            k == TempoKind.PlayMat || k == TempoKind.PlayNonCombat || k == TempoKind.Draw
            || k == TempoKind.MaintenanceSpend || k == TempoKind.PressureSpend;

        internal static List<TempoCandidate> BuildTempoCandidates(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            StrategicPhaseResult result, IReadOnlyList<ReconObjective> reconObjectives, float spendableAp,
            StrategicTempoBudget budget, bool verbose)
        {
            var list = new List<TempoCandidate>();

            // PlayCard — materialization lane. Utility = StrategicCardEvaluator decision score, verbatim.
            MatSurplusDecision mat = ComputeMatDecision(snap, player, root, hand, ctx, commitments,
                result, reconObjectives);
            if (mat.Admissible && mat.Plan != null)
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PlayMat, Mat = mat, Utility = mat.Utility,
                    ApCost = mat.Plan.ApCost, ResCost = mat.Plan.ResCost,
                    ConsumesGeneration = mat.Plan.Generation != null,
                    CountsAsSurplusCardPlay = true,
                    ActionKey = "mat:" + mat.Plan.StableKey,
                    Label = $"{mat.Plan.Kind} {AiCardLog.Plan(mat.Plan)}"
                        + (mat.Residual != null ? $" (residual {mat.Residual.Capability})" : ""),
                });
            else if (mat.DeferLog != null && verbose)
                AiDebugLog.Write(mat.DeferLog + " [tempo: not a candidate]");

            // PlayCard — non-combat lane (Aviation / Base / Facility / standalone Equipment).
            // Utility = StrategicCardEvaluator.ScoreNonCombat NetScore, verbatim (via BestPlay.Score).
            NonCombatCardPlayer.NonCombatPlay nc = NonCombatCardPlayer.BestPlay(
                snap, player, root, hand, ctx, out _, null, result.Reservation);
            TempoCandidate ncCand = null;
            if (nc != null)
            {
                // §P1.4 — a GENERATED non-combat candidate still owes the Challenge's ResourceCost
                // pre-mint. EffectivePlayResourceCost of the temporary stand-in is null after a
                // successful mint, which is wrong at arbitration time. Use the generation cost.
                ResourceCost ncResCost = nc.Generation != null
                    ? nc.Generation.GenerationResourceCost
                    : (nc.Card != null ? nc.Card.EffectivePlayResourceCost : null);
                ncCand = new TempoCandidate
                {
                    Kind = TempoKind.PlayNonCombat, Nc = nc, Utility = nc.Score,
                    ApCost = nc.Card != null ? nc.Card.EffectivePlayApCost : 0f,
                    ResCost = ncResCost,
                    ConsumesGeneration = nc.Generation != null,
                    CountsAsSurplusCardPlay = true,
                    ActionKey = $"nc:{nc.Kind}:{nc.Explain}",
                    Label = $"{nc.Kind} {nc.Explain}",
                };
                list.Add(ncCand);
            }

            // §P0.1 — the only card alternatives that suppress Draw are ones actually SELECTABLE
            // right now: not over the surplus card-play budget, AP + resources spendable. A card
            // blocked by the budget / affordability / placement must not make Draw look worthless.
            TempoCandidate matCand = list.FirstOrDefault(c => c.Kind == TempoKind.PlayMat);
            bool CardSelectableNow(TempoCandidate c) => c != null && !budget.CardCapHit
                && c.ApCost <= spendableAp + AiConfigV2.allocatorSliceEpsilon
                && StrategicSpendability.FitsSpendableResources(player, root, ctx, c.ResCost);
            float bestSelectablePlay = Mathf.Max(
                CardSelectableNow(matCand) ? matCand.Utility : 0f,
                CardSelectableNow(ncCand) ? ncCand.Utility : 0f);

            // DrawCard — a real scored peer (spec §1), NOT penalised for holding H/E/M/T (costs AP only).
            if (AiConfigV2.surplusAllowDraw && CardDrawExecutor.CanCycle(root, hand, ctx)
                && spendableAp + AiConfigV2.allocatorSliceEpsilon >= ctx.DrawApCost)
            {
                float drawU = DrawCandidateUtility(snap, hand, ctx, bestSelectablePlay, out string drawDiag);
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.Draw, Utility = drawU, ApCost = ctx.DrawApCost, ActionKey = "draw",
                    CountsAsTerminalDraw = true, DrawDiag = drawDiag,
                    Label = $"cycle 1 card ({ctx.DrawApCost} AP), hand {hand.Hand.Count}/{ctx.HandCapacity}",
                });
            }

            // ExistingStrategicSpendAction — genuinely NON-CARD strategic actions only (Base/Citadel
            // slot-capacity upgrade). Facility / Equipment / generation are ordinary PlayCard
            // candidates above (one StrategicCardEvaluator, spec §5). Every eligible non-card spend
            // is its own candidate — no hidden category priority chain (spec §3).
            foreach (StrategicSpendCandidate sp in StrategicMaintenancePolicy.EnumerateCandidates(player, root, hand, ctx))
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.MaintenanceSpend, Utility = sp.Utility, ApCost = sp.ApCost,
                    ResCost = sp.ResCost, Spend = sp,
                    ActionKey = "maint:" + sp.StableKey, Label = sp.Label,
                });

            StrategicPressurePlan pressure = StrategicPressureAdvance.BuildPlan(player, root, hand, ctx, commitments);
            if (pressure != null && pressure.Army != null)
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PressureSpend, Pressure = pressure,
                    Utility = AiConfigV2.tempoPressureAdvanceValue,
                    ApCost = pressure.Army.HasActivatedThisTurn ? 0f : pressure.Army.ActivationApCost,
                    ActionKey = "pressure:" + pressure.Army.Id,
                    Label = $"advance army #{pressure.Army.Id} toward known enemy Citadel "
                        + $"({pressure.TargetHex.Q},{pressure.TargetHex.R})",
                });

            // HoldResources — the value of NOT spending. AP is lost at EndTurn so holding it is ~0;
            // the loose persistent-resource pool is worth holding only when the economy is fragile.
            // (Per-card hold value is already inside every PlayCard NetScore — spec §5.)
            list.Add(new TempoCandidate
            {
                Kind = TempoKind.Hold, ActionKey = "hold",
                Utility = HoldEvaluator.HoldResourcesUtility(root, snap),
                Label = "keep unspent resources for future turns",
            });
            list.Add(new TempoCandidate
            {
                Kind = TempoKind.EndTurn, ActionKey = "endturn", Utility = 0f, Label = "end the turn",
            });
            return list;
        }

        // spec §1/§P0.1/§P1.6 — expected value of converting stranded AP into a fresh card option,
        // in the same [~0..5] band the PlayCard candidates use. Terms:
        //   · expectedDeckValue  = floor + normalised mean remaining-deck STRATEGIC card value
        //                          (combat power + generic role coverage + equipment/infra profile),
        //                          tapered when the deck is nearly empty;
        //   · fill factor         (softened — a single free slot is still a legal, ~0.70 draw);
        //   · last-slot block risk (softened to a small penalty);
        //   · AP opportunity cost;
        //   · handQualityPenalty  = weight * the best play SELECTABLE RIGHT NOW (0 if every card
        //                          alternative is blocked by budget / affordability / placement).
        private static float DrawCandidateUtility(WorldSnapshot snap, AiHandData hand, AiTurnContext ctx,
            float bestSelectablePlay, out string diag)
        {
            int freeSlots = Mathf.Max(0, ctx.HandCapacity - hand.Hand.Count);
            float fill = Mathf.Clamp(AiConfigV2.tempoDrawFillFloor
                + AiConfigV2.tempoDrawFillPerSlot * freeSlots, 0f, 1f);

            var deck = hand.RemainingDeck?.Where(d => d != null).ToList();
            float deckMean = 0f;
            if (deck != null && deck.Count > 0)
            {
                float sum = 0f;
                foreach (CardDefinition d in deck)
                    sum += GenericStrategicCardValue(d);
                deckMean = sum / deck.Count;
            }
            float deckValue = Mathf.Clamp01(deckMean / Mathf.Max(1f, AiConfigV2.tempoDrawDeckValueNorm));
            float thinTaper = deck == null ? 0f
                : Mathf.Clamp01(deck.Count / Mathf.Max(1f, AiConfigV2.tempoDrawThinDeckTaperCards));
            float expectedDeckValue =
                (AiConfigV2.tempoDrawBaseValue + AiConfigV2.tempoDrawDeckValueWeight * deckValue) * thinTaper;

            float blockRisk = freeSlots <= 1 ? AiConfigV2.tempoDrawFutureBlockPenalty : 0f;
            float apOpp = AiConfigV2.tempoDrawApOpportunityWeight * ctx.DrawApCost;
            float handQualityPenalty = AiConfigV2.tempoDrawHandActionableWeight * Mathf.Max(0f, bestSelectablePlay);

            float u = expectedDeckValue * fill - blockRisk - apOpp - handQualityPenalty;
            diag = $"expDeckVal {F(expectedDeckValue)} (mean {F(deckMean)} taper {F(thinTaper)}) freeSlots {freeSlots} "
                + $"fill {F(fill)} blockRisk {F(blockRisk)} apOpp {F(apOpp)} handQualPen {F(handQualityPenalty)} "
                + $"(selectablePlay {F(bestSelectablePlay)}) => draw {F(u)}";
            return u;
        }

        // spec §P1.6 — a lightweight GENERIC strategic value for an unseen deck card (the concrete
        // card is not drawn yet, so this is not a second StrategicCardEvaluator). Combat body power
        // + one bump per generic strategic role the card's granted abilities cover (AoE / Regen /
        // Aura / Summon / … via StrategicEffectRegistry), plus a flat profile for the non-combat
        // card families.
        private static float GenericStrategicCardValue(CardDefinition d)
        {
            if (d == null) return 0f;
            switch (d.cardType)
            {
                case CardType.Equipment:
                    return AiConfigV2.tempoDrawEquipmentValue;
                case CardType.Base:
                case CardType.Facility:
                    return AiConfigV2.tempoDrawInfraValue;
                case CardType.Unit:
                case CardType.Hero:
                default:
                {
                    float v = Mathf.Max(0f, AiPower.ToPowerUnit(d).BasePower);
                    if (d.grantedAbilities != null && d.grantedAbilities.Count > 0)
                    {
                        int roles = StrategicEffectRegistry
                            .Roles(d.grantedAbilities, Mathf.Max(1, d.moveMax)).Distinct().Count();
                        v += roles * AiConfigV2.tempoDrawEffectRoleValue;
                    }
                    return v;
                }
            }
        }

        private static bool PlanBaseIsHeroCard(MaterializationPlan plan)
        {
            CardDefinition def = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            return def != null && def.cardType == CardType.Hero;
        }

        private static bool CanDeliverResidualOperationally(MaterializationPlan plan, AxisDemand demand)
        {
            if (plan == null || demand == null)
                return false;
            switch (demand.Capability)
            {
                case CapabilityKind.ScoutCapability:
                    return true;
                case CapabilityKind.GarrisonCombatPower:
                    return plan.Deploy.Kind == DeploymentKind.Garrison;
                case CapabilityKind.Hero:
                    return plan.Deploy.Kind == DeploymentKind.ExistingArmy
                        && plan.Deploy.Army != null
                        && plan.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation);
                case CapabilityKind.FieldCombatPower:
                {
                    if (plan.Deploy.Kind == DeploymentKind.Garrison)
                        return false;
                    CardDefinition def = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
                    bool hero = def != null && def.cardType == CardType.Hero;
                    if (!hero)
                        return true;
                    return plan.Deploy.Kind == DeploymentKind.ExistingArmy
                        && plan.Deploy.Army != null
                        && plan.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation);
                }
                default:
                    return false;
            }
        }

        // §P1 — multiplier on the surplus-admission threshold for a generic garrison deposit when
        // the garrison is already a strong defensive stack (>= a fraction of BestStackPotential)
        // and no asset is threatened. 1f otherwise.
        private static float GarrisonSaturationThresholdMult(WorldSnapshot snap, MaterializationPlan plan,
            AxisDemand residual)
        {
            if (residual != null || plan == null || plan.Deploy.Kind != DeploymentKind.Garrison
                || snap?.Self == null)
                return 1f;
            bool assetThreat = snap.Threat?.Threats != null && snap.Threat.Threats.Count > 0;
            if (assetThreat)
                return 1f;
            float reserve = AiConfigV2.garrisonSaturatedReserveFractionOfBestStack
                * Mathf.Max(0f, snap.Self.BestStackPotential);
            return reserve > 0f && snap.Self.GarrisonPower >= reserve
                ? AiConfigV2.garrisonSaturatedSurplusThresholdMult : 1f;
        }

        // §P1 — a generic (no-residual) surplus chain of ANY kind (Direct / Attach / Generate*)
        // that founds a fresh lone-member army (NewArmy / ReusableShell) on a hex where a garrison
        // OR an already-viable friendly field army sits: Housekeeping folds/absorbs that
        // lone-member army the same turn (create -> fold). A genuine forward outpost — no base and
        // no viable force of ours on the hex — is still allowed.
        private static bool GenericSurplusWouldChurn(PlayerSetupData player, MaterializationPlan plan)
        {
            if (plan == null)
                return false;
            if (plan.Deploy.Kind != DeploymentKind.NewArmy && plan.Deploy.Kind != DeploymentKind.ReusableShell)
                return false;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || !a.Hex.Equals(plan.Deploy.Hex))
                    continue;
                if (a.IsGarrison)
                    return true;
                if (a.IsPrison || a.IsAirArmy || a.IsAirfield || AiArmyRoles.IsSoloRecce(a))
                    continue;
                if (a.Members.Count >= 2
                    && AiPower.EffectiveArmyPower(a.Members) >= AiConfigV2.housekeepingViabilityPowerFloor)
                    return true;
            }
            return false;
        }

        // §P1 — generic surplus must not add a scout beyond the physical IsSoloRecce portfolio
        // cap, across EVERY chain kind (BestSurplus treats a recce card as ScoutCapability and
        // will build NewArmy / ReusableShell / Attach / Generate placements for it — the Recon
        // DemandLayer portfolio cap never sees those). Primary bound is the CURRENT desired
        // concurrency + a warm spare; ReconConcurrencyPolicy.HardCap is the absolute ceiling.
        private static bool ScoutSurplusPortfolioSaturated(PlayerSetupData player, MaterializationPlan plan,
            WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives)
        {
            if (plan == null || plan.FinalCapability != CapabilityKind.ScoutCapability)
                return false;
            int solo = ArmyRegistry.AllForOwner(player).Count(a => a != null && AiArmyRoles.IsSoloRecce(a));
            if (solo >= ReconConcurrencyPolicy.HardCap)
                return true;
            if (reconObjectives == null)
                return false;
            var runnable = reconObjectives
                .Where(o => o != null && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            int desired = ReconConcurrencyPolicy.DesiredTotal(snap, runnable);
            return solo >= desired + AiConfigV2.scoutSurplusWarmSpare;
        }

        private static MatSurplusDecision ComputeMatDecision(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            StrategicPhaseResult result, IReadOnlyList<ReconObjective> reconObjectives)
        {
            var dec = new MatSurplusDecision
            {
                Inv = CapabilityInventory.Build(snap, player, commitments),
            };
            (MaterializationPlan plan, float utility)? pick = MaterializationCandidateBuilder.BestSurplus(
                snap, player, root, hand, ctx, dec.Inv, commitments, result.Reservation);
            if (pick == null)
                return dec;

            MaterializationPlan plan = pick.Value.plan;
            AxisDemand matchedResidual = result.Reservation.BestUnresolvedDemandFor(plan);
            AxisDemand residual = matchedResidual != null && CanDeliverResidualOperationally(plan, matchedResidual)
                ? matchedResidual : null;
            SurplusAdmission admission = SurplusAdmissionPolicy.Evaluate(root, player, plan);

            if (matchedResidual != null && residual == null)
                AiDebugLog.Write($"[AI][V2]   strat.B — residual bypass denied for {plan.StableKey}: "
                    + $"{plan.Deploy.Kind} cannot operationally deliver {matchedResidual.Capability}; "
                    + "evaluate as generic surplus");

            if (matchedResidual != null && residual == null
                && matchedResidual.Capability == CapabilityKind.Hero && PlanBaseIsHeroCard(plan))
            {
                dec.DeferLog = $"[AI][V2]   strat.B — hold {plan.StableKey}: hero card matches "
                    + $"unresolved {matchedResidual} but no placement delivers it; keep in hand";
                return dec;
            }

            // §P1 anti-grind — a strong-garrison generic deposit with nothing threatened must clear
            // a much higher bar (satMult). This is STRUCTURAL, not the ordinary utility floor: it
            // still gates the candidate even under stranded-AP tempo pressure so the garrison is not
            // ground from 6 to 40+ power with threats=0.
            float satMult = GarrisonSaturationThresholdMult(snap, plan, residual);
            float effThreshold = admission.EffectiveThreshold * satMult;

            if (residual == null && GenericSurplusWouldChurn(player, plan))
            {
                dec.DeferLog = $"[AI][V2]   strat.B — hold {plan.StableKey} {AiCardLog.Plan(plan)}: "
                    + "generic surplus would found a lone-member army housekeeping folds the same turn";
                return dec;
            }
            if (residual == null && ScoutSurplusPortfolioSaturated(player, plan, snap, reconObjectives))
            {
                dec.DeferLog = $"[AI][V2]   strat.B — hold {plan.StableKey} {AiCardLog.Plan(plan)}: "
                    + "generic surplus would add a scout beyond the physical portfolio "
                    + $"(desired concurrency + warm spare, hard cap {ReconConcurrencyPolicy.HardCap})";
                return dec;
            }
            if (residual == null && satMult > 1f && plan.Score < effThreshold)
            {
                dec.DeferLog = $"[AI][V2]   strat.B — defer {plan.StableKey} {AiCardLog.Plan(plan)} "
                    + $"score {F(plan.Score)} < garrison-saturated bar {F(effThreshold)} (x{F(satMult)})";
                return dec;
            }

            dec.Admissible = true;
            dec.Plan = plan;
            // pick.Value.utility is ALREADY the global decision score (NetScore + operational-residual
            // urgency), computed once in MaterializationCandidateBuilder.BestSurplus. Not re-adjusted
            // here — the arbiter compares it against Hold/EndTurn as-is (spec §5).
            dec.Utility = pick.Value.utility;
            dec.Residual = residual;
            return dec;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
