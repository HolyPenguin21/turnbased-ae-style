using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MATERIALIZATION CONSUMPTION STATE  (AI-MGR-02 round 9 — P0.2)
    // ===========================================================================================
    //  ONE joint-consumption model for assembling a multi-chain materialization portfolio: the
    //  physical hand cards a chain consumes (base + equipment), the single per-turn generation
    //  source (by CardKey), and the cumulative AP / H-E-M-T draw. Two chains can each be
    //  individually affordable yet un-runnable together — both want the last Tech, both want the
    //  one Challenge with different CardKeys, or the same physical card yields many alternative
    //  MaterializationPlans (Card A -> Army 1 / Army 2 / Garrison …) and a naive subset search
    //  counts one card as two bumps.
    //
    //  StrategicManager.BestInjectiveAssignment and the reaction materialization-closure DFS both
    //  push/pop against this, so they can never disagree about what a portfolio physically
    //  consumes. Push returns a Token that Pop consumes to reverse EXACTLY what was applied
    //  (a generation CardKey is only released by the push that first added it).
    // ===========================================================================================
    internal sealed class MaterializationConsumptionState
    {
        private readonly HashSet<CardData> _cards = new HashSet<CardData>();
        private readonly HashSet<string> _genKeys = new HashSet<string>();

        public int GenerationAttempts { get; private set; }
        public float ApUsed { get; private set; }
        public int HumanUsed { get; private set; }
        public int EnergyUsed { get; private set; }
        public int MaterialsUsed { get; private set; }
        public int TechUsed { get; private set; }

        public readonly struct Token
        {
            public readonly MaterializationPlan Plan;
            public readonly bool AddedGenKey;
            public readonly bool CountedGen;
            public readonly float ApAdded;

            public Token(MaterializationPlan plan, bool addedGenKey, bool countedGen, float apAdded)
            {
                Plan = plan;
                AddedGenKey = addedGenKey;
                CountedGen = countedGen;
                ApAdded = apAdded;
            }
        }

        // The physical hand-card instances a chain consumes (base + equipment). The generation
        // source is tracked separately by GenerationStep.CardKey.
        public static IReadOnlyList<CardData> PlanCards(MaterializationPlan p)
        {
            var list = new List<CardData>(2);
            if (p?.BaseCardInHand != null) list.Add(p.BaseCardInHand);
            if (p?.EquipmentInHand != null) list.Add(p.EquipmentInHand);
            return list;
        }

        public static string GenKey(MaterializationPlan p) => p?.Generation?.CardKey;

        public int ResourceUsed(ResourceType t)
        {
            switch (t)
            {
                case ResourceType.Human: return HumanUsed;
                case ResourceType.Energy: return EnergyUsed;
                case ResourceType.Materials: return MaterialsUsed;
                default: return TechUsed;
            }
        }

        // Physical disjointness only — no budget check: the same hand card / generation CardKey is
        // not already taken by an accepted chain.
        public bool CardsDisjoint(MaterializationPlan p)
        {
            foreach (CardData c in PlanCards(p))
                if (c != null && _cards.Contains(c))
                    return false;
            string gk = GenKey(p);
            return string.IsNullOrEmpty(gk) || !_genKeys.Contains(gk);
        }

        // Apply `p` (+ an optional caller-side follow-up AP, e.g. DemandCandidate.FollowupAp) to the
        // running totals. Assumes CardsDisjoint(p) already held.
        public Token Push(MaterializationPlan p, float extraAp = 0f)
        {
            foreach (CardData c in PlanCards(p))
                if (c != null) _cards.Add(c);
            string gk = GenKey(p);
            bool addedGen = !string.IsNullOrEmpty(gk) && _genKeys.Add(gk);
            bool countedGen = p?.Generation != null;
            if (countedGen) GenerationAttempts++;
            float apAdd = Mathf.Max(0f, p?.ApCost ?? 0f) + Mathf.Max(0f, extraAp);
            ApUsed += apAdd;
            ResourceCost rc = p?.ResCost;
            if (rc != null)
            {
                HumanUsed += rc.human;
                EnergyUsed += rc.energy;
                MaterialsUsed += rc.materials;
                TechUsed += rc.tech;
            }
            return new Token(p, addedGen, countedGen, apAdd);
        }

        public void Pop(in Token token)
        {
            MaterializationPlan p = token.Plan;
            foreach (CardData c in PlanCards(p))
                if (c != null) _cards.Remove(c);
            if (token.AddedGenKey)
            {
                string gk = GenKey(p);
                if (!string.IsNullOrEmpty(gk)) _genKeys.Remove(gk);
            }
            if (token.CountedGen) GenerationAttempts--;
            ApUsed -= token.ApAdded;
            ResourceCost rc = p?.ResCost;
            if (rc != null)
            {
                HumanUsed -= rc.human;
                EnergyUsed -= rc.energy;
                MaterialsUsed -= rc.materials;
                TechUsed -= rc.tech;
            }
        }
    }
}
