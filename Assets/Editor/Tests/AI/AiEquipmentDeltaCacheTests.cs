#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-15 (late-game perf, owner option A) — EquipmentDeltaCache: an equipment delta is
    // reused while what its own computation read is unchanged, and recomputed otherwise.
    public sealed class AiEquipmentDeltaCacheTests
    {
        [SetUp]
        public void SetUp() => EquipmentDeltaCache.Clear();

        [TearDown]
        public void TearDown()
        {
            EquipmentDeltaCache.Clear();
            Game.Map.ArmyRegistry.Clear();
        }

        private static EquipmentDeltaCache.ScopeKeys Scope(PlayerSetupData player, WorldSnapshot snap,
            string full, string core) => new EquipmentDeltaCache.ScopeKeys
        {
            Player = player, Snap = snap, FullKey = full,
            CoreKey = EquipmentDeltaCache.InternCore(player, snap.TurnNumber, core),
        };

        private static EquipmentUpgradeValuation.EquipmentDelta Get(EquipmentDeltaCache.ScopeKeys scope,
            WorldSnapshot snap, CardDefinition equipment, object recipient, string stamp,
            System.Func<EquipmentUpgradeValuation.EquipmentDelta> compute, object inventory = null) =>
            EquipmentDeltaCache.GetOrCompute(scope, snap, inventory, equipment, recipient, false,
                () => stamp, _ => compute());

        private static EquipmentUpgradeValuation.EquipmentDelta GetWithPart(EquipmentDeltaCache.ScopeKeys scope,
            WorldSnapshot snap, CardDefinition equipment, object recipient, string stamp,
            System.Func<EquipmentDeltaCache.CorePartSlot, EquipmentUpgradeValuation.EquipmentDelta> compute) =>
            EquipmentDeltaCache.GetOrCompute(scope, snap, null, equipment, recipient, false, () => stamp, compute);

        // 2026-11-28 (match Tessek/Halden perf) — the efficiency utility of a FULL entry (its
        // registry tail read non-core state) is reused under the same core key and recipient
        // stamp; the tail is recomputed. A FULL read before the part forbids both reuse and store.
        [Test]
        public void CorePartOfAFullEntryIsReused_OnlyWhileNothingBeforeItReadOutsideTheCore()
        {
            var player = new PlayerSetupData();
            var snap = new WorldSnapshot { Observer = player, TurnNumber = 5 };
            var equipment = new CardDefinition();
            var recipient = new object();
            int parts = 0, tails = 0;
            bool fullBeforePart = false;
            EquipmentUpgradeValuation.EquipmentDelta Compute(EquipmentDeltaCache.CorePartSlot slot)
            {
                if (fullBeforePart) EquipmentDeltaCache.MarkFull();
                if (!EquipmentDeltaCache.TryGetCorePart(slot, out int part))
                {
                    part = ++parts;
                    EquipmentDeltaCache.StoreCorePart(slot, part);
                }
                EquipmentDeltaCache.MarkFull(); // the registry tail
                return new EquipmentUpgradeValuation.EquipmentDelta(part, ++tails);
            }

            var d = GetWithPart(Scope(player, snap, "full-a", "core-1"), snap, equipment, recipient, "r1", Compute);
            Assert.That((d.Combat, d.Tactical), Is.EqualTo((1f, 1f)));
            d = GetWithPart(Scope(player, snap, "full-b", "core-1"), snap, equipment, recipient, "r1", Compute);
            Assert.That((d.Combat, d.Tactical), Is.EqualTo((1f, 2f)), "new verdict key: tail recomputed, part reused");
            d = GetWithPart(Scope(player, snap, "full-c", "core-1"), snap, equipment, recipient, "r2", Compute);
            Assert.That(d.Combat, Is.EqualTo(2f), "the recipient stamp changed: part recomputed");
            d = GetWithPart(Scope(player, snap, "full-d", "core-2"), snap, equipment, recipient, "r2", Compute);
            Assert.That(d.Combat, Is.EqualTo(3f), "the core changed: part recomputed");
            d = GetWithPart(Scope(player, snap, "full-e", "core-2"), snap, equipment, new object(), "r2", Compute);
            Assert.That(d.Combat, Is.EqualTo(4f), "another recipient never reads this part");

            fullBeforePart = true;
            d = GetWithPart(Scope(player, snap, "full-f", "core-2"), snap, equipment, recipient, "r2", Compute);
            Assert.That(d.Combat, Is.EqualTo(5f), "a FULL read before the part: not reused");
            fullBeforePart = false;
            d = GetWithPart(Scope(player, snap, "full-g", "core-2"), snap, equipment, recipient, "r2", Compute);
            Assert.That(d.Combat, Is.EqualTo(3f), "and not stored over the clean part either");

            // Outside a keyed scope there is no slot: always computed.
            d = GetWithPart(null, snap, equipment, recipient, "r2", Compute);
            Assert.That(d.Combat, Is.EqualTo(6f));
        }

        [Test]
        public void CoreEntryIsReusedAcrossStoresUntilItsCoreOrRecipientStampChanges()
        {
            var player = new PlayerSetupData();
            var snap = new WorldSnapshot { Observer = player, TurnNumber = 7 };
            var equipment = new CardDefinition();
            var recipient = new object();
            int computed = 0;
            EquipmentUpgradeValuation.EquipmentDelta Compute() =>
                new EquipmentUpgradeValuation.EquipmentDelta(++computed, 0f);

            Assert.That(Get(Scope(player, snap, "full-a", "core-1"), snap, equipment, recipient, "r1", Compute).Combat,
                Is.EqualTo(1f));
            // A new verdict store (another full key) with the same core and stamp reuses it.
            Assert.That(Get(Scope(player, snap, "full-b", "core-1"), snap, equipment, recipient, "r1", Compute).Combat,
                Is.EqualTo(1f));
            Assert.That(computed, Is.EqualTo(1));
            // The recipient's own inputs changed: recomputed.
            Assert.That(Get(Scope(player, snap, "full-c", "core-1"), snap, equipment, recipient, "r2", Compute).Combat,
                Is.EqualTo(2f));
            // The shared core changed: recomputed.
            Assert.That(Get(Scope(player, snap, "full-d", "core-2"), snap, equipment, recipient, "r2", Compute).Combat,
                Is.EqualTo(3f));
            // A different recipient never reads another recipient's entry.
            Assert.That(Get(Scope(player, snap, "full-d", "core-2"), snap, equipment, new object(), "r2", Compute).Combat,
                Is.EqualTo(4f));
        }

        [Test]
        public void FullEntryNeedsTheSameVerdictKeyAndInventory_AndMarksTheEnclosingComputation()
        {
            var player = new PlayerSetupData();
            var snap = new WorldSnapshot { Observer = player, TurnNumber = 3 };
            var equipment = new CardDefinition();
            var inner = new object();
            var outer = new object();
            var inventory = new object();
            int computed = 0;
            EquipmentUpgradeValuation.EquipmentDelta Forecast()
            {
                EquipmentDeltaCache.MarkFull(inventory: true);
                return new EquipmentUpgradeValuation.EquipmentDelta(++computed, 0f);
            }

            var a = Scope(player, snap, "full-a", "core");
            Get(a, snap, equipment, inner, "s", Forecast, inventory);
            Get(a, snap, equipment, inner, "s", Forecast, inventory);
            Assert.That(computed, Is.EqualTo(1), "same store, same inventory: reused");
            Get(a, snap, equipment, inner, "s", Forecast, new object());
            Assert.That(computed, Is.EqualTo(2), "an inventory-bound entry needs the same inventory");
            var b = Scope(player, snap, "full-b", "core");
            Get(b, snap, equipment, inner, "s", Forecast, inventory);
            Assert.That(computed, Is.EqualTo(3), "a FULL entry needs the same verdict key");

            // An outer delta that reads a FULL inner one (here: a cached hit) becomes FULL itself.
            int outerComputed = 0;
            EquipmentUpgradeValuation.EquipmentDelta Outer()
            {
                Get(b, snap, equipment, inner, "s", Forecast, inventory);
                return new EquipmentUpgradeValuation.EquipmentDelta(++outerComputed, 0f);
            }
            Get(b, snap, equipment, outer, "o", Outer, inventory);
            Get(Scope(player, snap, "full-c", "core"), snap, equipment, outer, "o", Outer, inventory);
            Assert.That(outerComputed, Is.EqualTo(2), "the outer entry inherited FULL from its inner read");
        }

        [Test]
        public void OutsideAKeyedScopeOrOnAnotherSnapshotNothingIsReused()
        {
            var player = new PlayerSetupData();
            var snap = new WorldSnapshot { Observer = player, TurnNumber = 1 };
            var other = new WorldSnapshot { Observer = player, TurnNumber = 1 };
            var equipment = new CardDefinition();
            var recipient = new object();
            int computed = 0;
            EquipmentUpgradeValuation.EquipmentDelta Compute() =>
                new EquipmentUpgradeValuation.EquipmentDelta(++computed, 0f);
            Get(null, snap, equipment, recipient, "s", Compute);
            Get(null, snap, equipment, recipient, "s", Compute);
            var scope = Scope(player, snap, "f", "c");
            Get(scope, snap, equipment, recipient, "s", Compute);
            Get(scope, other, equipment, recipient, "s", Compute);
            // A recipient without a stamp (not an own army member) is never reused.
            var loose = new object();
            Get(scope, snap, equipment, loose, null, Compute);
            Get(scope, snap, equipment, loose, null, Compute);
            Assert.That(computed, Is.EqualTo(6));
        }


        [Test]
        public void DevelopmentKeysRetainTheOrderAndPrecisionOfTheActualOpposition()
        {
            var owner = new PlayerSetupData();
            var first = new WorthIt.DefenderProfile(defense: 2f, hasCeramicArmor: false,
                attack: 2.0001f, hitPoints: 3f, initiative: 2);
            var second = new WorthIt.DefenderProfile(defense: 1f, hasCeramicArmor: false,
                attack: 3f, hitPoints: 2f, initiative: 2);
            var defenders = new[] { first, second };
            var attackers = new[] { new WorthIt.DefenderProfile(defense: 2f, hasCeramicArmor: false,
                attack: 3f, hitPoints: 5f, initiative: 2) };
            var snap = new WorldSnapshot { Observer = owner, TurnNumber = 5, Self = new SelfSnapshot(),
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] { new ArmySnapshot { Members = defenders } } } };
            (string Full, string Core) Keys() => (DevelopmentAdmission.RecipientVerdictKey(snap, null, owner, null),
                DevelopmentAdmission.RecipientDeltaCoreKey(snap, owner, null));

            var initial = Keys();
            var before = WorthIt.Estimate(attackers, defenders, 0f);
            defenders[0] = second;
            defenders[1] = first;
            var reordered = Keys();
            // The forecast has no positions: listing the same bodies in another order is the same estimate
            // (WorthIt.CanonicalBodyOrder); only the verdict KEYS below keep the actual opposition order.
            Assert.That(WorthIt.Estimate(attackers, defenders, 0f), Is.EqualTo(before));
            Assert.That(reordered.Full, Is.Not.EqualTo(initial.Full));
            Assert.That(reordered.Core, Is.Not.EqualTo(initial.Core));

            defenders[1] = first.WithState(first.HitPoints, 2.0004f, first.Defense);
            var changedAttack = Keys();
            Assert.That(changedAttack.Full, Is.Not.EqualTo(reordered.Full));
            Assert.That(changedAttack.Core, Is.Not.EqualTo(reordered.Core));
        }

        [Test]
        public void RecipientStampReadsItsOwnArmyAndPurposeButNotAnotherArmy()
        {
            var owner = new PlayerSetupData();
            var mine = new Game.Map.ArmyData { Owner = owner, Hex = new HexCoord(0, 0) };
            var host = new Game.Units.UnitData { Name = "host", Owner = owner, Attack = 2, Defense = 2,
                HitPointsCurrent = 3, HitPointsMax = 3, MoveMax = 2, MoveCurrent = 2 };
            var ally = new Game.Units.UnitData { Name = "ally", Owner = owner, Attack = 1, Defense = 1,
                HitPointsCurrent = 2, HitPointsMax = 2, MoveMax = 2, MoveCurrent = 2 };
            mine.Members.Add(host);
            mine.Members.Add(ally);
            var theirs = new Game.Map.ArmyData { Owner = owner, Hex = new HexCoord(3, 0) };
            var stranger = new Game.Units.UnitData { Name = "other", Owner = owner, Attack = 1, Defense = 1,
                HitPointsCurrent = 2, HitPointsMax = 2, MoveMax = 2, MoveCurrent = 2 };
            theirs.Members.Add(stranger);
            Game.Map.ArmyRegistry.Register(mine);
            Game.Map.ArmyRegistry.Register(theirs);
            var snap = new WorldSnapshot { Observer = owner, TurnNumber = 5, Self = new SelfSnapshot() };

            string before = DevelopmentAdmission.RecipientDeltaStamp(snap, host, null);
            Assert.That(before, Is.Not.Null);
            stranger.HitPointsCurrent = 1;
            theirs.Hex = new HexCoord(4, 0);
            Assert.That(DevelopmentAdmission.RecipientDeltaStamp(snap, host, null), Is.EqualTo(before),
                "another army's state is not this recipient's input");
            ally.HitPointsCurrent = 1;
            string allyHurt = DevelopmentAdmission.RecipientDeltaStamp(snap, host, null);
            Assert.That(allyHurt, Is.Not.EqualTo(before), "an army member is an input (allies, AA order)");
            mine.Hex = new HexCoord(1, 0);
            Assert.That(DevelopmentAdmission.RecipientDeltaStamp(snap, host, null), Is.Not.EqualTo(allyHurt),
                "the army's position is an input (route, detection, air distance)");
            var purpose = new MissionIntent { Kind = MissionKind.Scout,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Refresh, FocusHex = new HexCoord(2, 2) } };
            Assert.That(DevelopmentAdmission.RecipientDeltaStamp(snap, host, purpose),
                Is.Not.EqualTo(DevelopmentAdmission.RecipientDeltaStamp(snap, host, null)),
                "the purpose is an input");
            Assert.That(DevelopmentAdmission.RecipientDeltaStamp(snap,
                    new Game.Units.UnitData { Name = "loose", Owner = owner }, null), Is.Null,
                "a unit outside own armies is never reused");
        }
    }
}
#endif
