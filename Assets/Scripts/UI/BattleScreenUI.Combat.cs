using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cameras;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Styles;
using Game.Terrain;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Attack resolution and battle-end handling half of BattleScreenUI — split out purely for
    // file size, same reasoning as HexSelectionController's own multi-file split. Shares this
    // class's fields (_grid/_attacker/_defender/etc.) and the state-machine methods in the main
    // file (EndTurn, Show/Hide, ShowAiThought) automatically via `partial`.
    public partial class BattleScreenUI
    {
        // Same terrain + Base-building defense bonus BeginAttack folds into the real dice roll,
        // exposed for card/detail display so a unit's shown Defense always matches what it would
        // actually roll with right now. Only ever the original battle _defender's own hex — see
        // BeginAttack's own comment for why the attacker (or a mid-exchange role reversal) never
        // gets this.
        public int GetDisplayedDefenseBonus(UnitData unit)
        {
            ArmyData owner = OwningArmy(unit);
            if (owner == null || owner != _defender)
                return 0;
            return Mathf.RoundToInt(WorthIt.HexDefenseBonus(owner.Hex, map));
        }

        private void BeginAttack(UnitData attacker, UnitData defender)
        {
            if (attackPopup == null)
                return;

            // Identity (whose hero, whose Fate, whose movement gets zeroed) must come from actual
            // army membership, NOT from which grid row the attacker currently stands in — a unit
            // can advance into the opposing side's own rows to reach melee range, at which point
            // "row group" and "owning army" disagree. See OwningArmy's own comment and
            // project_battle_ai_bugs_open memory.
            ArmyData attackerArmy = OwningArmy(attacker);
            ArmyData defenderArmy = OwningArmy(defender);
            UnitData attackerHero = OwningHero(attackerArmy);
            UnitData defenderHero = OwningHero(defenderArmy);
            bool defenderIsRetreating = _retreatingArmy != null && defenderArmy == _retreatingArmy;

            // The user's own Siege spec: no separate manual-style Siege Challenge (a whole
            // second dice roll against the building itself) — instead, terrain and a Base-tagged
            // building's own Defense fold straight into THIS SAME Ground Combat roll, defender
            // side only. Terrain always applies (every hex has one); the building only counts
            // when it's a Base (a citadel or player-built Base, see BuildingData.IsBase's
            // own comment) — a bare hero-built extraction facility has nothing built up worth a
            // defense bonus (see HandleBuildingOnArmyDefeat's own note on why those get destroyed
            // outright instead of captured).
            //
            // "Defender side" here means the army that was actually attacked into this battle —
            // _defender (participants[1], see Show/BeginCaptureKillEncounter's own comment: index
            // 0 is always the original mover/hunter) — NOT just whichever army happens to be on
            // the receiving end of THIS particular exchange. Ground Combat rounds alternate: a
            // _defender-side unit taking ITS turn to attack a unit belonging to _attacker must
            // never hand the terrain/building bonus to _attacker just because `defender` (the
            // parameter) is one of its units this time around — only the side that was actually
            // attacked ever holds home-hex terrain, whichever unit happens to be mid-exchange (see
            // the user's own report).
            int defenderTerrainBonus = 0;
            int defenderConstructionBonus = 0;
            if (defenderArmy != null && defenderArmy == _defender && !AviationRules.IsAirArmy(defenderArmy))
            {
                if (map != null && map.TryGetTerrainAt(defenderArmy.Hex, out TerrainTypeEntry terrain))
                    defenderTerrainBonus += terrain.defenseModifier;
                BuildingData defendingBuilding = BuildingRegistry.FindAt(defenderArmy.Hex);
                if (defendingBuilding != null && defendingBuilding.IsBase)
                    defenderConstructionBonus += defendingBuilding.Defense;
            }

            // New rule, per the user: an army with a unit that attacks in the Tactical Battle
            // Module loses its remaining strategic-map movement for the current player turn —
            // applied the moment the attack is declared (popup opened), regardless of the roll's
            // outcome.
            if (attackerArmy != null)
                foreach (UnitData member in attackerArmy.Members)
                    member.MoveCurrent = 0;

            // A hero directly targeted by a Ground Combat attack (heroes are valid attack
            // candidates — see BattleTargetSelector, which doesn't exclude them) defends with a
            // dice pool equal to its own FateMax rather than its plain Defense stat — same "the
            // target hero receives a dice pool equal to his fate" rule the manual already gives
            // the standalone Capture/Kill Challenge (see BeginCaptureKill's own comment), just
            // applied here too since a hero can be attacked mid-battle, not only hunted after it.
            int? defenderPoolSize = defender.IsHero ? defender.FateMax : (int?)null;

            attackPopup.Begin(attacker, attackerHero, defender, defenderHero,
                ResolveCatalog(attacker.Owner)?.logo, ResolveCatalog(defender.Owner)?.logo,
                (damage, died) => OnAttackResolved(attacker, defender, damage, died), ShowAiThought, defenderIsRetreating,
                defenderTerrainBonus, defenderConstructionBonus, defenderPoolSize: defenderPoolSize);
        }

        private void OnAttackResolved(UnitData attacker, UnitData defender, int damage, bool defenderDied)
        {
            // A reaction from the side that TOOK the damage, only when that side is AI-controlled
            // — this is a first-person "how do I feel about this" line, not omniscient narration.
            // A death fires its own more specific UnitDied/EnemyKilled thought instead (see
            // RemoveUnit), so this only runs for a hit that didn't finish the target off.
            if (!defenderDied && damage > 0 && defender.Owner != null && !defender.Owner.IsHuman)
            {
                bool major = defender.HitPointsMax > 0 && defender.HitPointsCurrent <= defender.HitPointsMax / 3f;
                UnitData sideHero = OwningHero(OwningArmy(defender));
                aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                    major ? AiThoughtCategory.DamageTakenMajor : AiThoughtCategory.DamageTakenMinor, attacker?.Name, sideHero != null));
            }

            if (defenderDied)
                RemoveUnit(defender);
            // UnitAbilities.ShockAttack (pg. 40): "results in the target unit being committed
            // for the remainder of the turn if not already committed" — this project has no
            // separate per-round "committed" flag (see MECHANICS_CHECKLIST.md), so the
            // simplified equivalent is dropping the survivor from the rest of THIS round's turn
            // order outright, same as manually Passing it early.
            else if (damage > 0 && attacker.HasAbility(UnitAbilities.ShockAttack))
                SkipRemainingTurnThisRound(defender);
            RefreshGrid();
            if (!CheckBattleEnd())
                EndTurn();
        }

        // Only removes a still-pending (hasn't acted yet this round) entry — a unit whose turn
        // already came and went (index < _turnIndex) is a no-op, matching the manual's own "if
        // not already committed" qualifier. index == _turnIndex never happens here: that slot is
        // always the ATTACKER (the one currently taking the Ground Combat action), never its own
        // target.
        private void SkipRemainingTurnThisRound(UnitData unit)
        {
            if (_turnOrder == null)
                return;
            int index = _turnOrder.IndexOf(unit);
            if (index > _turnIndex)
                _turnOrder.RemoveAt(index);
        }

        private void RemoveUnit(UnitData unit)
        {
            // Captured before army membership is cleared below — needed for the hero-side lookup.
            ArmyData deadSideArmy = OwningArmy(unit);
            ArmyData killerSideArmy = deadSideArmy == _attacker ? _defender : _attacker;

            if (_grid.TryFindPosition(unit, out int row, out int col))
                _grid.Set(row, col, null);
            _attacker?.Members.Remove(unit);
            _defender?.Members.Remove(unit);
            // Drop any stealth/personal-detection state before this UnitData reference goes
            // stale (see Game.Map.StealthSystem).
            Game.Map.StealthSystem.OnUnitRemoved(unit);

            // Whichever side `unit` belonged to reacts with UnitDied; the OTHER side (if
            // AI-controlled) gets a small EnemyKilled reaction instead. Works the same whether
            // `unit` is a regular card or a hero — a dead hero just means OwningHero returns null
            // for that side from now on, which the no-hero phrase fallback already handles.
            UnitData deadSideHero = OwningHero(deadSideArmy);
            UnitData killerSideHero = OwningHero(killerSideArmy);
            if (deadSideArmy?.Owner != null && !deadSideArmy.Owner.IsHuman)
                aiThoughts?.Show(deadSideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.UnitDied, unit.Name, deadSideHero != null));
            if (killerSideArmy?.Owner != null && !killerSideArmy.Owner.IsHuman)
                aiThoughts?.Show(killerSideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.EnemyKilled, unit.Name, killerSideHero != null));

            // Otherwise a unit killed mid-round could still come up "on turn" later this same
            // round (BuildOrder is only recomputed at BeginRound). Removing by index (not
            // List.Remove) so _turnIndex can be corrected if the removed entry sat before it —
            // it never IS _turnIndex itself (the current actor is always the attacker here, a
            // different unit from whichever defender just died).
            if (_turnOrder != null)
            {
                int index = _turnOrder.IndexOf(unit);
                if (index >= 0)
                {
                    _turnOrder.RemoveAt(index);
                    if (index <= _turnIndex)
                        _turnIndex--;
                }
            }
        }

        // Manual's "Battle Results": ends the instant either side has no more combat-capable
        // units (BattleInitiator.IsCombatCapable's own "at least one non-hero unit" rule) — true
        // if the battle just ended (caller should NOT also EndTurn in that case).
        private bool CheckBattleEnd()
        {
            bool attackerAlive = BattleInitiator.IsCombatCapable(_attacker);
            bool defenderAlive = BattleInitiator.IsCombatCapable(_defender);
            if (attackerAlive && defenderAlive)
                return false;

            // Manual's "Capture Kill Challenges" (pg. 24) / Battle Results: a side that just lost
            // its last non-hero unit doesn't quietly lose whatever hero(es) it still has — each
            // one left behind is hunted, one Challenge at a time, by the OTHER side, PROVIDED that
            // side actually has units of its own to hunt with. A hero-only army hunting alone
            // needs a skill this project doesn't have yet (the manual's own "Hunter"-style ability
            // — see BattleAttackPopupUI.BeginCaptureKill's own note); until then, a hero-only
            // winner just can't press the advantage and the loser's hero(es) simply stay put.
            var pending = new Queue<(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)>();
            if (!attackerAlive && BattleInitiator.IsCombatCapable(_defender))
                foreach (UnitData hero in HeroesOnly(_attacker))
                    pending.Enqueue((hero, _attacker, _defender));
            if (!defenderAlive && BattleInitiator.IsCombatCapable(_attacker))
                foreach (UnitData hero in HeroesOnly(_defender))
                    pending.Enqueue((hero, _defender, _attacker));

            if (pending.Count > 0 && attackPopup != null)
                RunNextCaptureKillChallenge(pending, () => FinishBattleEnd(attackerAlive, defenderAlive));
            else
                FinishBattleEnd(attackerAlive, defenderAlive);
            return true;
        }

        // A hero-only army (see BattleInitiator.IsEngageable vs IsCombatCapable) is a poor fit
        // for the full Tactical Battle Module — heroes never act in a Ground Combat round (see
        // BattleTurnOrder's own "heroes never act" rule) and can't be attacked as a regular grid
        // target either, so there's nothing for a normal battle to actually DO against one.
        // Contact with one (see HexSelectionController.Movement.cs / GameTurnController's own
        // delayed-battle branch) comes straight here instead — no grid, no Arrangement/Round-
        // start, this popup (attackPopup) IS the entire encounter. `hunterArmy` needing its own
        // non-hero units is the caller's responsibility to have already checked (same rule
        // CheckBattleEnd's own trigger enforces) — this doesn't re-check it.
        public void BeginCaptureKillEncounter(ArmyData hunterArmy, ArmyData targetArmy, Action onClosed)
        {
            if (attackPopup == null || hunterArmy == null || targetArmy == null)
            {
                onClosed?.Invoke();
                return;
            }

            // Deliberately does NOT activate panelRoot — no grid/Arrangement/turn-order chrome
            // makes sense for a hero-only encounter, and per the user's own spec this needs to
            // stay a light popup-only interaction, reusable for every future Challenge type
            // (Retreat/Assassination/Sabotage/Sniper/...), not each one opening the whole battle
            // screen. attackPopup itself is the only UI this ever shows. IsShowing already covers
            // attackPopup on its own (see its own comment) so GameTurnController.InputBlocked
            // still works without panelRoot's involvement — just needs telling that it changed.
            //
            // cardHand deliberately stays VISIBLE (unlike Show's own cardHand?.Hide() — the map
            // isn't covered by a full battle screen here, just this one popup), per the user's
            // own report — only dragging needs to be blocked, and CardDraggingBlocked already
            // does that on its own once VisibilityChanged fires below (see GameTurnController.
            // RecomputeBlockedState's own battleScreen.IsShowing term).
            _onClosed = onClosed;
            hexSelectionController?.Deselect();
            rtsCamera?.SetPanningEnabled(false);

            _localArmy = null;
            if (hunterArmy.Owner != null && hunterArmy.Owner.IsHuman)
                _localArmy = hunterArmy;
            else if (targetArmy.Owner != null && targetArmy.Owner.IsHuman)
                _localArmy = targetArmy;

            // Starting the Capture/Kill Challenge is a directed action against the target hero
            // — a hidden hero the hunter had personally detected loses stealth now (§5/§7);
            // a hidden hero the hunter cannot see never reaches here (BattleInitiator.
            // FindEnemyAt excluded it).
            foreach (UnitData hero in HeroesOnly(targetArmy))
                if (hero.IsHidden && !Game.Map.StealthSystem.IsHiddenFrom(hero, hunterArmy.Owner))
                    Game.Map.StealthSystem.ExitStealth(hero);

            var pending = new Queue<(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)>();
            foreach (UnitData hero in HeroesOnly(targetArmy))
                pending.Enqueue((hero, targetArmy, hunterArmy));

            // Reuses this exact same class's own Hide() for cleanup (restores cardHand/camera
            // panning, resets _localArmy, invokes _onClosed, and fires VisibilityChanged itself
            // unconditionally — covering the closing edge) — nothing else here needs a bespoke
            // teardown since _grid/_attacker/_defender were never touched in the first place.
            // targetArmy itself might, though: if every hero in it just got Killed/Captured, it's
            // now empty and — unlike a normal battle's _attacker/_defender (torn down by
            // OnBattleOutcomeAcknowledged) — nothing else would ever clean it up, since this
            // encounter never goes through that method at all.
            //
            // suppressAiThoughts: true — aiThoughts lives under panelRoot, which this encounter
            // deliberately never activates (see this method's own comment above), so its
            // AiЕhoughts_Text is still inactive here; routing a thought through it threw
            // "Coroutine couldn't be started because the game object ... is inactive" (see the
            // user's own report). CheckBattleEnd's own RunNextCaptureKillChallenge call runs
            // while a real battle's panelRoot IS already showing, so that one still narrates.
            RunNextCaptureKillChallenge(pending, () =>
            {
                // This hero-only encounter never goes through OnBattleOutcomeAcknowledged (see
                // this method's own comment — attackPopup IS the entire encounter), so unlike a
                // normal battle it never got that method's own Fate replenish either. Without
                // this, a hero who spent Fate defending here (e.g. an Escaped outcome) stayed
                // permanently short on a LATER Capture Kill attempt against the same hero —
                // FateMax stayed correct as the roll's own pool size (see BeginCaptureKill), but
                // the actual current Fate available to spend during the duel never recovered
                // (see the project owner's own report: the defending side's Fate wasn't full on
                // a second capture attempt). Both sides, same as OnBattleOutcomeAcknowledged.
                foreach (UnitData unit in hunterArmy.Members)
                    unit.ReplenishFateForNewBattle();
                foreach (UnitData unit in targetArmy.Members)
                    unit.ReplenishFateForNewBattle();

                // hunterArmy.Hex (captured BEFORE any of this runs — see hunterArmy's own
                // comment on why it's stable here even for a targetArmy that ends up retreating)
                // rather than targetArmy.Hex — a hero that Escaped this exact Challenge stays a
                // member of targetArmy and may have just retreated it to a different hex below.
                HexCoord hunterHex = hunterArmy.Hex;
                HandleBuildingOnArmyDefeat(hunterArmy, targetArmy);
                hexSelectionController?.DeleteArmyIfEmptied(targetArmy);
                hexSelectionController?.RestackArmiesOn(targetArmy.Hex, null);

                // Same "what's left on this hex" resolution OnBattleOutcomeAcknowledged's own
                // chain uses (see ResolveHexAfterVictory's own comment) — needed here too since a
                // hero-only guard/contact never goes through that method at all, it resolves
                // entirely through this Capture Kill Challenge chain instead.
                ResolveHexAfterVictory(hunterHex, hunterArmy);
            }, suppressAiThoughts: true);
            VisibilityChanged?.Invoke(); // opening edge — attackPopup is showing as of this call
        }

        private static List<UnitData> HeroesOnly(ArmyData army) =>
            army?.Members.FindAll(m => m.IsHero) ?? new List<UnitData>();

        private void RunNextCaptureKillChallenge(Queue<(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)> pending,
            Action onAllResolved, bool suppressAiThoughts = false)
        {
            if (pending.Count == 0)
            {
                onAllResolved();
                return;
            }
            (UnitData hero, ArmyData heroArmy, ArmyData hunterArmy) next = pending.Dequeue();
            attackPopup.BeginCaptureKill(next.hunterArmy, next.hero,
                ResolveCatalog(next.hunterArmy?.Owner)?.logo, ResolveCatalog(next.hero?.Owner)?.logo,
                outcome => HandleCaptureKillOutcome(outcome, next.hero, next.heroArmy, next.hunterArmy, pending, onAllResolved, suppressAiThoughts),
                suppressAiThoughts ? null : ShowAiThought);
        }

        // Killed: discarded outright. Captured: handed to the hunter's own Prison (see
        // TryImprison) — or, if that's not possible (no citadel to hold them, per the user's
        // own call), discarded exactly like Killed. Escaped: the hero just survives — whether
        // its whole army needs to retreat is decided separately below, ONCE, only after every
        // hero belonging to that SAME army has been through its own Challenge.
        private void HandleCaptureKillOutcome(CaptureKillOutcome outcome, UnitData hero, ArmyData heroArmy,
            ArmyData hunterArmy, Queue<(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)> pending, Action onAllResolved,
            bool suppressAiThoughts = false)
        {
            switch (outcome)
            {
                case CaptureKillOutcome.Captured:
                    if (!TryImprison(hero, heroArmy, hunterArmy))
                        RemoveHero(heroArmy, hero);
                    RefreshGrid();
                    break;
                case CaptureKillOutcome.Killed:
                    RemoveHero(heroArmy, hero);
                    RefreshGrid();
                    break;
                case CaptureKillOutcome.Escaped:
                    // A garrison has no army of its own to flee IN — it's anchored to its
                    // Barracks building and can never move at all (see HexSelectionController.
                    // Movement.cs's own IssueMoveOrder garrison guard). The retreat logic just
                    // below assumes the loser's remaining army can physically leave the hex,
                    // which a garrison can't — per the project owner's own root-cause report
                    // (2026-08-26): a garrisoned hero who "escaped" the duel was retreating the
                    // whole garrison off its own base instead, an army that was never mobile to
                    // begin with. Closed the way the project owner specified: a garrisoned hero
                    // evading the duel is automatically treated as Captured instead — same
                    // handling as the case just above. A hero belonging to an ordinary (mobile)
                    // army still gets a real Escaped result; only the garrison case is overridden.
                    if (heroArmy != null && heroArmy.IsGarrison)
                    {
                        if (!TryImprison(hero, heroArmy, hunterArmy))
                            RemoveHero(heroArmy, hero);
                        RefreshGrid();
                    }
                    break;
                // Escaped, non-garrison: nothing removed, hero stays a member of heroArmy — its
                // own army retreats below once every hero in the queue has been resolved.
            }

            // Checking retreat-need right after EACH hero (the original version of this) was a
            // real bug with 2+ heroes in the same army: if the FIRST one resolved Escaped, its
            // army would retreat/get destroyed immediately — potentially relocating or, on a
            // failed retreat, wiping Members entirely — out from under a SECOND hero still
            // waiting its own turn in `pending` (see the user's own report). So this only
            // fires once nothing else in the queue still belongs to heroArmy — and only if at
            // least one hero actually survived to retreat with (heroArmy could be completely
            // empty here if every hero was Killed/Captured, in which case there's nothing left
            // to retreat and no announcement to show — the caller's own DeleteArmyIfEmptied
            // handles that empty-army case instead).
            bool moreForThisArmy = pending.Any(e => e.heroArmy == heroArmy);
            if (!moreForThisArmy && heroArmy != null && heroArmy.Members.Count > 0
                && !BattleInitiator.IsCombatCapable(heroArmy))
            {
                // The surviving hero(es) can't keep fighting, so their army just leaves the
                // battle — but no announcement popup for it (per the project owner, 2026-08-27):
                // the Capture/Kill result screen the player already clicked through IS the last
                // screen of this challenge. The old "The enemy retreats." / "Your army retreats."
                // (and the two "destroyed retreating" variants) only added a dead click. The
                // relocation itself still has to happen.
                PerformRetreat(heroArmy, hunterArmy, out _);
            }

            RunNextCaptureKillChallenge(pending, onAllResolved, suppressAiThoughts);
        }

        private void RemoveHero(ArmyData army, UnitData hero)
        {
            if (hero == null)
                return;
            if (_grid != null && _grid.TryFindPosition(hero, out int row, out int col))
                _grid.Set(row, col, null);
            army?.Members.Remove(hero);
        }

        // Manual's "transported to the nearest base of the empire that captured the hero" —
        // simplified to that empire's own STARTING citadel specifically (the one placed during
        // setup, per the user's own spec — there's no "nearest base" search or siege/ownership-
        // capture mechanic in this project to route through instead). False (caller falls back
        // to a plain Killed-style discard) if the capturing player has no citadel hex on record
        // at all — reachable two ways: a real player whose own starting citadel was somehow
        // destroyed first, or hunterArmy.Owner being the Neutral player, which never has a
        // citadel hex at all (see PlayerSetupData.IsNeutral) and can perfectly well end up as
        // the winning/hunting side once BattleScreenUI.ConsiderAiRetreat started letting two
        // non-human armies fight each other unsupervised. Either way, a hero a neutral "captures"
        // just dies instead of being imprisoned — no Prison to put it in.
        private bool TryImprison(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)
        {
            PlayerSetupData capturer = hunterArmy?.Owner;
            if (hero == null || heroArmy == null || capturer == null
                || !capturer.CitadelHexQ.HasValue || !capturer.CitadelHexR.HasValue)
                return false;

            var citadelHex = new HexCoord(capturer.CitadelHexQ.Value, capturer.CitadelHexR.Value);
            ArmyData prison = ArmyRegistry.AllAt(citadelHex).Find(a => a.IsPrison && a.Owner == capturer);
            if (prison == null)
                return false;

            if (_grid != null && _grid.TryFindPosition(hero, out int row, out int col))
                _grid.Set(row, col, null);
            heroArmy.Members.Remove(hero);

            // Owner changes to the captor (see UnitData.CapturedFrom's own comment) so every
            // existing owner-driven display/lookup — ArmyRegistry.AllForOwner, player-colour name
            // text — reads correctly for a card now sitting in the captor's own Prison;
            // IsPrisoner + CapturedFrom are what a future "return captured
            // heroes when this citadel changes hands" mechanic would need to undo this (not
            // implemented yet — no such ownership-change event exists in this project today).
            hero.CapturedFrom = hero.Owner;
            hero.Owner = capturer;
            hero.IsPrisoner = true;
            prison.Members.Add(hero);
            return true;
        }

        // The user's own Siege spec: a building on a hex whose defending army has just been
        // wiped out completely changes hands along with the fight — no separate manual-style
        // Siege Challenge, this is a straight consequence of Ground Combat/Capture Kill itself.
        // Its own garrison/Prison/facilities are separate ArmyData/data entries at the same hex
        // and aren't touched here (see GameTurnController's own citadel-recapture buffer check
        // for what a captured STARTING citadel eventually means for its former owner). See
        // BuildingRegistry.CaptureOrDestroy for the actual capture-vs-destroy split.
        private void HandleBuildingOnArmyDefeat(ArmyData winnerArmy, ArmyData loserArmy)
        {
            if (loserArmy == null || loserArmy.Members.Count > 0)
                return;
            BuildingData building = BuildingRegistry.FindAt(loserArmy.Hex);
            if (building == null || building.Owner != loserArmy.Owner)
                return;
            // A base can have more than one defending army on the same hex (e.g. a garrison PLUS
            // a field army — see BattleInitiator.FindEnemyAt's own comment on why only one gets
            // fought at a time), chained one battle at a time via ResolveHexAfterVictory. Only
            // capture/destroy once EVERY one of the building owner's own engageable armies here
            // is gone — same "any other defender left?" check TryHandoverVacatedBase already
            // applies for the retreat path (BattleScreenUI.Retreat.cs). Without this, the base
            // recoloured/gave vision to the winner the instant the FIRST defending army alone was
            // wiped out, even with a second battle for it still pending (see the project owner's
            // own report: clicking Delay before that second battle already showed the base as
            // captured).
            // A resident every member of which is hidden from the winner cannot hold the
            // building (see Game.Map.StealthSystem) — IsEngageable(resident, winner), not a
            // raw pass.
            bool otherDefenderRemains = ArmyRegistry.AllAt(loserArmy.Hex)
                .Any(resident => resident != loserArmy && resident.Owner == building.Owner
                    && BattleInitiator.IsEngageable(resident, winnerArmy?.Owner));
            if (otherDefenderRemains)
                return;
            BuildingRegistry.CaptureOrDestroy(building, winnerArmy?.Owner, hexSelectionController);
        }

        private static void RevertBerserkStacks(ArmyData army)
        {
            if (army == null)
                return;
            foreach (UnitData unit in army.Members)
            {
                if (unit.BerserkStacks <= 0)
                    continue;
                unit.Attack -= unit.BerserkStacks;
                unit.Defense += unit.BerserkDefenseLost;
                unit.BerserkStacks = 0;
                unit.BerserkDefenseLost = 0;
            }
        }

        private void FinishBattleEnd(bool attackerAlive, bool defenderAlive)
        {
            // UnitAbilities.Berserk only lasts "for the duration of the battle" (pg. 40) — revert
            // whatever ResolveDamage stacked onto survivors now that it's over, so it never
            // snowballs into a permanent buff across battles.
            RevertBerserkStacks(_attacker);
            RevertBerserkStacks(_defender);

            FireBattleEndThought(_attacker, attackerAlive);
            FireBattleEndThought(_defender, defenderAlive);

            // Whichever side's army is now completely gone (Members.Count == 0, not just
            // BattleInitiator.IsCombatCapable — a hero-only remnant would still pass that, but
            // by the time FinishBattleEnd runs any such remnant has already been through the full
            // Capture Kill Challenge chain, see CheckBattleEnd) loses whatever building sits on
            // the shared battle hex too (see HandleBuildingOnArmyDefeat). Skipped entirely on a
            // genuine mutual wipeout (both empty) — there's no real winner to hand a building to,
            // and calling this for both directions in that case would just flip ownership back
            // and forth depending on call order.
            bool attackerEmpty = _attacker != null && _attacker.Members.Count == 0;
            bool defenderEmpty = _defender != null && _defender.Members.Count == 0;
            if (attackerEmpty != defenderEmpty)
                HandleBuildingOnArmyDefeat(attackerEmpty ? _defender : _attacker, attackerEmpty ? _attacker : _defender);

            string title;
            if (_localArmy == null)
                title = attackerAlive ? "Attacker wins." : defenderAlive ? "Defender wins." : "Draw.";
            else
            {
                bool localWon = _localArmy == _attacker ? attackerAlive : defenderAlive;
                title = localWon ? "Victory!" : "Defeat!";
            }

            // Which army actually beat which, plus (on its own line) what happens once this
            // popup closes — per the user's own request for the newly added Message field.
            // `survivor` here mirrors OnBattleOutcomeAcknowledged's own attackerHere/defenderHere
            // check below: at THIS point (no retreat involved, straight combat resolution to a
            // wipeout) attacker/defender are still exactly where they started, so attackerAlive/
            // defenderAlive already answers the same question that check re-derives from Hex.
            string detail = attackerAlive != defenderAlive
                ? $"{(attackerAlive ? _attacker : _defender)?.Name} defeated {(attackerAlive ? _defender : _attacker)?.Name}."
                : $"{_attacker?.Name} and {_defender?.Name} destroy each other.";
            ArmyData survivor = attackerAlive != defenderAlive ? (attackerAlive ? _attacker : _defender) : null;
            string message = $"{detail}\n{DescribeNextAction(survivor)}";

            // No human involved in this fight (AI vs. neutrals/event guards/another AI) — nobody's
            // there to click Ok, so the popup closes itself after a beat instead of stalling the
            // AI's turn (see BattleOutcomePopupUI.Show's own comment).
            if (outcomePopup != null)
                outcomePopup.Show(title, message, OnBattleOutcomeAcknowledged, autoCloseNoHuman: _localArmy == null);
            else
                OnBattleOutcomeAcknowledged();
        }

        // Whether the battle screen is about to chain straight into another fight on this same
        // hex, or return to the map — same question OnBattleOutcomeAcknowledged (below) answers
        // for real right after this popup closes, computed early here just to describe it in the
        // BattleOutcome popup's own Message field (see FinishBattleEnd/ResolveRetreat, its only
        // two callers). `survivor` is null for a mutual wipeout — nothing to chain into either way.
        private string DescribeNextAction(ArmyData survivor)
        {
            bool hasNext = survivor?.Owner != null
                && !DelayedBattleRegistry.IsHexPending(_battleHex)
                && BattleInitiator.FindEnemyAt(_battleHex, survivor.Owner) != null;
            return hasNext ? "Proceeding to the next battle." : "Returning to the map.";
        }

        private void FireBattleEndThought(ArmyData army, bool survived)
        {
            if (army?.Owner == null || army.Owner.IsHuman)
                return;
            UnitData sideHero = BattleTurnOrder.FindHero(_grid, army == _attacker);
            aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                survived ? AiThoughtCategory.BattleWon : AiThoughtCategory.BattleLost, hasHero: sideHero != null));
        }

        private void OnBattleOutcomeAcknowledged()
        {
            // Hero Fate refills per-battle, not per strategic turn (see UnitData.
            // ReplenishFateForNewBattle's own comment) — done here, right as THIS battle ends,
            // rather than in the next Show() like it used to be. A survivor chaining straight
            // into a second army on the same hex (below) shows that fight's own
            // BattleContactPopupUI BEFORE Show() ever runs again — reading Fate off ArmyData.
            // Members directly, not through this screen — so replenishing only in Show() left
            // that preview showing whatever Fate was left over at the end of the fight that just
            // finished (see the user's own report). Both sides get it, not just the survivor —
            // the loser's own members (if any remain, e.g. a hero-only remnant) are about to be
            // discarded anyway, so this is harmless for them.
            if (_attacker != null)
                foreach (UnitData unit in _attacker.Members)
                    unit.ReplenishFateForNewBattle();
            if (_defender != null)
                foreach (UnitData unit in _defender.Members)
                    unit.ReplenishFateForNewBattle();

            // Tears down the just-finished battle's own grid cells/turn-queue icons/sub-popups
            // (see ResetBattlePanel's own comment) right as its outcome is acknowledged — BEFORE
            // deciding whether a second enemy on the same hex chains straight into a fresh Show().
            // Previously nothing here did this at all: the chained case went straight into a new
            // Show() with the FIRST battle's whole panel still exactly as it was, so debug
            // logging confirmed the new battle DID initialize while every one of the old battle's
            // UI elements stayed visually active on screen underneath it (see the user's own
            // report). Safe to call even when nothing chains afterward — Hide() (below, or via
            // onDelay) runs the exact same reset again, which is an idempotent no-op the second
            // time.
            ResetBattlePanel();

            // DeleteArmyIfEmptied only unregisters/destroys the ONE army's own marker — it
            // doesn't re-pick which army's marker should now represent this hex for a given
            // owner (see RestackArmiesOn's own comment). Without this, a second, untouched enemy
            // army sharing the hex (this battle only ever involved _attacker/_defender, see
            // BattleInitiator.FindEnemyAt picking just the first one) stayed hidden until the
            // player happened to reselect the hex — the same restack ArmyViewerModalUI's own
            // Hide() relies on, just done explicitly here since nothing re-selects the hex after
            // a battle closes.
            // _battleHex (captured once in Show from its own `hex` parameter, previously never
            // stored at all) rather than re-derived from _attacker.Hex/_defender.Hex — a retreat
            // (see PerformRetreat) changes the RETREATING side's own ArmyData.Hex mid-battle, so
            // reading it back off _attacker/_defender here used to silently return the WRONG hex
            // whenever the ATTACKER was the one who retreated (see the user's own report: retreat
            // announced repeatedly, the next enemy on the same hex never actually engaged).
            HexCoord hex = _battleHex;

            // The manual's own "two armies, one hex" case: if a THIRD army was already waiting
            // here, whichever side is still standing on `hex` and still combat-capable isn't
            // actually done — it continues into a second Battle Setup instead of being left stuck
            // on a hex it can no longer leave (BattleInitiator.FindEnemyAt would still see the
            // untouched army) with no way to ever actually fight it (see the user's own report).
            // Checked per-side against the captured `hex`, NOT `_attacker.Hex.Equals(_defender.
            // Hex)` — a retreat outcome (see ResolveRetreat) already relocated the retreating side
            // to a DIFFERENT hex by the time this runs, which used to make that old comparison
            // false and silently drop the second fight even though the winner is still right here.
            bool attackerHere = _attacker != null && _attacker.Hex.Equals(hex) && BattleInitiator.IsCombatCapable(_attacker);
            bool defenderHere = _defender != null && _defender.Hex.Equals(hex) && BattleInitiator.IsCombatCapable(_defender);
            ArmyData survivor = attackerHere != defenderHere ? (attackerHere ? _attacker : _defender) : null;

            hexSelectionController?.DeleteArmyIfEmptied(_attacker);
            hexSelectionController?.DeleteArmyIfEmptied(_defender);
            hexSelectionController?.RestackArmiesOn(hex, null);

            ResolveHexAfterVictory(hex, survivor);
        }

        // Shared by OnBattleOutcomeAcknowledged and BeginCaptureKillEncounter's own ending
        // callback (see its own comment on why it needs this too) — once `survivor` is the only
        // side left standing on `hex`, decides what happens next.
        //
        // hexPending (DelayedBattleRegistry.IsHexPending) covers two distinct things at once, on
        // purpose: it guards against re-offering a Fight/Delay choice for an army that's already
        // reserved for a different pending battle at this same hex (e.g. queued earlier this turn
        // by a different attacker's own Delay choice — per the user's own call, a reserved army
        // can't be signed up for a second one), AND it holds the event trigger back too — per the
        // user's own call, a hex with a still-undelivered Delay isn't actually "clear" yet, even
        // though FindEnemyAt has nothing left to report right this moment. Both are left
        // unresolved here on purpose; GameTurnController's own end-of-turn sweep is what
        // eventually forces the delayed pairing, and once THAT battle also ends, this same method
        // runs again and finds the hex genuinely clear.
        //
        // Hex Events, "collision hex" case: an unrelated pre-existing neutral army (or, same code
        // path since this method is shared, an event's own guard, spawned only once Explore was
        // actually chosen — see HexEventRegistry.Entry.ResolvedGuardMembers) shared this event's
        // hex (see CitadelSetupController.MapContent.GenerateRandomEvents), so the ordinary
        // contact flow forced combat here before the event's own choice popup ever got a chance
        // to show (see HexSelectionController.Movement.cs's own HasUnclaimedCleanEventHex, which
        // deliberately never claims a hex like this). Only reached once nextEnemy == null and
        // nothing is still pending — nothing hostile left standing here at all, regardless of
        // whether that took one fight or a whole chain of them — matching the user's own
        // "triggers only after full battle resolution" rule. survivor.Owner.IsNeutral is excluded
        // so a neutral-vs-neutral mutual fight (if that's ever reachable) never triggers anything.
        // TriggerHexEventIfClear (HexSelectionController.Events.cs) is what actually tells "hex
        // just went clear of an unrelated army" apart from "the event's own guard just lost" —
        // see its own comment.
        private void ResolveHexAfterVictory(HexCoord hex, ArmyData survivor)
        {
            bool hexPending = DelayedBattleRegistry.IsHexPending(hex);
            ArmyData nextEnemy = survivor?.Owner != null && !hexPending
                ? BattleInitiator.FindEnemyAt(hex, survivor.Owner)
                : null;

            // Diagnostics for the project owner's own report (2026-08-26): a hex left with
            // several separate hero-only armies (e.g. a citadel's combat garrison PLUS a
            // separate hero-only stack) is expected to chain straight into a Capture Kill
            // Challenge for the next one (see nextEnemyHeroOnly below) rather than opening a
            // normal battle screen — inspection didn't turn up a case where this branches wrong,
            // so this logs every chained pick instead of guessing; compare against what the
            // player actually saw next time this reproduces.
            if (nextEnemy != null)
                BattleDebugLog.Write($"[HeroChallengeDiag] ResolveHexAfterVictory at ({hex.Q},{hex.R}): survivor={survivor?.Name} " +
                    $"({survivor?.Owner?.Nickname}) -> nextEnemy={nextEnemy.Name} ({nextEnemy.Owner?.Nickname}, " +
                    $"members={nextEnemy.Members.Count}, heroes={nextEnemy.Members.Count(m => m.IsHero)}, " +
                    $"IsCombatCapable={BattleInitiator.IsCombatCapable(nextEnemy)})");

            // True only when TriggerHexEventIfClear just opened (or reopened) a fresh guard fight
            // on THIS SAME battleScreen instance, reentrantly, from inside this very call stack —
            // see that method's own comment. When it did, the Hide() below must NOT run: it would
            // tear down the fight just opened (its _attacker/_defender/_onClosed already
            // overwritten by that reentrant Show()) instead of the one actually finishing here,
            // and drop whoever was still waiting on the outcome of the fight that finished.
            bool eventOpenedBattle = false;
            if (nextEnemy == null && !hexPending && survivor?.Owner != null && !survivor.Owner.IsNeutral)
                eventOpenedBattle = hexSelectionController?.TriggerHexEventIfClear(hex, survivor) ?? false;

            if (nextEnemy != null)
            {
                var participants = new List<ArmyData> { survivor, nextEnemy };
                // Same hero-only branch as HexSelectionController.Movement.cs's own contact
                // handling / GameTurnController.ResolveDelayedBattlesThen's own targetHeroOnly
                // check — nothing for a normal Ground Combat round to do against a target with no
                // non-hero units, so this must skip the grid and go straight to a Capture Kill
                // Challenge instead. This chained-second-army path used to always call Show(),
                // opening a full battle with an empty grid row for a hero-only nextEnemy (see the
                // user's own report).
                bool nextEnemyHeroOnly = !BattleInitiator.IsCombatCapable(nextEnemy);

                // Same human-only gating as the very first contact on the strategic map (see
                // HexSelectionController.Movement.cs's own onFight/onDelay branch) — this used to
                // always show the interactive Fight/Delay popup here regardless of who `survivor`
                // belongs to, which meant a chained second enemy on an AI/Neutral survivor's own
                // hex opened a popup nobody would ever click, hanging the same way a deferred
                // AI-vs-Neutral contact used to (see that branch's own comment for the full report).
                if (survivor.Owner != null && survivor.Owner.IsHuman && battleContactPopup != null)
                {
                    battleContactPopup.Show(hex, participants,
                        onFight: () =>
                        {
                            if (nextEnemyHeroOnly)
                                BeginCaptureKillEncounter(survivor, nextEnemy, _onClosed);
                            else
                                Show(hex, participants, _onClosed);
                        },
                        onDelay: () =>
                        {
                            DelayedBattleRegistry.Add(new PendingBattle { Hex = hex, Participants = participants });
                            if (!TryChainPendingRetreatContact())
                                Hide();
                        });
                }
                else if (nextEnemyHeroOnly)
                {
                    BeginCaptureKillEncounter(survivor, nextEnemy, _onClosed);
                }
                else
                {
                    Show(hex, participants, _onClosed);
                }
                return;
            }

            if (eventOpenedBattle)
                return;

            if (!TryChainPendingRetreatContact())
                Hide();
        }
    }
}
