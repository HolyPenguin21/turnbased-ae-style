using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MATERIALIZATION CONSUMPTION STATE
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
        private readonly HashSet<string> _externalConflictKeys = new HashSet<string>();

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

        public static string GenKey(MaterializationPlan p) => p?.Generation?.CardKey;

        internal static string UpgradeConflictKey(CardData card, Game.Units.UnitData unit) =>
            unit != null ? "equipment:unit:" + unit.RuntimeId
                : card != null ? "equipment:hand:" + GenerationSource.StableCardKey(card) : null;

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
            // The plan's three cards, checked without allocating a list per check (this runs
            // for every candidate of every node of the portfolio search).
            if (p != null
                && (Held(p.BaseCardInHand) || Held(p.EquipmentInHand) || Held(p.UpgradeTargetCard)))
                return false;
            string hostKey = UpgradeConflictKey(p?.UpgradeTargetCard, p?.UpgradeTargetUnit);
            if (hostKey != null && _externalConflictKeys.Contains(hostKey)) return false;
            string gk = GenKey(p);
            return string.IsNullOrEmpty(gk) || !_genKeys.Contains(gk);
        }

        private bool Held(CardData c) => c != null && _cards.Contains(c);

        public bool ExternalDisjoint(CardData physicalCard, string generationCardKey,
            string conflictKey, CardData recipientCard = null)
        {
            if (recipientCard != null && _cards.Contains(recipientCard)) return false;
            if (physicalCard != null && _cards.Contains(physicalCard))
                return false;
            if (!string.IsNullOrEmpty(generationCardKey) && _genKeys.Contains(generationCardKey))
                return false;
            return string.IsNullOrEmpty(conflictKey) || !_externalConflictKeys.Contains(conflictKey);
        }

        public readonly struct ExternalToken
        {
            public readonly CardData PhysicalCard;
            public readonly CardData RecipientCard;
            public readonly string GenerationCardKey;
            public readonly string ConflictKey;
            public readonly bool CountedGeneration;
            public readonly float ApAdded;
            public readonly ResourceCost Resources;

            public ExternalToken(CardData physicalCard, string generationCardKey, string conflictKey,
                bool countedGeneration, float apAdded, ResourceCost resources, CardData recipientCard = null)
            {
                RecipientCard = recipientCard;
                PhysicalCard = physicalCard;
                GenerationCardKey = generationCardKey;
                ConflictKey = conflictKey;
                CountedGeneration = countedGeneration;
                ApAdded = apAdded;
                Resources = resources;
            }
        }

        public ExternalToken PushExternal(CardData physicalCard, string generationCardKey,
            string conflictKey, bool generation, float ap, ResourceCost resources, CardData recipientCard = null)
        {
            if (recipientCard != null) _cards.Add(recipientCard);
            if (physicalCard != null) _cards.Add(physicalCard);
            if (!string.IsNullOrEmpty(generationCardKey)) _genKeys.Add(generationCardKey);
            if (!string.IsNullOrEmpty(conflictKey)) _externalConflictKeys.Add(conflictKey);
            if (generation) GenerationAttempts++;
            float apAdded = Mathf.Max(0f, ap);
            ApUsed += apAdded;
            if (resources != null)
            {
                HumanUsed += resources.human;
                EnergyUsed += resources.energy;
                MaterialsUsed += resources.materials;
                TechUsed += resources.tech;
            }
            return new ExternalToken(physicalCard, generationCardKey, conflictKey,
                generation, apAdded, resources, recipientCard);
        }

        public void PopExternal(in ExternalToken token)
        {
            if (token.RecipientCard != null) _cards.Remove(token.RecipientCard);
            if (token.PhysicalCard != null) _cards.Remove(token.PhysicalCard);
            if (!string.IsNullOrEmpty(token.GenerationCardKey)) _genKeys.Remove(token.GenerationCardKey);
            if (!string.IsNullOrEmpty(token.ConflictKey)) _externalConflictKeys.Remove(token.ConflictKey);
            if (token.CountedGeneration) GenerationAttempts--;
            ApUsed -= token.ApAdded;
            if (token.Resources != null)
            {
                HumanUsed -= token.Resources.human;
                EnergyUsed -= token.Resources.energy;
                MaterialsUsed -= token.Resources.materials;
                TechUsed -= token.Resources.tech;
            }
        }

        // Apply `p` (+ an optional caller-side follow-up AP, e.g. DemandCandidate.FollowupAp) to the
        // running totals. Assumes CardsDisjoint(p) already held.
        public Token Push(MaterializationPlan p, float extraAp = 0f)
        {
            if (p != null)
            {
                if (p.BaseCardInHand != null) _cards.Add(p.BaseCardInHand);
                if (p.EquipmentInHand != null) _cards.Add(p.EquipmentInHand);
                if (p.UpgradeTargetCard != null) _cards.Add(p.UpgradeTargetCard);
            }
            string gk = GenKey(p);
            string hostKey = UpgradeConflictKey(p?.UpgradeTargetCard, p?.UpgradeTargetUnit);
            if (hostKey != null) _externalConflictKeys.Add(hostKey);
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
            if (p != null)
            {
                if (p.BaseCardInHand != null) _cards.Remove(p.BaseCardInHand);
                if (p.EquipmentInHand != null) _cards.Remove(p.EquipmentInHand);
                if (p.UpgradeTargetCard != null) _cards.Remove(p.UpgradeTargetCard);
            }
            string hostKey = UpgradeConflictKey(p?.UpgradeTargetCard, p?.UpgradeTargetUnit);
            if (hostKey != null) _externalConflictKeys.Remove(hostKey);
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
