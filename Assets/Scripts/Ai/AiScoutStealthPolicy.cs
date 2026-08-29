using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Layer-neutral shared primitive (NOT part of V1 or V2 — both depend on it, neither owns it).
    //
    // WHY IT LIVES HERE
    //   V1's AiTurnController.MoveArmyRoutine and V2's ProvisioningManager / TaskExecutor must
    //   answer one question identically: "does this scout's next step carry enough detection risk
    //   to justify paying to slip into stealth first?". If V1 keeps its own copy and V2 calls into
    //   V1, the new architecture inherits a dependency on the legacy slice it is meant to be able
    //   to retire; if the two answer it differently, the AP the allocator reserves for a stealth
    //   transition desyncs from the AP execution actually spends — exactly the estimate-vs-
    //   execution drift V2 was built to make impossible. So the rule sits in one place both sides
    //   call.
    //
    // THE RULE (unchanged from V1's spec item 17, 2026-08-28)
    //   A solo Recce pays the voluntary 1 AP to hide before its NEXT step only when there is a
    //   real reason to:
    //     · a known, non-neutral sighting (honest memory, not live vision) within
    //       AiConfig.scoutFleeRadius of EITHER the scout's current hex or the hex it is about to
    //       step onto — the genuine detection-risk case; or
    //     · that next step leads into a still-cooling scout-danger zone (AiMapMemory.IsScoutDangerous).
    //   A scout crossing empty, known-safe ground stays visible and keeps the AP for another
    //   discovery move. Detection / movement rules are untouched — this only gates the OPTIONAL
    //   EnterStealth, never anything mandatory.
    public static class AiScoutStealthPolicy
    {
        public static bool MoveWarrantsStealth(PlayerSetupData player, ArmyData army, HexCoord nextStep)
        {
            if (army == null)
                return false;
            if (AiMapMemory.IsScoutDangerous(player, nextStep))
                return true;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { army.Hex, nextStep }, AiConfig.scoutFleeRadius))
            {
                if (sighting.Owner != null && sighting.Owner.IsNeutral)
                    continue; // neutrals never threaten a scout — same rule as VisitHexTask.TryFlee
                return true;
            }
            return false;
        }
    }
}
