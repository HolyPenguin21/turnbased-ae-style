using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public static partial class AggressionDemandEvaluator
    {
        // A garrison under a known threat defends that asset in place: the worst threat on this
        // hex adds the same direction / prevented-damage terms ActiveDefence prices it with.
        internal static TaskScore BuildHeldBaseGarrisonScore(WorldSnapshot snap, HexCoord baseHex)
        {
            AssetThreatSnapshot worst = snap?.Threat?.Threats?
                .Where(t => t?.Asset != null && t.Contact?.Army != null && t.Asset.Hex.Equals(baseHex))
                .OrderByDescending(t => t.Severity).ThenBy(t => t.Contact.Army.ArmyId)
                .FirstOrDefault();
            float threatDirection = 0f, preventedDamage = 0f;
            if (worst != null)
                ActiveDefenceObjectiveEvaluator.AssetDefenceTerms(worst,
                    out threatDirection, out preventedDamage);
            return new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(
                    AiConfigV2.assetValueBase / Mathf.Max(1f, AiConfigV2.assetValueCitadel)),
                threatDirection: threatDirection,
                preventedDamage: preventedDamage);
        }

        // The power this own base's garrison still lacks: the one garrison defence floor
        // (AiArmyRoles.GarrisonDefenceFloor) on the snapshot's ground force — at least one body and
        // the Citadel / Base share of power, raised to the known threat against this garrison
        // (AiArmyRoles.GarrisonThreatFloor) — minus its power; an empty garrison asks for at least
        // one body. Shared by the hand demand below and GarrisonRelief.
        internal static float HeldBaseGarrisonShortfall(WorldSnapshot snap, ArmySnapshot garrison,
            out float floor)
        {
            floor = AiArmyRoles.GarrisonDefenceFloor(snap.Observer, snap.Self.AvailablePower,
                garrison.Hex, garrison.Hex.Equals(snap.Self.Citadel));
            int bodies = garrison.Members?.Count(AiArmyRoles.IsGroundBattleBody) ?? 0;
            return bodies == 0
                ? Mathf.Max(floor, AiConfigV2.combatPowerPerBodyEstimate)
                : floor - garrison.EffectiveArmyPower;
        }

        // Every own base, including a newly founded or vacated one, whose
        // garrison is below its non-hero floor is garrisoned from hand first. If the hand cannot
        // deliver, Housekeeping fills the floor at turn end from the holding army's most wounded,
        // then weakest, body (ArmyReorganizationCandidates, garrison fill). A relief detachment
        // already walking there (GarrisonRelief) covers its share, so the hand is not asked twice.
        private static void AppendHeldBaseGarrisonDemands(WorldSnapshot snap, List<string> diag,
            List<AxisDemand> demands)
        {
            IReadOnlyList<ArmySnapshot> armies = snap.Self.Armies
                ?? (IReadOnlyList<ArmySnapshot>)System.Array.Empty<ArmySnapshot>();
            foreach (HexCoord baseHex in snap.Self.BaseHexes ?? (IReadOnlyList<HexCoord>)System.Array.Empty<HexCoord>())
            {
                ArmySnapshot garrison = armies.FirstOrDefault(a => a != null && a.IsGarrison
                    && a.Hex.Equals(baseHex));
                if (garrison == null)
                    continue;
                float desired = HeldBaseGarrisonShortfall(snap, garrison, out float floor)
                    - GarrisonReliefObjectiveEvaluator.InFlightReliefPower(snap, baseHex);
                if (desired <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                string missing = $"{desired:0.#}";
                TaskScore score = BuildHeldBaseGarrisonScore(snap, baseHex);
                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE base=({baseHex.Q},{baseHex.R}) "
                    + $"capability=FieldCombatPower shape=Garrison missing={missing} desired={desired:0.#} "
                    + $"task={score.Value:0.##} reason=held_base_garrison_below_floor");
                demands.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DeliveryShape = CapabilityDeliveryShape.Garrison,
                    ConsumerPurpose = CapabilityConsumerPurpose.HeldBaseGarrison,
                    DesiredAmount = desired,
                    RequiredCapabilityPower = desired,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = baseHex,
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"garrison the own base ({baseHex.Q},{baseHex.R}) from hand: "
                        + $"{missing} power short of its floor {floor:0.#}; task={score.Value:0.##}",
                });
            }
        }
    }
}
