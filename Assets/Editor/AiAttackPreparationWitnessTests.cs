#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ATK-F02 — a preparation WAIT names a concrete card-borne source that would strengthen the
    // exact host (AggressionDemandEvaluator.PreparationHostCardSource); a positive Reserve is no
    // delivery (Cassia T19/T20: hand 0, deck 0, WAIT on a phantom reserve).
    public class AiAttackPreparationWitnessTests
    {
        private static readonly PlayerSetupData Us =
            new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly HexCoord Citadel = new HexCoord(-5, 3);

        private static CardDefinition Unit(int attack) => new CardDefinition
        {
            cardType = CardType.Unit, displayName = $"Unit{attack}", attack = attack, defenseRating = 2,
            hitPoints = 6, initiative = 2, unitTypeTags = new List<UnitTypeTag> { UnitTypeTag.Infantry },
        };

        private static UnitData Body(int attack) => new UnitData
        {
            Name = $"Body{attack}", Owner = Us, Attack = attack, Defense = 2,
            HitPointsMax = 6, HitPointsCurrent = 6, Initiative = 2,
        };

        private static ArmyData Host(HexCoord hex, params UnitData[] members)
        {
            var host = new ArmyData { Owner = Us, Hex = hex };
            host.Members.AddRange(members);
            return host;
        }

        private static WorldSnapshot Snap(IEnumerable<CardData> hand, IEnumerable<CardDefinition> deck,
            float unitsReserve = 0f) => new WorldSnapshot
        {
            Observer = Us,
            TurnNumber = 20,
            Self = new SelfSnapshot
            {
                BaseHexes = new List<HexCoord> { Citadel },
                Citadel = Citadel,
                Hand = new List<CardData>(hand ?? new CardData[0]),
                Deck = new List<CardDefinition>(deck ?? new CardDefinition[0]),
                Reserve = new ForceReserve(unitsReserve, 0f, 0f, 0f),
            },
            Development = new DevelopmentReadiness(),
        };

        [Test]
        public void EmptyHandAndDeck_NoWitness_EvenWithAPositiveReserve()
        {
            WorldSnapshot snap = Snap(null, null, unitsReserve: 7.9f);
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, Host(Citadel, Body(4))),
                Is.Null);
        }

        [Test]
        public void UndrawnStrengtheningUnit_IsTheWitness()
        {
            WorldSnapshot snap = Snap(null, new[] { Unit(6) });
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, Host(Citadel, Body(4))),
                Does.StartWith("undrawn_card:"));
        }

        // 2026-10-01 — with a target roster only a missing position (or an equivalent at least
        // as strong as the weakest missing body) is waited for; a weak non-roster body is not.
        [Test]
        public void TargetRoster_WaitsOnlyForAMissingPosition()
        {
            CardDefinition tank = Unit(9);
            float tankPower = AiPower.ToPowerUnit(tank).BasePower;
            var target = new List<StrikeRosterSlot>
                { new StrikeRosterSlot(StrikeRoster.CardKey(tank), false, tankPower, ForceSource.Deck) };
            ArmyData host = Host(Citadel, Body(4));

            WorldSnapshot weakOnly = Snap(new[] { new CardData(Unit(5)) }, null);
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(weakOnly, host, target),
                Is.Null, "a weaker non-roster card is no witness");

            WorldSnapshot exact = Snap(new[] { new CardData(Unit(5)), new CardData(Unit(9)) }, null);
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(exact, host, target),
                Does.StartWith("hand_card:Unit9"));

            WorldSnapshot stronger = Snap(new[] { new CardData(Unit(12)) }, null);
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(stronger, host, target),
                Does.StartWith("hand_card:Unit12"), "an equivalent at least as strong fills it");
        }

        [Test]
        public void HeldUnit_IsTheWitness()
        {
            WorldSnapshot snap = Snap(new[] { new CardData(Unit(6)) }, null);
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, Host(Citadel, Body(4))),
                Does.StartWith("hand_card:"));
        }

        // Vex T14-T22 ("Hooded", r1s4): a native Recce unit is a combat body for the Attack
        // preparation host (user decision 30.09) — a witness here — while every other
        // FieldCombatPower demand still keeps scouts for Recon.
        [Test]
        public void RecceUnit_FightsForThePreparationHostOnly()
        {
            CardDefinition scout = Unit(6);
            scout.displayName = "Hooded";
            scout.grantedAbilities = new List<string> { "r1s4", "RapidReaction", "Stealth4" };
            WorldSnapshot snap = Snap(new[] { new CardData(scout) }, null);
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, Host(Citadel, Body(4))),
                Does.StartWith("hand_card:Hooded"));
            Assert.That(MaterializationChainMatching.AbilitiesSatisfyCapability(scout.grantedAbilities,
                CardType.Unit, CapabilityKind.FieldCombatPower), Is.False);
            Assert.That(MaterializationChainMatching.AbilitiesSatisfyCapability(scout.grantedAbilities,
                CardType.Unit, CapabilityKind.FieldCombatPower, recceMayFight: true), Is.True);
        }

        [Test]
        public void FullHost_TakesNoCard()
        {
            // No hero: two slots, both taken.
            WorldSnapshot snap = Snap(new[] { new CardData(Unit(9)) }, new[] { Unit(9) });
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap,
                Host(Citadel, Body(4), Body(4))), Is.Null);
        }

        [Test]
        public void HostOffAnOwnBase_TakesNoCard()
        {
            WorldSnapshot snap = Snap(null, new[] { Unit(6) });
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap,
                Host(new HexCoord(0, 0), Body(4))), Is.Null);
        }

        [Test]
        public void StaffedOutput_IsAGenerationWitness_EvenWhenStockIsShort()
        {
            WorldSnapshot snap = Snap(null, null);
            snap.Development.StaffedOutputs = new[] { Unit(6) };
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, Host(Citadel, Body(4))),
                Does.StartWith("generation:").And.Contain("stock_short"));
        }

        [Test]
        public void StaffedOutput_OutOfReachWithinTheFundingHorizon_IsNoWitness()
        {
            WorldSnapshot snap = Snap(null, null);
            CardDefinition costly = Unit(6);
            costly.resourceCost = new ResourceCost { tech = 3 };
            snap.Development.StaffedOutputs = new[] { costly };
            // No Tech in stock and no Tech income: the Challenge never becomes affordable.
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, Host(Citadel, Body(4))),
                Is.Null);
            snap.Self.PerTurnIncome = new ResourceBundle { Tech = 1f };
            Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, Host(Citadel, Body(4))),
                Does.StartWith("generation:"));
        }

        // User decision 30.09: the fist assembles on the own Base nearest to the target, the
        // starting Citadel only breaks a tie.
        [Test]
        public void StagingBase_IsTheOwnBaseNearestTheTarget()
        {
            WorldSnapshot snap = Snap(null, null);
            var forward = new HexCoord(0, 0);
            snap.Self.BaseHexes = new List<HexCoord> { Citadel, forward };
            Assert.That(AttackObjectiveEvaluator.PreparationStagingBase(snap, new HexCoord(3, -1)),
                Is.EqualTo(forward));
            Assert.That(AttackObjectiveEvaluator.PreparationStagingBase(snap, new HexCoord(-6, 4)),
                Is.EqualTo(Citadel));
            snap.Self.BaseHexes = new List<HexCoord>();
            Assert.That(AttackObjectiveEvaluator.PreparationStagingBase(snap, new HexCoord(3, -1)),
                Is.Null);
        }

        // A structural host only stages on an own Base it can reach (ReachableOwnBaseHexes);
        // no reachable Base -> no staging point (the preparation stalls instead of waiting forever).
        [Test]
        public void StagingBase_ForAHost_IsOnlyAReachableBase()
        {
            WorldSnapshot snap = Snap(null, null);
            var forward = new HexCoord(0, 0);
            snap.Self.BaseHexes = new List<HexCoord> { Citadel, forward };
            var host = new ArmySnapshot { ArmyId = 5, Hex = new HexCoord(-3, 2), IsStructuralRaidActor = true,
                ReachableOwnBaseHexes = new[] { Citadel } };
            Assert.That(AttackObjectiveEvaluator.PreparationStagingBase(snap, new HexCoord(3, -1), host),
                Is.EqualTo(Citadel));
            host.ReachableOwnBaseHexes = new HexCoord[0];
            Assert.That(AttackObjectiveEvaluator.PreparationStagingBase(snap, new HexCoord(3, -1), host),
                Is.Null);
        }
    }
}
#endif
