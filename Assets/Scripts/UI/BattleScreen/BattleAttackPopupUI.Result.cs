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
    // Resolution and the result screen: damage with ability magnitudes, Capture Kill outcome,
    // secondary-attack results and the Ok hand-off.
    public partial class BattleAttackPopupUI
    {
        // Manual: "the number of success rolls for the defender is subtracted from the number of
        // success rolls for the attacker" — one-directional, only the defender ever takes damage
        // in a Ground Combat challenge (no retaliation in this pass).
        private void ResolveDamage()
        {
            _phase = Phase.Resolved;
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;

            BattleRules.GroundAttackOutcome outcome = _challengeSession != null
                ? _challengeSession.ResolveGroundOutcome()
                : default;
            bool wasHit = outcome.WasHit;
            int damage = outcome.Damage;
            var appliedAbilities = new List<string>(outcome.AppliedAbilities);

            // UnitAbilities.Bodyguard: a landed hit goes to the guard instead, re-run against its
            // own tags/armor — the same rule BattleEngine.ResolveAndApplyGroundAttack applies.
            UnitData victim = _defender;
            if (_interceptor != null && damage > 0 && _attacker != null)
            {
                victim = _interceptor;
                damage = ChallengeResult.ApplyAbilityModifiers(outcome.RawDamage, _attacker, _interceptor,
                    Magnitudes, out List<string> guardAbilities);
                appliedAbilities = guardAbilities;
                appliedAbilities.Add(UnitAbilities.Bodyguard);
            }

            BattleRules.AppendHitEffectAbilities(appliedAbilities, _attacker, victim,
                damage, _groundHitEffects?.Invoke(victim, damage));

            // Presentation only: BattleEngine applies HP/Berserk/removal after the player
            // acknowledges this result. Keep a projected HP here so the Result panel can display
            // the post-hit value without mutating battle state inside the UI.
            int projectedHp = victim != null
                ? Mathf.Max(0, victim.HitPointsCurrent - damage)
                : 0;
            bool died = victim != null && projectedHp <= 0;

            BattleDebugLog.Write($"[ResolveDiag] {_attacker?.Name} -> {_defender?.Name}: " +
                $"rawSuccesses(attacker={outcome.AttackerSuccesses},defender={outcome.DefenderSuccesses}) wasHit={wasHit} " +
                $"finalDamage={damage} appliedAbilities=[{(appliedAbilities != null ? string.Join(",", appliedAbilities) : string.Empty)}] " +
                $"victim={victim?.Name} projectedVictimHp={projectedHp}/{victim?.HitPointsMax ?? 0} died={died}");
            ShowResult(damage, died, wasHit, appliedAbilities, projectedHp, victim);
        }

        // Public so BattleScreenUI.ConsiderAiRetreat can feed the same tunable magnitudes into
        // BattleAi.AssessRetreat's damage projection — one UnitAbilityCatalog reference for the
        // whole battle screen instead of wiring a second copy onto BattleScreenUI itself.
        public float CriticalDamageMultiplier => Magnitudes.CriticalDamageMultiplier;
        public int CeramicArmorReduction => Magnitudes.CeramicArmorReduction;
        public int HyperkineticBonusDamage => Magnitudes.HyperkineticBonusDamage;
        public int PyrokineticBonusDamage => Magnitudes.PyrokineticBonusDamage;

        // One authored bundle from UnitAbilityCatalog; Default is only the no-asset safety net.
        public AbilityMagnitudes Magnitudes =>
            abilityCatalog != null ? abilityCatalog.Magnitudes : AbilityMagnitudes.Default;

        // UnitAbilities.RaiseTheRots — the card each carrier summons at battle start, and how
        // many per carrier. Same single-abilityCatalog-reference pattern as Magnitudes above;
        // null card if the catalog isn't wired (BattleScreenUI.Show logs and skips the summon).
        public CardDefinition RaiseTheRotsCard => abilityCatalog != null ? abilityCatalog.ResolveRaiseTheRotsCard() : null;
        public int RaiseTheRotsUnitsPerSummoner => abilityCatalog != null ? abilityCatalog.raiseTheRotsUnitsPerSummoner : 2;

        // Purely the rolled successes decide this — per the user's own call, dropping the
        // manual's separate "capture threshold" (comparing the hunter's successes against the
        // target's full original tokens-pool size) since that let a clean win still resolve as a
        // kill for reasons the player can't see on the tokens themselves:
        //   Escaped  — attacker successes < defender successes.
        //   Killed   — attacker successes == defender successes (a bare, even win).
        //   Captured — attacker successes > defender successes (a clean win).
        // No HP/damage involved, unlike ResolveDamage.
        private void ResolveCaptureKill()
        {
            _phase = Phase.Resolved;
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;

            CaptureKillOutcome outcome = _challengeSession != null
                ? _challengeSession.ResolveCaptureKillOutcome()
                : CaptureKillOutcome.Killed;
            var result = _challengeSession?.CurrentResult ?? new ChallengeResult(_attackerTokens, _defenderTokens);

            BattleDebugLog.Write($"[ResolveDiag] {_attacker?.Name} (hunter) -> {_defender?.Name} (target hero): " +
                $"rawSuccesses(attacker={result.AttackerSuccesses},defender={result.DefenderSuccesses}) outcome={outcome}");
            ShowCaptureKillResult(outcome);
        }

        private void ShowCaptureKillResult(CaptureKillOutcome outcome)
        {
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
            {
                Sprite attackerArt = _attacker != null
                    ? (_attacker.DetailArt != null ? _attacker.DetailArt : _attacker.Art)
                    : null;
                resultArtImage.sprite = attackerArt;
                resultArtImage.gameObject.SetActive(attackerArt != null);
            }
            if (resultSummaryText != null)
            {
                string outcomeLine = outcome switch
                {
                    CaptureKillOutcome.Escaped => "The hero evades the hunters and remains free.",
                    CaptureKillOutcome.Captured => "The hero is captured!",
                    _ => "The hero is killed while attempting to escape!",
                };
                resultSummaryText.text = $"Capture Kill Challenge\nTarget: {_defender?.Name}\n{outcomeLine}";
            }
            if (resultTargetArtImage != null)
            {
                Sprite targetArt = _defender != null ? (_defender.DetailArt != null ? _defender.DetailArt : _defender.Art) : null;
                resultTargetArtImage.sprite = targetArt;
                resultTargetArtImage.gameObject.SetActive(targetArt != null);
            }
            if (resultTargetNameText != null)
                resultTargetNameText.text = _defender?.Name;
            if (resultTargetHpText != null)
                resultTargetHpText.text = _defender != null
                    ? $"HP: {_defender.HitPointsCurrent}/{_defender.HitPointsMax}"
                    : string.Empty;
            // Captured is not a death — only Killed earns the Destroyed stamp (see the user's
            // own report: a 3:2 win that resolved as Captured still showed Destroyed here).
            if (destroyedStamp != null)
                destroyedStamp.SetActive(outcome == CaptureKillOutcome.Killed);

            if (RunsAutomatically || IsAutoCloseResultEnabled)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        // `victim`: the unit the hit landed on — _defender, or the Bodyguard that intercepted it.
        private void ShowResult(int damage, bool died, bool wasHit, List<string> appliedAbilities, int projectedHp,
            UnitData victim)
        {
            string hitLine = wasHit ? "Hit!" : "Miss";
            string outcomeLine = died ? "\nThe target was destroyed." : string.Empty;
            // Damage modifiers and on-hit effects share this list; an unmodified hit/miss
            // gets no extra line.
            string skillsLine = appliedAbilities != null && appliedAbilities.Count > 0
                ? $"\nAffected by Skill{(appliedAbilities.Count > 1 ? "s" : string.Empty)}: {string.Join(", ", appliedAbilities)}"
                : string.Empty;
            string interceptLine = victim != null && victim != _defender
                ? $"\nIntercepted by: {victim.Name}"
                : string.Empty;
            string summary = $"Attacker ID: {_attacker.Name}\nTarget ID: {_defender.Name}{interceptLine}\n" +
                $"Hit Assessment: {hitLine}\nDamage Assessment: {damage} Damage{outcomeLine}{skillsLine}";

            RenderResultScreen(_attacker, victim ?? _defender, summary, died, projectedHp);

            if (RunsAutomatically || IsAutoCloseResultEnabled)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        // The shared visual population of the Result state — attacker art (its DetailArt, or
        // Art), a free-form summary block, and the target's own art / name / "HP: x/y" /
        // DESTROYED stamp. Used by ShowResult (the primary Ground Combat hit) and
        // ShowSecondaryAttackResult (each Splash/Scorcher follow-up), so the two can't drift.
        private void RenderResultScreen(UnitData attackerForArt, UnitData target, string summary, bool died,
            int? displayedHpOverride = null)
        {
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
            {
                Sprite attackerArt = attackerForArt != null
                    ? (attackerForArt.DetailArt != null ? attackerForArt.DetailArt : attackerForArt.Art)
                    : null;
                resultArtImage.sprite = attackerArt;
                resultArtImage.gameObject.SetActive(attackerArt != null);
            }
            if (resultSummaryText != null)
                resultSummaryText.text = summary;
            if (resultTargetArtImage != null)
            {
                Sprite targetArt = target != null ? (target.DetailArt != null ? target.DetailArt : target.Art) : null;
                resultTargetArtImage.sprite = targetArt;
                resultTargetArtImage.gameObject.SetActive(targetArt != null);
            }
            if (resultTargetNameText != null)
                resultTargetNameText.text = target != null ? target.Name : string.Empty;
            if (resultTargetHpText != null)
                resultTargetHpText.text = target != null
                    ? $"HP: {displayedHpOverride ?? target.HitPointsCurrent}/{target.HitPointsMax}" : string.Empty;
            if (destroyedStamp != null)
                destroyedStamp.SetActive(died);
        }

        // A standalone follow-up Result screen for ONE Splash/Scorcher side-hit — same full
        // layout as the primary Ground Combat result (attacker art + summary + target art / HP /
        // DESTROYED stamp), re-openable on top of a just-closed result, its Ok advancing via
        // onAcknowledged. Rides the Announcement _kind's own OnOkClicked plumbing (no roll/duel
        // state involved); StopAllCoroutines clears any auto-close still pending from the screen
        // before it. See BattleScreenUI.Combat.cs's ShowSecondaryResultsThen.
        public void ShowSecondaryAttackResult(UnitData attacker, UnitData target, string summary, bool died,
            Action onAcknowledged, bool automateHumanSides = false)
        {
            StopAllCoroutines();
            CloseTacticPresentation();
            CleanupResearchProduction();

            _automateHumanSides = automateHumanSides;
            _kind = ChallengeKind.Announcement;
            _onAnnouncementAcknowledged = onAcknowledged;
            _okAlreadyHandled = false;
            _phase = Phase.Resolved;

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();

            RenderResultScreen(attacker, target, summary, died);

            if (RunsAutomatically || IsAutoCloseResultEnabled)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        private void OnOkClicked()
        {
            // Guards against a same-frame double-fire: Space (see Update's own WasSpacePressed
            // check) can land on the SAME frame the EventSystem's own Submit action already
            // routes to okButton.onClick because okButton is the currently-selected UI element —
            // both then call this. A CaptureKill chain (see BattleScreenUI.Combat.cs's
            // RunNextCaptureKillChallenge) reopens this exact popup for the NEXT hero synchronously
            // inside the first call's own callback, so without this guard the stray second call
            // used to fire against that freshly-opened NEXT challenge instead of a no-op — Hide()
            // closing it early and stale result state from the PREVIOUS challenge resolving it on the
            // spot, corrupting the next result in the chain (see the user's own report: the
            // Capture/Kill Challenge result popup's second message duplicated/broken). Every
            // Resolve* site sets _phase = Resolved right before showing this result; the reopen
            // above always resets it back to NotRolled via Begin, so a stray second call usually
            // finds the wrong phase and bails here instead — EXCEPT a plain ShowSecondaryAttackResult
            // (e.g. "The enemy retreats." after a hero escapes its Capture Kill Challenge), which
            // never leaves Phase.Resolved (see its own comment), so that stray second call used
            // to sail straight through this check and re-fire the announcement's own callback a
            // second time, showing the same line again (the user's own report of a "parasitic"
            // duplicate message). _okAlreadyHandled catches that case too, since it's reset fresh
            // by every entry point that shows something new, not just the ones that also change
            // _phase.
            if (_phase != Phase.Resolved || _okAlreadyHandled)
                return;
            if (_tacticInfoPopup is not null && TacticInputBlocked) return;
            _okAlreadyHandled = true;

            // Research/Production reports through the shared finalization path — the SAME one a
            // forced Hide() uses. Run it BEFORE Hide() (which calls it again as a safety net)
            // so the pass with the real success/failure wins and Hide()'s call no-ops instead
            // of reporting a second time. The R/P modal itself stays open; the caller decides
            // what happens next.
            if (_kind == ChallengeKind.ResearchProduction)
            {
                FinalizeResearchProduction(_rpResultSuccess);
                Hide();
                return;
            }

            Hide();
            if (_kind == ChallengeKind.CaptureKill)
            {
                Action<BattleChallengeRollResult> callback = _onCaptureKillResolved;
                _onCaptureKillResolved = null;
                callback?.Invoke(_challengeSession != null
                    ? _challengeSession.ToRollResult()
                    : new BattleChallengeRollResult(
                        _attackerTokens, _defenderTokens, _attackerFateRemaining, _defenderFateRemaining));
            }
            else if (_kind == ChallengeKind.Announcement)
            {
                Action callback = _onAnnouncementAcknowledged;
                _onAnnouncementAcknowledged = null;
                callback?.Invoke();
            }
            else
            {
                // ChallengeKind.ResearchProduction is handled earlier (before Hide) via
                // FinalizeResearchProduction and never reaches here.
                Action<BattleChallengeRollResult> callback = _onResolved;
                _onResolved = null;
                callback?.Invoke(_challengeSession != null
                    ? _challengeSession.ToRollResult()
                    : new BattleChallengeRollResult(
                        _attackerTokens, _defenderTokens, _attackerFateRemaining, _defenderFateRemaining));
            }
        }
    }
}
