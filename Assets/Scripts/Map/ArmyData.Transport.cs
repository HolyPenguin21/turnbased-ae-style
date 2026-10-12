using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Units;

namespace Game.Map
{
    public partial class ArmyData
    {
        private readonly HashSet<UnitData> _transportRecipients = new HashSet<UnitData>();
        private static readonly Dictionary<UnitData, UnitData> NoTransport = new Dictionary<UnitData, UnitData>();

        internal void ClearTransport()
        {
            foreach (UnitData target in _transportRecipients)
                if (target.TransportArmy == this) target.SetTransportCarrier(null, null);
            _transportRecipients.Clear();
        }

        // Commit-boundary refresh: one target per carrier; valid pairs survive roster ordering
        // and turn changes. No random rolls or live writes occur in projected-roster reads.
        public void RefreshTransport()
        {
            var links = IsPrison || IsAirfield || IsAirArmy
                ? new Dictionary<UnitData, UnitData>() : ProjectTransport(Members);
            foreach (UnitData old in _transportRecipients)
                if (!links.ContainsKey(old) && old.TransportArmy == this) old.SetTransportCarrier(null, null);
            _transportRecipients.Clear();
            foreach (UnitData member in Members)
            {
                if (member == null) continue;
                links.TryGetValue(member, out UnitData carrier);
                member.SetTransportCarrier(carrier, carrier != null ? this : null);
                if (carrier != null) _transportRecipients.Add(member);
            }
        }

        private static bool TransportEligible(UnitData target, UnitData carrier)
            => target != null && target != carrier && !target.IsHero && !target.IsAviation
                && !target.IsPrisoner && !target.IsSummoned && target.HitPointsCurrent > 0
                && target.TypeTags.Contains(UnitTypeTag.Infantry)
                && target.PermanentMoveMax < carrier.PermanentMoveMax;

        internal static Dictionary<UnitData, UnitData> ProjectTransport(IReadOnlyList<UnitData> roster)
        {
            if (roster == null) return NoTransport;
            bool anyCarrier = false;
            foreach (UnitData unit in roster)
                if (unit != null && unit.HasAbility(UnitAbilities.Transport)) { anyCarrier = true; break; }
            if (!anyCarrier) return NoTransport;
            var links = new Dictionary<UnitData, UnitData>();
            var carriers = roster.Where(u => u != null && !u.IsAviation && !u.IsPrisoner
                && !u.IsSummoned && u.HitPointsCurrent > 0 && u.HasAbility(UnitAbilities.Transport))
                .OrderByDescending(u => u.PermanentMoveMax).ThenBy(u => u.RuntimeId).ToList();
            // Reserve ALL existing valid pairs before choosing replacements. A newly joined
            // faster carrier must not steal the target of an existing slower transport.
            foreach (UnitData carrier in carriers)
            {
                UnitData existing = roster.FirstOrDefault(u => u != null
                    && (u.AssignedTransportCarrier == carrier || carrier.RuntimeId > 0
                        && u.AssignedTransportCarrier?.RuntimeId == carrier.RuntimeId)
                    && TransportEligible(u, carrier) && !links.ContainsKey(u));
                if (existing != null) links[existing] = carrier;
            }
            // The army moves at its slowest member, so a carrier serves the slowest eligible
            // Infantry. A kept pair yields only to a strictly slower unassigned body; the freed
            // body stays available to the next carrier.
            foreach (UnitData carrier in carriers)
            {
                UnitData current = links.FirstOrDefault(l => l.Value == carrier).Key;
                UnitData target = roster.Where(u => TransportEligible(u, carrier) && !links.ContainsKey(u))
                    .OrderBy(u => u.PermanentMoveMax)
                    .ThenBy(u => TransportChoiceKey(carrier.RuntimeId, u.RuntimeId)).FirstOrDefault();
                if (target == null || current != null && target.PermanentMoveMax >= current.PermanentMoveMax)
                    continue;
                if (current != null) links.Remove(current);
                links[target] = carrier;
            }
            return links;
        }

        // Seeded tie-break between equally slow bodies by stable live identities: prediction
        // and commit make the same choice, and list reordering has no effect.
        private static uint TransportChoiceKey(int carrier, int target)
        {
            unchecked
            {
                uint x = (uint)carrier * 0x9e3779b9u ^ (uint)target;
                x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu;
                return x ^ (x >> 16);
            }
        }

        private static int ProjectedMemberMoveMax(UnitData unit, Dictionary<UnitData, UnitData> links)
            => System.Math.Max(unit.PermanentMoveMax,
                links.TryGetValue(unit, out UnitData carrier) ? carrier.PermanentMoveMax : 0);
    }
}
