using Game.Economy;
using Game.Map;

namespace Game.Cards
{
    public enum PaymentFailure { None, InvalidCost, MissingAccount, ActionPoints, Resources }

    // A quote copies authored/mutable costs. Debit rechecks the entire budget immediately
    // before any write; observers only see the completed account. It does not reserve funds.
    public readonly struct PaymentQuote
    {
        public int Ap { get; }
        private readonly int human, energy, materials, tech;
        public ResourceCost Resources => new ResourceCost(human, energy, materials, tech);
        public PaymentQuote(int ap, ResourceCost cost)
        { Ap = ap; human = cost?.human ?? 0; energy = cost?.energy ?? 0; materials = cost?.materials ?? 0; tech = cost?.tech ?? 0; }
        public bool CanDebit(PlayerRoot root, out PaymentFailure failure)
        {
            failure = PaymentFailure.None;
            if (Ap < 0 || human < 0 || energy < 0 || materials < 0 || tech < 0) failure = PaymentFailure.InvalidCost;
            else if (root == null) failure = PaymentFailure.MissingAccount;
            else if (root.ActionPoints < Ap) failure = PaymentFailure.ActionPoints;
            else if (root.GetResource(ResourceType.Human) < human || root.GetResource(ResourceType.Energy) < energy
                || root.GetResource(ResourceType.Materials) < materials || root.GetResource(ResourceType.Tech) < tech)
                failure = PaymentFailure.Resources;
            return failure == PaymentFailure.None;
        }
        public bool TryDebit(PlayerRoot root, out PaymentReceipt receipt, out PaymentFailure failure)
        {
            receipt = null;
            if (!CanDebit(root, out failure)) return false;
            using var mutation = VisionSystem.BeginMutation();
            root.ActionPoints -= Ap;
            root.AddResource(ResourceType.Human, -human); root.AddResource(ResourceType.Energy, -energy);
            root.AddResource(ResourceType.Materials, -materials); root.AddResource(ResourceType.Tech, -tech);
            receipt = new PaymentReceipt(root, this);
            return true;
        }
    }

    // Only the owning application operation may compensate. A challenge loss or consumed
    // SingleUse attachment never receives a public refund route. Refund is a delta, once.
    public sealed class PaymentReceipt
    {
        private readonly PlayerRoot root;
        private readonly PaymentQuote quote;
        private bool compensated;
        internal PaymentReceipt(PlayerRoot root, PaymentQuote quote) { this.root = root; this.quote = quote; }
        internal bool TryCompensate()
        {
            if (compensated || root == null) return false;
            compensated = true;
            using var mutation = VisionSystem.BeginMutation();
            root.ActionPoints += quote.Ap;
            var cost = quote.Resources;
            root.AddResource(ResourceType.Human, cost.human); root.AddResource(ResourceType.Energy, cost.energy);
            root.AddResource(ResourceType.Materials, cost.materials); root.AddResource(ResourceType.Tech, cost.tech);
            return true;
        }
    }
}
