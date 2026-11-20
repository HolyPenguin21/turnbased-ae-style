using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    internal enum FistMusterKind
    {
        // A free field army (or a lone hero) walks to the staging Base and joins the fist there.
        Walk = 0,
        // A safe garrison elsewhere lends the bodies above its floor: they leave in an empty shell
        // on its hex (the GarrisonRelief extraction) and walk to the staging Base.
        Detach = 1,
        // The staging Base's own garrison hands the bodies above its own share floor (or a
        // commanding hero) straight to the fist standing on the same hex: zero AP, no walk.
        Intake = 2,
    }

    // One muster step: who moves, from where, into the fist on which Base, and what it is worth.
    internal sealed class FistMusterObjective
    {
        public FistMusterKind Kind;
        public HexCoord StagingHex;
        // Walk: the army itself. Detach: the projected detachment (the empty shell's id).
        // Intake: the receiving fist host (or the empty shell that becomes it).
        public ArmySnapshot Mover;
        public int? DonorGarrisonId;
        public IReadOnlyList<int> UnitRuntimeIds;
        public float Power;
        public float Share;
        public int EtaTurns;
        public TaskScore TaskScore;
    }

    // ===========================================================================================
    //  FIST MUSTER (2026-11-19, project owner's decision — match Tessek vs Draven: free armies
    //  idled 50 of 69 army-turns, Atlas 24 turns on (0,-4), garrisons held ~163 power against
    //  ~70 in the field, the strike fist only ever existed inside a preparation opened by the
    //  mobilization gate). Before the gate opens, every idle combat body is brought to ONE fist
    //  on the staging Base — the own Base nearest to the best Attack objective, the same Base a
    //  preparation would pick (AttackPreparationPolicy.PreparationStagingBase).
    //  While it gathers the fist (owner's choice "defence + intercept + opportunity"):
    //   * stands on the staging Base and answers its garrison floor (AiArmyRoles.ResidentFieldPower)
    //     and, as a mobile reserve, the floor of every own Base it reaches before the threat
    //     (AiArmyRoles.MobileReserveCredit);
    //   * stays FREE: a muster leg lives for the turn it was admitted only
    //     (ActiveDefenceContinuity), so an ActiveDefence Intercept, an opportunity Attack or a Raid
    //     compete for the same army on the next pass on their own value, and the army walks back
    //     to the staging Base by the next muster afterwards;
    //   * becomes the preparation host once the gate opens (PreparationFieldHost: the strongest
    //     free field army standing on the staging Base).
    //  The garrison keeps its own share floor (AiArmyRoles.GarrisonDefenceFloor with the
    //  ground force alone): no intake below it, so a fist that leaves never strips the Base bare.
    //  Value: the best objective's intrinsic TaskScore scaled by the share of the strike bar the
    //  step brings, priced like an Attack leg (AiConfigV2.attackDeliveryHorizonTurns).
    // ===========================================================================================
    internal static class FistMusterObjectiveEvaluator
    {
        // The Base the fist gathers on for `objective` (no host: the plain nearest own Base).
        internal static HexCoord? StagingHex(WorldSnapshot snap, AttackObjective objective) =>
            objective == null ? (HexCoord?)null
                : AttackPreparationPolicy.PreparationStagingBase(snap, objective.Hex);

        // A field container that may hold the fist: never a garrison, prison, aircraft, airfield,
        // carrier, dedicated scout, an Economy-protected roster or a facility operator's bench;
        // a lone hero only when it can command (no Support / home-vocation hero). (Not
        // IsMobileEconomyBuilder: Analysis sets it for EVERY hero-led army.) The live twin the
        // arrival join reads is IsLiveFistContainer.
        internal static bool IsFistContainer(ArmySnapshot a) =>
            a != null && !a.IsGarrison && !a.IsPrison && !a.IsAir && !a.IsAirfield
            && !a.IsCarrierContainer && !a.IsSoloRecce
            && !a.EconomyRosterProtected && !a.OperatorDutyBlocksDeparture
            && !(GroundCombatActorEligibility.IsHeroAssemblyHost(a) && a.HeroIsHomeVocation);

        // Live twin of IsFistContainer + the host shape (ActiveDefenceExecutor.JoinFist): a
        // structural ground combat army or a lone commanding hero, no scout, no aircraft, no
        // facility operator of its hex. Claims are the caller's.
        internal static bool IsLiveFistContainer(PlayerSetupData player, ArmyData a)
        {
            if (a == null || a.Members.Count == 0 || a.IsGarrison || a.IsPrison || a.IsAirfield
                || a.IsCarrierContainer || AviationRules.IsAirArmy(a) || AiArmyRoles.IsSoloRecce(a)
                || a.Members.Any(u => u != null && (u.IsAviation
                    || (u.IsHero && AiArmyRoles.IsFacilityOperator(player, a.Hex, u)))))
                return false;
            bool loneHero = a.Members.Count == 1 && a.Members[0] != null && a.Members[0].IsHero;
            return loneHero ? !HeroRoleEvaluator.IsHomeHero(a.Members[0])
                : AiArmyRoles.IsStructuralGroundCombatActor(a);
        }

        // The fist on `staging`: the free field army there with the widest command, then the
        // strongest. Null when none stands there.
        internal static ArmySnapshot FistHost(WorldSnapshot snap, HexCoord staging, ISet<int> committed) =>
            snap?.Self?.Armies?
                .Where(a => IsFistContainer(a) && a.Hex.Equals(staging) && a.MemberCount > 0
                    && (committed == null || !committed.Contains(a.ArmyId))
                    && (a.IsStructuralRaidActor || GroundCombatActorEligibility.IsHeroAssemblyHost(a)))
                .OrderByDescending(a => a.Capacity)
                .ThenByDescending(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .FirstOrDefault();

        // One answer per snapshot, objective and claimed set: every admission pass of the same world
        // asks the same question and each Walk candidate costs a route search (ArrivalEta).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            Dictionary<string, List<FistMusterObjective>>> Memo =
                new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
                    Dictionary<string, List<FistMusterObjective>>>();

        internal static List<FistMusterObjective> Enumerate(WorldSnapshot snap, AttackObjective objective,
            ISet<int> committed)
        {
            if (snap == null || objective == null)
                return new List<FistMusterObjective>();
            string key = $"{objective.IntentKey}|{objective.TaskScore.Value:R}|"
                + string.Join(",", (committed ?? (ISet<int>)new HashSet<int>()).OrderBy(i => i));
            Dictionary<string, List<FistMusterObjective>> byKey = Memo.GetOrCreateValue(snap);
            if (!byKey.TryGetValue(key, out List<FistMusterObjective> cached))
                byKey[key] = cached = EnumerateCore(snap, objective, committed);
            return cached.ToList();
        }

        private static List<FistMusterObjective> EnumerateCore(WorldSnapshot snap, AttackObjective objective,
            ISet<int> committed)
        {
            var result = new List<FistMusterObjective>();
            PlayerSetupData player = snap?.Observer;
            if (player == null || snap.Self?.Armies == null || objective == null)
                return result;
            HexCoord? stagingHex = StagingHex(snap, objective);
            if (!stagingHex.HasValue)
                return result;
            HexCoord staging = stagingHex.Value;
            ArmySnapshot host = FistHost(snap, staging, committed);
            float fist = host?.EffectiveArmyPower ?? 0f;
            float required = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak);
            float gap = Mathf.Max(1f, required - fist);
            int hostCapacity = host?.Capacity ?? 0;
            float objectiveValue = objective.TaskScore.Value;
            if (objectiveValue <= AiConfigV2.allocatorSliceEpsilon)
            {
                Log(snap, staging, host, required, $"no_value objective={objective.Target.DiagnosticLabel} task={objectiveValue:0.##}");
                return result;
            }
            float Share(float power, int capacity) => Mathf.Clamp01(Mathf.Max(0f, power) / gap
                + (capacity > hostCapacity
                    ? AiConfigV2.fistMusterCommandShare * (capacity - hostCapacity) / (float)Mathf.Max(1, capacity)
                    : 0f));
            TaskScore Value(float share) => TaskScore.FromSlots(slot =>
                TaskScoreEvaluator.GroupOf(slot) == TaskSlotGroup.Intrinsic ? objective.TaskScore[slot] * share : 0f);

            // ---- Walk: free field armies and lone heroes standing elsewhere -----------------
            foreach (ArmySnapshot a in GroundCombatActorEligibility.EligibleArmies(snap, committed,
                         requireMovementNow: true, allowHeroAssemblyHost: true))
            {
                if (a.Hex.Equals(staging) || !IsFistContainer(a)
                    || a.ReachableOwnBaseHexes == null || !a.ReachableOwnBaseHexes.Contains(staging))
                    continue;
                float share = Share(a.EffectiveArmyPower, a.HasHero ? a.Capacity : 0);
                int eta = ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, a, staging);
                if (share <= AiConfigV2.allocatorSliceEpsilon || eta == int.MaxValue)
                    continue;
                result.Add(new FistMusterObjective
                {
                    Kind = FistMusterKind.Walk, StagingHex = staging, Mover = a,
                    Power = a.EffectiveArmyPower, Share = share, EtaTurns = eta,
                    TaskScore = TaskScoreEvaluator.WithActorResponse(Value(share), a, 0f, eta,
                        deliveryHorizonTurns: AiConfigV2.attackDeliveryHorizonTurns),
                });
            }

            var usedShells = new HashSet<int>();
            List<ArmySnapshot> garrisons = snap.Self.Armies.Where(g => g != null && g.IsGarrison
                && snap.Self.BaseHexes != null && snap.Self.BaseHexes.Contains(g.Hex)).ToList();

            // ---- Intake: the staging garrison's bodies above its own share floor -------------
            ArmySnapshot stagingGarrison = garrisons.FirstOrDefault(g => g.Hex.Equals(staging));
            if (stagingGarrison != null)
            {
                FistMusterObjective intake = Intake(snap, player, staging, stagingGarrison, host,
                    committed, usedShells, Share, Value);
                if (intake != null)
                    result.Add(intake);
            }

            // ---- Detach: safe garrisons elsewhere lend their spare bodies ------------------
            // A threatened staging garrison is GarrisonRelief's recipient: the same safe donors
            // and shells answer it there (one owner; the leg keys would also coincide).
            int hostFree = host == null ? int.MaxValue : host.FreeBattleSlots;
            bool reliefOwnsDonors = stagingGarrison != null
                && stagingGarrison.GarrisonThreatFloor > AiConfigV2.allocatorSliceEpsilon;
            foreach (ArmySnapshot donor in reliefOwnsDonors ? new List<ArmySnapshot>() : garrisons)
            {
                if (donor.Hex.Equals(staging) || donor.GarrisonThreatFloor > AiConfigV2.allocatorSliceEpsilon
                    || hostFree <= 0)
                    continue;
                ArmySnapshot shell = snap.Self.Armies.Where(a => ReusableArmySelector.IsReusableShell(a, donor.Hex, committed)
                        && !usedShells.Contains(a.ArmyId))
                    .OrderBy(a => a.ArmyId).FirstOrDefault();
                if (shell == null)
                    continue;
                List<int> chosen = ChooseSpare(snap, donor, Mathf.Min(shell.Capacity, hostFree),
                    OwnShareFloor(snap, donor));
                if (chosen.Count == 0)
                    continue;
                ArmySnapshot detachment = GarrisonDetachment.Project(donor, chosen, shell.ArmyId, shell.Capacity);
                detachment.ReachableOwnBaseHexes = snap.Self.BaseHexes;
                int eta = ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, detachment, staging);
                float share = Share(detachment.EffectiveArmyPower, 0);
                if (eta == int.MaxValue || share <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                usedShells.Add(shell.ArmyId);
                result.Add(new FistMusterObjective
                {
                    Kind = FistMusterKind.Detach, StagingHex = staging, Mover = detachment,
                    DonorGarrisonId = donor.ArmyId,
                    UnitRuntimeIds = chosen.Select(i => donor.NonHeroRuntimeIds[i]).ToList(),
                    Power = detachment.EffectiveArmyPower, Share = share, EtaTurns = eta,
                    TaskScore = TaskScoreEvaluator.WithActorResponse(Value(share), detachment, 0f, eta,
                        deliveryHorizonTurns: AiConfigV2.attackDeliveryHorizonTurns),
                });
            }

            Log(snap, staging, host, required, $"objective={objective.Target.DiagnosticLabel} task={objectiveValue:0.##} "
                + $"candidates=[{string.Join(" ", result.Select(Describe))}]");
            return result;
        }

        // The staging garrison's intake: a commanding hero first when the fist has none (a hero
        // sets the capacity), else the spare bodies the fist has room for.
        private static FistMusterObjective Intake(WorldSnapshot snap, PlayerSetupData player,
            HexCoord staging, ArmySnapshot garrison, ArmySnapshot host, ISet<int> committed,
            ISet<int> usedShells, System.Func<float, int, float> share, System.Func<float, TaskScore> value)
        {
            ArmySnapshot receiver = host ?? snap.Self.Armies
                .Where(a => ReusableArmySelector.IsReusableShell(a, staging, committed))
                .OrderBy(a => a.ArmyId).FirstOrDefault();
            // A transfer into an army that already activated this turn charges the newcomers'
            // activation (ArmyActions.TransferMembersApCost): the intake is a zero-AP step only.
            if (receiver == null || receiver.HasActivatedThisTurn)
                return null;
            ArmyData live = OwnWorld.Of(player)?.Army(garrison.ArmyId);
            if (!receiver.HasHero && live != null)
            {
                UnitData hero = AiArmyRoles.BestSparableCommandHero(player, live);
                if (hero != null && hero.CommandRating > receiver.Capacity)
                {
                    float s = share(0f, hero.CommandRating);
                    if (s > AiConfigV2.allocatorSliceEpsilon)
                    {
                        if (host == null) usedShells.Add(receiver.ArmyId);
                        return new FistMusterObjective
                        {
                            Kind = FistMusterKind.Intake, StagingHex = staging, Mover = receiver,
                            DonorGarrisonId = garrison.ArmyId, UnitRuntimeIds = new[] { hero.RuntimeId },
                            Power = 0f, Share = s, EtaTurns = 0,
                            TaskScore = TaskScoreEvaluator.WithResponse(value(s), 0f, 0f, 0f, 0f),
                        };
                    }
                }
            }
            int room = host == null ? receiver.Capacity : receiver.FreeBattleSlots;
            List<int> chosen = ChooseSpare(snap, garrison, room, OwnShareFloor(snap, garrison));
            if (chosen.Count == 0)
                return null;
            float power = AiPower.EffectiveArmyPowerFromProfiles(chosen.Select(i => garrison.Members[i]).ToList());
            float bodiesShare = share(power, 0);
            if (bodiesShare <= AiConfigV2.allocatorSliceEpsilon)
                return null;
            if (host == null) usedShells.Add(receiver.ArmyId);
            return new FistMusterObjective
            {
                Kind = FistMusterKind.Intake, StagingHex = staging, Mover = receiver,
                DonorGarrisonId = garrison.ArmyId,
                UnitRuntimeIds = chosen.Select(i => garrison.NonHeroRuntimeIds[i]).ToList(),
                Power = power, Share = bodiesShare, EtaTurns = 0,
                TaskScore = TaskScoreEvaluator.WithResponse(value(bodiesShare), 0f, 0f, 0f, 0f),
            };
        }

        // The garrison's OWN share floor (the peacetime share of the ground force): what it keeps
        // whoever else stands on its hex, so a departing fist never leaves the Base bare.
        internal static float OwnShareFloor(WorldSnapshot snap, ArmySnapshot garrison) =>
            AiArmyRoles.GarrisonDefenceFloor(snap?.Self?.AvailablePower ?? 0f,
                garrison != null && snap?.Self != null && garrison.Hex.Equals(snap.Self.Citadel));

        // Spare bodies, strongest first: each joins while the set may leave together under the
        // garrison's release rule (GarrisonDetachment.MayRelease) AND what stays still holds the
        // garrison's own share floor. At most `room` bodies.
        private static List<int> ChooseSpare(WorldSnapshot snap, ArmySnapshot donor, int room, float ownFloor)
        {
            var chosen = new List<int>();
            if (donor?.Members == null || room <= 0)
                return chosen;
            List<int> all = Enumerable.Range(0, donor.Members.Count).ToList();
            foreach (int i in GarrisonDetachment.SpareBodyIndices(donor))
            {
                if (chosen.Count >= room)
                    break;
                chosen.Add(i);
                float kept = AiPower.EffectiveArmyPowerFromProfiles(all.Where(k => !chosen.Contains(k))
                    .Select(k => donor.Members[k]).ToList());
                if (!GarrisonDetachment.MayRelease(snap, donor, chosen) || kept + 0.001f < ownFloor
                    || chosen.Count >= donor.Members.Count)
                    chosen.RemoveAt(chosen.Count - 1);
            }
            return chosen;
        }

        private static string Describe(FistMusterObjective o) =>
            $"{o.Kind}:#{o.Mover?.ArmyId}{(o.DonorGarrisonId.HasValue ? $"<g#{o.DonorGarrisonId}" : "")}"
            + $"/p{o.Power:0.#}/s{o.Share:0.##}/eta{o.EtaTurns}/v{o.TaskScore.Value:0.##}";

        private static void Log(WorldSnapshot snap, HexCoord staging, ArmySnapshot host, float required, string tail) =>
            AiDebugLog.WriteDeduped($"muster#{snap.Observer?.ColorIndex}",
                $"[AI][V2][Muster] staging=({staging.Q},{staging.R}) host={(host == null ? "none" : $"#{host.ArmyId}")} "
                + $"fist={(host?.EffectiveArmyPower ?? 0f):0.#}/{required:0.#} cap={(host?.Capacity ?? 0)} {tail}");
    }
}
