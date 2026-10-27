#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class HexRegistryStatePreparationTests
    {
        [TearDown] public void Clear()
        { HexResourceBonusRegistry.Clear(); HexEventRegistry.Clear(); VisionSystem.Clear(); }

        [Test] public void ResourceBonusCaptureAndRestoreCopyMutableYieldsInBothDirections()
        {
            var hex = new HexCoord(3, 5); var live = new ResourceYields { human = 4, energy = 2 };
            HexResourceBonusRegistry.Set(hex, live);
            var state = HexResourceBonusRegistry.CaptureState();
            live.human = 9;
            Assert.That(state[0].Bonus.human, Is.EqualTo(4));
            state[0].Bonus.energy = 6;
            Assert.That(HexResourceBonusRegistry.GetBonus(hex).energy, Is.EqualTo(2));
            HexResourceBonusRegistry.RestoreState(state);
            state[0].Bonus.energy = 12; state.Clear();
            Assert.That(HexResourceBonusRegistry.GetBonus(hex).human, Is.EqualTo(4));
            Assert.That(HexResourceBonusRegistry.GetBonus(hex).energy, Is.EqualTo(6));
        }

        [Test] public void ResourceBonusRestoreSilentlyReplacesEntriesAndRejectsDuplicatesAtomically()
        {
            var owner = new PlayerSetupData { PlayerId = "viewer" }; var hex = new HexCoord(1, 0); var oldHex = new HexCoord(9, 0);
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState> {
                new PlayerVisionState { PlayerId = owner.PlayerId, Visible = new List<HexCoord> { hex } } } }, _ => owner);
            HexResourceBonusRegistry.Set(oldHex, new ResourceYields { human = 8 });
            int notifications = 0; Action<PlayerSetupData, HexCoord> listener = (p, h) => notifications++;
            VisionSystem.VisibleContentChanged += listener;
            try
            {
                var state = new List<HexResourceBonusState> { new HexResourceBonusState { Hex = hex, Bonus = new ResourceYields { tech = 3 } } };
                HexResourceBonusRegistry.RestoreState(state);
                Assert.That(notifications, Is.Zero);
                Assert.That(HexResourceBonusRegistry.GetBonus(oldHex), Is.Null);
                state.Add(new HexResourceBonusState { Hex = hex, Bonus = new ResourceYields { tech = 10 } });
                Assert.Throws<ArgumentException>(() => HexResourceBonusRegistry.RestoreState(state));
                Assert.That(HexResourceBonusRegistry.GetBonus(hex).tech, Is.EqualTo(3));
            }
            finally { VisionSystem.VisibleContentChanged -= listener; }
        }

        [Test] public void EventCodecBoundaryCopiesRuntimeCollectionsAndRewardValues()
        {
            var hex = new HexCoord(2, 4); var viewer = new PlayerSetupData { PlayerId = "p1" };
            var reward = new RewardEntry { type = RewardType.Resources, resources = new ResourceYields { materials = 5 } };
            HexEventRegistry.Set(hex, null, "Guards", null, viewer, new List<(RewardEntry, CardDefinition)> { (reward, null) }, new List<RewardEntry> { reward });
            HexEventRegistry.MarkSkipped(hex, viewer);
            var live = HexEventRegistry.FindAt(hex);
            var state = HexEventRegistry.CaptureEntries((observedHex, entry) =>
            { Assert.That(observedHex, Is.EqualTo(hex)); return entry; });
            var captured = state[0].Payload;
            live.KnownBy.Clear(); reward.resources.materials = 11;
            Assert.That(captured.KnownBy.Contains(viewer), Is.True);
            Assert.That(captured.SelectedRewards[0].resources.materials, Is.EqualTo(5));
            Assert.That(captured.ResolvedCardRewards[0].reward, Is.SameAs(captured.SelectedRewards[0]), "Copy retains reward aliasing within one event.");
            captured.GuardArmyName = "Restored guards";
            Assert.That(live.GuardArmyName, Is.EqualTo("Guards"));
            HexEventRegistry.RestoreEntries(state, (restoredHex, entry) =>
            { Assert.That(restoredHex, Is.EqualTo(hex)); return entry; });
            captured.SelectedRewards[0].resources.materials = 20;
            captured.DiscoveredBy.Clear(); captured.ResolvedCardRewards.Clear(); state.Clear();
            var restored = HexEventRegistry.FindAt(hex);
            Assert.That(restored.GuardArmyName, Is.EqualTo("Restored guards"));
            Assert.That(restored.SelectedRewards[0].resources.materials, Is.EqualTo(5));
            Assert.That(restored.DiscoveredBy.Contains(viewer), Is.True);
            Assert.That(restored.ResolvedCardRewards, Has.Count.EqualTo(1));
        }

        [Test] public void EventRestoreReplacesSilentlyAndCodecFailureLeavesExistingEntriesUntouched()
        {
            var oldHex = new HexCoord(6, 1); var hex = new HexCoord(7, 2);
            HexEventRegistry.Set(oldHex, null, "Old", null, null, null);
            var decoded = new HexEventRegistry.Entry { Consumed = true, Skipped = true, GuardArmyName = "Saved" };
            var state = new List<HexEventEntryState<string>> { new HexEventEntryState<string> { Hex = hex, Payload = "saved:event" } };
            int notifications = 0; Action<HexCoord> listener = _ => notifications++;
            Action<HexCoord, PlayerSetupData> discovered = (h, p) => notifications++;
            HexEventRegistry.EventSkipped += listener; HexEventRegistry.EventConsumed += listener; HexEventRegistry.EventDiscovered += discovered;
            try
            {
                HexEventRegistry.RestoreEntries(state, (h, payload) => decoded);
                Assert.That(HexEventRegistry.FindAt(oldHex), Is.Null);
                Assert.That(HexEventRegistry.FindAt(hex).Consumed, Is.True);
                Assert.That(notifications, Is.Zero);
                decoded.Consumed = false;
                Assert.That(HexEventRegistry.FindAt(hex).Consumed, Is.True);
                Assert.Throws<InvalidOperationException>(() => HexEventRegistry.RestoreEntries<string>(state, (h, payload) => throw new InvalidOperationException("Invalid saved entry")));
                Assert.That(HexEventRegistry.FindAt(hex).Consumed, Is.True);
                state.Add(new HexEventEntryState<string> { Hex = hex, Payload = "duplicate" });
                Assert.Throws<ArgumentException>(() => HexEventRegistry.RestoreEntries(state, (h, payload) => decoded));
                Assert.That(HexEventRegistry.FindAt(hex).Consumed, Is.True);
            }
            finally { HexEventRegistry.EventSkipped -= listener; HexEventRegistry.EventConsumed -= listener; HexEventRegistry.EventDiscovered -= discovered; }
        }
    }
}
#endif
