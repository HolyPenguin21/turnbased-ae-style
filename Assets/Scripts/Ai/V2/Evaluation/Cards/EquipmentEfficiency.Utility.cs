using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SIGNED UTILITY U of an attachment on one host  (card-score units, three-owner-turn horizon)
    // ===========================================================================================
    //      U = 1.10 dC + U_AP + U_move + U_vision + U_detection + U_stealth + U_fate + U_AA
    //  Every component is a signed before/after difference of the SAME projected host in the SAME
    //  context; nothing is clamped or abs'd. Costs, Challenge chance and repeat damping belong to the
    //  callers (GenerationStep / ActionPrice / DevelopmentDiversity) and never enter here.
    //  Combat rules are not re-implemented: damage, lethality and ability modifiers come from
    //  BattleSimulationKernel.ExpectedExchangeDamage (exact, deterministic, no RNG, no Fate spend).
    //  Aviation hosts keep the legacy linear table (Delta) as an explicitly labelled proxy.
    // ===========================================================================================
    internal readonly struct UtilityBreakdown
    {
        public readonly float DeltaC, Combat, OffenseCombat, DefenseCombat, Ap, Move, Vision, Detection,
            Stealth, Fate, AntiAir;
        public float Total => Combat + Ap + Move + Vision + Detection + Stealth + Fate;
        public float Tactical => Total - Combat;

        public UtilityBreakdown(float deltaC, float offense, float defense, float ap, float move, float vision,
            float detection, float stealth, float fate, float antiAir)
        {
            DeltaC = deltaC; OffenseCombat = offense; DefenseCombat = defense; Combat = offense + defense + antiAir;
            Ap = ap; Move = move; Vision = vision; Detection = detection; Stealth = stealth; Fate = fate;
            AntiAir = antiAir;
        }

        public override string ToString() =>
            $"U={Total:0.###} (dC {DeltaC:0.###} combat {Combat:0.###} ap {Ap:0.###} move {Move:0.###} "
            + $"vision {Vision:0.###} detect {Detection:0.###} stealth {Stealth:0.###} fate {Fate:0.###} aa {AntiAir:0.###})";
    }

    internal static partial class EquipmentEfficiency
    {
        // Signed card score of an AP-equivalent amount (ActionPrice.ToCardScore itself clamps at 0).
        internal static float SignedCard(float apEquivalents) =>
            apEquivalents < 0f ? -ActionPrice.ToCardScore(-apEquivalents) : ActionPrice.ToCardScore(apEquivalents);

        internal static UtilityBreakdown Utility(EfficiencyStats before, IReadOnlyCollection<string> beforeAbilities,
            EfficiencyStats after, IReadOnlyCollection<string> afterAbilities, EfficiencyContext ctx)
        {
            ctx = ctx ?? new EfficiencyContext();
            beforeAbilities = beforeAbilities ?? Array.Empty<string>();
            afterAbilities = afterAbilities ?? Array.Empty<string>();

            // ---- combat ---------------------------------------------------------------------------
            float dC = 0f, offense = 0f, defense = 0f;
            if (!ctx.IsHero)
            {
                IReadOnlyList<WorthIt.DefenderProfile> ground = GroundTargets(ctx);
                float cBefore = Contribution(before, beforeAbilities, ctx, ground);
                float cAfter = Contribution(after, afterAbilities, ctx, ground);
                dC = cAfter - cBefore;
                float scale = AiConfigV2.equipCombatCardScale;
                if (Mathf.Approximately(ctx.OffenseMult, ctx.DefenseMult))
                {
                    offense = scale * dC * ctx.OffenseMult;
                }
                else
                {
                    // Mission multipliers act on the offensive and the defensive share separately:
                    // offense = what the new Attack/Range/Initiative/abilities change at the OLD defence.
                    var offensiveOnly = new EfficiencyStats(after.Attack, before.Defense, before.HitPoints,
                        after.Range, before.Move, after.Initiative, before.ActivationAp, before.Fate);
                    // Defensive abilities (Regeneration, CeramicArmor) belong to the defensive share.
                    var midAbilities = afterAbilities.Where(a => !IsDefensiveAbility(a))
                        .Concat(beforeAbilities.Where(IsDefensiveAbility)).ToList();
                    float cMid = Contribution(offensiveOnly, midAbilities, ctx, ground);
                    offense = scale * (cMid - cBefore) * ctx.OffenseMult;
                    defense = scale * (cAfter - cMid) * ctx.DefenseMult;
                }
            }

            // ---- activation AP and speed ------------------------------------------------------------
            int apBefore = EffectiveAp(before, beforeAbilities), apAfter = EffectiveAp(after, afterAbilities);
            int vBefore = Mathf.Min(before.Move, ctx.OtherSpeedMin), vAfter = Mathf.Min(after.Move, ctx.OtherSpeedMin);
            float ap, move;
            if (ctx.RouteLength > 0)
            {
                // A known route: ONE change of the army's total cost of walking it. The army pays the SUM of its
                // members' activation AP once per step; the speed share and the per-step AP share are split so
                // that Move + (route part of) AP equals exactly (O+apB)*stepsB - (O+apA)*stepsA.
                int stepsBefore = Mathf.CeilToInt(ctx.RouteLength / (float)Mathf.Max(1, vBefore));
                int stepsAfter = Mathf.CeilToInt(ctx.RouteLength / (float)Mathf.Max(1, vAfter));
                float others = Mathf.Max(0f, ctx.OtherArmyActivationAp);
                move = SignedCard((others + apBefore) * (stepsBefore - stepsAfter));
                // The rest of the expected load (beyond the route's own activations) is the usual AP saving.
                float remaining = Mathf.Max(0f, ctx.ExpectedActivations - stepsBefore);
                ap = SignedCard(stepsAfter * (apBefore - apAfter) + remaining * (apBefore - apAfter));
            }
            else
            {
                float eRef = ctx.IsHero
                    ? Base(Mathf.RoundToInt(ctx.ArmyAttack), before.Defense, before.HitPoints, 2) : Base(before);
                // Proxy: equipCardValuePerE (card per E) x equipMoveFactor (move share of E), NOT a new AP price.
                move = AiConfigV2.equipCardValuePerE * AiConfigV2.equipMoveFactor * eRef * (vAfter - vBefore)
                    * (3f / Mathf.Max(1, vBefore));
                ap = SignedCard(ctx.ExpectedActivations * (apBefore - apAfter));
            }

            // ---- vision / detection (the host's own army saturates at its local maximum) --------------
            int radiusBefore = Mathf.Max(ctx.OtherRecceRadius, AbilityParams.GetBestRecceRadius(beforeAbilities));
            int radiusAfter = Mathf.Max(ctx.OtherRecceRadius, AbilityParams.GetBestRecceRadius(afterAbilities));
            float vision = ctx.IncludeStealthTrait
                ? AiConfigV2.equipVisionWeight * Mathf.Clamp01(ctx.UsefulDarkFraction) * (radiusAfter - radiusBefore) : 0f;
            float detection = 0f;
            if (ctx.IncludeStealthTrait && ctx.DetectionRelevance > 0f)
            {
                int spotBefore = Mathf.Max(ctx.OtherSpotStrength, AbilityParams.GetBestRecceSpotStrength(beforeAbilities));
                int spotAfter = Mathf.Max(ctx.OtherSpotStrength, AbilityParams.GetBestRecceSpotStrength(afterAbilities));
                bool hadRadius = radiusBefore > 0, hasRadius = radiusAfter > 0;
                detection = AiConfigV2.equipDetectionWeight * ctx.DetectionRelevance
                    * ((hasRadius ? DetectProbability(spotAfter, ctx.HideStrength) : 0f)
                        - (hadRadius ? DetectProbability(spotBefore, ctx.HideStrength) : 0f));
            }

            // ---- stealth: an option to move unseen, priced once through the entry AP ------------------
            float stealth = ctx.IncludeStealthTrait
                ? StealthOption(afterAbilities, ctx) - StealthOption(beforeAbilities, ctx) : 0f;
            stealth *= ctx.SkillMult;
            vision *= ctx.SkillMult;
            detection *= ctx.SkillMult;

            // ---- Fate -------------------------------------------------------------------------------
            float fate = 0f;
            float dFate = after.Fate - before.Fate;
            if (ctx.IsHero)
                // Commander Fate keeps its owner weight (mean army Attack per Fate point), in card units.
                fate += dFate * AiConfigV2.equipHeroFateFactor * ctx.ArmyAttack * AiConfigV2.equipCardValuePerE;
            bool operatorHost = ctx.IsHero && (beforeAbilities.Contains(UnitAbilities.Researcher)
                || beforeAbilities.Contains(UnitAbilities.Assembler));
            if (ctx.OperatorOutputs != null && ctx.OperatorOutputs.Count > 0)
                foreach (var o in ctx.OperatorOutputs)
                    fate += (o.PAfter - o.PBefore) * Mathf.Max(0f, o.Utility - o.CostIfSuccess);
            else if (operatorHost)
                fate += dFate * AiConfigV2.equipOperatorFateValue;   // owner-set value per Fate point

            // ---- anti-air reactions ---------------------------------------------------------------
            float antiAir = ctx.IsHero ? 0f
                : AiConfigV2.equipCombatCardScale
                    * (ReactionValue(after, afterAbilities, ctx) - ReactionValue(before, beforeAbilities, ctx));

            return new UtilityBreakdown(dC, offense, defense, ap, move, vision, detection, stealth, fate, antiAir);
        }

        private static bool IsDefensiveAbility(string a) =>
            a == UnitAbilities.Regeneration || a == UnitAbilities.CeramicArmor;

        // An aviation host strikes once per sortie, on one random living ground defender, without return fire
        // (AviationCombatEstimator's shape). Each attack is one armed exchange of the shared kernel, so the
        // expectation is read from the kernel directly: analytic, deterministic, and the SAME for the before and
        // after state (the estimator's seeded 25-trial Monte Carlo would put different noise in each).
        // Sorties in the horizon = ExpectedActivations (proxy). Defence/HP/speed of aircraft stay on the
        // legacy table (AA fire is not modelled by the estimator either).
        internal static float AviationOffenseDelta(EfficiencyStats before, IReadOnlyCollection<string> beforeAbilities,
            EfficiencyStats after, IReadOnlyCollection<string> afterAbilities, EfficiencyContext ctx)
        {
            IReadOnlyList<WorthIt.DefenderProfile> ground = GroundTargets(ctx);
            return AiConfigV2.equipCombatCardScale * (SortieValue(after, afterAbilities, ground, ctx)
                - SortieValue(before, beforeAbilities, ground, ctx));
        }

        private static float SortieValue(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            IReadOnlyList<WorthIt.DefenderProfile> targets, EfficiencyContext ctx)
        {
            float sum = 0f;
            foreach (var t in targets)
            {
                int hp = Mathf.Max(1, Mathf.CeilToInt(t.HitPoints));
                sum += BattleSimulationKernel.ExpectedExchangeDamage(s.Attack, Mathf.RoundToInt(t.Defense),
                    abilities, t.TypeTags, t.Abilities ?? Array.Empty<string>(), hp, out _) / hp;
            }
            return AiConfigV2.equipCombatBodyScale * ctx.ExpectedActivations * sum / targets.Count;
        }

        // RapidReaction makes an activation free: the effective AP is what a turn really pays.
        private static int EffectiveAp(EfficiencyStats s, IReadOnlyCollection<string> abilities) =>
            abilities.Contains(UnitAbilities.RapidReaction) ? 0 : Mathf.Max(0, s.ActivationAp);

        // P(spot successes > hide successes) for fair dice pools.
        internal static float DetectProbability(int spot, int hide)
        {
            spot = Mathf.Max(0, spot); hide = Mathf.Max(0, hide);
            if (spot == 0) return 0f;
            double p = 0;
            for (int s = 0; s <= spot; s++)
                for (int h = 0; h < s && h <= hide; h++)
                    p += Binomial(spot, s) * Binomial(hide, h);
            return (float)(p / Math.Pow(2, spot + hide));
        }

        private static double Binomial(int n, int k)
        {
            double c = 1;
            for (int i = 1; i <= k; i++) c = c * (n - k + i) / i;
            return c;
        }

        // option = use x max(0, 0.9 x max(0.35, risk) - price of entering Stealth)
        private static float StealthOption(IReadOnlyCollection<string> abilities, EfficiencyContext ctx)
        {
            if (!AbilityParams.AbilitiesHaveAnyStealth(abilities))
                return 0f;
            float use = ctx.StealthUsable ? AiConfigV2.equipStealthUseScout : AiConfigV2.equipStealthUseReserve;
            float entry = ActionPrice.ToCardScore(AiConfigV2.equipStealthEntryAp);
            return use * Mathf.Max(0f, AiConfigV2.equipStealthRiskWeight
                * Mathf.Max(AiConfigV2.equipStealthRiskFloor, ctx.StealthRisk) - entry);
        }

        // ---- combat model -------------------------------------------------------------------------------
        private static IReadOnlyList<WorthIt.DefenderProfile> GroundTargets(EfficiencyContext ctx)
        {
            var list = new List<WorthIt.DefenderProfile>();
            if (ctx.Targets != null)
                foreach (var t in ctx.Targets)
                    if (!t.IsHero && t.HitPoints > 0 && !(t.TypeTags?.Contains(UnitTypeTag.Aircraft) ?? false))
                        list.Add(t);
            if (list.Count == 0)
                list.Add(new WorthIt.DefenderProfile(AiConfigV2.equipPriorDefense, false, null,
                    AiConfigV2.equipPriorAttack, AiConfigV2.equipPriorHitPoints,
                    AiConfigV2.equipPriorInitiative));
            if (list.Count <= AiConfigV2.equipEvalTargetCap)
                return list;
            var sampled = new List<WorthIt.DefenderProfile>(AiConfigV2.equipEvalTargetCap);
            for (int i = 0; i < AiConfigV2.equipEvalTargetCap; i++)
                sampled.Add(list[(int)((long)i * list.Count / AiConfigV2.equipEvalTargetCap)]);
            return sampled;
        }

        // P(a target at the contact distance is inside the host's Range).
        private static float Reach(int range, EfficiencyContext ctx)
        {
            if (ctx.KnownDistance.HasValue)
                return range >= ctx.KnownDistance.Value ? 1f : 0f;
            return (range >= 1 ? AiConfigV2.equipDistanceShare1 : 0f) + (range >= 2 ? AiConfigV2.equipDistanceShare2 : 0f)
                + (range >= 3 ? AiConfigV2.equipDistanceShare3 : 0f) + (range >= 4 ? AiConfigV2.equipDistanceShare4 : 0f);
        }

        // Contributions are pure in (host state, context inputs, target list): one host's "before" state is
        // read once per product, so memoise per target-list instance. The table lives exactly as long as
        // the list (ConditionalWeakTable), so nothing outlives the snapshot / purpose scope that built it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, float>>
            s_contributions = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, float>>();

        private static string ContributionKey(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx)
        {
            var tags = ctx.HostTags != null ? string.Join(",", ctx.HostTags) : string.Empty;
            return $"{s.Attack}|{s.Defense}|{s.HitPoints}|{s.Range}|{s.Initiative}|{ctx.HpSpent}|{ctx.KnownDistance}|"
                + $"{ctx.HostCommanderInitiative}|{ctx.EnemyCommanderInitiative}|{ctx.SecondaryNeighbors}|{tags}|"
                + string.Join(",", abilities);
        }

        // Mean over the enemy profiles of the unscaled contribution, times the body scale.
        // Mean-field over contacts: expected wound carries, P(alive) multiplies every later contact.
        private static float Contribution(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, IReadOnlyList<WorthIt.DefenderProfile> targets)
        {
            Dictionary<string, float> memo = null;
            string key = null;
            object owner = ctx.Targets;
            if (owner != null)
            {
                memo = s_contributions.GetValue(owner, _ => new Dictionary<string, float>());
                key = ContributionKey(s, abilities, ctx);
                lock (memo)
                    if (memo.TryGetValue(key, out float cached))
                        return cached;
            }
            float sum = 0f;
            foreach (var t in targets)
                sum += ContactSeries(s, abilities, ctx, t);
            float value = AiConfigV2.equipCombatBodyScale * sum / targets.Count;
            if (memo != null)
                lock (memo)
                    memo[key] = value;
            return value;
        }

        private static float ContactSeries(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, WorthIt.DefenderProfile t)
        {
            AbilityMagnitudes mags = AbilityMagnitudes.Default;
            IReadOnlyCollection<UnitTypeTag> hostTags = ctx.HostTags ?? Array.Empty<UnitTypeTag>();
            IReadOnlyList<string> targetAbilities = t.Abilities ?? Array.Empty<string>();
            int targetHp = Mathf.Max(1, Mathf.CeilToInt(t.HitPoints));
            float reach = Reach(s.Range, ctx);
            bool berserk = abilities.Contains(UnitAbilities.Berserk);
            bool shock = abilities.Contains(UnitAbilities.ShockAttack);
            bool targetShock = targetAbilities.Contains(UnitAbilities.ShockAttack);
            bool regeneration = abilities.Contains(UnitAbilities.Regeneration);

            // Host strike on a fresh enemy (what it deals as % of the target, how likely it kills / hits).
            float Strike(int attack, out float kill, out float hit)
            {
                float expected = BattleSimulationKernel.ExpectedExchangeDamage(attack, t.Defense < 0 ? 0 : Mathf.RoundToInt(t.Defense),
                    abilities, t.TypeTags, targetAbilities, targetHp, out hit);
                float below = targetHp > 1
                    ? BattleSimulationKernel.ExpectedExchangeDamage(attack, Mathf.RoundToInt(t.Defense),
                        abilities, t.TypeTags, targetAbilities, targetHp - 1, out _)
                    : 0f;
                kill = Mathf.Clamp01(expected - below);
                float damage = expected;
                int neighbours = Mathf.Max(0, Mathf.RoundToInt(ctx.SecondaryNeighbors));
                if (neighbours > 0)
                {
                    int splash = abilities.Contains(UnitAbilities.Splash) ? Mathf.Min(2, neighbours) : 0;
                    int scorch = abilities.Contains(UnitAbilities.Scorcher)
                        && (t.TypeTags?.Contains(UnitTypeTag.Bio) ?? false)
                        ? Mathf.Clamp(neighbours - splash, 0, 1) : 0;
                    int secondary = splash + scorch;
                    if (secondary > 0)
                    {
                        float side = BattleSimulationKernel.ExpectedExchangeDamage(attack, Mathf.RoundToInt(t.Defense),
                            abilities, t.TypeTags, targetAbilities, targetHp, out _, true, targetAbilities);
                        damage += secondary * side;
                    }
                }
                hit *= reach;
                kill *= reach;
                return damage * reach / targetHp;
            }

            float cumulativeWound = Mathf.Max(0, ctx.HpSpent);
            float alive = 1f, total = 0f;
            int hostInitiative = s.Initiative + ctx.HostCommanderInitiative;
            int targetInitiative = t.Initiative + ctx.EnemyCommanderInitiative;
            float hostFirst = hostInitiative > targetInitiative ? 1f : hostInitiative == targetInitiative ? 0.5f : 0f;

            // The host's own strike does not depend on the contact: price it once.
            int attack = s.Attack, defense = s.Defense;
            float dealtA = Strike(attack, out float killA, out float hitA);          // host acts first
            float boosted = berserk ? Strike(attack + mags.BerserkAttackGain, out _, out _) : dealtA;
            int lastHp = -1;
            float tExpected = 0f, tHit = 0f, hostDies = 0f;

            for (int contact = 0; contact < AiConfigV2.equipContactCount; contact++)
            {
                int hostHp = Mathf.Max(1, s.HitPoints - Mathf.RoundToInt(cumulativeWound));

                // The enemy's strike on the host (changes only when the carried wound changes).
                if (hostHp != lastHp)
                {
                    lastHp = hostHp;
                    tExpected = BattleSimulationKernel.ExpectedExchangeDamage(Mathf.RoundToInt(t.Attack), defense,
                        t.Abilities, hostTags, abilities, hostHp, out tHit);
                    float tBelow = hostHp > 1
                        ? BattleSimulationKernel.ExpectedExchangeDamage(Mathf.RoundToInt(t.Attack), defense,
                            t.Abilities, hostTags, abilities, hostHp - 1, out _)
                        : 0f;
                    hostDies = Mathf.Clamp01(tExpected - tBelow);
                }

                float hostRespondsAfterEnemy = 1f - (targetShock ? tHit : 0f);
                // Berserk: a hit on the host raises its Attack for its answer within this contact.
                float dealtB = berserk ? tHit * boosted + (1f - tHit) * dealtA : dealtA;

                // Host first: dealt; the enemy answers unless killed or suppressed.
                float enemyAnswers = (1f - killA) * (1f - (shock ? hitA : 0f));
                float woundA = enemyAnswers * tExpected;
                float deathA = enemyAnswers * hostDies;
                // Enemy first: the host answers unless dead or suppressed.
                float woundB = tExpected;
                float deathB = hostDies;

                total += alive * (hostFirst * dealtA
                    + (1f - hostFirst) * (1f - deathB) * hostRespondsAfterEnemy * dealtB);
                cumulativeWound += hostFirst * woundA + (1f - hostFirst) * woundB;
                alive *= 1f - (hostFirst * deathA + (1f - hostFirst) * deathB);
                if (regeneration && alive > 0f)
                    cumulativeWound = Mathf.Max(0f, cumulativeWound - 1f);   // +1 HP at the end of the owner's turn
            }
            return total;
        }

        // Value of the host's anti-air reaction: its damage on a legal air target, discounted by the
        // reactions of the local army that already fire first. 0 without a known air target.
        private static float ReactionValue(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx)
        {
            if (!abilities.Contains(UnitAbilities.AntiAir) || ctx.Targets == null || ctx.Targets.Count == 0)
                return 0f;
            float value = 0f;
            int air = 0;
            foreach (var t in ctx.Targets)
            {
                if (!(t.TypeTags?.Contains(UnitTypeTag.Aircraft) ?? false) || t.HitPoints <= 0)
                    continue;
                air++;
                int hp = Mathf.Max(1, Mathf.CeilToInt(t.HitPoints));
                float damage = BattleSimulationKernel.ExpectedExchangeDamage(s.Attack, Mathf.RoundToInt(t.Defense),
                    abilities, t.TypeTags, t.Abilities, hp, out _) / hp;
                value += damage * Mathf.Pow(1f - Mathf.Clamp01(damage), Mathf.Max(0, ctx.OtherAntiAirCarriers));
            }
            if (air == 0)
                return 0f;
            float airShare = air / (float)ctx.Targets.Count;
            return AiConfigV2.equipCombatBodyScale * airShare * value / air;
        }
    }
}
