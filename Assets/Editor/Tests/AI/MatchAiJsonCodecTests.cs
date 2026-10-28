#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Combat;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Units;
using Game.Players;
using Game.Saves;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class MatchAiJsonCodecTests
    {
        [Test]
        public void AllSixObjectivesSurviveProductionJsonWithFreshObjectsAndExactRaidClock()
        {
            AllSixObjectivesRoundtrip(value => MatchSaveJson.Deserialize<MatchAiObjectiveData>(MatchSaveJson.Serialize(value)), value => MatchSaveJson.Serialize(value));
        }

        internal void AllSixObjectivesRoundtrip(Func<MatchAiObjectiveData, MatchAiObjectiveData> roundtrip, Func<MatchAiObjectiveData, string> json)
        {
            var owner = new PlayerSetupData { PlayerId = "target", ColorIndex = 3 };
            var codec = new MatchAiCodec.Objectives(p => p.PlayerId, null, null);
            var attack = new AttackIntent {
                Target = AttackTargetRef.For(new HexCoord(3, -2), owner, AttackTargetKind.Citadel),
                TargetRoster = new System.Collections.Generic.List<StrikeRosterSlot> {
                    new StrikeRosterSlot("authored-card", false, 19.5f, ForceSource.Map) },
                GatherReturns = new System.Collections.Generic.List<AttackGatherReturn> {
                    new AttackGatherReturn { ArmyId = 17, BaseHex = new HexCoord(-1, 2) } },
                PreparationCardWaitSinceTurn = 6, TargetRosterPeak = 22, TargetRosterCapacity = 4
            };
            var raid = new RaidIntent { Target = RaidTargetRef.ForNeutralArmy(0), SupportArmyId = 7,
                Phase = RaidMissionPhase.Reinforcement, ReinforcementRequestedTurn = 11,
                ReturnHex = new HexCoord(1, 0), RecoveryBaseHex = new HexCoord(2, 0) };
            var snapshot = new ArmySnapshot {
                ArmyId = 21, Owner = owner, Hex = new HexCoord(2, 1),
                StrategicCoverage = RoleCoverage.None.With(IntendedRole.CombatBody),
                AllyAuraEffects = StrategicEffectRegistry.Resolve(new[] { UnitAbilities.Drill }, 0),
                Members = new[] { new WorthIt.DefenderProfile(7, false, attack: 4.5f, hitPoints: 6, maxHitPoints: 10) },
                NonHeroRuntimeIds = new[] { 11 }, NonHeroMovementSpent = new[] { 3 }
            };
            var sortie = new GarrisonSortiePlan(true, 10, 11, 12, 3, null, new HexCoord(0, 1), new HexCoord(2, 1), new[] { 11 }, snapshot, 0.85f, true);
            var pairs = new [] {
                (MissionKind.Scout, codec.CaptureScout(new ScoutIntent { FocusHex = new HexCoord(1, 2), RequiresStealth = true })),
                (MissionKind.Raid, codec.CaptureRaid(raid)),
                (MissionKind.Economy, codec.CaptureEconomy(new EconomyIntent { IntrinsicValue = null, ArrivalTurn = 8, BuildValue = 4.5f })),
                (MissionKind.Development, codec.CaptureDevelopment(new DevelopmentIntent { FacilityHex = new HexCoord(0, 2), IntrinsicValue = 8.25f })),
                (MissionKind.ActiveDefence, codec.CaptureActiveDefence(new ActiveDefenceIntent { Confidence = 0.5f, EnemyArmyId = 9, LastObservedTurn = 3, Sortie = sortie, EnemyEta = 2 })),
                (MissionKind.Attack, codec.CaptureAttack(attack))
            };
            var reboundOwner = new PlayerSetupData { PlayerId = "target", ColorIndex = 3 };
            var restoredCodec = new MatchAiCodec.Objectives(id => id == "target" ? reboundOwner : null, id => null, id => null, id => null);
            foreach (var pair in pairs)
            {
                string expectedJson = json(pair.Item2);
                var restored = restoredCodec.Restore(pair.Item1, roundtrip(pair.Item2));
                Assert.AreEqual(pair.Item1, restored.Kind);
                Assert.AreEqual(expectedJson, json(Capture(restoredCodec, restored)));
                if (restored is RaidIntent r) Assert.AreEqual(11, r.ReinforcementRequestedTurn);
                if (restored is ActiveDefenceIntent defence) {
                    Assert.AreSame(reboundOwner, defence.Sortie.Detachment.Owner);
                    Assert.AreNotSame(snapshot, defence.Sortie.Detachment);
                    Assert.AreEqual(4.5f, defence.Sortie.Detachment.Members[0].Attack);
                    Assert.IsTrue(defence.Sortie.Detachment.StrategicCoverage.Has(IntendedRole.CombatBody));
                    Assert.IsTrue(defence.Sortie.Detachment.AllyAuraEffects[0].EligiblePredicate(defence.Sortie.Detachment.Members[0]));
                }
                if (restored is AttackIntent a) {
                    Assert.AreSame(reboundOwner, a.Target.ExpectedOwner);
                    Assert.AreNotSame(attack.TargetRoster, a.TargetRoster);
                    Assert.AreEqual(17, a.GatherReturns[0].ArmyId);
                    Assert.AreEqual(new HexCoord(-1, 2), a.GatherReturns[0].BaseHex);
                }
            }
        }

        [Test]
        public void DefenceSnapshotDetachesInternalFieldsAndKeepsEquipmentAndSortieBehavior()
        {
            var owner = new PlayerSetupData { PlayerId = "ai" };
            var hex = new HexCoord(2, 3);
            var movement = new System.Collections.Generic.List<int> { 3 };
            var routes = new System.Collections.Generic.Dictionary<HexCoord, int> { [hex] = 7 };
            var effects = StrategicEffectRegistry.Resolve(new[] { UnitAbilities.Heal }, 0);
            var before = new PredictedEquipmentState(new System.Collections.Generic.Dictionary<EquipmentStat, int> {
                [EquipmentStat.HitPoints] = 10, [EquipmentStat.Fate] = 4 }, new[] { UnitAbilities.Heal });
            var after = new PredictedEquipmentState(new System.Collections.Generic.Dictionary<EquipmentStat, int> {
                [EquipmentStat.HitPoints] = 8, [EquipmentStat.Fate] = 3 }, Array.Empty<string>());
            var consumption = new EquipmentConsumption(before, after, 2, 1);
            var snapshot = new ArmySnapshot {
                Owner = owner, ArmyId = -1, NonHeroMovementSpent = movement, AllyAuraEffects = effects,
                ActivationApCost = 5, Capacity = 4, OccupiedBattleSlots = 2,
                EconomyDeparture = new ArmySnapshot { Owner = owner, ArmyId = -1, EconomyHomeRouteCosts = routes },
                Members = new[] { new WorthIt.DefenderProfile(4, false, typeTags: new[] { UnitTypeTag.Bio }, attack: 6, hitPoints: 9,
                    maxHitPoints: 10, consumption: consumption) }
            };
            var sortie = new GarrisonSortiePlan(true, 1, 2, 3, 4, null, hex, hex, new[] { 2 }, snapshot, 0.9f, true);
            var capture = new MatchAiCodec.Objectives(p => p.PlayerId, null, null);
            var data = capture.CaptureActiveDefence(new ActiveDefenceIntent { Sortie = sortie });
            movement[0] = 99; routes[hex] = 99; effects.Clear(); snapshot.ActivationApCost = 99;
            var rebound = new PlayerSetupData { PlayerId = "ai" };
            var restored = (ActiveDefenceIntent)new MatchAiCodec.Objectives(id => rebound,
                id => null, id => null, id => null).Restore(MissionKind.ActiveDefence,
                    MatchSaveJson.Deserialize<MatchAiObjectiveData>(MatchSaveJson.Serialize(data)));
            var saved = restored.Sortie.Detachment;
            Assert.AreSame(rebound, saved.Owner); Assert.AreSame(rebound, saved.EconomyDeparture.Owner);
            Assert.AreEqual(3, saved.NonHeroMovementSpent[0]);
            Assert.AreEqual(7, saved.EconomyDeparture.EconomyHomeRouteCosts[hex]);
            Assert.AreEqual(2, saved.FreeBattleSlots);
            Assert.AreEqual(ArmyActions.CreateArmyApCost + 5, restored.Sortie.TotalAp);
            Assert.IsTrue(saved.AllyAuraEffects[0].EligiblePredicate(saved.Members[0]));
            var retainedConsumption = saved.Members[0].Consumption;
            Assert.AreEqual(5f, retainedConsumption.RemainingHitPoints(9, 10, out float maximum));
            Assert.AreEqual(8f, maximum);
            var applied = retainedConsumption.Apply(before);
            Assert.AreEqual(2, applied.Stats[EquipmentStat.Fate]);
            Assert.IsEmpty(applied.Abilities);
        }

        [Test]
        public void ObjectiveDecoderRejectsUnknownTagAndNonfiniteNestedFact()
        {
            var codec = new MatchAiCodec.Objectives(p => p.PlayerId, null, null);
            var data = codec.CaptureAttack(new AttackIntent());
            data.Tag = 123456;
            Assert.Throws<ArgumentException>(() => codec.Restore(MissionKind.Attack, data));
            data = codec.CaptureEconomy(new EconomyIntent());
            data.Economy.BuildApCost = float.NaN;
            Assert.Throws<ArgumentException>(() => codec.Restore(MissionKind.Economy, data));
        }

        [Test]
        public void LateMalformedOwnerFailsBeforeStaticStateOrIdentitySequenceChanges()
        {
            var owners = new AiPersistentGraphRoundTripTests(); owners.ClearOwners();
            try {
                var owner = new PlayerSetupData { PlayerId = "ai" };
                ForceBaselineRegistry.RecordStart(owner, 42f);
                LifecycleReturnPolicy.RecordWait(owner, MissionIntentKey.ForScout(ScoutTargetKind.Explore, new HexCoord(1, 2)), 7);
                var data = MatchAiCodec.Capture(p => p.PlayerId, null, null, null);
                data.LifecycleWaits.Entries[0].Value.Entries[0].Key.Kind = 123456;
                int high = ArmyData.IdentityHighWaterMark;
                // No authored key exists in this test, so validation only needs a nonnull config shell.
                var config = (GameConfig)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameConfig));
                Assert.Throws<ArgumentException>(() => MatchAiCodec.Validate(data, id => id == "ai" ? owner : null, id => null, id => null, id => null, config));
                Assert.AreEqual(42f, ForceBaselineRegistry.CapturePersistentState(p => p.PlayerId)["ai"]);
                Assert.AreEqual(high, ArmyData.IdentityHighWaterMark);
            }
            finally { owners.ClearOwners(); }
        }

        [Test]
        public void AbsentReconSortieTrailIsRejectedBeforeOwnersOrIdentitiesChange()
        {
            var owners = new AiPersistentGraphRoundTripTests(); owners.ClearOwners();
            try {
                var owner = new PlayerSetupData { PlayerId = "ai" };
                var sortie = ReconAirSortieRegistry.GetOrCreate(owner, 101, new HexCoord(1, 2));
                ForceBaselineRegistry.RecordStart(owner, 42f);
                var data = MatchAiCodec.Capture(p => p.PlayerId, null, null, null);
                var trail = data.ReconSorties.Players.Entries[0].Value.Entries[0].Value.Trail;
                trail.HasData = false; trail.Values.Clear();
                data = MatchSaveJson.Deserialize<MatchAiData>(MatchSaveJson.Serialize(data));
                var config = (GameConfig)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameConfig));
                int armyHigh = ArmyData.IdentityHighWaterMark, unitHigh = UnitData.IdentityHighWaterMark,
                    cardHigh = CardData.IdentityHighWaterMark;
                Assert.Throws<ArgumentException>(() => MatchAiCodec.Validate(data, id => id == "ai" ? owner : null,
                    id => null, id => null, id => null, config));
                Assert.IsTrue(ReconAirSortieRegistry.TryGet(owner, 101, out var retained));
                Assert.AreSame(sortie, retained); Assert.AreEqual(1, retained.Trail.Count);
                Assert.AreEqual(42f, ForceBaselineRegistry.CapturePersistentState(p => p.PlayerId)["ai"]);
                Assert.AreEqual(armyHigh, ArmyData.IdentityHighWaterMark);
                Assert.AreEqual(unitHigh, UnitData.IdentityHighWaterMark); Assert.AreEqual(cardHigh, CardData.IdentityHighWaterMark);
            }
            finally { owners.ClearOwners(); }
        }

        [Test]
        public void ValidAndLateInvalidValidationPreserveIssuedIdentityHighwater()
        {
            var owners = new AiPersistentGraphRoundTripTests(); owners.ClearOwners();
            try {
                var owner = new PlayerSetupData { PlayerId = "ai", ColorIndex = 2 };
                int high = ArmyData.IdentityHighWaterMark;
                int unitHigh = UnitData.IdentityHighWaterMark;
                int cardHigh = CardData.IdentityHighWaterMark;
                MissionIntentRegistry.GetOrCreate(owner).PutRetreatWitness(new AttackRetreatWitness {
                    Target = AttackTargetRef.For(new HexCoord(1, 2), owner, AttackTargetKind.Base),
                    OwnArmyId = high + 20, EnemyArmyId = high + 30
                });
                var data = MatchAiCodec.Capture(p => p.PlayerId, null, null, null);
                var config = (GameConfig)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameConfig));
                MatchAiCodec.Validate(data, id => id == "ai" ? owner : null, id => null, id => null, id => null, config);
                Assert.AreEqual(high, ArmyData.IdentityHighWaterMark, "Valid detached preflight must not reserve historical mission identities.");
                Assert.AreEqual(unitHigh, UnitData.IdentityHighWaterMark); Assert.AreEqual(cardHigh, CardData.IdentityHighWaterMark);
                data.HistoricalArmyIds.Clear();
                Assert.Throws<ArgumentException>(() => MatchAiCodec.Validate(data, id => id == "ai" ? owner : null, id => null, id => null, id => null, config));
                Assert.AreEqual(high, ArmyData.IdentityHighWaterMark, "A late rejection must not reserve historical mission identities.");
                Assert.AreEqual(unitHigh, UnitData.IdentityHighWaterMark); Assert.AreEqual(cardHigh, CardData.IdentityHighWaterMark);
            }
            finally { owners.ClearOwners(); }
        }

        private static MatchAiObjectiveData Capture(MatchAiCodec.Objectives codec, IMissionObjective value)
        {
            switch(value) {
                case ScoutIntent x: return codec.CaptureScout(x);
                case RaidIntent x: return codec.CaptureRaid(x);
                case EconomyIntent x: return codec.CaptureEconomy(x);
                case DevelopmentIntent x: return codec.CaptureDevelopment(x);
                case ActiveDefenceIntent x: return codec.CaptureActiveDefence(x);
                case AttackIntent x: return codec.CaptureAttack(x);
                default: throw new Exception();
            }
        }
    }
}
#endif
