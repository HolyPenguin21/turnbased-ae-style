using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Intercept — one capable army (or a same-hex assembly around it) meets the enemy.
    // Return — one army reinforces the threatened own base, or withdraws from a dangerous
    // field position to a safe own base. ReturnPurpose controls their distinct lifecycles.
    // AirSupport — a free wing strikes the same threat in parallel with whatever the ground answer
    // is (Intercept, Regroup, Shortage or no ground responder at all). A technical assignment of
    // the same ActiveDefence objective, with its own intent and key; appended so the numeric values
    // of Intercept/Return never change.
    public enum ActiveDefencePhase { Intercept = 0, Return = 1, AirSupport = 2 }

    // Zero preserves old payloads as withdrawals; new regroup legs always opt in explicitly.
    // GarrisonRelief (2026-10-18) — the Aggression relief mission: a detachment of a safe base's
    // spare garrison bodies walks to a threatened own base and joins its garrison
    // (GarrisonReliefObjectiveEvaluator). It answers no enemy army (EnemyArmyId = -1).
    // Muster (2026-11-19): a fist muster leg (FistMusterObjectiveEvaluator) — an idle army, a
    // garrison detachment or a staging-garrison intake joining the standing fist on the staging Base.
    // It lives for the turn it was admitted only (ImmediateTurn), so the army stays free to compete.
    public enum ActiveDefenceReturnPurpose { SafeWithdrawal = 0, RegroupForAsset = 1, GarrisonRelief = 2, Muster = 3 }

    // Why an Intercept exists. Zero keeps every existing intent a strategic defence of an asset.
    // ImmediateOpportunity is one fight the defender can START in the very own turn it is admitted
    // (a weak field army the strategic threat model does not list): it never pursues over turns,
    // regroups, withdraws, raises demand or calls air support.
    // PreparationStrike is air-only: an independently useful known missile target, with no own asset threat.
    public enum ActiveDefenceInterceptPurpose { StrategicDefence = 0, ImmediateOpportunity = 1, PreparationStrike = 2 }

    public struct ActiveDefenceMissionTarget : IMissionTarget
    {
        MissionKind IMissionTarget.Kind => MissionKind.ActiveDefence;
        public MissionIntentKey IntentKey() => MissionIntentKey.ForActiveDefence(Phase, EnemyArmyId, PrimaryArmyId, ReturnHex);
        public StableMissionKey StableKey() => StableMissionKey.ForActiveDefence(this);

        public ActiveDefencePhase Phase;
        public ActiveDefenceReturnPurpose ReturnPurpose;
        public ActiveDefenceInterceptPurpose InterceptPurpose;
        // ImmediateOpportunity Intercept and Muster Return: the global turn it was admitted in
        // (it ends with that turn).
        public int ImmediateTurn;
        public int? EnemyEta;
        public int EnemyArmyId;
        public HexCoord LastKnownHex;
        public int LastObservedTurn;
        public float Confidence;
        public HexCoord ProtectedAssetHex;
        public AssetKind ProtectedAssetKind;
        public float ProtectedAssetValue;
        public float ThreatSeverity;
        public int? PrimaryArmyId;
        public HexCoord? ReturnHex;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public int EstimatedEta;
        // AirSupport only: the wing (never a ground PrimaryArmyId) and its landing base.
        public int? AirSupportArmyId;
        public HexCoord? AirSupportLandingHex;
        // GarrisonRelief only, before the detachment exists: the donor garrison and the exact spare
        // bodies Provisioning moves into the empty shell PrimaryArmyId. Null once the shell walks.
        public int? ReliefDonorGarrisonId;
        public IReadOnlyList<int> ReliefUnitRuntimeIds;
        // Kept through the intercept, not discarded on materialization: the exact roster remains pinned.
        public GarrisonSortiePlan Sortie;
        public bool SortieMaterialized;
        public bool RequiresSortieMaterialization => Sortie != null && !SortieMaterialized;
        public bool IsMaterializedSortie => Sortie != null && SortieMaterialized;
    }

    // The ONE answer to "how does this player respond to this threat", shared by the Mission
    // planner (what to propose) and Demand (whether to buy). See AssessResponse.
    public enum ActiveDefenceResponseKind
    {
        Intercept,  // a concrete army / same-hex assembly clears the combat gate now
        Defer,      // a capable force exists but cannot act this pass (spent MP, claimed, pinned)
        Regroup,    // sufficient timely reinforcements gather on the threatened own asset
        Shortage,   // no viable timely defence; only endangered armies may withdraw
    }

    public sealed class ActiveDefenceResponse
    {
        public ActiveDefenceResponseKind Kind;
        public IReadOnlyList<WorthIt.DefendingArmy> Opposition;
        // Intercept only: the plan and the exclusion set it was solved under (the same set the
        // proposal's admission fingerprint must use).
        public GroundCombatAssemblyPlan Plan;
        // 2026-11-15 — this Intercept destroys the force that keeps an Attack fist home: it serves
        // that Attack, so taking the preparation's host costs the preparation nothing.
        public bool UnblocksAttack;
        public GarrisonSortiePlan Sortie;
        public ISet<int> ExcludedArmyIds;
        public float RequiredPower;
        public float AvailablePower;
        // Power of the strongest single usable army (sizes a composition shortage).
        public float StrongestPower;
        // Regroup / Shortage: usable field armies that still have to walk; each gets its own
        // one-actor Return leg. Armies already withdrawing are never listed again.
        public readonly List<ArmySnapshot> Movers = new List<ArmySnapshot>();
        // Includes already bound regroup legs, so Continuity can retain only necessary actors.
        public readonly HashSet<int> ReinforcementArmyIds = new HashSet<int>();
        // Regroup (and a Shortage at the own Citadel, 2026-11-18): the exact currently owned
        // protected asset hex the ReinforcementArmyIds walk to.
        public HexCoord? RegroupHex;
        // THE answer to "is this army sent to stand on the protected asset?" — a sufficient
        // Regroup, or the own Citadel's best-effort regroup inside a Shortage (its loss ends the
        // player, so every army that arrives in time stands there even below the hold bar).
        // Read by the planner, Continuity and Provisioning alike.
        public bool LeadsToAsset(int armyId) => RegroupHex.HasValue
            && (Kind == ActiveDefenceResponseKind.Regroup || Kind == ActiveDefenceResponseKind.Shortage)
            && ReinforcementArmyIds.Contains(armyId);
        public string Reason;
        // The defence estimate with the roles the fight really has (our armies DEFEND the asset,
        // with its terrain + structure defence): the chance the force that stands at the asset holds
        // against the enemy fist. -1 when the asset is not a Base / Citadel of ours.
        public float HoldWinChance = -1f;
    }

    public sealed class ActiveDefenceObjective
    {
        public ActiveDefenceMissionTarget Target;
        public TaskScore TaskScore;
        public float BaseValue => TaskScore.Value;
        public MissionIntentKey IntentKey => MissionIntentKey.ForActiveDefence(Target.EnemyArmyId);
    }

    // Threat -> objective is deliberately snapshot-only. One hostile army yields at most one
    // objective: defend its earliest known damage deadline, then use the canonical TaskScore
    // inside that deadline bucket. Unknown ETA never displaces a known arrival.
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        public static List<ActiveDefenceObjective> Enumerate(WorldSnapshot snap)
        {
            var result = new List<ActiveDefenceObjective>();
            IEnumerable<AssetThreatSnapshot> threats = snap?.Threat?.Threats
                ?? Array.Empty<AssetThreatSnapshot>();

            // Analysis includes historical AiReconMemory contacts in Threat. They are honest
            // observations, but are not necessarily present in AiMapMemory's canonical enemy
            // sightings. ActiveDefenceProvisioner requires that canonical sighting to resolve
            // an actual intercept. Filter at objective admission, BEFORE AP allocation; retain
            // historical threats in Analysis for strategic pressure and Recon information needs.
            // AiReconMemory.Historical excludes IDs already in Known.EnemySightings, so an ID
            // membership check identifies precisely the source contract the provisioner uses.
            var interceptableIds = AiV2Util.KnownArmyIds(snap?.Known?.EnemySightings
                ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>());

            foreach (IGrouping<int, AssetThreatSnapshot> group in threats
                .Where(t => IsDefendableAsset(snap, t?.Asset?.Kind, t?.Asset?.Hex)
                    && IsHonestPositionedHostile(t)
                    && interceptableIds.Contains(t.Contact.Army.ArmyId))
                .GroupBy(t => t.Contact.Army.ArmyId))
            {
                EnemyContactSnapshot contact = group.First().Contact;
                if (OnKnownForeignStructure(snap, contact.Position.Value))
                {
                    AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                        $"[AI][V2][ActiveDefence][Objective] decision=DEFER enemy={group.Key} "
                        + "reason=enemy_on_known_foreign_structure attack_owner_required");
                    continue;
                }
                // Damage is an admission gate for EACH pair, before selecting any deadline or
                // score. A harmless pair must never hide another asset this army can damage.
                List<AssetThreatSnapshot> damaging = group.Where(t => t.PotentialDamage > 0f).ToList();
                if (damaging.Count == 0)
                {
                    AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                        $"[AI][V2][ActiveDefence][Objective] decision=DEFER enemy={group.Key} "
                        + "reason=no_potential_damage_to_any_asset");
                    continue;
                }
                // Significance belongs to the physical enemy, not to the representative asset.
                // ImmediateOpportunity retains its separate admission for weak targets.
                float enemyPower = contact.Army.EffectiveArmyPower;
                if (!IsSignificantHostilePower(enemyPower))
                {
                    AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                        $"[AI][V2][ActiveDefence][Objective] decision=DEFER enemy={group.Key} "
                        + $"reason=enemy_below_min_power power={enemyPower:0.0}");
                    continue;
                }
                var representative = AiV2Util.AtEarliestKnownThreatDeadline(damaging)
                    .Select(t => new { Threat = t, Score = BuildActiveDefenceScore(snap, t) })
                    .OrderByDescending(x => x.Score.Value)
                    .ThenByDescending(x => x.Threat.Asset.Value * x.Threat.Severity)
                    // Approach cost is remaining terrain-route MP, not a finer turn deadline.
                    // It breaks strategic ties within one ETA bucket; it does not outrank score.
                    .ThenBy(x => x.Threat.EnemyApproachCost.HasValue ? 0 : 1)
                    .ThenBy(x => x.Threat.EnemyApproachCost.GetValueOrDefault())
                    .ThenBy(x => x.Threat.Asset.Hex.Q).ThenBy(x => x.Threat.Asset.Hex.R)
                    .First();
                AssetThreatSnapshot chosen = representative.Threat;
                TaskScore score = representative.Score;
                var target = new ActiveDefenceMissionTarget
                {
                    Phase = ActiveDefencePhase.Intercept,
                    EnemyArmyId = group.Key,
                    LastKnownHex = chosen.Contact.Position.Value,
                    LastObservedTurn = chosen.Contact.LastObservedTurn,
                    Confidence = chosen.Confidence,
                    ProtectedAssetHex = chosen.Asset.Hex,
                    ProtectedAssetKind = chosen.Asset.Kind,
                    ProtectedAssetValue = chosen.Asset.Value,
                    ThreatSeverity = chosen.Severity,
                    EstimatedEta = chosen.EnemyEta.GetValueOrDefault(),
                    EnemyEta = chosen.EnemyEta,
                };
                result.Add(new ActiveDefenceObjective { Target = target, TaskScore = score });
                AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                    $"[AI][V2][ActiveDefence][Objective] decision=ACCEPT enemy={group.Key} "
                    + $"asset={chosen.Asset.Kind}@({chosen.Asset.Hex.Q},{chosen.Asset.Hex.R}) "
                    + $"enemyEta={chosen.EnemyEta?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} "
                    + $"task={score.Value:0.00} severity={chosen.Severity:0.00}");
            }

            return result.OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Target.EnemyArmyId).ToList();
        }

        // A tracked enemy under the lifecycle the asking intent actually has. StrategicDefence keeps
        // the original Enumerate semantics bit for bit; ImmediateOpportunity reads only the
        // immediate enumeration, so one never answers for the other.
        public static ActiveDefenceObjective ForTrackedEnemy(WorldSnapshot snap, int enemyArmyId,
            ActiveDefenceInterceptPurpose purpose) =>
            purpose == ActiveDefenceInterceptPurpose.ImmediateOpportunity
                ? EnumerateImmediate(snap).FirstOrDefault(o => o.Target.EnemyArmyId == enemyArmyId)
                : ForTrackedEnemy(snap, enemyArmyId);

        // THE significance bar of a hostile contact (2026-10-01: by roster strength, not by skills —
        // a scout or a lone weak body is not worth an army's AP). Attack's local fight and its
        // retreat rule read the same bar instead of inventing a second importance model.
        internal static bool IsSignificantHostilePower(float effectiveArmyPower) =>
            effectiveArmyPower >= AiConfigV2.activeDefenceMinEnemyPower;

        internal static bool OnKnownForeignStructure(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap?.Known?.Buildings;
            if (buildings == null || snap.Observer == null)
                return false;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i].Hex.Equals(hex) && buildings[i].Owner != null
                    && buildings[i].Owner != snap.Observer)
                    return true;
            return false;
        }

        public static ActiveDefenceObjective ForTrackedEnemy(WorldSnapshot snap, int enemyArmyId) =>
            Enumerate(snap).FirstOrDefault(o => o.Target.EnemyArmyId == enemyArmyId);

        // Armies already walking an ActiveDefence Return leg (a regroup or a withdrawal).
        internal static HashSet<int> WithdrawingArmyIds(IEnumerable<MissionIntent> intents) =>
            new HashSet<int>((intents ?? Enumerable.Empty<MissionIntent>())
                .Where(i => i != null && i.Status == IntentStatus.Active
                    && i.ActiveDefence?.Phase == ActiveDefencePhase.Return
                    && i.ActiveDefence.PrimaryArmyId.HasValue)
                .Select(i => i.ActiveDefence.PrimaryArmyId.Value));

        // The live Intercept intent answering this enemy, if any.
        internal static MissionIntent IncumbentIntercept(IEnumerable<MissionIntent> intents,
            int enemyArmyId) =>
            intents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.ActiveDefence
                && i.ActiveDefence?.Phase == ActiveDefencePhase.Intercept
                && i.ActiveDefence.EnemyArmyId == enemyArmyId);

        internal static bool IsDefendableAsset(WorldSnapshot snap, AssetKind? kind, HexCoord? hex) =>
            (kind == AssetKind.Citadel || kind == AssetKind.Base) && hex.HasValue
            && snap?.Self?.BaseHexes?.Contains(hex.Value) == true;

        internal static bool IsHonestPositionedHostile(AssetThreatSnapshot t) =>
            t?.Asset != null && t.Contact?.Army != null
            && t.Contact.Army.ArmyId >= 0
            && t.Contact.Position.HasValue
            && t.Contact.Army.Owner != null && !t.Contact.Army.Owner.IsNeutral;

    }
}

