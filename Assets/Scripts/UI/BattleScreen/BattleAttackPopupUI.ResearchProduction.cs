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
    // Research / Production challenge: one hero's roll against the card's required successes,
    // Fate rerolls, result screen and attempt completion.
    public partial class BattleAttackPopupUI
    {
        // ============================ Research / Production Challenge ============================
        // A new, separate entry point (per the spec): it does NOT go through Begin(), never runs
        // RunRollAndDuel/RunDuel/Resolve, and never touches Ground Combat / Capture Kill / Aviation
        // rules. Attacker = the producing Hero, rolling its full Fate stat (see ChallengeResolver.
        // RollTokens) with unchanged per-die success probability. Defender = the chosen card: a
        // fixed CardDefinition.fate guaranteed successes, no tokens, no Fate. Win = attacker
        // successes >= card.fate. The Fate the Hero spends here is temporary — restored to its
        // pre-Challenge value on the way out (RestoreResearchProductionFate).
        //
        // The caller (HexSelectionController) has already: revalidated eligibility, checked hand
        // capacity, and PAID the card's ResourceCost. This method only runs the roll and reports
        // success/failure through onResolved.
        public void BeginResearchProduction(UnitData hero, CardDefinition card, ResearchProductionMode mode,
            Sprite heroLogo, Sprite cardLogo, Action<bool> onResolved)
        {
            StopAllCoroutines();
            // A prior R/P state that never resolved (should not happen — the modal blocks input
            // for the whole Challenge — but be safe, per spec §15).
            CleanupResearchProduction();

            CloseTacticPresentation();
            _rpTactics = new ResearchProductionChallengeSession(hero, card);
            _kind = ChallengeKind.ResearchProduction;
            _automateHumanSides = false;
            _rpHero = hero;
            _rpCard = card;
            _rpRequiredSuccesses = ResearchProductionSystem.RequiredSuccesses(card);
            _rpFateSnapshot = hero != null ? hero.Fate : 0;
            _attackerTokenPoolSize = ResearchProductionSystem.TokenPoolSizeFor(hero);
            _rpActive = true;
            _rpResultSuccess = false;
            _rpTokens = null;
            _onResearchProductionResolved = onResolved;

            // The Hero is a real UnitData and a legitimate attacker (only the DEFENDER must not
            // be a fake unit). Setting _attacker lets RunsAutomatically / the AI-thought hooks read
            // correctly; _defender stays null (there is no defender unit).
            _attacker = hero;
            _defender = null;
            _interceptor = null;
            _attackerHero = hero;
            _defenderHero = null;
            _attackerTokens = null;
            _defenderTokens = null;
            _phase = Phase.InProgress;
            _okAlreadyHandled = false;
            _awaitingHumanDecision = false;
            _humanSpent = false;
            _humanDeclined = false;

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
            if (rollStateRoot != null)
                rollStateRoot.SetActive(true);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(false);
            if (titleText != null)
                titleText.text = mode == ResearchProductionMode.Research
                    ? "RESEARCH CHALLENGE" : "PRODUCTION CHALLENGE";

            attackerRow?.Setup(hero, hero, heroLogo);
            attackerRow?.SetTokenPoolSize(_attackerTokenPoolSize);
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetupCardDefender(card != null ? card.displayName : string.Empty, null);

            if (rollButton != null)
                rollButton.interactable = false;
            if (acceptButton != null)
                acceptButton.interactable = false;

            StartCoroutine(RunResearchProductionChallenge());
        }

        // Defender shows its fixed successes up front; attacker rolls its Fate pool; then it
        // gets a Spend-or-Accept loop. Spend, per spec §2: if a miss exists → reroll it (existing
        // RerollOneMiss, unchanged); otherwise, if still short of target → append and roll ONE new
        // die (overflow). Loop ends on Accept, on Fate == 0, or when there's no miss AND the
        // target is already met.
        private IEnumerator RunResearchProductionChallenge()
        {
            defenderRow?.SetFixedSuccesses(_rpRequiredSuccesses);
            bool spentAtLeastOnce = false;

            _rpTokens = ChallengeResolver.RollTokens(_attackerTokenPoolSize);
            _rpTactics.Tokens = _rpTokens;
            PrepareTacticPresentation(_rpHero?.Owner);
            bool animDone = attackerRow == null;
            attackerRow?.SetTokens(_rpTokens, onComplete: () =>
            {
                attackerRow.SetFatePreview(_rpHero?.Fate ?? 0);
                animDone = true;
            });
            yield return new WaitUntil(() => animDone);
            RefreshTacticTotals();

            BattleDebugLog.Write($"[RollDiag] ResearchProduction: {_rpHero?.Name} -> {_rpCard?.displayName} " +
                $"target={_rpRequiredSuccesses}, roll={BattleDebugLog.TokenString(_rpTokens)} ({CountHits(_rpTokens)} hits)");

            while (true)
            {
                bool canSpend = CanSpendResearchProductionFate();
                if (!canSpend && !_rpTactics.HasTactic && _pendingTactic == null)
                    break;

                _awaitingHumanDecision = true;
                _defenderTurnActive = false;
                _humanSpent = false;
                _humanDeclined = false;
                attackerRow?.SetSpendInteractable(canSpend);
                OpenTacticDecision(false);
                OfferFateTutorial(attackerRow, false, canSpend);
                if (acceptButton != null)
                    acceptButton.interactable = true;

                yield return new WaitUntil(() => _humanSpent || _humanDeclined);

                _awaitingHumanDecision = false;
                CloseTacticDecision();
                attackerRow?.SetSpendInteractable(false);
                if (acceptButton != null)
                    acceptButton.interactable = false;

                if (_humanDeclined)
                    break;

                // OnResearchProductionSpend already mutated _rpTokens + Fate and kicked the anim —
                // wait for it to land before re-offering Spend/Accept (same gate as the duel).
                yield return new WaitUntil(() => _rerollAnimDone);
                RefreshTacticTotals();
                spentAtLeastOnce = true;
            }

            // Autoroll can make the last Spend exhaust Fate (or remove the last useful reroll),
            // so the loop has no next decision window and used to replace the landed die with the
            // result screen immediately. Preserve one readable half-second acceptance beat after
            // that final animation. Manual Accept and challenges with no Spend remain immediate.
            if (NeedsResearchProductionAutoAcceptDelay(
                    IsAutorollEnabled, spentAtLeastOnce, _humanDeclined)
                && aiAcceptDelay > 0f)
                yield return new WaitForSeconds(aiAcceptDelay);

            ResolveResearchProduction();
        }

        internal static bool NeedsResearchProductionAutoAcceptDelay(
            bool autorollEnabled, bool spentAtLeastOnce, bool humanDeclined) =>
            autorollEnabled && spentAtLeastOnce && !humanDeclined;

        // The attacker's Spend for a Research/Production Challenge — reached from OnAttackerSpend,
        // which forks here on _kind. Miss present → single reroll (RerollOneMiss, shared/unchanged).
        // No miss but still short of target → overflow: append + roll one new die.
        private void OnResearchProductionSpend()
        {
            if (!_awaitingHumanDecision || _humanSpent || _humanDeclined || _rpHero == null || _rpHero.Fate <= 0)
                return;

            bool hasMiss = ChallengeResolver.HasMiss(_rpTokens);
            if (!hasMiss && !CanSpendResearchProductionFate())
                return; // nothing Fate can do

            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
            _rpHero.Fate--;
            TutorialHintController.Complete(TutorialHintId.Fate);
            _rerollAnimDone = attackerRow == null;

            if (hasMiss)
            {
                RerollOneMiss(ref _rpTokens, out int rerolledIndex);
                attackerRow?.SetTokens(_rpTokens, rerolledIndex, OnResearchProductionRollLanded);
                attackerRow?.OnFateSpent();
                BattleDebugLog.Write($"[RPChallenge] {_rpHero.Name} spent Fate (remaining={_rpHero.Fate}), " +
                    $"rerolled slot {rerolledIndex} -> {_rpTokens[rerolledIndex]}");
            }
            else
            {
                bool hit = ChallengeResolver.RollTokens(1)[0];
                var grown = new bool[_rpTokens.Length + 1];
                System.Array.Copy(_rpTokens, grown, _rpTokens.Length);
                grown[grown.Length - 1] = hit;
                _rpTokens = grown;
                attackerRow?.SetTokenPoolSize(_rpTokens.Length);
                attackerRow?.AppendToken(hit, OnResearchProductionRollLanded);
                attackerRow?.OnFateSpent();
                BattleDebugLog.Write($"[RPChallenge] {_rpHero.Name} spent Fate (remaining={_rpHero.Fate}), " +
                    $"added overflow die #{_rpTokens.Length} -> {hit}");
            }
            _rpTactics.Tokens = _rpTokens;
            _humanSpent = true;
        }

        private void OnResearchProductionRollLanded()
        {
            attackerRow?.SetFatePreview(_rpHero?.Fate ?? 0);
            _rerollAnimDone = true;
        }

        private void ResolveResearchProduction()
        {
            _phase = Phase.Resolved;
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;

            CloseTacticDecision();
            int successes = _rpTactics.Successes;
            _rpRequiredSuccesses = _rpTactics.Required;
            _rpResultSuccess = _rpTactics.Success;

            // Central, unconditional Fate restore — success or failure, before the result screen
            // or any callback (spec §3/§15).
            RestoreResearchProductionFate();

            BattleDebugLog.Write($"[ResolveDiag] ResearchProduction {_rpHero?.Name} -> {_rpCard?.displayName}: " +
                $"{successes} successes vs required {_rpRequiredSuccesses} -> {(_rpResultSuccess ? "SUCCESS" : "FAILURE")}; " +
                $"hero Fate restored to {_rpFateSnapshot}");

            ShowResearchProductionResult(_rpResultSuccess, successes);
        }

        private void ShowResearchProductionResult(bool success, int successes)
        {
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
            {
                Sprite art = _rpHero != null
                    ? (_rpHero.DetailArt != null ? _rpHero.DetailArt : _rpHero.Art)
                    : null;
                resultArtImage.sprite = art;
                resultArtImage.gameObject.SetActive(art != null);
            }
            if (resultSummaryText != null)
            {
                string verdict = success
                    ? $"{_rpHero?.Name} completes {_rpCard?.displayName}."
                    : $"{_rpHero?.Name} fails to complete {_rpCard?.displayName}.";
                resultSummaryText.text = $"{verdict}\nSuccesses: {successes} / {_rpRequiredSuccesses}";
            }
            if (resultTargetArtImage != null)
            {
                Sprite targetArt = _rpCard != null ? (_rpCard.detailArt != null ? _rpCard.detailArt : _rpCard.art) : null;
                resultTargetArtImage.sprite = targetArt;
                resultTargetArtImage.gameObject.SetActive(targetArt != null);
            }
            if (resultTargetNameText != null)
            {
                resultTargetNameText.text = _rpCard != null ? _rpCard.displayName : string.Empty;
                resultTargetNameText.color = Color.white;
            }
            if (resultTargetHpText != null)
            {
                resultTargetHpText.text = success ? "Success" : "Failed";
                resultTargetHpText.color = Color.white;
            }
            if (destroyedStamp != null)
                destroyedStamp.SetActive(false);

            if (RunsAutomatically || IsAutoCloseResultEnabled)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        // R/P ONLY. Puts the Hero's Fate back to its pre-Challenge value. Idempotent (the
        // snapshot isn't cleared, but re-assigning the same value is harmless) and gated so it
        // can NEVER run for a Ground Combat / Capture Kill / Aviation resolve (spec §15/§33).
        private void RestoreResearchProductionFate()
        {
            if (_kind != ChallengeKind.ResearchProduction && !_rpActive)
                return;
            if (_rpHero != null)
                _rpHero.Fate = _rpFateSnapshot;
        }

        // THE single teardown/finalization path for a Research/Production Challenge — used by
        // BOTH the normal Result -> OK flow (OnOkClicked) and every abnormal exit: a forced
        // Hide() from elsewhere, this popup being grabbed for another Challenge (Begin /
        // BeginCaptureKill / ShowSecondaryAttackResult / a fresh BeginResearchProduction), any other
        // teardown of an active R/P state. Idempotent: the pending callback is captured-then-
        // nulled and _rpActive cleared on the first call, so any later call (e.g. Hide()
        // running right after OnOkClicked already finalized) is a no-op — the R/P callback can
        // never fire twice.
        //
        //   - stops the running R/P coroutine (roll / reroll waits),
        //   - restores the Hero's Fate to its pre-Challenge snapshot (spec §3/§15) — always,
        //     success or failure,
        //   - clears this popup's R/P state,
        //   - invokes the R/P callback exactly once with `success`.
        //
        // Does NOT refund the card's ResourceCost — that was paid by HexSelectionController
        // before the Challenge and is deliberately never returned (spec §4). An interrupted
        // Challenge is finalized with success == false, so the caller
        // (HexSelectionController.OnResearchProductionResolved) mints no card and unwinds its
        // own _rpTransactionActive / _rpPendingCard and the modal's Busy state.
        private void FinalizeResearchProduction(bool success)
        {
            if (!_rpActive && _onResearchProductionResolved == null)
                return;

            if (_kind == ChallengeKind.ResearchProduction)
                StopAllCoroutines();

            RestoreResearchProductionFate();

            Action<bool> callback = _onResearchProductionResolved;
            _onResearchProductionResolved = null;
            _rpActive = false;
            _rpResultSuccess = success;
            _awaitingHumanDecision = false;
            _rpHero = null;
            _rpCard = null;
            _rpTokens = null;
            _rpTactics = null;

            callback?.Invoke(success);
        }

        // Popup-reuse call sites (Begin / BeginCaptureKill via Begin / ShowSecondaryAttackResult /
        // BeginResearchProduction): a reused popup abandons any in-flight R/P Challenge as a
        // failure — no card minted, Fate restored, callback fired exactly once.
        private void CleanupResearchProduction() => FinalizeResearchProduction(false);

        private static int CountHits(bool[] tokens)
        {
            if (tokens == null)
                return 0;
            int hits = 0;
            foreach (bool hit in tokens)
                if (hit) hits++;
            return hits;
        }
    }
}
