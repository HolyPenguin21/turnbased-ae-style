using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;

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
    //
    //  2026-11-07 (owner, playtest Mordak/Ysolde) — the need is read once per pass (Assess) and
    //  carried by the pass, because the strike is produced only at ClosePass, after the card
    //  rounds:
    //    · a Development upgrade on a unit outside the operation's fist may not take the
    //      persistent resources the cheapest strike source needs (LeavesStrikeSupport). Ysolde
    //      T26 started with E6 M9 T5 — one Tactical Missile — and three upgrades (a Fate genome on a
    //      garrison hero among them) left T0 before the strike was asked;
    //    · the need is an operational forecast for Economy (ResourceStarvationRegistry, producer
    //      "attack-strike", the channel the air lane's energy uses): with the deck spent, Economy's
    //      targets fell to 1/1/1/1 while Tech, the one resource a strike needed, stayed at 1;
    //    · a refused strike says why ([AI][V2][Attack][Support] ... not produced: ...).
    //  Upgrades of the fist itself, and every card delivery, keep their priority.
    // ===========================================================================================
    internal sealed class StrikeSupportNeed
    {
        internal MissionIntent Operation;
        internal AttackIntent Attack;
        internal ArmySnapshot Primary;
        internal AttackSupportForecast Forecast;
        // The persistent resources of the cheapest strike source (a hand card, or a Challenge plus
        // the card it mints) — what the operation needs left after the card rounds.
        internal ResourceCost Cost;
        internal string Source;
    }

    internal static class PhaseAOperationSupport
    {
        internal const string ForecastProducer = "attack-strike";

        // The live operation's need for a strike, or null: Aggression admitted, a live Gather /
        // Assault with a primary, a strike source (hand card, or a free hand slot + generation left +
        // a forecast generator minting one), no counted air support and a fist not ready without it.
        internal static StrikeSupportNeed Assess(PhaseAPass pass)
        {
            if (pass?.Snap?.Self == null || !pass.AdmittedAxes.Contains(DesireAxis.Aggression))
                return null;
            MissionIntent operation = AggressionMissionLayer.LiveAttackOperation(pass.ActiveIntents);
            AttackIntent a = operation?.Attack;
            if (a == null || !a.PrimaryArmyId.HasValue
                || (a.Phase != AttackMissionPhase.Gather && a.Phase != AttackMissionPhase.Assault))
                return null;
            // Structural sources: a strike card in hand, or a Challenge that mints one, that can
            // actually reach this target from a legal airfield (the deployment play's own rule).
            // A source the delivery would refuse must not hold resources for a strike
            // (2026-11-08: an out-of-range Moth Bomber froze Materials and blocked two upgrades).
            List<CardData> handSources = pass.Hand.Hand.Where(c => IsStrikeCard(c?.Definition)).ToList();
            List<GenerationStep> generatedSources = pass.Hand.HasFreeSlot && pass.Reservation.CanGenerateMore
                ? (pass.Snap.Development?.ForecastSources ?? System.Array.Empty<GenerationStep>())
                    .Where(g => IsStrikeCard(g?.CardDef)).ToList()
                : new List<GenerationStep>();
            if (handSources.Count == 0 && generatedSources.Count == 0)
                return null;
            ArmySnapshot primary = pass.Snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (primary == null)
                return null;
            AttackSupportForecast forecast = AttackSupportProjection.For(pass.Snap, a.Target.Hex, primary);
            if (forecast.AirSourceArmyId.HasValue
                || AttackPreparationPolicy.AssessReadiness(primary, pass.Snap.Self.AttackPeak,
                    forecast.Opposition, forecast.HexBonus).Ready)
                return null;
            List<CombatAirSupportRequest> requests = NonCombatCardPlayer.KnownStrikeTargets(pass.Snap,
                    pass.Player, pass.Root, pass.Ctx,
                    ReconObjectiveEvaluator.Enumerate(pass.Snap).Where(o => o != null).ToList())
                .Where(r => r.Target.Equals(a.Target.Hex)).ToList();
            var costs = new List<(ResourceCost Cost, string Source)>();
            foreach (CardData c in handSources)
                if (NonCombatCardPlayer.CanStructurallyStrike(pass.Snap, pass.Player, pass.Root, pass.Ctx, c, requests))
                    costs.Add((c.EffectivePlayResourceCost, "hand:" + c.Definition.displayName));
            foreach (GenerationStep g in generatedSources)
            {
                var minted = new CardData(g.CardDef) { ResearchProductionCreated = true };
                if (NonCombatCardPlayer.CanStructurallyStrike(pass.Snap, pass.Player, pass.Root, pass.Ctx, minted, requests))
                    costs.Add((StrategicCardEvaluator.AddResourceCosts(minted.EffectivePlayResourceCost,
                        g.ChallengeCard?.resourceCost), "generate:" + g.CardDef.displayName));
            }
            if (costs.Count == 0)
                return null;
            var cheapest = costs.OrderBy(x => Total(x.Cost)).ThenBy(x => x.Source, System.StringComparer.Ordinal).First();
            return new StrikeSupportNeed
            {
                Operation = operation, Attack = a, Primary = primary, Forecast = forecast,
                Cost = cheapest.Cost, Source = cheapest.Source,
            };
        }

        // Economy's view of the need: replaced by every pass that has Aggression admitted (the
        // only passes that can judge it), cleared when no strike is needed. A pass without
        // Aggression leaves the last read; the registry's one-turn grace retires it.
        internal static void PublishForecast(PhaseAPass pass, StrikeSupportNeed need)
        {
            if (pass?.Player == null || !pass.AdmittedAxes.Contains(DesireAxis.Aggression))
                return;
            Dictionary<string, ResourceBundle> needs = null;
            if (need?.Cost != null)
            {
                var bundle = new ResourceBundle();
                foreach (ResourceType t in ResourceBundle.All)
                    bundle.Add(t, need.Cost.Get(t));
                needs = new Dictionary<string, ResourceBundle> { [need.Operation.IntentKey.ToString()] = bundle };
            }
            ResourceStarvationRegistry.ReplaceOperationalForecast(pass.Player, pass.Turn, ForecastProducer, needs);
        }

        // False when this Development upgrade would leave less of a resource it spends than the
        // pass's strike need: only resources the upgrade itself consumes are compared, and an
        // upgrade of the operation's own fist (primary or a held support) is never held back.
        internal static bool LeavesStrikeSupport(PhaseAPass pass, MaterializationPlan plan, out string why) =>
            LeavesStrikeSupport(pass?.Player, pass?.Root, pass?.Ctx, pass?.StrikeSupport, plan?.FundingCost,
                plan?.DevelopmentUpgrade?.RecipientArmyId, "upgrade", out why);

        // The same rule for an Equipment card played from hand in Phase B (the need Phase A left
        // in StrategicPhaseResult.StrikeSupport): equipment for a unit outside the fist must not
        // spend what the strike is accumulating toward. Ysolde T19 played a Flamer from hand.
        internal static bool LeavesStrikeSupport(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            StrikeSupportNeed need, NonCombatCardPlayer.NonCombatPlay play, out string why)
        {
            int? recipient = play?.EquipHost != null
                ? OwnWorld.Of(player)?.ArmyContaining(play.EquipHost)?.Id : null;
            return LeavesStrikeSupport(player, root, ctx, need, play?.FundingCost, recipient, "equipment", out why);
        }

        private static bool LeavesStrikeSupport(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            StrikeSupportNeed need, ResourceCost cost, int? recipient, string spender, out string why)
        {
            why = null;
            if (need?.Cost == null || cost == null || player == null)
                return true;
            if (recipient.HasValue && (recipient.Value == need.Attack.PrimaryArmyId
                    || GroundCombatLegs.HeldGroundSupportArmyIds(need.Operation).Contains(recipient.Value)))
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int spends = cost.Get(t);
                if (spends <= 0)
                    continue;
                float spendable = StrategicSpendability.SpendableAmount(player, root, ctx, t);
                if (spendable + AiConfigV2.allocatorSliceEpsilon < spends + need.Cost.Get(t))
                {
                    why = $"leaves_strike_support:{need.Operation.IntentKey} {t} "
                        + $"{spendable.ToString("0.##", CultureInfo.InvariantCulture)} < {spender} {spends} "
                        + $"+ strike {need.Cost.Get(t)} ({need.Source})";
                    return false;
                }
            }
            return true;
        }

        internal static bool TryProduceStrikeSupport(PhaseAPass pass)
        {
            StrikeSupportNeed need = Assess(pass);
            if (need == null)
                return false;
            MissionIntent operation = need.Operation;
            AttackIntent a = need.Attack;
            AttackSupportForecast forecast = need.Forecast;

            float spendableAp = StrategicSpendability.SpendableAp(pass.Player, pass.Root, pass.Ctx)
                - pass.ApBudget.ReservedFollowup();
            List<NonCombatCardPlayer.NonCombatPlay> onSite = NonCombatCardPlayer.EnumeratePlays(pass.Snap,
                    pass.Player, pass.Root, pass.Hand, pass.Ctx, new List<string>(), pass.Reservation,
                    aviationOnly: true)
                .Where(p => p.Kind == NonCombatCardPlayer.PlayKind.Aviation
                    && p.StrikeTarget.HasValue && p.StrikeTarget.Value.Equals(a.Target.Hex))
                .ToList();
            NonCombatCardPlayer.NonCombatPlay best = onSite
                .Where(p => p.Score > AiConfigV2.devEvMargin
                    && p.ApCost <= spendableAp + AiConfigV2.allocatorSliceEpsilon
                    && StrategicSpendability.FitsSpendableResources(pass.Player, pass.Root, pass.Ctx, p.FundingCost))
                .OrderByDescending(p => p.Score).ThenBy(p => p.StableKey, System.StringComparer.Ordinal)
                .FirstOrDefault();
            if (best == null)
            {
                AiDebugLog.WriteDeduped($"{operation.IntentKey}#strike-refused",
                    $"[AI][V2][Attack][Support] {operation.IntentKey} strike support for "
                    + $"{a.Target.DiagnosticLabel} ({forecast.Describe()}) not produced: "
                    + RefusalReason(pass, onSite, spendableAp) + $" (cheapest source {need.Source})");
                return false;
            }

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

        // The first gate the best on-site strike play missed, in the order the selection applies them.
        private static string RefusalReason(PhaseAPass pass, List<NonCombatCardPlayer.NonCombatPlay> onSite,
            float spendableAp)
        {
            if (onSite.Count == 0)
                return "no_strike_play_on_site";
            NonCombatCardPlayer.NonCombatPlay top = onSite.OrderByDescending(p => p.Score)
                .ThenBy(p => p.StableKey, System.StringComparer.Ordinal).First();
            if (top.Score <= AiConfigV2.devEvMargin)
                return $"score {top.Score.ToString("0.00", CultureInfo.InvariantCulture)} <= margin";
            if (top.ApCost > spendableAp + AiConfigV2.allocatorSliceEpsilon)
                return $"ap {spendableAp.ToString("0.##", CultureInfo.InvariantCulture)} < {top.ApCost}";
            var shortages = new List<string>();
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = top.FundingCost?.Get(t) ?? 0;
                float have = StrategicSpendability.SpendableAmount(pass.Player, pass.Root, pass.Ctx, t);
                if (need > 0 && have + AiConfigV2.allocatorSliceEpsilon < need)
                    shortages.Add($"{t} {have.ToString("0.##", CultureInfo.InvariantCulture)}<{need}");
            }
            return shortages.Count > 0 ? "resources " + string.Join(",", shortages) : "not_selected";
        }

        private static int Total(ResourceCost c) =>
            c == null ? 0 : c.human + c.energy + c.materials + c.tech;

        // An aircraft that strikes ground (a missile or a bomber), never a recon-only frame.
        private static bool IsStrikeCard(Game.Cards.CardDefinition def) =>
            def?.isAviation == true
            && Game.Combat.ExplosionAttack.AttackPower(def.attack, def.grantedAbilities) > 0;
    }
}
