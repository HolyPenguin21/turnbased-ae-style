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
    // DefenceDemands.
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
    public static partial class DemandLayer
    {
        // ---------------------------------------------------------------------------------------
        //  DEF — a threatened Citadel/Base whose committed defence is below requirement. NEVER
        //  fires just because resources are free: it needs a real AssetThreatSnapshot above the
        //  severity trigger AND a saturation deficit. Existing garrison + own field armies already
        //  standing on the asset + defence bodies already requested earlier in THIS same call are
        //  all subtracted before a new demand is raised (spec §5).
        // ---------------------------------------------------------------------------------------
        private static IEnumerable<AxisDemand> DefenceDemands(WorldSnapshot s, DesireBreakdown b)
        {
            IReadOnlyList<AssetThreatSnapshot> threats = s?.Threat?.Threats;
            if (threats == null || threats.Count == 0 || s.Self?.Armies == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Defence] decision=NONE reason=no_asset_threats");
                yield break;
            }

            // Highest-severity threat per defended asset hex.
            var worst = new Dictionary<HexCoord, AssetThreatSnapshot>();
            foreach (AssetThreatSnapshot t in threats)
            {
                if (t?.Asset == null || t.Contact == null)
                    continue;
                if (t.Asset.Kind != AssetKind.Citadel && t.Asset.Kind != AssetKind.Base)
                    continue;
                if (t.Severity < AiConfigV2.defenceSeverityTrigger)
                    continue;
                if (!worst.TryGetValue(t.Asset.Hex, out AssetThreatSnapshot cur) || t.Severity > cur.Severity)
                    worst[t.Asset.Hex] = t;
            }
            if (worst.Count == 0)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Defence] decision=SATISFIED reason=no_threat_above_severity_trigger "
                    + $"trigger={AiConfigV2.defenceSeverityTrigger:0.##} threats={threats.Count}");
                yield break;
            }

            int emitted = 0;
            var plannedByHex = new Dictionary<HexCoord, float>();
            foreach (AssetThreatSnapshot t in worst.Values
                .OrderByDescending(x => x.Severity).ThenBy(x => x.Asset.Hex.Q).ThenBy(x => x.Asset.Hex.R))
            {
                if (emitted >= AiConfigV2.defenceMaxDemandsPerTurn)
                    break;

                HexCoord hex = t.Asset.Hex;
                float threateningPower = t.Contact.Army?.EffectiveArmyPower ?? 0f;
                float required = threateningPower * AiConfigV2.defenceReserveMargin;

                float existingGarrison = 0f, assignedField = 0f;
                foreach (ArmySnapshot a in s.Self.Armies)
                {
                    if (a == null || !a.Hex.Equals(hex)) continue;
                    if (a.IsGarrison) existingGarrison += a.EffectiveArmyPower;
                    else if (!a.IsAir && !a.IsPrison) assignedField += a.EffectiveArmyPower;
                }
                plannedByHex.TryGetValue(hex, out float planned);
                float available = existingGarrison + assignedField + planned;

                if (available + AiConfigV2.allocatorSliceEpsilon >= required)
                {
                    AiDebugLog.Write($"[AI][V2][Demand][Defence] decision=SATISFIED asset=({hex.Q},{hex.R}) "
                        + $"kind={t.Asset.Kind} severity={t.Severity:0.##} required={required:0.#} "
                        + $"available={available:0.#} (garrison={existingGarrison:0.#} field={assignedField:0.#} "
                        + $"planned={planned:0.#}) — saturated");
                    continue;
                }

                float deficit = required - available;
                int bodies = Mathf.Clamp(
                    Mathf.CeilToInt(deficit / Mathf.Max(1f, AiConfigV2.defencePerBodyPowerEstimate)),
                    1, AiConfigV2.defenceMaxBodiesPerAsset);
                plannedByHex[hex] = planned + bodies * AiConfigV2.defencePerBodyPowerEstimate;
                emitted++;

                AiDebugLog.Write($"[AI][V2][Demand][Defence] decision=CREATE asset=({hex.Q},{hex.R}) "
                    + $"kind={t.Asset.Kind} capability=GarrisonCombatPower desired={bodies} "
                    + $"severity={t.Severity:0.##} required={required:0.#} available={available:0.#} "
                    + $"deficit={deficit:0.#} (garrison={existingGarrison:0.#} field={assignedField:0.#})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Defence,
                    Capability = CapabilityKind.GarrisonCombatPower,
                    DesiredAmount = bodies,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = hex,
                    RequiredCapabilityPower = required,
                    Value = Mathf.Clamp01(t.Severity) * 100f,
                    Explain = $"{t.Asset.Kind} @({hex.Q},{hex.R}) under threat sev {t.Severity:0.##}: "
                        + $"need ~{required:0.#} defence, have {available:0.#} "
                        + $"(garrison {existingGarrison:0.#} + field {assignedField:0.#}); request {bodies} body(s)",
                };
            }

            if (emitted == 0)
                AiDebugLog.Write("[AI][V2][Demand][Defence] decision=SATISFIED reason=all_threatened_assets_saturated");
        }

        // ---------------------------------------------------------------------------------------
        //  ECO — a known extraction site with positive marginal income and acceptable payback.
        //  Deficit/starvation remain urgency multipliers, but are not admission prerequisites.
        //  Extraction and Base expansion each expose at most one demand to the shared allocator.
        // ---------------------------------------------------------------------------------------
    }
}
