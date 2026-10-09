using System.Collections.Generic;
using Game.Cards;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  PROJECTED PHYSICAL STATE  (ARCH-02 §15 / §57 / §58)
    // ===========================================================================================
    //  The ONE joint physical-capacity model for a multi-chain materialization portfolio, shared
    //  by MaterializationPortfolioSolver (Phase A) and ReactionMaterializationSolver (reaction
    //  closure). Consumption (cards / generation / AP / H-E-M-T) stays in
    //  MaterializationConsumptionState; this owns the recipient/hero/hand-slot side that the
    //  consumption model does not model.
    //
    //  CAPACITY RULE — mirrors ArmyData.ComputeCapacity + CardPlayExecutor exactly:
    //    FIELD army:
    //    · an EXISTING hero in the recipient governs capacity (its CommandRating is already baked
    //      into the frozen live Capacity we seed with);
    //    · otherwise the FIRST hero THIS portfolio adds governs capacity (== its CommandRating —
    //      a replacement of the nominal value, even when CommandRating is zero);
    //    · otherwise the nominal base (field 2).
    //    GARRISON (kept ordered by CommandRating, ArmyData.NormalizeRoster): the HIGHEST
    //    CommandRating among existing and added heroes governs; no hero -> nominal garrison base.
    //  A recipient's projected roster fits when projected member count (EVERY existing member,
    //  heroes included, plus everything added) <= projected capacity.
    //  Hand-slot peaks of every generate chain are summed against the free hand.
    // ===========================================================================================
    internal sealed class ProjectedPhysicalState
    {
        // Obtain the unseeded field capacity from the live domain rule rather than
        // independently hardcoding its default in the V2 projection.
        private static readonly int FieldBaseCapacity =
            ArmyData.ComputeCapacity(System.Array.Empty<UnitData>(), isGarrison: false);

        // Canonical recipient identity for a plan's deploy target. NewArmy is keyed by StableKey so
        // two distinct fresh-army plans never share a projection; every other kind by army id.
        internal static string RecipientKey(MaterializationPlan p)
        {
            if (p == null) return "?";
            if (p.Kind == MaterializationChainKind.GenerateAttachUpgrade)
                return "upgrade:" + p.StableKey;
            switch (p.Deploy.Kind)
            {
                case DeploymentKind.ExistingArmy: return "existing:" + (p.Deploy.Army?.Id ?? -1);
                case DeploymentKind.Garrison:     return "garrison:" + (p.Deploy.Army?.Id ?? -1);
                case DeploymentKind.ReusableShell:return "shell:" + (p.Deploy.Army?.Id ?? -1);
                default:                          return "new:" + p.StableKey;
            }
        }

        // EXECUTION ORDER. The joint projection above proves a portfolio fits as a FINAL roster;
        // Phase A then runs ONE chain per pass and re-plans on the refreshed world. A chain may
        // therefore only be picked if it is legal against the recipient AS IT IS NOW (the same
        // ArmyData.CanFitAdditionalCard the executor re-checks): a unit that fits only once a
        // portfolio hero has landed waits for that hero, which is always legal first (it sets the
        // capacity every other member of the portfolio relies on). Upgrades and fresh armies have
        // no live roster to outgrow.
        internal static bool FitsLiveNow(MaterializationPlan p)
        {
            if (p == null) return false;
            if (p.Kind == MaterializationChainKind.GenerateAttachUpgrade || p.AttackRefitPrimaryId.HasValue)
                return true;
            CardDefinition d = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
            if (d == null) return true;
            switch (p.Deploy.Kind)
            {
                case DeploymentKind.ExistingArmy:
                case DeploymentKind.Garrison:
                case DeploymentKind.ReusableShell:
                    return p.Deploy.Army != null && p.Deploy.Army.CanFitAdditionalCard(d);
                default:
                    return true;
            }
        }

        private static bool IsHeroPlan(MaterializationPlan p)
        {
            CardDefinition d = p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
            return d != null && d.cardType == CardType.Hero;
        }

        private static int HeroCommandRating(MaterializationPlan p)
        {
            CardDefinition d = p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
            // The physical ArmyData.ComputeCapacity uses CommandRating verbatim. Clamping to 1
            // made a zero-command Hero look deployable in planning, then fail in ArmyActions.
            return d != null ? d.commandRating : 0;
        }

        private struct Recipient
        {
            public bool IsGarrison;
            public int BaseNonHero;         // existing non-hero members
            public int BaseHeroCount;       // existing heroes: every one occupies a slot
            public int BaseMaxHeroCr;       // best existing CommandRating; valid only when BaseHeroCount > 0
            public int BaseNominalCapacity; // frozen live Capacity: governs the field base-hero case
            public int AddedNonHero;
            public int AddedHeroes;
            public int FirstAddedHeroCr;    // valid only when AddedHeroes > 0; zero is a legal rating
            public int AddedMaxHeroCr;      // best added CommandRating; valid only when AddedHeroes > 0
        }

        private readonly Dictionary<string, Recipient> _recipients =
            new Dictionary<string, Recipient>();
        private int _handSlotsFree = int.MaxValue;
        private int _handSlotsUsed;
        private readonly HashSet<UnitData> _upgradedUnits = new HashSet<UnitData>();

        internal void SeedHandSlots(int free) => _handSlotsFree = Mathf.Max(0, free);

        // Seed an EXISTING recipient once from the live container: the one place both solvers
        // read these facts, so Phase A and Reaction can never disagree on a recipient's base.
        // Fresh (NewArmy / ReusableShell) recipients need no seed; they default to an empty
        // non-garrison container.
        internal void SeedRecipient(string key, ArmyData live)
        {
            if (live == null || _recipients.ContainsKey(key)) return;
            int heroes = 0, maxCr = 0, nonHero = 0;
            foreach (UnitData m in live.Members)
            {
                if (m == null) continue;
                if (!m.IsHero) { nonHero++; continue; }
                maxCr = heroes == 0 ? m.CommandRating : Mathf.Max(maxCr, m.CommandRating);
                heroes++;
            }
            _recipients[key] = new Recipient
            {
                IsGarrison = live.IsGarrison,
                BaseNonHero = nonHero,
                BaseHeroCount = heroes,
                BaseMaxHeroCr = maxCr,
                // Preserve the physical capacity exactly. A zero-CommandRating existing hero
                // yields capacity zero, not one; a planner must not make that army expandable.
                BaseNominalCapacity = live.Capacity,
            };
        }

        private Recipient Get(string key) =>
            _recipients.TryGetValue(key, out Recipient r) ? r
            : new Recipient { IsGarrison = false, BaseNominalCapacity = FieldBaseCapacity };

        // ARCH-02 §58 — the fit test goes through ArmyData's shared domain projection, the same primitive
        // physical deployment uses, so planner and executor cannot disagree.
        private static bool RosterFits(in Recipient r)
        {
            int members = r.BaseNonHero + r.BaseHeroCount + r.AddedNonHero + r.AddedHeroes;
            if (r.IsGarrison)
                return members <= ArmyData.ComputeProjectedGarrisonCapacity(r.BaseNominalCapacity,
                    r.BaseHeroCount, r.BaseMaxHeroCr, r.AddedHeroes, r.AddedMaxHeroCr);
            return ArmyData.ProjectedRosterFits(r.BaseNominalCapacity, r.BaseHeroCount > 0,
                members, r.AddedHeroes, r.FirstAddedHeroCr);
        }

        // Count is the identity sentinel, not the CommandRating (which can be zero).
        private static void AddHero(ref Recipient r, int commandRating)
        {
            bool first = r.AddedHeroes == 0;
            r.AddedHeroes++;
            if (first) r.FirstAddedHeroCr = commandRating;
            r.AddedMaxHeroCr = first ? commandRating : Mathf.Max(r.AddedMaxHeroCr, commandRating);
        }

        internal readonly struct Token
        {
            public readonly string Key;
            public readonly bool IsHero;
            public readonly bool WasFirstAddedHero;
            public readonly int HandPeak;
            public readonly UnitData UpgradedUnit;
            // The recipient hero facts BEFORE this Add: Remove restores them verbatim (both
            // solvers push/pop in strict LIFO), so a repeated maximum survives removing one copy.
            public readonly int PrevFirstAddedHeroCr;
            public readonly int PrevAddedMaxHeroCr;

            public Token(string key, bool isHero, bool wasFirstAddedHero, int handPeak,
                UnitData upgradedUnit = null, int prevFirstAddedHeroCr = 0, int prevAddedMaxHeroCr = 0)
            {
                Key = key;
                IsHero = isHero;
                WasFirstAddedHero = wasFirstAddedHero;
                HandPeak = handPeak;
                UpgradedUnit = upgradedUnit;
                PrevFirstAddedHeroCr = prevFirstAddedHeroCr;
                PrevAddedMaxHeroCr = prevAddedMaxHeroCr;
            }
        }

        // Would `p` still be physically placeable on top of everything already added?
        internal bool CanAdd(MaterializationPlan p)
        {
            if (p == null) return false;
            int peak = Mathf.Max(0, p.HandSlotsNeededAtPeak);
            if (_handSlotsUsed + peak > _handSlotsFree)
                return false;
            if (p.Kind == MaterializationChainKind.GenerateAttachUpgrade)
                return p.UpgradeTargetUnit == null || !_upgradedUnits.Contains(p.UpgradeTargetUnit);

            string key = RecipientKey(p);
            Recipient r = Get(key);
            bool hero = IsHeroPlan(p);
            if (hero) AddHero(ref r, HeroCommandRating(p));   // r is a copy: CanAdd never mutates state
            else r.AddedNonHero++;
            return RosterFits(r);
        }

        internal Token Add(MaterializationPlan p)
        {
            if (p.Kind == MaterializationChainKind.GenerateAttachUpgrade)
            {
                if (p.UpgradeTargetUnit != null)
                    _upgradedUnits.Add(p.UpgradeTargetUnit);
                int upgradePeak = Mathf.Max(0, p.HandSlotsNeededAtPeak);
                _handSlotsUsed += upgradePeak;
                return new Token(RecipientKey(p), false, false, upgradePeak, p.UpgradeTargetUnit);
            }

            string key = RecipientKey(p);
            Recipient r = Get(key);
            bool hero = IsHeroPlan(p);
            bool firstHero = hero && r.AddedHeroes == 0;
            int prevFirst = r.FirstAddedHeroCr, prevMax = r.AddedMaxHeroCr;
            if (hero) AddHero(ref r, HeroCommandRating(p));
            else r.AddedNonHero++;
            _recipients[key] = r;
            int peak = Mathf.Max(0, p.HandSlotsNeededAtPeak);
            _handSlotsUsed += peak;
            return new Token(key, hero, firstHero, peak, null, prevFirst, prevMax);
        }

        internal void Remove(in Token t)
        {
            if (t.UpgradedUnit != null)
                _upgradedUnits.Remove(t.UpgradedUnit);
            if (_recipients.TryGetValue(t.Key, out Recipient r))
            {
                if (t.IsHero)
                {
                    r.AddedHeroes = Mathf.Max(0, r.AddedHeroes - 1);
                    r.FirstAddedHeroCr = t.PrevFirstAddedHeroCr;
                    r.AddedMaxHeroCr = t.PrevAddedMaxHeroCr;
                }
                else r.AddedNonHero = Mathf.Max(0, r.AddedNonHero - 1);
                _recipients[t.Key] = r;
            }
            _handSlotsUsed = Mathf.Max(0, _handSlotsUsed - t.HandPeak);
        }
    }
}
