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
            bool operatorHost = ctx.IsHero && (beforeAbilities.Contains(UnitAbilities.Researcher)
                || beforeAbilities.Contains(UnitAbilities.Assembler));
            // Roles by fact: an operator that leads no field army is not valued as a battle commander.
            if (ctx.IsHero && (!operatorHost || ctx.CommandsFieldArmy))
                // Commander Fate keeps its owner weight (mean army Attack per Fate point), in card units.
                fate += dFate * AiConfigV2.equipHeroFateFactor * ctx.ArmyAttack * AiConfigV2.equipCardValuePerE;
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

        // One enemy profile against the host over the contact horizon. Geometry is a distance bucket: at
        // distance d the HOST strikes only if its Range reaches d and the ENEMY answers only if its Range
        // does (unknown enemy Range answers everywhere). Buckets are the reference distance shares, or the
        // single known contact distance. The exchange numbers are computed once; buckets only gate them.
        // Outcomes are JOINT: a kill is a subset of a hit, so Shock (any positive damage cancels the answer)
        // and a plain kill never both subtract from the same probability mass; an enemy Berserk that
        // survives a hit answers with the raised Attack.
        private static float ContactSeries(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, WorthIt.DefenderProfile t)
        {
            AbilityMagnitudes mags = AbilityMagnitudes.Default;
            IReadOnlyCollection<UnitTypeTag> hostTags = ctx.HostTags ?? Array.Empty<UnitTypeTag>();
            IReadOnlyList<string> targetAbilities = t.Abilities ?? Array.Empty<string>();
            int targetHp = Mathf.Max(1, Mathf.CeilToInt(t.HitPoints));
            bool berserk = abilities.Contains(UnitAbilities.Berserk);
            bool shock = abilities.Contains(UnitAbilities.ShockAttack);
            bool targetShock = targetAbilities.Contains(UnitAbilities.ShockAttack);
            bool targetBerserk = targetAbilities.Contains(UnitAbilities.Berserk);
            bool regeneration = abilities.Contains(UnitAbilities.Regeneration);

            // Splash / Scorcher recipients: the REAL neighbours of the primary target when its battle is
            // known, else the known composition stands in for unknown ones. Each neighbour is judged by its
            // own tags and HP (Scorcher only reaches a Bio neighbour, Splash any).
            float SecondaryFraction(int attack)
            {
                bool splash = abilities.Contains(UnitAbilities.Splash);
                bool scorch = abilities.Contains(UnitAbilities.Scorcher);
                if (!splash && !scorch)
                    return 0f;
                float sumAll = 0f, sumBio = 0f;
                int n;
                float Side(WorthIt.DefenderProfile j)
                {
                    int hpJ = Mathf.Max(1, Mathf.CeilToInt(j.HitPoints));
                    return BattleSimulationKernel.ExpectedExchangeDamage(attack, Mathf.RoundToInt(t.Defense),
                        abilities, t.TypeTags, targetAbilities, hpJ, out _, true, j.Abilities) / hpJ;
                }
                bool IsBio(WorthIt.DefenderProfile j) => j.TypeTags?.Contains(UnitTypeTag.Bio) ?? false;
                List<WorthIt.DefenderProfile> known = KnownNeighbours(ctx, t);
                if (known != null)
                {
                    n = known.Count;
                    foreach (var j in known)
                    {
                        float side = Side(j);
                        sumAll += side;
                        if (IsBio(j)) sumBio += side;
                    }
                }
                else
                {
                    // Unknown neighbours: how many (mean enemy army size - 1) and what they look like (the
                    // other known profiles, a small deterministic sample) are separate estimates.
                    List<WorthIt.DefenderProfile> pool = NeighbourPool(ctx, t);
                    n = Mathf.Min(Mathf.Max(0, Mathf.RoundToInt(ctx.SecondaryNeighbors)), 8);
                    if (pool.Count == 0 || n == 0)
                        return 0f;
                    float allMean = 0f, bioMean = 0f;
                    foreach (var j in pool)
                    {
                        float side = Side(j);
                        allMean += side;
                        if (IsBio(j)) bioMean += side;
                    }
                    sumAll = n * allMean / pool.Count;
                    sumBio = n * bioMean / pool.Count;
                }
                if (n <= 0)
                    return 0f;
                int splashCount = splash ? Mathf.Min(2, n) : 0;
                float fraction = splashCount / (float)n * sumAll;
                // Scorcher picks one of the neighbours Splash left: each neighbour is its pick with 1/n.
                if (scorch && n > splashCount)
                    fraction += sumBio / n;
                return fraction;
            }

            // Host strike on a fresh enemy: damage as a fraction of the targets, kill and hit chances.
            float Strike(int attack, out float kill, out float hit)
            {
                float expected = BattleSimulationKernel.ExpectedExchangeDamage(attack, t.Defense < 0 ? 0 : Mathf.RoundToInt(t.Defense),
                    abilities, t.TypeTags, targetAbilities, targetHp, out hit);
                float below = targetHp > 1
                    ? BattleSimulationKernel.ExpectedExchangeDamage(attack, Mathf.RoundToInt(t.Defense),
                        abilities, t.TypeTags, targetAbilities, targetHp - 1, out _)
                    : 0f;
                kill = Mathf.Clamp01(expected - below);
                return expected / targetHp + SecondaryFraction(attack);
            }

            int attackPool = s.Attack, defense = s.Defense;
            float dealtA = Strike(attackPool, out float killA, out float hitA);          // host acts first
            float boosted = berserk ? Strike(attackPool + mags.BerserkAttackGain, out _, out _) : dealtA;
            int hostInitiative = s.Initiative + ctx.HostCommanderInitiative;
            int targetInitiative = t.Initiative + ctx.EnemyCommanderInitiative;
            float hostFirst = hostInitiative > targetInitiative ? 1f : hostInitiative == targetInitiative ? 0.5f : 0f;

            // The enemy strike depends on the wound the host carries into the contact and on whether a hit
            // on the enemy raised its Attack (Berserk): memoised per (HP, raised).
            var enemy = new Dictionary<(int, bool), (float expected, float hit, float dies)>();
            (float expected, float hit, float dies) EnemyStrike(int hostHp, bool raised)
            {
                if (enemy.TryGetValue((hostHp, raised), out var known))
                    return known;
                int attack = Mathf.RoundToInt(t.Attack) + (raised ? mags.BerserkAttackGain : 0);
                float e = BattleSimulationKernel.ExpectedExchangeDamage(attack, defense,
                    t.Abilities, hostTags, abilities, hostHp, out float hit);
                float below = hostHp > 1
                    ? BattleSimulationKernel.ExpectedExchangeDamage(attack, defense,
                        t.Abilities, hostTags, abilities, hostHp - 1, out _)
                    : 0f;
                return enemy[(hostHp, raised)] = (e, hit, Mathf.Clamp01(e - below));
            }

            float Series(bool hostCan, bool targetCan)
            {
                float hDealtA = hostCan ? dealtA : 0f, hKillA = hostCan ? killA : 0f, hHitA = hostCan ? hitA : 0f;
                float hBoosted = hostCan ? boosted : 0f;
                float cumulativeWound = Mathf.Max(0, ctx.HpSpent);
                float alive = 1f, total = 0f;
                for (int contact = 0; contact < AiConfigV2.equipContactCount; contact++)
                {
                    int hostHp = Mathf.Max(1, s.HitPoints - Mathf.RoundToInt(cumulativeWound));
                    (float expected, float hit, float dies) plain = targetCan ? EnemyStrike(hostHp, false) : (0f, 0f, 0f);
                    (float expected, float hit, float dies) raised = targetCan && targetBerserk ? EnemyStrike(hostHp, true) : plain;

                    // ---- host first. The enemy answers unless it died (a kill) or, with Shock, took ANY
                    // damage (a hit, kills included). A survivor of a hit answers with the raised Attack.
                    AnswerWeights(hHitA, hKillA, shock, out float answerUnhurt, out float answerHurt);
                    float woundA = answerUnhurt * plain.expected + answerHurt * raised.expected;
                    float deathA = answerUnhurt * plain.dies + answerHurt * raised.dies;

                    // ---- enemy first (at its plain Attack). The host answers unless it died or, against a
                    // Shock enemy, was hit at all; a surviving hit raises a Berserk host.
                    AnswerWeights(plain.hit, plain.dies, targetShock, out float hostUnhit, out float hostHitSurvives);
                    float answerB = hostUnhit * hDealtA + hostHitSurvives * (berserk ? hBoosted : hDealtA);
                    float woundB = plain.expected;
                    float deathB = plain.dies;

                    total += alive * (hostFirst * hDealtA + (1f - hostFirst) * answerB);
                    cumulativeWound += hostFirst * woundA + (1f - hostFirst) * woundB;
                    alive *= 1f - (hostFirst * deathA + (1f - hostFirst) * deathB);
                    if (regeneration && alive > 0f)
                        cumulativeWound = Mathf.Max(0f, cumulativeWound - 1f);   // +1 HP at the end of the owner turn
                }
                return total;
            }

            bool EnemyCan(int d) => t.Range < 0 || t.Range >= d;
            if (ctx.KnownDistance.HasValue)
            {
                int d = ctx.KnownDistance.Value;
                return Series(s.Range >= d, EnemyCan(d));
            }
            return AiConfigV2.equipDistanceShare1 * Series(s.Range >= 1, EnemyCan(1))
                + AiConfigV2.equipDistanceShare2 * Series(s.Range >= 2, EnemyCan(2))
                + AiConfigV2.equipDistanceShare3 * Series(s.Range >= 3, EnemyCan(3))
                + AiConfigV2.equipDistanceShare4 * Series(s.Range >= 4, EnemyCan(4));
        }

        // Joint outcomes of one strike for the side that may answer it. A kill is a SUBSET of a hit, so:
        // unhurt (answers at its plain Attack) = 1 - hit; hurt-but-alive (answers, a Berserk raised) = hit - kill,
        // and with Shock any positive damage cancels the answer, so only the unhurt answer remains.
        internal static void AnswerWeights(float hit, float kill, bool shock, out float unhurt, out float hurtAlive)
        {
            unhurt = Mathf.Clamp01(1f - hit);
            hurtAlive = shock ? 0f : Mathf.Max(0f, hit - kill);
        }

        // The other ground bodies of the battle that contains `t`, or null when its battle is unknown.
        private static List<WorthIt.DefenderProfile> KnownNeighbours(EfficiencyContext ctx, WorthIt.DefenderProfile t)
        {
            if (ctx.Battles == null)
                return null;
            foreach (var battle in ctx.Battles)
            {
                int at = -1;
                for (int i = 0; i < battle.Count; i++)
                    if (battle[i].Equals(t)) { at = i; break; }
                if (at < 0)
                    continue;
                var others = new List<WorthIt.DefenderProfile>(battle.Count - 1);
                for (int i = 0; i < battle.Count; i++)
                    if (i != at) others.Add(battle[i]);
                return others;
            }
            return null;
        }

        // Stand-in neighbours: the other known ground profiles, at most six, picked by stride.
        private static List<WorthIt.DefenderProfile> NeighbourPool(EfficiencyContext ctx, WorthIt.DefenderProfile t)
        {
            var all = new List<WorthIt.DefenderProfile>();
            bool skipped = false;
            if (ctx.Targets != null)
                foreach (var j in ctx.Targets)
                {
                    if (j.IsHero || j.HitPoints <= 0 || (j.TypeTags?.Contains(UnitTypeTag.Aircraft) ?? false))
                        continue;
                    if (!skipped && j.Equals(t)) { skipped = true; continue; }
                    all.Add(j);
                }
            if (all.Count <= 6)
                return all;
            var sample = new List<WorthIt.DefenderProfile>(6);
            for (int i = 0; i < 6; i++)
                sample.Add(all[(int)((long)i * all.Count / 6)]);
            return sample;
        }

        // AA value, two explicitly different things.
        //  * CONCRETE: known air armies with a distance, owner vision, the host hidden state and the used
        //    reaction. A reaction is legal exactly when AntiAirRules would offer it (radius, the owner sees
        //    the hex, the carrier is not hidden, it has not already reacted); only legal ones pay, and a
        //    carrier that fires earlier in the rule order (same army, lower slot) takes its share first.
        //  * RESERVE PROXY: no concrete contact, only a known air composition - a labelled estimate of one
        //    shot at that composition. Never presented as a legal reaction.
        private static float ReactionValue(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx)
        {
            if (!abilities.Contains(UnitAbilities.AntiAir))
                return 0f;
            if (ctx.AirContacts != null && ctx.AirContacts.Count > 0)
                return ConcreteReactionValue(s, abilities, ctx);
            return ReserveReactionProxy(s, abilities, ctx);
        }

        private static float AirShotFraction(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            WorthIt.DefenderProfile air)
        {
            int hp = Mathf.Max(1, Mathf.CeilToInt(air.HitPoints));
            return BattleSimulationKernel.ExpectedExchangeDamage(s.Attack, Mathf.RoundToInt(air.Defense),
                abilities, air.TypeTags, air.Abilities ?? Array.Empty<string>(), hp, out _) / hp;
        }

        private static float ConcreteReactionValue(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx)
        {
            if (ctx.HostHidden || !Game.Aviation.AntiAirRules.TryGetRadius(abilities, ctx.AntiAirRadius, out int radius))
                return 0f;
            float value = 0f;
            foreach (AirContact c in ctx.AirContacts)
            {
                if (c.ReactionUsed || !c.OwnerSeesHex || c.Distance > radius)
                    continue;
                float damage = Mathf.Clamp01(AirShotFraction(s, abilities, c.Air));
                value += damage * Mathf.Pow(1f - damage, Mathf.Max(0, c.EarlierReactions));
            }
            return AiConfigV2.equipCombatBodyScale * value;
        }

        private static float ReserveReactionProxy(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx)
        {
            if (ctx.Targets == null || ctx.Targets.Count == 0)
                return 0f;
            float value = 0f;
            int air = 0;
            foreach (var t in ctx.Targets)
            {
                if (!(t.TypeTags?.Contains(UnitTypeTag.Aircraft) ?? false) || t.HitPoints <= 0)
                    continue;
                air++;
                float damage = Mathf.Clamp01(AirShotFraction(s, abilities, t));
                value += damage * Mathf.Pow(1f - damage, Mathf.Max(0, ctx.OtherAntiAirCarriers));
            }
            if (air == 0)
                return 0f;
            float airShare = air / (float)ctx.Targets.Count;
            return AiConfigV2.equipCombatBodyScale * airShare * value / air;
        }
    }
}
