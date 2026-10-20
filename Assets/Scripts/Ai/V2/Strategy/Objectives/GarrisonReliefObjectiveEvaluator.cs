using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // One known relief opportunity: a detachment of a safe base's spare garrison bodies for a
    // threatened own base whose garrison is below its defence floor.
    internal sealed class GarrisonReliefObjective
    {
        public HexCoord RecipientHex;
        public AssetKind RecipientKind;
        // The recipient's floor shortfall after the reliefs already walking there.
        public float Shortfall;
        public int DonorGarrisonId;
        public HexCoord DonorHex;
        // Snapshot of the detachment as it will stand on the donor hex: the empty shell's id with the
        // chosen bodies' own movement, activation and power (Provisioning moves the bodies in).
        public ArmySnapshot Detachment;
        public IReadOnlyList<int> UnitRuntimeIds;
        public int EtaTurns;
        public TaskScore TaskScore;
    }

    // ===========================================================================================
    //  GARRISON RELIEF (2026-10-18, project owner's decision) — the garrison defence need of one
    //  own base answered by the garrison of another. Need: the held-base garrison shortfall
    //  (AggressionDemandEvaluator.HeldBaseGarrisonShortfall, the floor the hand demand fills) of a
    //  base a known enemy threatens (ArmySnapshot.GarrisonThreatFloor > 0). Supply: a SAFE own
    //  base (GarrisonThreatFloor = 0 — so two threatened bases never trade bodies) lends the spare
    //  bodies its own floor lets go (ArmySnapshot.NonHeroSpareable, AiArmyRoles.SpareableBodies),
    //  strongest first, no more than an empty reusable shell on that hex can carry. Snapshot-pure.
    //  The mission is a GarrisonRelief Return leg (AggressionMissionLayer.AppendGarrisonRelief);
    //  its value is the held-base task score, priced with the detachment's walk.
    // ===========================================================================================
    internal static class GarrisonReliefObjectiveEvaluator
    {
        internal static List<GarrisonReliefObjective> Enumerate(WorldSnapshot snap, ISet<int> committed)
        {
            var result = new List<GarrisonReliefObjective>();
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies;
            IReadOnlyList<HexCoord> bases = snap?.Self?.BaseHexes;
            if (armies == null || bases == null || snap.Observer == null)
                return result;
            List<ArmySnapshot> garrisons = armies.Where(a => a != null && a.IsGarrison
                && bases.Contains(a.Hex)).ToList();
            var usedDonors = new HashSet<int>();
            var usedShells = new HashSet<int>();

            var recipients = garrisons
                .Where(g => g.GarrisonThreatFloor > AiConfigV2.allocatorSliceEpsilon)
                .Select(g => (Garrison: g, Shortfall: AggressionDemandEvaluator.HeldBaseGarrisonShortfall(snap, g, out _)
                    - InFlightReliefPower(snap, g.Hex)))
                .Where(x => x.Shortfall > AiConfigV2.allocatorSliceEpsilon)
                .Select(x => (x.Garrison, x.Shortfall,
                    Score: AggressionDemandEvaluator.BuildHeldBaseGarrisonScore(snap, x.Garrison.Hex)))
                .OrderByDescending(x => x.Score.Value)
                .ThenBy(x => x.Garrison.Hex.Q).ThenBy(x => x.Garrison.Hex.R)
                .ToList();

            foreach (var r in recipients)
            {
                GarrisonReliefObjective best = null;
                foreach (ArmySnapshot donor in garrisons)
                {
                    if (donor.Hex.Equals(r.Garrison.Hex) || usedDonors.Contains(donor.ArmyId)
                        || donor.GarrisonThreatFloor > AiConfigV2.allocatorSliceEpsilon)
                        continue;
                    ArmySnapshot shell = armies.Where(a => ReusableArmySelector.IsReusableShell(a, donor.Hex, committed)
                            && !usedShells.Contains(a.ArmyId))
                        .OrderBy(a => a.ArmyId).FirstOrDefault();
                    GarrisonReliefObjective candidate = shell == null ? null
                        : Detachment(snap, r.Garrison.Hex, r.Shortfall, r.Score, donor, shell);
                    if (candidate != null && (best == null
                            || candidate.EtaTurns < best.EtaTurns
                            || (candidate.EtaTurns == best.EtaTurns
                                && candidate.Detachment.EffectiveArmyPower > best.Detachment.EffectiveArmyPower)))
                        best = candidate;
                }
                if (best == null)
                {
                    AiDebugLog.WriteDeduped($"garrison-relief:{snap.Observer.Nickname}:{r.Garrison.Hex}",
                        $"[AI][V2][GarrisonRelief] decision=NONE recipient=({r.Garrison.Hex.Q},{r.Garrison.Hex.R}) "
                        + $"shortfall={r.Shortfall:0.#} reason=no_safe_donor_with_spare_bodies_and_free_shell");
                    continue;
                }
                best.RecipientKind = r.Garrison.Hex.Equals(snap.Self.Citadel) ? AssetKind.Citadel : AssetKind.Base;
                usedDonors.Add(best.DonorGarrisonId);
                usedShells.Add(best.Detachment.ArmyId);
                result.Add(best);
                AiDebugLog.WriteDeduped($"garrison-relief:{snap.Observer.Nickname}:{r.Garrison.Hex}",
                    $"[AI][V2][GarrisonRelief] decision=PROPOSE recipient=({best.RecipientHex.Q},{best.RecipientHex.R}) "
                    + $"shortfall={best.Shortfall:0.#} donor=#{best.DonorGarrisonId}@({best.DonorHex.Q},{best.DonorHex.R}) "
                    + $"shell=#{best.Detachment.ArmyId} bodies={best.UnitRuntimeIds.Count} "
                    + $"power={best.Detachment.EffectiveArmyPower:0.#} eta={best.EtaTurns} task={best.TaskScore.Value:0.##}");
            }
            return result;
        }

        // Power of the relief detachments already walking to `baseHex` (active GarrisonRelief legs):
        // they cover that share of its shortfall, for the hand demand and for a new relief alike.
        internal static float InFlightReliefPower(WorldSnapshot snap, HexCoord baseHex)
        {
            float power = 0f;
            foreach (MissionIntent i in WorldSnapshot.IntentsOf(snap))
            {
                ActiveDefenceIntent d = i?.ActiveDefence;
                if (d == null || i.Status != IntentStatus.Active || d.Phase != ActiveDefencePhase.Return
                    || d.ReturnPurpose != ActiveDefenceReturnPurpose.GarrisonRelief
                    || !d.ReturnHex.HasValue || !d.ReturnHex.Value.Equals(baseHex) || !d.PrimaryArmyId.HasValue)
                    continue;
                ArmySnapshot mover = snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == d.PrimaryArmyId.Value);
                if (mover != null)
                    power += Mathf.Max(0f, mover.EffectiveArmyPower);
            }
            return power;
        }

        private static GarrisonReliefObjective Detachment(WorldSnapshot snap, HexCoord recipient,
            float shortfall, TaskScore intrinsic, ArmySnapshot donor, ArmySnapshot shell)
        {
            IReadOnlyList<WorthIt.DefenderProfile> bodies = donor.Members;
            if (bodies == null || donor.NonHeroSpareable == null)
                return null;
            var chosen = new List<int>();
            float power = 0f;
            foreach (int i in GarrisonDetachment.SpareBodyIndices(donor))
            {
                if (chosen.Count >= shell.Capacity || power >= shortfall)
                    break;
                chosen.Add(i);
                power = AiPower.EffectiveArmyPowerFromProfiles(chosen.Select(c => bodies[c]).ToList());
            }
            if (chosen.Count == 0)
                return null;
            ArmySnapshot detachment = GarrisonDetachment.Project(donor, chosen, shell.ArmyId, shell.Capacity);
            int eta = AiV2Util.CeilDiv(HexGridMath.Distance(donor.Hex, recipient),
                Mathf.Max(1, detachment.MaxMovement));
            return new GarrisonReliefObjective
            {
                RecipientHex = recipient, Shortfall = shortfall,
                DonorGarrisonId = donor.ArmyId, DonorHex = donor.Hex,
                Detachment = detachment,
                UnitRuntimeIds = chosen.Select(i => donor.NonHeroRuntimeIds[i]).ToList(),
                EtaTurns = eta,
                // No fight on the way: the value is the held-base task itself, priced with the walk.
                TaskScore = TaskScoreEvaluator.WithActorResponse(intrinsic, detachment, 0f, eta),
            };
        }
    }
}
