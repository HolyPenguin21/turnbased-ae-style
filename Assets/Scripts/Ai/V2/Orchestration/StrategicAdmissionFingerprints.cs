using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  One entry point for "the admission key of an axis". The key itself is computed by the axis'
    //  domain owner (DevelopmentAdmission / AggressionAdmission / EconomyAdmission); this type only
    //  selects the owner and builds the physical-stock digest Development and Economy share. It
    //  composes no key of its own and keeps no state.
    // ===========================================================================================
    internal static class StrategicAdmissionFingerprints
    {
        internal static string For(DesireAxis axis, WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, PlayerRoot root, AiHandData hand,
            PlayerSetupData player, AiTurnContext ctx)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Pipeline.AdmissionFingerprint");
            string resources = root == null ? "-" : string.Join(",",
                ResourceBundle.All.Select(t => root.GetResource(t).ToString("0.###",
                    CultureInfo.InvariantCulture)));
            if (axis == DesireAxis.Development)
                return DevelopmentAdmission.Fingerprint(snapshot, activeIntents,
                    root?.ActionPoints ?? 0, resources, hand?.MutationVersion ?? -1, hand, player,
                    root, ctx);
            // T03 — Aggression carries its own inputs.
            if (axis == DesireAxis.Aggression)
                return AggressionAdmission.Fingerprint(snapshot, player);
            return EconomyAdmission.Fingerprint(axis, snapshot, activeIntents, root, hand, player,
                ctx, resources);
        }
    }
}
