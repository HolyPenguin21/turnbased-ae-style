using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // BaselineForceReadinessDemands (AI-MGR-01 standing-force pull).
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
    public static partial class DemandLayer
    {
        // ---------------------------------------------------------------------------------------
        //  AI-MGR-01 — BaselineForceReadiness (spec §4). NOT a threat response and NOT a radar
        //  desire: the AI must continuously keep a reasonable standing potential for future tasks.
        //  Emits at most ONE low-priority FieldCombatPower demand so an ordinary combat unit gets
        //  Phase-A pull instead of only Phase-B surplus. Charged to the Defence entitlement (a
        //  standing field force is latent defence). Suppressed whenever an Aggression/Defence
        //  combat demand already exists this pass, when Need is below the threshold, or when the
        //  AI already fields enough free field power and combat actors. It only decides a card is
        //  worth materialising — never which army/garrison it joins (that stays Housekeeping's).
        // ---------------------------------------------------------------------------------------
        private static IEnumerable<AxisDemand> BaselineForceReadinessDemands(WorldSnapshot snap,
            PlayerSetupData player, ActorCommitments commitments, IReadOnlyList<AxisDemand> already)
        {
            if (snap?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Baseline] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            bool combatDemandExists = already != null && already.Any(d => d != null
                && (d.Capability == CapabilityKind.FieldCombatPower || d.Capability == CapabilityKind.Hero
                    || d.Capability == CapabilityKind.GarrisonCombatPower));
            if (combatDemandExists)
            {
                AiDebugLog.Write("[AI][V2][Demand][Baseline] decision=SATISFIED reason=combat_demand_already_raised");
                yield break;
            }

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            BaselineForceReadiness r = BaselineForceReadiness.Evaluate(snap, inv, snap.Self?.Hand);

            if (r.Need < AiConfigV2.baselineReadinessDemandMinNeed)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Baseline] decision=SATISFIED reason=need_below_threshold "
                    + $"need={r.Need:0.00} min={AiConfigV2.baselineReadinessDemandMinNeed:0.00} "
                    + $"actors={r.CombatActors} freeFieldPower={r.FreeFieldPower:0.#} "
                    + $"hasBody={(r.HasFieldBody ? 1 : 0)} hasHero={(r.HasHero ? 1 : 0)}");
                yield break;
            }

            if (r.FreeFieldPower >= AiConfigV2.baselineReadinessSatisfiedPower
                && r.CombatActors >= AiConfigV2.baselineReadinessTargetActors)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Baseline] decision=SATISFIED reason=standing_force_sufficient "
                    + $"need={r.Need:0.00} actors={r.CombatActors}/{AiConfigV2.baselineReadinessTargetActors} "
                    + $"freeFieldPower={r.FreeFieldPower:0.#}/{AiConfigV2.baselineReadinessSatisfiedPower:0.#}");
                yield break;
            }

            // P1.7 — FLAT low Value. The Need >= min gate already decides the demand exists; Need
            // is priced once, downstream, in the evaluator's ForceGrowthValue. Scaling Value by
            // Need too (then Value x Plan.Score in arbitration) triple-counted the same signal.
            float value = AiConfigV2.baselineReadinessDemandValue;
            AiDebugLog.Write($"[AI][V2][Demand][Baseline] decision=CREATE capability=FieldCombatPower desired=1 "
                + $"need={r.Need:0.00} value={value:0.#} actors={r.CombatActors} freeFieldPower={r.FreeFieldPower:0.#} "
                + $"hasBody={(r.HasFieldBody ? 1 : 0)} hasHero={(r.HasHero ? 1 : 0)}");
            yield return new AxisDemand
            {
                RequestingAxis = DesireAxis.Defence,
                Capability = CapabilityKind.FieldCombatPower,
                DesiredAmount = 1,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = null,
                Value = value,
                Explain = $"baseline force readiness: need {r.Need:0.00} (actors {r.CombatActors}, "
                    + $"free field power {r.FreeFieldPower:0.#}, body {(r.HasFieldBody ? 1 : 0)}, "
                    + $"hero {(r.HasHero ? 1 : 0)}); maintain standing potential for future tasks",
            };
        }
    }
}
