using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using UnityEngine;
using Game.Combat;

namespace Game.Ai.V2
{
    // Shared snapshot-tier estimator for known army and guarded-event combat opportunities.
    // The same target facts are consumed by radar, objective admission and Raid provisioning.
    public readonly struct CombatOpportunity
    {
        public readonly bool HasTarget;
        public readonly HexCoord TargetHex;
        public readonly RaidTargetRef Target;
        public int TargetArmyId => Target.Kind == RaidTargetKind.NeutralArmy ? Target.ArmyId : 0;
        public readonly PlayerSetupData TargetOwner;
        public readonly bool TargetIsNeutral;
        public readonly int DefenderCount;
        public readonly float ReadyWinChance;
        public readonly float AssemblableWinChance;
        public readonly bool CanCoverAllDefenders;
        public readonly float BattleCostProxy;
        public readonly int Eta;
        public readonly float TargetValue;
        public readonly float Confidence;
        public readonly bool GatePassed;
        public readonly float OpportunityScore;

        public CombatOpportunity(bool hasTarget, HexCoord targetHex, RaidTargetRef target, PlayerSetupData targetOwner, bool targetIsNeutral,
            int defenderCount, float readyWinChance, float assemblableWinChance, bool canCoverAll, float battleCostProxy,
            int eta, float targetValue, float confidence, bool gatePassed, float opportunityScore)
        {
            HasTarget = hasTarget;
            TargetHex = targetHex;
            Target = target;
            TargetOwner = targetOwner;
            TargetIsNeutral = targetIsNeutral;
            DefenderCount = defenderCount;
            ReadyWinChance = readyWinChance;
            AssemblableWinChance = assemblableWinChance;
            CanCoverAllDefenders = canCoverAll;
            BattleCostProxy = battleCostProxy;
            Eta = eta;
            TargetValue = targetValue;
            Confidence = confidence;
            GatePassed = gatePassed;
            OpportunityScore = opportunityScore;
        }

        public static CombatOpportunity None =>
            new CombatOpportunity(false, default, RaidTargetRef.None, null, false, 0, 0f, 0f, false, 0f, 0, 0f, 0f, false, 0f);
    }

    public sealed class CombatOpportunityReport
    {
        public IReadOnlyList<CombatOpportunity> All = System.Array.Empty<CombatOpportunity>();
        public CombatOpportunity Best = CombatOpportunity.None;
        public IReadOnlyList<CombatOpportunity> NeutralOpportunities = System.Array.Empty<CombatOpportunity>();
        public CombatOpportunity BestNeutralOpportunity = CombatOpportunity.None;
        public bool HeroAvailable;
        public int AssemblableCap;
    }

    public static class CombatOpportunityAnalyzer
    {
        private const int NoHeroStackCapacity = 2;

        public static CombatOpportunityReport Analyze(WorldSnapshot snap)
        {
            var report = new CombatOpportunityReport();
            if (snap?.Self == null || snap.Known == null)
                return report;

            var ownBodies = new List<WorthIt.DefenderProfile>();
            int heroCap = 0;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || a.IsPrison) continue;
                if (a.Members != null) ownBodies.AddRange(a.Members);
                if (a.BestHeroCommandRating > heroCap) heroCap = a.BestHeroCommandRating;
            }

            var handBodies = new List<WorthIt.DefenderProfile>();
            foreach (CardData card in snap.Self.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
            {
                CardDefinition d = card?.Definition;
                if (d == null) continue;
                if (d.cardType == CardType.Hero && d.commandRating > heroCap) heroCap = d.commandRating;
                if (d.cardType == CardType.Unit) handBodies.Add(AiPower.ToDefenderProfile(d));
            }

            bool heroAvailable = heroCap > 0;
            int cap = heroAvailable ? heroCap : NoHeroStackCapacity;
            int bodySlots = heroAvailable ? Mathf.Max(0, cap - 1) : Mathf.Max(0, cap);
            report.HeroAvailable = heroAvailable;
            report.AssemblableCap = cap;

            ArmySnapshot bestReadyArmy = snap.Self.Armies
                .Where(a => a.IsStructuralRaidActor && a.Members != null && a.Members.Count > 0)
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .FirstOrDefault();
            List<WorthIt.DefenderProfile> readyRoster = bestReadyArmy?.Members?.ToList()
                ?? new List<WorthIt.DefenderProfile>();

            List<WorthIt.DefenderProfile> assemblableRoster = ownBodies.Concat(handBodies)
                .OrderByDescending(ProfilePower)
                .Take(bodySlots)
                .ToList();

            var fromHexes = new List<HexCoord>();
            int moverBudget = AiConfigV2.etaFallbackMoveBudget;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (!a.IsStructuralRaidActor) continue;
                fromHexes.Add(a.Hex);
                if (a.MaxMovement > moverBudget) moverBudget = a.MaxMovement;
            }
            if (snap.Self.BaseHexes != null) fromHexes.AddRange(snap.Self.BaseHexes);

            var candidates = new List<AiMapMemory.KnownEnemySighting>();
            if (snap.Known.EnemySightings != null) candidates.AddRange(snap.Known.EnemySightings);
            if (snap.Known.NeutralSightings != null) candidates.AddRange(snap.Known.NeutralSightings);

            var all = new List<CombatOpportunity>(candidates.Count);
            foreach (AiMapMemory.KnownEnemySighting t in candidates)
            {
                IReadOnlyList<WorthIt.DefenderProfile> defenders = t.Defenders
                    ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
                float readyWin = WorthIt.WinChance(readyRoster, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
                float asmWin = WorthIt.WinChance(assemblableRoster, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
                bool cover = WorthIt.CanDamageAll(assemblableRoster, defenders, 0f);
                int minDist = fromHexes.Count > 0 ? fromHexes.Min(h => HexGridMath.Distance(h, t.Hex)) : 99;
                int eta = CeilDiv(minDist, moverBudget);
                float targetValue = Mathf.Min(AiConfigV2.assetValueArmyCap,
                    AiPower.EffectiveArmyPowerFromProfiles(defenders) / AiConfigV2.assetValueArmyPowerDivisor);
                float confidence = ConfidenceForSighting(snap, t);
                bool gate = cover && asmWin >= AiConfigV2.opportunityMinViableWinChance;
                float score = 0f;
                if (gate)
                {
                    float effValue = Mathf.Max(targetValue, AiConfigV2.opportunityBeatableValueFloor);
                    float valueTerm = Mathf.Clamp01(effValue / Mathf.Max(0.0001f, AiConfigV2.opportunityValueNorm));
                    float etaTerm = 1f / (1f + AiConfigV2.opportunityEtaWeight * Mathf.Max(0, eta));
                    float costTerm = Mathf.Clamp01(1f - AiConfigV2.opportunityCostWeight * (1f - asmWin));
                    float raw = asmWin * valueTerm * etaTerm * costTerm * confidence;
                    score = Mathf.Clamp01(raw / Mathf.Max(0.0001f, AiConfigV2.opportunityScoreNorm));
                }

                all.Add(new CombatOpportunity(
                    hasTarget: true,
                    targetHex: t.Hex,
                    target: RaidTargetRef.ForNeutralArmy(t.ArmyId),
                    targetOwner: t.Owner,
                    targetIsNeutral: RaidObjectiveEvaluator.IsNeutralRaidTarget(t.Owner),
                    defenderCount: defenders.Count,
                    readyWinChance: readyWin,
                    assemblableWinChance: asmWin,
                    canCoverAll: cover,
                    battleCostProxy: 1f - asmWin,
                    eta: eta,
                    targetValue: targetValue,
                    confidence: confidence,
                    gatePassed: gate,
                    opportunityScore: score));
            }

            if (snap.Known.EventGuards != null)
            {
                foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                {
                    IReadOnlyList<WorthIt.DefenderProfile> defenders = g.Defenders
                        ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
                    float readyWin = WorthIt.WinChance(readyRoster, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
                    float asmWin = WorthIt.WinChance(assemblableRoster, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
                    bool cover = WorthIt.CanDamageAll(assemblableRoster, defenders, 0f);
                    int minDist = fromHexes.Count > 0 ? fromHexes.Min(h => HexGridMath.Distance(h, g.Hex)) : 99;
                    int eta = CeilDiv(minDist, moverBudget);
                    float targetValue = Mathf.Min(AiConfigV2.assetValueArmyCap,
                        AiPower.EffectiveArmyPowerFromProfiles(defenders) / AiConfigV2.assetValueArmyPowerDivisor);
                    // A remembered event guard is a fixed, card-defined encounter, not a moving
                    // enemy-army contact. Its observed defender profile cannot become less reliable
                    // merely because ThreatModel (correctly) contains no enemy army at this hex.
                    // Event existence is revalidated by the existing Raid lifecycle/execution owners.
                    float confidence = AiConfigV2.threatConfidenceExact;
                    bool gate = cover && asmWin >= AiConfigV2.opportunityMinViableWinChance;
                    float score = 0f;
                    if (gate)
                    {
                        float effValue = Mathf.Max(targetValue, AiConfigV2.opportunityBeatableValueFloor);
                        float valueTerm = Mathf.Clamp01(effValue / Mathf.Max(0.0001f, AiConfigV2.opportunityValueNorm));
                        float etaTerm = 1f / (1f + AiConfigV2.opportunityEtaWeight * Mathf.Max(0, eta));
                        float costTerm = Mathf.Clamp01(1f - AiConfigV2.opportunityCostWeight * (1f - asmWin));
                        float raw = asmWin * valueTerm * etaTerm * costTerm * confidence;
                        score = Mathf.Clamp01(raw / Mathf.Max(0.0001f, AiConfigV2.opportunityScoreNorm));
                    }

                    all.Add(new CombatOpportunity(
                        hasTarget: true,
                        targetHex: g.Hex,
                        target: RaidTargetRef.ForEventGuard(g.Hex),
                        targetOwner: null,
                        targetIsNeutral: true,
                        defenderCount: defenders.Count,
                        readyWinChance: readyWin,
                        assemblableWinChance: asmWin,
                        canCoverAll: cover,
                        battleCostProxy: 1f - asmWin,
                        eta: eta,
                        targetValue: targetValue,
                        confidence: confidence,
                        gatePassed: gate,
                        opportunityScore: score));
                }
            }

            report.All = all;
            report.Best = all.Count > 0
                ? all.OrderByDescending(o => o.OpportunityScore).ThenByDescending(o => o.AssemblableWinChance).First()
                : CombatOpportunity.None;
            List<CombatOpportunity> neutrals = all.Where(o => o.TargetIsNeutral).ToList();
            report.NeutralOpportunities = neutrals;
            report.BestNeutralOpportunity = neutrals.Count > 0
                ? neutrals.OrderByDescending(o => o.OpportunityScore)
                    .ThenByDescending(o => o.AssemblableWinChance).First()
                : CombatOpportunity.None;
            return report;
        }

        private static float ProfilePower(WorthIt.DefenderProfile p) => Mathf.Max(0f,
            p.Attack * AiConfigV2.powerAttackWeight
            + p.Defense * AiConfigV2.powerDefenseWeight
            + p.HitPoints * AiConfigV2.powerHitPointsWeight
            + p.Initiative * AiConfigV2.powerInitiativeWeight);

        // A neutral sighting has its own honest last-observed turn. It is not part of the enemy
        // threat-contact model; using that model's missing-contact fallback used to mark even a
        // just-discovered neutral as stale and silently reject viable Raid objectives.
        // Keep the established threat-contact confidence for ordinary enemy armies unchanged.
        private static float ConfidenceForSighting(WorldSnapshot snap, AiMapMemory.KnownEnemySighting sighting)
        {
            if (RaidObjectiveEvaluator.IsNeutralRaidTarget(sighting.Owner))
                return sighting.SeenTurn == snap.TurnNumber
                    ? AiConfigV2.threatConfidenceExact : AiConfigV2.threatConfidenceLastKnown;
            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts != null)
                foreach (EnemyContactSnapshot c in contacts)
                    if (c.Position.HasValue && c.Position.Value.Equals(sighting.Hex))
                        return c.Confidence;
            return AiConfigV2.threatConfidenceLastKnown;
        }

        private static int CeilDiv(int a, int b) => AiV2Util.CeilDiv(a, b);
    }
}
