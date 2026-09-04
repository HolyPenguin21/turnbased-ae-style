using System.Collections.Generic;
using Game.Cards;
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
    //    · an EXISTING hero in the recipient governs capacity (its CommandRating is already baked
    //      into the frozen ArmySnapshot.Capacity we seed with);
    //    · otherwise the FIRST hero THIS portfolio adds governs capacity (== its CommandRating —
    //      a replacement of the nominal value, never nominal + 1);
    //    · otherwise the nominal base (garrison 4 / field 2).
    //  A recipient's projected roster fits when projected member count <= projected capacity.
    //  Hand-slot peaks of every generate chain are summed against the free hand.
    // ===========================================================================================
    internal sealed class ProjectedPhysicalState
    {
        private const int FieldBaseCapacity = 2;
        private const int GarrisonBaseCapacity = 4;

        // Canonical recipient identity for a plan's deploy target. NewArmy is keyed by StableKey so
        // two distinct fresh-army plans never share a projection; every other kind by army id.
        internal static string RecipientKey(MaterializationPlan p)
        {
            if (p == null) return "?";
            switch (p.Deploy.Kind)
            {
                case DeploymentKind.ExistingArmy: return "existing:" + (p.Deploy.Army?.Id ?? -1);
                case DeploymentKind.Garrison:     return "garrison:" + (p.Deploy.Army?.Id ?? -1);
                case DeploymentKind.ReusableShell:return "shell:" + (p.Deploy.Army?.Id ?? -1);
                default:                          return "new:" + p.StableKey;
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
            return d != null ? Mathf.Max(1, d.commandRating) : 1;
        }

        private struct Recipient
        {
            public bool IsGarrison;
            public int BaseNonHero;         // existing non-hero members
            public bool BaseHasHero;        // an existing hero (its CR is already in BaseNominalCapacity)
            public int BaseNominalCapacity; // frozen ArmySnapshot.Capacity — governs the base-hero case
            public int AddedNonHero;
            public int AddedHeroes;
            public int FirstAddedHeroCr;    // 0 until the first hero is added by this projection
        }

        private readonly Dictionary<string, Recipient> _recipients =
            new Dictionary<string, Recipient>();
        private int _handSlotsFree = int.MaxValue;
        private int _handSlotsUsed;

        internal void SeedHandSlots(int free) => _handSlotsFree = Mathf.Max(0, free);

        // Seed an EXISTING recipient once from world facts. Fresh (NewArmy / ReusableShell)
        // recipients need no seed — they default to an empty non-garrison container.
        internal void SeedRecipient(string key, bool isGarrison, int baseNonHero, bool baseHasHero,
            int baseNominalCapacity)
        {
            if (_recipients.ContainsKey(key)) return;
            _recipients[key] = new Recipient
            {
                IsGarrison = isGarrison,
                BaseNonHero = Mathf.Max(0, baseNonHero),
                BaseHasHero = baseHasHero,
                BaseNominalCapacity = Mathf.Max(1, baseNominalCapacity),
            };
        }

        private Recipient Get(string key) =>
            _recipients.TryGetValue(key, out Recipient r) ? r
            : new Recipient { IsGarrison = false, BaseNominalCapacity = FieldBaseCapacity };

        private static bool RosterFits(in Recipient r)
        {
            int members = r.BaseNonHero + (r.BaseHasHero ? 1 : 0) + r.AddedNonHero + r.AddedHeroes;
            int cap = r.BaseHasHero ? r.BaseNominalCapacity
                : r.AddedHeroes > 0 ? r.FirstAddedHeroCr
                : (r.IsGarrison ? GarrisonBaseCapacity : FieldBaseCapacity);
            return members <= cap;
        }

        internal readonly struct Token
        {
            public readonly string Key;
            public readonly bool IsHero;
            public readonly bool WasFirstAddedHero;
            public readonly int HandPeak;

            public Token(string key, bool isHero, bool wasFirstAddedHero, int handPeak)
            {
                Key = key;
                IsHero = isHero;
                WasFirstAddedHero = wasFirstAddedHero;
                HandPeak = handPeak;
            }
        }

        // Would `p` still be physically placeable on top of everything already added?
        internal bool CanAdd(MaterializationPlan p)
        {
            if (p == null) return false;
            int peak = Mathf.Max(0, p.HandSlotsNeededAtPeak);
            if (_handSlotsUsed + peak > _handSlotsFree)
                return false;

            string key = RecipientKey(p);
            Recipient r = Get(key);
            bool hero = IsHeroPlan(p);
            if (hero)
            {
                r.AddedHeroes++;
                if (r.FirstAddedHeroCr == 0) r.FirstAddedHeroCr = HeroCommandRating(p);
            }
            else r.AddedNonHero++;
            return RosterFits(r);
        }

        internal Token Add(MaterializationPlan p)
        {
            string key = RecipientKey(p);
            Recipient r = Get(key);
            bool hero = IsHeroPlan(p);
            bool firstHero = false;
            if (hero)
            {
                r.AddedHeroes++;
                if (r.FirstAddedHeroCr == 0) { r.FirstAddedHeroCr = HeroCommandRating(p); firstHero = true; }
            }
            else r.AddedNonHero++;
            _recipients[key] = r;
            int peak = Mathf.Max(0, p.HandSlotsNeededAtPeak);
            _handSlotsUsed += peak;
            return new Token(key, hero, firstHero, peak);
        }

        internal void Remove(in Token t)
        {
            if (_recipients.TryGetValue(t.Key, out Recipient r))
            {
                if (t.IsHero)
                {
                    r.AddedHeroes = Mathf.Max(0, r.AddedHeroes - 1);
                    if (t.WasFirstAddedHero) r.FirstAddedHeroCr = 0;
                }
                else r.AddedNonHero = Mathf.Max(0, r.AddedNonHero - 1);
                _recipients[t.Key] = r;
            }
            _handSlotsUsed = Mathf.Max(0, _handSlotsUsed - t.HandPeak);
        }
    }
}
