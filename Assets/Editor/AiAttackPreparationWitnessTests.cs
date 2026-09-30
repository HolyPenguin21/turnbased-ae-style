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
    }
}
#endif
