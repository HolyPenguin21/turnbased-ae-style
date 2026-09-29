using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Physical continuation of an already-started Strike sortie. Ground missions may create the
    // first support opportunity, but once the wing is airborne this state owns repeat strikes and
    // recovery. Nothing here reads or mutates Attack/Raid intent lifecycle.
    internal static class AviationStrikeContinuation
    {
        internal static List<ArmyData> FindMandatoryContinuations(PlayerSetupData player, int turn)
        {
            var result = new List<ArmyData>();
            if (player == null)
                return result;

            foreach (AirSortie sortie in AirSortieRegistry.For(player).ToList())
            {
                if (sortie == null || sortie.Kind != AirSortieKind.Strike)
                    continue;

                ArmyData wing = sortie.Army;
                bool live = wing != null && wing.Owner == player
                    && ArmyRegistry.AllForOwner(player).Contains(wing)
                    && AviationRules.IsValidAirArmy(wing) && wing.Controller != null;
                if (!live)
                {
                    AirSortieRegistry.Remove(player, sortie);
                    continue;
                }

                if (!sortie.Outbound && !sortie.OnStation && wing.Hex.Equals(sortie.LandingHex))
                {
                    AirSortieRegistry.Remove(player, sortie);
                    continue;
                }

                if (AviationObligationStallRegistry.IsStalled(player, turn, wing.Id))
                    continue;

                if (sortie.OnStation)
                {
                    bool targetRemains = HasStrikeTarget(sortie, player);
                    bool attackReady = wing.Members.Any(u => u != null && !u.HasAirAttackedThisTurn);
                    int safeEnds = AviationRange.SafeUnlandedEndsRemaining(wing);

                    // A wing that already attacked this turn may deliberately remain on station
                    // while it still has endurance. It needs no movement command this turn.
                    if (targetRemains && safeEnds > 0 && !attackReady)
                        continue;

                    // Otherwise it either has a new-turn strike to resolve, or must start home.
                    if ((targetRemains && attackReady) || wing.CurrentMovement > 0)
                        result.Add(wing);
                    continue;
                }

                if (wing.CurrentMovement > 0)
                    result.Add(wing);
            }

            return result.Distinct().OrderBy(a => a.Id).ToList();
        }

        internal static IEnumerator ExecuteStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ArmyData wing, System.Action<bool> setChanged)
        {
            bool changed = false;
            if (player == null || root == null || ctx?.Map == null || wing == null)
            {
                setChanged?.Invoke(false);
                yield break;
            }

            AirSortie sortie = AirSortieRegistry.ForArmy(player, wing);
            if (sortie == null || sortie.Kind != AirSortieKind.Strike)
            {
                setChanged?.Invoke(false);
                yield break;
            }

            if (!sortie.Outbound && !sortie.OnStation && wing.Hex.Equals(sortie.LandingHex))
            {
                AirSortieRegistry.Remove(player, sortie);
                setChanged?.Invoke(false);
                yield break;
            }

            if (sortie.OnStation)
            {
                bool targetRemains = HasStrikeTarget(sortie, player);
                bool attackReady = wing.Members.Any(u => u != null && !u.HasAirAttackedThisTurn);
                if (targetRemains && attackReady)
                {
                    AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
                    var strike = new AviationCombatPresenter.AirStrikeResult();
                    yield return AviationActions.ResolveStationaryStrike(
                        presenter, wing, sortie.StrikePolicy, strike);
                    changed |= strike.Attacked;

                    wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == wing.Id);
                    if (wing == null)
                    {
                        setChanged?.Invoke(changed);
                        yield break;
                    }

                    targetRemains = HasStrikeTarget(sortie, player);
                    if (targetRemains && AviationRange.SafeUnlandedEndsRemaining(wing) > 0)
                    {
                        // Attack does not consume MP. A dedicated strike may nevertheless remain
                        // over its target deliberately to exploit the next endurance window.
                        AiDebugLog.Write($"[AI][V2][Aviation][Strike] wing #{wing.Id} holds on station "
                            + $"at ({wing.Hex.Q},{wing.Hex.R}); safeEnds="
                            + $"{AviationRange.SafeUnlandedEndsRemaining(wing)}");
                        setChanged?.Invoke(changed);
                        yield break;
                    }

                    sortie.OnStation = false;
                    sortie.Outbound = false;
                    sortie.TargetHex = sortie.LandingHex;
                    setChanged?.Invoke(true);
                    yield break;
                }

                // No target remains, or this is the final safe turn: go home. If the wing already
                // attacked this turn with endurance left, FindMandatoryContinuations does not call
                // us, so this branch never steals the deliberate hold.
                sortie.OnStation = false;
                sortie.Outbound = false;
                sortie.TargetHex = sortie.LandingHex;
                changed = true;
            }

            if (wing.CurrentMovement <= 0)
            {
                setChanged?.Invoke(changed);
                yield break;
            }

            AiDecision move = AiAirSortiePlanner.ContinueSortie(
                player, root, ctx, sortie, "AirStrike",
                sortie.Outbound ? "flies toward the strike target" : "returns from strike",
                AiConfig.airStrikeContinuationScore);
            if (move == null)
            {
                setChanged?.Invoke(changed);
                yield break;
            }

            bool enteringAction = sortie.Outbound && move.TargetHex.Equals(sortie.ActionHex);
            if (enteringAction)
                wing.PendingAirStrikePolicy = sortie.StrikePolicy;

            HexCoord before = wing.Hex;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
            wing.PendingAirStrikePolicy = null;

            wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == wing.Id);
            if (wing == null)
            {
                setChanged?.Invoke(true);
                yield break;
            }
            changed |= !wing.Hex.Equals(before);

            if (sortie.Outbound && wing.Hex.Equals(sortie.ActionHex))
            {
                bool attacked = wing.LastAirStrikeHex.HasValue
                    && wing.LastAirStrikeHex.Value.Equals(sortie.ActionHex)
                    && wing.LastAirStrikeAttacked;
                bool targetRemains = HasStrikeTarget(sortie, player);
                if (attacked && targetRemains
                    && AviationRange.SafeUnlandedEndsRemaining(wing) > 0)
                {
                    sortie.OnStation = true;
                    sortie.Outbound = false;
                    sortie.TargetHex = sortie.ActionHex;
                }
                else
                {
                    sortie.OnStation = false;
                    sortie.Outbound = false;
                    sortie.TargetHex = sortie.LandingHex;
                }
            }

            if (!sortie.Outbound && !sortie.OnStation && wing.Hex.Equals(sortie.LandingHex))
                AirSortieRegistry.Remove(player, sortie);

            setChanged?.Invoke(changed);
        }

        internal static bool HasStrikeTarget(AirSortie sortie, PlayerSetupData owner)
        {
            if (sortie == null || owner == null)
                return false;
            List<ArmyData> targets = AviationCombatPresenter.FindAirStrikeTargetsAt(
                sortie.ActionHex, owner, sortie.StrikePolicy.ExactTargetArmyId);
            return targets.Sum(a => a?.Members?.Count ?? 0) > sortie.StrikePolicy.MinimumSurvivors;
        }
    }
}
