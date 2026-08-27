using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // The map/UI adapter for aviation combat — deliberately separate from the pure rules in
    // AntiAirRules/AviationRules, same split HexSelectionController.Movement.cs's own resolver
    // already follows for ordinary ground contact. Wired into ArmyController.MoveAlong's optional
    // resolveStepAsync hook (see HexSelectionController.Movement.cs's own IssueMoveOrder) for BOTH
    // an air army's own steps (AA reactions, then an air strike) and a ground army's steps when it
    // carries an AA-tagged member (an opportunity shot at any enemy air army newly in range) — one
    // shared entry point, ResolveStep, picks which of those applies.
    public class AviationCombatPresenter : MonoBehaviour
    {
        [SerializeField] private BattleAttackPopupUI attackPopup;
        [SerializeField] private AaChoicePopupUI aaChoicePopup;
        [SerializeField] private HexSelectionController hexSelection;

        // Called once per hex `mover` actually enters, from ArmyController.MoveRoutine — see that
        // method's own comment on why `outcome` is a mutated scratch object rather than a return
        // value. A plain ground army with no AA member and no aviation of its own returns
        // immediately, same as if no resolver had been wired at all.
        public IEnumerator ResolveStep(ArmyData mover, HexCoord hex, ArmyController.StepResolutionOutcome outcome)
        {
            if (mover == null)
                yield break;

            if (AviationRules.IsAirArmy(mover))
                yield return ResolveAirArmyStep(mover, hex, outcome);
            else if (mover.Members.Exists(member => AntiAirRules.TryGetRadius(member, out _)))
                yield return ResolveGroundAaStep(mover, hex);
        }

        // 1. Every enemy AA unit in range reacts, in order, to the air army that just landed here.
        // 2. If it survives, whatever enemy content now shares this hex gets struck once each by
        //    every aircraft that hasn't already attacked this turn.
        private IEnumerator ResolveAirArmyStep(ArmyData airArmy, HexCoord hex, ArmyController.StepResolutionOutcome outcome)
        {
            foreach (AaReaction reaction in AntiAirRules.CollectEntryReactions(airArmy, hex))
            {
                if (airArmy.Members.Count == 0)
                    break;
                yield return RunAaReaction(reaction);
            }

            if (airArmy.Members.Count == 0)
            {
                outcome.StopMovement = true;
                hexSelection?.DeleteArmyIfEmptied(airArmy);
                yield break;
            }

            // hex here is the just-entered hex (from ArmyController.MoveRoutine's own per-step
            // loop) — airArmy.Hex/Data.Hex is NOT updated until the whole move finishes (see
            // ArmyController.CurrentHex's own comment), so during an in-progress multi-hex move it
            // still names the ORIGIN hex, not this one. Passing it explicitly (rather than letting
            // ResolveAirStrikeAtCurrentHex read airArmy.Hex itself) is what makes a strike at hex 3
            // of a 5-hex path actually resolve against hex 3's own defenders instead of hex 0's.
            var result = new AirStrikeResult();
            yield return ResolveAirStrikeAtCurrentHex(airArmy, hex, result);
            // Repeat-strike bookkeeping (AiAggressionPlanner.TryContinueAirStrikeTask) — overwritten
            // on every hex actually entered, so by the time a caller reads it back the values always
            // describe the LAST hex resolved, which is exactly wherever this move actually stopped.
            airArmy.LastAirStrikeHex = hex;
            airArmy.LastAirStrikeAttacked = result.Attacked;
        }

        // Coroutines can't return a value directly — same "mutable scratch instance" pattern
        // ArmyController.StepResolutionOutcome already uses. Attacked is true only once an aircraft
        // ACTUALLY fired (RunAirStrike below); merely finding a non-empty target list is not enough
        // (every aircraft may already have HasAirAttackedThisTurn set from an earlier hex this same
        // turn — see AviationCombatPresenter.ResolveAirArmyStep's own comment on stale hexes, and
        // AiAggressionPlanner.TryContinueAirStrikeTask's own comment on why "reached the hex" and
        // "struck the hex" must never be conflated).
        public sealed class AirStrikeResult
        {
            public bool Attacked;
        }

        // The actual "strike whatever enemy content shares this hex" step, factored out (2026-08-26
        // repeat-strike spec, point 5) so a multi-turn AirStrike's own repeat attack next turn — the
        // army already sitting on ActionHex, HasAirAttackedThisTurn freshly reset by
        // GameTurnController.ReplenishMoveForOwner — runs through the EXACT same mechanic as the
        // first strike (RunAirStrike, HasAirAttackedThisTurn gating, combat resolver, defender/empty-
        // army cleanup), never a second implementation. Deliberately does NOT re-run AA entry
        // reactions above — those trigger only on actually ENTERING the hex (AntiAirRules.
        // CollectEntryReactions), which a repeat strike from an army already parked there never
        // does. Public so Game.Aviation.AviationActions.ResolveStationaryStrike (the shared,
        // AI-and-human aviation action) can call it directly, the one case a caller needs this
        // presenter without a move happening first (see HexSelectionController.
        // AviationCombatPresenter's own comment). `hex` is the army's real current hex — see
        // ResolveAirArmyStep's own comment on why this is never read off airArmy.Hex internally.
        public IEnumerator ResolveAirStrikeAtCurrentHex(ArmyData airArmy, HexCoord hex, AirStrikeResult result = null)
        {
            if (airArmy == null || airArmy.Members.Count == 0)
                yield break;
            List<ArmyData> targets = FindAirStrikeTargetsAt(hex, airArmy.Owner);
            if (targets.Count == 0)
                yield break;

            yield return RunAirStrike(airArmy, targets, result);

            // AiMapMemory's own EnemySightings snapshot is otherwise only refreshed by
            // VisionSystem.VisibleContentChanged (see that event's own comment) — a surviving
            // defender that merely lost HP, or lost a squadmate without the whole army dying,
            // never routes through ArmyRegistry.Unregister (a full-army kill already renotifies
            // this same hex on its own — see DeleteArmyIfEmptied → ArmyRegistry.Unregister), so
            // without this the AI kept re-scoring a follow-up raid against the pre-strike roster
            // (project owner's own report: a raid forecast that should have risen 28% → 52% after
            // a supporting strike stayed at 28%). Routed through the SAME shared observation
            // mechanism every other content change already uses, once for the whole strike (not
            // per aircraft) — this presenter never touches AiMapMemory's own dictionaries
            // directly.
            VisionSystem.NotifyContentChanged(hex);
        }

        // The ground-mover mirror: this army carries at least one AA-tagged member, so every
        // enemy air army newly in ITS range gets offered as a shot (see AntiAirRules.
        // CollectGroundOpportunities) — never stops ground movement itself, just offers the shot.
        private IEnumerator ResolveGroundAaStep(ArmyData groundArmy, HexCoord hex)
        {
            foreach (AaReaction reaction in AntiAirRules.CollectGroundOpportunities(groundArmy, hex))
                yield return RunAaReaction(reaction);
        }

        private IEnumerator RunAaReaction(AaReaction reaction)
        {
            ArmyData airArmy = reaction.AirArmy;
            if (airArmy.Members.Count == 0)
                yield break;

            // Human gets Attack/Skip; AI (or a missing popup reference) always attacks — per the
            // design's own "AI always attacks" rule.
            bool attack = true;
            if (reaction.AaArmy.Owner != null && reaction.AaArmy.Owner.IsHuman && aaChoicePopup != null)
            {
                bool decided = false;
                aaChoicePopup.Show(reaction.AaUnit, airArmy,
                    onAttack: () => { attack = true; decided = true; },
                    onSkip: () => { attack = false; decided = true; });
                yield return new WaitUntil(() => decided);
            }

            // Recorded whether fired or skipped (see AntiAirState's own comment) — a skip must
            // still suppress a re-prompt for this SAME air army re-entering the same radius later
            // this same turn.
            AntiAirState.RecordPrompted(reaction.AaUnit, airArmy.Id, attack);
            if (!attack || attackPopup == null)
                yield break;

            UnitData target = PickRandomSurvivor(airArmy);
            if (target == null)
                yield break;

            UnitData aaHero = reaction.AaArmy.Members.Find(unit => unit.IsHero);
            bool resolved = false;
            attackPopup.Begin(reaction.AaUnit, aaHero, target, null, null, null,
                onResolved: (damage, died) =>
                {
                    resolved = true;
                    if (died)
                    {
                        airArmy.Members.Remove(target);
                        Game.Map.StealthSystem.OnUnitRemoved(target);
                    }
                },
                // The manual's AA rule: double dice against an air target, never against the
                // defender's own Defense — see BattleAttackPopupUI.Begin's own attackerPoolSize.
                attackerPoolSize: reaction.AaUnit.Attack * 2);
            yield return new WaitUntil(() => resolved);
            hexSelection?.RestackArmiesOn(airArmy.Hex, null);
        }

        private IEnumerator RunAirStrike(ArmyData airArmy, List<ArmyData> targetArmies, AirStrikeResult result = null)
        {
            foreach (UnitData aircraft in airArmy.Members.ToList())
            {
                if (aircraft.HasAirAttackedThisTurn || airArmy.Members.Count == 0)
                    continue;

                List<(UnitData unit, ArmyData army)> pool = CollectStrikeTargets(targetArmies, airArmy.Owner);
                if (pool.Count == 0)
                    break; // nothing left standing on this hex for the rest of the roster either

                (UnitData target, ArmyData targetArmy) = pool[Random.Range(0, pool.Count)];
                aircraft.HasAirAttackedThisTurn = true;
                // A directed strike on a personally-detected hidden unit lifts its stealth
                // (§7) — no effect on a non-hidden target.
                Game.Map.StealthSystem.ExitStealth(target);
                if (result != null)
                    result.Attacked = true;

                UnitData defenderHero = target.IsHero ? target : targetArmy.Members.Find(unit => unit.IsHero);
                // A targeted hero's own Fate stat is its real pool (its Defense is 0 — see
                // BattleAttackPopupUI.Begin's own comment on why a hero target needs this
                // override); an ordinary unit target just rolls its plain Defense as usual.
                int? defenderPoolOverride = target.IsHero ? target.FateMax : (int?)null;

                bool resolved = false;
                attackPopup.Begin(aircraft, null, target, defenderHero, null, null,
                    onResolved: (damage, died) =>
                    {
                        resolved = true;
                        if (died)
                        {
                            targetArmy.Members.Remove(target);
                            Game.Map.StealthSystem.OnUnitRemoved(target);
                        }
                    },
                    defenderPoolSize: defenderPoolOverride);
                yield return new WaitUntil(() => resolved);

                hexSelection?.RestackArmiesOn(targetArmy.Hex, null);
                if (targetArmy.Members.Count == 0)
                    hexSelection?.DeleteArmyIfEmptied(targetArmy);
            }
        }

        private static List<(UnitData, ArmyData)> CollectStrikeTargets(List<ArmyData> targetArmies, PlayerSetupData striker)
        {
            var pool = new List<(UnitData, ArmyData)>();
            foreach (ArmyData army in targetArmies)
                if (army.Members.Count > 0)
                    foreach (UnitData unit in army.Members)
                        // A unit hidden from the striker is neither a target nor collateral
                        // (see Game.Map.StealthSystem) — unless the striker has personally
                        // detected it, in which case IsHiddenFrom is already false.
                        if (!Game.Map.StealthSystem.IsHiddenFrom(unit, striker))
                            pool.Add((unit, army));
            return pool;
        }

        // Ground armies/garrison AND an enemy air army sharing the same hex alike (per the
        // design: "against an unlanded enemy air army, choose a random aircraft; no dice
        // doubling") — an airfield's own STORED aircraft are never a strike target, only ever
        // emptied by capturing the Base building underneath it (see
        // AviationActions.ReturnAircraftToDeck).
        // Public: also the move-arrow preview's own query for whether a hovered destination holds
        // a real target (see HexSelectionController.Movement.cs's own ShowPathArrow) — same
        // targets set, so the arrow's red/green never disagrees with what actually gets struck.
        public static List<ArmyData> FindAirStrikeTargetsAt(HexCoord hex, PlayerSetupData owner)
        {
            var result = new List<ArmyData>();
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner == owner || army.Owner == null || army.IsPrison || army.Members.Count == 0)
                    continue;
                if (AviationRules.IsAirfield(army))
                    continue;
                // Individual stealth (see Game.Map.StealthSystem): an army with no member
                // visible to the striking player is not an air-strike target at all — this
                // also keeps the move-arrow preview from turning red over a hidden-only hex.
                if (!Game.Map.StealthSystem.HasAnyTargetableMember(army, owner))
                    continue;
                result.Add(army);
            }
            return result;
        }

        private static UnitData PickRandomSurvivor(ArmyData airArmy)
        {
            return airArmy.Members.Count > 0 ? airArmy.Members[Random.Range(0, airArmy.Members.Count)] : null;
        }
    }
}
