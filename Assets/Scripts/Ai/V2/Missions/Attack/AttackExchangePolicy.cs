using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;

namespace Game.Ai.V2
{
    // The expected trade of one assault on a multi-army hex.
    internal readonly struct AttackExchange
    {
        // Chance to win the first battle (the strongest defending army, fought first).
        internal readonly float FirstWin;
        // Chance to win every battle: the capture the ordinary gates read.
        internal readonly float CaptureWin;
        // Expected enemy power destroyed: sum over the battles of P(won through it) x its power.
        internal readonly float EnemyKilled;
        // Own power put at risk: all of it unless every battle is won, the wounds of a full win.
        internal readonly float OwnAtRisk;
        internal readonly int Battles;
        internal readonly bool Favorable;

        internal AttackExchange(float firstWin, float captureWin, float enemyKilled, float ownAtRisk,
            int battles, bool favorable)
        {
            FirstWin = firstWin;
            CaptureWin = captureWin;
            EnemyKilled = enemyKilled;
            OwnAtRisk = ownAtRisk;
            Battles = battles;
            Favorable = favorable;
        }

        internal string Describe() => Battles < 2 ? "exchange=n/a"
            : string.Format(CultureInfo.InvariantCulture,
                "exchange={0} first={1:0.00} capture={2:0.00} kill={3:0.#} risk={4:0.#} battles={5}",
                Favorable ? "favorable" : "unfavorable", FirstWin, CaptureWin, EnemyKilled, OwnAtRisk, Battles);
    }

    // ===========================================================================================
    //  2026-11-02 (owner decision) — AN ASSAULT MAY BE A TRADE, NOT ONLY A CAPTURE.
    //
    //  The rules take a hex held by several armies one battle at a time, strongest first, the
    //  attacker's wounds carried on (WorthIt.EstimateSequential). Judged only as a capture, a
    //  player who parks every army on its stronghold can never be attacked (playtest: 158 power /
    //  23 units on a Citadel against an 8-slot fist of ~80): both AIs idled and stockpiled.
    //
    //  The trade is the same estimator asked per prefix of the fight order: P(win battles 1..k)
    //  for every k. The march is a FAVORABLE trade when
    //    · the first battle clears the ordinary start gate (AttackWinChanceGate) — the strongest
    //      army really falls; and
    //    · the enemy power expected destroyed exceeds the own power put at risk, counted
    //      pessimistically: the whole fist is lost unless every battle is won (a battle-time retreat
    //      that saves bodies is a bonus, never assumed).
    //  No timer, no new threshold. A single defending army is a plain capture (no trade to make).
    //  Read by readiness (AttackPreparationPolicy.AssessReadiness), the march verdict
    //  (AttackMarchPolicy.Assess) and the mission planner when no capture plan exists.
    // ===========================================================================================
    internal static class AttackExchangePolicy
    {
        internal static AttackExchange Assess(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            WorthIt.SideCommander commander, IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus)
        {
            List<WorthIt.DefendingArmy> fighting = AttackTacticalOpportunity.FightingArmies(opposition);
            if (attackers == null || attackers.Count == 0 || fighting.Count < 2
                || !attackers.Any(AiArmyRoles.IsGroundCombatBody))
                return new AttackExchange(0f, 0f, 0f, 0f, fighting.Count, false);

            // The rules' fight order, the one EstimateSequential takes (WorthIt.FightOrder).
            List<WorthIt.DefendingArmy> order = WorthIt.FightOrder(attackers, commander, fighting, hexBonus);

            var prefix = new List<WorthIt.DefendingArmy>(order.Count);
            float firstWin = 0f, killed = 0f;
            WorthIt.BattleEstimate through = default;
            foreach (WorthIt.DefendingArmy army in order)
            {
                prefix.Add(army);
                through = WorthIt.EstimateSequential(attackers, commander, prefix, hexBonus);
                if (prefix.Count == 1)
                    firstWin = through.WinChance;
                killed += through.WinChance * AiPower.EffectiveArmyPowerFromProfiles(army.Units.ToList());
            }
            float own = AiPower.EffectiveArmyPowerFromProfiles(attackers);
            float capture = through.WinChance;
            float atRisk = own * (1f - capture) + own * capture * (1f - through.ExpectedSurvivingHpRatioOnWin);
            bool favorable = firstWin >= GroundCombatAdmissionPolicy.AttackWinChanceGate && killed > atRisk;
            return new AttackExchange(firstWin, capture, killed, atRisk, order.Count, favorable);
        }

        // The trade plan for a target no capture plan takes: the same request without the capture
        // gate and coverage (the fist the planner would field anyway), kept only when that fist's
        // departing roster makes a favorable trade. One shape for the mission planner and Demand.
        // `request` is not modified.
        internal static GroundCombatAssemblyPlan TradePlan(WorldSnapshot snap, GroundCombatAssemblyRequest request,
            out AttackExchange trade)
        {
            trade = default;
            if (snap?.Self == null || request == null)
                return null;
            var free = new GroundCombatAssemblyRequest
            {
                Opposition = request.Opposition,
                WinChanceGate = 0f,
                RequireCoverage = false,
                PreferredPrimaryArmyId = request.PreferredPrimaryArmyId,
                PinToPreferred = request.PinToPreferred,
                ExcludedArmyIds = request.ExcludedArmyIds,
                StagingHex = request.StagingHex,
                AllowSameHexAssembly = request.AllowSameHexAssembly,
                RequireReadyThisTurn = request.RequireReadyThisTurn,
                PreserveDepartureRoster = request.PreserveDepartureRoster,
                RequiresSeparateSupportActor = request.RequiresSeparateSupportActor,
                DefenderHexDefenseBonus = request.DefenderHexDefenseBonus,
                MinimumArmyPower = request.MinimumArmyPower,
                AllowDecisivePinnedDefender = request.AllowDecisivePinnedDefender,
                AcceptPlan = request.AcceptPlan,
            };
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap, free);
            if (plan?.Feasible != true)
                return null;
            var departing = GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan);
            ArmySnapshot fist = departing.TryGetValue(plan.BaseArmyId, out ArmySnapshot d) ? d
                : snap.Self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == plan.BaseArmyId);
            if (fist == null)
                return null;
            trade = Assess(fist.Members, fist.Commander, request.Opposition, request.DefenderHexDefenseBonus);
            return trade.Favorable ? plan : null;
        }
    }
}
