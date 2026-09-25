using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // Stage model types for the V2 pipeline — design record: AiStrategyV2Pipeline.cs file header.

    // Normalised radar axes. Order is the canonical iteration order for every Dictionary<DesireAxis,*>
    // and every log line below. Management is intentionally absent — see the file header.
    public enum DesireAxis { Recon, Aggression, Economy, Development }

    public static class DesireAxes
    {
        public static readonly DesireAxis[] All =
        {
            DesireAxis.Recon, DesireAxis.Aggression,
            DesireAxis.Economy, DesireAxis.Development,
        };

        public static string Abbrev(DesireAxis a)
        {
            switch (a)
            {
                case DesireAxis.Recon: return "RCN";
                case DesireAxis.Aggression: return "AGG";
                case DesireAxis.Economy: return "ECO";
                default: return "DEV";
            }
        }

        // Strategy-level mapping from factual state invalidations to the task families whose
        // prior conclusions are now dirty. State owns the flags; Orchestration only asks this
        // policy which local family to re-admit.
        internal static StrategicInvalidationReason InvalidationMaskFor(DesireAxis axis)
        {
            switch (axis)
            {
                case DesireAxis.Recon:
                    // ATK §54 — a Base is a Recon WORLD FACT, not just infrastructure: it is the
                    // home anchor, the nearest-owned-base distance origin, the local exploration
                    // anchor and the coverage anchor. Building, capturing or losing one changes
                    // every one of those, so Recon has to be re-admitted on Infrastructure exactly
                    // the way Economy and Development already are — otherwise the settled-step loop
                    // keeps answering from the pre-capture topology for the rest of the turn.
                    return StrategicInvalidationReason.ReconKnowledge
                        | StrategicInvalidationReason.Actor
                        | StrategicInvalidationReason.Infrastructure;
                case DesireAxis.Aggression:
                    // ATK §55 — own/foreign Base facts drive the Aggression lane too: Attack
                    // objectives come from known enemy Bases/Citadels, and every offensive lane's
                    // route, support reachability and recovery/return base come from our own Base
                    // network. A Base built, captured, lost, discovered or observed under a new
                    // owner must therefore re-admit Aggression, not wait for the next turn.
                    return StrategicInvalidationReason.Contact
                        | StrategicInvalidationReason.Actor
                        | StrategicInvalidationReason.EventState
                        | StrategicInvalidationReason.Threat
                        | StrategicInvalidationReason.Infrastructure;
                case DesireAxis.Economy:
                    // Economy feasibility depends on where a Hero-led builder is NOW, not only
                    // on newly discovered resources. A Recon step can deliver that builder onto
                    // an already-known extraction hex; without Actor here the settled-step loop
                    // re-admits Recon alone and immediately walks the Hero away before the
                    // existing Phase-A infrastructure owner gets another chance to build.
                    return StrategicInvalidationReason.Actor
                        | StrategicInvalidationReason.Resources
                        | StrategicInvalidationReason.Infrastructure
                        | StrategicInvalidationReason.Hand
                        | StrategicInvalidationReason.Capability
                        | StrategicInvalidationReason.ResourceSite;
                case DesireAxis.Development:
                    // Contact/Threat: an upgrade's value is its matchup against the KNOWN threats
                    // (StrategicCardEvaluator.EquipmentMatchupFit), so a new or changed
                    // sighting can change which recipient/output wins. The admission fingerprint
                    // still suppresses the pass when the threat compositions did not change.
                    return StrategicInvalidationReason.Contact
                        | StrategicInvalidationReason.Threat
                        | StrategicInvalidationReason.Actor
                        | StrategicInvalidationReason.Resources
                        | StrategicInvalidationReason.Infrastructure
                        | StrategicInvalidationReason.Hand
                        | StrategicInvalidationReason.Capability
                        | StrategicInvalidationReason.ResourceSite;
                default:
                    return StrategicInvalidationReason.None;
            }
        }
    }

    // --- Stage 2 output: the single shared world scan (WorldSnapshot). Every later stage reads
    //     ONLY this, never raw game state. Types live in WorldSnapshot.cs; the scan that fills it
    //     is WorldAnalysis.Scan in WorldAnalysis.cs.

    // --- Stage 3a output: INDEPENDENT raw desire intensities in [0..1], one per axis, plus the
    //     two out-of-simplex absolute scalars. Not yet normalised.
    public sealed class DesireVector
    {
        public readonly Dictionary<DesireAxis, float> Raw = new Dictionary<DesireAxis, float>();
        // Absolute, NOT a share of anything. Modifiers only (see file header).
        public float MilitaryThreat;   // 0 = no known threat ... 1 = existential
        public float EconomicRunway;   // 0 = broke/stalled ... 1 = deep surplus

        public static DesireVector Neutral()
        {
            var v = new DesireVector();
            foreach (DesireAxis a in DesireAxes.All)
                v.Raw[a] = 0.5f;
            return v;
        }
    }

    // --- Stage 3b output: the normalised allocation vector (sum of Weight == 1).
    public sealed class Radar
    {
        public readonly Dictionary<DesireAxis, float> Weight = new Dictionary<DesireAxis, float>();

        public static Radar Even()
        {
            var r = new Radar();
            foreach (DesireAxis a in DesireAxes.All)
                r.Weight[a] = 1f / DesireAxes.All.Length;
            return r;
        }

        // The ONLY normalisation point in the pipeline. Raw independent intensities in, an
        // allocation vector summing to 1 out.
        public static Radar Normalize(DesireVector desires)
        {
            var r = new Radar();
            float sum = 0f;
            foreach (DesireAxis a in DesireAxes.All)
                sum += UnityEngine.Mathf.Max(0f, desires.Raw.TryGetValue(a, out float w) ? w : 0f);
            if (sum < 0.0001f)
                return Even();
            foreach (DesireAxis a in DesireAxes.All)
                r.Weight[a] = UnityEngine.Mathf.Max(0f, desires.Raw[a]) / sum;
            return r;
        }

        public string DebugLine()
        {
            return string.Join(" ", DesireAxes.All.Select(a =>
                $"{DesireAxes.Abbrev(a)} {Weight[a].ToString("0.00", CultureInfo.InvariantCulture)}"));
        }
    }

    // --- The strategic coefficient that scales mission VALUE by Radar (RadarValueScale) now
    //     lives in Strategy/Desire (DesireEvaluators.cs) — it computes a Radar-derived number, the
    //     same responsibility as the rest of that file, not an Orchestration one.

    // --- How much each axis a single mission serves. MANY-TO-MANY (risk 1): never collapse to one
    //     category. Values are 0..1 "relevance", not required to sum to anything.
    public sealed class AxisContribution
    {
        public readonly Dictionary<DesireAxis, float> Value = new Dictionary<DesireAxis, float>();
    }
}
