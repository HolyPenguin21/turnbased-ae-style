using System;
using Game.Units;

namespace Game.Map
{
    public partial class ArmyData
    {
        public static int IdentityHighWaterMark => _nextId - 1;
        public static void ReserveIdentity(int id)
        {
            if (id < 0 || id == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(id));
            if (_nextId <= id) _nextId = id + 1;
        }
        public ArmyActivationState CaptureActivationState(Action<UnitData> includeReferencedUnit = null)
        {
            var result = new ArmyActivationState { HasActivated = HasActivatedThisTurn };
            foreach (var unit in _activationCoveredUnits)
                if (unit != null)
                {
                    includeReferencedUnit?.Invoke(unit);
                    result.CoveredUnitIds.Add(unit.RuntimeId);
                }
            result.CoveredUnitIds.Sort();
            return result;
        }
        // Resolve the complete ledger, including units now in OTHER armies, before mutation.
        public void RestoreActivationState(ArmyActivationState state, Func<int, UnitData> resolveUnit)
        {
            if (state == null || state.CoveredUnitIds == null) throw new ArgumentException("Missing activation state.");
            var covered = new System.Collections.Generic.HashSet<UnitData>();
            foreach (int id in state.CoveredUnitIds)
                if (!covered.Add(StateReference.Unit(id, resolveUnit))) throw new ArgumentException("Duplicate activation unit.");
            _activationCoveredUnits.Clear();
            _activationCoveredUnits.UnionWith(covered);
            HasActivatedThisTurn = state.HasActivated;
        }
        // Rebuild the owner's cleanup index from restored links, without reassigning passengers.
        public void RebindRestoredTransportRecipients()
        {
            _transportRecipients.Clear();
            foreach (var member in Members)
                if (member != null && member.TransportArmy == this && member.AssignedTransportCarrier != null)
                    _transportRecipients.Add(member);
        }
    }
}
