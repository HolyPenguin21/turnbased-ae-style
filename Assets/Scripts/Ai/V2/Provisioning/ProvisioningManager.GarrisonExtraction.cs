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
    // Economy/Development garrison extraction at Provisioning time: the identity of a builder that
    // does not exist yet and the preview of its container. The container choice itself is
    // ReusableArmySelector.ResolveGarrisonExtraction; the transfer is EconomyExecutor's.

    internal static partial class ProvisioningManager
    {
        // The MoverArmyId of a funded builder still inside its garrison: Provisioning prices the
        // extraction, EconomyExecutor / DevelopmentExecutor perform it as their own step.
        internal static int SyntheticGarrisonExtractionActorId(int garrisonArmyId) =>
            -(3_000_000 + (garrisonArmyId & 0xFFFFFF));

        // Builds a READ-ONLY preview of what the deferred garrison-extraction container will look
        // like immediately after the (real) hero transfer, so PlanEconomyCompletion can compute the
        // FULL composition/donor/AP decision at Provisioning time against ArmyData's own real
        // Members/MaxMovement/CurrentMovement/HasActivatedThisTurn projections — no duplicated
        // math, no live mutation. ArmyData.CreateVisualSnapshot() never touches ArmyRegistry or
        // burns a real Id (Id stays -1), which is why callers pass `identityArmyId` instead (see
        // PlanEconomyArmyLightening).
        private static ArmyData BuildGarrisonExtractionPreview(
            PlayerSetupData player, ArmyData garrison, ReusableArmySelector.GarrisonExtractionCandidate plan)
        {
            ArmyData preview = ArmyData.CreateVisualSnapshot();
            preview.Hex = garrison.Hex;
            preview.Owner = player;
            if (plan.Container != null)
            {
                preview.Members.AddRange(plan.Container.Members);
                // Propagate the REAL container's activation state so the projected AP/charge math
                // (EconomyMissionClaimedAp, ArmyActions.RequiresActivationCharge-style reasoning)
                // matches what Execution will actually see once the hero is really transferred in —
                // MarkActivated covers exactly the pre-existing members, matching a container that
                // already moved this turn; the hero appended below is deliberately NOT covered, same
                // as any brand-new join into an activated army.
                if (plan.Container.HasActivatedThisTurn)
                    preview.MarkActivated();
            }
            preview.Members.Add(plan.Hero);
            return preview;
        }
    }
}
