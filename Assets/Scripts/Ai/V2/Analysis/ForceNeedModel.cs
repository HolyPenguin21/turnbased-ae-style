using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Game.Ai;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // One snapshot-pure owner of the force facts the axis chain hands down:
    //   Recon -> knowledge -> Economy -> budget -> Attack/Defence -> need -> Production amplifies.
    // Aggression reads presence, reserve and edge from here; Development (desire and every
    // Production output score) reads JustifiedForceNeed from here. Nobody else derives
    // "is there a fight", "what must stay home" or "how badly do we need force".
    internal readonly struct ForceNeed
    {
        // Share of the known fights our force cannot take now: neutral armies, event guards and
        // known enemy armies our ready or assemblable force cannot win (CombatOpportunity.IsViable),
        // and (2026-10-04) known defended hostile Bases/Citadels whose garrison needs more power
        // than our strongest stack can form (GroundCombatDemandPolicy.RequiredSitePower).
        public readonly float Offensive;
        // How far our best force is from out-classing the known enemy players (1 - RelativeEdge);
        // zero without enemy intel.
        public readonly float Enemy;
        // Share of the defensive reserve known threats demand that our total force cannot cover.
        public readonly float Defensive;
        // Idle stock the deck is not absorbing (DevelopmentReadiness.InvestmentSurplusByType, mean
        // over H/E/M/T, ramped and weighted). Rises while resources pile up and falls as they are
        // spent, so Production arms in advance for the next Attack instead of banking stock.
        public readonly float Surplus;
        public readonly bool Witnessed;

        public ForceNeed(float offensive, float enemy, float defensive, bool witnessed,
            float surplus = 0f)
        {
            Offensive = offensive;
            Enemy = enemy;
            Defensive = defensive;
            Surplus = surplus;
            Witnessed = witnessed;
        }

        // [0..1]. Zero without a military witness: Production never creates a need.
        public float Total => Witnessed
            ? Mathf.Clamp01(Mathf.Max(Mathf.Max(Offensive, Surplus), Mathf.Max(Enemy, Defensive)))
            : 0f;

        public override string ToString() =>
            $"need={Total:0.00} (offensive={Offensive:0.00} enemy={Enemy:0.00} "
            + $"defensive={Defensive:0.00} surplus={Surplus:0.00} witnessed={(Witnessed ? 1 : 0)})";
    }

    internal static class ForceNeedModel
    {
        private sealed class Box { public ForceNeed Value; }
        private static readonly ConditionalWeakTable<WorldSnapshot, Box> Cache =
            new ConditionalWeakTable<WorldSnapshot, Box>();

        // Presence only: no objectives, viability, threat severity or task value is read here.
        internal static bool HasKnownCombatActivity(WorldSnapshot snap) =>
            (snap?.Known?.NeutralSightings?.Count ?? 0) > 0
            || (snap?.Known?.EventGuards?.Count ?? 0) > 0
            || (snap?.Known?.EnemySightings?.Count ?? 0) > 0
            || (snap?.Known?.Buildings?.Any(b => b.Owner != null && b.Owner != snap.Observer
                && !b.Owner.IsNeutral && !b.Owner.IsEliminated) ?? false);

        // 2026-10-04 — the Radar's Aggression witness: a known fight, OR a war target that needs no
        // sighting — an opponent's sanctioned starting-Citadel coordinates (the location-only Attack
        // objective, WorldAnalysis.SanctionedEnemyCitadels) or an open Attack mobilization gate.
        // Without it a player that has not met (or has wiped out) every field force held a zero
        // Aggression weight and its Attack preparation competed only for leftovers. Development's
        // need keeps the stricter HasMilitaryWitness below: a location is no measured fight.
        internal static bool HasAggressionWitness(WorldSnapshot snap) =>
            HasKnownCombatActivity(snap)
            || AttackForceReadiness.MobilizationOpen(snap?.Self)
            || WorldAnalysis.SanctionedEnemyCitadels(snap).Any();

        // A live military witness: a known fight, or an asset threat at/above the shared trigger.
        // The one gate behind "Attack/Defence created a need" for every force-building score.
        internal static bool HasMilitaryWitness(WorldSnapshot snap) =>
            HasKnownCombatActivity(snap)
            || (snap?.Threat?.Threats?.Any(t => t != null
                && t.Severity >= AiConfigV2.threatSeverityTrigger) ?? false);

        // One physical hostile force contributes once. The protected asset selects relevance and
        // diagnostics, exactly like ActiveDefenceObjectiveEvaluator; it must not clone the same
        // enemy power for every Citadel/Base/Facility lying inside its threat envelope.
        internal static float DefensiveReserveForThreats(
            IReadOnlyList<AssetThreatSnapshot> threats, bool log = false)
        {
            if (threats == null)
                return 0f;
            float reserve = 0f;
            foreach (IGrouping<int, AssetThreatSnapshot> group in threats
                .Where(t => t?.Contact?.Army != null
                    && t.Asset != null
                    && (t.Asset.Kind == AssetKind.Citadel || t.Asset.Kind == AssetKind.Base))
                .GroupBy(t => t.Contact.Army.ArmyId))
            {
                AssetThreatSnapshot best = group
                    .OrderByDescending(t => t.Severity)
                    .ThenByDescending(t => t.Asset.Value)
                    .ThenBy(t => t.EnemyEta ?? int.MaxValue)
                    .First();
                float contribution = Mathf.Max(0f,
                    best.Contact.Army.EffectiveArmyPower
                    * AiConfigV2.aggDefenceConfidenceMargin);
                reserve += contribution;
                if (log)
                {
                    string enemyLabel = $"#{best.Contact.Army.ArmyId}";
                    AiDebugLog.WriteDeduped($"reserve:{enemyLabel}:{best.Asset.Hex.Q}:{best.Asset.Hex.R}",
                        $"[AI][V2][Defence][Reserve] enemy={enemyLabel} contributes={contribution:0.##} "
                        + $"asset={best.Asset.Kind}@({best.Asset.Hex.Q},{best.Asset.Hex.R}) "
                        + $"severity={best.Severity:0.00} pairCount={group.Count()}");
                }
            }
            return reserve;
        }

        // Our best force against the known enemy players, ramped. "Haven't seen them" is not
        // "winning": without enemy intel the edge is the neutral aggRelEdgeNoIntel.
        internal static float RelativeEdge(WorldSnapshot snap)
        {
            float ownPower = Mathf.Max(snap?.Self?.FieldPower ?? 0f, snap?.Self?.BestStackPotential ?? 0f);
            float enemyPower = snap?.Known?.EnemyKnownStrength ?? 0f;
            return enemyPower < 1f
                ? AiConfigV2.aggRelEdgeNoIntel
                : Curves.Ramp(ownPower / enemyPower, AiConfigV2.aggRelEdgeRampLo, AiConfigV2.aggRelEdgeRampHi);
        }

        internal static ForceNeed JustifiedForceNeed(WorldSnapshot snap)
        {
            if (snap?.Self == null)
                return default;
            if (Cache.TryGetValue(snap, out Box cached))
                return cached.Value;
            ForceNeed need = Compute(snap);
            Cache.Add(snap, new Box { Value = need });
            return need;
        }

        private static ForceNeed Compute(WorldSnapshot snap)
        {
            using var __profile = new Game.Core.ProfileScope("AI/ForceNeed.Compute");
            bool witnessed = HasMilitaryWitness(snap);
            if (!witnessed)
                return new ForceNeed(0f, 0f, 0f, false);

            IReadOnlyList<CombatOpportunity> fights = CombatOpportunityAnalyzer.Analyze(snap).All
                ?? System.Array.Empty<CombatOpportunity>();
            int known = 0, unwinnable = 0;
            foreach (CombatOpportunity o in fights)
            {
                if (!o.HasTarget)
                    continue;
                known++;
                if (!o.IsViable)
                    unwinnable++;
            }
            AttackSiteTerms(snap, out int siteKnown, out int siteUnwinnable);
            known += siteKnown;
            unwinnable += siteUnwinnable;
            float offensive = known == 0 ? 0f : unwinnable / (float)known;
            CheapTerms(snap, out float enemy, out float defensive, out float surplus);
            return new ForceNeed(offensive, enemy, defensive, true, surplus);
        }

        // Everything JustifiedForceNeed depends on, without running the Monte Carlo behind its
        // Offensive term: that term is stood in for by its exact inputs
        // (CombatOpportunityAnalyzer.ViabilityInputsFingerprint). Equal key => equal ForceNeed.
        // For callers that only ask "did the need change" (the Development admission fingerprint).
        internal static string ChangeKey(WorldSnapshot snap)
        {
            if (snap?.Self == null)
                return "none";
            if (!HasMilitaryWitness(snap))
                return "unwitnessed";
            CheapTerms(snap, out float enemy, out float defensive, out float surplus);
            AttackSiteTerms(snap, out int siteKnown, out int siteUnwinnable);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"enemy={enemy.ToString("R", inv)}|def={defensive.ToString("R", inv)}"
                + $"|surplus={surplus.ToString("R", inv)}|sites={siteKnown}/{siteUnwinnable}"
                + $"|offensive={Fnv64(CombatOpportunityAnalyzer.ViabilityInputsFingerprint(snap)):x16}";
        }

        // 64-bit FNV-1a: keeps the (roster-long) viability inputs out of the admission log line.
        private static ulong Fnv64(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        // 2026-10-04 — the Attack side of the Offensive term (no Monte Carlo, so ChangeKey can carry
        // it exactly): every known hostile Base/Citadel with a known garrison is a known fight; it
        // is out of reach when the power that garrison needs on its own hex
        // (GroundCombatDemandPolicy.RequiredSitePower, the Attack shortage sizing) exceeds the
        // strongest stack our field can form. Location-only and unobserved-empty sites are no
        // measured fight and stay out.
        private static void AttackSiteTerms(WorldSnapshot snap, out int known, out int unwinnable)
        {
            known = 0;
            unwinnable = 0;
            PlayerSetupData observer = snap?.Observer;
            if (observer == null || snap.Known?.Buildings == null || snap.Self == null)
                return;
            float own = Mathf.Max(snap.Self.FieldPower, snap.Self.BestStackPotential);
            foreach (AiMapMemory.KnownBuilding b in snap.Known.Buildings)
            {
                if (!AttackObjectiveEvaluator.IsHostileStrategicStructure(b, observer)
                    || (snap.Self.BaseHexes != null && snap.Self.BaseHexes.Contains(b.Hex)))
                    continue;
                List<Game.Combat.WorthIt.DefendingArmy> opposition =
                    AttackObjectiveEvaluator.KnownSiteOpposition(snap, b.Hex);
                if (Game.Combat.WorthIt.UnitsOf(opposition).Count == 0)
                    continue;
                known++;
                if (own < GroundCombatDemandPolicy.RequiredSitePower(opposition,
                        AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, b.Hex)))
                    unwinnable++;
            }
        }

        // The need terms that are cheap to compute (no Monte Carlo).
        private static void CheapTerms(WorldSnapshot snap, out float enemy, out float defensive,
            out float surplus)
        {
            bool enemyIntel = (snap.Known?.EnemyKnownStrength ?? 0f) >= 1f;
            enemy = enemyIntel ? 1f - RelativeEdge(snap) : 0f;

            float threatReserve = DefensiveReserveForThreats(snap.Threat?.Threats);
            defensive = threatReserve <= AiConfigV2.allocatorSliceEpsilon ? 0f
                : Mathf.Clamp01((threatReserve - Mathf.Max(0f, snap.Self.TotalPower)) / threatReserve);
            surplus = SurplusNeed(snap);
        }

        // Dynamic force need from resources the deck leaves idle. Mean (not min) headroom: one
        // exhausted resource must not hide three piling up. Capped by forceNeedSurplusWeight so
        // an idle bank alone never outranks a fight we cannot take.
        internal static float SurplusNeed(WorldSnapshot snap)
        {
            DevelopmentReadiness rd = snap?.Development;
            if (rd == null)
                return 0f;
            float mean = ResourceBundle.All.Average(t =>
                Mathf.Clamp01(rd.InvestmentSurplusByType.Get(t)));
            return AiConfigV2.forceNeedSurplusWeight * Curves.Ramp(mean,
                AiConfigV2.forceNeedSurplusRampLo, AiConfigV2.forceNeedSurplusRampHi);
        }

        // Development alone may invest in useful upgrades without a contact. This does not
        // create military demand for Aggression or turn stock into value for an unsuitable card.
        internal static float DevelopmentNeed(WorldSnapshot snap)
        {
            float military = JustifiedForceNeed(snap).Total;
            DevelopmentReadiness rd = snap?.Development;
            float reserve = rd?.UpgradeTargetCount > 0
                ? AiConfigV2.forceNeedSurplusWeight * Curves.Ramp(rd.SurplusFraction,
                    AiConfigV2.devSurplusRampLo, AiConfigV2.devSurplusRampHi) : 0f;
            return Mathf.Max(military, reserve);
        }
    }
}
