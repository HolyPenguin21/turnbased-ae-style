using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cameras;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Styles;
using Game.Terrain;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Attack resolution and battle-end handling half of BattleScreenUI — split out purely for
    // file size, same reasoning as HexSelectionController's own multi-file split. Shares this
    // class's fields (_grid/_attacker/_defender/etc.) and the state-machine methods in the main
    // file (EndTurn, Show/Hide, ShowAiThought) automatically via `partial`.
    public partial class BattleScreenUI
    {
        // Same terrain + Base-building defense bonus BeginAttack folds into the real dice roll,
        // exposed for card/detail display so a unit's shown Defense always matches what it would
        // actually roll with right now. Only ever the original battle _defender's own hex — see
        // BeginAttack's own comment for why the attacker (or a mid-exchange role reversal) never
        // gets this.
        public int GetDisplayedDefenseBonus(UnitData unit)
        {
            return _battleEngine != null
                ? _battleEngine.GetDefenseBreakdown(unit, map).TotalBonus
                : 0;
        }

        private void BeginAttack(UnitData attacker, UnitData defender)
        {
            if (attackPopup == null || attacker == null || defender == null)
                return;

            // Identity (whose hero, whose Fate, whose movement gets zeroed) must come from actual
            // army membership, NOT from which grid row the attacker currently stands in — a unit
            // can advance into the opposing side's own rows to reach melee range, at which point
            // "row group" and "owning army" disagree. See OwningArmy's own comment and
            // project_battle_ai_bugs_open memory.
            ArmyData attackerArmy = OwningArmy(attacker);
            ArmyData defenderArmy = OwningArmy(defender);
            UnitData attackerHero = OwningHero(attackerArmy);
            UnitData defenderHero = OwningHero(defenderArmy);
            bool defenderIsRetreating = _retreatingArmy != null && defenderArmy == _retreatingArmy;

            BattleDefenseBreakdown defense = _battleEngine != null
                ? _battleEngine.GetDefenseBreakdown(defender, map)
                : default;

            // Strategic movement commitment is a battle-state mutation, not UI work.
            _battleEngine?.CommitGroundAttack(attacker);

            // Heroes on the tactical grid are ordinary attack targets. Their base defense pool
            // is FateMax (resolved by BattleAttackPopupUI/BattleCombatOdds); Capture/Kill remains
            // a separate post-combat/hero-only encounter mechanic.
            attackPopup.Begin(attacker, attackerHero, defender, defenderHero,
                ResolveCatalog(attacker.Owner)?.logo, ResolveCatalog(defender.Owner)?.logo,
                roll => OnAttackResolved(attacker, defender, attackerHero, defenderHero, roll),
                ShowAiThought, defenderIsRetreating,
                defense.TerrainBonus, defense.ConstructionBonus,
                defenderFormationBonus: defense.FormationBonus);
        }

        private void OnAttackResolved(UnitData attacker, UnitData defender,
            UnitData attackerHero, UnitData defenderHero, BattleChallengeRollResult roll)
        {
            if (_battleEngine == null)
                return;
            BattleAttackApplication application = _battleEngine.ResolveAndApplyGroundAttack(
                attacker, defender, roll, attackerHero, defenderHero);

            foreach (BattleUnitRemoval removal in application.Removals)
                ShowUnitRemovalThought(removal);

            // A surviving AI-controlled target reacts after the engine has applied the hit, so
            // severity reads the authoritative post-hit HP rather than the popup's projection.
            if (!application.DefenderDied && application.Damage > 0
                && defender.Owner != null && !defender.Owner.IsHuman)
            {
                bool major = defender.HitPointsMax > 0
                    && defender.HitPointsCurrent <= defender.HitPointsMax / 3f;
                UnitData sideHero = OwningHero(OwningArmy(defender));
                aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                    major ? AiThoughtCategory.DamageTakenMajor : AiThoughtCategory.DamageTakenMinor,
                    attacker?.Name, sideHero != null));
            }

            var secondary = new List<BattleSecondaryHit>();
            foreach (BattleSecondaryHit hit in application.SecondaryHits)
            {
                BattleDebugLog.Write($"[SplashDiag] {hit.Skill}: {attacker?.Name} -> {hit.Victim?.Name} " +
                    $"dealt={hit.Damage} hpAfter={hit.Victim?.HitPointsCurrent}/{hit.Victim?.HitPointsMax} died={hit.Died}");
                secondary.Add(hit);
            }

            RefreshGrid();

            if (secondary.Count > 0)
            {
                ShowSecondaryResultsThen(attacker, secondary, () =>
                {
                    RefreshGrid();
                    if (!CheckBattleEnd())
                        EndTurn();
                });
                return;
            }

            if (!CheckBattleEnd())
                EndTurn();
        }

        private void ShowUnitRemovalThought(BattleUnitRemoval removal)
        {
            UnitData deadSideHero = removal.DeadSide?.Commander;
            UnitData killerSideHero = removal.KillerSide?.Commander;
            if (removal.DeadSide?.Owner != null && !removal.DeadSide.Owner.IsHuman)
                aiThoughts?.Show(deadSideHero, BattleAiPhraseBank.GetRandomPhrase(
                    AiThoughtCategory.UnitDied, removal.Unit?.Name, deadSideHero != null));
            if (removal.AnnounceKiller && removal.KillerSide?.Owner != null
                && !removal.KillerSide.Owner.IsHuman)
                aiThoughts?.Show(killerSideHero, BattleAiPhraseBank.GetRandomPhrase(
                    AiThoughtCategory.EnemyKilled, removal.Unit?.Name, killerSideHero != null));
        }

        // Re-opens the attack popup's full Result screen once per Splash/Scorcher side-hit, in
        // order, each advancing to the next on Ok (or auto-closing in an all-AI fight — see
        // BattleAttackPopupUI.ShowSecondaryAttackResult). onDone runs after the last one.
        private void ShowSecondaryResultsThen(UnitData attacker, List<BattleSecondaryHit> hits, Action onDone)
        {
            int index = 0;
            void ShowNext()
            {
                if (attackPopup == null || index >= hits.Count)
                {
                    onDone();
                    return;
                }
                BattleSecondaryHit hit = hits[index++];
                string hitLine = hit.Damage > 0 ? "Hit!" : "Absorbed";
                string outcomeLine = hit.Died ? "\nThe target was destroyed." : string.Empty;
                string summary = $"Attacker ID: {attacker?.Name}\nTarget ID: {hit.Victim.Name}\n" +
                    $"Skill: {hit.Skill}\nHit Assessment: {hitLine}\nDamage Assessment: {hit.Damage} Damage{outcomeLine}";
                attackPopup.ShowSecondaryAttackResult(attacker, hit.Victim, summary, hit.Died, ShowNext);
            }
            ShowNext();
        }

        // ---- UnitAbilities.RaiseTheRots — battle-only summoned units ----

        // Called from BattleScreenUI.Show for each side, before arrangement. Every non-summoned
        // member carrying UnitAbilities.RaiseTheRots conjures UnitAbilityCatalog.
        // raiseTheRotsUnitsPerSummoner copies of the configured card into that side's own free
        // grid cells; it simply stops early once the side of the grid is full ("если на поле есть
        // место"). The units are added to the real ArmyData for the battle and flagged
        // UnitData.IsSummoned so StripSummonedUnits can pull them back out afterward.
        private void SpawnRaiseTheRotsFor(ArmyData army, int frontRow, int backRow)
        {
            if (army == null || army.Owner == null || _battleEngine == null
                || hexSelectionController == null || attackPopup == null)
                return;

            CardDefinition template = attackPopup.RaiseTheRotsCard;
            if (template == null)
            {
                if (army.Members.Exists(m => !m.IsSummoned && m.HasAbility(UnitAbilities.RaiseTheRots)))
                    BattleDebugLog.Write("[RaiseTheRots] a carrier is present but UnitAbilityCatalog resolved no summon card — skipped");
                return;
            }

            int perSummoner = Mathf.Max(0, attackPopup.RaiseTheRotsUnitsPerSummoner);
            BattleSummonApplication result = _battleEngine.SpawnBattleSummons(
                army, frontRow, backRow, template, perSummoner,
                (card, owner) => hexSelectionController.SpawnUnit(
                    card.displayName, owner, card.moveMax, card.activationApCost, false,
                    card.commandRating, card.art, card.grantedAbilities, card.attack, card.range,
                    card.hitPoints, card.initiative, card.fate, card.defenseRating,
                    card.resistanceRating, card.unitTypeTags, card.detailArt,
                    card.apCost, card.resourceCost));

            if (result.Summoners > 0)
                BattleDebugLog.Write($"[RaiseTheRots] {army.Name}: {result.Summoners} summoner(s) x{perSummoner} " +
                    $"-> {result.Spawned}/{result.Requested} \"{template.displayName}\" conjured");
        }

        // Manual's "Battle Results": ends the instant either side has no more combat-capable
        // units (BattleInitiator.IsCombatCapable's own "at least one non-hero unit" rule) — true
        // if the battle just ended (caller should NOT also EndTurn in that case).
        private bool CheckBattleEnd()
        {
            if (_battleEngine == null)
                return false;

            BattlePostActionResolution resolution = _battleEngine.EvaluatePostAction();
            if (!resolution.Ended)
                return false;

            BattleEndStatus status = resolution.EndStatus;
            BattleCaptureKillSequence sequence = resolution.CaptureKillSequence;
            if (sequence != null && sequence.HasPending && attackPopup != null)
                RunCaptureKillSequence(sequence,
                    () => FinishBattleEnd(status.AttackerAlive, status.DefenderAlive));
            else
                FinishBattleEnd(status.AttackerAlive, status.DefenderAlive);
            return true;
        }

        // A hero-only army (see BattleInitiator.IsEngageable vs IsCombatCapable) is a poor fit
        // for the full Tactical Battle Module — heroes never act in a Ground Combat round (see
        // BattleTurnOrder's own "heroes never act" rule) and can't be attacked as a regular grid
        // target either, so there's nothing for a normal battle to actually DO against one.
        // Contact with one (see HexSelectionController.Movement.cs / GameTurnController's own
        // delayed-battle branch) comes straight here instead — no grid, no Arrangement/Round-
        // start, this popup (attackPopup) IS the entire encounter. `hunterArmy` needing its own
        // non-hero units is the caller's responsibility to have already checked (same rule
        // CheckBattleEnd's own trigger enforces) — this doesn't re-check it.
        public void BeginCaptureKillEncounter(ArmyData hunterArmy, ArmyData targetArmy, Action onClosed)
        {
            if (attackPopup == null || hunterArmy == null || targetArmy == null)
            {
                onClosed?.Invoke();
                return;
            }

            // Deliberately does NOT activate panelRoot — no grid/Arrangement/turn-order chrome
            // makes sense for a hero-only encounter, and per the user's own spec this needs to
            // stay a light popup-only interaction, reusable for every future Challenge type
            // (Retreat/Assassination/Sabotage/Sniper/...), not each one opening the whole battle
            // screen. attackPopup itself is the only UI this ever shows. IsShowing already covers
            // attackPopup on its own (see its own comment) so GameTurnController.InputBlocked
            // still works without panelRoot's involvement — just needs telling that it changed.
            //
            // cardHand deliberately stays VISIBLE (unlike Show's own cardHand?.Hide() — the map
            // isn't covered by a full battle screen here, just this one popup), per the user's
            // own report — only dragging needs to be blocked, and CardDraggingBlocked already
            // does that on its own once VisibilityChanged fires below (see GameTurnController.
            // RecomputeBlockedState's own battleScreen.IsShowing term).
            _onClosed = onClosed;
            hexSelectionController?.Deselect();
            rtsCamera?.SetPanningEnabled(false);

            _localArmy = null;
            if (hunterArmy.Owner != null && hunterArmy.Owner.IsHuman)
                _localArmy = hunterArmy;
            else if (targetArmy.Owner != null && targetArmy.Owner.IsHuman)
                _localArmy = targetArmy;

            // STEALTH-COMBAT-01: reveal is now the committed-encounter coordinator's job, run by
            // every caller (TryBeginBattleAt, ResolveHexAfterVictory, TryChainPendingRetreatContact,
            // ResolveDelayedBattlesThen) before it ever routes into this method — kept here only
            // as an idempotent safety net (a fully-hidden target never reaches here anyway, see
            // BattleInitiator.FindEnemyAt), same as BattleScreenUI.Show's own safety-net copy.
            bool hunterHadHidden = hunterArmy.Members.Exists(m => m.IsHidden);
            bool targetHadHidden = targetArmy.Members.Exists(m => m.IsHidden);
            if (hunterHadHidden || targetHadHidden)
                BattleDebugLog.Write("[STEALTH-COMBAT] BeginCaptureKillEncounter received hidden participant after committed encounter preparation");
            Game.Map.StealthSystem.RevealArmy(targetArmy);
            Game.Map.StealthSystem.RevealArmy(hunterArmy);

            unchecked
            {
                int seed = 17;
                seed = seed * 31 + hunterArmy.Hex.Q;
                seed = seed * 31 + hunterArmy.Hex.R;
                seed = seed * 31 + hunterArmy.Id;
                seed = seed * 31 + targetArmy.Id;
                _battleState = new BattleState(hunterArmy.Hex, hunterArmy, targetArmy,
                    new BattleGrid(), seed);
            }
            _battleEngine = new BattleEngine(_battleState,
                attackPopup != null ? attackPopup.Magnitudes : AbilityMagnitudes.Default,
                new System.Random(_battleState.BattleSeed));
            BattleCaptureKillSequence sequence = BattleEngine.CreateTargetOnlyCaptureKillSequence(
                _battleState, hunterArmy, targetArmy);

            // Reuses this exact same class's own Hide() for cleanup (restores cardHand/camera
            // panning, resets _localArmy, invokes _onClosed, and fires VisibilityChanged itself
            // unconditionally — covering the closing edge) — nothing else here needs a bespoke
            // teardown since _grid/_attacker/_defender were never touched in the first place.
            // targetArmy itself might, though: if every hero in it just got Killed/Captured, it's
            // now empty and — unlike a normal battle's _attacker/_defender (torn down by
            // OnBattleOutcomeAcknowledged) — nothing else would ever clean it up, since this
            // encounter never goes through that method at all.
            //
            // suppressAiThoughts: true — aiThoughts lives under panelRoot, which this encounter
            // deliberately never activates (see this method's own comment above), so its
            // AiЕhoughts_Text is still inactive here; routing a thought through it threw
            // "Coroutine couldn't be started because the game object ... is inactive" (see the
            // user's own report). CheckBattleEnd's own RunNextCaptureKillChallenge call runs
            // while a real battle's panelRoot IS already showing, so that one still narrates.
            RunCaptureKillSequence(sequence, () =>
            {
                // This hero-only encounter never goes through OnBattleOutcomeAcknowledged (see
                // this method's own comment — attackPopup IS the entire encounter), so unlike a
                // normal battle it never got that method's own Fate replenish either. Without
                // this, a hero who spent Fate defending here (e.g. an Escaped outcome) stayed
                // permanently short on a LATER Capture Kill attempt against the same hero —
                // FateMax stayed correct as the roll's own pool size (see BeginCaptureKill), but
                // the actual current Fate available to spend during the duel never recovered
                // (see the project owner's own report: the defending side's Fate wasn't full on
                // a second capture attempt). Both sides, same as OnBattleOutcomeAcknowledged.
                HexCoord hunterHex = hunterArmy.Hex;
                BattleEncounterFinalization finalization = BattleEngine.FinalizeStandaloneCaptureKill(
                    hunterArmy, targetArmy, hexSelectionController);

                EncounterResolved?.Invoke(hunterArmy, targetArmy);

                // Same "what's left on this hex" resolution as a normal battle, but the engine
                // has already handled Fate, roster cleanup, building transfer, vision and restack.
                ResolveHexAfterVictory(hunterHex, finalization.Survivor);
            }, suppressAiThoughts: true);
            VisibilityChanged?.Invoke(); // opening edge — attackPopup is showing as of this call
        }

        private void RunCaptureKillSequence(BattleCaptureKillSequence sequence,
            Action onAllResolved, bool suppressAiThoughts = false)
        {
            if (sequence == null || !sequence.TryGetCurrent(out BattleCaptureKillTarget next))
            {
                onAllResolved?.Invoke();
                return;
            }

            attackPopup.BeginCaptureKill(next.HunterArmy, next.Hero,
                ResolveCatalog(next.HunterArmy?.Owner)?.logo, ResolveCatalog(next.Hero?.Owner)?.logo,
                roll =>
                {
                    BattleCaptureKillStep step = sequence.ResolveCurrent(
                        roll, map, hexSelectionController);
                    RefreshGrid();

                    if (step.Retreat.ContactParticipants != null)
                        _pendingRetreatContacts.Enqueue((
                            step.Retreat.Destination, step.Retreat.ContactParticipants));

                    RunCaptureKillSequence(sequence, onAllResolved, suppressAiThoughts);
                },
                suppressAiThoughts ? null : ShowAiThought);
        }

        private void FinishBattleEnd(bool attackerAlive, bool defenderAlive)
        {
            _battleEngine?.CompleteBattle(attackerAlive, defenderAlive, hexSelectionController);

            FireBattleEndThought(_attacker, attackerAlive);
            FireBattleEndThought(_defender, defenderAlive);

            string title;
            if (_localArmy == null)
                title = attackerAlive ? "Attacker wins." : defenderAlive ? "Defender wins." : "Draw.";
            else
            {
                bool localWon = _localArmy == _attacker ? attackerAlive : defenderAlive;
                title = localWon ? "Victory!" : "Defeat!";
            }

            // Which army actually beat which, plus (on its own line) what happens once this
            // popup closes — per the user's own request for the newly added Message field.
            // `survivor` here mirrors OnBattleOutcomeAcknowledged's own attackerHere/defenderHere
            // check below: at THIS point (no retreat involved, straight combat resolution to a
            // wipeout) attacker/defender are still exactly where they started, so attackerAlive/
            // defenderAlive already answers the same question that check re-derives from Hex.
            string detail = attackerAlive != defenderAlive
                ? $"{(attackerAlive ? _attacker : _defender)?.Name} defeated {(attackerAlive ? _defender : _attacker)?.Name}."
                : $"{_attacker?.Name} and {_defender?.Name} destroy each other.";
            ArmyData survivor = attackerAlive != defenderAlive ? (attackerAlive ? _attacker : _defender) : null;
            string message = $"{detail}\n{DescribeNextAction(survivor)}";

            // No human involved in this fight (AI vs. neutrals/event guards/another AI) — nobody's
            // there to click Ok, so the popup closes itself after a beat instead of stalling the
            // AI's turn (see BattleOutcomePopupUI.Show's own comment).
            if (outcomePopup != null)
                outcomePopup.Show(title, message, OnBattleOutcomeAcknowledged, autoCloseNoHuman: _localArmy == null);
            else
                OnBattleOutcomeAcknowledged();
        }

        // Whether the battle screen is about to chain straight into another fight on this same
        // hex, or return to the map — same question OnBattleOutcomeAcknowledged (below) answers
        // for real right after this popup closes, computed early here just to describe it in the
        // BattleOutcome popup's own Message field (see FinishBattleEnd/ResolveRetreat, its only
        // two callers). `survivor` is null for a mutual wipeout — nothing to chain into either way.
        private string DescribeNextAction(ArmyData survivor)
        {
            bool hasNext = BattleEncounterCoordinator.HasContinuation(_battleHex, survivor);
            return hasNext ? "Proceeding to the next battle." : "Returning to the map.";
        }

        private void FireBattleEndThought(ArmyData army, bool survived)
        {
            if (army?.Owner == null || army.Owner.IsHuman)
                return;
            UnitData sideHero = army.Commander;
            aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                survived ? AiThoughtCategory.BattleWon : AiThoughtCategory.BattleLost, hasHero: sideHero != null));
        }

        private void OnBattleOutcomeAcknowledged()
        {
            ArmyData resolvedAttacker = _attacker;
            ArmyData resolvedDefender = _defender;
            HexCoord hex = _battleHex;

            BattleEncounterFinalization finalization = BattleEngine.FinalizeEncounter(
                _battleState, hexSelectionController);

            EncounterResolved?.Invoke(resolvedAttacker, resolvedDefender);
            ResetBattlePanel();

            ResolveHexAfterVictory(hex, finalization.Survivor);
        }

        // Shared by OnBattleOutcomeAcknowledged and BeginCaptureKillEncounter's own ending
        // callback (see its own comment on why it needs this too) — once `survivor` is the only
        // side left standing on `hex`, decides what happens next.
        //
        // hexPending (DelayedBattleRegistry.IsHexPending) covers two distinct things at once, on
        // purpose: it guards against re-offering a Fight/Delay choice for an army that's already
        // reserved for a different pending battle at this same hex (e.g. queued earlier this turn
        // by a different attacker's own Delay choice — per the user's own call, a reserved army
        // can't be signed up for a second one), AND it holds the event trigger back too — per the
        // user's own call, a hex with a still-undelivered Delay isn't actually "clear" yet, even
        // though FindEnemyAt has nothing left to report right this moment. Both are left
        // unresolved here on purpose; GameTurnController's own end-of-turn sweep is what
        // eventually forces the delayed pairing, and once THAT battle also ends, this same method
        // runs again and finds the hex genuinely clear.
        //
        // Hex Events, "collision hex" case: an unrelated pre-existing neutral army (or, same code
        // path since this method is shared, an event's own guard, spawned only once Explore was
        // actually chosen — see HexEventRegistry.Entry.ResolvedGuardMembers) shared this event's
        // hex (see CitadelSetupController.MapContent.GenerateRandomEvents), so the ordinary
        // contact flow forced combat here before the event's own choice popup ever got a chance
        // to show (see HexSelectionController.Movement.cs's own HasUnclaimedCleanEventHex, which
        // deliberately never claims a hex like this). Only reached once nextEnemy == null and
        // nothing is still pending — nothing hostile left standing here at all, regardless of
        // whether that took one fight or a whole chain of them — matching the user's own
        // "triggers only after full battle resolution" rule. survivor.Owner.IsNeutral is excluded
        // so a neutral-vs-neutral mutual fight (if that's ever reachable) never triggers anything.
        // TriggerHexEventIfClear (HexSelectionController.Events.cs) is what actually tells "hex
        // just went clear of an unrelated army" apart from "the event's own guard just lost" —
        // see its own comment.
        private void ResolveHexAfterVictory(HexCoord hex, ArmyData survivor)
        {
            BattleEncounterContinuation continuation =
                BattleEncounterCoordinator.ResolveContinuation(hex, survivor);
            BattleEncounterContext encounter = continuation.NextEncounter;

            if (encounter != null)
            {
                ArmyData nextEnemy = encounter.Target;
                BattleDebugLog.Write($"[HeroChallengeDiag] ResolveHexAfterVictory at ({hex.Q},{hex.R}): survivor={survivor?.Name} " +
                    $"({survivor?.Owner?.Nickname}) -> nextEnemy={nextEnemy?.Name} ({nextEnemy?.Owner?.Nickname}, " +
                    $"members={nextEnemy?.Members.Count ?? 0}, heroes={nextEnemy?.Members.Count(m => m.IsHero) ?? 0}, " +
                    $"IsCombatCapable={BattleInitiator.IsCombatCapable(nextEnemy)})");

                var participants = encounter.Participants.ToList();
                if (survivor.Owner != null && survivor.Owner.IsHuman && battleContactPopup != null)
                {
                    battleContactPopup.Show(hex, participants, encounter.PresentationObserver,
                        onFight: () =>
                        {
                            if (encounter.TargetHeroOnly)
                                BeginCaptureKillEncounter(survivor, nextEnemy, _onClosed);
                            else
                                Show(hex, participants, _onClosed);
                        },
                        onDelay: () =>
                        {
                            DelayedBattleRegistry.Add(new PendingBattle { Hex = hex, Participants = participants });
                            if (!TryChainPendingRetreatContact())
                                Hide();
                        });
                }
                else if (encounter.TargetHeroOnly)
                {
                    BeginCaptureKillEncounter(survivor, nextEnemy, _onClosed);
                }
                else
                {
                    Show(hex, participants, _onClosed);
                }
                return;
            }

            bool eventOpenedBattle = false;
            if (continuation.CanTriggerEvent)
                eventOpenedBattle = hexSelectionController?.TriggerHexEventIfClear(hex, survivor) ?? false;

            if (eventOpenedBattle)
                return;

            if (!TryChainPendingRetreatContact())
                Hide();
        }
    }
}
