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
        // InitiativeRules.BaseDice — reset every turn before the buying phase, so nothing carries
        // over once that turn's roll has consumed them. Stays the authoritative count so the V1
        // placeholder AI (InitiativeDiceAI, which grants free dice via AddBonusInitiativeDice
        // without any payment) keeps working unchanged when Strategy V2 is off.
        public int BonusInitiativeDice { get; private set; }

        public void AddBonusInitiativeDice(int amount)
        {
            BonusInitiativeDice = Mathf.Max(0, BonusInitiativeDice + amount);
        }

        public void ResetBonusInitiativeDice()
        {
            BonusInitiativeDice = 0;
            _initiativePayments.Clear();
        }

        // The exact Human/Energy/Materials/Tech bundle that paid for each PAID bonus die, one
        // entry per die in purchase order (index order matches AllResourceTypes). Refunding the
        // most recently bought die restores exactly its bundle — see RefundLastInitiativeDie.
        // The V1 free-dice path never adds here, so this can be shorter than BonusInitiativeDice
        // when Strategy V2 is off; the human buy panel and the V2 planner only ever go through
        // the paid API below, so for them the two stay in lockstep.
        private readonly List<int[]> _initiativePayments = new List<int[]>();

        // How many bonus dice were actually PAID for through the canonical purchase API (<=
        // BonusInitiativeDice). This is what the progressive price ladder is indexed by.
        public int PaidInitiativeDice => _initiativePayments.Count;

        // Cost of the next paid bonus die for this player, per the shared progressive ladder.
        public int NextInitiativeDieCost => Game.Turns.InitiativeRules.NextBonusDieCost(_initiativePayments.Count);

        public bool CanBuyMoreInitiativeDice => _initiativePayments.Count < Game.Turns.InitiativeRules.MaxBonusDice;

        private static int ResourceIndex(ResourceType type)
        {
            for (int i = 0; i < AllResourceTypes.Length; i++)
                if (AllResourceTypes[i] == type)
                    return i;
            return 0;
        }

        // Canonical paid-purchase API (used by the human buy panel AND the V2 Initiative
        // planner). `bundle` is 4 non-negative amounts in AllResourceTypes order; it must sum to
        // exactly NextInitiativeDieCost and every component must be currently affordable.
        public bool CanPayInitiativeBundle(IReadOnlyList<int> bundle)
        {
            if (!CanBuyMoreInitiativeDice || bundle == null || bundle.Count != AllResourceTypes.Length)
                return false;
            int sum = 0;
            for (int i = 0; i < AllResourceTypes.Length; i++)
            {
                if (bundle[i] < 0 || GetResource(AllResourceTypes[i]) < bundle[i])
                    return false;
                sum += bundle[i];
            }
            return sum == NextInitiativeDieCost;
        }

        public void PurchaseInitiativeDie(IReadOnlyList<int> bundle)
        {
            if (!CanPayInitiativeBundle(bundle))
                return;
            var stored = new int[AllResourceTypes.Length];
            for (int i = 0; i < AllResourceTypes.Length; i++)
            {
                stored[i] = bundle[i];
                if (bundle[i] > 0)
                    AddResource(AllResourceTypes[i], -bundle[i]);
            }
            _initiativePayments.Add(stored);
            AddBonusInitiativeDice(1);
        }

        public bool CanRefundLastInitiativeDie => _initiativePayments.Count > 0;

        public void RefundLastInitiativeDie()
        {
            if (_initiativePayments.Count == 0)
                return;
            int last = _initiativePayments.Count - 1;
            int[] bundle = _initiativePayments[last];
            _initiativePayments.RemoveAt(last);
            for (int i = 0; i < AllResourceTypes.Length; i++)
                if (bundle[i] > 0)
                    AddResource(AllResourceTypes[i], bundle[i]);
            AddBonusInitiativeDice(-1);
        }

        // ---- Back-compat shims for the existing per-resource buy panel (InitiativeBuyPanelUI /
        // BuyDiceRowUI). Each row still buys one whole die from its own single resource (a legal
        // bundle: all-from-one-type), and its "+" refund only lights up when THAT resource paid
        // the most recent die — refund is last-die-only now that pricing is progressive. The
        // `price` argument is ignored: the real cost is always NextInitiativeDieCost.
        public bool CanBuyInitiativeDie(ResourceType resource, int price)
        {
            var bundle = new int[AllResourceTypes.Length];
            bundle[ResourceIndex(resource)] = NextInitiativeDieCost;
            return CanPayInitiativeBundle(bundle);
        }

        public void BuyInitiativeDie(ResourceType resource, int price)
        {
            var bundle = new int[AllResourceTypes.Length];
            bundle[ResourceIndex(resource)] = NextInitiativeDieCost;
            PurchaseInitiativeDie(bundle);
        }

        public bool CanRefundInitiativeDie(ResourceType resource)
        {
            if (_initiativePayments.Count == 0)
                return false;
            int[] last = _initiativePayments[_initiativePayments.Count - 1];
            int idx = ResourceIndex(resource);
            for (int i = 0; i < last.Length; i++)
                if (i != idx && last[i] != 0)
                    return false;
            return last[idx] > 0;
        }

        public void RefundInitiativeDie(ResourceType resource, int price)
        {
            if (CanRefundInitiativeDie(resource))
                RefundLastInitiativeDie();
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
