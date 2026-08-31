using System;
using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;
using UnityEngine;

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
    //  Raid-facing combat supply is deliberately stricter than SelfSnapshot.FieldPower. What the
    //  Demand layer needs is not "how much military power exists somewhere", but "how much FREE
    //  ground force can still execute a Raid this allocation cycle". Airfield aircraft, mobile air
    //  armies, dedicated Recce, already-committed armies and armies with no movement left cannot
    //  suppress a FieldCombatPower / Hero demand that none of them could actually execute.
    // ===========================================================================================
    public sealed class CapabilityInventory
    {
        public int ReadyScouts;        // solo Recce, can act this turn, NOT claimed by an operation
        public int CommittedScouts;    // solo Recce claimed by an active intent (existing != available)
        public int ReserveScouts;      // solo Recce that exists but cannot be tasked this turn
        public int StealthScouts;      // subset of ReadyScouts already hidden or able to enter stealth

        public float FieldCombatPower;         // structurally Raid-eligible ground field power (ready + spent + committed)
        public float CommittedFieldCombatPower;// subset locked to an active mission (Raid / other durable op)
        public float RaidAvailableFieldPower;  // unclaimed Raid-eligible ground power that can still act THIS cycle
        public float GarrisonCombatPower;
        public int AvailableHeroes;    // hero-led Raid-eligible field armies that can act this cycle and are unclaimed
        public int CommittedHeroes;    // hero-led Raid-eligible field armies claimed by an active mission

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

            inv.GarrisonCombatPower = snap.Self.GarrisonPower;

            // Raid supply and Raid provisioning now share the exact same actor-shape predicate.
            // Garrison remains real reserve/potential combat power, but never mobile Raid supply;
            // solo heroes awaiting escort and airfield storage are likewise excluded everywhere.
            float totalGroundPower = 0f;
            float committedPower = 0f;
            float availablePower = 0f;
            int committedHeroes = 0;
            int availableHeroes = 0;

            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (!RaidAssemblyPlanner.IsStructuralRaidActor(a))
                    continue;

                totalGroundPower += a.EffectiveArmyPower;
                bool claimed = commitments != null && commitments.IsArmyClaimed(a.ArmyId);
                if (claimed)
                {
                    committedPower += a.EffectiveArmyPower;
                    if (a.HasHero)
                        committedHeroes++;
                    continue;
                }

                if (!RaidAssemblyPlanner.IsReadyRaidActor(a))
                    continue;

                availablePower += a.EffectiveArmyPower;
                if (a.HasHero)
                    availableHeroes++;
            }

            inv.FieldCombatPower = totalGroundPower;
            inv.CommittedFieldCombatPower = committedPower;
            inv.RaidAvailableFieldPower = Mathf.Max(0f, availablePower);
            inv.AvailableHeroes = availableHeroes;
            inv.CommittedHeroes = committedHeroes;

            inv.ReusableEmptyArmies = ReusableArmySelector.ReusableShells(player, commitments);
            return inv;
        }
    }
}
