using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  2026-11-02 (owner decision) — THE STRIKE A LIVE ATTACK WAITS FOR, PAID BY THE OPERATION.
    //
    //  Missile manufacture used to be a Phase-B action only: it got the AP missions left over, which
    //  was 0 on most turns (playtest: 115 strike candidates on Rurik's Glass Ward, none produced),
    //  and its only strike witnesses were operations already marching. The fist, in turn, never
    //  marched without the softening — a closed loop.
    //
    //  A live Attack operation (Gather, preparation included, or Assault) whose forecast counts no
    //  air support (AttackSupportProjection) and whose fist does not clear the site without it is a
    //  justified need (Attack forms the need, Production strengthens it). Phase A then plays the one
    //  best aviation play whose witnessed use is a strike on that operation's site — a missile card
    //  in hand, or a Production Challenge that mints one — through the same enumeration, score,
    //  spendability and executor as Phase B. One per pass; the shared per-turn generation budget,
    //  the tried-generator set and free hand slots bound it. Nothing is flown here: the stored
    //  missile is counted by the forecast, the fist departs, and the Assault binds and flies it.
    // ===========================================================================================
    internal static class PhaseAOperationSupport
    {
        internal static bool TryProduceStrikeSupport(PhaseAPass pass)
        {
            if (pass?.Snap?.Self == null || !pass.AdmittedAxes.Contains(DesireAxis.Aggression))
                return false;
            MissionIntent operation = AggressionMissionLayer.LiveAttackOperation(pass.ActiveIntents);
            AttackIntent a = operation?.Attack;
            if (a == null || !a.PrimaryArmyId.HasValue
                || (a.Phase != AttackMissionPhase.Gather && a.Phase != AttackMissionPhase.Assault))
                return false;
            // Cheap structural pre-check: a strike card in hand, or a Challenge that mints one.
            bool strikeCard = pass.Hand.Hand.Any(c => IsStrikeCard(c?.Definition))
                || pass.Hand.HasFreeSlot && pass.Reservation.CanGenerateMore
                    && (pass.Snap.Development?.ForecastSources ?? System.Array.Empty<GenerationStep>())
                        .Any(g => IsStrikeCard(g?.CardDef));
            if (!strikeCard)
                return false;
            ArmySnapshot primary = pass.Snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (primary == null)
                return false;
            AttackSupportForecast forecast = AttackSupportProjection.For(pass.Snap, a.Target.Hex, primary);
            if (forecast.AirSourceArmyId.HasValue
                || AttackPreparationPolicy.AssessReadiness(primary, pass.Snap.Self.AttackPeak,
                    forecast.Opposition, forecast.HexBonus).Ready)
                return false;

            float spendableAp = StrategicSpendability.SpendableAp(pass.Player, pass.Root, pass.Ctx)
                - pass.ApBudget.ReservedFollowup();
            NonCombatCardPlayer.NonCombatPlay best = NonCombatCardPlayer.EnumeratePlays(pass.Snap,
                    pass.Player, pass.Root, pass.Hand, pass.Ctx, new List<string>(), pass.Reservation,
                    aviationOnly: true)
                .Where(p => p.Kind == NonCombatCardPlayer.PlayKind.Aviation
                    && p.StrikeTarget.HasValue && p.StrikeTarget.Value.Equals(a.Target.Hex)
                    && p.Score > AiConfigV2.devEvMargin
                    && p.ApCost <= spendableAp + AiConfigV2.allocatorSliceEpsilon
                    && StrategicSpendability.FitsSpendableResources(pass.Player, pass.Root, pass.Ctx, p.ResCost))
                .OrderByDescending(p => p.Score).ThenBy(p => p.StableKey, System.StringComparer.Ordinal)
                .FirstOrDefault();
            if (best == null)
                return false;

            pass.Result.MaterializationAttempts++;
            ReservationInvariants.SpendProbe probe = ReservationInvariants.BeginSpend(
                pass.Player, pass.Root, pass.Ctx, "operation strike support");
            NonCombatExecuteResult res = NonCombatPlayExecutor.Execute(best, pass.Snap, pass.Player,
                pass.Root, pass.Hand, pass.Ctx);
            ReservationInvariants.EndSpend(pass.Player, pass.Root, pass.Ctx, probe);
            if (res.GenerationAttempted)
            {
                pass.Reservation.RecordGenerationAttempt(best.Generation, null);
                StrategicTempoBudget.RecordGenerationAttempt(pass.Player, pass.Ctx.TurnNumber);
                pass.Result.GeneratedCardAttempts++;
                if (res.Generated) pass.Result.GeneratedCardsSucceeded++;
            }
            if (res.StateChanged)
            {
                pass.Result.StateChanged = true;
                WorldDeltaLifecycle.CommitMutation();
                pass.RefreshKnowledge();
            }
            if (res.Played)
            {
                pass.Result.MaterializationsSucceeded++;
                pass.Result.CardsPlayed++;
            }
            AiDebugLog.Write($"[AI][V2][Attack][Support] {operation.IntentKey} strike support for "
                + $"{a.Target.DiagnosticLabel} ({forecast.Describe()}): {best.Explain} score "
                + best.Score.ToString("0.00", CultureInfo.InvariantCulture)
                + (res.Played ? $" - played (ap {res.ApSpent.ToString("0.##", CultureInfo.InvariantCulture)})"
                    : $" - not played ({res.FailReason}{(res.Generated ? "; generated card kept in hand" : "")})"));
            return res.StateChanged;
        }

        // An aircraft that strikes ground (a missile or a bomber), never a recon-only frame.
        private static bool IsStrikeCard(Game.Cards.CardDefinition def) =>
            def?.isAviation == true
            && Game.Combat.ExplosionAttack.AttackPower(def.attack, def.grantedAbilities) > 0;
    }
}
