#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  ATK stage 1 — the GROUND-COMBAT KERNEL / BASE-IDENTITY contracts every ground lane shares
    //  (Raid, ActiveDefence and, from ATK, Attack). Nothing here is Attack-specific: these are the
    //  shared owners Attack will be built on, pinned BEFORE the new lane exists so a later stage
    //  cannot quietly regrow a second estimator, a second assignment solver, a second own-Base
    //  store or a second equipment-witness proof.
    // ===========================================================================================
    public class AiGroundCombatKernelContractTests
    {
        private static WorthIt.DefenderProfile Body(float atk, float def, float hp, int init) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, init, null, hp);

        // A matchup calibrated (Tools probe, 2026-09-23) so that a large defender hex bonus moves
        // the WIN CHANCE while COVERAGE stays intact — the bonus must be able to fail an admission
        // on its own, not only by making defenders undamageable.
        private static List<WorthIt.DefenderProfile> Attackers() => new List<WorthIt.DefenderProfile>
        {
            Body(12, 4, 14, 5), Body(11, 4, 13, 4),
        };

        private static List<WorthIt.DefenderProfile> Defenders() => new List<WorthIt.DefenderProfile>
        {
            Body(6, 3, 12, 4), Body(6, 3, 12, 3),
        };

        // ---- §29 the estimator is no longer blind to the defender's hex -----------------------

        [Test]
        public void DefenderHexBonus_LowersWinChance_WithCoverageIntact()
        {
            List<WorthIt.DefenderProfile> attackers = Attackers();
            List<WorthIt.DefenderProfile> defenders = Defenders();

            bool flatClears = GroundCombatFeasibility.Clears(attackers, defenders,
                AiConfigV2.raidMinViableWinChance, 0f, out float flatWin, out bool flatCover);
            bool fortifiedClears = GroundCombatFeasibility.Clears(attackers, defenders,
                AiConfigV2.raidMinViableWinChance, 8f, out float fortifiedWin, out bool fortifiedCover);

            Assert.That(flatCover, Is.True, "same rosters on open ground stay damageable");
            Assert.That(fortifiedCover, Is.True,
                "this matchup is calibrated so coverage survives the bonus");
            Assert.That(fortifiedWin, Is.LessThan(flatWin),
                "a defended hex must make the SAME assault less likely to win");
            Assert.That(flatClears, Is.True);
            Assert.That(fortifiedClears, Is.False,
                "admission must fail on the win term alone once the hex defence is priced in");
        }

        [Test]
        public void DefenderHexBonus_ZeroIsBitForBitTheOldBehaviour()
        {
            List<WorthIt.DefenderProfile> attackers = Attackers();
            List<WorthIt.DefenderProfile> defenders = Defenders();

            GroundCombatFeasibility.Clears(attackers, defenders, out float legacyWin, out bool legacyCover);
            GroundCombatFeasibility.Clears(attackers, defenders,
                AiConfigV2.raidMinViableWinChance, 0f, out float explicitWin, out bool explicitCover);

            Assert.That(explicitWin, Is.EqualTo(legacyWin),
                "existing callers that pass no bonus must not shift by a single Monte-Carlo trial");
            Assert.That(explicitCover, Is.EqualTo(legacyCover));
        }

        // ---- §29 the bonus reaches the kernel through the REQUEST, not a local estimator -------

        [Test]
        public void AssemblyPlanner_CarriesDefenderHexBonusIntoTheProjectedPlan()
        {
            WorldSnapshot snap = ActorSnapshot(7, Attackers());
            List<WorthIt.DefenderProfile> defenders = Defenders();

            GroundCombatAssemblyPlan flat = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                snap, defenders, 7, AiConfigV2.raidMinViableWinChance, 0f);
            GroundCombatAssemblyPlan fortified = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                snap, defenders, 7, AiConfigV2.raidMinViableWinChance, 8f);

            Assert.That(flat.Feasible, Is.True);
            Assert.That(fortified.Feasible, Is.False,
                "the assigned-actor gate must see the same hex defence the estimator does");
            Assert.That(flat.ProjectedWinChance, Is.GreaterThan(0f));
        }

        [Test]
        public void AssemblyRequest_DefenderHexBonusReachesFeasibility()
        {
            WorldSnapshot snap = ActorSnapshot(7, Attackers());
            List<WorthIt.DefenderProfile> defenders = Defenders();

            GroundCombatAssemblyPlan flat = GroundCombatAssemblyPlanner.Plan(snap,
                new GroundCombatAssemblyRequest
                {
                    Defenders = defenders,
                    WinChanceGate = AiConfigV2.raidMinViableWinChance,
                    AllowSameHexAssembly = false,
                    DefenderHexDefenseBonus = 0f,
                });
            GroundCombatAssemblyPlan fortified = GroundCombatAssemblyPlanner.Plan(snap,
                new GroundCombatAssemblyRequest
                {
                    Defenders = defenders,
                    WinChanceGate = AiConfigV2.raidMinViableWinChance,
                    AllowSameHexAssembly = false,
                    DefenderHexDefenseBonus = 8f,
                });

            Assert.That(flat.Feasible, Is.True);
            Assert.That(fortified.Feasible, Is.False,
                "GroundCombatAssemblyRequest.DefenderHexDefenseBonus must be an honoured constraint");
        }

        [Test]
        public void ReinforcementProjection_HonoursDefenderHexBonus()
        {
            // One weak primary body, one clearly better support body: on open ground the swap/fill
            // improves the odds, so the projection is admitted. The improvement must be evaluated
            // against the SAME fight, hex defence included.
            var primary = new List<WorthIt.DefenderProfile> { Body(2, 1, 6, 1) };
            var support = new List<WorthIt.DefenderProfile> { Body(12, 4, 14, 5) };
            List<WorthIt.DefenderProfile> defenders = Defenders();

            bool flat = GroundCombatAssemblyPlanner.TryProjectReinforcement(primary, support,
                primaryCapacity: 2, primaryMemberCount: 1, defenders,
                out List<WorthIt.DefenderProfile> flatRoster, out string flatWhy, 0f);
            bool fortified = GroundCombatAssemblyPlanner.TryProjectReinforcement(primary, support,
                primaryCapacity: 2, primaryMemberCount: 1, defenders,
                out _, out _, 40f);

            Assert.That(flat, Is.True, flatWhy);
            Assert.That(flatRoster.Count, Is.EqualTo(2), "a free slot is filled, nothing is swapped");
            Assert.That(fortified, Is.False,
                "behind an overwhelming hex defence neither roster can win, so the convoy improves "
                + "nothing — the verdict must depend on the bonus, not ignore it");
        }

        // ---- §20/§48 own-Base identity is Self.BaseHexes, never a remembered owner -------------

        [Test]
        public void SelectReturnBase_TakesIdentityFromSelfBaseHexes_NotStaleMemoryOwner()
        {
            var player = new PlayerSetupData { Nickname = "BaseIdentity" };
            var enemy = new PlayerSetupData { Nickname = "Enemy" };
            var justCaptured = new HexCoord(6, 0);
            var justLost = new HexCoord(0, 0);

            var snap = new WorldSnapshot
            {
                TurnNumber = 4,
                Self = new SelfSnapshot
                {
                    // Current truth: we hold the captured hex and no longer hold the lost one.
                    BaseHexes = new[] { justCaptured },
                    Citadel = justCaptured,
                    Armies = new[] { Mover(player, 9, new HexCoord(5, 0)) },
                },
                Known = new KnownSnapshot
                {
                    // Memory still lags reality in BOTH directions.
                    Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>
                    {
                        new Game.Ai.AiMapMemory.KnownBuilding(justCaptured, enemy,
                            isStartingCitadel: false, facilityAbilities: null, isBase: true),
                        new Game.Ai.AiMapMemory.KnownBuilding(justLost, player,
                            isStartingCitadel: true, facilityAbilities: null, isBase: true),
                    },
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };

            Assert.That(MissionContinuityLayer.SelectReturnBase(snap, player, 9),
                Is.EqualTo(justCaptured),
                "a base we hold NOW is a legal home even before its structure is re-observed");
            Assert.That(MissionContinuityLayer.ReturnBaseStillValid(snap, player, 9, justLost),
                Is.False,
                "a base that left Self.BaseHexes is lost, whatever memory still remembers");
            Assert.That(MissionContinuityLayer.ReturnBaseStillValid(snap, player, 9, justCaptured),
                Is.True);
        }

        [Test]
        public void SelectReturnBase_NoOwnBases_ReturnsNull()
        {
            var player = new PlayerSetupData { Nickname = "NoBases" };
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    BaseHexes = Array.Empty<HexCoord>(),
                    Armies = new[] { Mover(player, 3, new HexCoord(1, 1)) },
                },
                Known = new KnownSnapshot
                {
                    Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>
                    {
                        new Game.Ai.AiMapMemory.KnownBuilding(new HexCoord(0, 0), player,
                            isStartingCitadel: true, facilityAbilities: null, isBase: true),
                    },
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };

            Assert.That(MissionContinuityLayer.SelectReturnBase(snap, player, 3), Is.Null,
                "a remembered building must never resurrect a base topology we no longer have");
        }

        // ---- §66 structural intel carries an observation age --------------------------------

        [Test]
        public void KnownBuilding_CarriesObservationTurn_AndDefaultsToAgeUnknown()
        {
            var owner = new PlayerSetupData { Nickname = "Observed" };
            var stamped = new Game.Ai.AiMapMemory.KnownBuilding(new HexCoord(3, 3), owner,
                isStartingCitadel: false, facilityAbilities: null, collectedAmounts: null,
                freeFacilitySlots: 0, isBase: true, defense: 5f, seenTurn: 12);
            var unstamped = new Game.Ai.AiMapMemory.KnownBuilding(new HexCoord(3, 3), owner,
                isStartingCitadel: false, facilityAbilities: null, isBase: true);

            Assert.That(stamped.SeenTurn, Is.EqualTo(12));
            Assert.That(unstamped.SeenTurn, Is.EqualTo(0),
                "0 reads as 'age unknown / very old', matching KnownEnemySighting.SeenTurn");
            Assert.That(stamped.IsBase, Is.True, "the age field must not disturb the existing shape");
            Assert.That(stamped.Defense, Is.EqualTo(5f));
        }

        // ---- §54/§55 a Base change wakes Recon and Aggression --------------------------------

        [Test]
        public void ReconAndAggressionMasks_ConsumeInfrastructureInvalidation()
        {
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Recon)
                    .HasFlag(StrategicInvalidationReason.Infrastructure), Is.True,
                "a new/lost Base moves Recon's home anchor, nearest-base distance and coverage");
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Aggression)
                    .HasFlag(StrategicInvalidationReason.Infrastructure), Is.True,
                "Attack targets and every offensive lane's support/recovery network are Base facts");
            // Pre-existing reasons must survive the addition.
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Recon)
                    .HasFlag(StrategicInvalidationReason.ReconKnowledge), Is.True);
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Aggression)
                    .HasFlag(StrategicInvalidationReason.Contact), Is.True);
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Aggression)
                    .HasFlag(StrategicInvalidationReason.Threat), Is.True);
        }

        // ---- helpers -------------------------------------------------------------------------

        private static ArmySnapshot Mover(PlayerSetupData player, int id, HexCoord hex) =>
            new ArmySnapshot
            {
                ArmyId = id, Owner = player, Hex = hex, IsStructuralRaidActor = true,
                MemberCount = 1, MaxMovement = 3, CurrentMovement = 3,
                ReachableOwnBaseHexes = Array.Empty<HexCoord>(),
                Members = Array.Empty<WorthIt.DefenderProfile>(),
            };

        private static WorldSnapshot ActorSnapshot(int armyId,
            IReadOnlyList<WorthIt.DefenderProfile> members) => new WorldSnapshot
        {
            Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot>
                {
                    new ArmySnapshot
                    {
                        ArmyId = armyId, IsStructuralRaidActor = true, MemberCount = members.Count,
                        CurrentMovement = 3, MaxMovement = 3, Members = members,
                    },
                },
            },
        };

        private static WorldSnapshot SightingSnapshot(int armyId, HexCoord hex,
            IReadOnlyList<WorthIt.DefenderProfile> defenders) => new WorldSnapshot
        {
            Known = new KnownSnapshot
            {
                EnemySightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>
                {
                    new Game.Ai.AiMapMemory.KnownEnemySighting(hex, null, "tracked", defenders.Count,
                        0f, 0f, defenders, false, 0, 0, 1, armyId),
                },
            },
        };

        private static MissionIntent RaidIntentFor(int primaryArmyId, int targetArmyId,
            RaidMissionPhase phase) => new MissionIntent
        {
            Kind = MissionKind.Raid,
            Status = IntentStatus.Active,
            Objective = new RaidIntent
            {
                Target = RaidTargetRef.ForNeutralArmy(targetArmyId),
                LastKnownHex = new HexCoord(9, 1),
                PrimaryArmyId = primaryArmyId,
                Phase = phase,
            },
        };

        private static MissionIntent DefenceIntentFor(int primaryArmyId, int enemyArmyId,
            ActiveDefencePhase phase) => new MissionIntent
        {
            Kind = MissionKind.ActiveDefence,
            Status = IntentStatus.Active,
            Objective = new ActiveDefenceIntent
            {
                Phase = phase,
                EnemyArmyId = enemyArmyId,
                LastKnownHex = new HexCoord(9, 1),
                PrimaryArmyId = primaryArmyId,
            },
        };
    }
}
#endif
