using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    internal static partial class GroundCombatAirSupport
    {
        internal static bool IsStockMissileGroup(IReadOnlyList<UnitData> aircraft) =>
            aircraft?.Count == 1 && AviationRules.IsAviation(aircraft[0])
            && ExplosionAttack.IsSingleUseUnit(aircraft[0])
            && ExplosionAttack.TargetLimit(aircraft[0].Abilities) > 0;

        private static bool IsStockMissile(CardDefinition card) => card?.isAviation == true
            && ExplosionAttack.IsSingleUseUnit(card) && ExplosionAttack.TargetLimit(card.grantedAbilities) > 0;

        // One reserve slot per occupied hex, using only recent observed enemy-player armies.
        internal static List<CombatAirSupportRequest> MissileStockTargets(WorldSnapshot snap)
        {
            if (snap?.Observer == null) return new List<CombatAirSupportRequest>();
            return (snap.Known?.AirSightings ?? Array.Empty<AiMapMemory.KnownAirSighting>())
                .Where(s => s.Owner != null && s.Owner != snap.Observer && !s.Owner.IsNeutral
                    && !s.Owner.IsEliminated && s.IsGroundArmy && s.Roster.Units.Any(p => p.HitPoints > 0)
                    && (snap.TurnNumber - s.SeenTurn <= AiConfig.enemySightingMemoryTurns
                        || snap.Known.EnemySightings?.Any(e => e.ArmyId == s.ArmyId && e.IsGarrison) == true))
                .GroupBy(s => s.Hex).Select(g => g.OrderBy(s => s.ArmyId).First())
                .OrderBy(s => s.ArmyId)
                .Select(s => new CombatAirSupportRequest(default, s.Hex,
                    new AirStrikePolicy(AirStrikePolicyKind.Standard, s.ArmyId),
                    new TaskScore(strategicRelevance: TaskScoreEvaluator.StrategicRelevance(1f),
                        intelAgePenalty: TaskScoreEvaluator.IntelAgePenalty(
                            Math.Max(0, snap.TurnNumber - s.SeenTurn)
                            / (float)Mathf.Max(1, AiConfigV2.reconIntelStaleTurnsHi))), s.ArmyId)).ToList();
        }

        // Air-only union: an independent preparation strike shares the existing AirSupport
        // operation identity and lifecycle, without inventing a threatened own asset. Ground
        // defence and its objective reader remain unchanged. One target hex gets one air request.
        internal static List<ActiveDefenceObjective> AirSupportObjectives(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> intents = null)
        {
            var result = ActiveDefenceObjectiveEvaluator.Enumerate(snap);
            var covered = new HashSet<HexCoord>(result.Select(o => o.Target.LastKnownHex));
            foreach (MissionIntent intent in intents ?? WorldSnapshot.IntentsOf(snap).ToList())
                if (intent?.Status == IntentStatus.Active)
                {
                    if (intent.Attack?.AirSupportArmyId.HasValue == true) covered.Add(intent.Attack.Target.Hex);
                    if (intent.Raid?.AirSupportArmyId.HasValue == true) covered.Add(intent.Raid.LastKnownHex);
                }
            foreach (CombatAirSupportRequest request in MissileStockTargets(snap))
                if (covered.Add(request.Target)) result.Add(PreparationStrikeObjective(snap, request));
            return result;
        }

        private static ActiveDefenceObjective PreparationStrikeObjective(WorldSnapshot snap,
            CombatAirSupportRequest request)
        {
            var sighting = snap.Known.AirSightings.First(s => s.ArmyId == request.StockEnemyArmyId);
            return new ActiveDefenceObjective { TaskScore = request.Score,
                Target = new ActiveDefenceMissionTarget { Phase = ActiveDefencePhase.AirSupport,
                    InterceptPurpose = ActiveDefenceInterceptPurpose.PreparationStrike,
                    EnemyArmyId = request.StockEnemyArmyId.Value, LastKnownHex = request.Target,
                    LastObservedTurn = sighting.SeenTurn, Confidence = 1f } };
        }

        internal static ActiveDefenceObjective AirSupportObjective(WorldSnapshot snap, int enemyId,
            ActiveDefenceInterceptPurpose purpose)
        {
            if (purpose != ActiveDefenceInterceptPurpose.PreparationStrike)
                return ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap, enemyId);
            foreach (CombatAirSupportRequest request in MissileStockTargets(snap))
                if (request.StockEnemyArmyId == enemyId) return PreparationStrikeObjective(snap, request);
            return null;
        }

        internal static AirStrikePolicy AirSupportPolicy(ActiveDefenceMissionTarget target) =>
            target.InterceptPurpose == ActiveDefenceInterceptPurpose.PreparationStrike
                ? new AirStrikePolicy(AirStrikePolicyKind.Standard, target.EnemyArmyId)
                : AirStrikePolicy.DefenceSupport(target.EnemyArmyId);

        internal static CombatAirSupportRequest AirSupportRequest(ActiveDefenceMissionTarget target,
            TaskScore score = default, HexCoord? currentHex = null) => new CombatAirSupportRequest(target.IntentKey(),
                currentHex ?? target.LastKnownHex, AirSupportPolicy(target), score,
                target.InterceptPurpose == ActiveDefenceInterceptPurpose.PreparationStrike
                    ? (int?)target.EnemyArmyId : null);

        // Re-read only our own roster and canonical observed knowledge at the action boundary.
        // Earlier actions may have reduced the blast value or moved friendly collateral onto it.
        internal static bool PreparationStrikeUsefulLive(PlayerSetupData player, AiTurnContext ctx,
            ActiveDefenceMissionTarget target, ArmyData source)
        {
            if (target.InterceptPurpose != ActiveDefenceInterceptPurpose.PreparationStrike) return true;
            if (player == null || ctx?.Map == null || source == null) return false;
            var snap = new WorldSnapshot { Observer = player, Map = ctx.Map, TurnNumber = ctx.TurnNumber,
                Self = new SelfSnapshot { Armies = OwnWorld.Of(player).Armies
                    .Select(a => WorldAnalysis.ToArmySnapshot(a, player, true, 0, null, ctx)).ToList() },
                Known = WorldAnalysis.BuildKnown(player, null) };
            var request = AirSupportRequest(target);
            if (!MissileStockTargets(snap).Any(r => r.StockEnemyArmyId == target.EnemyArmyId
                    && r.Target.Equals(target.LastKnownHex))) return false;
            return AviationRules.IsAirfield(source)
                ? StoredSupportGroup(snap, source, target.LastKnownHex, request.Policy, out _, request).Count > 0
                : ProjectService(snap, player, ctx.Map, source.Members, source.Hex, request, source).HasValue;
        }

        // Resolve eligibility per army before the uniform ten-target draw. A protected hero
        // neither takes damage nor dilutes the pool; all other known occupants can be hit.
        internal static float MissileStockValue(WorldSnapshot snap, IReadOnlyList<UnitData> aircraft,
            HexCoord hex, int? wingId = null)
        {
            if (!IsStockMissileGroup(aircraft)) return float.NegativeInfinity;
            // The optional preparation strike never knowingly sacrifices any owned Base.
            // Net collateral pricing still decides ordinary friendly bodies/facilities elsewhere.
            if ((snap?.Known?.Buildings ?? Array.Empty<AiMapMemory.KnownBuilding>()).Any(b =>
                    b.Hex.Equals(hex) && b.Owner == snap.Observer && (b.IsBase || b.IsStartingCitadel)))
                return float.NegativeInfinity;
            var pool = new List<AviationCombatEstimator.ExplosionCollateral>();
            foreach (var enemy in KnownAirTargets(snap, hex, AirStrikePolicy.Standard))
            {
                int living = enemy.Roster.Units.Count(p => p.HitPoints > 0);
                foreach (var unit in enemy.Roster.Units)
                    if (unit.HitPoints > 0 && AviationRules.CanTargetFromAir(unit.IsHero, living))
                        pool.Add(new AviationCombatEstimator.ExplosionCollateral(unit, false, armyId: enemy.ArmyId));
            }
            foreach (var own in snap?.Self?.Armies ?? Array.Empty<ArmySnapshot>())
            {
                if (!own.Hex.Equals(hex) || own.ArmyId == wingId) continue;
                var members = own.MembersWithHeroes?.Count > 0 ? own.MembersWithHeroes
                    : own.Members ?? Array.Empty<WorthIt.DefenderProfile>();
                int living = members.Count(p => p.HitPoints > 0);
                ArmyData source = snap.Observer == null ? null : OwnWorld.Of(snap.Observer).Army(own.ArmyId);
                // A stored launcher on this hex is consumed, never struck by its own explosion.
                int actorIndex = source?.Members.IndexOf(aircraft[0]) ?? -1;
                for (int i = 0; i < members.Count; i++)
                    if (i != actorIndex && members[i].HitPoints > 0
                        && AviationRules.CanTargetFromAir(members[i].IsHero, living))
                        pool.Add(new AviationCombatEstimator.ExplosionCollateral(members[i], true, armyId: own.ArmyId));
            }
            foreach (var building in snap?.Known?.Buildings ?? Array.Empty<AiMapMemory.KnownBuilding>())
                if (building.Hex.Equals(hex))
                {
                    int index = 0;
                    foreach (var structure in building.ExplosionStructures ?? Array.Empty<WorthIt.DefenderProfile>())
                        pool.Add(new AviationCombatEstimator.ExplosionCollateral(structure,
                            building.Owner == snap.Observer, index++ == 0 && building.IsStartingCitadel
                                && building.Owner == snap.Observer));
                }
            return ExplosionAttack.ExpectedNetValue(WorthIt.FromLiveUnit(aircraft[0]), pool);
        }

        // Absolute net damage value: adding bodies beyond the ten-target limit cannot dilute
        // a useful army's service score merely by increasing its total HP denominator.
        internal static float MissileStockEffect(float netValue, UnitData missile) => Mathf.Clamp01(netValue
            / Mathf.Max(1f, ExplosionAttack.TargetLimit(missile.Abilities) * AiConfigV2.combatPowerPerBodyEstimate));

        internal static List<CombatAirSupportRequest> UncoveredMissileStock(WorldSnapshot snap,
            IReadOnlyList<UnitData> candidate, IReadOnlyList<CombatAirSupportRequest> requests,
            IReadOnlyList<CardData> hand, HexMap map, CardData excludeCard = null)
        {
            if (!IsStockMissileGroup(candidate)) return requests.Where(r => !r.IsStockPreparation).ToList();
            var eligible = requests.Where(r => r.Policy.MinimumSurvivors == 0
                    && MissileStockValue(snap, candidate, r.Target) >= MinimumValue(r))
                .GroupBy(r => r.Target).Select(g => g.OrderBy(r => r.IsStockPreparation)
                    .ThenByDescending(r => r.Score.Value).First())
                .OrderByDescending(r => MissileStockValue(snap, candidate, r.Target))
                .ThenBy(r => r.Identity, StringComparer.Ordinal).ToList();
            if (eligible.Count == 0) return eligible;
            var supplies = new List<(IReadOnlyList<UnitData> Aircraft, ArmyData Army)>();
            foreach (ArmyData army in OwnWorld.Of(snap.Observer).Armies.Where(a => !a.IsPrison))
                foreach (UnitData unit in army.Members.Where(u => u.HitPointsCurrent > 0))
                    if (IsStockMissileGroup(new[] { unit })) supplies.Add((new[] { unit }, army));
            // Oldest hand card deploys first. Later cards must not block it, while existing
            // earlier cards suppress new manufacture. A blueprint is never physical stock.
            foreach (CardData card in hand ?? Array.Empty<CardData>())
                if (card != excludeCard && IsStockMissile(card?.Definition)
                    && (excludeCard == null || card.RuntimeId < excludeCard.RuntimeId))
                    supplies.Add((NonCombatCardPlayer.ProjectedAircraft(card.Definition, snap.Observer), null));
            supplies.RemoveAll(s => !eligible.Any(r => MissileStockValue(snap, s.Aircraft, r.Target) >= MinimumValue(r)));
            // Stranded but useful missiles remain inventory: a temporary route/capacity loss
            // must not trigger unlimited production. No reservation is made for a forecast.
            if (supplies.Count >= eligible.Count) return new List<CombatAirSupportRequest>();
            bool CanCover(int request, int supply)
            {
                var s = supplies[supply];
                var r = eligible[request];
                if (MissileStockValue(snap, s.Aircraft, r.Target) < MinimumValue(r)) return false;
                if (s.Army == null)
                    return AiAirSortiePlanner.OwnedAirfieldHexes(snap.Observer).Any(h =>
                        AiAirSortiePlanner.FreeLandingCapacity(h, snap.Observer) > 0
                        && StockCanReach(snap.Observer, map, s.Aircraft, h, r.Target));
                AirSortie sortie = AirSortieRegistry.ForArmy(snap.Observer, s.Army);
                if (sortie?.Kind == AirSortieKind.Strike)
                    return (sortie.StrikePolicy.ExactTargetArmyId == r.StockEnemyArmyId
                        && sortie.StrikePolicy.ExactTargetArmyId.HasValue) || sortie.TargetHex.Equals(r.Target);
                HexCoord start = sortie?.Kind == AirSortieKind.Rebase ? sortie.LandingHex : s.Army.Hex;
                return StockCanReach(snap.Observer, map, s.Aircraft, start, r.Target);
            }
            var matched = MatchCoverage(eligible.Count, supplies.Count, CanCover, _ => true);
            var covered = new HashSet<int>(matched.Values);
            return eligible.Where((r, index) => !covered.Contains(index)).ToList();
        }

        private static float MinimumValue(CombatAirSupportRequest request) => request.IsStockPreparation
            ? AiConfigV2.missileStockMinNetValue : AiConfigV2.allocatorSliceEpsilon;

        private static bool StockCanReach(PlayerSetupData player, HexMap map,
            IReadOnlyList<UnitData> aircraft, HexCoord source, HexCoord target)
        {
            if (map == null || !AviationRules.IsOwnedAirfieldAt(source, player)) return false;
            var refreshed = AviationRebasePlanner.ProjectAfterLanding(aircraft);
            bool Direct(HexCoord from) => AiAirSortiePlanner.TryPlanSortieFromStorage(from,
                    refreshed, target, map, player, singleUseStrike: true).HasValue
                || AiAirSortiePlanner.TryPlanMultiTurnSortieFromStorage(from,
                    refreshed, target, map, player, singleUseStrike: true).HasValue;
            return Direct(source) || AviationRebasePlanner.DeliveryStops(source, refreshed, map, player)
                .Any(stop => Direct(stop.Hex));
        }

        // Raw observable dependencies for admission/Fate caches; no simulation or route search.
        internal static string MissileStockFacts(WorldSnapshot snap, PlayerSetupData player)
        {
            string Profiles(IEnumerable<WorthIt.DefenderProfile> profiles) => string.Join(";",
                (profiles ?? Array.Empty<WorthIt.DefenderProfile>()).Select(p =>
                    ProfileRowCache.Get(p, ProfileRowCache.Format.MissileStock, MissileProfileRow)));
            string contacts = string.Join("|", (snap?.Known?.AirSightings ?? Array.Empty<AiMapMemory.KnownAirSighting>())
                .OrderBy(s => s.ArmyId).Select(s => $"{s.ArmyId}:{s.Hex}:{s.SeenTurn}:{s.IsGroundArmy}:"
                    + $"{s.Owner == player}:{s.Owner?.IsNeutral}:{s.Owner?.IsEliminated}:"
                    + $"garrison={snap.Known.EnemySightings?.Any(e => e.ArmyId == s.ArmyId && e.IsGarrison)}:"
                    + $"{s.Roster.CommanderIndex}:{string.Join(",", s.Roster.CurrentFates)}:{Profiles(s.Roster.Units)}"));
            string own = string.Join("|", (snap?.Self?.Armies ?? Array.Empty<ArmySnapshot>()).OrderBy(a => a.ArmyId)
                .Select(a => $"{a.ArmyId}:{a.Hex}:" + Profiles(a.MembersWithHeroes?.Count > 0 ? a.MembersWithHeroes : a.Members)));
            string structures = string.Join("|", (snap?.Known?.Buildings ?? Array.Empty<AiMapMemory.KnownBuilding>())
                .OrderBy(b => b.Hex.Q).ThenBy(b => b.Hex.R).Select(b =>
                    $"{b.Hex}:{b.Owner == player}:{b.IsStartingCitadel}:{Profiles(b.ExplosionStructures)}"));
            string inventory = player == null ? "" : string.Join("|", OwnWorld.Of(player).Armies.OrderBy(a => a.Id)
                .Where(a => a.Members.Any(u => IsStockMissileGroup(new[] { u }))).Select(a =>
                {
                    var sortie = AirSortieRegistry.ForArmy(player, a);
                    return $"{a.Id}:{a.Hex}:{a.IsPrison}:{sortie?.Kind}:{sortie?.TargetHex}:{sortie?.LandingHex}:"
                        + $"{sortie?.StrikePolicy.ExactTargetArmyId}:" + string.Join(";", a.Members.Select(u =>
                            $"{u.RuntimeId}:{u.MoveCurrent}:{u.MoveMax}:{u.TurnsWithoutRefuel}:{u.ConsecutiveUnlandedEnds}:"
                            + $"{u.SortieLaunchPaid}:{u.HasAirAttackedThisTurn}:{Profiles(new[] { WorthIt.FromLiveUnit(u) })}"));
                }));
            string held = string.Join("|", (snap?.Self?.Hand ?? Array.Empty<CardData>())
                .Where(c => IsStockMissile(c?.Definition)).OrderBy(c => c.RuntimeId)
                .Select(c => $"{c.RuntimeId}:{c.Definition.authoredKey}:{c.Definition.attack}:{c.Definition.defenseRating}:"
                    + $"{c.Definition.hitPoints}:{c.Definition.initiative}:"
                    + string.Join(",", c.Definition.grantedAbilities.OrderBy(a => a, StringComparer.Ordinal))));
            return $"turn={snap?.TurnNumber}|contacts={contacts}|own={own}|structures={structures}|inventory={inventory}|hand={held}";
        }

        private static string MissileProfileRow(WorthIt.DefenderProfile p) => string.Join("/", new[] {
            p.Attack.ToString("R", CultureInfo.InvariantCulture), p.Defense.ToString("R", CultureInfo.InvariantCulture),
            p.HitPoints.ToString("R", CultureInfo.InvariantCulture), p.MaxHitPoints.ToString("R", CultureInfo.InvariantCulture),
            p.Initiative.ToString(), p.IsHero.ToString(), p.FateMax.ToString(), p.SingleUseUnit.ToString(),
            string.Join(",", p.TypeTags ?? Array.Empty<UnitTypeTag>()),
            string.Join(",", (p.Abilities ?? Array.Empty<string>()).OrderBy(a => a, StringComparer.Ordinal))
        });
    }
}
