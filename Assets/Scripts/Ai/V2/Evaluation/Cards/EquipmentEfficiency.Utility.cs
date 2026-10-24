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
                GroundSet ground = GroundTargets(ctx);
                // One common value of the already owned body, including its existing stat upgrades.
                // End-of-horizon survival is separate from attacks already earned inside it.
                float survivorValue = Base(before) * AiConfigV2.equipCardValuePerE;
                bool area = ctx.SingleUseHost || ExplosionAttack.TargetLimit(beforeAbilities) > 0 || ExplosionAttack.TargetLimit(afterAbilities) > 0;
                float cBefore = area ? ExplosionBattleValue(before, beforeAbilities, ctx, ground.All, ctx.BeforeConsumption)
                    : Contribution(before, beforeAbilities, ctx, ground, survivorValue, ctx.BeforeConsumption);
                float cAfter = area ? ExplosionBattleValue(after, afterAbilities, ctx, ground.All, ctx.AfterConsumption)
                    : Contribution(after, afterAbilities, ctx, ground, survivorValue, ctx.AfterConsumption);
                dC = cAfter - cBefore;
                float scale = AiConfigV2.equipCombatCardScale;
                if (Mathf.Approximately(ctx.OffenseMult, ctx.DefenseMult)
                    || ctx.BeforeConsumption != null || ctx.AfterConsumption != null || area)
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
                // The rest of the expected load (beyond the route's own activations) is the usual AP saving.
                float remaining = Mathf.Max(0f, ctx.ExpectedActivations - stepsBefore);
                // 2026-10-21 (playtest Mordak T21) — the speed change outlives the route like the AP
                // change does: the walking the army still does after it (`remaining` activations at
                // the old speed) takes remaining * vBefore / vAfter at the new one. Recoil Cannon
                // (MoveMax 2->1) on an army one hex from its target read "move 0" and the whole army
                // walked home at speed 1 that same turn.
                float afterRoute = vAfter == vBefore ? 0f
                    : remaining * (1f - vBefore / (float)Mathf.Max(1, vAfter));
                move = SignedCard((others + apBefore) * (stepsBefore - stepsAfter + afterRoute));
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
            IReadOnlyList<WorthIt.DefenderProfile> ground = GroundTargets(ctx).All;
            if (ctx.SingleUseHost || ExplosionAttack.TargetLimit(beforeAbilities) > 0 || ExplosionAttack.TargetLimit(afterAbilities) > 0)
                return AiConfigV2.equipCombatCardScale * (ExplosionSortieValue(after, afterAbilities, ctx, ground, ctx.AfterConsumption)
                    - ExplosionSortieValue(before, beforeAbilities, ctx, ground, ctx.BeforeConsumption));
            return AiConfigV2.equipCombatCardScale * (SortieValue(after, afterAbilities, ground, ctx, ctx.AfterConsumption)
                - SortieValue(before, beforeAbilities, ground, ctx, ctx.BeforeConsumption));
        }

        private static WorthIt.DefenderProfile ExplosionHost(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, EquipmentConsumption consumption) =>
            new WorthIt.DefenderProfile(s.Defense, abilities.Contains(UnitAbilities.CeramicArmor), ctx.HostTags?.ToArray(),
                s.Attack, Mathf.Max(1, s.HitPoints - ctx.HpSpent), s.Initiative, abilities.ToArray(), s.HitPoints,
                range: s.Range, consumption: consumption, singleUseUnit: ctx.SingleUseHost);

        // Area attacks need a roster forecast: the one-on-one contact horizon cannot represent
        // random distinct victims or friendly fire. Both candidate states use the same estimator.
        private static float ExplosionBattleValue(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, IReadOnlyList<WorthIt.DefenderProfile> targets, EquipmentConsumption consumption)
        {
            var own = (ctx.ExplosionAllies ?? Array.Empty<WorthIt.DefenderProfile>()).ToList();
            own.Add(ExplosionHost(s, abilities, ctx, consumption));
            var battles = ctx.Battles != null && ctx.Battles.Count > 0 ? ctx.Battles
                : new[] { (IReadOnlyList<WorthIt.DefenderProfile>)targets };
            float value = 0;
            foreach (var enemies in battles)
            {
                var outcome = WorthIt.Estimate(own, enemies.ToArray(), 0f);
                value += outcome.WinChance * (1f + outcome.ExpectedSurvivingHpRatioOnWin);
            }
            return AiConfigV2.equipCombatBodyScale * value / battles.Count;
        }

        private static float ExplosionSortieValue(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, IReadOnlyList<WorthIt.DefenderProfile> targets, EquipmentConsumption consumption)
        {
            var host = ExplosionHost(s, abilities, ctx, consumption);
            var allies = (ctx.ExplosionAllies ?? Array.Empty<WorthIt.DefenderProfile>())
                .Select(p => new AviationCombatEstimator.ExplosionCollateral(p, true, armyId: 0)).ToArray();
            var forecast = AviationCombatEstimator.EstimateAirStrike(new[] { host }, targets,
                Game.Aviation.AirStrikePolicy.Standard, Mathf.Max(1, Mathf.CeilToInt(ctx.ExpectedActivations)), collateral: allies);
            float hp = targets.Sum(p => Mathf.Max(1, p.HitPoints));
            return AiConfigV2.equipCombatBodyScale * forecast.NetExpectedDamage / Mathf.Max(1, hp);
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

        // P(spot successes > hide successes) for fair tokens pools.
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
        // The priced ground targets of one context, and the same list folded by content: identical
        // bodies (four Riflemen of one army) give identical contact series, so Contribution prices
        // each distinct profile once and weights it by its count. Pure in ctx.Targets: memoised per
        // target-list instance for as long as the list lives. Consumers treat both lists as read-only.
        private sealed class GroundSet
        {
            internal IReadOnlyList<WorthIt.DefenderProfile> All;
            internal List<WorthIt.DefenderProfile> Distinct;
            internal List<int> Counts;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, GroundSet> s_groundSets =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, GroundSet>();

        private static GroundSet GroundTargets(EfficiencyContext ctx)
        {
            if (ctx.Targets == null)
                return Fold(GroundTargetList(ctx));
            if (s_groundSets.TryGetValue(ctx.Targets, out GroundSet known))
                return known;
            GroundSet built = Fold(GroundTargetList(ctx));
            return s_groundSets.GetValue(ctx.Targets, _ => built);
        }

        private static GroundSet Fold(IReadOnlyList<WorthIt.DefenderProfile> all)
        {
            var set = new GroundSet { All = all, Distinct = new List<WorthIt.DefenderProfile>(), Counts = new List<int>() };
            foreach (var t in all)
            {
                int at = set.Distinct.FindIndex(d => d.SameContent(t));
                if (at >= 0) set.Counts[at]++;
                else { set.Distinct.Add(t); set.Counts.Add(1); }
            }
            return set;
        }

        private static IReadOnlyList<WorthIt.DefenderProfile> GroundTargetList(EfficiencyContext ctx)
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
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<ContributionKey, float>>
            s_contributions = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<ContributionKey, float>>();

        // Every input of Contribution besides the target list (the memo owner).
        private readonly struct ContributionKey : IEquatable<ContributionKey>
        {
            private readonly float _survivorValue, _secondaryNeighbors;
            private readonly int _attack, _defense, _hitPoints, _range, _initiative;
            private readonly int _hpSpent, _hostCommanderInitiative, _enemyCommanderInitiative;
            private readonly int? _knownDistance;
            private readonly bool _stateful;
            private readonly string _tags, _abilities, _consumption;

            internal ContributionKey(EfficiencyStats s, IReadOnlyCollection<string> abilities, EfficiencyContext ctx,
                float survivorValue, EquipmentConsumption consumption)
            {
                _survivorValue = survivorValue; _secondaryNeighbors = ctx.SecondaryNeighbors;
                _attack = s.Attack; _defense = s.Defense; _hitPoints = s.HitPoints; _range = s.Range;
                _initiative = s.Initiative;
                _hpSpent = ctx.HpSpent; _knownDistance = ctx.KnownDistance;
                _hostCommanderInitiative = ctx.HostCommanderInitiative;
                _enemyCommanderInitiative = ctx.EnemyCommanderInitiative;
                _stateful = ctx.BeforeConsumption != null || ctx.AfterConsumption != null;
                _tags = ctx.HostTags != null ? string.Join(",", ctx.HostTags) : string.Empty;
                _abilities = string.Join(",", abilities);
                _consumption = consumption?.Key;
            }

            public bool Equals(ContributionKey o) =>
                _survivorValue.Equals(o._survivorValue) && _secondaryNeighbors.Equals(o._secondaryNeighbors)
                && _attack == o._attack && _defense == o._defense && _hitPoints == o._hitPoints
                && _range == o._range && _initiative == o._initiative
                && _hpSpent == o._hpSpent && _knownDistance == o._knownDistance
                && _hostCommanderInitiative == o._hostCommanderInitiative
                && _enemyCommanderInitiative == o._enemyCommanderInitiative && _stateful == o._stateful
                && string.Equals(_tags, o._tags, StringComparison.Ordinal)
                && string.Equals(_abilities, o._abilities, StringComparison.Ordinal)
                && string.Equals(_consumption, o._consumption, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is ContributionKey o && Equals(o);

            public override int GetHashCode() =>
                (_attack, _defense, _hitPoints, _range, _initiative, _survivorValue,
                    StringComparer.Ordinal.GetHashCode(_abilities)).GetHashCode();
        }

        // Mean over the enemy profiles of the unscaled contribution, times the body scale.
        // Each contact carries the complete surviving HP distribution, including lethal tails.
        private static float Contribution(EfficiencyStats s, IReadOnlyCollection<string> abilities,
            EfficiencyContext ctx, GroundSet ground, float survivorValue,
            EquipmentConsumption consumption = null)
        {
            Dictionary<ContributionKey, float> memo = null;
            ContributionKey key = default;
            object owner = ctx.Targets;
            if (owner != null)
            {
                memo = s_contributions.GetValue(owner, _ => new Dictionary<ContributionKey, float>());
                key = new ContributionKey(s, abilities, ctx, survivorValue, consumption);
                lock (memo)
                    if (memo.TryGetValue(key, out float cached))
                        return cached;
            }
            bool stateful = ctx.BeforeConsumption != null || ctx.AfterConsumption != null || consumption != null;
            float sum = 0f;
            // Splash / Scorcher read a target's battle neighbours by profile IDENTITY: equal bodies
            // fold only while the host carries neither, armed or spent (a consumption may rewrite
            // the ability list). Without them a series reads its target by content alone.
            bool Secondary(IReadOnlyCollection<string> list) =>
                list.Contains(UnitAbilities.Splash) || list.Contains(UnitAbilities.Scorcher);
            if (!Secondary(abilities) && (consumption == null || !Secondary(consumption.ApplyAbilities(abilities))))
            {
                for (int i = 0; i < ground.Distinct.Count; i++)
                {
                    var t = ground.Distinct[i];
                    sum += ground.Counts[i] * (stateful || t.Consumption != null
                        ? ConsumableContactSeries(s, abilities, consumption, ctx, t, survivorValue)
                        : ContactSeries(s, abilities, ctx, t, survivorValue));
                }
            }
            else
                foreach (var t in ground.All)
                    sum += stateful || t.Consumption != null
                        ? ConsumableContactSeries(s, abilities, consumption, ctx, t, survivorValue)
                        : ContactSeries(s, abilities, ctx, t, survivorValue);
            float value = AiConfigV2.equipCombatBodyScale * sum / ground.All.Count;
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
            // The kernel's memoised PMFs themselves: read only.
            double[] plain = BattleSimulationKernel.SharedExchangeDamageDistribution(
                Mathf.RoundToInt(t.Attack), defense, t.Abilities, hostTags, abilities, maxHp);
            double[] raised = targetBerserk
                ? BattleSimulationKernel.SharedExchangeDamageDistribution(
                    Mathf.RoundToInt(t.Attack) + mags.BerserkAttackGain, defense,
                    t.Abilities, hostTags, abilities, maxHp)
                : plain;

            float Series(bool hostCan, bool targetCan, bool adjacent = false)
            {
                float hDealt = hostCan ? dealtA : 0f;
                float hBoosted = hostCan ? boosted : 0f;
                float hHit = hostCan ? hitA : 0f, hKill = hostCan ? killA : 0f;
                bool answers = counterattack && adjacent && hostCan && targetCan;
                AnswerWeights(hHit, hKill, shock, out float unhurt, out float hurt);
                // Two scratch HP distributions, swapped per contact (the series never nests).
                double[] hpMass = Scratch(ref s_massA, maxHp + 1), next = Scratch(ref s_massB, maxHp + 1);
                Array.Clear(hpMass, 0, maxHp + 1);
                int initialHp = Mathf.Max(0, maxHp - Mathf.Max(0, ctx.HpSpent));
                hpMass[initialHp] = 1d;
                double total = 0d;
                for (int contact = 0; contact < AiConfigV2.equipContactCount; contact++)
                {
                    Array.Clear(next, 0, maxHp + 1);
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
                    (hpMass, next) = (next, hpMass);
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
            // A bucket's series depends only on who reaches whom (adjacency only through
            // Counterattack): buckets with the same reach share one computation.
            int Reach(int d) => (s.Range >= d ? 1 : 0) | (EnemyCan(d) ? 2 : 0) | (counterattack && d == 1 ? 4 : 0);
            int r1 = Reach(1), r2 = Reach(2), r3 = Reach(3), r4 = Reach(4);
            float v1 = Series(s.Range >= 1, EnemyCan(1), true);
            float v2 = r2 == r1 ? v1 : Series(s.Range >= 2, EnemyCan(2));
            float v3 = r3 == r2 ? v2 : r3 == r1 ? v1 : Series(s.Range >= 3, EnemyCan(3));
            float v4 = r4 == r3 ? v3 : r4 == r2 ? v2 : r4 == r1 ? v1 : Series(s.Range >= 4, EnemyCan(4));
            return AiConfigV2.equipDistanceShare1 * v1 + AiConfigV2.equipDistanceShare2 * v2
                + AiConfigV2.equipDistanceShare3 * v3 + AiConfigV2.equipDistanceShare4 * v4;
        }

        [ThreadStatic] private static double[] s_massA, s_massB;

        private static double[] Scratch(ref double[] buffer, int length)
        {
            if (buffer == null || buffer.Length < length)
                buffer = new double[Math.Max(length, 16)];
            return buffer;
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
            // Every state below is derived from armed / spent / the target and shares their owned
            // lists, so (lists, HP, Attack, Defense) identifies a profile: its after-attack state
            // is computed once per call, not once per branch of the outcome tree.
            var afterAttack = new Dictionary<(object, object, float, float, float, float), WorthIt.DefenderProfile>();
            WorthIt.DefenderProfile AfterAttack(WorthIt.DefenderProfile p)
            {
                if (p.Consumption == null && !p.SingleUseUnit)
                    return p;
                var key = ((object)p.Abilities, (object)p.Consumption, p.HitPoints, p.Attack, p.Defense, p.MaxHitPoints);
                if (!afterAttack.TryGetValue(key, out WorthIt.DefenderProfile known))
                    afterAttack[key] = known = WorthIt.AfterAttack(p);
                return known;
            }
            // Splash / Scorcher share of a strike: it reads the struck enemy's Defense, tags and
            // abilities (not its HP) and the observed target's neighbours - once per distinct strike.
            var secondary = new Dictionary<(int, object, float, object, object), float>();
            float Secondary(int attack, IReadOnlyList<string> hostAbilities, WorthIt.DefenderProfile enemy)
            {
                var key = (attack, (object)hostAbilities, enemy.Defense, (object)enemy.TypeTags, (object)enemy.Abilities);
                if (!secondary.TryGetValue(key, out float known))
                    secondary[key] = known = SecondaryFraction(attack, hostAbilities, ctx, enemy, target);
                return known;
            }
            WorthIt.DefenderProfile State(WorthIt.DefenderProfile p, float hp, float attack, float defense) =>
                p.WithState(hp, attack, defense);
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
                var next = new Dictionary<(int Hp, int Attack, int Defense, bool Spent, bool Refilled), double>();
                // One contact is linear in the state's probability and the same in every contact:
                // each host state's step (damage dealt, next states) is walked once at unit weight
                // and scaled for every contact that reaches the state again.
                var steps = new Dictionary<(int Hp, int Attack, int Defense, bool Spent, bool Refilled),
                    (double Dealt, KeyValuePair<(int Hp, int Attack, int Defense, bool Spent, bool Refilled), double>[] Next)>();
                var into = new Dictionary<(int Hp, int Attack, int Defense, bool Spent, bool Refilled), double>();
                double total = 0d, series = 0d;
                int targetHp = Mathf.Max(1, Mathf.CeilToInt(target.HitPoints));
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
                    into.TryGetValue(key, out double prior); into[key] = prior + weight;
                }
                // What follows a host strike (no delegates: the tree is walked per outcome).
                void Then(StrikeThen then, WorthIt.DefenderProfile host, WorthIt.DefenderProfile enemy,
                    double weight, bool shock)
                {
                    if (then == StrikeThen.Finish || then == StrikeThen.EnemyAnswers && shock) Finish(host, weight);
                    else if (then == StrikeThen.EnemyAnswers) EnemyStrike(host, enemy, weight, false);
                    else HostStrike(host, enemy, weight, StrikeThen.Finish);
                }
                void HostStrike(WorthIt.DefenderProfile host, WorthIt.DefenderProfile enemy, double weight,
                    StrikeThen then)
                {
                    if (host.HitPoints <= 0 || enemy.HitPoints <= 0 || host.Range < distance)
                    { Then(then, host, enemy, weight, false); return; }
                    int limit = Mathf.Max(1, Mathf.CeilToInt(enemy.HitPoints));
                    double[] outcomes = BattleSimulationKernel.SharedExchangeDamageDistribution(
                        Mathf.RoundToInt(host.Attack), Mathf.RoundToInt(enemy.Defense), host.Abilities,
                        enemy.TypeTags, enemy.Abilities, limit);
                    total += weight * Secondary(Mathf.RoundToInt(host.Attack), host.Abilities, enemy);
                    var after = AfterAttack(host);
                    for (int damage = 0; damage < outcomes.Length; damage++)
                    {
                        double p = weight * outcomes[damage];
                        if (p <= 0d) continue;
                        total += p * damage / targetHp;
                        Then(then, after, Hit(enemy, damage), p,
                            BattleSimulationKernel.SuppressesTurn(damage, host.Abilities));
                    }
                }
                void EnemyStrike(WorthIt.DefenderProfile host, WorthIt.DefenderProfile enemy, double weight,
                    bool hostStillHasTurn)
                {
                    if (host.HitPoints <= 0 || enemy.HitPoints <= 0
                        || enemy.Range >= 0 && enemy.Range < distance)
                    {
                        if (hostStillHasTurn) HostStrike(host, enemy, weight, StrikeThen.Finish);
                        else Finish(host, weight);
                        return;
                    }
                    int limit = Mathf.Max(1, Mathf.CeilToInt(host.HitPoints));
                    double[] outcomes = BattleSimulationKernel.SharedExchangeDamageDistribution(
                        Mathf.RoundToInt(enemy.Attack), Mathf.RoundToInt(host.Defense), enemy.Abilities,
                        host.TypeTags, host.Abilities, limit);
                    var afterEnemy = AfterAttack(enemy);
                    for (int damage = 0; damage < outcomes.Length; damage++)
                    {
                        double p = weight * outcomes[damage];
                        if (p <= 0d) continue;
                        var hurt = Hit(host, damage);
                        bool shock = BattleSimulationKernel.SuppressesTurn(damage, enemy.Abilities);
                        // The host's own turn (if it still has one and was not Shock-stopped) follows
                        // the counterattack, whose own Shock does not cancel it.
                        StrikeThen rest = hostStillHasTurn && !shock ? StrikeThen.HostStrikes : StrikeThen.Finish;
                        if (BattleSimulationKernel.TriggersCounterattack(hurt.Abilities, false, hurt.HitPoints,
                            distance == 1, damage, enemy.Abilities))
                            HostStrike(hurt, afterEnemy, p, rest);
                        else Then(rest, hurt, afterEnemy, p, false);
                    }
                }
                for (int contact = 0; contact < AiConfigV2.equipContactCount; contact++)
                {
                    next.Clear();
                    foreach (var entry in mass)
                    {
                        var k = entry.Key;
                        if (!steps.TryGetValue(k, out var step))
                        {
                            var basis = k.Spent ? spent : armed;
                            if (k.Refilled && basis.Consumption?.HpSpentExcess > 0)
                                basis = new WorthIt.DefenderProfile(basis.Defense, basis.HasCeramicArmor, basis.TypeTags,
                                    basis.Attack, basis.HitPoints, basis.Initiative, basis.Abilities, basis.MaxHitPoints,
                                    range: basis.Range, consumption: basis.Consumption.AfterHpRefill());
                            var host = State(basis, k.Hp, k.Attack, k.Defense);
                            int hIni = host.Initiative + ctx.HostCommanderInitiative;
                            int eIni = target.Initiative + ctx.EnemyCommanderInitiative;
                            double first = hIni > eIni ? 1d : hIni == eIni ? 0.5d : 0d;
                            total = 0d;
                            into.Clear();
                            if (first > 0d) HostStrike(host, target, first, StrikeThen.EnemyAnswers);
                            if (first < 1d) EnemyStrike(host, target, 1d - first, true);
                            steps[k] = step = (total, into.ToArray());
                        }
                        series += entry.Value * step.Dealt;
                        foreach (var to in step.Next)
                        {
                            next.TryGetValue(to.Key, out double prior);
                            next[to.Key] = prior + entry.Value * to.Value;
                        }
                    }
                    (mass, next) = (next, mass);
                }
                return (float)(series + mass.Values.Sum() * survivorValue
                    / (AiConfigV2.equipCombatCardScale * AiConfigV2.equipCombatBodyScale));
            }
            if (ctx.KnownDistance.HasValue) return Series(ctx.KnownDistance.Value);
            // A distance reaches the series only through who can strike whom (each state's Range,
            // armed and spent) and adjacency (Counterattack): equal reach, equal series.
            var spentTarget = WorthIt.AfterAttack(target);
            int Reach(int d) => (armed.Range >= d ? 1 : 0) | (spent.Range >= d ? 2 : 0)
                | (target.Range < 0 || target.Range >= d ? 4 : 0)
                | (spentTarget.Range < 0 || spentTarget.Range >= d ? 8 : 0) | (d == 1 ? 16 : 0);
            int r1 = Reach(1), r2 = Reach(2), r3 = Reach(3), r4 = Reach(4);
            float v1 = Series(1);
            float v2 = r2 == r1 ? v1 : Series(2);
            float v3 = r3 == r2 ? v2 : Series(3);
            float v4 = r4 == r3 ? v3 : r4 == r2 ? v2 : Series(4);
            return AiConfigV2.equipDistanceShare1 * v1 + AiConfigV2.equipDistanceShare2 * v2
                + AiConfigV2.equipDistanceShare3 * v3 + AiConfigV2.equipDistanceShare4 * v4;
        }

        // What a ConsumableContactSeries host strike continues with: nothing more this contact, the
        // enemy's answer unless the strike Shock-stopped it, or the host's own (second) strike.
        private enum StrikeThen { Finish, EnemyAnswers, HostStrikes }

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
