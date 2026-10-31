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
        // successes >= card.fate. The session spends its local Fate budget; live hero Fate is unchanged.
        //
        // The caller (HexSelectionController) has already: revalidated eligibility, checked hand
        // capacity, and PAID the card's ResourceCost. This method only runs the roll and reports
        // success/failure through onResolved.
        public void BeginResearchProduction(UnitData hero, CardDefinition card, ResearchProductionMode mode,
            Sprite heroLogo, Sprite cardLogo, Action<bool> onResolved,
            ResearchProductionChallengeSession session = null)
        {
            int generation = ++_presentationGeneration;
            StopAllCoroutines();
            _challengeSession?.Dispose();
            _challengeSession = null;
            // A prior R/P state that never resolved (should not happen — the modal blocks input
            // for the whole Challenge — but be safe, per spec §15).
            try { CleanupResearchProduction(); }
            catch
            {
                if (!ReferenceEquals(_rpTactics, session)) session?.Dispose();
                onResolved?.Invoke(false);
                throw;
            }
            if (generation != _presentationGeneration)
            {
                if (!ReferenceEquals(_rpTactics, session)) session?.Dispose();
                onResolved?.Invoke(false);
                return;
            }

            CloseTacticPresentation();
            _rpTactics = session ?? new ResearchProductionChallengeSession(hero, card);
            _kind = ChallengeKind.ResearchProduction;
            _automateHumanSides = false;
            _rpHero = hero;
            _rpCard = card;
            _rpRequiredSuccesses = _rpTactics.BaseRequired;
            _attackerTokenPoolSize = _rpTactics.TokenPoolSize;
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

            var presentationSession = _rpTactics;
            try
            {
                if (panelRoot != null)
                {
                    panelRoot.SetActive(true);
                    panelRoot.transform.SetAsLastSibling();
                }
                VisibilityChanged?.Invoke();
                if (generation != _presentationGeneration) return;
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

                StartCoroutine(RunResearchProductionSafely(_rpTactics));
            }
            catch
            {
                if (ReferenceEquals(_rpTactics, presentationSession)) FinalizeResearchProduction(false);
                throw;
            }
        }

        // Catch iterator failures at MoveNext (Unity does not reliably dispose an abandoned coroutine).
        private IEnumerator RunResearchProductionSafely(ResearchProductionChallengeSession session)
        {
            var routine = RunResearchProductionChallenge(session);
            while (ReferenceEquals(_rpTactics, session))
            {
                bool next;
                try { next = routine.MoveNext(); }
                catch
                {
                    if (ReferenceEquals(_rpTactics, session)) FinalizeResearchProduction(false);
                    throw;
                }
                if (!next) yield break;
                yield return routine.Current;
            }
        }

        // Defender shows its fixed successes up front; attacker rolls its Fate pool; then it
        // gets a Spend-or-Accept loop. Spend, per spec §2: if a miss exists → reroll it (existing
        // RerollOneMiss, unchanged); otherwise, if still short of target → append and roll ONE new
        // die (overflow). Loop ends on Accept, on Fate == 0, or when there's no miss AND the
        // target is already met.
        private IEnumerator RunResearchProductionChallenge(ResearchProductionChallengeSession session)
        {
            defenderRow?.SetFixedSuccesses(_rpRequiredSuccesses);
            bool spentAtLeastOnce = false;

            session.Roll();
            _rpTokens = session.Tokens;
            PrepareTacticPresentation(_rpHero?.Owner);
            bool animDone = attackerRow == null;
            attackerRow?.SetTokens(_rpTokens, onComplete: () =>
            {
                if (!ReferenceEquals(_rpTactics, session)) return;
                attackerRow.SetFateDisplay(session.FateRemaining);
                attackerRow.SetFatePreview(session.FateRemaining);
                animDone = true;
            });
            yield return new WaitUntil(() => animDone);
            RefreshTacticTotals();

            BattleDebugLog.Write($"[RollDiag] ResearchProduction: {_rpHero?.Name} -> {_rpCard?.displayName} " +
                $"target={_rpRequiredSuccesses}, roll={BattleDebugLog.TokenString(_rpTokens)} ({CountHits(_rpTokens)} hits)");

            while (ReferenceEquals(_rpTactics, session))
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

            if (ReferenceEquals(_rpTactics, session)) ResolveResearchProduction();
        }

        internal static bool NeedsResearchProductionAutoAcceptDelay(
            bool autorollEnabled, bool spentAtLeastOnce, bool humanDeclined) =>
            autorollEnabled && spentAtLeastOnce && !humanDeclined;

        // The attacker's Spend for a Research/Production Challenge — reached from OnAttackerSpend,
        // which forks here on _kind. Miss present → single reroll (RerollOneMiss, shared/unchanged).
        // No miss but still short of target → overflow: append + roll one new die.
        private void OnResearchProductionSpend()
        {
            var session = _rpTactics;
            if (!_awaitingHumanDecision || _humanSpent || _humanDeclined || session == null) return;
            try
            {
                GetTacticPreview(out int a, out int d);
                if (!session.TrySpendWithModifiers(a, d, out int index, out bool appended)) return;
                _rpTokens = session.Tokens;
                attackerRow?.SetSpendInteractable(false);
                if (acceptButton != null) acceptButton.interactable = false;
                TutorialHintController.Complete(TutorialHintId.Fate);
                _rerollAnimDone = attackerRow == null;
                Action landed = () =>
                {
                    if (!ReferenceEquals(_rpTactics, session)) return;
                    attackerRow?.SetFateDisplay(session.FateRemaining);
                    attackerRow?.SetFatePreview(session.FateRemaining);
                    _rerollAnimDone = true;
                };
                if (appended)
                {
                    attackerRow?.SetTokenPoolSize(_rpTokens.Length);
                    attackerRow?.AppendToken(_rpTokens[index], landed);
                }
                else attackerRow?.SetTokens(_rpTokens, index, landed);
                attackerRow?.SetFateDisplay(session.FateRemaining);
                attackerRow?.SetFatePreview(session.FateRemaining);
                _humanSpent = true;
            }
            catch
            {
                if (ReferenceEquals(_rpTactics, session)) FinalizeResearchProduction(false);
                throw;
            }
        }

        private void OnResearchProductionAccept()
        {
            var session = _rpTactics;
            if (session == null) return;
            try
            {
                if (!CommitPendingTactic()) return;
                _humanDeclined = true;
                defenderRow?.SetSpendInteractable(false);
                attackerRow?.SetSpendInteractable(false);
                if (acceptButton != null) acceptButton.interactable = false;
            }
            catch
            {
                if (ReferenceEquals(_rpTactics, session)) FinalizeResearchProduction(false);
                throw;
            }
        }

        private void ResolveResearchProduction()
        {
            _phase = Phase.Resolved;
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;

            CloseTacticDecision();
            var result = _rpTactics.Accept();
            int successes = result.Successes;
            _rpRequiredSuccesses = result.Required;
            _rpResultSuccess = result.Success;

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

        // Detach and close the old presentation before invoking arbitrary caller code. A callback
        // may open a new challenge synchronously, or throw; neither can strand this attempt.
        private void FinalizeResearchProduction(bool success)
        {
            if (!_rpActive && _onResearchProductionResolved == null) return;
            StopAllCoroutines();
            Action<bool> callback = _onResearchProductionResolved;
            var session = _rpTactics;
            bool accepted = success && session?.IsAccepted == true && session.Result.Success;
            _onResearchProductionResolved = null;
            _rpActive = false;
            _rpTactics = null;
            _rpHero = null;
            _rpCard = null;
            _rpTokens = null;
            _awaitingHumanDecision = false;
            session?.Dispose();
            try
            {
                attackerRow?.CancelPendingRolls();
                defenderRow?.CancelPendingRolls();
                CloseTacticPresentation();
                if (panelRoot != null) panelRoot.SetActive(false);
                VisibilityChanged?.Invoke();
            }
            finally { callback?.Invoke(accepted); }
        }

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
