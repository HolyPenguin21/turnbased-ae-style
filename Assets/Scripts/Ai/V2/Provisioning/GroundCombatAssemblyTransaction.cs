using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // What a same-hex assembly may change besides the moved members themselves, read before its
    // first transfer: every involved army's commander and every involved unit's transport carrier.
    // A rollback restores the commanders and reports what it could not restore.
    internal sealed class GroundCombatAssemblyBaseline
    {
        internal readonly Dictionary<ArmyData, UnitData> Commanders = new Dictionary<ArmyData, UnitData>();
        internal readonly Dictionary<UnitData, UnitData> Carriers = new Dictionary<UnitData, UnitData>();
    }

    // The one same-hex ground-combat assembly rollback/accounting primitive, shared by Raid and
    // ActiveDefence: a transaction that has to report honestly whether the world was left mutated
    // must measure that the same way in both lanes.
    // No state of its own: it is the undo half of the Provisioning-tier transaction, nothing more.
    internal static class GroundCombatAssemblyTransaction
    {
        internal static GroundCombatAssemblyBaseline Capture(PlayerSetupData player, ArmyData host,
            IEnumerable<GroundCombatAssemblyTransfer> transfers)
        {
            var baseline = new GroundCombatAssemblyBaseline();
            var armies = new List<ArmyData> { host };
            foreach (GroundCombatAssemblyTransfer t in transfers ?? Enumerable.Empty<GroundCombatAssemblyTransfer>())
                armies.Add(AiV2Util.ResolveArmy(player, t?.DonorArmyId ?? -1));
            foreach (ArmyData army in armies.Where(a => a != null).Distinct())
            {
                baseline.Commanders[army] = army.Commander;
                foreach (UnitData u in army.Members.Where(u => u != null))
                    baseline.Carriers[u] = u.TransportCarrier;
            }
            return baseline;
        }

        // Undo every applied transfer, newest first. Returns false if ANY body could not be put
        // back — the caller must then report the mutation honestly instead of claiming a clean
        // rejection.
        internal static bool Rollback(PlayerSetupData player, ArmyData host,
            List<GroundCombatAssemblyTransfer> applied, AiTurnContext ctx, string lane,
            ArmyData heroHome = null, IReadOnlyList<UnitData> returnedHeroes = null,
            UnitData originalCommander = null, GroundCombatAssemblyBaseline baseline = null)
        {
            bool ok = true;
            if (host == null || applied == null)
                return false;
            for (int i = applied.Count - 1; i >= 0; --i)
            {
                GroundCombatAssemblyTransfer t = applied[i];
                ArmyData donor = AiV2Util.ResolveArmy(player, t?.DonorArmyId ?? -1);
                string why = donor == null ? "donor missing"
                    : t?.Unit == null || !host.Members.Contains(t.Unit) ? "unit no longer in host"
                    : null;
                if (donor == null || t?.Unit == null || !host.Members.Contains(t.Unit)
                    || !ArmyActions.TransferMember(t.Unit, host, donor, ctx.HexSelection, out why))
                {
                    ok = false;
                    AiDebugLog.Write($"[AI][V2]   {lane} assembly rollback — FAILED {t?.Unit?.Name} "
                        + $"host #{host.Id}->donor #{t?.DonorArmyId}: {why}");
                    continue;
                }
                // The join pre-marked the unit as covered by the host's activation. Nothing was
                // paid for a host that has not activated (an activated host only took zero-cost
                // joins), so a compensated join must not leave that coverage behind.
                if (!host.HasActivatedThisTurn)
                    host.MarkUnitActivationPaid(t.Unit, paid: false);
            }
            if (returnedHeroes != null && returnedHeroes.Count > 0)
            {
                string why = heroHome == null ? "return garrison missing" : null;
                UnitData promote = returnedHeroes.Contains(originalCommander) ? originalCommander : null;
                if (heroHome == null || !ArmyActions.TransferMembersAtomic(returnedHeroes, heroHome,
                    host, ctx.HexSelection, out why, promoteToCommander: promote))
                {
                    ok = false;
                    AiDebugLog.Write($"[AI][V2]   {lane} assembly rollback — FAILED hero return: {why}");
                }
            }
            if (originalCommander != null && host.Members.Contains(originalCommander)
                && host.Commander != originalCommander && !host.TryReorderCommander(originalCommander, out _))
                ok = false;
            // A field donor that gave its commander away is led by it again (a zero-AP reorder);
            // a garrison's order is canonical (ArmyData.NormalizeGarrisonOrder) and restores itself.
            if (baseline != null)
                foreach (KeyValuePair<ArmyData, UnitData> kv in baseline.Commanders)
                    if (kv.Key != host && !kv.Key.IsGarrison && kv.Value != null && kv.Key.Members.Contains(kv.Value)
                        && kv.Key.Commander != kv.Value && !kv.Key.TryReorderCommander(kv.Value, out _))
                        ok = false;
            return ok;
        }

        // How many of the applied transfers are STILL sitting in the host after a rollback attempt
        // — i.e. how much of the world this transaction really changed. Zero means a Complete
        // rollback (no mutation to report); anything else is an Incomplete one. With a baseline it
        // also counts a donor commander that was not restored and a transport pairing the
        // canonical refresh re-assigned (it changes that unit's movement).
        internal static int RemainingApplied(ArmyData host,
            List<GroundCombatAssemblyTransfer> applied, IReadOnlyList<UnitData> returnedHeroes = null,
            UnitData originalCommander = null, GroundCombatAssemblyBaseline baseline = null)
        {
            if (host == null || applied == null)
                return 0;
            int count = 0;
            foreach (GroundCombatAssemblyTransfer t in applied)
                if (t?.Unit != null && host.Members.Contains(t.Unit))
                    count++;
            if (returnedHeroes != null)
                count += returnedHeroes.Count(h => h != null && !host.Members.Contains(h));
            if (originalCommander != null && host.Commander != originalCommander) count++;
            if (baseline != null)
            {
                count += baseline.Commanders.Count(kv => kv.Key != host && !kv.Key.IsGarrison
                    && kv.Key.Commander != kv.Value);
                count += baseline.Carriers.Count(kv => kv.Key.TransportCarrier != kv.Value);
            }
            return count;
        }
    }
}
