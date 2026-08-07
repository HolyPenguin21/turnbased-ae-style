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

        // Action Points — separate from the four stockpiled ResourceType resources below: AP
        // is spent during the player's own turn and replenished at the start of it, not part
        // of the citadel-yield/dice-buying economy. Allocated fresh every turn by
        // GameTurnController.AllocateActionPoints (based on initiative rank, not cumulative);
        // spend rules land separately later.
        public int ActionPoints { get; set; }

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
            _resources[type] += amount;
        }
    }
}
