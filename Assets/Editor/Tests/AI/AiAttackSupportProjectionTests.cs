#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  2026-11-02 — what an Attack march meets (AttackSupportProjection) and the allocator's
    //  negative-value gate. Playtest Rurik vs Draven: marches started and turned back on
    //  unchanged facts; a fresh march at value -10.3 was funded.
    // ===========================================================================================
    public sealed class AiAttackSupportProjectionTests
    {
        private static readonly HexCoord Site = new HexCoord(3, 0);
        private PlayerSetupData _player;
        private PlayerSetupData _enemy;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            _player = new PlayerSetupData { Nickname = "Us" };
            _enemy = new PlayerSetupData { Nickname = "Red" };
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
        }

        // A carrier standing on the hex removes the structural bonus only: terrain stays, and so
        // does an army's own non-structural part of its exact bonus.
        [Test]
        public void Disruption_RemovesOnlyTheStructuralPartOfTheBonus()
        {
            var opposition = new List<WorthIt.DefendingArmy>
            {
                new WorthIt.DefendingArmy(new[] { Body() }, default, defenseBonusOverride: 3f, armyId: 5,
                    buildingDefenseBonus: 2f),
                new WorthIt.DefendingArmy(new[] { Body() }, default, armyId: 6, buildingDefenseBonus: 2f),
            };

            List<WorthIt.DefendingArmy> after = AttackSupportProjection.WithoutStructure(opposition, 3f, out float bonus);

            Assert.That(bonus, Is.EqualTo(1f), "terrain 1 + structure 2 -> terrain 1");
            Assert.That(after[0].DefenseBonusOverride, Is.EqualTo(1f));
            Assert.That(after[1].DefenseBonusOverride, Is.Null, "the shared hex bonus stays the fallback");
            Assert.That(after.All(a => a.BuildingDefenseBonus == 0f), Is.True);
            Assert.That(after.Select(a => a.ArmyId), Is.EqualTo(new int?[] { 5, 6 }));
        }

        // Only a fielded carrier on an active Disruption leg that arrives no later than the fist
        // counts; a slower one, a non-carrier or a suspended leg does not.
        [Test]
        public void Carrier_CountsOnlyWhenOnAnActiveLegAndNoLaterThanTheFist()
        {
            ArmySnapshot carrier = Carrier(2, maxMovement: 3);
            WorldSnapshot snap = Snapshot(carrier);
            MissionIntent leg = PutLeg(2, IntentStatus.Active);

            Assert.That(AttackSupportProjection.CountedCarrier(snap, Site, armyEta: 0), Is.EqualTo(2),
                "two hexes with three movement points: there this turn");

            carrier.MaxMovement = 1; carrier.CurrentMovement = 1;
            Assert.That(AttackSupportProjection.CountedCarrier(snap, Site, armyEta: 0), Is.Null,
                "a carrier arriving after the fist removes nothing from that fight");
            Assert.That(AttackSupportProjection.CountedCarrier(snap, Site, armyEta: 1), Is.EqualTo(2));

            carrier.Members = carrier.MembersWithHeroes = new[] { Body() };
            Assert.That(AttackSupportProjection.CountedCarrier(snap, Site, armyEta: 5), Is.Null,
                "an army without a carrier is no Disruption support");

            carrier.Members = carrier.MembersWithHeroes = new[] { CarrierBody() };
            leg.Status = IntentStatus.Suspended;
            Assert.That(AttackSupportProjection.CountedCarrier(snap, Site, armyEta: 5), Is.Null);
        }

        // 2026-11-07 playtest (Ysolde T26): with two free stealth Scouts beside, the gather swapped
        // a Crawler out of the fist for an in-fist carrier, because only a carrier already on a
        // Disruption leg counted. A free one counts like a free wing: for a march of a turn or
        // more, arriving no later than the fist, held by nothing but a displaceable Recon lane.
        [Test]
        public void FreeCarrier_CountsLikeAFreeWing()
        {
            ArmySnapshot carrier = Carrier(2, maxMovement: 3);
            WorldSnapshot snap = Snapshot(carrier);

            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.EqualTo(2));
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 0), Is.Null,
                "a fist striking this turn waits for nobody: only a bound leg counts then");

            carrier.MaxMovement = 1; carrier.CurrentMovement = 1;
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.EqualTo(2),
                "two hexes at one movement point: next turn, with the fist");
            carrier.Hex = new HexCoord(-1, 0);
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.Null,
                "four hexes at one movement point: after the fist");
            carrier.Hex = new HexCoord(1, 0); carrier.MaxMovement = 3; carrier.CurrentMovement = 3;

            var refresh = new MissionIntent { Kind = MissionKind.Scout, Status = IntentStatus.Active,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Refresh, FocusHex = new HexCoord(5, 5) },
                PreferredMoverArmyId = 2,
                IntentKey = MissionIntentKey.ForScout(ScoutTargetKind.Refresh, new HexCoord(5, 5)) };
            MissionIntentRegistry.GetOrCreate(_player).Put(refresh);
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.EqualTo(2),
                "a scout on a displaceable Refresh lane is still free for the operation");

            refresh.Funding = CommitmentTier.Hard;
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.Null,
                "a Hard lane keeps its scout");

            refresh.Funding = CommitmentTier.None;
            // The same honest entry rule as the delivery itself: a hard-blocked site or certain
            // detection there means no hidden carrier will stand on it.
            snap.MapKnowledge = new MapKnowledgeSnapshot { ScoutHardBlockedHexes = new HashSet<HexCoord> { Site } };
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.Null,
                "a site blocked for a stealth scout");
            snap.MapKnowledge.ScoutHardBlockedHexes.Clear();
            snap.Known.EnemySightings = Enumerable.Range(0, (int)Math.Ceiling(AiConfigV2.scoutDetectionRiskNorm))
                .Select(id => new AiMapMemory.KnownEnemySighting(Site, _enemy, "detector", 1, 1f, 1f,
                    new[] { Body() }, armyId: 90 + id)).ToList();
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.Null,
                "certain detection on the site");
            carrier.Hex = Site;
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.EqualTo(2),
                "a carrier already standing hidden there counts as it stands");
            carrier.Hex = new HexCoord(1, 0);
            snap.Known.EnemySightings = Array.Empty<AiMapMemory.KnownEnemySighting>();

            carrier.IsHidden = false;
            carrier.Members = carrier.MembersWithHeroes = new[] { Body() };
            Assert.That(AttackSupportProjection.FreeCarrier(snap, Site, armyEta: 1), Is.Null,
                "no Disruption carrier, no support");
        }

        // The Disruption a live Attack counts on keeps Attack's priority (AttackReadiness full), so
        // it can take a scout off a lower Refresh lane; with no live Attack it has none.
        [Test]
        public void DisruptionForALiveAttack_CarriesAttackPriority()
        {
            var fist = new ArmySnapshot { ArmyId = 9, Owner = _player, Hex = new HexCoord(0, 0),
                MemberCount = 1, Members = new[] { Body() }, MembersWithHeroes = new[] { Body() },
                MaxMovement = 2, CurrentMovement = 2 };
            WorldSnapshot snap = Snapshot(Carrier(2, maxMovement: 3), fist);
            Assert.That(ReconObjectiveEvaluator.DisruptAt(snap, Site, requireMover: false), Is.Null,
                "no Attack and no ready fist nearby: nothing needs the carrier");

            MissionIntentRegistry.GetOrCreate(_player).Put(new MissionIntent
            {
                Kind = MissionKind.Attack, Status = IntentStatus.Active, PreferredMoverArmyId = 9,
                Objective = new AttackIntent { Target = AttackTargetRef.For(Site, _enemy, AttackTargetKind.Base),
                    Phase = AttackMissionPhase.Gather, PrimaryArmyId = 9 },
                IntentKey = MissionIntentKey.ForAttack(AttackTargetRef.For(Site, _enemy, AttackTargetKind.Base)),
            });
            ReconObjective o = ReconObjectiveEvaluator.DisruptAt(snap, Site, requireMover: false);
            Assert.That(o, Is.Not.Null);
            Assert.That(o.TaskScore[TaskSlot.AttackReadiness],
                Is.EqualTo(TaskScoreEvaluator.AttackReadiness(1f)).Within(0.0001f));
        }

        // 2026-11-07 playtest (Ysolde T26): E6 M9 T5 bought one Tactical Missile, but three upgrades
        // in the card rounds (a Fate genome on a garrison hero among them) spent T to 0 before the
        // strike was asked. An upgrade outside the fist leaves the strike what it needs of the
        // resources the upgrade itself spends; the fist's own upgrades are never held back.
        [Test]
        public void UpgradeOutsideTheFist_LeavesTheStrikeItsResources()
        {
            PlayerRoot root = PlayerRoot.Create(_player, "strike support test");
            PlayerRootRegistry.Register(_player, root);
            try
            {
                root.AddResource(Game.Economy.ResourceType.Tech, 5);
                root.AddResource(Game.Economy.ResourceType.Energy, 6);
                var attack = new AttackIntent { PrimaryArmyId = 9, Phase = AttackMissionPhase.Gather,
                    Target = AttackTargetRef.For(Site, _enemy, AttackTargetKind.Citadel) };
                var operation = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                    PreferredMoverArmyId = 9, Objective = attack,
                    IntentKey = MissionIntentKey.ForAttack(attack.Target) };
                var pass = new PhaseAPass(Snapshot(), _player, root, null, new AiTurnContext { TurnNumber = 10 },
                    null, null, null, null, null, null, null, null, false,
                    new StrategicPhaseResult { Reservation = new MaterializationReservation() })
                {
                    StrikeSupport = new StrikeSupportNeed { Operation = operation, Attack = attack,
                        Cost = new ResourceCost(energy: 5, materials: 6, tech: 5), Source = "generate:test" },
                };
                MaterializationPlan Upgrade(ResourceCost cost, int recipient) => new MaterializationPlan
                    { ResCost = cost, DevelopmentUpgrade = new DevelopmentOpportunity { RecipientArmyId = recipient } };

                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(pass,
                    Upgrade(new ResourceCost(tech: 1), recipient: 4), out string why), Is.False);
                Assert.That(why, Does.Contain("Tech"));
                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(pass,
                    Upgrade(new ResourceCost(energy: 1), recipient: 4), out _), Is.True,
                    "E6 covers the upgrade's 1 and the strike's 5; Tech it does not spend");
                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(pass,
                    Upgrade(new ResourceCost(tech: 1), recipient: 9), out _), Is.True,
                    "the fist's own upgrade keeps its priority");
                // Phase B: an Equipment card from hand for a unit outside the fist, the same rule.
                var play = new NonCombatCardPlayer.NonCombatPlay { Kind = NonCombatCardPlayer.PlayKind.Equipment,
                    ResCost = new ResourceCost(tech: 1) };
                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(_player, root, pass.Ctx,
                    pass.StrikeSupport, play, out why), Is.False);
                Assert.That(why, Does.Contain("equipment"));
                play.ResCost = new ResourceCost(energy: 1);
                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(_player, root, pass.Ctx,
                    pass.StrikeSupport, play, out _), Is.True);
                // 2026-11-09 (P6a) — when the strike is a bomb, attaching that bomb IS the strike.
                var bomb = new CardDefinition { cardType = CardType.Equipment, attachmentSlot = AttachmentSlot.Equipment,
                    grantedAbilities = new System.Collections.Generic.List<string> { UnitAbilities.SingleUse },
                    equipment = new EquipmentGrant {
                        hostTypeTags = new System.Collections.Generic.List<UnitTypeTag> { UnitTypeTag.Aircraft },
                        addAbilities = new System.Collections.Generic.List<string> { UnitAbilities.Explosion3 } } };
                pass.StrikeSupport.Source = "hand-bomb:Air Bomb 3";
                var attachBomb = new NonCombatCardPlayer.NonCombatPlay { Kind = NonCombatCardPlayer.PlayKind.Equipment,
                    Card = new CardData(bomb), ResCost = new ResourceCost(tech: 5) };
                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(_player, root, pass.Ctx,
                    pass.StrikeSupport, attachBomb, out _), Is.True);
                play.ResCost = new ResourceCost(tech: 1);
                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(_player, root, pass.Ctx,
                    pass.StrikeSupport, play, out _), Is.False, "any other equipment still yields to the bomb");
                pass.StrikeSupport = null;
                Assert.That(PhaseAOperationSupport.LeavesStrikeSupport(pass,
                    Upgrade(new ResourceCost(tech: 5), recipient: 4), out _), Is.True);
            }
            finally
            {
                PlayerRootRegistry.Clear();
                StrategicResourceReservationLedger.ClearAll();
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }

        // No known defenders: nothing to project, the forecast is the bare package.
        [Test]
        public void Forecast_WithoutKnownDefenders_CountsNoSupport()
        {
            WorldSnapshot snap = Snapshot(Carrier(2, maxMovement: 3));
            PutLeg(2, IntentStatus.Active);
            AttackSupportForecast forecast = AttackSupportProjection.For(snap, Site, armyEta: 0);
            Assert.That(forecast.CountsSupport, Is.False);
            Assert.That(forecast.Describe(), Is.EqualTo("support=none"));
        }

        // P1b: a fresh proposal whose cost outweighs its benefit gets no AP even with AP to spare,
        // not from the remainder either; a zero-valued and a durable leg still do.
        [Test]
        public void Allocator_NeverFundsAFreshNegativeValueProposal()
        {
            var snap = new WorldSnapshot { TurnNumber = 1, Self = new SelfSnapshot { ActionPoints = 10 } };
            MissionProposal negative = Scout(new HexCoord(1, 0), -10.3f, durable: false);
            MissionProposal zero = Scout(new HexCoord(2, 0), 0f, durable: false);
            MissionProposal durable = Scout(new HexCoord(3, 0), -1f, durable: true);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { negative, zero, durable }, new List<Commitment>(), _player);

            TentativeAllocation a = session.Pack();

            Assert.That(a.Funded.Any(f => f.Mission == negative), Is.False);
            Assert.That(a.Deferred.Single(d => d.Mission == negative).Reason, Is.EqualTo(DeferReason.NegativeValue));
            Assert.That(a.Funded.Any(f => f.Mission == zero), Is.True, "a zero-ranked proposal still takes spare AP");
            Assert.That(a.Funded.Any(f => f.Mission == durable), Is.True, "a durable leg carries its commitment");
        }

        // Proposal 3: a hex held by one army is a plain capture — there is no trade to make.
        [Test]
        public void Exchange_SingleDefendingArmy_IsNotATrade()
        {
            AttackExchange x = AttackExchangePolicy.Assess(Strong(), default,
                new[] { new WorthIt.DefendingArmy(Weak(), default, armyId: 5) }, 0f);
            Assert.That(x.Favorable, Is.False);
            Assert.That(x.Describe(), Is.EqualTo("exchange=n/a"));
        }

        // The strongest army must really fall: an unbeatable first battle is never a trade, however
        // much power stands behind it.
        [Test]
        public void Exchange_UnbeatableFirstBattle_IsUnfavorable()
        {
            var opposition = new[]
            {
                new WorthIt.DefendingArmy(new[] { Fighter(900, 900, 9000) }, default, armyId: 5),
                new WorthIt.DefendingArmy(Weak(), default, armyId: 6),
            };
            AttackExchange x = AttackExchangePolicy.Assess(Strong(), default, opposition, 0f);
            Assert.That(x.Battles, Is.EqualTo(2));
            Assert.That(x.FirstWin, Is.LessThan(GroundCombatAdmissionPolicy.AttackWinChanceGate));
            Assert.That(x.Favorable, Is.False);
        }

        // Two beatable armies: both fall, little of the fist is lost — the expected kill outweighs
        // the risk and the per-battle chances are ordered (through more battles is never likelier).
        [Test]
        public void Exchange_BeatableArmies_KillOutweighsRisk()
        {
            var opposition = new[]
            {
                new WorthIt.DefendingArmy(Weak(), default, armyId: 5),
                new WorthIt.DefendingArmy(Weak(), default, armyId: 6),
            };
            AttackExchange x = AttackExchangePolicy.Assess(Strong(), default, opposition, 0f);
            Assert.That(x.FirstWin, Is.GreaterThanOrEqualTo(x.CaptureWin));
            Assert.That(x.EnemyKilled, Is.GreaterThan(x.OwnAtRisk));
            Assert.That(x.Favorable, Is.True);
        }

        private static List<WorthIt.DefenderProfile> Strong() => new List<WorthIt.DefenderProfile>
            { Fighter(40, 8, 120), Fighter(40, 8, 120), Fighter(40, 8, 120) };

        private static WorthIt.DefenderProfile[] Weak() => new[] { Fighter(12, 2, 3), Fighter(12, 2, 3) };

        private static WorthIt.DefenderProfile Fighter(float atk, float def, float hp) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, 4, null, hp);

        private MissionIntent PutLeg(int carrierId, IntentStatus status)
        {
            var intent = new MissionIntent { Kind = MissionKind.Scout, Status = status,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.DisruptStructure, FocusHex = Site,
                    RequiresStealth = true }, PreferredMoverArmyId = carrierId,
                IntentKey = MissionIntentKey.ForScout(ScoutTargetKind.DisruptStructure, Site) };
            MissionIntentRegistry.GetOrCreate(_player).Put(intent);
            return intent;
        }

        private static MissionProposal Scout(HexCoord hex, float value, bool durable)
        {
            var m = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = hex },
                BaseValue = value, EffectiveValue = value, LocalAdmissionScore = value,
                FromDurableIntent = durable,
                Requirements = new MissionRequirements { ApMinimum = 2f, ApDesired = 2f, ApMaximum = 2f },
            };
            m.Axes.Value[DesireAxis.Recon] = 1f;
            return m;
        }

        private ArmySnapshot Carrier(int id, int maxMovement) => new ArmySnapshot
        {
            ArmyId = id, Owner = _player, Hex = new HexCoord(1, 0), IsSoloRecce = true, MemberCount = 1,
            Members = new[] { CarrierBody() }, MembersWithHeroes = new[] { CarrierBody() },
            CurrentMovement = maxMovement, MaxMovement = maxMovement, IsHidden = true,
        };

        private WorldSnapshot Snapshot(params ArmySnapshot[] armies) => new WorldSnapshot
        {
            Observer = _player, TurnNumber = 10, Intents = MissionIntentRegistry.GetOrCreate(_player),
            Self = new SelfSnapshot { Armies = armies.ToList(), BaseHexes = new[] { new HexCoord(0, 0) } },
            Known = new KnownSnapshot
            {
                Buildings = new[] { new AiMapMemory.KnownBuilding(Site, _enemy, true, Array.Empty<string>(),
                    isBase: true, defense: 10f) },
                EnemySightings = Array.Empty<AiMapMemory.KnownEnemySighting>(),
                NeutralSightings = Array.Empty<AiMapMemory.KnownEnemySighting>(),
                EventGuards = Array.Empty<KnownEventGuardSnapshot>(),
            },
        };

        private static WorthIt.DefenderProfile CarrierBody() => new WorthIt.DefenderProfile(
            1f, false, attack: 1f, hitPoints: 4f, abilities: new[] { UnitAbilities.Disruption, "r1s0", "Stealth4" });

        private static WorthIt.DefenderProfile Body() =>
            new WorthIt.DefenderProfile(2f, false, null, 4f, 4f, 4, null, 4f);
    }
}
#endif
