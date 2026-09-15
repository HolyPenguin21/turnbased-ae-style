#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiRaidReturnBaseTests
    {
        [Test]
        public void ReturnBaseStillValid_NoStructurallyReachableBase_KeepsOwnedFallback()
        {
            var player = new PlayerSetupData { Nickname = "ReturnFallback" };
            var fallback = new HexCoord(0, 0);
            var mover = new ArmySnapshot
            {
                ArmyId = 7,
                Owner = player,
                Hex = new HexCoord(4, 0),
                IsStructuralRaidActor = true,
                MemberCount = 1,
                MaxMovement = 3,
                ReachableOwnBaseHexes = Array.Empty<HexCoord>(),
            };
            WorldSnapshot snap = Snapshot(player, mover, fallback);

            Assert.That(MissionContinuityLayer.SelectReturnBase(snap, player, mover.ArmyId),
                Is.EqualTo(fallback), "selector deliberately supplies a deterministic fallback");
            Assert.That(MissionContinuityLayer.ReturnBaseStillValid(
                    snap, player, mover.ArmyId, fallback),
                Is.True, "validator must not immediately reject the selector's fallback");
        }

        [Test]
        public void ReturnBaseStillValid_ReachableBaseAppears_InvalidatesOldFallback_AndSelectorRetargets()
        {
            var player = new PlayerSetupData { Nickname = "ReturnRetarget" };
            var oldFallback = new HexCoord(0, 0);
            var reachable = new HexCoord(8, 0);
            var mover = new ArmySnapshot
            {
                ArmyId = 8,
                Owner = player,
                Hex = new HexCoord(1, 0),
                IsStructuralRaidActor = true,
                MemberCount = 1,
                MaxMovement = 3,
                ReachableOwnBaseHexes = new[] { reachable },
            };
            WorldSnapshot snap = Snapshot(player, mover, oldFallback, reachable);

            Assert.That(MissionContinuityLayer.ReturnBaseStillValid(
                    snap, player, mover.ArmyId, oldFallback),
                Is.False, "once a real structural route exists, an unreachable fallback is stale");
            Assert.That(MissionContinuityLayer.SelectReturnBase(snap, player, mover.ArmyId),
                Is.EqualTo(reachable), "reachable base must outrank the old fallback");
        }

        private static WorldSnapshot Snapshot(PlayerSetupData player, ArmySnapshot mover,
            params HexCoord[] bases)
        {
            var knownBases = new List<Game.Ai.AiMapMemory.KnownBuilding>();
            foreach (HexCoord hex in bases)
            {
                knownBases.Add(new Game.Ai.AiMapMemory.KnownBuilding(
                    hex, player, isStartingCitadel: hex.Equals(bases[0]), isBase: true,
                    facilityAbilities: null, collectedAmounts: null, freeFacilitySlots: 0));
            }

            return new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    BaseHexes = bases,
                    Armies = new[] { mover },
                },
                Known = new KnownSnapshot
                {
                    Buildings = knownBases,
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };
        }
    }
}
#endif
