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
            var model = new ThreatModel();
            var contacts = new List<EnemyContactSnapshot>();

            foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings)
            {
                bool visibleNow = VisionSystem.IsVisible(player, s.Hex);
                contacts.Add(new EnemyContactSnapshot
                {
                    Army = SightingToArmySnapshot(s),
                    Knowledge = visibleNow ? ContactKnowledge.Exact : ContactKnowledge.LastKnown,
                    Source = ContactSource.Honest,
                    Position = s.Hex,
                    Confidence = visibleNow ? AiConfigV2.threatConfidenceExact : AiConfigV2.threatConfidenceLastKnown,
                    LastObservedTurn = visibleNow ? snap.TurnNumber : s.SeenTurn,
                });
            }

            var liveArmyIds = new HashSet<int>(snap.Known.EnemySightings.Select(s => s.ArmyId));
            foreach (ReconObservation obs in AiReconMemory.Historical(player, liveArmyIds))
            {
                int age = System.Math.Max(0, snap.TurnNumber - obs.LastObservedTurn);
                contacts.Add(new EnemyContactSnapshot
                {
                    Army = ObservationToArmySnapshot(obs),
                    Knowledge = ContactKnowledge.LastKnown,
                    Source = ContactSource.Honest,
                    Position = obs.LastObservedHex,
                    Confidence = AiConfigV2.threatConfidenceLastKnown * AiReconMemory.ConfidenceDecay(age),
                    LastObservedTurn = obs.LastObservedTurn,
                });
            }

            foreach (HexCoord home in snap.Self.BaseHexes)
            {
                if (AiMapMemory.HasKnownEnemyWithin(player, home, AiConfig.defenceReactionRadius))
                    continue;

                ArmySnapshot strongest = null;
                float strongestSum = 0f;
                foreach (ArmySnapshot ea in snap.TrueWorld.EnemyArmies)
                {
                    if (ea.IsGarrison || ea.MemberCount == 0 || ea.MemberCount > AiConfig.makeshiftScoutMinMembers)
                        continue;
                    if (HexGridMath.Distance(home, ea.Hex) > AiConfig.defenceReactionRadius)
                        continue;
                    float sum = ea.AttackSum + ea.DefenseSum;
                    if (sum > strongestSum)
                    {
                        strongestSum = sum;
                        strongest = ea;
                    }
                }
                if (strongest != null)
                    contacts.Add(MakeCheatContact(strongest, home, AiConfig.defenceReactionRadius));
            }
            model.Contacts = contacts;

            var byArmy = new Dictionary<int, EnemyContactSnapshot>();
            foreach (EnemyContactSnapshot c in contacts)
            {
                if (c.Source != ContactSource.Honest || !c.Position.HasValue) continue;
                int id = c.Army?.ArmyId ?? 0;
                if (id <= 0) continue;
                if (!byArmy.TryGetValue(id, out EnemyContactSnapshot cur) || c.LastObservedTurn > cur.LastObservedTurn)
                    byArmy[id] = c;
            }
            model.ReconContactByArmyId = byArmy;

            var assets = new List<StrategicAssetSnapshot>();
            float totalIncome = snap.Self.PerTurnIncome.Sum;

            foreach (BuildingSnapshot b in snap.TrueWorld.AllBuildings.Where(x => x.Owner == player))
            {
                ArmySnapshot garrison = snap.Self.Armies.FirstOrDefault(a => a.IsGarrison && a.Hex.Equals(b.Hex));
                var defenders = garrison?.Members ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
                float garrisonDef = defenders.Sum(d => d.Defense);

                AssetKind kind = b.IsStartingCitadel ? AssetKind.Citadel
                    : b.HasFacilityAbility(UnitAbilities.Barracks) ? AssetKind.Base
                    : AssetKind.Facility;

                assets.Add(new StrategicAssetSnapshot
                {
                    Hex = b.Hex,
                    Kind = kind,
                    HexDefenseBonus = b.Defense,
                    Defense = b.Defense + garrisonDef,
                    Defenders = defenders,
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
                    Defense = a.DefenseSum,
                    Defenders = a.Members,
                    Value = Mathf.Min(AiConfigV2.assetValueArmyCap, a.EffectiveArmyPower / AiConfigV2.assetValueArmyPowerDivisor),
                });
            }
            model.Assets = assets;

            var threats = new List<AssetThreatSnapshot>();
            List<ArmySnapshot> ownFieldForResponse = snap.Self.Armies
                .Where(a => !a.IsPrison && a.MemberCount > 0).ToList();

            foreach (EnemyContactSnapshot c in contacts)
            {
                foreach (StrategicAssetSnapshot asset in assets)
                {
                    bool canDamage = WorthIt.CanDamageAll(c.Army.Members, asset.Defenders, asset.HexDefenseBonus);
                    float winChance = WorthIt.WinChance(
                        (IReadOnlyCollection<WorthIt.DefenderProfile>)c.Army.Members,
                        (IReadOnlyCollection<WorthIt.DefenderProfile>)asset.Defenders,
                        asset.HexDefenseBonus);

                    int? enemyEta = null;
                    if (c.Position.HasValue)
                        enemyEta = CeilDiv(HexGridMath.Distance(c.Position.Value, asset.Hex),
                            Mathf.Max(AiConfigV2.etaFallbackMoveBudget, c.Army.MaxMovement));

                    int? responseEta = null;
                    foreach (ArmySnapshot r in ownFieldForResponse)
                    {
                        int e = CeilDiv(HexGridMath.Distance(r.Hex, asset.Hex),
                            Mathf.Max(AiConfigV2.etaFallbackMoveBudget, r.MaxMovement));
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
                        ResponseEta = responseEta,
                        AttackWinChance = winChance,
                        PotentialDamage = potentialDamage,
                        Confidence = c.Confidence,
                        Severity = severity,
                    });
                }
            }
            model.Threats = threats;

            bool derivedSiege = threats.Any(t =>
                (t.Asset.Kind == AssetKind.Citadel || t.Asset.Kind == AssetKind.Base)
                && t.AttackWinChance >= AiConfigV2.siegeEnemyWinChanceThreshold
                && ((t.Contact.Position.HasValue
                        && HexGridMath.Distance(t.Contact.Position.Value, t.Asset.Hex) <= AiConfigV2.siegeRadius)
                    || (t.EnemyEta.HasValue && t.EnemyEta.Value <= AiConfigV2.siegeEnemyEtaTurns)));
            model.UnderSiege = derivedSiege;

            return model;
        }

        private static EnemyContactSnapshot MakeCheatContact(ArmySnapshot source, HexCoord regionCenter, int regionRadius)
        {
            return new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    ArmyId = -1,
                    Owner = source.Owner,
                    Hex = default,
                    MemberCount = source.MemberCount,
                    HasHero = source.HasHero,
                    HasAntiAir = source.HasAntiAir,
                    IsHiddenFromUs = source.IsHiddenFromUs,
                    AttackSum = source.AttackSum,
                    DefenseSum = source.DefenseSum,
                    EffectiveArmyPower = source.EffectiveArmyPower,
                    MaxMovement = 0,
                    Members = source.Members,
                },
                Knowledge = ContactKnowledge.Region,
                Source = ContactSource.Cheat,
                Position = null,
                RegionCenter = regionCenter,
                RegionRadius = regionRadius,
                Confidence = AiConfigV2.threatConfidenceCheatRegion,
            };
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
                AttackSum = o.AttackSum,
                DefenseSum = o.DefenseSum,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
                MaxMovement = 1,
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
                ArmyId = -1,
                Owner = s.Owner,
                Hex = s.Hex,
                MemberCount = s.MemberCount,
                HasAntiAir = s.HasAntiAir,
                AttackSum = s.AttackSum,
                DefenseSum = s.DefenseSum,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
                MaxMovement = 1,
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
                    if (b.HasFacilityAbility(UnitAbilities.Barracks))
                        v += AiConfigV2.assetValueFacilityBarracksBonus;
                    if (b.HasFacilityAbility(UnitAbilities.Research) || b.HasFacilityAbility(UnitAbilities.Production))
                        v += AiConfigV2.assetValueFacilityDevBonus * (snap.Self.HasDevOperator || snap.Self.HasDevFacility ? 1f : 0.3f);
                    for (int i = 0; i < ResourceBundle.All.Length; i++)
                    {
                        ResourceType t = ResourceBundle.All[i];
                        if (b.HasFacilityAbility(UnitAbilities.CollectAbilityFor(t)))
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
            float etaUrgency = enemyEta.HasValue
                ? 1f / (1f + enemyEta.Value)
                : 1f / (1f + AiConfigV2.etaUnknownContactPenalty);

            float responseHeadstart = (enemyEta.HasValue && responseEta.HasValue)
                ? Mathf.Clamp01((responseEta.Value - enemyEta.Value) / 4f)
                : 0f;

            float posWeight = AiConfigV2.severityWinChanceWeight + AiConfigV2.severityDamageWeight
                + AiConfigV2.severityEtaWeight + AiConfigV2.severityCanDamageWeight;
            float raw = AiConfigV2.severityWinChanceWeight * winChance
                + AiConfigV2.severityDamageWeight * potentialDamage
                + AiConfigV2.severityEtaWeight * etaUrgency
                + AiConfigV2.severityCanDamageWeight * (canDamage ? 1f : 0f)
                - AiConfigV2.severityResponseHeadstartWeight * responseHeadstart;

            return confidence * Mathf.Clamp01(raw / Mathf.Max(0.0001f, posWeight));
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
