using System;
using System.Collections;
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Ground Combat roll and Fate duel: Roll Token, then alternating defender/attacker turns
    // (human or AI) until both sides decline back-to-back (see the class comment in BattleAttackPopupUI.cs).
    public partial class BattleAttackPopupUI
    {
        private void OnRollClicked()
        {
            if (_phase != Phase.NotRolled)
                return;

            BattleChallengeMode mode = _kind == ChallengeKind.CaptureKill
                ? BattleChallengeMode.CaptureKill
                : BattleChallengeMode.GroundCombat;
            _challengeSession = new BattleChallengeSession(
                mode, _attacker, _defender,
                _attackerTokenPoolSize, _defenderTokenPoolSize,
                _attackerFateRemaining, _defenderFateRemaining,
                Magnitudes, _defenderIsRetreating, tacticsCritical: _tacticsCritical);

            _phase = Phase.InProgress;
            if (rollButton != null)
                rollButton.interactable = false;
            StartCoroutine(RunRollAndDuel());
        }

        // Rolls, reveals both rows, then runs the Fate duel (see RunDuel) and resolves once it's
        // actually over — waits for both rows' own flip animation to actually land before opening
        // the duel (per the user's own request, 2026-08-24: Spend/Accept shouldn't become
        // available, and an AI shouldn't react, before the tokens are visibly done spinning).
        private IEnumerator RunRollAndDuel()
        {
            if (_challengeSession == null)
                yield break;

            _challengeSession.Roll();
            SyncChallengePresentationState();

            var result = new ChallengeResult(_attackerTokens, _defenderTokens);

            BattleDebugLog.Write($"[RollDiag] {_kind}: {_attacker?.Name} ({_attacker?.Owner?.Nickname}) " +
                $"vs {_defender?.Name} ({_defender?.Owner?.Nickname}) -> " +
                $"attacker={BattleDebugLog.TokenString(_attackerTokens)} ({result.AttackerSuccesses} hits), " +
                $"defender={BattleDebugLog.TokenString(_defenderTokens)} ({result.DefenderSuccesses} hits), rawDamage={result.Damage}");

            // Wait for the initial roll's own flip animation to actually land before anything
            // downstream happens — the success counts, the Fate duel, and Spend/Accept becoming
            // interactable all used to fire the instant the tokens were rolled, which let a human
            // click Spend (or an AI decide) while the strip was still visibly mid-flip (per the
            // user's own request, 2026-08-24). Every later Fate-spend reroll inside the duel gets
            // the same treatment via RunHumanTurn/RunAiTurn's own wait on _rerollAnimDone.
            bool attackerAnimDone = attackerRow == null;
            bool defenderAnimDone = defenderRow == null;
            attackerRow?.SetTokens(_attackerTokens, onComplete: () => attackerAnimDone = true);
            defenderRow?.SetTokens(_defenderTokens, onComplete: () => defenderAnimDone = true);
            // Nobody human is watching this roll land (see RunsAutomatically) — don't gate the duel
            // on the flip animation actually finishing, per the user's own request (2026-08-24) to
            // stop pacing an AI-vs-AI/AI-vs-neutral fight for a spectator's benefit. SetTokens already
            // ran above so _attackerTokens/_defenderTokens are the real values either way.
            if (!RunsAutomatically)
                yield return new WaitUntil(() => attackerAnimDone && defenderAnimDone);

            PrepareTacticPresentation(HumanParticipant());
            RefreshTacticTotals();
            FireRollThought();
            yield return RunDuel();
            Resolve();
        }

        // First-look reaction to the fresh roll, before any Fate is spent — fires for whichever
        // side(s) are AI-controlled, independent of whether that side even has a hero (a hero-less
        // side still "feels" about its own roll, it just can't act on it with Fate).
        private void FireRollThought()
        {
            if (_challengeSession == null || _onAiThought == null)
                return;
            // Equal successes kill the hunted hero, so a tie favors the hunter in Capture/Kill.
            // These are provisional tokens reactions; Fate and ability modifiers can change the result.
            bool attackerFavored = _challengeSession.Mode == BattleChallengeMode.CaptureKill
                ? _challengeSession.ResolveCaptureKillOutcome() != CaptureKillOutcome.Escaped
                : _challengeSession.RawDamage > 0;
            if (IsAiSide(_attacker))
                _onAiThought.Invoke(_attacker, attackerFavored ? AiThoughtCategory.GoodRoll : AiThoughtCategory.BadRoll, _defender?.Name);
            if (IsAiSide(_defender))
                _onAiThought.Invoke(_defender, attackerFavored ? AiThoughtCategory.BadRoll : AiThoughtCategory.GoodRoll, _attacker?.Name);
        }

        private bool IsAiSide(UnitData unit) => unit?.Owner != null && (_automateHumanSides || !unit.Owner.IsHuman);

        // Whether a Spend button should ever be interactable for `hero` — needs both a hero AND
        // Fate to spend from a human's own hand (an AI side spends automatically instead, see
        // RunAiTurn). NOT the same question as "should this turn pause for Accept" — a human side
        // with no hero (e.g. a CaptureKill hunter army with no hero of its own — see
        // BeginCaptureKill's own note) still has nothing to SPEND, but still clicked Roll Token and
        // still needs to see the result and click Accept themselves.
        private bool CanSpend(UnitData hero) => !_automateHumanSides && IsHumanSide(hero);

        // The Fate duel itself. Defender's Prerogative: the defender always goes first. Declining
        // (Accept, or an AI/no-Fate side auto-declining — see RunHumanTurn/RunAiTurn) hands the
        // turn to the OTHER side rather than ending the duel — that side still gets its own look
        // at the roll and a chance to spend, since the tokens it would be reacting to just changed.
        // Tracked per side (defenderDone/attackerDone) rather than a single "two declines in a
        // row" counter: a side is done the instant ITS OWN turn ends (RunHumanTurn/RunAiTurn
        // always end in a decline, whether chosen or forced by having nothing left to spend), and
        // is only reopened when the OTHER side's turn actually spent Fate — declining changes
        // nothing, so it must never reopen anyone. The duel ends once both sides are done. Per
        // the user's own report: with a plain "two declines in a row" counter, a defender with no
        // hero (permanently nothing to decide, e.g. an army without a hero) still counted as a
        // fresh decline every time it was asked, so its second forced auto-decline — after the
        // attacker had already spent, reconsidered, and explicitly declined — wrongly earned the
        // attacker one more redundant round against tokens that hadn't changed at all.
        private IEnumerator RunDuel()
        {
            if (_challengeSession == null || !_challengeSession.HasAnyDecision)
            {
                // Neither side has any Fate left to spend, so there's no Spend-or-Accept turn for
                // anyone to take — but resolving with zero pause right after the tokens land reads as
                // the roll being skipped entirely. Same beat as RunHumanTurn/RunAiTurn's own
                // auto-decline (aiAcceptDelay) so this case doesn't feel instant/broken — except
                // when nobody human is in this fight at all (2026-08-24), where instant IS the goal.
                if (!RunsAutomatically && aiAcceptDelay > 0f)
                    yield return new WaitForSeconds(aiAcceptDelay);
                yield break;
            }

            while (_challengeSession.TryNextFateTurn(out bool isDefenderTurn))
            {
                yield return RunTurn(isDefenderTurn);
                _challengeSession.ReportFateTurn(_turnSpent);
            }
        }

        // One side's whole decision window, which can include SEVERAL Spends before it concludes
        // (see the user's own example: defender Spend → Accept; attacker Spend, Spend-again,
        // *then* effectively done) — a single Spend does NOT hand the turn to the other side by
        // itself anymore; only Accept (human) or the AI running out of reasons to keep going does.
        // AI evaluates and acts on its own (see RunAiTurn); a human side with nothing to Spend on
        // auto-declines instead of forcing an empty click (see RunHumanTurn's own canSpend gate).
        private IEnumerator RunTurn(bool isDefenderTurn)
        {
            UnitData actingUnit = isDefenderTurn ? _defender : _attacker;
            if (IsAiSide(actingUnit))
            {
                yield return RunAiTurn(isDefenderTurn);
                yield break;
            }
            yield return RunHumanTurn(isDefenderTurn);
        }

        // Loops Spend-or-Accept for as long as this side keeps spending — each successful Spend
        // re-offers the SAME choice (Spend again / Accept) rather than ending the turn, per the
        // user's own report: Accept used to silently mean "give up the whole challenge" the moment
        // ANY Spend had happened, when it should just mean "I'm done deciding for THIS turn."
        // _turnSpent (read by RunDuel once this returns) is true iff at least one Spend landed
        // ANYWHERE in this turn, not just the last loop iteration.
        private IEnumerator RunHumanTurn(bool isDefenderTurn)
        {
            _tacticCommittedThisTurn = false;
            bool spentThisTurn = false;
            while (true)
            {
                UnitData hero = isDefenderTurn ? _defenderHero : _attackerHero;
                bool[] ownTokens = isDefenderTurn ? _defenderTokens : _attackerTokens;
                int fateRemaining = isDefenderTurn ? _defenderFateRemaining : _attackerFateRemaining;
                bool canSpend = CanSpend(hero) && _challengeSession != null
                    && _challengeSession.CanSpend(isDefenderTurn);

                // Nothing this side could possibly do this turn (no hero, no Fate, or no miss
                // left to reroll) — auto-decline after a short beat instead of forcing a click on
                // a decision that isn't actually one (mirrors RunAiTurn's own aiAcceptDelay pause).
                if (!canSpend && !(_challengeSession?.HasTactic(isDefenderTurn) == true) && _pendingTactic == null)
                {
                    if (aiAcceptDelay > 0f)
                        yield return new WaitForSeconds(aiAcceptDelay);
                    break;
                }

                _defenderTurnActive = isDefenderTurn;
                _humanSpent = false;
                _humanDeclined = false;
                _awaitingHumanDecision = true;
                if (isDefenderTurn)
                    defenderRow?.SetSpendInteractable(canSpend);
                else
                    attackerRow?.SetSpendInteractable(canSpend);
                if (acceptButton != null)
                    acceptButton.interactable = true;

                OpenTacticDecision(isDefenderTurn);

                OfferFateTutorial(isDefenderTurn ? defenderRow : attackerRow, isDefenderTurn, canSpend);

                // Diagnostics for the project owner's own report (Ground Combat, 2026-08-26):
                // the duel sometimes ends before the defender gets to react to/make a reroll.
                // Nothing in RunDuel/RunHumanTurn's own structure looked wrong on inspection —
                // this side always opens Spend/Accept first per Defender's Prerogative — so this
                // logs the exact moment the window opens (and OnAcceptClicked logs the moment it
                // closes) rather than guessing at a fix blind; compare timestamps/sequence next
                // time this reproduces.
                BattleDebugLog.Write($"[FateDuelDiag] Spend/Accept OPEN for {(isDefenderTurn ? "defender" : "attacker")} " +
                    $"{hero?.Name} (fate={fateRemaining}, tokens={BattleDebugLog.TokenString(ownTokens)})");

                yield return new WaitUntil(() => _humanSpent || _humanDeclined);

                _awaitingHumanDecision = false;
                CloseTacticDecision();
                defenderRow?.SetSpendInteractable(false);
                attackerRow?.SetSpendInteractable(false);
                if (acceptButton != null)
                    acceptButton.interactable = false;

                if (_humanDeclined)
                    break;

                // Wait for the reroll's own flip animation to land before offering Spend-or-
                // Accept again — otherwise the next Spend became available while the die just
                // spent on was still visibly mid-flip (see _rerollAnimDone's own comment).
                yield return new WaitUntil(() => _rerollAnimDone);
                RefreshTacticTotals();

                spentThisTurn = true;
                // A human may keep spending (even all their Fate in one turn) after any reroll,
                // hit or miss; the turn passes to the other side only on Accept, or automatically
                // once nothing is left to spend (no Fate or no missed die — the canSpend gate above).
            }
            _turnSpent = spentThisTurn || _tacticCommittedThisTurn;
        }

        // AI path for a whole duel turn — mirrors the human Spend/Accept loop exactly (may spend
        // more than once), just decided via FateDuelAi.ShouldSpendFate instead of a click. Per the
        // user's own correction: the AI isn't "obligated" to keep rerolling — it evaluates the
        // CURRENT tokens fresh before EVERY reroll (same as a human deciding again after each one).
        // A defender keeps trying an unfavorable result even after a failed reroll; an attacker
        // retains its stop-on-failed-reroll policy.
        // getting a turn at all only happens because the other side just spent, per RunDuel.
        private IEnumerator RunAiTurn(bool isDefenderTurn)
        {
            UnitData hero = isDefenderTurn ? _defenderHero : _attackerHero;
            bool couldSpend = hero != null && _challengeSession != null
                && _challengeSession.CanSpend(isDefenderTurn);
            bool spentThisTurn = false;
            UnitData acting = isDefenderTurn ? _defender : _attacker;
            UnitData opposing = isDefenderTurn ? _attacker : _defender;

            while (true)
            {
                bool[] ownTokens = isDefenderTurn ? _defenderTokens : _attackerTokens;
                int fateRemaining = isDefenderTurn ? _defenderFateRemaining : _attackerFateRemaining;
                bool isRetreating = isDefenderTurn && _defenderIsRetreating;
                int defendingUnitHp = _defender != null ? _defender.HitPointsCurrent : int.MaxValue;
                bool shouldSpend = hero != null && _challengeSession != null
                    && _challengeSession.ShouldAiSpend(isDefenderTurn);
                BattleDebugLog.Write($"[FateDuelDiag] {(isDefenderTurn ? "defender" : "attacker")} " +
                    $"{(isDefenderTurn ? _defender?.Name : _attacker?.Name)} (hero {hero?.Name}, fate={fateRemaining}, " +
                    $"isRetreating={isRetreating}, defendingUnitHp={defendingUnitHp}): " +
                    $"attacker={BattleDebugLog.TokenString(_attackerTokens)} defender={BattleDebugLog.TokenString(_defenderTokens)} " +
                    $"-> shouldSpend={shouldSpend}");
                if (!shouldSpend)
                {
                    // Skipped entirely for a battle with no human in it at all (2026-08-24) — same
                    // RunsAutomatically carve-out as RunDuel's own no-Fate-to-spend beat above.
                    if (!RunsAutomatically && aiAcceptDelay > 0f)
                        yield return new WaitForSeconds(aiAcceptDelay);
                    break;
                }

                if (_challengeSession == null
                    || !_challengeSession.TrySpend(isDefenderTurn, out int rerolledIndex, out bool rerolledHit))
                    break;
                SyncChallengePresentationState();
                int remainingAfterSpend = isDefenderTurn ? _defenderFateRemaining : _attackerFateRemaining;
                // Announce spending when it actually happens, once per turn, before its animation.
                if (!spentThisTurn)
                    _onAiThought?.Invoke(acting, AiThoughtCategory.FateSpendWorthIt, opposing?.Name);
                spentThisTurn = true;
                BattleDebugLog.Write($"[FateDuelDiag] {(isDefenderTurn ? "defender" : "attacker")} {hero.Name} spent Fate " +
                    $"(remaining={remainingAfterSpend}), rerolled slot {rerolledIndex} -> " +
                    $"{(isDefenderTurn ? _defenderTokens : _attackerTokens)[rerolledIndex]}");

                _rerollAnimDone = isDefenderTurn ? defenderRow == null : attackerRow == null;
                if (isDefenderTurn)
                {
                    defenderRow?.SetTokens(_defenderTokens, rerolledIndex, () => _rerollAnimDone = true);
                    defenderRow?.SetFateDisplay(_defenderFateRemaining);
                }
                else
                {
                    attackerRow?.SetTokens(_attackerTokens, rerolledIndex, () => _rerollAnimDone = true);
                    attackerRow?.SetFateDisplay(_attackerFateRemaining);
                }
                // Wait for this reroll's own flip animation to land before deciding whether to
                // keep going — matches RunHumanTurn's own reroll gate (see _rerollAnimDone's own
                // comment): the AI shouldn't react to a die that isn't visibly done spinning yet.
                // Skipped when nobody human is in this fight (2026-08-24) — same reasoning as
                // RunRollAndDuel's own initial-roll anim gate.
                if (!RunsAutomatically)
                    yield return new WaitUntil(() => _rerollAnimDone);

                RefreshTacticTotals();

                // Failed defensive rerolls still leave a decision while Fate remains.
                if (!isDefenderTurn && !rerolledHit)
                    break;
                // Loop back — re-evaluate from scratch against the now-updated tokens before
                // deciding whether another reroll is still worth it.
            }

            var tactic = _challengeSession?.ChooseAiTactic(isDefenderTurn, _interceptor);
            if (tactic != null && _challengeSession.TryPlayTactic(isDefenderTurn, tactic))
            {
                spentThisTurn = true;
                ShowTacticResult(tactic);
            }

            if (couldSpend && !spentThisTurn)
                _onAiThought?.Invoke(acting, AiThoughtCategory.FateSpendSkip, opposing?.Name);
            _turnSpent = spentThisTurn;
        }

        private void OfferFateTutorial(BattleCombatantRowUI row, bool defender, bool canSpend)
        {
            if (!canSpend || row?.TutorialSpendButton == null) return;
            TutorialHintController.Offer(TutorialHintId.Fate, row.TutorialSpendButton.transform as RectTransform,
                () => this != null && IsShowing && _awaitingHumanDecision && !_humanSpent && !_humanDeclined
                    && _defenderTurnActive == defender && !TacticInputBlocked && !RunsAutomatically && !TacticDragInProgress
                    && (_kind == ChallengeKind.ResearchProduction
                        ? _rpHero?.Owner?.IsHuman == true && CanSpendResearchProductionFate()
                        : (defender ? _defender : _attacker)?.Owner?.IsHuman == true
                            && _challengeSession?.CanSpend(defender) == true)
                    && row != null && row.TutorialSpendButton != null && row.TutorialSpendButton.IsInteractable());
        }

        private void OnAcceptClicked()
        {
            if (!_awaitingHumanDecision || _humanSpent || _humanDeclined || TacticInputBlocked)
                return;
            if (!CommitPendingTactic()) return;
            // See RunHumanTurn's own OPEN log — the matching close, for the same diagnostics.
            BattleDebugLog.Write($"[FateDuelDiag] Accept clicked for {(_defenderTurnActive ? "defender" : "attacker")} " +
                $"{(_defenderTurnActive ? _defenderHero?.Name : _attackerHero?.Name)}");
            _humanDeclined = true;
            defenderRow?.SetSpendInteractable(false);
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
        }

        private void Resolve()
        {
            _phase = Phase.Resolved;
            CloseTacticDecision();
            if (_kind == ChallengeKind.CaptureKill)
                ResolveCaptureKill();
            else
                ResolveDamage();
        }

        private void OnDefenderSpend()
        {
            if (!_awaitingHumanDecision || _humanSpent || _humanDeclined || !_defenderTurnActive || _defenderHero == null
                || _challengeSession == null || TacticInputBlocked)
                return;
            if (!_challengeSession.TrySpend(true, out int rerolledIndex, out _))
                return;
            TutorialHintController.Complete(TutorialHintId.Fate);
            SyncChallengePresentationState();
            defenderRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
            _rerollAnimDone = defenderRow == null;
            defenderRow?.SetTokens(_defenderTokens, rerolledIndex, () => _rerollAnimDone = true);
            defenderRow?.SetFateDisplay(_defenderFateRemaining);
            _humanSpent = true;
            BattleDebugLog.Write($"[FateDuelDiag] defender {_defenderHero.Name} (human) spent Fate " +
                $"(remaining={_defenderFateRemaining}), rerolled slot {rerolledIndex} -> {_defenderTokens[rerolledIndex]}");
        }

        private void OnAttackerSpend()
        {
            if (!_awaitingHumanDecision || _humanSpent || _humanDeclined) return;
            if (TacticInputBlocked) return;
            if (_kind == ChallengeKind.ResearchProduction)
            {
                OnResearchProductionSpend();
                return;
            }
            if (!_awaitingHumanDecision || _defenderTurnActive || _attackerHero == null
                || _challengeSession == null)
                return;
            if (!_challengeSession.TrySpend(false, out int rerolledIndex, out _))
                return;
            TutorialHintController.Complete(TutorialHintId.Fate);
            SyncChallengePresentationState();
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
            _rerollAnimDone = attackerRow == null;
            attackerRow?.SetTokens(_attackerTokens, rerolledIndex, () => _rerollAnimDone = true);
            attackerRow?.SetFateDisplay(_attackerFateRemaining);
            _humanSpent = true;
            BattleDebugLog.Write($"[FateDuelDiag] attacker {_attackerHero.Name} (human) spent Fate " +
                $"(remaining={_attackerFateRemaining}), rerolled slot {rerolledIndex} -> {_attackerTokens[rerolledIndex]}");
        }

        private void SyncChallengePresentationState()
        {
            if (_challengeSession == null)
                return;
            _attackerTokens = _challengeSession.AttackerTokens;
            _defenderTokens = _challengeSession.DefenderTokens;
            _attackerFateRemaining = _challengeSession.AttackerFateRemaining;
            _defenderFateRemaining = _challengeSession.DefenderFateRemaining;
        }

        // Rerolls the first still-missed die found (no per-die selection UI — matches the
        // reference's single Spend button, not a click-a-die-to-target interaction).
        // rerolledIndex (out): which slot actually got rerolled, so the caller can force THAT
        // slot's flip animation regardless of whether the new value happens to match the old one
        // (see BattleCombatantRowUI.SetTokens's own comment — a plain before/after value diff
        // can't tell "unchanged" apart from "rerolled back to the same result").
        private static bool RerollOneMiss(ref bool[] tokens, out int rerolledIndex)
        {
            rerolledIndex = -1;
            if (tokens == null)
                return false;
            for (int i = 0; i < tokens.Length; i++)
                if (!tokens[i])
                {
                    tokens[i] = ChallengeResolver.RollTokens(1)[0];
                    rerolledIndex = i;
                    return true;
                }
            return false;
        }
    }
}
