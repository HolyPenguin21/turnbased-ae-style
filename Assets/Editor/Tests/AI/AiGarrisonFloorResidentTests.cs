#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-13 (user decision, F1 variant A) — match Korrin vs Brynn: Brynn's Base (1,-6) asked
    // 87.7 power from hand while Fester (52.4) stood on the hex; Korrin's 4-slot Base asked 94.9.
    // The floor stays the hex's need; own field armies standing there (not leaving on an
    // operation) answer it first, and the hand is asked only for what the slots can hold.
    public class AiGarrisonFloorResidentTests
    {
        private static readonly PlayerSetupData Us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly HexCoord BaseHex = new HexCoord(1, -6);

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
        }

        private static UnitData Body(string name, int attack) => new UnitData
        {
            Name = name, Owner = Us, Attack = attack, Defense = 2,
            HitPointsMax = 6, HitPointsCurrent = 6, Initiative = 2, MoveMax = 3, MoveCurrent = 3,
        };

        private static ArmyData Army(bool garrison, params UnitData[] members)
        {
            var army = new ArmyData { Owner = Us, Hex = BaseHex, IsGarrison = garrison };
            army.Members.AddRange(members);
            ArmyRegistry.Register(army);
            return army;
        }

        private static float ShareFloor(float available) => AiArmyRoles.GarrisonDefenceFloor(available, false);

        [Test]
        public void FieldArmyOnTheHexAnswersTheFloor_AndGarrisonBodiesBecomeSpare()
        {
            ArmyData garrison = Army(true, Body("G1", 3), Body("G2", 3), Body("G3", 3), Body("G4", 3));
            const float available = 2000f;   // share floor 100: four small bodies cannot hold it
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(Us, available, BaseHex, false),
                Is.EqualTo(ShareFloor(available)).Within(1e-3f), "no resident: the whole floor is the garrison's");

            ArmyData fester = Army(false, Enumerable.Range(0, 8).Select(i => Body("F" + i, 40)).ToArray());
            float resident = AiArmyRoles.ResidentFieldPower(Us, BaseHex);
            Assert.That(resident, Is.GreaterThan(ShareFloor(available)));
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(Us, available, BaseHex, false), Is.Zero,
                "the field army on the hex covers the floor: nothing is left for the garrison to hold");

            // The old spare rule on the credited floor: one body stays, the others may go.
            HashSet<int> spare = AiArmyRoles.SpareableBodies(garrison.Members.ToList(),
                set => AiPower.EffectiveArmyPower(set.ToList()),
                AiArmyRoles.GarrisonDefenceFloor(Us, available, BaseHex, false));
            Assert.That(spare.Count, Is.EqualTo(3));
            Assert.That(fester.Members.Count, Is.EqualTo(8));
        }

        [Test]
        public void FieldArmyLeavingOnAnOperation_DoesNotHoldTheHex()
        {
            Army(true, Body("G1", 3), Body("G2", 3));
            ArmyData fist = Army(false, Body("F1", 40), Body("F2", 40));
            var target = AttackTargetRef.For(new HexCoord(5, -2), new PlayerSetupData(), AttackTargetKind.Base);
            MissionIntentRegistry.GetOrCreate(Us).Put(new MissionIntent
            {
                Kind = MissionKind.Attack, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.ForAttack(target), PreferredMoverArmyId = fist.Id,
                Objective = new AttackIntent { Target = target, Phase = AttackMissionPhase.Gather, PrimaryArmyId = fist.Id },
            });
            Assert.That(AiArmyRoles.ResidentFieldPower(Us, BaseHex), Is.Zero);
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(Us, 2000f, BaseHex, false),
                Is.EqualTo(ShareFloor(2000f)).Within(1e-3f));
        }

        private static WorthIt.DefenderProfile Profile(float attack) =>
            new WorthIt.DefenderProfile(2f, false, attack: attack, hitPoints: 6f, initiative: 2);

        private static StrikeRosterCandidate Card(string key, float power) =>
            new StrikeRosterCandidate(new StrikeRosterSlot(key, false, power, ForceSource.Hand),
                new AiPower.PowerUnit(power, null, 1, false));

        // Korrin's Base (-1,-3): 4/4 and a 94.9 shortfall. A full garrison takes from hand only the
        // swap of its weakest body for a stronger card; with no stronger card nothing fits.
        [Test]
        public void HandDemandIsCappedByWhatTheSlotsCanHold()
        {
            var garrison = new ArmySnapshot
            {
                IsGarrison = true, Hex = BaseHex, Capacity = 4, MemberCount = 4, OccupiedBattleSlots = 4,
                Members = new List<WorthIt.DefenderProfile> { Profile(3), Profile(3), Profile(3), Profile(3) },
            };
            float weakest = AiPower.ToPowerUnit(garrison.Members[0]).BasePower;
            var snap = new WorldSnapshot { Observer = Us, Self = new SelfSnapshot
                { StrikePool = new List<StrikeRosterCandidate> { Card("weak", weakest * 0.5f) } } };
            Assert.That(AggressionDemandEvaluator.GarrisonHandGainCap(snap, garrison), Is.Zero,
                "full garrison, no stronger card: the hand cannot add anything");

            snap.Self.StrikePool = new List<StrikeRosterCandidate> { Card("tank", weakest * 3f) };
            float swap = AggressionDemandEvaluator.GarrisonHandGainCap(snap, garrison);
            Assert.That(swap, Is.GreaterThan(0f), "a stronger card may still replace the weakest body");
            Assert.That(swap, Is.LessThan(weakest * 3f));

            snap.Self.StrikePool = new List<StrikeRosterCandidate> { Card("weak", weakest * 0.5f) };
            garrison.MemberCount = 2; garrison.OccupiedBattleSlots = 2;
            garrison.Members = garrison.Members.Take(2).ToList();
            // Two relief bodies already walking there take the two free slots (RecipientFreeSlots).
            var walking = new ArmySnapshot { ArmyId = 31, Owner = Us, Hex = new HexCoord(2, -6), MemberCount = 2,
                Members = new List<WorthIt.DefenderProfile> { Profile(3), Profile(3) } };
            snap.Self.Armies = new List<ArmySnapshot> { garrison, walking };
            var state = new MissionIntentState();
            var relief = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active,
                Objective = new ActiveDefenceIntent { Phase = ActiveDefencePhase.Return,
                    ReturnPurpose = ActiveDefenceReturnPurpose.GarrisonRelief, EnemyArmyId = -1,
                    PrimaryArmyId = 31, ReturnHex = BaseHex, ProtectedAssetHex = BaseHex,
                    ProtectedAssetKind = AssetKind.Base },
            };
            relief.IntentKey = MissionIntentKey.For(relief);
            state.Put(relief);
            snap.Intents = state;
            Assert.That(AggressionDemandEvaluator.GarrisonHandGainCap(snap, garrison), Is.Zero,
                "two relief bodies already walking there take the two free slots");
        }
    }
}
#endif
