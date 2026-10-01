using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATTACK PREPARATION GOES BEFORE FRESH RAIDS
    // ===========================================================================================
    //  2026-10-01 (user decision, playtest #6): with mobilization open, Tessek's first preparation
    //  step (value ~15, 4 AP) lost the allocator's value order to guarded-event Raids (value
    //  18-27 since EventReward) for six turns and never started. A preparation step exists only
    //  while mobilization is open or a preparation is live; while a FRESH one (the first step) is
    //  proposed, every FRESH Raid (no durable intent yet) ranks just below it. A Raid already
    //  under way (a durable intent) keeps its rank; nothing else is reordered.
    // ===========================================================================================
    internal static class AttackPreparationPriority
    {
        internal static bool IsPreparationStep(MissionProposal m) =>
            m != null && m.Kind == MissionKind.Attack && m.Target is AttackMissionTarget t && t.Preparation;

        // Returns how many fresh Raids were ranked below the preparation. Call after
        // EffectiveValue is set; it only lowers EffectiveValue (the allocator's rank key).
        internal static int Apply(IList<MissionProposal> missions)
        {
            if (missions == null)
                return 0;
            // Only a FRESH step (the preparation's first, no intent yet) sets the bar: a live
            // preparation's steps are Hard commitments the allocator funds before every fresh
            // proposal anyway, and their lifecycle value (often 0) would sink the Raids below
            // every other task instead of just below the preparation.
            List<MissionProposal> steps = missions.Where(m => IsPreparationStep(m) && !m.FromDurableIntent).ToList();
            if (steps.Count == 0)
                return 0;
            float bar = steps.Max(m => m.EffectiveValue);
            var demoted = new List<string>();
            foreach (MissionProposal m in missions)
            {
                if (m == null || m.Kind != MissionKind.Raid || m.FromDurableIntent || m.EffectiveValue < bar)
                    continue;
                demoted.Add($"{StableMissionKey.For(m)} {m.EffectiveValue.ToString("0.#", CultureInfo.InvariantCulture)}");
                m.EffectiveValue = bar - AiConfigV2.allocatorSliceEpsilon;
            }
            if (demoted.Count > 0)
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] preparation step "
                    + $"(rank {bar.ToString("0.#", CultureInfo.InvariantCulture)}) goes before fresh raids: "
                    + string.Join("; ", demoted));
            return demoted.Count;
        }
    }
}
