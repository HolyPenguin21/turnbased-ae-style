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
        // free/random InitiativeDiceAI path is gone, so BonusInitiativeDice and the payment ledger
        // are a strict 1:1 invariant for the whole round.
        public int BonusInitiativeDice { get; private set; }

        private readonly struct InitiativePayment
        {
            public readonly ResourceType Resource;
            public readonly int Amount;

            public InitiativePayment(ResourceType resource, int amount)
            {
                Resource = resource;
                Amount = amount;
            }
        }

        // One entry per bought die, in purchase order. Current UI semantics are authoritative:
        // a single die is paid ENTIRELY from one resource type. The amount is stored as well as
        // the type because the progressive ladder changes after every purchase; refunding must
        // restore the exact historical price, not recompute today's next-die price.
        private readonly List<InitiativePayment> _initiativePayments = new List<InitiativePayment>();

        public int NextInitiativeDieCost => Game.Turns.InitiativeRules.NextBonusDieCost(_initiativePayments.Count);
        public bool CanBuyMoreInitiativeDice => _initiativePayments.Count < Game.Turns.InitiativeRules.MaxBonusDice;

        public void ResetBonusInitiativeDice()
        {
            BonusInitiativeDice = 0;
            _initiativePayments.Clear();
        }

        // Canonical initiative purchase path for BOTH the human UI and Strategy V2. One purchase
        // consumes the full current progressive price from exactly one H/E/M/T stockpile.
        public bool CanBuyInitiativeDie(ResourceType resource)
        {
            return CanBuyMoreInitiativeDice
                && _resources.ContainsKey(resource)
                && GetResource(resource) >= NextInitiativeDieCost;
        }

        public bool PurchaseInitiativeDie(ResourceType resource)
        {
            if (!CanBuyInitiativeDie(resource))
                return false;

            int cost = NextInitiativeDieCost;
            AddResource(resource, -cost);
            _initiativePayments.Add(new InitiativePayment(resource, cost));
            BonusInitiativeDice++;
            return true;
        }

        // Refund remains last-purchase-only because undoing an older die while leaving a later,
        // more expensive die bought would make the progressive ladder ambiguous. The row that
        // paid the last die is the only one whose "+" button is enabled.
        public bool CanRefundInitiativeDie(ResourceType resource)
        {
            if (_initiativePayments.Count == 0)
                return false;
            return _initiativePayments[_initiativePayments.Count - 1].Resource == resource;
        }

        public bool RefundLastInitiativeDie(ResourceType resource)
        {
            if (!CanRefundInitiativeDie(resource))
                return false;

            int last = _initiativePayments.Count - 1;
            InitiativePayment payment = _initiativePayments[last];
            _initiativePayments.RemoveAt(last);
            AddResource(payment.Resource, payment.Amount);
            BonusInitiativeDice--;
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
