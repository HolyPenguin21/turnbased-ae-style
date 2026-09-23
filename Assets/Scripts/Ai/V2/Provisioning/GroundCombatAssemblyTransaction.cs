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
    // FIX-03 — the one same-hex ground-combat assembly rollback/accounting primitive. Raid and
    // ActiveDefence ran two byte-similar private copies of this; a transaction that has to report
    // honestly whether the world was left mutated must measure that the same way in both lanes.
    // No state of its own: it is the undo half of the Provisioning-tier transaction, nothing more.
    internal static class GroundCombatAssemblyTransaction
    {
        // Undo every applied transfer, newest first. Returns false if ANY body could not be put
        // back — the caller must then report the mutation honestly instead of claiming a clean
        // rejection.
        internal static bool Rollback(PlayerSetupData player, ArmyData host,
            List<GroundCombatAssemblyTransfer> applied, AiTurnContext ctx, string lane)
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
                }
            }
            return ok;
        }

        // How many of the applied transfers are STILL sitting in the host after a rollback attempt
        // — i.e. how much of the world this transaction really changed. Zero means a Complete
        // rollback (no mutation to report); anything else is an Incomplete one.
        internal static int RemainingApplied(ArmyData host,
            List<GroundCombatAssemblyTransfer> applied)
        {
            if (host == null || applied == null)
                return 0;
            int count = 0;
            foreach (GroundCombatAssemblyTransfer t in applied)
                if (t?.Unit != null && host.Members.Contains(t.Unit))
                    count++;
            return count;
        }
    }
}
