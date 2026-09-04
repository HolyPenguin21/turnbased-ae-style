using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §24/§28 — the reaction witness selector. Given every ReactionWitness the probe found,
    // it applies the canonical, deterministic arbitration and turns the winner into the bounded
    // reservation offer:
    //     1. minimum full RequiredAp
    //     2. minimum persistent-resource opportunity cost (envelope cost)
    //     3. stable ActionKey
    // No fixed "Discovery before Hand" (or the reverse) reason priority. Extracted verbatim from
    // StrategicReactionPass.BuildReactionOpportunity.
    internal static class ReactionWitnessSelector
    {
        internal static StrategicReactionOpportunity Select(IReadOnlyList<ReactionWitness> witnesses,
            float ceiling, PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            // round 10 (P0.1) — rank and gate on the witness's FULL RequiredAp (downstream/move
            // envelope already folded in). A witness whose RequiredAp exceeds the ceiling is dropped
            // outright — never clamped down and then treated as "protected". P1 — the envelope-
            // spendable check excludes THIS witness's own prospective reservation OWNER (not merely
            // its shared Reason), so the §6 re-probe of an already-placed budget does not fail
            // against itself and two distinct reaction owners cannot shadow each other.
            var feasible = witnesses
                .Where(w => w.RequiredAp <= ceiling + 0.001f)
                .Where(w => w.Envelope == null
                    || StrategicSpendability.FitsSpendableResources(player, root, ctx, w.Envelope, w.OwnerKey))
                .OrderBy(w => w.RequiredAp)
                .ThenBy(w => w.EnvelopeCost)
                .ThenBy(w => w.ActionKey, System.StringComparer.Ordinal)
                .ToList();
            if (feasible.Count == 0)
                return StrategicReactionOpportunity.None(witnesses.Count == 0
                    ? "noFeasibleReaction(no witness from any enabled source)"
                    : $"noFeasibleReaction({witnesses.Count} witness(es), none whose full RequiredAp fits "
                        + $"the ceiling {ceiling:0.#} + spendable envelope)");

            ReactionWitness win = feasible[0];
            float budget = win.RequiredAp; // reserve EXACTLY what the protected reaction needs (<= ceiling)
            string rationale = $"{win.Detail}; reserve {budget:0.#} AP (full RequiredAp); "
                + $"basis T{win.StateBasis.TurnNumber}/iv{win.StateBasis.InterruptVersion}/ap{win.StateBasis.ApAtProbe:0.#}";
            if (feasible.Count > 1)
                rationale += $"; chosen over {feasible.Count - 1} other feasible witness(es) by (RequiredAp, envelope cost, key)";
            return new StrategicReactionOpportunity(true, win.OwnerKey, win.Kind,
                budget, win.Envelope, win.StateBasis, rationale, null);
        }
    }
}
