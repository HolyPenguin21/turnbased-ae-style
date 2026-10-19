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
            EfficiencyStats after, IReadOnlyCollection<string> afterAbilities, EfficiencyContext ctx, bool includeCombat = true)
        {
            ctx = ctx ?? new EfficiencyContext();
            beforeAbilities = beforeAbilities ?? Array.Empty<string>();
            afterAbilities = afterAbilities ?? Array.Empty<string>();

            // ---- combat ---------------------------------------------------------------------------
            float dC = 0f, offense = 0f, defense = 0f;
            if (includeCombat && !ctx.IsHero)
            {
                IReadOnlyList<WorthIt.DefenderProfile> ground = GroundTargets(ctx);
                // One common value of the already owned body, including its existing stat upgrades.
                // End-of-horizon survival is separate from attacks already earned inside it.
                float survivorValue = Base(before) * AiConfigV2.equipCardValuePerE;
                float cBefore = Contribution(before, beforeAbilities, ctx, ground, survivorValue, ctx.BeforeConsumption);
                float cAfter = Contribution(after, afterAbilities, ctx, ground, survivorValue, ctx.AfterConsumption);
                dC = cAfter - cBefore;
                float scale = AiConfigV2.equipCombatCardScale;
                if (Mathf.Approximately(ctx.OffenseMult, ctx.DefenseMult)
                    || ctx.BeforeConsumption != null || ctx.AfterConsumption != null)
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
                    float cMid = Contribution(offensiveOnly, midAbilities, ctx, ground, survivorValue);
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
            // null = a reference context without witnesses; empty = a known operator with
            // no useful affordable output. Empty must not restore the flat reference value.
            if (ctx.OperatorOutputs != null)
                foreach (var o in ctx.OperatorOutputs)
                    fate += (o.PAfter - o.PBefore) * Mathf.Max(0f, o.Utility - o.CostIfSuccess);
            else if (operatorHost)
                fate += dFate * AiConfigV2.equipOperatorFateValue;   // owner-set value per Fate point

            // ---- anti-air reactions ---------------------------------------------------------------
            float antiAir = ctx.IsHero ? 0f
                : AiConfigV2.equipCombatCardScale
                    * (ReactionValue(after, afterAbilities, ctx, ctx.AfterConsumption)
                        - ReactionValue(before, beforeAbilities, ctx, ctx.BeforeConsumption));

            if (ctx.BeforeConsumption != null || ctx.AfterConsumption != null)
            {
                // Movement, AP and global-support values already use a reserve-horizon proxy.
                // End their equipment-only portion at the first expected activation/shot,
                // while the stateful combat term above prices the actual first-shot outcome.
                var steady = Utility(SpentStats(before, ctx.BeforeConsumption),
                    ctx.BeforeConsumption?.ApplyAbilities(beforeAbilities) ?? beforeAbilities,
                    SpentStats(after, ctx.AfterConsumption),
                    ctx.AfterConsumption?.ApplyAbilities(afterAbilities) ?? afterAbilities,
                    ctx.WithoutConsumption(), includeCombat: false);
                float first = 1f / Mathf.Max(1f, ctx.ExpectedActivations);
                float Mix(float armed, float later) => later + (armed - later) * first;
                ap = Mix(ap, steady.Ap); move = Mix(move, steady.Move);
                vision = Mix(vision, steady.Vision); detection = Mix(detection, steady.Detection);
                stealth = Mix(stealth, steady.Stealth); fate = Mix(fate, steady.Fate);
            }

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
            return AiConfigV2.equipCombatCardScale * (SortieValue(after, afterAbilities, ground, ctx, ctx.AfterConsumption)
                - SortieValue(before, beforeAbilities, ground, ctx, ctx.BeforeConsumption));
        }

        private static float SortieValue(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            IReadOnlyList<WorthIt.DefenderProfile> targets, EfficiencyContext ctx, EquipmentConsumption consumption = null)
        {
            if (consumption != null)
            {
                float uses = Mathf.Max(0f, ctx.ExpectedActivations);
                float first = Mathf.Min(1f, uses);
                float armed = SortieValue(s, abilities, targets, ctx);
                var spent = SpentStats(s, consumption);
                float rest = SortieValue(spent, consumption.ApplyAbilities(abilities), targets, ctx);
                return uses > 0f ? (first * armed + Mathf.Max(0f, uses - first) * rest) / uses : 0f;
            }
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

        private static string ContributionKey(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx, float survivorValue,
            EquipmentConsumption consumption)
        {
            var tags = ctx.HostTags != null ? string.Join(",", ctx.HostTags) : string.Empty;
            return $"{survivorValue}|{s.Attack}|{s.Defense}|{s.HitPoints}|{s.Range}|{s.Initiative}|{ctx.HpSpent}|{ctx.KnownDistance}|"
                + $"{ctx.HostCommanderInitiative}|{ctx.EnemyCommanderInitiative}|{ctx.SecondaryNeighbors}|{tags}|"
                + string.Join(",", abilities) + "|stateful="
                + (ctx.BeforeConsumption != null || ctx.AfterConsumption != null) + "|consumption=" + consumption?.Key;
        }

        // Mean over the enemy profiles of the unscaled contribution, times the body scale.
        // Each contact carries the complete surviving HP distribution, including lethal tails.
        private static float Contribution(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, IReadOnlyList<WorthIt.DefenderProfile> targets, float survivorValue,
            EquipmentConsumption consumption = null)
        {
            Dictionary<string, float> memo = null;
            string key = null;
            object owner = ctx.Targets;
            if (owner != null)
            {
                memo = s_contributions.GetValue(owner, _ => new Dictionary<string, float>());
                key = ContributionKey(s, abilities, ctx, survivorValue, consumption);
                lock (memo)
                    if (memo.TryGetValue(key, out float cached))
                        return cached;
            }
            float sum = 0f;
            foreach (var t in targets)
                sum += ctx.BeforeConsumption != null || ctx.AfterConsumption != null || consumption != null || t.Consumption != null
                    ? ConsumableContactSeries(s, abilities, consumption, ctx, t, survivorValue)
                    : ContactSeries(s, abilities, ctx, t, survivorValue);
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
            EfficiencyContext ctx, WorthIt.DefenderProfile t, float survivorValue)
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
            bool counterattack = abilities.Contains(UnitAbilities.Counterattack);

            // Splash / Scorcher recipients: the REAL neighbours of the primary target when its battle is
            // known, else the known composition stands in for unknown ones. Each neighbour is judged by its
            // own tags and HP (Scorcher only reaches a Bio neighbour, Splash any).

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
                return expected / targetHp + SecondaryFraction(attack, abilities, ctx, t);
            }

            int attackPool = s.Attack, defense = s.Defense;
            float dealtA = Strike(attackPool, out float killA, out float hitA);          // host acts first
            float boosted = berserk ? Strike(attackPool + mags.BerserkAttackGain, out _, out _) : dealtA;
            int hostInitiative = s.Initiative + ctx.HostCommanderInitiative;
            int targetInitiative = t.Initiative + ctx.EnemyCommanderInitiative;
            float hostFirst = hostInitiative > targetInitiative ? 1f : hostInitiative == targetInitiative ? 0.5f : 0f;

            // Compute both enemy damage distributions once; no mean-HP approximation and no
            // separate combat rules. The final bucket is lethal for the host's maximum HP.
            int maxHp = Mathf.Max(1, s.HitPoints);
            double[] plain = BattleSimulationKernel.ExchangeDamageDistribution(
                Mathf.RoundToInt(t.Attack), defense, t.Abilities, hostTags, abilities, maxHp, out _);
            double[] raised = targetBerserk
                ? BattleSimulationKernel.ExchangeDamageDistribution(
                    Mathf.RoundToInt(t.Attack) + mags.BerserkAttackGain, defense,
                    t.Abilities, hostTags, abilities, maxHp, out _)
                : plain;

            float Series(bool hostCan, bool targetCan, bool adjacent = false)
            {
                float hDealt = hostCan ? dealtA : 0f;
                float hBoosted = hostCan ? boosted : 0f;
                float hHit = hostCan ? hitA : 0f, hKill = hostCan ? killA : 0f;
                bool answers = counterattack && adjacent && hostCan && targetCan;
                var hpMass = new double[maxHp + 1];
                int initialHp = Mathf.Max(0, maxHp - Mathf.Max(0, ctx.HpSpent));
                hpMass[initialHp] = 1d;
                double total = 0d;
                for (int contact = 0; contact < AiConfigV2.equipContactCount; contact++)
                {
                    var next = new double[maxHp + 1];
                    void Survive(int hp, double weight)
                    {
                        if (hp <= 0 || weight <= 0d) return;
                        next[regeneration ? Mathf.Min(maxHp, hp + 1) : hp] += weight;
                    }
                    for (int hp = 1; hp <= maxHp; hp++)
                    {
                        double mass = hpMass[hp];
                        if (mass <= 0d) continue;
                        double first = mass * hostFirst, second = mass * (1f - hostFirst);
                        total += first * hDealt;
                        AnswerWeights(hHit, hKill, shock, out float unhurt, out float hurt);
                        // A killed or Shock-stopped enemy cannot answer this contact.
                        if (!targetCan)
                        {
                            Survive(hp, first + second);
                            total += second * hDealt;
                            continue;
                        }
                        Survive(hp, first * (1d - unhurt - hurt));
                        for (int damage = 0; damage < hp; damage++)
                        {
                            double pFirst = first * (unhurt * plain[damage] + hurt * raised[damage]);
                            double pSecond = second * plain[damage];
                            Survive(hp - damage, pFirst + pSecond);
                            bool canAnswer = !targetShock || damage == 0;
                            if (!canAnswer) continue;
                            if (answers) total += (pFirst + pSecond) * hDealt;
                            total += pSecond * (damage > 0 && berserk ? hBoosted : hDealt);
                        }
                    }
                    hpMass = next;
                }
                // Salvage the same pre-upgrade body in both projections. This prices losses
                // after the last contact without counting its already scored attacks again.
                double alive = 0d;
                for (int hp = 1; hp <= maxHp; hp++) alive += hpMass[hp];
                return (float)(total + alive * survivorValue
                    / (AiConfigV2.equipCombatCardScale * AiConfigV2.equipCombatBodyScale));
            }

            bool EnemyCan(int d) => t.Range < 0 || t.Range >= d;
            if (ctx.KnownDistance.HasValue)
            {
                int d = ctx.KnownDistance.Value;
                return Series(s.Range >= d, EnemyCan(d), d == 1);
            }
            return AiConfigV2.equipDistanceShare1 * Series(s.Range >= 1, EnemyCan(1), true)
                + AiConfigV2.equipDistanceShare2 * Series(s.Range >= 2, EnemyCan(2))
                + AiConfigV2.equipDistanceShare3 * Series(s.Range >= 3, EnemyCan(3))
                + AiConfigV2.equipDistanceShare4 * Series(s.Range >= 4, EnemyCan(4));
        }

        private static EfficiencyStats SpentStats(EfficiencyStats s, EquipmentConsumption c) => c == null ? s
            : new EfficiencyStats(s.Attack + c.Delta(EquipmentStat.Attack), s.Defense + c.Delta(EquipmentStat.Defense),
                s.HitPoints + c.Delta(EquipmentStat.HitPoints), s.Range + c.Delta(EquipmentStat.Range),
                s.Move + c.Delta(EquipmentStat.MoveMax), s.Initiative + c.Delta(EquipmentStat.Initiative),
                s.ActivationAp + c.Delta(EquipmentStat.ActivationApCost),
                s.Fate + c.Delta(EquipmentStat.Fate) - c.FateSpentExcess);

        // The same contact horizon, now carrying the host's HP distribution AND armed/spent
        // state. One real attack spends the item, not the contact/round itself. Enemy-first
        // deaths and Shock can therefore prevent its use; a counterattack can be its first shot.
        // Damage probabilities, armor, Berserk and Shock stay owned by the shared combat kernel.
        private static float ConsumableContactSeries(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EquipmentConsumption consumption, EfficiencyContext ctx, WorthIt.DefenderProfile target, float survivorValue)
        {
            var armed = new WorthIt.DefenderProfile(s.Defense, abilities.Contains(UnitAbilities.CeramicArmor),
                ctx.HostTags?.ToList(), s.Attack, Mathf.Max(1, s.HitPoints - ctx.HpSpent), s.Initiative,
                abilities.ToList(), s.HitPoints, range: s.Range, consumption: consumption);
            var spent = WorthIt.AfterAttack(armed);
            var mags = AbilityMagnitudes.Default;
            WorthIt.DefenderProfile State(WorthIt.DefenderProfile p, float hp, float attack, float defense) =>
                new WorthIt.DefenderProfile(defense, p.HasCeramicArmor, p.TypeTags, attack, hp,
                    p.Initiative, p.Abilities, p.MaxHitPoints, p.IsGroundCombatant, p.IsHero, p.FateMax,
                    p.IsSummoned, p.Range, p.Consumption);
            WorthIt.DefenderProfile Hit(WorthIt.DefenderProfile p, int damage)
            {
                int attack = Mathf.RoundToInt(p.Attack), defense = Mathf.RoundToInt(p.Defense);
                BattleSimulationKernel.ApplyBerserkIfHit(damage > 0, p.Abilities, ref attack, ref defense, mags);
                return State(p, Mathf.Max(0, p.HitPoints - damage), attack, defense);
            }
            float Series(int distance)
            {
                var mass = new Dictionary<(int Hp, int Attack, int Defense, bool Spent, bool Refilled), double>
                { [(Mathf.RoundToInt(armed.HitPoints), s.Attack, s.Defense, false, false)] = 1d };
                double total = 0d;
                int targetHp = Mathf.Max(1, Mathf.CeilToInt(target.HitPoints));
                for (int contact = 0; contact < AiConfigV2.equipContactCount; contact++)
                {
                    var next = new Dictionary<(int Hp, int Attack, int Defense, bool Spent, bool Refilled), double>();
                    void Finish(WorthIt.DefenderProfile host, double weight)
                    {
                        if (host.HitPoints <= 0 || weight <= 0d) return;
                        int hp = Mathf.RoundToInt(host.HitPoints);
                        bool refill = consumption?.HpSpentExcess > 0 && host.Consumption?.HpSpentExcess == 0;
                        if (host.Abilities.Contains(UnitAbilities.Regeneration))
                        {
                            refill |= hp < host.MaxHitPoints;
                            hp = Mathf.Min(Mathf.RoundToInt(host.MaxHitPoints), hp + 1);
                        }
                        var key = (hp, Mathf.RoundToInt(host.Attack), Mathf.RoundToInt(host.Defense),
                            consumption != null && host.Consumption == null, refill);
                        next.TryGetValue(key, out double prior); next[key] = prior + weight;
                    }
                    void HostStrike(WorthIt.DefenderProfile host, WorthIt.DefenderProfile enemy, double weight,
                        System.Action<WorthIt.DefenderProfile, WorthIt.DefenderProfile, double, bool> then)
                    {
                        if (host.HitPoints <= 0 || enemy.HitPoints <= 0 || host.Range < distance)
                        { then(host, enemy, weight, false); return; }
                        int limit = Mathf.Max(1, Mathf.CeilToInt(enemy.HitPoints));
                        double[] outcomes = BattleSimulationKernel.ExchangeDamageDistribution(
                            Mathf.RoundToInt(host.Attack), Mathf.RoundToInt(enemy.Defense), host.Abilities,
                            enemy.TypeTags, enemy.Abilities, limit, out _);
                        total += weight * SecondaryFraction(Mathf.RoundToInt(host.Attack), host.Abilities, ctx, enemy, target);
                        var after = WorthIt.AfterAttack(host);
                        for (int damage = 0; damage < outcomes.Length; damage++)
                        {
                            double p = weight * outcomes[damage];
                            if (p <= 0d) continue;
                            total += p * damage / targetHp;
                            then(after, Hit(enemy, damage), p,
                                BattleSimulationKernel.SuppressesTurn(damage, host.Abilities));
                        }
                    }
                    void EnemyStrike(WorthIt.DefenderProfile host, WorthIt.DefenderProfile enemy, double weight,
                        bool hostStillHasTurn)
                    {
                        if (host.HitPoints <= 0 || enemy.HitPoints <= 0
                            || enemy.Range >= 0 && enemy.Range < distance)
                        {
                            if (hostStillHasTurn) HostStrike(host, enemy, weight, (h, e, p, shock) => Finish(h, p));
                            else Finish(host, weight);
                            return;
                        }
                        int limit = Mathf.Max(1, Mathf.CeilToInt(host.HitPoints));
                        double[] outcomes = BattleSimulationKernel.ExchangeDamageDistribution(
                            Mathf.RoundToInt(enemy.Attack), Mathf.RoundToInt(host.Defense), enemy.Abilities,
                            host.TypeTags, host.Abilities, limit, out _);
                        var afterEnemy = WorthIt.AfterAttack(enemy);
                        for (int damage = 0; damage < outcomes.Length; damage++)
                        {
                            double p = weight * outcomes[damage];
                            if (p <= 0d) continue;
                            var hurt = Hit(host, damage);
                            bool shock = BattleSimulationKernel.SuppressesTurn(damage, enemy.Abilities);
                            void Continue(WorthIt.DefenderProfile h, WorthIt.DefenderProfile e, double chance, bool ignored)
                            {
                                if (hostStillHasTurn && !shock)
                                    HostStrike(h, e, chance, (a, b, w, stop) => Finish(a, w));
                                else Finish(h, chance);
                            }
                            if (BattleSimulationKernel.TriggersCounterattack(hurt.Abilities, false, hurt.HitPoints,
                                distance == 1, false, damage, enemy.Abilities))
                                HostStrike(hurt, afterEnemy, p, Continue);
                            else Continue(hurt, afterEnemy, p, false);
                        }
                    }
                    foreach (var entry in mass)
                    {
                        var k = entry.Key;
                        var basis = k.Spent ? spent : armed;
                        if (k.Refilled && basis.Consumption?.HpSpentExcess > 0)
                            basis = new WorthIt.DefenderProfile(basis.Defense, basis.HasCeramicArmor, basis.TypeTags,
                                basis.Attack, basis.HitPoints, basis.Initiative, basis.Abilities, basis.MaxHitPoints,
                                range: basis.Range, consumption: basis.Consumption.AfterHpRefill());
                        var host = State(basis, k.Hp, k.Attack, k.Defense);
                        int hIni = host.Initiative + ctx.HostCommanderInitiative;
                        int eIni = target.Initiative + ctx.EnemyCommanderInitiative;
                        double first = hIni > eIni ? 1d : hIni == eIni ? 0.5d : 0d;
                        if (first > 0d) HostStrike(host, target, entry.Value * first, (h, e, p, shock) =>
                        {
                            if (shock) Finish(h, p); else EnemyStrike(h, e, p, false);
                        });
                        if (first < 1d) EnemyStrike(host, target, entry.Value * (1d - first), true);
                    }
                    mass = next;
                }
                return (float)(total + mass.Values.Sum() * survivorValue
                    / (AiConfigV2.equipCombatCardScale * AiConfigV2.equipCombatBodyScale));
            }
            if (ctx.KnownDistance.HasValue) return Series(ctx.KnownDistance.Value);
            return AiConfigV2.equipDistanceShare1 * Series(1) + AiConfigV2.equipDistanceShare2 * Series(2)
                + AiConfigV2.equipDistanceShare3 * Series(3) + AiConfigV2.equipDistanceShare4 * Series(4);
        }

        private static float SecondaryFraction(int attack, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, WorthIt.DefenderProfile t, WorthIt.DefenderProfile? referenceTarget = null)
        {
            IReadOnlyList<string> targetAbilities = t.Abilities ?? Array.Empty<string>();
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
            // Projected stats may change after a shot; army membership still belongs to the observation.
            WorthIt.DefenderProfile observed = referenceTarget ?? t;
            List<WorthIt.DefenderProfile> known = KnownNeighbours(ctx, observed);
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
                List<WorthIt.DefenderProfile> pool = NeighbourPool(ctx, observed);
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
        private static float ReactionValue(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx,
            EquipmentConsumption consumption = null)
        {
            if (!abilities.Contains(UnitAbilities.AntiAir))
                return 0f;
            if (ctx.AirContacts != null && ctx.AirContacts.Count > 0)
                return ConcreteReactionValue(s, abilities, ctx, consumption);
            return ReserveReactionProxy(s, abilities, ctx);
        }

        private static float AirShotFraction(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            WorthIt.DefenderProfile air)
        {
            int hp = Mathf.Max(1, Mathf.CeilToInt(air.HitPoints));
            return BattleSimulationKernel.ExpectedExchangeDamage(s.Attack, Mathf.RoundToInt(air.Defense),
                abilities, air.TypeTags, air.Abilities ?? Array.Empty<string>(), hp, out _) / hp;
        }

        private static float ConcreteReactionValue(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx,
            EquipmentConsumption consumption = null)
        {
            if (ctx.HostHidden || !Game.Aviation.AntiAirRules.TryGetRadius(abilities, ctx.AntiAirRadius, out int radius))
                return 0f;
            float value = 0f;
            foreach (AirContact c in ctx.AirContacts)
            {
                if (!Game.Aviation.AntiAirRules.TryGetRadius(abilities, ctx.AntiAirRadius, out radius)
                    || c.ReactionUsed || !c.OwnerSeesHex || c.Distance > radius)
                    continue;
                float damage = Mathf.Clamp01(AirShotFraction(s, abilities, c.Air));
                value += damage * Mathf.Pow(1f - damage, Mathf.Max(0, c.EarlierReactions));
                if (consumption != null)
                {
                    s = SpentStats(s, consumption); abilities = consumption.ApplyAbilities(abilities);
                    consumption = null;
                }
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
