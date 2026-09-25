using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Cards;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HERO OPERATIONAL-ROLE EVALUATOR  (Strategy V2 — spec §8)
    // ===========================================================================================
    //  ONE place that answers "is this hero better used commanding a field force, or kept for
    //  base / research / production support?". Reads ONLY canonical data already on the unit —
    //  CommandRating, Attack/Defense/HitPoints/Initiative, Fate, MoveMax, and the granted
    //  Researcher / Assembler / ApBonus support abilities. Never a card or display name.
    //
    //  The result is a PREFERENCE, not a prohibition: RaidAssembly / Housekeeping fall back to a
    //  SupportOperator rather than deadlock when it is the only usable hero.
    // ===========================================================================================
    public enum HeroOperationalRole
    {
        CombatLeader,     // well suited to lead a field army
        Flexible,         // acceptable for either use
        SupportOperator,  // production/research/base utility clearly outweighs combat leadership
    }

    public static class HeroRoleEvaluator
    {
        // Combat-leadership merit from canonical hero data: CommandRating (leadership capacity)
        // plus the hero's own AiPower contribution (HitPoints / Initiative / Resistance / Fate —
        // heroes carry no Attack/Defense). Higher = better field commander.
        public static float CombatLeadershipScore(UnitData hero)
        {
            if (hero == null || !hero.IsHero)
                return 0f;
            float ownContribution = AiPower.ToPowerUnit(hero).BasePower;
            float staticLeadership = hero.CommandRating * AiConfigV2.heroRoleCommandWeight
                 + ownContribution * AiConfigV2.heroRoleCombatContributionWeight;
            // A commander only contributes this leadership to a field force it can keep pace with.
            // Reuse the canonical MobileCombat movement line instead of inventing another cutoff:
            // MoveMax 2 is materially worse than 5, while faster heroes receive no extra inflation.
            float mobility = Mathf.Clamp01(
                (float)Mathf.Max(0, hero.MoveMax) / Mathf.Max(1, AiConfigV2.mobileCombatMoveMax));
            return staticLeadership * mobility;
        }

        // A "home" hero: either a canonical production/research vocation granted by the hero's own
        // abilities, an AP-generating vocation (ApBonus — a hero built to boost the owner's turn,
        // not to travel), or simply too slow to keep pace with a field force at all
        // (homeHeroMoveMaxThreshold). The MoveMax floor applies regardless of ability tags — a
        // slow hero with no support abilities is still a home hero by design.
        public static bool HasSupportVocation(UnitData hero) =>
            hero != null && hero.IsHero
            && (hero.HasAbility(UnitAbilities.Researcher) || hero.HasAbility(UnitAbilities.Assembler)
                || hero.HasAbility(UnitAbilities.ApBonus)
                || hero.MoveMax <= AiConfigV2.homeHeroMoveMaxThreshold);

        public static HeroOperationalRole Classify(UnitData hero)
        {
            if (hero == null || !hero.IsHero)
                return HeroOperationalRole.Flexible;

            bool support = HasSupportVocation(hero);
            float combat = CombatLeadershipScore(hero);

            if (support)
                return combat < AiConfigV2.heroRoleFlexibleCombatFloor
                    ? HeroOperationalRole.SupportOperator
                    : HeroOperationalRole.Flexible;

            return combat >= AiConfigV2.heroRoleCombatLeaderFloor
                ? HeroOperationalRole.CombatLeader
                : HeroOperationalRole.Flexible;
        }

        // Preference rank for leading a FIELD force: CombatLeader (2) > Flexible (1) >
        // SupportOperator (0).
        public static int FieldCommandPreference(UnitData hero)
        {
            return RolePreference(Classify(hero));
        }

        // ---- THE commander evaluation (strike force step 2) -------------------------------------
        //
        // "Which hero should lead THIS formation" has one answer for every caller — same-hex
        // assembly (GroundCombatDonorPolicy), Housekeeping's commander reorder and bench pick, the
        // strike-force planner. A hero never fights; it decides how many bodies fit (CommandRating)
        // and adds its initiative and Fate to every battle (WorthIt.SideCommander). So the hero is
        // judged by the fight its formation would have:
        //   1. win chance of the best bodies that fit under it against the opposition
        //      (differences within one Monte-Carlo trial are a tie);
        //   2. body slots — capacity is the lasting value when the fight does not separate them
        //      (no known opposition, or a win either way);
        //   3. the static field-command signals: role, combat leadership, CommandRating, Fate;
        //   4. a caller-supplied stable key.

        // What one hero would make of a formation.
        public readonly struct CommandProjection
        {
            public readonly float WinChance;
            public readonly int BodySlots;

            public CommandProjection(float winChance, int bodySlots)
            {
                WinChance = winChance;
                BodySlots = bodySlots;
            }
        }

        // `bodies` — the fighting bodies the formation can field (the best ones fill the slots);
        // `otherHeroes` — heroes besides the commander that also sit in the army and take slots;
        // `opposition` — the fight to judge against (empty: no known fight, win 1).
        public static CommandProjection ProjectCommand(int commandRating, int otherHeroes,
            WorthIt.SideCommander commander, IEnumerable<WorthIt.DefenderProfile> bodies,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus)
        {
            int slots = Math.Max(0, commandRating - 1 - Math.Max(0, otherHeroes));
            List<WorthIt.DefenderProfile> roster = (bodies ?? Array.Empty<WorthIt.DefenderProfile>())
                .Where(p => p.IsGroundCombatant)
                .OrderByDescending(WorthIt.CombatValue)
                .Take(slots)
                .ToList();
            float win = opposition == null || opposition.Count == 0
                ? 1f
                : WorthIt.EstimateSequential(roster, commander, opposition, defenderHexDefenseBonus).WinChance;
            return new CommandProjection(win, slots);
        }

        // One commander candidate, whatever form the caller holds the hero in.
        public readonly struct CommandCandidate
        {
            public readonly CommandProjection Projection;
            public readonly int RolePreference;
            public readonly float Leadership;
            public readonly int CommandRating;
            public readonly int Fate;
            public readonly int StableKey;

            public CommandCandidate(CommandProjection projection, int rolePreference, float leadership,
                int commandRating, int fate, int stableKey)
            {
                Projection = projection;
                RolePreference = rolePreference;
                Leadership = leadership;
                CommandRating = commandRating;
                Fate = fate;
                StableKey = stableKey;
            }
        }

        public static CommandCandidate Candidate(UnitData hero, CommandProjection projection, int stableKey) =>
            new CommandCandidate(projection, FieldCommandPreference(hero), CombatLeadershipScore(hero),
                hero?.CommandRating ?? 0, hero?.FateMax ?? 0, stableKey);

        public static int RolePreference(HeroOperationalRole role) =>
            role == HeroOperationalRole.CombatLeader ? 2 : role == HeroOperationalRole.Flexible ? 1 : 0;

        // Differences smaller than one Monte-Carlo trial (1/25) are noise, not a better commander.
        private const float CommandWinEpsilon = 0.05f;

        // Best commander first.
        public static int CompareCandidates(CommandCandidate a, CommandCandidate b)
        {
            float dw = b.Projection.WinChance - a.Projection.WinChance;
            if (Math.Abs(dw) >= CommandWinEpsilon) return dw > 0f ? 1 : -1;
            int c = b.Projection.BodySlots.CompareTo(a.Projection.BodySlots);
            if (c != 0) return c;
            c = b.RolePreference.CompareTo(a.RolePreference);
            if (c != 0) return c;
            c = b.Leadership.CompareTo(a.Leadership);
            if (c != 0) return c;
            c = b.CommandRating.CompareTo(a.CommandRating);
            if (c != 0) return c;
            c = b.Fate.CompareTo(a.Fate);
            if (c != 0) return c;
            return a.StableKey.CompareTo(b.StableKey);
        }
    }
}
