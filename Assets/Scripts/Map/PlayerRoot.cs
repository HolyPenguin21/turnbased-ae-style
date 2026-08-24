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

        // Extra initiative dice bought for this turn's dice-off, on top of
        // TurnOrderResolver's base count — reset every turn before the buying phase, so
        // nothing carries over once that turn's roll has consumed them.
        public int BonusInitiativeDice { get; private set; }

        public void AddBonusInitiativeDice(int amount)
        {
            BonusInitiativeDice = Mathf.Max(0, BonusInitiativeDice + amount);
        }

        public void ResetBonusInitiativeDice()
        {
            BonusInitiativeDice = 0;
            foreach (ResourceType type in AllResourceTypes)
                _diceBoughtByResource[type] = 0;
        }

        // Which resource funded each currently-bought bonus die — lets the buy panel's "+"
        // (undo) button refund the right resource instead of guessing. Cleared alongside
        // BonusInitiativeDice at the start of every turn's buying phase.
        private readonly Dictionary<ResourceType, int> _diceBoughtByResource = new Dictionary<ResourceType, int>
        {
            { ResourceType.Human, 0 },
            { ResourceType.Energy, 0 },
            { ResourceType.Materials, 0 },
            { ResourceType.Tech, 0 },
        };

        // Total dice per player is capped at 10 (TurnOrderResolver.BaseDiceCount's 3 plus up to
        // 7 bought) — without this, buying was only ever limited by how much a player could
        // afford.
        private const int MaxBonusInitiativeDice = 7;

        public bool CanBuyInitiativeDie(ResourceType resource, int price) =>
            GetResource(resource) >= price && BonusInitiativeDice < MaxBonusInitiativeDice;

        public void BuyInitiativeDie(ResourceType resource, int price)
        {
            if (!CanBuyInitiativeDie(resource, price))
                return;
            AddResource(resource, -price);
            AddBonusInitiativeDice(1);
            _diceBoughtByResource[resource]++;
        }

        public bool CanRefundInitiativeDie(ResourceType resource) => _diceBoughtByResource[resource] > 0;

        public void RefundInitiativeDie(ResourceType resource, int price)
        {
            if (!CanRefundInitiativeDie(resource))
                return;
            AddResource(resource, price);
            AddBonusInitiativeDice(-1);
            _diceBoughtByResource[resource]--;
        }

        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

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
