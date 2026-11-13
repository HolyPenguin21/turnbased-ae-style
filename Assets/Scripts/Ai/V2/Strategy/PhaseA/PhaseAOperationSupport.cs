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
    //
    //  2026-11-09 (owner, match Q4) — an Air Bomb in hand is produced here too: Phase A attaches
    //  it (the Phase B equipment play: same enumeration, score, spendability, executor) to an own
    //  aircraft whose service on the site WITH the bomb is positive (ProjectService on the
    //  projected carrier, from its airfield or its formed wing). The forecast then counts that
    //  aircraft (GroundCombatAirSupport.Options) and the need is gone. The carrier test of Assess
    //  reads the same projection: the bare frame's own attack was read before.
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
        // Roster need only (AssessRosterFunding): the card's key and how many copies of each
        // missing roster key the host still waits for.
        internal string RosterKey;
        internal IReadOnlyDictionary<string, int> MissingRosterKeys;
        // The strike requests on the operation's site (NonCombatCardPlayer.KnownStrikeTargets).
        internal IReadOnlyList<CombatAirSupportRequest> Requests = System.Array.Empty<CombatAirSupportRequest>();
    }

    internal static class PhaseAOperationSupport
    {
        internal const string ForecastProducer = "attack-strike";
        internal const string RosterForecastProducer = "attack-roster";

        // ===================================================================================
        //  2026-11-13 (owner, P7, match Korrin vs Brynn) — THE ROSTER CARD A LIVE PREPARATION
        //  WAITS FOR. Brynn held Slag Lobber (E2 M2 T2) and Iron Maw (E3 M2 T2) from turn 1 with
        //  Materials income 2; the Materials and Tech went to whatever could be paid at once —
        //  garrison plays, eight Research/Production upgrades T24-T31 (scouts included), equipment
        //  — and the Slag Lobbers that were played went into other armies, never into the host.
        //  The cheapest missing position of the live preparation's frozen roster that the income
        //  pays within the deck horizon is read once per pass, and every spender respects it:
        //    · RESERVE, NOT A BLOCK: a spend is refused only when it would leave less of a resource
        //      than that ONE card costs. Everything above the reserve is free, so Research,
        //      Production, equipment and repairs go on with the surplus; a type the card does not
        //      cost is never touched; a type without income never gets a reserve (the card is then
        //      unfundable and is no need at all);
        //    · the roster card itself is not played into another army while the host still waits
        //      for that copy;
        //    · a spend INTO the host (a card, an upgrade, equipment) is always allowed, and the
        //      need is re-read live, so it ends the moment the host has the card or the preparation
        //      ends;
        //    · in Phase A a demand worth at least the host's own demand keeps its priority;
        //    · Economy sees the card as an operational forecast (producer "attack-roster").
        // ===================================================================================
        internal static StrikeSupportNeed AssessRosterFunding(PhaseAPass pass)
        {
            if (pass?.Snap?.Self == null || pass.Player == null || !pass.AdmittedAxes.Contains(DesireAxis.Aggression))
                return null;
            MissionIntent operation = AggressionMissionLayer.LiveAttackOperation(pass.ActiveIntents);
            AttackIntent a = operation?.Attack;
            if (a == null || !a.Preparation || a.Phase != AttackMissionPhase.Gather || !a.PrimaryArmyId.HasValue
                || a.TargetRoster == null || a.TargetRoster.Count == 0)
                return null;
            ArmyData host = OwnWorld.Of(pass.Player).Army(a.PrimaryArmyId.Value);
            if (host == null)
                return null;
            Dictionary<string, int> missing = MissingHandOrDeckKeys(a, host);
            if (missing.Count == 0)
                return null;
            var cards = (pass.Snap.Self.PoolCards
                    ?? System.Array.Empty<(CardDefinition Card, CardDefinition Equipment, CardDefinition Mutator, bool InHand)>())
                .Where(p => p.Card != null && p.Card.resourceCost != null
                    && missing.ContainsKey(StrikeRoster.CardKey(p.Card))
                    && AggressionDemandEvaluator.FundableWithinDeckHorizon(pass.Snap, p.Card.resourceCost))
                .OrderBy(p => Total(p.Card.resourceCost))
                .ThenBy(p => p.InHand ? 0 : 1)
                .ThenBy(p => p.Card.displayName, System.StringComparer.Ordinal)
                .ToList();
            if (cards.Count == 0)
                return null;
            var cheapest = cards[0];
            return new StrikeSupportNeed
            {
                Operation = operation, Attack = a,
                Primary = pass.Snap.Self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == host.Id),
                Cost = cheapest.Card.resourceCost,
                Source = (cheapest.InHand ? "hand:" : "deck:") + cheapest.Card.displayName,
                RosterKey = StrikeRoster.CardKey(cheapest.Card),
                MissingRosterKeys = missing,
            };
        }

        private static Dictionary<string, int> MissingHandOrDeckKeys(AttackIntent a, ArmyData host) =>
            StrikeRoster.Missing(a.TargetRoster, host.Members)
                .Where(m => !m.IsHero && m.Source != ForceSource.Map)
                .GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.Count());

        internal static void PublishRosterForecast(PhaseAPass pass, StrikeSupportNeed need)
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
            ResourceStarvationRegistry.ReplaceOperationalForecast(pass.Player, pass.Turn, RosterForecastProducer, needs);
        }

        // Phase A materialization: a demand worth at least the host's own demand keeps its priority.
        internal static bool LeavesRosterFunding(PhaseAPass pass, AxisDemand demand, MaterializationPlan plan,
            out string why)
        {
            why = null;
            StrikeSupportNeed need = pass?.RosterFunding;
            if (need == null || demand == null || plan == null)
                return true;
            int host = need.Attack.PrimaryArmyId ?? -1;
            if (demand.AttackFistArmyId == host)
                return true;
            float hostValue = (pass.Demands ?? System.Array.Empty<AxisDemand>())
                .Where(d => d != null && d.AttackFistArmyId == host && d.AttackFistIsPreparationHost)
                .Select(d => d.Value).DefaultIfEmpty(float.NegativeInfinity).Max();
            if (demand.Value >= hostValue)
                return true;
            return LeavesRosterFunding(pass.Player, pass.Root, pass.Ctx, need, plan.FundingCost,
                plan.BaseCardInHand, plan.Deploy.Army?.Id ?? plan.DevelopmentUpgrade?.RecipientArmyId,
                $"demand {demand.Value.ToString("0.##", CultureInfo.InvariantCulture)} < host "
                + hostValue.ToString("0.##", CultureInfo.InvariantCulture), out why);
        }

        // THE roster reserve rule for every spender (Phase A upgrades, Phase B cards, equipment,
        // facilities, repairs). `card` — the card the spend plays, if any; `recipientArmyId` — the
        // army it goes into, if known (the host itself is always allowed).
        internal static bool LeavesRosterFunding(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            StrikeSupportNeed need, ResourceCost cost, CardData card, int? recipientArmyId, string spender,
            out string why)
        {
            why = null;
            if (need?.Cost == null || player == null || !need.Attack.PrimaryArmyId.HasValue)
                return true;
            int host = need.Attack.PrimaryArmyId.Value;
            if (recipientArmyId == host)
                return true;
            // Re-read live: the need ends as soon as the host holds the card or the wait is over.
            ArmyData live = OwnWorld.Of(player)?.Army(host);
            if (live == null || !need.Attack.Preparation || need.Attack.Phase != AttackMissionPhase.Gather
                || need.Attack.TargetRoster == null)
                return true;
            Dictionary<string, int> missing = MissingHandOrDeckKeys(need.Attack, live);
            if (!missing.ContainsKey(need.RosterKey ?? ""))
                return true;
            string key = StrikeRoster.CardKey(card?.Definition);
            if (key != null && missing.TryGetValue(key, out int waited))
            {
                int held = AiHandRegistry.Peek(player)?.Hand?.Count(c => StrikeRoster.CardKey(c?.Definition) == key) ?? 0;
                if (held <= waited)
                {
                    why = $"roster_card_reserved:{need.Operation.IntentKey} {card.Definition.displayName} "
                        + $"is for host #{host} ({spender})";
                    return false;
                }
            }
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int spends = cost.Get(t);
                int reserve = need.Cost.Get(t);
                if (spends <= 0 || reserve <= 0)
                    continue;
                float spendable = StrategicSpendability.SpendableAmount(player, root, ctx, t);
                if (spendable - spends + AiConfigV2.allocatorSliceEpsilon < reserve)
                {
                    why = $"leaves_roster_funding:{need.Operation.IntentKey} {t} "
                        + $"{spendable.ToString("0.##", CultureInfo.InvariantCulture)} - {spends} < reserve {reserve} "
                        + $"for {need.Source} ({spender})";
                    return false;
                }
            }
            return true;
        }

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
            // 2026-11-09 (owner, match P6a) — a single-use bomb for an own aircraft is a strike
            // source too: in hand, or a Production source (it needs no hand slot until made).
            List<CardData> handBombs = pass.Hand.Hand.Where(c => GroundCombatAirSupport.IsAircraftBomb(c?.Definition)).ToList();
            List<GenerationStep> generatedBombs = pass.Reservation.CanGenerateMore
                ? (pass.Snap.Development?.ForecastSources ?? System.Array.Empty<GenerationStep>())
                    .Where(g => GroundCombatAirSupport.IsAircraftBomb(g?.CardDef)).ToList()
                : new List<GenerationStep>();
            if (handSources.Count == 0 && generatedSources.Count == 0
                && handBombs.Count == 0 && generatedBombs.Count == 0)
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
            foreach (CardData c in handBombs)
                if (BombCarrierStrikes(pass, c.Definition, requests))
                    costs.Add((c.EffectivePlayResourceCost, "hand-bomb:" + c.Definition.displayName));
            foreach (GenerationStep g in generatedBombs)
                if (BombCarrierStrikes(pass, g.CardDef, requests))
                    costs.Add((StrategicCardEvaluator.AddResourceCosts(g.CardDef.resourceCost,
                        g.ChallengeCard?.resourceCost), "generate-bomb:" + g.CardDef.displayName));
            if (costs.Count == 0)
                return null;
            var cheapest = costs.OrderBy(x => Total(x.Cost)).ThenBy(x => x.Source, System.StringComparer.Ordinal).First();
            return new StrikeSupportNeed
            {
                Operation = operation, Attack = a, Primary = primary, Forecast = forecast,
                Cost = cheapest.Cost, Source = cheapest.Source, Requests = requests,
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
            // The bomb's own attachment is the strike being prepared, not a competitor for it.
            if (GroundCombatAirSupport.IsAircraftBomb(play?.Card?.Definition) && need?.Source?.Contains("bomb:") == true)
            { why = null; return true; }
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
            onSite.AddRange(BombPlaysOnSite(pass, need));
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

        // Some own aircraft, carrying `bomb`, serves a strike on the site from where it stands.
        private static bool BombCarrierStrikes(PhaseAPass pass, Game.Cards.CardDefinition bomb,
            IReadOnlyList<CombatAirSupportRequest> requests) =>
            OwnWorld.Of(pass.Player).Armies.Where(a => a != null)
                .SelectMany(a => a.Members)
                .Any(u => u != null && u.IsAviation
                    && GroundCombatAirSupport.BombedService(pass.Snap, pass.Player, pass.Ctx, u, bomb, requests).HasValue);

        // The hand bombs' attachment plays whose recipient then serves the operation's site.
        private static IEnumerable<NonCombatCardPlayer.NonCombatPlay> BombPlaysOnSite(PhaseAPass pass,
            StrikeSupportNeed need)
        {
            foreach (CardData card in pass.Hand.Hand.Where(c => GroundCombatAirSupport.IsAircraftBomb(c?.Definition)).ToList())
                foreach (NonCombatCardPlayer.NonCombatPlay play in NonCombatCardPlayer.EquipmentPlaysFor(card,
                             pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx))
                    if (play.EquipHost != null && GroundCombatAirSupport.BombedService(pass.Snap, pass.Player, pass.Ctx,
                            play.EquipHost, card.Definition, need.Requests).HasValue)
                        yield return play;
        }

        // An aircraft that strikes ground (a missile or a bomber), never a recon-only frame.
        private static bool IsStrikeCard(Game.Cards.CardDefinition def) =>
            def?.isAviation == true
            && Game.Combat.ExplosionAttack.AttackPower(def.attack, def.grantedAbilities) > 0;
    }
}
