using System;
using System.Collections.Generic;
using Game.Ai;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.UI;
using Game.Units;
using UnityEngine;

namespace Game.Map
{
    // Hex Events half of HexSelectionController — split out purely for file size, same reasoning
    // as the .Movement.cs/.Factory.cs split. Owns the "clean hex" trigger (see
    // HexSelectionController.Movement.cs's own shouldStopEarly/onComplete wiring, which calls
    // BeginCleanHexEvent once ArmyRegistry/RestackArmiesOn are already consistent for the hex the
    // mover just landed on), the "collision hex" trigger (TriggerHexEventIfClear, called from
    // BattleScreenUI.Combat.cs's ResolveHexAfterVictory once nothing hostile is left standing on
    // a hex that carries an active event), and reward granting — see each method's own comment.
    public partial class HexSelectionController
    {
        [SerializeField] private EventChoicePopupUI eventChoicePopup;
        [SerializeField] private EventRewardPopupUI eventRewardPopup;
        // Own serialized slot rather than sharing GameTurnController's — this project doesn't
        // pass single scene references between controllers, each gets its own Inspector-assigned
        // link to the same object (see e.g. battleScreen above, also independently referenced by
        // GameTurnController).
        [SerializeField] private CardHandUI cardHandUI;

        // AllResourceTypes already exists on the main HexSelectionController.cs file (used by its
        // own resource-action-row wiring) — reused here rather than redeclared.

        // Called from HexSelectionController.Movement.cs's onComplete, once ArmyRegistry.MoveArmy
        // and RestackArmiesOn(originHex, ...) have already run for this move — deliberately NOT
        // called synchronously from shouldStopEarly (mid-coroutine, before that re-keying), so a
        // popup showing or a battle opening here never sees the mover still registered at its
        // stale origin hex.
        private void BeginCleanHexEvent(ArmyData mover, HexCoord hex, HexCoord finalDestination, ArmyController moverController)
        {
            HexEventRegistry.Entry entry = HexEventRegistry.FindAt(hex);
            if (entry == null || entry.Consumed)
                return; // shouldStopEarly's own check already guarantees this, but stay defensive

            // Same "clear just the origin/departed hex's own selection, not a full Deselect()"
            // treatment as the ordinary enemy-contact branch just above this call site in
            // Movement.cs's onComplete — see that branch's own comment for why _selectedArmy
            // itself is left alone.
            _selectedHex = null;
            if (highlight != null) highlight.Hide();
            if (infoPanel != null) infoPanel.Hide();
            if (armyInfoPanel != null) armyInfoPanel.Hide();
            if (armyButtonRow != null) armyButtonRow.Hide();

            // Return value unused here — this caller never Hide()s the battle screen itself
            // afterward, so it has no clobbered-callback risk to guard against (see
            // TriggerHexEventIfClear's own comment for the caller that does).
            ShowEventChoice(mover, hex, entry, onSkip: () => ResolveEventSkip(mover, finalDestination, moverController));
        }

        // Called once nothing hostile is left standing on `hex` at all — see ResolveHexAfterVictory
        // (BattleScreenUI.Combat.cs's own post-battle hook, its only caller), which covers both a
        // normal Ground Combat round and a Capture Kill Challenge alike. entry.Triggered tells two
        // situations apart: false means this is the first time the hex has gone clear with this
        // event still active — an unrelated "collision" army (see CitadelSetupController.
        // MapContent.GenerateRandomEvents) just lost, and the event itself hasn't been asked
        // anything yet, so it gets the exact same Explore/Skip choice a "clean" hex shows on entry
        // (see BeginCleanHexEvent) — per the user's own call, a Hex Event never resolves itself
        // just because whatever ELSE was sharing its hex happened to die. True means Explore was
        // already chosen once (see ResolveEventExplore) and THIS fight was that choice's own guard
        // fight — nothing left to ask, the reward just pays out, matching the manual's own
        // "guarded → fight → reward" sequence with no extra click in between.
        // Return value: true only when this call synchronously opened (or reopened) the battle
        // screen for a guard fight (see ResolveEventExplore's own return) — BattleScreenUI.
        // Combat.cs's ResolveHexAfterVictory (this method's only non-BeginCleanHexEvent caller)
        // needs to know that, since it would otherwise fall through to its own Hide() right after
        // this returns, tearing down the fight this call just reentrantly opened on the SAME
        // battleScreen instance and losing whatever completion callback the ORIGINAL battle
        // (the one that just triggered this event) was still counting on _onClosed to fire — see
        // BattleScreenUI.Show's single shared _onClosed field, clobbered by the second Show() call
        // before the first ever gets a chance to invoke it (the project owner's own report,
        // 2026-08-16: an AI army's win over a neutral guarding this exact event hung the turn
        // loop, because ResolveDelayedBattlesThen's own WaitUntil(() => closed) never saw its
        // closure fire). BeginCleanHexEvent (the other caller) never Hide()s the battle screen
        // itself, so it has no need for this and just discards it.
        public bool TriggerHexEventIfClear(HexCoord hex, ArmyData survivor)
        {
            HexEventRegistry.Entry entry = HexEventRegistry.FindAt(hex);
            if (entry == null || entry.Consumed || survivor?.Owner == null)
                return false;

            if (entry.Triggered)
            {
                GrantEventReward(hex, survivor);
                return false;
            }

            return ShowEventChoice(survivor, hex, entry, onSkip: () => { });
        }

        // Shared by BeginCleanHexEvent (hex-entry trigger) and TriggerHexEventIfClear (post-battle,
        // "collision hex just went clear" trigger) — the only difference between the two is what
        // "Skip" means for that caller (continue a queued move vs. simply leave the event
        // untouched for a later visit), so onSkip is the one thing left for each to supply. Return
        // value — see TriggerHexEventIfClear's own comment for why this matters at all: true only
        // for the AI/Neutral branch's own ResolveEventExplore actually opening a guard fight right
        // now; the human branch always returns false since eventChoicePopup resolves asynchronously
        // (by the time a human clicks Explore, whatever battle triggered this has long since
        // Hide()-d safely on its own).
        private bool ShowEventChoice(ArmyData mover, HexCoord hex, HexEventRegistry.Entry entry, Action onSkip)
        {
            if (mover.Owner != null && mover.Owner.IsHuman && eventChoicePopup != null)
            {
                eventChoicePopup.Show(mover, entry.Definition,
                    onExplore: () => ResolveEventExplore(mover, hex, entry),
                    // Marked right here, ahead of the caller's own onSkip (BeginCleanHexEvent's
                    // move-continuation vs. TriggerHexEventIfClear's no-op) — see
                    // HexEventRegistry.MarkSkipped for why this is the one place both the human
                    // and AI decline branches below funnel through.
                    onSkip: () => { HexEventRegistry.MarkSkipped(hex); onSkip(); });
                return false;
            }
            else
            {
                // AI/Neutral mover — never shown a popup, matches battleContactPopup's own
                // human-only gating a few lines above this call site (see the user's own
                // explicit instruction: no popup ever renders/waits for an AI-only army).
                bool shouldExplore = AiEventPlanner.ShouldExplore(mover, entry);
                AiDebugLog.Write($"[AI] {mover.Owner?.Nickname ?? "Neutral"}: {mover.Name} visited event '{entry.Definition.name}' at {hex} — {(shouldExplore ? "accepted" : "declined")}.");
                if (shouldExplore)
                    return ResolveEventExplore(mover, hex, entry);
                HexEventRegistry.MarkSkipped(hex);
                onSkip();
                return false;
            }
        }

        // "Изучить" — per the user's own spec: movement is over for the turn regardless of what
        // happens next (MoveCurrent zeroed right away, same moment BattleScreenUI.Combat.cs's own
        // BeginAttack already applies this exact rule for a Tactical Battle Module attack).
        // entry.Triggered is set right here, unconditionally — see its own comment — so
        // ResolveHexAfterVictory's post-battle hook can tell this guard's own fight apart from a
        // later, unrelated one on the same hex once it ends. A guardless event skips straight to
        // the reward; a guarded one spawns its guard fresh, right now (see SpawnEventGuard's own
        // comment on why it was never spawned any earlier than this).
        // Return value — see ShowEventChoice/TriggerHexEventIfClear's own comments: true only when
        // this call just opened a fresh guard fight on battleScreen (the two GrantEventReward
        // branches never touch battleScreen at all, so false is always correct for them).
        private bool ResolveEventExplore(ArmyData mover, HexCoord hex, HexEventRegistry.Entry entry)
        {
            foreach (UnitData member in mover.Members)
                member.MoveCurrent = 0;
            entry.Triggered = true;

            if (entry.ResolvedGuardMembers == null || entry.ResolvedGuardMembers.Count == 0)
            {
                GrantEventReward(hex, mover);
                return false;
            }

            ArmyData guard = SpawnEventGuard(hex, entry);
            if (guard == null) // every member entry failed to resolve — nothing to actually fight
            {
                GrantEventReward(hex, mover);
                return false;
            }

            var participants = new List<ArmyData> { mover, guard };
            // Same hero-only branch every other contact point in this project already has (see
            // HexSelectionController.Movement.cs's own onFight callback) — nothing for a normal
            // Ground Combat round to do against a target with no non-hero units.
            if (!BattleInitiator.IsCombatCapable(guard))
                battleScreen?.BeginCaptureKillEncounter(mover, guard, null);
            else
                battleScreen?.Show(hex, participants, null);
            return true;
        }

        // Builds `entry`'s own guard as a fresh ArmyData, right now — the only place one of these
        // is ever created (see HexEventRegistry.Entry.ResolvedGuardMembers's own comment for why
        // it's never pre-spawned at map-generation time any more): a guard that physically existed
        // on the map from the start, even with its marker hidden, was still a live ArmyRegistry
        // entry BattleInitiator.FindEnemyAt could find — a red move-preview arrow leaked its
        // presence before the player ever got near it, and it made a "collision hex" look
        // permanently un-cleared even after the unrelated army actually sharing it was beaten (see
        // the user's own report). Registered in ArmyRegistry so the Tactical Battle Module/Capture
        // Kill Challenge can fight it exactly like any other army, but deliberately never given a
        // marker — DeleteArmyIfEmptied (BattleScreenUI.Combat.cs's own post-battle cleanup,
        // unchanged) tears it back out of ArmyRegistry the moment it's defeated, same as any other
        // army that hits zero members, so there was never a moment for a marker to matter.
        private ArmyData SpawnEventGuard(HexCoord hex, HexEventRegistry.Entry entry)
        {
            var guard = new ArmyData { Name = entry.GuardArmyName, Hex = hex, Owner = entry.GuardOwner };
            ArmyRegistry.Register(guard);

            foreach ((CardDefinition card, int count) in entry.ResolvedGuardMembers)
            {
                if (card == null)
                    continue;
                for (int i = 0; i < count; i++)
                {
                    UnitData spawned = SpawnUnit(card.displayName, entry.GuardOwner, card.moveMax,
                        card.activationApCost, card.cardType == CardType.Hero, card.commandRating, card.art,
                        card.grantedAbilities, card.attack, card.range, card.hitPoints, card.initiative, card.fate,
                        card.defenseRating, card.resistanceRating, card.unitTypeTags, card.detailArt,
                        card.apCost, card.resourceCost);
                    if (spawned != null)
                        guard.AddMemberSorted(spawned);
                }
            }

            if (guard.Members.Count == 0)
            {
                ArmyRegistry.Unregister(guard);
                return null;
            }
            return guard;
        }

        // "Пропустить" — the event stays un-consumed and untouched, exactly as it was, so it
        // retriggers identically on any future visit (the user's own explicit rule). If the mover
        // was following a multi-hex pathfinder order toward a destination further along, a fresh
        // order is reissued right away so it "continues along the pathfinder" per the spec — this
        // codebase has no pause/auto-resume primitive for an in-progress MoveRoutine (see
        // ArmyController.MoveRoutine's own shouldStopEarly contract: an early stop is permanent),
        // so this simulates the same effect the way any other early-stopped move already requires
        // a fresh order to continue. A single manual-click move (already at its destination, or
        // out of movement) just leaves the army where it is, same as any other early stop today.
        private void ResolveEventSkip(ArmyData mover, HexCoord finalDestination, ArmyController moverController)
        {
            if (!mover.Hex.Equals(finalDestination) && mover.CurrentMovement > 0)
                IssueMoveOrder(moverController, finalDestination);
        }

        // The one place a Hex Event's reward is ever actually paid out — called both for the
        // "clean hex" Explore branch above (guardless case directly, guarded case once combat's
        // fully resolved — see ResolveEventExplore's own comment) AND from TriggerHexEventIfClear,
        // for the "collision hex" case once its own guard fight (started by that same Explore
        // choice) is fully resolved — so there's only one implementation of "what a reward means"
        // to ever drift out of sync between the two cases.
        public void GrantEventReward(HexCoord hex, ArmyData recipient)
        {
            HexEventRegistry.Entry entry = HexEventRegistry.FindAt(hex);
            if (entry == null || entry.Consumed || recipient?.Owner == null)
                return;

            PlayerRoot root = PlayerRootRegistry.FindFor(recipient.Owner);
            var grantedText = new List<string>();
            foreach (RewardEntry reward in entry.Definition.rewards)
            {
                if (reward.type != RewardType.Resources)
                    continue;
                foreach (ResourceType type in AllResourceTypes)
                {
                    int amount = reward.resources.Get(type);
                    if (amount > 0)
                    {
                        root?.AddResource(type, amount);
                        grantedText.Add($"{amount} {type}");
                    }
                }
            }
            foreach ((RewardEntry _, CardDefinition card) in entry.ResolvedCardRewards)
            {
                if (card == null)
                    continue;
                GrantCard(recipient.Owner, card);
                grantedText.Add(card.displayName);
            }

            HexEventRegistry.MarkConsumed(hex);

            if (recipient.Owner.IsHuman)
            {
                eventRewardPopup?.Show(entry, recipient);
            }
            else
            {
                // Mirrors the human popup branch above, but to AiDebugLog instead of a UI popup —
                // same "no popup ever renders for an AI-only army" rule as BeginCleanHexEvent's own
                // human-only gating, so this is the only record an AI's reward ever gets.
                string rewardSummary = grantedText.Count > 0 ? string.Join(", ", grantedText) : "nothing";
                AiDebugLog.Write($"[AI] {recipient.Owner?.Nickname ?? "Neutral"}: {recipient.Name} received reward for event '{entry.Definition.name}' at {hex} — {rewardSummary}.");
            }
        }

        // Public so Game.Aviation.AviationActions can hand a stored/flying aircraft's card back
        // to its owner the same way a hex-event reward does (see ReturnAircraftToDeck) — same
        // human/AI hand routing either way, no separate "returned" destination of its own.
        public void GrantCard(PlayerSetupData owner, CardDefinition card)
        {
            if (cardHandUI == null || owner == null || card == null)
                return;
            if (owner.IsHuman)
                cardHandUI.AddCard(new CardData(card));
            else
                AiHandRegistry.GetOrCreate(owner, cardHandUI.StartingDeckCatalog, cardHandUI.StartingHandSize)?.Hand.Add(new CardData(card));
        }
    }
}
