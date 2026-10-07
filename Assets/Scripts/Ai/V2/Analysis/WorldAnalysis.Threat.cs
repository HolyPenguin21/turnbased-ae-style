using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Threat — threat/contact/asset builders.
    // File-split (mechanical, no behaviour change) from WorldAnalysis.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 5. Still exactly the WorldAnalysis
    // class; only this snapshot family's slice moved to its own file.
    public static partial class WorldAnalysis
    {
        private static ThreatModel BuildThreat(PlayerSetupData player, AiTurnContext ctx, WorldSnapshot snap)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Analysis.BuildThreat");
            var model = new ThreatModel();
            var contacts = new List<EnemyContactSnapshot>();

            // A building-bound garrison cannot leave its structure, so it never threatens OUR
            // assets; it is that site's defender package, read directly from the sightings by
            // AttackObjectiveEvaluator.KnownSiteDefenders. Keeps a permanently remembered garrison (AiMapMemory, audit
            // F1) from inflating the defensive reserve / ActiveDefence / Recon contact tracking.
            foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings.Where(x => !x.IsGarrison))
            {
                bool visibleNow = VisionSystem.IsVisible(player, s.Hex);
                contacts.Add(new EnemyContactSnapshot
                {
                    Army = SightingToArmySnapshot(s),
                    Knowledge = visibleNow ? ContactKnowledge.Exact : ContactKnowledge.LastKnown,
                    Position = s.Hex,
                    Confidence = visibleNow ? AiConfigV2.threatConfidenceExact : AiConfigV2.threatConfidenceLastKnown,
                    LastObservedTurn = visibleNow ? snap.TurnNumber : s.SeenTurn,
                });
            }

            var liveArmyIds = AiV2Util.KnownArmyIds(snap.Known.EnemySightings);
            foreach (ReconObservation obs in AiReconMemory.Historical(player, liveArmyIds)
                .Where(o => !o.IsGarrison))
            {
                int age = System.Math.Max(0, snap.TurnNumber - obs.LastObservedTurn);
                contacts.Add(new EnemyContactSnapshot
                {
                    Army = ObservationToArmySnapshot(obs),
                    Knowledge = ContactKnowledge.LastKnown,
                    Position = obs.LastObservedHex,
                    Confidence = AiConfigV2.threatConfidenceLastKnown * AiReconMemory.ConfidenceDecay(age),
                    LastObservedTurn = obs.LastObservedTurn,
                });
            }

            model.Contacts = contacts;

            var assets = new List<StrategicAssetSnapshot>();
            float totalIncome = snap.Self.PerTurnIncome.Sum;

            foreach (BuildingSnapshot b in snap.TrueWorld.AllBuildings.Where(x => x.Owner == player))
            {
                List<WorthIt.DefendingArmy> opposition = snap.Self.Armies
                    .Where(a => a != null && a.Hex.Equals(b.Hex) && !a.IsPrison && !a.IsAir
                        && !a.IsAirfield && a.MemberCount > 0)
                    .OrderBy(a => a.ArmyId)
                    .Select(a => new WorthIt.DefendingArmy(a.Members, a.Commander))
                    .ToList();

                AssetKind kind = ClassifyBuildingAsset(b);
                if (kind == AssetKind.Base || kind == AssetKind.Citadel)
                    AiDebugLog.WriteDeduped($"base:{b.Owner?.Nickname}:{b.Hex.Q}:{b.Hex.R}:{kind}",
                        $"[AI][V2][Base] asset={kind}@({b.Hex.Q},{b.Hex.R}) "
                        + $"owner={b.Owner?.Nickname ?? "none"} authoritativeIsBase={(b.IsBase ? 1 : 0)}");

                assets.Add(new StrategicAssetSnapshot
                {
                    Hex = b.Hex,
                    Kind = kind,
                    HexDefenseBonus = b.Defense,
                    Opposition = opposition,
                    Value = BuildingAssetValue(kind, b, snap, totalIncome),
                });
            }

            foreach (ArmySnapshot a in snap.Self.Armies.Where(x => !x.IsGarrison && !x.IsPrison && x.MemberCount > 0))
            {
                assets.Add(new StrategicAssetSnapshot
                {
                    Hex = a.Hex,
                    Kind = AssetKind.Army,
                    HexDefenseBonus = 0f,
                    Opposition = new[] { new WorthIt.DefendingArmy(a.Members, a.Commander) },
                    Value = Mathf.Min(AiConfigV2.assetValueArmyCap, a.EffectiveArmyPower / AiConfigV2.assetValueArmyPowerDivisor),
                });
            }
            model.Assets = assets;

            var threats = new List<AssetThreatSnapshot>();
            List<ArmySnapshot> ownFieldForResponse = snap.Self.Armies
                .Where(a => !a.IsPrison && !a.IsGarrison && !a.IsAirfield && a.MemberCount > 0).ToList();
            var responseCosts = ownFieldForResponse.ToDictionary(a => a.ArmyId,
                a => TerrainCostField(ctx.Map, a.Hex, Mathf.Max(1, a.MaxMovement), a.IsAir));

            foreach (EnemyContactSnapshot c in contacts)
            {
                // Public terrain only; no live enemy registry or hidden occupancy blockers.
                Dictionary<HexCoord, int> approachCosts = c.Position.HasValue
                    ? TerrainCostField(ctx.Map, c.Position.Value,
                        Mathf.Max(1, c.Army.MaxMovement), c.Army.IsAir)
                    : new Dictionary<HexCoord, int>();
                foreach (StrategicAssetSnapshot asset in assets)
                {
                    int? approachCost = ContactApproachCost(ctx.Map, c, asset.Hex, snap.TurnNumber, approachCosts);
                    // A current observation with no terrain route is not an arriving threat.
                    // Historical contacts remain conservative because they may have moved.
                    if (c.Knowledge == ContactKnowledge.Exact && !approachCost.HasValue) continue;
                    bool canDamage = WorthIt.CanDamageAll(c.Army.Members,
                        WorthIt.UnitsOf(asset.Opposition), asset.HexDefenseBonus);
                    float winChance = WorthIt.EstimateSequential(c.Army.Members, c.Army.Commander,
                        asset.Opposition, asset.HexDefenseBonus).WinChance;

                    int? enemyEta = null;
                    if (approachCost.HasValue)
                        enemyEta = CeilDiv(approachCost.Value, Mathf.Max(1, c.Army.MaxMovement));

                    int? responseEta = null;
                    foreach (ArmySnapshot r in ownFieldForResponse)
                    {
                        if (!responseCosts[r.ArmyId].TryGetValue(asset.Hex, out int cost)) continue;
                        int e = CeilDiv(cost, Mathf.Max(1, r.MaxMovement));
                        if (!responseEta.HasValue || e < responseEta.Value)
                            responseEta = e;
                    }

                    float potentialDamage = winChance * (canDamage ? 1f : 0f);
                    float severity = Severity(winChance, potentialDamage, enemyEta, responseEta, canDamage, c.Confidence);
                    if (severity < AiConfigV2.severityListingCutoff) continue;

                    threats.Add(new AssetThreatSnapshot
                    {
                        Asset = asset,
                        Contact = c,
                        CanDamage = canDamage,
                        EnemyEta = enemyEta,
                        EnemyApproachCost = approachCost,
                        ResponseEta = responseEta,
                        AttackWinChance = winChance,
                        PotentialDamage = potentialDamage,
                        Confidence = c.Confidence,
                        Severity = severity,
                    });
                }
            }
            model.Threats = threats;

            bool derivedSiege = threats.Any(IsSiegeThreat);
            model.UnderSiege = derivedSiege;
            model.CitadelThreatSeverity = MaxSeverity(threats, AssetKind.Citadel);
            model.BaseThreatSeverity = MaxSeverity(threats, AssetKind.Base);

            return model;
        }

        // FindCosts floods the whole public map (maxMovement only rejects single steps), so one
        // field costs a full Dijkstra. It reads terrain only, hence is identical for the same
        // (source, maxMovement, flat) until the map's PathingVersion changes: contacts and own
        // armies keep their position across the refreshes of a turn and across turns. Callers
        // only read the returned field.
        private const int MaxTerrainCostFields = 256;
        private static HexMap _terrainFieldMap;
        private static int _terrainFieldVersion = -1;
        private static readonly Dictionary<(HexCoord, int, bool), Dictionary<HexCoord, int>>
            _terrainFields = new();

        private static Dictionary<HexCoord, int> TerrainCostField(HexMap map, HexCoord source,
            int maxMovement, bool flat)
        {
            if (map == null)
                return new Dictionary<HexCoord, int>();
            if (!ReferenceEquals(map, _terrainFieldMap) || map.PathingVersion != _terrainFieldVersion
                || _terrainFields.Count >= MaxTerrainCostFields)
            {
                _terrainFields.Clear();
                _terrainFieldMap = map;
                _terrainFieldVersion = map.PathingVersion;
            }
            var key = (source, maxMovement, flat);
            if (!_terrainFields.TryGetValue(key, out Dictionary<HexCoord, int> field))
                _terrainFields[key] = field = HexPathfinder.FindCosts(map, new[] { source },
                    maxMovement: maxMovement, flatCost: flat);
            return field;
        }

        // Cost to the asset from the last honest location, discounted by the maximum advance
        // since that observation. A current contact uses the real terrain route; a historical
        // contact whose old origin is now blocked/unreachable falls back to geometric proximity.
        // This models uncertainty, never the enemy's actual hidden position or planned route.
        internal static int? ContactApproachCost(HexMap map, EnemyContactSnapshot contact,
            HexCoord destination, int turn, Dictionary<HexCoord, int> costs = null)
        {
            if (map == null || contact?.Army == null || !contact.Position.HasValue) return null;
            int movement = Mathf.Max(1, contact.Army.MaxMovement);
            costs ??= TerrainCostField(map, contact.Position.Value, movement, contact.Army.IsAir);
            bool reachable = costs.TryGetValue(destination, out int cost);
            if (contact.Knowledge == ContactKnowledge.Exact) return reachable ? cost : (int?)null;
            if (!reachable) cost = HexGridMath.Distance(contact.Position.Value, destination);
            long possibleAdvance = (long)contact.AgeTurns(turn) * movement;
            return (int)System.Math.Max(0L, cost - possibleAdvance);
        }

        internal static bool IsSiegeThreat(AssetThreatSnapshot threat) =>
            threat?.Asset != null
            && (threat.Asset.Kind == AssetKind.Citadel || threat.Asset.Kind == AssetKind.Base)
            && threat.AttackWinChance >= AiConfigV2.siegeEnemyWinChanceThreshold
            && ((threat.EnemyApproachCost.HasValue && threat.EnemyApproachCost.Value <= AiConfigV2.siegeRadius)
                || (threat.EnemyEta.HasValue && threat.EnemyEta.Value <= AiConfigV2.siegeEnemyEtaTurns));

        private static float MaxSeverity(IEnumerable<AssetThreatSnapshot> threats, AssetKind kind) =>
            threats.Where(t => t.Asset.Kind == kind).Select(t => t.Severity)
                .DefaultIfEmpty(0f).Max();

        internal static AssetKind ClassifyBuildingAsset(BuildingSnapshot building)
        {
            if (building == null)
                return AssetKind.Facility;
            if (building.IsStartingCitadel)
                return AssetKind.Citadel;
            return building.IsBase ? AssetKind.Base : AssetKind.Facility;
        }

        private static ArmySnapshot ObservationToArmySnapshot(ReconObservation o)
        {
            var members = o.Defenders != null
                ? new List<WorthIt.DefenderProfile>(o.Defenders)
                : new List<WorthIt.DefenderProfile>();
            return new ArmySnapshot
            {
                ArmyId = o.ArmyId,
                Owner = o.Owner,
                Hex = o.LastObservedHex,
                MemberCount = o.MemberCount,
                HasAntiAir = o.HasAntiAir,
                Commander = o.Commander,
                AttackSum = o.AttackSum,
                DefenseSum = o.DefenseSum,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
                IsAir = o.IsAir,
                MaxMovement = o.MaxMovement,
                Members = members,
            };
        }

        private static ArmySnapshot SightingToArmySnapshot(AiMapMemory.KnownEnemySighting s)
        {
            var members = s.Defenders != null
                ? new List<WorthIt.DefenderProfile>(s.Defenders)
                : new List<WorthIt.DefenderProfile>();
            return new ArmySnapshot
            {
                ArmyId = s.ArmyId,
                Owner = s.Owner,
                Hex = s.Hex,
                MemberCount = s.MemberCount,
                HasAntiAir = s.HasAntiAir,
                IsGarrison = s.IsGarrison,
                Commander = s.Commander,
                AttackSum = s.AttackSum,
                DefenseSum = s.DefenseSum,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
                IsAir = s.IsAir,
                MaxMovement = s.MaxMovement,
                Members = members,
            };
        }

        private static float BuildingAssetValue(AssetKind kind, BuildingSnapshot b, WorldSnapshot snap, float totalIncome)
        {
            switch (kind)
            {
                case AssetKind.Citadel: return AiConfigV2.assetValueCitadel;
                case AssetKind.Base: return AiConfigV2.assetValueBase;
                default:
                    float v = AiConfigV2.assetValueFacilityBase;
                    if (b.HasAbility(UnitAbilities.Barracks))
                        v += AiConfigV2.assetValueFacilityBarracksBonus;
                    if (b.HasAbility(UnitAbilities.Research) || b.HasAbility(UnitAbilities.Production))
                        v += AiConfigV2.assetValueFacilityDevBonus * (snap.Self.HasDevOperator || snap.Self.HasDevFacility ? 1f : 0.3f);
                    for (int i = 0; i < ResourceBundle.All.Length; i++)
                    {
                        ResourceType t = ResourceBundle.All[i];
                        if (b.HasAbility(UnitAbilities.CollectAbilityFor(t)))
                        {
                            float share = totalIncome > 0.0001f ? snap.Self.PerTurnIncome.Get(t) / totalIncome : 0.25f;
                            v += AiConfigV2.assetValueFacilityCollectorBonus * share;
                        }
                    }
                    return v;
            }
        }

        private static float Severity(float winChance, float potentialDamage, int? enemyEta, int? responseEta,
            bool canDamage, float confidence)
        {
            float etaUrgency = enemyEta.HasValue ? 1f / (1f + enemyEta.Value) : 0f;

            float posWeight = AiConfigV2.severityWinChanceWeight + AiConfigV2.severityDamageWeight
                + AiConfigV2.severityEtaWeight + AiConfigV2.severityCanDamageWeight;
            float raw = AiConfigV2.severityWinChanceWeight * winChance
                + AiConfigV2.severityDamageWeight * potentialDamage
                + AiConfigV2.severityEtaWeight * etaUrgency
                + AiConfigV2.severityCanDamageWeight * (canDamage ? 1f : 0f);

            return confidence * Mathf.Clamp01(raw / Mathf.Max(0.0001f, posWeight));
        }

        private static int CeilDiv(int a, int b) => AiV2Util.CeilDiv(a, b);
    }
}
