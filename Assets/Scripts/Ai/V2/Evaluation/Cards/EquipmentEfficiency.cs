using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  EQUIPMENT EFFICIENCY  (Strategy V2 — the ONE bonus-weight table for equipment / mutators)
    // ===========================================================================================
    //  An attachment is worth the change it makes to the host's efficiency:
    //      E = Attack + 0.5 Defense + 0.5 HP + 2 min(Range, cap)          (AiConfigV2.equip*)
    //      value = E(host after) - E(host before)  [+ ability and flat terms]
    //  Every bonus has ONE weight here, scaled by the host's own characteristics (an item that
    //  sets Attack to 5 is worth 3 to a scout and 1 to a heavy infantryman; Regeneration is worth
    //  more on a tall, armoured unit). Enemy knowledge only enters as the SHARE of known enemies an
    //  ability can hit (Hyperkinetic <- armoured, Scorcher/Pyrokinetic <- bio, AA <- aircraft) and
    //  as mission context multipliers (the host's current assignment). The design table with
    //  worked examples is docs/ai-v2-equipment-efficiency-table.md.
    //
    //  Pure arithmetic over plain structs: no snapshot, registry or engine type is read here, so
    //  the table is EditMode-testable. StrategicCardEvaluator.ScoreEquipmentDelta builds the
    //  context from the live world and converts E to card-value units (equipCardValuePerE).
    // ===========================================================================================
    internal readonly struct EfficiencyStats
    {
        public readonly int Attack, Defense, HitPoints, Range, Move, Initiative, ActivationAp, Fate;

        public EfficiencyStats(int attack, int defense, int hitPoints, int range, int move,
            int initiative, int activationAp, int fate)
        {
            Attack = attack; Defense = defense; HitPoints = hitPoints; Range = range; Move = move;
            Initiative = initiative; ActivationAp = activationAp; Fate = fate;
        }
    }

    internal sealed class EfficiencyContext
    {
        public bool IsHero;
        public float ArmyAttack = AiConfigV2.equipHeroArmyAttackDefault;   // hero: the army's mean Attack
        public float ArmoredShare = AiConfigV2.equipDefaultArmoredShare;
        public float BioShare = AiConfigV2.equipDefaultBioShare;
        public float AirShare = AiConfigV2.equipDefaultAirShare;
        public float SplashTargets = 1f;      // enemy bodies Splash can reach beside the primary (0..2)
        public float SecondaryNeighbors = 1f; // enemy bodies beside the primary at all (shared by Splash and Scorcher)
        internal bool HostHasSplash;          // set by Delta: Splash already takes secondary slots
        public bool StealthUsable;            // the host is (or becomes) a scout: it really enters stealth
        public bool IncludeStealthTrait = true;   // a deploy chain prices Stealth/Recce in its own scorer
        public bool IsFacilityOperator;
        public float OffenseMult = 1f, DefenseMult = 1f, SkillMult = 1f;
        public int OtherSpeedMin = int.MaxValue;  // slowest OTHER member of the host's army (MaxValue: alone)
        public Func<string, int> Carriers;        // saturating family -> own units already carrying it (legacy table only)

        // ---- signed utility U (EquipmentEfficiency.Utility) -----------------------------------------------
        public IReadOnlyList<Game.Combat.WorthIt.DefenderProfile> Targets;  // enemy profiles; empty: catalog prior
        public IReadOnlyCollection<UnitTypeTag> HostTags;
        public int HpSpent;                       // wound already carried (never healed by the attachment)
        public int? KnownDistance;                // a known contact distance replaces the geometry prior
        public int HostCommanderInitiative, EnemyCommanderInitiative;
        public float ExpectedActivations = AiConfigV2.equipExpectedActivations;
        public int RouteLength;                   // 0: no known route (speed uses the proxy)
        public float OtherArmyActivationAp;       // activation AP of the army's OTHER members (army AP = sum of members)
        public int OtherRecceRadius, OtherSpotStrength, OtherAntiAirCarriers;  // the host's own army
        public float UsefulDarkFraction = AiConfigV2.equipUsefulDarkDefault;
        public float DetectionRelevance;          // 0 unless a hidden target is actually known
        public int HideStrength = AiConfigV2.equipHideStrengthDefault;
        public float StealthRisk = AiConfigV2.equipStealthRiskFloor;
        // Witnessed expected outputs of a Fate-lifted operator: (utility, cost if success, P before, P after).
        public IReadOnlyList<(float Utility, float CostIfSuccess, float PBefore, float PAfter)> OperatorOutputs;
    }

    internal readonly struct EfficiencyBreakdown
    {
        public readonly float Offense, Defense, Skill, Flat;
        public float Total => Offense + Defense + Skill + Flat;
        public float Combat => Offense + Defense;
        public float Tactical => Skill + Flat;

        public EfficiencyBreakdown(float offense, float defense, float skill, float flat)
        { Offense = offense; Defense = defense; Skill = skill; Flat = flat; }

        public override string ToString() =>
            $"dE={Total:0.##} (off {Offense:0.##} def {Defense:0.##} skill {Skill:0.##} flat {Flat:0.##})";
    }

    internal static partial class EquipmentEfficiency
    {
        private enum Group { Offense, Defense, Skill, Flat }

        // ---- base efficiency ----------------------------------------------------------------------
        internal static float Base(int attack, int defense, int hitPoints, int range) =>
            attack * AiConfigV2.equipWeightAttack
            + defense * AiConfigV2.equipWeightDefense
            + hitPoints * AiConfigV2.equipWeightHitPoints
            + Mathf.Min(range, AiConfigV2.equipRangeCap) * AiConfigV2.equipWeightRange;

        internal static float Base(EfficiencyStats s) => Base(s.Attack, s.Defense, s.HitPoints, s.Range);

        // ---- price of E / supply ---------------------------------------------------------------------
        internal static float ToCardValue(float efficiency) => efficiency * AiConfigV2.equipCardValuePerE;

        // ---- the table -----------------------------------------------------------------------------------
        internal static EfficiencyBreakdown Delta(EfficiencyStats before, IReadOnlyCollection<string> beforeAbilities,
            EfficiencyStats after, IReadOnlyCollection<string> afterAbilities, EfficiencyContext ctx)
        {
            ctx = ctx ?? new EfficiencyContext();
            beforeAbilities = beforeAbilities ?? Array.Empty<string>();
            afterAbilities = afterAbilities ?? Array.Empty<string>();
            float[] sum = new float[4];
            ctx.HostHasSplash = afterAbilities.Contains(UnitAbilities.Splash);

            // Stats. A hero is the army's container, not a fighter: no Attack / Range value.
            if (!ctx.IsHero)
                sum[(int)Group.Offense] += (after.Attack - before.Attack) * AiConfigV2.equipWeightAttack
                    + (Mathf.Min(after.Range, AiConfigV2.equipRangeCap)
                        - Mathf.Min(before.Range, AiConfigV2.equipRangeCap)) * AiConfigV2.equipWeightRange;
            float frailty = AiConfigV2.equipDefenseHpReference / Mathf.Max(1f, before.Defense + before.HitPoints);
            sum[(int)Group.Defense] += ((after.Defense - before.Defense) * AiConfigV2.equipWeightDefense
                + (after.HitPoints - before.HitPoints) * AiConfigV2.equipWeightHitPoints) * frailty;

            // Abilities: kept ones change with the stats, gained ones saturate with their carriers,
            // lost ones are given up at the old stats.
            foreach (string a in afterAbilities.Where(beforeAbilities.Contains))
            {
                Add(sum, a, after, ctx, 1f);
                Add(sum, a, before, ctx, -1f);
            }
            foreach (string a in afterAbilities.Where(x => !beforeAbilities.Contains(x)))
                Add(sum, a, after, ctx, Saturation(a, ctx));
            foreach (string a in beforeAbilities.Where(x => !afterAbilities.Contains(x)))
                Add(sum, a, before, ctx, -1f);

            // Flat bonuses.
            float flat = 0f;
            float eRef = ctx.IsHero
                ? Base(Mathf.RoundToInt(ctx.ArmyAttack), before.Defense, before.HitPoints, 2) : Base(before);
            int speedBefore = Mathf.Min(before.Move, ctx.OtherSpeedMin);
            int speedAfter = Mathf.Min(after.Move, ctx.OtherSpeedMin);
            flat += AiConfigV2.equipMoveFactor * eRef * (speedAfter - speedBefore)
                * (3f / Mathf.Max(1, speedBefore));
            if (!ctx.IsHero)
                flat += AiConfigV2.equipInitiativeFactor * after.Attack * (after.Initiative - before.Initiative);
            flat += AiConfigV2.equipActivationApValue * (before.ActivationAp - after.ActivationAp);
            if (ctx.IsHero)
            {
                float dFate = after.Fate - before.Fate;
                flat += dFate * AiConfigV2.equipHeroFateFactor * ctx.ArmyAttack;
                if (ctx.IsFacilityOperator && dFate > 0f)
                    flat += dFate * AiConfigV2.equipOperatorFateGain;
            }
            sum[(int)Group.Flat] += flat;

            return new EfficiencyBreakdown(
                sum[(int)Group.Offense] * ctx.OffenseMult,
                sum[(int)Group.Defense] * ctx.DefenseMult,
                sum[(int)Group.Skill] * ctx.SkillMult,
                sum[(int)Group.Flat]);
        }

        private static void Add(float[] sum, string ability, EfficiencyStats s, EfficiencyContext ctx, float weight)
        {
            if (!TryValue(ability, s, ctx, out Group group, out float value))
                return;
            sum[(int)group] += value * weight;
        }

        // The family saturation of a GAINED ability (1 / (1 + units already carrying it)).
        private static float Saturation(string ability, EfficiencyContext ctx)
        {
            string family = DevelopmentDiversity.FamilyOf(ability);
            if (family == null || ctx.Carriers == null)
                return 1f;
            return 1f / (1f + Mathf.Max(0, ctx.Carriers(family)));
        }

        // Which group an ability's value belongs to and what it is worth on host stats `s`.
        // false: the ability has no row here (StrategicEffectRegistry prices it, e.g. global effects).
        internal static bool TryValue(string ability, EfficiencyStats s, EfficiencyContext ctx,
            out string group, out float value)
        {
            bool ok = TryValue(ability, s, ctx, out Group g, out value);
            group = g.ToString();
            return ok;
        }

        private static bool TryValue(string ability, EfficiencyStats s, EfficiencyContext ctx,
            out Group group, out float value)
        {
            group = Group.Offense;
            value = 0f;
            bool hero = ctx.IsHero;
            float a = s.Attack;
            switch (ability)
            {
                case UnitAbilities.Regeneration:
                    group = Group.Defense;
                    value = AiConfigV2.equipRegenerationFactor * (s.Defense + s.HitPoints);
                    return true;
                case UnitAbilities.CeramicArmor:
                    group = Group.Defense;
                    value = AiConfigV2.equipCeramicFactor * s.HitPoints;
                    return true;
                case UnitAbilities.CriticalDamage:
                    value = hero ? 0f : AiConfigV2.equipCriticalFactor * a;
                    return true;
                case UnitAbilities.Splash:
                    value = hero ? 0f : AiConfigV2.equipSplashFactor * a * ctx.SplashTargets;
                    return true;
                case UnitAbilities.Scorcher:
                    // One random neighbour: only what Splash (up to 2) leaves of the neighbours.
                    float slots = Mathf.Clamp(ctx.SecondaryNeighbors
                        - (ctx.HostHasSplash ? Mathf.Min(2f, ctx.SecondaryNeighbors) : 0f), 0f, 1f);
                    value = hero ? 0f : AiConfigV2.equipScorcherFactor * a * ctx.BioShare * slots;
                    return true;
                case UnitAbilities.Hyperkinetic:
                    value = hero ? 0f : AiConfigV2.equipHyperkineticFactor * a * ctx.ArmoredShare;
                    return true;
                case UnitAbilities.Pyrokinetic:
                    value = hero ? 0f : AiConfigV2.equipPyrokineticFactor * a * ctx.BioShare;
                    return true;
                case UnitAbilities.ShockAttack:
                    value = hero ? 0f : AiConfigV2.equipShockFactor * a * s.Initiative;
                    return true;
                case UnitAbilities.AntiAir:
                    value = hero ? 0f : AiConfigV2.equipAntiAirFactor * a * ctx.AirShare;
                    return true;
                case UnitAbilities.RapidReaction:
                    group = Group.Flat;
                    value = AiConfigV2.equipRapidReactionValue;
                    return true;
            }
            if (AbilityParams.TryGetStealthLevel(ability, out _))
            {
                group = Group.Skill;
                value = ctx.IncludeStealthTrait
                    ? (hero ? AiConfigV2.equipHeroStealthFactor * (s.Defense + s.HitPoints)
                            : AiConfigV2.equipStealthFactor * a)
                        * (ctx.StealthUsable ? 1f : AiConfigV2.equipStealthUnusedShare)
                    : 0f;
                return true;
            }
            if (AbilityParams.AbilitiesHaveAnyRecce(new[] { ability }))
            {
                group = Group.Skill;
                value = ctx.IncludeStealthTrait ? AiConfigV2.equipRecceValue : 0f;
                return true;
            }
            return false;
        }

        // Abilities the signed utility (Utility) prices; everything else stays with StrategicEffectRegistry.
        // The answer depends on the ability name alone. Berserk, Shock and the damage modifiers are read
        // from the combat kernel; Stealth/Recce through the option / vision / detection terms; RapidReaction
        // through the effective activation AP; AntiAir through the legal reactions.
        private static readonly HashSet<string> s_priced = new HashSet<string>
        {
            UnitAbilities.Regeneration, UnitAbilities.CeramicArmor, UnitAbilities.CriticalDamage,
            UnitAbilities.Splash, UnitAbilities.Scorcher, UnitAbilities.Hyperkinetic, UnitAbilities.Pyrokinetic,
            UnitAbilities.ShockAttack, UnitAbilities.AntiAir, UnitAbilities.RapidReaction, UnitAbilities.Berserk,
        };

        internal static bool IsPriced(string ability)
        {
            if (ability == null)
                return false;
            return s_priced.Contains(ability) || AbilityParams.TryGetStealthLevel(ability, out _)
                || AbilityParams.AbilitiesHaveAnyRecce(new[] { ability });
        }

        // ---- mission context ---------------------------------------------------------------------------
        // The host's assignment shifts which bonuses matter; `hexDefenseBonus` is the target hex's
        // known defence (Attack / Raid): an Attack bonus has to cover it.
        internal static void ApplyMission(EfficiencyContext ctx, MissionKind? kind, float hexDefenseBonus)
        {
            if (ctx == null || !kind.HasValue)
                return;
            switch (kind.Value)
            {
                case MissionKind.Attack:
                    ctx.OffenseMult = AiConfigV2.equipAttackOffenseMult
                        * (1f + AiConfigV2.equipHexDefenseOffensePerPoint * Mathf.Max(0f, hexDefenseBonus));
                    ctx.DefenseMult = AiConfigV2.equipAttackDefenseMult;
                    break;
                case MissionKind.Raid:
                    ctx.OffenseMult = AiConfigV2.equipRaidOffenseMult
                        * (1f + AiConfigV2.equipHexDefenseOffensePerPoint * Mathf.Max(0f, hexDefenseBonus));
                    break;
                case MissionKind.ActiveDefence:
                    ctx.DefenseMult = AiConfigV2.equipActiveDefenceDefenseMult;
                    break;
                case MissionKind.Scout:
                    ctx.SkillMult = AiConfigV2.equipScoutSkillMult;
                    break;
            }
        }
    }
}
