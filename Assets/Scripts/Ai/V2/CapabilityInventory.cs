using System;
using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  CAPABILITY INVENTORY  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  Current capability SUPPLY, so StrategicManager can tell scarcity from oversupply
    //  ("0 scouts -> first Recce is high value" vs "3 scouts, little demand -> another Recce is
    //  near worthless"). Rebuilt from the (possibly refreshed) snapshot + ActorCommitments each
    //  time it is consulted — after every Phase-A play, and each Phase-B iteration.
    //
    //  Scout fields are fully wired. The combat / hero fields are best-effort from the snapshot
    //  and are extended when a demand for those capabilities is actually implemented.
    // ===========================================================================================
    public sealed class CapabilityInventory
    {
        public int ReadyScouts;        // solo Recce, can act this turn, NOT claimed by an operation
        public int CommittedScouts;    // solo Recce claimed by an active intent (existing != available)
        public int ReserveScouts;      // solo Recce that exists but cannot be tasked this turn
        public int StealthScouts;      // subset of ReadyScouts already hidden or able to enter stealth

        public float FieldCombatPower;
        public float GarrisonCombatPower;
        public int AvailableHeroes;    // fielded (non-garrison) hero-led armies

        public IReadOnlyList<ArmyData> ReusableEmptyArmies = Array.Empty<ArmyData>();

        public int TotalScouts => ReadyScouts + CommittedScouts + ReserveScouts;

        public static CapabilityInventory Build(WorldSnapshot snap, PlayerSetupData player, ActorCommitments commitments)
        {
            var inv = new CapabilityInventory();
            if (snap?.Self?.Armies == null)
            {
                inv.ReusableEmptyArmies = ReusableArmySelector.ReusableShells(player, commitments);
                return inv;
            }

            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (commitments != null && commitments.IsArmyClaimed(a.ArmyId))
                {
                    inv.CommittedScouts++;
                }
                else if (a.CurrentMovement > 0)
                {
                    inv.ReadyScouts++;
                    if (a.IsHidden || a.CanEnterStealth)
                        inv.StealthScouts++;
                }
                else
                {
                    inv.ReserveScouts++;
                }
            }

            inv.FieldCombatPower = snap.Self.FieldPower;
            inv.GarrisonCombatPower = snap.Self.GarrisonPower;
            inv.AvailableHeroes = snap.Self.Armies.Count(a =>
                a != null && a.HasHero && !a.IsGarrison && !a.IsPrison);

            inv.ReusableEmptyArmies = ReusableArmySelector.ReusableShells(player, commitments);
            return inv;
        }
    }
}
