using System;
using System.Collections;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Turns
{
    // Committed battles the turn loop resolves itself: the round-end drain of delayed and
    // contested pairings (BeginNewTurn -> ResolveDelayedBattlesThen -> ProceedWithNewTurn) and
    // the turn-start own-building contact (BeginPlayerTurn). Order is part of the rules: every
    // delayed and contested battle resolves before the next round's weather, income and tokens.
    public partial class GameTurnController
    {
        private IEnumerator ResolveDelayedBattlesThen(Action onDone)
        {
            while (true)
            {
                // Once every EXPLICITLY delayed battle has drained, sweep the whole map for
                // anything still left contested — per the user's own call, "no stealth yet"
                // means two different-owner armies can only ever coexist on a hex TEMPORARILY,
                // never past this method. Most such leftovers are a SECOND attacker that walked
                // onto a hex whose target was already reserved for one of the battles just
                // resolved above (see DelayedBattleRegistry.IsHexPending's own callers) — now
                // that the reservation's cleared, this is what actually forces their fight too,
                // still within this same turn-boundary pass. Looped (not just checked once) since
                // resolving ANY battle here can just as easily reveal another.
                if (!DelayedBattleRegistry.HasAny)
                {
                    if (!TryFindNextContestedBattle(out HexCoord contestedHex, out List<ArmyData> contestedParticipants))
                        break;
                    DelayedBattleRegistry.Add(new PendingBattle { Hex = contestedHex, Participants = contestedParticipants });
                }

                PendingBattle battle = DelayedBattleRegistry.TakeNext();

                // Participants were captured back when Delay was chosen — anything can have
                // happened to that same pairing since (one side fought and won/retreated through
                // an unrelated direct contact, an army was destroyed elsewhere, etc.). Re-check
                // it's still a genuine, current fight on this hex before reopening the battle
                // screen for it — otherwise a stale delay could resurface a fight the player
                // already resolved a completely different way, on the very hex they just left.
                if (!IsStillAGenuineBattle(battle))
                    continue;

                yield return RunCommittedBattle(battle);
            }
            onDone();
        }

        // One committed pairing, from the reveal to the closed battle screen: the round-end drain
        // above and the turn-start own-building contact (ResolveOwnBuildingContacts) share it.
        private IEnumerator RunCommittedBattle(PendingBattle battle)
        {
            // STEALTH-COMBAT-01: this delayed/contested pairing is a committed encounter the
            // instant it's drained here — reveal right now, before ShowResolved (or a direct
            // Show()/BeginCaptureKillEncounter for a no-human pairing) below ever displays it.
            // The observer can't just be battle.Participants[0].Owner — in a hot-seat game
            // that's whichever side happened to be the original mover/hunter, which is just
            // as likely to be an AI as the human this popup is actually about to render for
            // (see BattleContactPopupUI.ShowSideList's own note) — ResolveHumanObserver finds
            // the real one. The context this returns is also the single source of truth for
            // TargetHeroOnly below, instead of a second, separately re-derived bool.
            Game.Combat.BattleEncounterContext encounter = Game.Combat.BattleEncounterCoordinator.PrepareCommittedEncounter(
                battle.Hex, battle.Participants, Game.Combat.BattleEncounterCoordinator.ResolveHumanObserver(battle.Participants));

            // ShowResolved only makes sense when a human is actually there to click its own
            // "Continue" button — same human-only gating every other contact point in this
            // project already applies (see HexSelectionController.Movement.cs's own onFight/
            // onDelay branch). Ordinary AI-vs-Neutral contact no longer reaches this drain
            // at all (it fights immediately on contact now — see that same branch's own
            // comment), but TryFindNextContestedBattle's own fallback sweep just above can
            // still surface a leftover pairing with no human on either side (its own comment:
            // e.g. a hero that Escaped a Capture Kill Challenge). Without this check that
            // used to wait on a click nobody was ever going to make — the exact same hang,
            // just through a rarer door.
            // Hotseat: a participating human who is not at this screen takes it for the whole
            // committed encounter (contact report + battle) and gives it back afterwards.
            PlayerSetupData returnTo = null;
            if (encounter.PresentationObserver != null)
                yield return PlayerDecisionRouter.TakeScreenFor(encounter.PresentationObserver,
                    $"Battle of {encounter.PresentationObserver.Nickname}", p => returnTo = p);
            if (_gameOver || !GameSession.IsCurrent(matchGeneration)) yield break;
            bool acknowledged = false;
            bool fastResolve = false;
            bool anyHuman = encounter.PresentationObserver != null;
            if (battleContactPopup != null && anyHuman)
                battleContactPopup.ShowResolved(battle.Hex, battle.Participants, encounter.PresentationObserver,
                    automatic => { fastResolve = automatic; acknowledged = true; });
            else
                acknowledged = true;
            yield return new WaitUntil(() => acknowledged);

            bool closed = false;
            // Same hero-only branch as HexSelectionController.Movement.cs's own contact
            // handling — nothing for a Ground Combat round to do against a target with no
            // units, so this goes straight to a Capture Kill Challenge sequence instead
            // (see BattleScreenUI.BeginCaptureKillEncounter). Participants[0] is always the
            // original mover/hunter — see IsStillAGenuineBattle's own comment.
            if (battleScreen != null)
                battleScreen.ShowEncounter(encounter, () => closed = true, fastResolve);
            else
                closed = true;
            yield return new WaitUntil(() => closed);
            if (returnTo != null)
                yield return PlayerDecisionRouter.GiveScreenBack(returnTo);
        }

        // 2026-11-09 (project owner's decision, match P4) — at the start of `owner`'s turn, a
        // detected enemy army on the hex of `owner`'s own building is fought by `owner`'s
        // strongest army on that hex (BattleInitiator.TryFindOwnBuildingContact). Resolved after
        // turn-start AA and before the AI plans / the human's input unlocks; the same committed
        // battle path as the round-end drain (reveal, contact popup for a human, battle screen).
        // A pairing is fought at most once per turn start.
        private IEnumerator ResolveOwnBuildingContacts(PlayerSetupData owner)
        {
            var fought = new HashSet<(int Initiator, int Enemy)>();
            while (!_gameOver && CurrentPlayer == owner
                && Game.Combat.BattleInitiator.TryFindOwnBuildingContact(owner, fought,
                    out HexCoord hex, out List<ArmyData> participants))
            {
                fought.Add((participants[0].Id, participants[1].Id));
                yield return RunCommittedBattle(new PendingBattle { Hex = hex, Participants = participants });
            }
        }

        // The end-of-drain sweep ResolveDelayedBattlesThen falls back on once
        // DelayedBattleRegistry is empty — scans every occupied hex for a still-unresolved
        // conflict (armies of different owners, per the user's own "this applies to every
        // battle, not just building ones" call) and hands back a fresh PendingBattle-shaped
        // pairing for it, same as if the player had chosen Delay on it directly. `mover`
        // (always Participants[0]) must be combat-capable to match IsStillAGenuineBattle's own
        // requirement — a hex where every engageable army present is hero-only (nobody able
        // to hunt, see BattleInitiator.IsEngageable's own note) is left alone rather than returned
        // here, same as it already is everywhere else in this project; otherwise this would
        // loop forever trying to "resolve" a pairing nothing can ever actually fight. The
        // defending side (`other`) is picked via BattleInitiator.FindEnemyAt rather than a
        // hand-rolled "first engageable enemy" scan (2026-08-21 fix, consistency follow-up to
        // FindEnemyAt's own strongest-defender fix) — a multi-army hex left with several
        // unresolved contests at once (e.g. a delayed battle plus a second, uninvolved attacker
        // that also landed there) should keep offering up its strongest remaining defender each
        // time this sweep asks, not whichever one the registry happens to enumerate first.
        private static bool TryFindNextContestedBattle(out HexCoord hex, out List<ArmyData> participants)
        {
            // Stealth: two armies sharing a hex only fight here if both sides actually show up —
            // the initiator needs a visible non-hero member (CanInitiateContact, the same rule a
            // move uses), and a FULLY hidden army (StealthSystem.IsArmyFullyHidden) is never pulled
            // in, even when its opponent has detected it: it walked in hidden and did nothing, and
            // the detecting player attacks it only by an explicit attack of their own.
            foreach (HexCoord candidateHex in ArmyRegistry.AllOccupiedHexes())
            {
                List<ArmyData> armies = ArmyRegistry.AllAt(candidateHex);
                foreach (ArmyData mover in armies)
                {
                    if (!BattleInitiator.IsCombatCapable(mover) || !BattleInitiator.CanInitiateContact(mover))
                        continue;

                    // A building's own garrison is the exception: hidden or not it defends its
                    // building (BattleInitiator.FindDefenderOnArrival / IsBuildingGarrison).
                    ArmyData other = BattleInitiator.FindDefenderOnArrival(candidateHex, mover);
                    if (other != null && Game.Map.StealthSystem.IsArmyFullyHidden(other)
                        && !BattleInitiator.IsBuildingGarrison(other))
                    {
                        other = null;
                        foreach (ArmyData candidate in armies)
                            if (candidate.Owner != mover.Owner
                                && BattleInitiator.IsEngageable(candidate, mover.Owner)
                                && !Game.Map.StealthSystem.IsArmyFullyHidden(candidate)
                                && (other == null || candidate.Id < other.Id))
                                other = candidate;
                    }
                    if (other == null)
                        continue;

                    hex = candidateHex;
                    participants = new List<ArmyData> { mover, other };
                    return true;
                }
            }
            hex = default;
            participants = null;
            return false;
        }

        // Both original participants must still be sitting on the delayed hex and still opposing
        // owners — anything less means this exact confrontation isn't real any more (see
        // ResolveDelayedBattlesThen's own comment). Participants[0] (the original mover/hunter,
        // see HexSelectionController.Movement.cs's own convention) additionally still needs real
        // units of its own — a hero-only army can't fight OR hunt (see BattleInitiator.
        // IsEngageable's own note) — but the OTHER side only needs IsEngageable: a target that's
        // since been ground down to hero-only is still a genuine Capture Kill Challenge target
        // (see ResolveDelayedBattlesThen's own branch), just not a Ground Combat one any more.
        private static bool IsStillAGenuineBattle(PendingBattle battle)
        {
            if (battle?.Participants == null || battle.Participants.Count < 2)
                return false;
            if (!BattleInitiator.IsCombatCapable(battle.Participants[0]) || !battle.Participants[0].Hex.Equals(battle.Hex))
                return false;
            foreach (ArmyData army in battle.Participants)
                if (army == null || !army.Hex.Equals(battle.Hex) || !BattleInitiator.IsEngageable(army))
                    return false;
            ArmyData first = battle.Participants[0];
            for (int i = 1; i < battle.Participants.Count; i++)
                if (battle.Participants[i].Owner == first.Owner)
                    return false;
            return true;
        }
    }
}
