using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // The turn's operational mission loop has had its chance to fund and run every continuing
    // operation. From then on (Phase B, housekeeping, reactions of the same turn) the Hard-operation
    // continuation protection no longer holds AP: an operation still standing was not funded or
    // could not step, and freezing its AP would only waste it. Turn-stamped; lazily resets.
    internal static class OperationContinuationWindow
    {
        private static readonly Dictionary<PlayerSetupData, int> SettledTurn =
            new Dictionary<PlayerSetupData, int>();

        internal static void EndTurn(PlayerSetupData player, int turn)
        {
            if (player == null) return;
            if (SettledTurn.TryGetValue(player, out int settled) && settled == turn)
                SettledTurn.Remove(player);
            if (MobilizationOpenTurn.TryGetValue(player, out int mobilized) && mobilized == turn)
                MobilizationOpenTurn.Remove(player);
        }

        internal static void Settle(PlayerSetupData player, int turn)
        {
            if (player != null)
                SettledTurn[player] = turn;
        }

        internal static bool IsSettled(PlayerSetupData player, int turn) =>
            player != null && SettledTurn.TryGetValue(player, out int t) && t == turn;

        // Attack mobilization gate of this turn (AttackForceReadiness.MobilizationOpen on the
        // turn's scan), stamped by the pipeline: the first preparation step's AP hold reads it.
        private static readonly Dictionary<PlayerSetupData, int> MobilizationOpenTurn =
            new Dictionary<PlayerSetupData, int>();

        internal static void SetMobilizationOpen(PlayerSetupData player, int turn, bool open)
        {
            if (player == null)
                return;
            if (open) MobilizationOpenTurn[player] = turn;
            else MobilizationOpenTurn.Remove(player);
        }

        internal static bool IsMobilizationOpen(PlayerSetupData player, int turn) =>
            player != null && MobilizationOpenTurn.TryGetValue(player, out int t) && t == turn;

        // Mobilization hysteresis (project owner, 2026-10-02): the gate is razor-thin (a field strike
        // of 63.92 against a 63.70 bar) and the preparation's own first step moves bodies out of the
        // measured force, so a gate that opened for one pass closed again and the preparation
        // started four turns late (Thane T13 -> T17). Once a preparation step is PROPOSED from a
        // genuinely open gate, the gate is treated as open for the next MobilizationHoldTurns turns.
        internal const int MobilizationHoldTurns = 2;
        private static readonly Dictionary<PlayerSetupData, int> MobilizationHoldUntil =
            new Dictionary<PlayerSetupData, int>();

        internal static void HoldMobilization(PlayerSetupData player, int fromTurn)
        {
            if (player != null)
                MobilizationHoldUntil[player] = fromTurn + MobilizationHoldTurns;
        }

        internal static bool IsMobilizationHeld(PlayerSetupData player, int turn) =>
            player != null && MobilizationHoldUntil.TryGetValue(player, out int until) && turn <= until;

        // Match-start reset (CitadelSetupController), alongside the other V2 registries.
        internal static void ClearAll()
        {
            SettledTurn.Clear();
            MobilizationOpenTurn.Clear();
            MobilizationHoldUntil.Clear();
        }
    }

    // Which strategic reservations ONE action may draw on. Every stage that admits, prices,
    // portfolio-checks and executes the same action reads the SAME value, so they cannot disagree
    // (the demand-side rule is InfrastructureFulfillment.SpendAuthorityFor, exposed as
    // AxisDemand.SpendAuthority):
    //   · Owner — the action's own hold (e.g. the build a builder hero serves), excluded from the sum;
    //   · EconomyCompletesNow — an Economy action that pays off NOW (an on-hex build, a global
    //     resource source put into play) is senior to OTHER builds' EconomyDeferredBuild holds,
    //     which only shield H/E/M/T from non-Economy spending. Every other hold still counts.
    // default == no special authority (the historical behaviour of every other spend).
    // TurnResourceBook.MayDrawOn is the one place these two rules are applied to claims.
    public readonly struct SpendAuthority
    {
        public readonly string Owner;
        public readonly bool EconomyCompletesNow;

        public SpendAuthority(string owner, bool economyCompletesNow)
        {
            Owner = owner;
            EconomyCompletesNow = economyCompletesNow;
        }

        public bool IsNone => Owner == null && !EconomyCompletesNow;
        // Identity for per-authority caches (MaterializationPortfolioSolver own-hold add-back).
        public string Key => (Owner ?? "-") + (EconomyCompletesNow ? "|now" : "");
        public override string ToString() => IsNone ? "none" : Key;
    }

    // ARCH-02 §45/§47 — the ONE owner-aware strategic-spendability seam. Every "can I afford this
    // persistent-resource cost right now" question in the strategic + materialization + reaction
    // paths goes through SpendableAmount, which nets from the raw PlayerRoot stockpile:
    //   · StrategicResourceReservationLedger — the owner-aware explicit reservations (e.g. a
    //     bounded reaction envelope), optionally excluding the caller's own owner key; and
    //   · the unpaid activation of continuing Hard ground-combat operations
    //     (OutstandingOperationContinuationAp, AP only).
    // Mandatory aviation (returns, committed rebases) holds nothing: it is settled before any card
    // play of the turn (AviationObligations).
    public static class StrategicSpendability
    {
        // A Hard ground-combat operation (Raid, Attack, ActiveDefence) is already underway: an
        // intercept has set out, an assault has started, supports are walking to a gather. Phase A
        // card play runs BEFORE the mission allocator funds commitments, so without protection a
        // card could spend the AP the operation needs for its next step — the gap
        // EconomyBuildCompletion closes for a build that finishes now. Protected: the unpaid
        // activation of every army the operation's CURRENT leg moves (Raid Assault -> primary,
        // Reinforcement -> support; Attack Assault -> primary, Gather -> each walking support,
        // Reinforcement -> support, plus a committed Assault's primary still walking to the
        // rendezvous; ActiveDefence Intercept -> primary) that can still act this
        // turn (has not activated, has movement). Lifecycle legs (returns, recovery) own no
        // protection — they carry no operation value (MissionIntent.IsLifecycleLeg). Live:
        // activation, loss, a phase change or the end of the operation releases it at once; after
        // the operational loop settles (OperationContinuationWindow) nothing is held. Only the
        // prefix that fits today's AP is protected, so an unaffordable continuation never freezes
        // the pool.
        // The mission allocator does not read this: it is the owner that funds these operations.
        private static float OutstandingOperationContinuationAp(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx)
            => OutstandingOperationContinuationAp(player, root, ctx, null, out _);

        // Credit belongs only to an actor actually included in the bank's affordable prefix.
        internal static float OperationContinuationCredit(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, int armyId)
        {
            OutstandingOperationContinuationAp(player, root, ctx, armyId, out float credit);
            return credit;
        }

        private static float OutstandingOperationContinuationAp(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, int? creditedArmyId, out float credit)
        {
            credit = 0f;
            if (player == null || root == null || ctx == null
                || OperationContinuationWindow.IsSettled(player, ctx.TurnNumber))
                return 0f;
            var movers = new SortedSet<int>();
            List<MissionIntent> intents = MissionIntentRegistry.GetOrCreate(player).All.ToList();
            foreach (MissionIntent intent in intents)
            {
                if (intent == null || intent.Status != IntentStatus.Active
                    || intent.Funding != CommitmentTier.Hard || intent.IsLifecycleLeg)
                    continue;
                foreach (int id in OperationLegMovers(intent, player))
                    movers.Add(id);
            }
            // 2026-10-01 (user decision, playtest #6): with mobilization open and no Attack
            // operation yet, the FIRST preparation step (MoveHost / CreateHost) has no intent to
            // protect it, so Phase A card play spent its AP six turns running. Hold its typical
            // price until the mission loop settles; cards that build the strike force may use it
            // (InfrastructureFulfillment.SpendAuthorityFor), the allocator always may.
            bool firstPreparationStep = OperationContinuationWindow.IsMobilizationOpen(player, ctx.TurnNumber)
                && AggressionMissionLayer.LiveAttackOperation(intents) == null;
            if (movers.Count == 0 && !firstPreparationStep)
                return 0f;
            // The continuation is the junior claim: it protects only the AP the ledger's committed
            // AP rows (a build completing now, a reaction envelope) leave over, so the two
            // together never exceed the stock (2026-10-07 playtest: 4 completion + 3 continuation
            // against 6 AP).
            float ledgerCommittedAp = 0f;
            foreach (ResourceClaim c in TurnResourceBook.LedgerClaims(player, ctx.TurnNumber,
                         StrategicReservedResource.ActionPoints))
                if (ReservationInvariants.IsCommitted(c.Kind))
                    ledgerCommittedAp += Mathf.Max(0f, c.Amount);
            float available = Mathf.Max(0f, root.ActionPoints - ledgerCommittedAp);
            float protectedAp = 0f;
            var live = new Dictionary<int, ArmyData>();
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
                if (a != null)
                    live[a.Id] = a;
            foreach (int id in movers)
            {
                if (!live.TryGetValue(id, out ArmyData army)
                    || army == null || army.HasActivatedThisTurn || army.CurrentMovement <= 0)
                    continue;
                float activation = Mathf.Max(0, army.ActivationApCost);
                if (protectedAp + activation > available)
                    break;
                protectedAp += activation;
                if (id == creditedArmyId)
                    credit = activation;
            }
            if (firstPreparationStep)
                protectedAp += Mathf.Min(AiConfigV2.attackPreparationFirstStepApHold,
                    Mathf.Max(0f, available - protectedAp));
            return protectedAp;
        }

        private static IEnumerable<int> OperationLegMovers(MissionIntent intent, PlayerSetupData player)
        {
            if (intent.Raid != null)
            {
                RaidIntent r = intent.Raid;
                if (r.Phase == RaidMissionPhase.Assault && r.PrimaryArmyId.HasValue)
                    yield return r.PrimaryArmyId.Value;
                else if (r.Phase == RaidMissionPhase.Reinforcement && r.SupportArmyId.HasValue)
                    yield return r.SupportArmyId.Value;
            }
            else if (intent.Attack != null)
            {
                AttackIntent a = intent.Attack;
                if (a.Phase == AttackMissionPhase.Assault && a.PrimaryArmyId.HasValue)
                    yield return a.PrimaryArmyId.Value;
                else if (a.Phase == AttackMissionPhase.Gather)
                {
                    foreach (int id in a.GatherSupportArmyIds)
                        yield return id;
                    // 2026-10-01 (variant B) — the fetched commander walking to the host is a
                    // leg of the same operation: its next activation is held from card play too.
                    if (a.CommanderArmyId.HasValue)
                        yield return a.CommanderArmyId.Value;
                    // 2026-10-01: a preparation host still walking to its staging Base (not on an
                    // own Base/Citadel yet) is the preparation's own leg too.
                    if (a.Preparation && a.PrimaryArmyId.HasValue
                        && !OnOwnBase(player, a.PrimaryArmyId.Value))
                        yield return a.PrimaryArmyId.Value;
                }
                else if (a.Phase == AttackMissionPhase.Reinforcement && a.SupportArmyId.HasValue)
                {
                    yield return a.SupportArmyId.Value;
                    // 2026-10-04 — a committed Reinforcement's primary still walking along its
                    // route to the rendezvous is the operation's own leg too.
                    if (a.RendezvousHex.HasValue && a.PrimaryArmyId.HasValue
                        && !IsAt(player, a.PrimaryArmyId.Value, a.RendezvousHex.Value))
                        yield return a.PrimaryArmyId.Value;
                }
            }
            else if (intent.ActiveDefence != null)
            {
                ActiveDefenceIntent d = intent.ActiveDefence;
                if (d.Phase == ActiveDefencePhase.Intercept && d.PrimaryArmyId.HasValue)
                    yield return d.PrimaryArmyId.Value;
            }
        }

        private static bool IsAt(PlayerSetupData player, int armyId, HexCoord hex) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(x => x != null && x.Id == armyId)
                ?.Hex.Equals(hex) == true;

        private static bool OnOwnBase(PlayerSetupData player, int armyId)
        {
            ArmyData army = ArmyRegistry.AllForOwner(player).FirstOrDefault(x => x != null && x.Id == armyId);
            BuildingData building = army != null ? BuildingRegistry.FindAt(army.Hex) : null;
            return building != null && building.Owner == player
                && (building.IsBase || building.IsStartingCitadel);
        }

        // The derived (non-ledger) hold: the next step of continuing Hard operations (AP).
        // TurnResourceBook lists it as a claim beside the ledger rows.
        internal static float OperationContinuationHold(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx) =>
            player == null || root == null || ctx == null
                ? 0f : OutstandingOperationContinuationAp(player, root, ctx);

        // Every Spendable* query below is TurnResourceBook.Free under a SpendAuthority: the
        // caller's own hold (excludeOwner) and, for a build completing now, other builds'
        // deferred holds are the only claims it may draw on.
        internal static float SpendableAp(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, string excludeOwner = null) =>
            SpendableAp(player, root, ctx, new SpendAuthority(excludeOwner, false));

        internal static float SpendableAp(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, SpendAuthority authority) =>
            TurnResourceBook.Free(player, root, ctx, StrategicReservedResource.ActionPoints, authority);

        // The canonical primitive: how much of resource `t` may actually be spent this turn.
        internal static float SpendableAmount(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceType t, SpendAuthority authority = default) =>
            TurnResourceBook.Free(player, root, ctx, StrategicResourceReservationLedger.Map(t), authority);

        internal static bool FitsSpendableResources(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceCost cost, SpendAuthority authority) =>
            FitsSpendable(player, root, ctx, cost, authority);

        // spec §6 — a spend candidate must fit SPENDABLE persistent resources, not just raw stock.
        // `excludeOwner` drops the caller's OWN reservation (by its EXACT Owner
        // key, not by the shared Reason) so a re-probe of the reaction that placed a hold does not fail
        // against itself and two owners sharing a Reason can't shadow each other's revalidation.
        internal static bool FitsSpendableResources(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceCost cost, string excludeOwner = null)
            => FitsSpendable(player, root, ctx, cost, new SpendAuthority(excludeOwner, false));

        // The Economy-completion gate: an Economy build that finishes THIS turn (Provisioning's
        // completion stage, its Execution re-check, or a Phase A on-hex build). Every other
        // owner's EconomyDeferredBuild hold is ignored — by contract such a hold belongs to a
        // build that cannot complete this turn and only shields H/E/M/T from non-Economy spending
        // (cards, Phase B, reactions), which keep using FitsSpendableResources. Reaction holds,
        // other builds' proven EconomyBuildCompletion rows and unpaid air-recovery activation
        // still count, so two builds completing in the same turn are ordered by whoever claims first.
        internal static bool FitsSpendableForEconomyCompletion(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ResourceCost cost, string owner)
            => FitsSpendable(player, root, ctx, cost, new SpendAuthority(owner, true));

        private static bool FitsSpendable(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceCost cost, SpendAuthority authority)
        {
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = cost.Get(t);
                if (need <= 0)
                    continue;
                if (SpendableAmount(player, root, ctx, t, authority)
                    + AiConfigV2.allocatorSliceEpsilon < need)
                    return false;
            }
            return true;
        }

        // Physical guard for a whole materialization chain: AP and every persistent resource
        // must fit the SAME owner-aware spendable pool. Raw AP is not available to a discretionary
        // chain while another owner holds an explicit reaction/completion reservation. Callers
        // without a turn-scoped owner retain the historical raw-AP fallback.
        // `excludeOwner` — see AxisDemand.EconomyHeroBuildOwner: a builder-hero chain may draw on its
        // own pending build's hold.
        internal static bool ReservesOkAfterChain(PlayerRoot root, AiTurnContext ctx,
            MaterializationPlan plan, PlayerSetupData player = null, string excludeOwner = null)
            => ReservesOkAfterChain(root, ctx, plan, player, new SpendAuthority(excludeOwner, false));

        internal static bool ReservesOkAfterChain(PlayerRoot root, AiTurnContext ctx,
            MaterializationPlan plan, PlayerSetupData player, SpendAuthority authority)
        {
            if (root == null || plan == null)
                return false;
            float availableAp = player != null && ctx != null
                ? SpendableAp(player, root, ctx, authority)
                : root.ActionPoints;
            if (availableAp - plan.ApCost < 0f)
                return false;

            ResourceCost cost = plan.ResCost;
            if (cost == null)
                return true;

            foreach (ResourceType t in ResourceBundle.All)
                if (SpendableAmount(player, root, ctx, t, authority) < Mathf.Max(0, cost.Get(t)))
                    return false;
            return true;
        }
    }
}

