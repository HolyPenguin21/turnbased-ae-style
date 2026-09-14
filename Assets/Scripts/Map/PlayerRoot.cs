using System;
using System.Collections.Generic;
using Game.Economy;
using Game.Players;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Parents every scene object owned by one player — citadel, buildings, armies, whatever
    // comes later — under a single GameObject, so the scene hierarchy groups by owner instead
    // of staying flat. Setup is null for the neutral "belongs to no player" root.
    public class PlayerRoot : MonoBehaviour
    {
        public PlayerSetupData Setup { get; private set; }
        public Color Color { get; private set; } = Color.grey;
        public MapObjectVisual Citadel { get; private set; }

        // Fired whenever ActionPoints or any stockpiled resource actually changes value — lets
        // ResourceBarUI (and anything else that only cares about "did the number I'm showing
        // change") subscribe once instead of polling every frame just to notice. Deliberately
        // one combined event rather than one per resource type: every current subscriber
        // displays all of them together anyway, so there's nothing to gain from finer-grained
        // events, only more subscription bookkeeping.
        public event Action ResourcesChanged;

        // Action Points — separate from the four stockpiled ResourceType resources below: AP
        // is spent during the player's own turn and replenished at the start of it, not part
        // of the citadel-yield/dice-buying economy. Allocated fresh every turn by
        // GameTurnController.AllocateActionPoints (based on initiative rank, not cumulative);
        // spend rules land separately later. Backing field + explicit setter (rather than an
        // auto-property) so ResourcesChanged fires from every mutation, direct assignment
        // included, without every call site needing to remember to raise it itself.
        private int _actionPoints;
        public int ActionPoints
        {
            get => _actionPoints;
            set
            {
                if (_actionPoints == value)
                    return;
                _actionPoints = value;
                ResourcesChanged?.Invoke();
            }
        }

        // Turn-start AP breakdown — purely diagnostic, never read by any gameplay logic. Set by
        // GameTurnController.AllocateActionPoints/GrantPrisonBonusActionPoints/
        // GrantApBonusActionPoints as each is applied (each runs exactly once per player per
        // turn, so these are plain overwrites, not accumulators — no reset step needed). Lets
        // AiTurnController's own turn-begins log line show WHY this turn's AP total is what it
        // is instead of just the opaque final number (project owner's own report, 2026-08-24 —
        // a UnitAbilities.ApBonus hero's contribution was invisible in the log).
        public int LastApFromInitiative { get; private set; }
        public int LastApFromPrisonBonus { get; private set; }
        public int LastApFromApBonus { get; private set; }

        // Per-source breakdown for LastApFromApBonus (e.g. "Aldric Voss +2, Base at (5,1) +2") —
        // same purely-diagnostic purpose as the totals above, just naming WHICH carriers made up
        // the total instead of only the total itself (project owner's own report: with several
        // ApBonus carriers in play the flat number alone doesn't say which one is missing when a
        // hero dies or a base is lost). Empty when LastApFromApBonus is 0.
        public string LastApBonusSources { get; private set; } = string.Empty;

        public void SetLastApFromInitiative(int amount) => LastApFromInitiative = amount;
        public void SetLastApFromPrisonBonus(int amount) => LastApFromPrisonBonus = amount;
        public void SetLastApFromApBonus(int amount) => LastApFromApBonus = amount;
        public void SetLastApBonusSources(string breakdown) => LastApBonusSources = breakdown ?? string.Empty;

        public bool CanSpendActionPoints(int amount) => ActionPoints >= amount;

        public void SpendActionPoints(int amount)
        {
            if (!CanSpendActionPoints(amount))
                return;
            ActionPoints -= amount;
        }

        // Extra initiative dice bought for this turn's dice-off, on top of InitiativeRules.BaseDice.
        // There is now exactly ONE path to obtain a bonus die: the paid purchase API below. The old
        // free/random InitiativeDiceAI path is gone, so BonusInitiativeDice and the contribution
        // ledger are a strict 1:1 invariant for the whole round.
        //
        // One entry per 1-unit resource contribution, in the exact order the player spent them —
        // across the WHOLE turn, not reset per die. A die's progressive cost (see
        // InitiativeRules.NextBonusDieCost) no longer has to come from one resource in one
        // purchase: the player can mix any H/E/M/T in any order, one unit per click, and the die
        // completes automatically once the running total clears its threshold (see
        // InitiativeRules.CumulativeUnitsForDiceCount). BonusInitiativeDice and "how far into the
        // current die" are therefore both DERIVED from this list rather than tracked separately,
        // so refunding a single unit (always the most recent one — see RefundLastInitiativeDie)
        // can walk back across a just-completed die boundary for free.
        private readonly List<ResourceType> _initiativeUnitContributions = new List<ResourceType>();

        public int BonusInitiativeDice
        {
            get
            {
                int dice = 0;
                while (dice < Game.Turns.InitiativeRules.MaxBonusDice
                    && _initiativeUnitContributions.Count >= Game.Turns.InitiativeRules.CumulativeUnitsForDiceCount(dice + 1))
                    dice++;
                return dice;
            }
        }

        // Units already paid toward the die currently being assembled — 0 right after a die
        // completes, resets automatically once BonusInitiativeDice ticks over.
        public int CurrentDieUnitsContributed =>
            _initiativeUnitContributions.Count - Game.Turns.InitiativeRules.CumulativeUnitsForDiceCount(BonusInitiativeDice);

        // Total cost (in resource units, any mix) of the die currently being assembled.
        public int NextInitiativeDieCost => Game.Turns.InitiativeRules.NextBonusDieCost(BonusInitiativeDice);
        public bool CanBuyMoreInitiativeDice => BonusInitiativeDice < Game.Turns.InitiativeRules.MaxBonusDice;

        public void ResetBonusInitiativeDice()
        {
            _initiativeUnitContributions.Clear();
        }

        // Canonical initiative purchase path for BOTH the human UI and Strategy V2. Spends exactly
        // 1 unit of `resource` toward the die currently being assembled — call repeatedly, mixing
        // any H/E/M/T in any order the player likes, until NextInitiativeDieCost units are in.
        public bool CanBuyInitiativeDie(ResourceType resource)
        {
            return CanBuyMoreInitiativeDice
                && _resources.ContainsKey(resource)
                && GetResource(resource) >= 1;
        }

        public bool PurchaseInitiativeDie(ResourceType resource)
        {
            if (!CanBuyInitiativeDie(resource))
                return false;

            AddResource(resource, -1);
            _initiativeUnitContributions.Add(resource);
            return true;
        }

        // Only the resource that paid the single most recent unit can undo it — same
        // last-purchase-only rule as before, now at unit granularity, so it can also un-complete
        // a die that just finished (as long as nothing has been paid toward the next one yet).
        public bool CanRefundInitiativeDie(ResourceType resource)
        {
            if (_initiativeUnitContributions.Count == 0)
                return false;
            return _initiativeUnitContributions[_initiativeUnitContributions.Count - 1] == resource;
        }

        public bool RefundLastInitiativeDie(ResourceType resource)
        {
            if (!CanRefundInitiativeDie(resource))
                return false;

            int last = _initiativeUnitContributions.Count - 1;
            _initiativeUnitContributions.RemoveAt(last);
            AddResource(resource, 1);
            return true;
        }

        private readonly Dictionary<ResourceType, int> _resources = new Dictionary<ResourceType, int>
        {
            { ResourceType.Human, 0 },
            { ResourceType.Energy, 0 },
            { ResourceType.Materials, 0 },
            { ResourceType.Tech, 0 },
        };

        public static PlayerRoot Create(PlayerSetupData setup, string name)
        {
            var root = new GameObject(name).AddComponent<PlayerRoot>();
            root.Setup = setup;
            if (setup != null)
                root.Color = PlayerColorPalette.Colors[setup.ColorIndex];
            return root;
        }

        public void SetCitadel(MapObjectVisual citadel)
        {
            Citadel = citadel;
        }

        public int GetResource(ResourceType type) => _resources[type];

        public void AddResource(ResourceType type, int amount)
        {
            if (amount == 0)
                return;
            _resources[type] += amount;
            ResourcesChanged?.Invoke();
        }
    }
}
