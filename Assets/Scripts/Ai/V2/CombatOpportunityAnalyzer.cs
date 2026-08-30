using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  COMBAT OPPORTUNITY ANALYZER  (Strategy V2 build-order step 3 — the shared estimator)
    // ===========================================================================================
    //  "ONE ESTIMATOR, MANY STAGES." The V2 design forbids a throwaway aggression-only target
    //  estimator: if Strategy scores a target one way and Provisioning validates it another, the
    //  old disease comes back (allocator funds a raid the provisioner then can't deliver). This
    //  module is the single place "is there a target worth attacking, and could we take it" is
    //  answered. Its consumers, in build order:
    //    - AggressionEvaluator  (step 3, NOW) — Best.OpportunityScore drives the `opportunity`
    //                                           sub-term of the raidOpportunity driver.
    //    - Raid planner          (step 9)      — Best target -> a MissionProposal.
    //    - MissionRequirements   (step 4/9)    — the roster the projection needed -> Min/Desired.
    //    - ProvisioningManager   (step 6/9)    — feasibility validation, same CanDamageAll/WinChance.
    //
    //  It ports the PRINCIPLE of RaidWeakerArmyTask.EvaluateAssemblablePlan (prove a viable force
    //  can be gathered BEFORE spending anything on the raid), not its code.
    //
    //  FIDELITY — this is the SNAPSHOT tier:
    //    * targets  : known enemy / neutral ARMY sightings only (the set that actually carries
    //                 per-unit WorthIt.DefenderProfiles). Buildings, event guards and cheat-region
    //                 contacts are deferred to the live tier (steps 6/9), where a garrison read is
    //                 available.
    //    * roster   : own on-map non-hero members + hand unit cards, top (cap-1) by profile power,
    //                 cap = best obtainable hero's CommandRating. A composition-aware profile
    //                 compose and the live AiResourcePool / CanLeaveWithoutOvercrowding gates are
    //                 the live tier's job.
    //    * cost     : BattleCostProxy = 1 - AssemblableWinChance. The real cost-of-victory
    //                 (WorthIt.Estimate survivor-HP / critical-after-win) needs a live forming
    //                 ArmyData and lands with Provisioning.
    //    * hex bonus: 0 — sightings don't carry terrain and the snapshot has no per-hex map.
    //  Every one of these becomes a same-contract overload later; CombatOpportunity's fields do
    //  not change shape.
    // ===========================================================================================

    // One ranked "could we, and is it worth it" verdict on a single known target.
    public readonly struct CombatOpportunity
    {
        public readonly bool HasTarget;
        public readonly HexCoord TargetHex;
        // Step 9 — the STABLE strategic identity of a Raid target (spec §7). The last-known hex is
        // only the target's current position; a moving army must stay the SAME objective, so
        // AggressionObjective / RaidIntent / StableMissionKey all key off this, not the hex. 0 for
        // a target with no tracked army id (should not happen for a sighting-sourced opportunity).
        public readonly int TargetArmyId;
        public readonly PlayerSetupData TargetOwner;
        public readonly bool TargetIsNeutral;
        public readonly int DefenderCount;

        public readonly float ReadyWinChance;         // strongest EXISTING single stack vs this target
        public readonly float AssemblableWinChance;   // strongest roster we could realistically gather
        public readonly bool CanCoverAllDefenders;    // WorthIt.CanDamage covers every defender
        public readonly float BattleCostProxy;        // 1 - AssemblableWinChance (see fidelity note)
        public readonly int Eta;                      // turns for our nearest usable force to arrive
        public readonly float TargetValue;            // 0..assetValueArmyCap, reuses AiConfigV2.assetValueArmy*
        public readonly float Confidence;             // knowledge-tier confidence of the sighting

        public readonly bool GatePassed;              // hero obtainable AND CanCoverAll AND win >= min
        public readonly float OpportunityScore;       // 0..1 — exactly 0 when GatePassed is false

        public CombatOpportunity(bool hasTarget, HexCoord targetHex, int targetArmyId, PlayerSetupData targetOwner, bool targetIsNeutral,
            int defenderCount, float readyWinChance, float assemblableWinChance, bool canCoverAll, float battleCostProxy,
            int eta, float targetValue, float confidence, bool gatePassed, float opportunityScore)
        {
            HasTarget = hasTarget;
            TargetHex = targetHex;
            TargetArmyId = targetArmyId;
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
            new CombatOpportunity(false, default, 0, null, false, 0, 0f, 0f, false, 0f, 0, 0f, 0f, false, 0f);
    }

    public sealed class CombatOpportunityReport
    {
        public IReadOnlyList<CombatOpportunity> All = System.Array.Empty<CombatOpportunity>();
        public CombatOpportunity Best = CombatOpportunity.None;
        public bool HeroAvailable;   // was any hero obtainable for a fresh raid at all
        public int AssemblableCap;   // roster slot cap the projection used
    }

    public static class CombatOpportunityAnalyzer
    {
        // Game-rule mirror of ArmyData's private no-hero BaseCapacity (same value WorldAnalysis uses).
        private const int NoHeroStackCapacity = 2;

        public static CombatOpportunityReport Analyze(WorldSnapshot snap)
        {
            var report = new CombatOpportunityReport();
            if (snap?.Self == null || snap.Known == null)
                return report;

            // ---- our two attacking rosters, as real per-unit profiles --------------------
            var ownBodies = new List<WorthIt.DefenderProfile>();
            int heroCap = 0;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || a.IsPrison) continue;
                if (a.Members != null) ownBodies.AddRange(a.Members);      // Members is non-hero already
                if (a.HeroCommandRating > heroCap) heroCap = a.HeroCommandRating;
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
            report.HeroAvailable = heroAvailable;
            report.AssemblableCap = cap;

            ArmySnapshot bestReadyArmy = snap.Self.Armies
                .Where(a => a != null && !a.IsPrison && a.MemberCount > 0 && a.Members != null && a.Members.Count > 0)
                .OrderByDescending(a => a.EffectiveArmyPower)
                .FirstOrDefault();
            List<WorthIt.DefenderProfile> readyRoster = bestReadyArmy?.Members?.ToList()
                ?? new List<WorthIt.DefenderProfile>();

            List<WorthIt.DefenderProfile> assemblableRoster = ownBodies
                .Concat(handBodies)
                .OrderByDescending(ProfilePower)
                .Take(Mathf.Max(0, cap - 1))
                .ToList();

            // ---- ETA basis: our nearest usable force / a move budget --------------------
            var fromHexes = new List<HexCoord>();
            int moverBudget = AiConfigV2.etaFallbackMoveBudget;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || a.IsPrison || a.MemberCount == 0) continue;
                fromHexes.Add(a.Hex);
                if (a.MaxMovement > moverBudget) moverBudget = a.MaxMovement;
            }
            if (snap.Self.BaseHexes != null) fromHexes.AddRange(snap.Self.BaseHexes);

            // ---- score every candidate target ------------------------------------------
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
                bool cover = ProfilesCoverAll(assemblableRoster, defenders, 0f);

                int minDist = fromHexes.Count > 0 ? fromHexes.Min(h => HexGridMath.Distance(h, t.Hex)) : 99;
                int eta = CeilDiv(minDist, moverBudget);

                float targetValue = Mathf.Min(AiConfigV2.assetValueArmyCap,
                    AiPower.EffectiveArmyPowerFromProfiles(defenders) / AiConfigV2.assetValueArmyPowerDivisor);
                float confidence = ConfidenceFor(snap, t.Hex);

                bool gate = heroAvailable && cover && asmWin >= AiConfigV2.opportunityMinViableWinChance;
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
                    targetArmyId: t.ArmyId,
                    targetOwner: t.Owner,
                    targetIsNeutral: t.Owner != null && t.Owner.IsNeutral,
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

            report.All = all;
            report.Best = all.Count > 0
                ? all.OrderByDescending(o => o.OpportunityScore).ThenByDescending(o => o.AssemblableWinChance).First()
                : CombatOpportunity.None;
            return report;
        }

        // Same weighted stat line AiPower uses, restricted to what a DefenderProfile carries (no
        // resistance / ability multiplier on a fog-read roster) — enough to RANK bodies for the
        // greedy roster pick.
        private static float ProfilePower(WorthIt.DefenderProfile p) => Mathf.Max(0f,
            p.Attack * AiConfigV2.powerAttackWeight
            + p.Defense * AiConfigV2.powerDefenseWeight
            + p.HitPoints * AiConfigV2.powerHitPointsWeight
            + p.Initiative * AiConfigV2.powerInitiativeWeight);

        // Profile-vs-profile coverage — WorthIt.CanDamageAll only accepts UnitData/ArmyData, and a
        // projected roster has neither. Same rule (every defender needs one attacker that can dent
        // it), same WorthIt.CanDamage primitive. Mirrors WorldAnalysis.ProfilesCanDamageAll.
        private static bool ProfilesCoverAll(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float extraDefense)
        {
            if (defenders == null || defenders.Count == 0) return true;
            if (attackers == null || attackers.Count == 0) return false;
            foreach (WorthIt.DefenderProfile def in defenders)
            {
                bool covered = false;
                foreach (WorthIt.DefenderProfile atk in attackers)
                    if (WorthIt.CanDamage(atk.Attack, def, extraDefense)) { covered = true; break; }
                if (!covered) return false;
            }
            return true;
        }

        private static float ConfidenceFor(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts != null)
                foreach (EnemyContactSnapshot c in contacts)
                    if (c.Position.HasValue && c.Position.Value.Equals(hex))
                        return c.Confidence;
            return AiConfigV2.threatConfidenceLastKnown;
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
