using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    public enum BattleTurnAdvance
    {
        NextUnit,
        NextRound,
        ResolveRetreat,
        NoTurn
    }

    public readonly struct BattleDefenseBreakdown
    {
        public readonly int TerrainBonus;
        public readonly int ConstructionBonus;
        public readonly int FormationBonus;
        public int TotalBonus => TerrainBonus + ConstructionBonus + FormationBonus;

        public BattleDefenseBreakdown(int terrainBonus, int constructionBonus, int formationBonus)
        {
            TerrainBonus = terrainBonus;
            ConstructionBonus = constructionBonus;
            FormationBonus = formationBonus;
        }
    }

    public readonly struct BattleChallengeRollResult
    {
        public readonly bool[] AttackerDice;
        public readonly bool[] DefenderDice;
        public readonly int AttackerFateRemaining;
        public readonly int DefenderFateRemaining;

        public BattleChallengeRollResult(bool[] attackerDice, bool[] defenderDice,
            int attackerFateRemaining, int defenderFateRemaining)
        {
            AttackerDice = attackerDice ?? Array.Empty<bool>();
            DefenderDice = defenderDice ?? Array.Empty<bool>();
            AttackerFateRemaining = Mathf.Max(0, attackerFateRemaining);
            DefenderFateRemaining = Mathf.Max(0, defenderFateRemaining);
        }
    }

    public readonly struct BattleSecondaryHit
    {
        public readonly UnitData Victim;
        public readonly int Damage;
        public readonly bool Died;
        public readonly string Skill;

        public BattleSecondaryHit(UnitData victim, int damage, bool died, string skill)
        {
            Victim = victim;
            Damage = damage;
            Died = died;
            Skill = skill;
        }
    }

    public readonly struct BattleUnitRemoval
    {
        public readonly UnitData Unit;
        public readonly ArmyData DeadSide;
        public readonly ArmyData KillerSide;
        public readonly bool AnnounceKiller;

        public BattleUnitRemoval(UnitData unit, ArmyData deadSide, ArmyData killerSide, bool announceKiller)
        {
            Unit = unit;
            DeadSide = deadSide;
            KillerSide = killerSide;
            AnnounceKiller = announceKiller;
        }
    }

    public sealed class BattleAttackApplication
    {
        public BattleResolutionRules.GroundAttackOutcome Outcome;
        public int Damage;
        public bool DefenderDied;
        public bool Shocked;
        public readonly List<BattleSecondaryHit> SecondaryHits = new List<BattleSecondaryHit>();
        public readonly List<BattleUnitRemoval> Removals = new List<BattleUnitRemoval>();
    }

    public readonly struct BattleEndStatus
    {
        public readonly bool Ended;
        public readonly bool AttackerAlive;
        public readonly bool DefenderAlive;

        public BattleEndStatus(bool ended, bool attackerAlive, bool defenderAlive)
        {
            Ended = ended;
            AttackerAlive = attackerAlive;
            DefenderAlive = defenderAlive;
        }
    }

    public readonly struct BattlePostActionResolution
    {
        public readonly BattleEndStatus EndStatus;
        public readonly BattleCaptureKillSequence CaptureKillSequence;

        public bool Ended => EndStatus.Ended;

        public BattlePostActionResolution(BattleEndStatus endStatus,
            BattleCaptureKillSequence captureKillSequence)
        {
            EndStatus = endStatus;
            CaptureKillSequence = captureKillSequence;
        }
    }

    public readonly struct BattleCaptureKillApplication
    {
        public readonly CaptureKillOutcome RequestedOutcome;
        public readonly CaptureKillOutcome EffectiveOutcome;
        public readonly bool Imprisoned;
        public readonly bool Removed;
        public readonly bool HeroArmyNeedsRetreat;

        public BattleCaptureKillApplication(CaptureKillOutcome requestedOutcome, CaptureKillOutcome effectiveOutcome,
            bool imprisoned, bool removed, bool heroArmyNeedsRetreat)
        {
            RequestedOutcome = requestedOutcome;
            EffectiveOutcome = effectiveOutcome;
            Imprisoned = imprisoned;
            Removed = removed;
            HeroArmyNeedsRetreat = heroArmyNeedsRetreat;
        }
    }

    public readonly struct BattleCaptureKillTarget
    {
        public readonly UnitData Hero;
        public readonly ArmyData HeroArmy;
        public readonly ArmyData HunterArmy;

        public BattleCaptureKillTarget(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)
        {
            Hero = hero;
            HeroArmy = heroArmy;
            HunterArmy = hunterArmy;
        }
    }

    public readonly struct BattleCaptureKillStep
    {
        public readonly BattleCaptureKillApplication Application;
        public readonly BattleRetreatApplication Retreat;

        public BattleCaptureKillStep(BattleCaptureKillApplication application,
            BattleRetreatApplication retreat)
        {
            Application = application;
            Retreat = retreat;
        }
    }

    public sealed class BattleCaptureKillSequence
    {
        private readonly BattleState _state;
        private readonly Queue<BattleCaptureKillTarget> _pending;

        public int Count => _pending.Count;
        public bool HasPending => _pending.Count > 0;

        public BattleCaptureKillSequence(BattleState state,
            IEnumerable<BattleCaptureKillTarget> targets)
        {
            _state = state;
            _pending = new Queue<BattleCaptureKillTarget>(
                targets ?? Array.Empty<BattleCaptureKillTarget>());
        }

        public bool TryGetCurrent(out BattleCaptureKillTarget target)
        {
            if (_pending.Count == 0)
            {
                target = default;
                return false;
            }
            target = _pending.Peek();
            return true;
        }

        public BattleCaptureKillStep ResolveCurrent(BattleChallengeRollResult roll,
            HexMap map, HexSelectionController hexSelectionController)
        {
            if (_pending.Count == 0)
                return default;

            BattleCaptureKillTarget current = _pending.Dequeue();
            BattleCaptureKillApplication application = BattleEngine.ResolveAndApplyCaptureKill(
                _state, roll, current.Hero, current.HeroArmy, current.HunterArmy);

            BattleRetreatApplication retreat = default;
            bool moreForSameArmy = _pending.Any(e => e.HeroArmy == current.HeroArmy);
            if (!moreForSameArmy && application.HeroArmyNeedsRetreat)
            {
                retreat = BattleEngine.PerformRetreat(
                    _state, current.HeroArmy, current.HunterArmy, map, hexSelectionController);
            }

            return new BattleCaptureKillStep(application, retreat);
        }
    }

    public readonly struct BattleRetreatApplication
    {
        public readonly bool Destroyed;
        public readonly bool Relocated;
        public readonly HexCoord Destination;
        public readonly List<ArmyData> ContactParticipants;

        public BattleRetreatApplication(bool destroyed, bool relocated, HexCoord destination,
            List<ArmyData> contactParticipants)
        {
            Destroyed = destroyed;
            Relocated = relocated;
            Destination = destination;
            ContactParticipants = contactParticipants;
        }
    }

    public readonly struct BattleSummonApplication
    {
        public readonly int Summoners;
        public readonly int Requested;
        public readonly int Spawned;

        public BattleSummonApplication(int summoners, int requested, int spawned)
        {
            Summoners = summoners;
            Requested = requested;
            Spawned = spawned;
        }
    }

    public readonly struct BattleEncounterFinalization
    {
        public readonly ArmyData Survivor;
        public readonly bool AttackerHere;
        public readonly bool DefenderHere;

        public BattleEncounterFinalization(ArmyData survivor, bool attackerHere, bool defenderHere)
        {
            Survivor = survivor;
            AttackerHere = attackerHere;
            DefenderHere = defenderHere;
        }
    }

    // Domain owner for one tactical encounter. It mutates BattleState/ArmyData/UnitData and
    // returns presentation-neutral result objects. BattleScreenUI only renders those results.
    public sealed class BattleEngine
    {
        private readonly BattleState _state;
        private readonly AbilityMagnitudes _magnitudes;
        private readonly System.Random _random;

        public BattleState State => _state;

        public BattleEngine(BattleState state, AbilityMagnitudes magnitudes, System.Random random = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _magnitudes = magnitudes;
            _random = random ?? new System.Random(state.BattleSeed);
        }

        public void StartRound()
        {
            _state.TurnOrder = BattleTurnOrder.BuildOrder(_state.Grid, _state.Attacker, _state.Defender,
                unchecked(_state.BattleSeed * 31 + _state.Round));
            if (_state.RetreatingArmy != null)
                _state.TurnOrder = _state.TurnOrder
                    .Where(u => _state.OwningArmy(u) != _state.RetreatingArmy)
                    .ToList();
            _state.TurnIndex = 0;
        }

        public BattleTurnAdvance AdvanceTurn()
        {
            if (_state.TurnOrder == null || _state.TurnOrder.Count == 0)
                return BattleTurnAdvance.NoTurn;

            _state.TurnIndex++;
            if (_state.TurnIndex < _state.TurnOrder.Count)
                return BattleTurnAdvance.NextUnit;

            if (_state.RetreatingArmy != null)
                return BattleTurnAdvance.ResolveRetreat;

            _state.Round++;
            return BattleTurnAdvance.NextRound;
        }

        public BattleSummonApplication SpawnBattleSummons(ArmyData army, int frontRow, int backRow,
            CardDefinition template, int perSummoner,
            Func<CardDefinition, PlayerSetupData, UnitData> spawn)
        {
            if (army == null || template == null || spawn == null || _state.Grid == null)
                return default;

            int summoners = army.Members.Count(m => !m.IsSummoned
                && m.HasAbility(UnitAbilities.RaiseTheRots));
            int requested = summoners * Mathf.Max(0, perSummoner);
            int spawned = 0;

            for (int i = 0; i < requested; i++)
            {
                if (!TryFindFreeDeploymentCell(frontRow, backRow, out int row, out int col))
                    break;
                UnitData unit = spawn(template, army.Owner);
                if (unit == null)
                    break;
                unit.IsSummoned = true;
                army.AddMemberSorted(unit);
                _state.Grid.Set(row, col, unit);
                spawned++;
            }

            return new BattleSummonApplication(summoners, requested, spawned);
        }

        public int GetDefenderHexDefenseBonus(HexMap map)
        {
            int bonus = 0;
            if (map != null && map.TryGetTerrainAt(_state.BattleHex,
                out Game.Terrain.TerrainTypeEntry terrain) && terrain != null)
                bonus += terrain.defenseModifier;

            BuildingData building = BuildingRegistry.FindAt(_state.BattleHex);
            if (building != null && building.IsBase
                && _state.Defender?.Owner != null
                && building.Owner == _state.Defender.Owner)
                bonus += building.Defense;
            return bonus;
        }

        public void ArrangeAiArmy(ArmyData army, ArmyData enemyArmy, int frontRow, int backRow,
            HexMap map)
        {
            if (army == null)
                return;
            BattleAi.ArrangeArmy(_state.Grid, army, frontRow, backRow, enemyArmy,
                _magnitudes, _state.Defender, GetDefenderHexDefenseBonus(map));
        }

        public BattleAi.RetreatAssessment AssessAiRetreat(ArmyData army, ArmyData enemyArmy,
            HexMap map)
        {
            BuildingData building = army != null ? BuildingRegistry.FindAt(army.Hex) : null;
            bool defendingOwnCitadel = building != null && army != null
                && building.Owner == army.Owner && building.IsStartingCitadel;
            return BattleAi.AssessRetreat(_state.Grid, army, enemyArmy, defendingOwnCitadel,
                _magnitudes, _state.Defender, GetDefenderHexDefenseBonus(map));
        }

        public BattleAi.AiAction ChooseAiAction(UnitData actor,
            Dictionary<UnitData, int> waitStreak, ArmyData ownArmy, ArmyData enemyArmy,
            List<UnitData> turnOrder, int turnIndex, bool favorableFight, HexMap map,
            Dictionary<UnitData, Vector2Int> previousPositions = null)
        {
            return BattleAi.ChooseAction(_state.Grid, actor, waitStreak, ownArmy, enemyArmy,
                _magnitudes, turnOrder, turnIndex, favorableFight,
                _state.Defender, GetDefenderHexDefenseBonus(map), previousPositions);
        }

        public BattleDefenseBreakdown GetDefenseBreakdown(UnitData defender, HexMap map)
        {
            ArmyData defenderArmy = _state.OwningArmy(defender);
            int terrain = 0;
            int construction = 0;
            int formation = BattleProtectionRules.GetGuardedDefenseBonus(
                _state.Grid, defender, _state.Defender);

            if (defenderArmy != null && defenderArmy == _state.Defender
                && !Game.Aviation.AviationRules.IsAirArmy(defenderArmy))
            {
                if (map != null && map.TryGetTerrainAt(_state.BattleHex, out Game.Terrain.TerrainTypeEntry terrainEntry)
                    && terrainEntry != null)
                    terrain = terrainEntry.defenseModifier;

                BuildingData building = BuildingRegistry.FindAt(_state.BattleHex);
                if (building != null && building.IsBase
                    && building.Owner == defenderArmy.Owner)
                    construction = building.Defense;
            }

            return new BattleDefenseBreakdown(terrain, construction, formation);
        }

        public void CommitGroundAttack(UnitData attacker)
        {
            ArmyData army = _state.OwningArmy(attacker);
            if (army == null)
                return;
            foreach (UnitData member in army.Members)
                member.MoveCurrent = 0;
        }

        public bool CanMoveUnit(UnitData unit, int toRow, int toCol)
        {
            if (unit == null || _state.Grid == null
                || !_state.Grid.TryFindPosition(unit, out int fromRow, out int fromCol)
                || !BattleGrid.InBounds(toRow, toCol)
                || _state.Grid.Get(toRow, toCol) != null)
                return false;
            return BattleGrid.IsOrthogonallyAdjacent(fromRow, fromCol, toRow, toCol);
        }

        public bool CanGroundAttack(UnitData attacker, UnitData defender)
        {
            if (attacker == null || defender == null
                || attacker.Owner == defender.Owner || _state.Grid == null
                || !_state.Grid.TryFindPosition(attacker, out int ar, out int ac)
                || !_state.Grid.TryFindPosition(defender, out int dr, out int dc))
                return false;
            return BattleGrid.IsInRange(ar, ac, dr, dc, attacker.Range);
        }

        public bool TryMoveUnit(UnitData unit, int fromRow, int fromCol, int toRow, int toCol)
        {
            if (unit == null || _state.Grid == null
                || !_state.Grid.TryFindPosition(unit, out int actualRow, out int actualCol)
                || actualRow != fromRow || actualCol != fromCol
                || !CanMoveUnit(unit, toRow, toCol))
                return false;

            _state.Grid.Set(fromRow, fromCol, null);
            _state.Grid.Set(toRow, toCol, unit);
            return true;
        }

        public bool TrySwapDeployment(int fromRow, int fromCol, int toRow, int toCol,
            int frontRow, int backRow)
        {
            if (!BattlePlacementRules.CanSwap(_state.Grid, fromRow, fromCol, toRow, toCol,
                frontRow, backRow))
                return false;
            _state.Grid.Swap(fromRow, fromCol, toRow, toCol);
            return true;
        }

        public void CommitRetreat(ArmyData army)
        {
            _state.RetreatingArmy = army;
        }

        public static BattleAttackApplication ResolveStandaloneAttack(UnitData attacker, UnitData defender,
            BattleChallengeRollResult roll, AbilityMagnitudes magnitudes,
            UnitData attackerHero = null, UnitData defenderHero = null)
        {
            ApplyFateState(attackerHero, defenderHero, roll);
            BattleResolutionRules.GroundAttackOutcome outcome = BattleResolutionRules.ResolveGroundAttack(
                roll.AttackerDice, roll.DefenderDice, attacker, defender, magnitudes);
            var application = new BattleAttackApplication
            {
                Outcome = outcome,
                Damage = outcome.Damage,
            };
            if (defender == null)
                return application;

            if (outcome.Damage > 0)
            {
                defender.HitPointsCurrent = Mathf.Max(0, defender.HitPointsCurrent - outcome.Damage);
                ChallengeResult.ApplyBerserkOnHit(defender, magnitudes);
            }
            application.DefenderDied = defender.HitPointsCurrent <= 0;
            return application;
        }

        public BattleAttackApplication ResolveAndApplyGroundAttack(UnitData attacker, UnitData defender,
            BattleChallengeRollResult roll, UnitData attackerHero = null, UnitData defenderHero = null)
        {
            ApplyFateState(attackerHero, defenderHero, roll);
            BattleResolutionRules.GroundAttackOutcome outcome = BattleResolutionRules.ResolveGroundAttack(
                roll.AttackerDice, roll.DefenderDice, attacker, defender, _magnitudes);
            BattleAttackApplication application = ApplyGroundAttack(attacker, defender, outcome.Damage);
            application.Outcome = outcome;
            return application;
        }

        public BattleAttackApplication ApplyGroundAttack(UnitData attacker, UnitData defender, int damage)
        {
            var application = new BattleAttackApplication { Damage = Mathf.Max(0, damage) };
            if (attacker == null || defender == null)
                return application;

            if (application.Damage > 0)
            {
                defender.HitPointsCurrent = Mathf.Max(0, defender.HitPointsCurrent - application.Damage);
                ChallengeResult.ApplyBerserkOnHit(defender, _magnitudes);
            }

            application.DefenderDied = defender.HitPointsCurrent <= 0;

            // Collateral is resolved while the primary target still occupies its grid cell.
            ResolveSecondarySkills(attacker, defender, application.Damage, application);

            if (application.DefenderDied)
                application.Removals.Add(RemoveUnit(defender, announceKiller: true));
            else if (BattleSimulationKernel.SuppressesTurn(application.Damage, attacker.Abilities))
            {
                application.Shocked = SkipPendingTurn(defender);
            }

            return application;
        }

        public bool SkipPendingTurn(UnitData unit)
        {
            if (_state.TurnOrder == null)
                return false;
            int index = _state.TurnOrder.IndexOf(unit);
            if (index <= _state.TurnIndex)
                return false;
            _state.TurnOrder.RemoveAt(index);
            return true;
        }

        public BattleUnitRemoval RemoveUnit(UnitData unit, bool announceKiller)
        {
            ArmyData deadSide = _state.OwningArmy(unit);
            ArmyData killerSide = _state.OpposingArmy(deadSide);

            if (_state.Grid != null && _state.Grid.TryFindPosition(unit, out int row, out int col))
                _state.Grid.Set(row, col, null);
            _state.Attacker?.Members.Remove(unit);
            _state.Defender?.Members.Remove(unit);
            StealthSystem.OnUnitRemoved(unit);

            if (_state.TurnOrder != null)
            {
                int index = _state.TurnOrder.IndexOf(unit);
                if (index >= 0)
                {
                    _state.TurnOrder.RemoveAt(index);
                    if (index <= _state.TurnIndex)
                        _state.TurnIndex--;
                }
            }

            return new BattleUnitRemoval(unit, deadSide, killerSide, announceKiller);
        }

        public BattleEndStatus EvaluateBattleEnd()
        {
            bool attackerAlive = BattleInitiator.IsCombatCapable(_state.Attacker);
            bool defenderAlive = BattleInitiator.IsCombatCapable(_state.Defender);
            return new BattleEndStatus(!(attackerAlive && defenderAlive), attackerAlive, defenderAlive);
        }

        public BattlePostActionResolution EvaluatePostAction()
        {
            BattleEndStatus status = EvaluateBattleEnd();
            BattleCaptureKillSequence sequence = status.Ended
                ? CreateCaptureKillSequence(status.AttackerAlive, status.DefenderAlive)
                : null;
            return new BattlePostActionResolution(status, sequence);
        }

        public BattleCaptureKillSequence CreateCaptureKillSequence(
            bool attackerAlive, bool defenderAlive)
        {
            var targets = new List<BattleCaptureKillTarget>();
            foreach ((UnitData hero, ArmyData heroArmy, ArmyData hunterArmy) entry
                in BuildCaptureKillQueue(_state.Attacker, _state.Defender, attackerAlive, defenderAlive))
            {
                targets.Add(new BattleCaptureKillTarget(entry.hero, entry.heroArmy, entry.hunterArmy));
            }
            return new BattleCaptureKillSequence(_state, targets);
        }

        public static BattleCaptureKillSequence CreateTargetOnlyCaptureKillSequence(
            BattleState state, ArmyData hunterArmy, ArmyData targetArmy)
        {
            var targets = new List<BattleCaptureKillTarget>();
            foreach (UnitData hero in HeroesOnly(targetArmy))
                targets.Add(new BattleCaptureKillTarget(hero, targetArmy, hunterArmy));
            return new BattleCaptureKillSequence(state, targets);
        }

        public static List<(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)> BuildCaptureKillQueue(
            ArmyData attacker, ArmyData defender, bool attackerAlive, bool defenderAlive)
        {
            var result = new List<(UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)>();
            if (!attackerAlive && BattleInitiator.IsCombatCapable(defender))
                foreach (UnitData hero in HeroesOnly(attacker))
                    result.Add((hero, attacker, defender));
            if (!defenderAlive && BattleInitiator.IsCombatCapable(attacker))
                foreach (UnitData hero in HeroesOnly(defender))
                    result.Add((hero, defender, attacker));
            return result;
        }

        public static BattleCaptureKillApplication ResolveAndApplyCaptureKill(
            BattleState state, BattleChallengeRollResult roll, UnitData hero, ArmyData heroArmy,
            ArmyData hunterArmy)
        {
            ApplyFateState(hunterArmy?.Commander, hero, roll);
            CaptureKillOutcome outcome = BattleResolutionRules.ResolveCaptureKill(
                roll.AttackerDice, roll.DefenderDice);
            return ApplyCaptureKillOutcome(state, outcome, hero, heroArmy, hunterArmy);
        }

        public static void ApplyFateState(UnitData attackerHero, UnitData defenderHero,
            BattleChallengeRollResult roll)
        {
            if (attackerHero != null)
                attackerHero.Fate = Mathf.Clamp(roll.AttackerFateRemaining, 0, attackerHero.FateMax);
            if (defenderHero != null)
                defenderHero.Fate = Mathf.Clamp(roll.DefenderFateRemaining, 0, defenderHero.FateMax);
        }

        public static BattleCaptureKillApplication ApplyCaptureKillOutcome(BattleState state,
            CaptureKillOutcome outcome, UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)
        {
            CaptureKillOutcome effective = outcome;
            if (effective == CaptureKillOutcome.Escaped && heroArmy != null && heroArmy.IsGarrison)
                effective = CaptureKillOutcome.Captured;

            bool imprisoned = false;
            bool removed = false;
            if (effective == CaptureKillOutcome.Captured)
            {
                imprisoned = TryImprison(state, hero, heroArmy, hunterArmy);
                if (!imprisoned)
                {
                    RemoveHero(state, heroArmy, hero);
                    removed = true;
                    effective = CaptureKillOutcome.Killed;
                }
            }
            else if (effective == CaptureKillOutcome.Killed)
            {
                RemoveHero(state, heroArmy, hero);
                removed = true;
            }

            bool needsRetreat = heroArmy != null && heroArmy.Members.Count > 0
                && !BattleInitiator.IsCombatCapable(heroArmy);
            return new BattleCaptureKillApplication(outcome, effective, imprisoned, removed, needsRetreat);
        }

        public void CompleteBattle(bool attackerAlive, bool defenderAlive,
            HexSelectionController hexSelectionController)
        {
            RevertTemporaryBattleStats(_state.Attacker, _magnitudes);
            RevertTemporaryBattleStats(_state.Defender, _magnitudes);
            AiMatchStats.RecordBattle(_state.Attacker?.Owner, _state.Defender?.Owner,
                attackerAlive, defenderAlive);

            bool attackerEmpty = _state.Attacker != null && _state.Attacker.Members.Count == 0;
            bool defenderEmpty = _state.Defender != null && _state.Defender.Members.Count == 0;
            if (attackerEmpty != defenderEmpty)
                HandleBuildingOnArmyDefeat(
                    attackerEmpty ? _state.Defender : _state.Attacker,
                    attackerEmpty ? _state.Attacker : _state.Defender,
                    hexSelectionController);
        }

        public BattleRetreatApplication CompleteRetreat(ArmyData army, ArmyData survivingArmy,
            HexMap map, HexSelectionController hexSelectionController)
        {
            RevertTemporaryBattleStats(army, _magnitudes);
            RevertTemporaryBattleStats(survivingArmy, _magnitudes);
            BattleRetreatApplication result = PerformRetreat(
                _state, army, survivingArmy, map, hexSelectionController);
            AiMatchStats.RecordRetreat(army?.Owner, survivingArmy?.Owner, result.Destroyed);
            _state.RetreatingArmy = null;
            return result;
        }

        public static void RevertTemporaryBattleStats(ArmyData army,
            AbilityMagnitudes? magnitudesOverride = null)
        {
            if (army == null)
                return;
            AbilityMagnitudes magnitudes = magnitudesOverride ?? AbilityMagnitudes.Default;
            foreach (UnitData unit in army.Members)
            {
                if (unit.BerserkStacks <= 0)
                    continue;
                unit.Attack -= unit.BerserkStacks * magnitudes.BerserkAttackGain;
                unit.Defense += unit.BerserkDefenseLost;
                unit.BerserkStacks = 0;
                unit.BerserkDefenseLost = 0;
            }
        }

        public static void ReplenishFate(ArmyData army)
        {
            if (army == null)
                return;
            foreach (UnitData unit in army.Members)
                unit.ReplenishFateForNewBattle();
        }

        public static BattleEncounterFinalization FinalizeEncounter(BattleState state,
            HexSelectionController hexSelectionController)
        {
            if (state == null)
                return default;

            ReplenishFate(state.Attacker);
            ReplenishFate(state.Defender);

            // Determine which side actually won while battle-only summons still exist. A side may
            // legitimately finish the tactical battle with Hero + summoned combatants: stripping
            // those temporary bodies first would make IsCombatCapable false and lose the winner
            // before continuation/building/event resolution gets a chance to see it.
            HexCoord hex = state.BattleHex;
            bool attackerWonBattle = state.Attacker != null && state.Attacker.Hex.Equals(hex)
                && BattleInitiator.IsCombatCapable(state.Attacker);
            bool defenderWonBattle = state.Defender != null && state.Defender.Hex.Equals(hex)
                && BattleInitiator.IsCombatCapable(state.Defender);

            StripSummonedUnits(state, state.Attacker);
            StripSummonedUnits(state, state.Defender);

            // After stripping, only a real persistent member may carry the army back to the map.
            // Hero + summons therefore preserves the hero army as survivor; summons-only does not.
            bool attackerHere = attackerWonBattle && state.Attacker != null
                && state.Attacker.Hex.Equals(hex) && state.Attacker.Members.Count > 0;
            bool defenderHere = defenderWonBattle && state.Defender != null
                && state.Defender.Hex.Equals(hex) && state.Defender.Members.Count > 0;
            ArmyData survivor = attackerHere != defenderHere
                ? (attackerHere ? state.Attacker : state.Defender)
                : null;

            var changedHexes = new HashSet<HexCoord> { hex };
            if (state.Attacker != null)
                changedHexes.Add(state.Attacker.Hex);
            if (state.Defender != null)
                changedHexes.Add(state.Defender.Hex);
            foreach (HexCoord changedHex in changedHexes)
                VisionSystem.NotifyContentChanged(changedHex);

            hexSelectionController?.DeleteArmyIfEmptied(state.Attacker);
            hexSelectionController?.DeleteArmyIfEmptied(state.Defender);
            hexSelectionController?.RestackArmiesOn(hex, null);

            return new BattleEncounterFinalization(survivor, attackerHere, defenderHere);
        }

        public static BattleEncounterFinalization FinalizeStandaloneCaptureKill(
            ArmyData hunterArmy, ArmyData targetArmy, HexSelectionController hexSelectionController)
        {
            if (hunterArmy == null)
                return default;

            HexCoord hunterHex = hunterArmy.Hex;
            ReplenishFate(hunterArmy);
            ReplenishFate(targetArmy);
            HandleBuildingOnArmyDefeat(hunterArmy, targetArmy, hexSelectionController);

            var changedHexes = new HashSet<HexCoord> { hunterHex };
            if (targetArmy != null)
                changedHexes.Add(targetArmy.Hex);
            foreach (HexCoord changedHex in changedHexes)
                VisionSystem.NotifyContentChanged(changedHex);

            hexSelectionController?.DeleteArmyIfEmptied(targetArmy);
            if (targetArmy != null)
                hexSelectionController?.RestackArmiesOn(targetArmy.Hex, null);
            hexSelectionController?.RestackArmiesOn(hunterHex, null);

            bool hunterHere = hunterArmy.Hex.Equals(hunterHex)
                && BattleInitiator.IsCombatCapable(hunterArmy);
            return new BattleEncounterFinalization(hunterHere ? hunterArmy : null,
                attackerHere: hunterHere, defenderHere: false);
        }

        public static void HandleBuildingOnArmyDefeat(ArmyData winnerArmy, ArmyData loserArmy,
            HexSelectionController hexSelectionController)
        {
            if (loserArmy == null || loserArmy.Members.Count > 0)
                return;
            BuildingData building = BuildingRegistry.FindAt(loserArmy.Hex);
            if (building == null || building.Owner != loserArmy.Owner)
                return;

            bool otherDefenderRemains = ArmyRegistry.AllAt(loserArmy.Hex)
                .Any(resident => resident != loserArmy && resident.Owner == building.Owner
                    && BattleInitiator.IsEngageable(resident, winnerArmy?.Owner));
            if (!otherDefenderRemains)
                BuildingRegistry.CaptureOrDestroy(building, winnerArmy?.Owner, hexSelectionController);
        }

        public static bool TryFindRetreatDestination(HexMap map, ArmyData army, HexCoord battleHex,
            int battleSeed, out HexCoord destination)
        {
            destination = default;
            if (map == null || army?.Owner == null)
                return false;

            BuildingData battleHexBuilding = BuildingRegistry.FindAt(battleHex);
            bool battleHexIsOwnBarracks = battleHexBuilding != null && battleHexBuilding.Owner == army.Owner
                && battleHexBuilding.HasAbility(UnitAbilities.Barracks);

            HexCoord? target = FindNearestOwnBarracksHex(army.Owner, battleHex, battleHexIsOwnBarracks);
            if (target.HasValue && TryPickNeighborToward(map, army, battleHex, target.Value, out destination))
                return true;

            var options = new List<HexCoord>();
            foreach (HexCoord candidate in HexGridMath.Neighbors(battleHex))
                if (map.CanEnter(candidate, army))
                    options.Add(candidate);
            if (options.Count == 0)
                return false;
            int index = (int)((uint)battleSeed % (uint)options.Count);
            destination = options[index];
            return true;
        }

        public static BattleRetreatApplication PerformRetreat(BattleState state, ArmyData army,
            ArmyData survivingArmy, HexMap map, HexSelectionController hexSelectionController)
        {
            if (army == null)
                return new BattleRetreatApplication(false, false, default, null);

            HexCoord battleHex = army.Hex;
            StripSummonedUnits(state, army);

            BuildingData defendedBuilding = BuildingRegistry.FindAt(battleHex);
            bool retreatingFromOwnBuilding = defendedBuilding != null && defendedBuilding.Owner == army.Owner;
            bool retreatingFromOwnBase = retreatingFromOwnBuilding && defendedBuilding.IsBase;
            bool wasGarrison = army.IsGarrison;
            PlayerSetupData previousOwner = defendedBuilding?.Owner;

            if (state?.Grid != null)
                foreach (UnitData member in army.Members)
                    if (state.Grid.TryFindPosition(member, out int row, out int col))
                        state.Grid.Set(row, col, null);

            int seed = state?.BattleSeed ?? BuildRetreatSeed(army, battleHex);
            bool relocated = TryFindRetreatDestination(map, army, battleHex, seed, out HexCoord destination);
            List<ArmyData> contactParticipants = null;

            if (relocated)
            {
                if (retreatingFromOwnBase && wasGarrison)
                    army.IsGarrison = false;

                ArmyRegistry.MoveArmy(army, destination);
                hexSelectionController?.RestackArmiesOn(battleHex, null);
                StealthSystem.RunChecksForArrival(army, destination);

                BuildingRegistry.CaptureOrDestroyIfUndefended(destination, army.Owner,
                    hexSelectionController, army);
                hexSelectionController?.RestackArmiesOn(destination, null);

                ArmyData contactedEnemy = DelayedBattleRegistry.IsHexPending(destination)
                    ? null
                    : BattleInitiator.FindEnemyAt(destination, army);
                if (contactedEnemy != null)
                {
                    bool armyCanFight = BattleInitiator.IsCombatCapable(army);
                    bool enemyCanFight = BattleInitiator.IsCombatCapable(contactedEnemy);
                    if (armyCanFight || enemyCanFight)
                    {
                        ArmyData hunter = armyCanFight ? army : contactedEnemy;
                        ArmyData target = armyCanFight ? contactedEnemy : army;
                        contactParticipants = new List<ArmyData> { hunter, target };
                    }
                }
            }
            else
            {
                foreach (UnitData member in army.Members.ToArray())
                    StealthSystem.OnUnitRemoved(member);
                army.Members.Clear();
                hexSelectionController?.DeleteArmyIfEmptied(army);
            }

            if (retreatingFromOwnBuilding)
                TryHandoverOrDestroyVacatedBuilding(battleHex, defendedBuilding, previousOwner,
                    survivingArmy, hexSelectionController);

            hexSelectionController?.HandleRetreatFromHexEvent(battleHex, army.Owner);

            return new BattleRetreatApplication(!relocated, relocated, destination, contactParticipants);
        }

        public static void StripSummonedUnits(BattleState state, ArmyData army)
        {
            if (army == null)
                return;
            for (int i = army.Members.Count - 1; i >= 0; i--)
            {
                UnitData member = army.Members[i];
                if (!member.IsSummoned)
                    continue;
                if (state?.Grid != null && state.Grid.TryFindPosition(member, out int row, out int col))
                    state.Grid.Set(row, col, null);
                state?.TurnOrder?.Remove(member);
                army.Members.RemoveAt(i);
                StealthSystem.OnUnitRemoved(member);
            }
        }

        private bool TryFindFreeDeploymentCell(int frontRow, int backRow, out int row, out int col)
        {
            for (int c = 0; c < BattleGrid.Columns; c++)
                if (_state.Grid.Get(frontRow, c) == null)
                {
                    row = frontRow;
                    col = c;
                    return true;
                }

            for (int c = 0; c < BattleGrid.Columns; c++)
            {
                if (_state.Grid.Get(backRow, c) == null)
                {
                    row = backRow;
                    col = c;
                    return true;
                }
            }

            row = col = -1;
            return false;
        }

        private void ResolveSecondarySkills(UnitData attacker, UnitData defender, int primaryDamage,
            BattleAttackApplication application)
        {
            if (attacker == null || defender == null || primaryDamage <= 0 || _state.Grid == null)
                return;

            bool splash = attacker.HasAbility(UnitAbilities.Splash);
            bool scorcher = attacker.HasAbility(UnitAbilities.Scorcher);
            if (!splash && !scorcher)
                return;
            if (!_state.Grid.TryFindPosition(defender, out int row, out int col))
                return;

            int half = Mathf.FloorToInt(primaryDamage / 2f);
            if (half <= 0)
                return;

            var neighbours = new List<UnitData>();
            int[] dr = { -1, 1, 0, 0 };
            int[] dc = { 0, 0, -1, 1 };
            for (int i = 0; i < 4; i++)
            {
                UnitData unit = _state.Grid.Get(row + dr[i], col + dc[i]);
                if (unit != null && unit != attacker && unit != defender)
                    neighbours.Add(unit);
            }
            if (neighbours.Count == 0)
                return;

            List<BattleSimSecondaryTarget> secondaryTargets =
                BattleSimulationKernel.SelectSecondaryTargets(
                    neighbours.Count,
                    i => neighbours[i].TypeTags.Contains(UnitTypeTag.Bio),
                    splash, scorcher, _random);
            foreach (BattleSimSecondaryTarget selected in secondaryTargets)
                ApplySecondaryHit(attacker, neighbours[selected.Index], primaryDamage,
                    selected.Skill, application);
        }

        private void ApplySecondaryHit(UnitData attacker, UnitData victim, int primaryDamage, string skill,
            BattleAttackApplication application)
        {
            int damage = BattleSimulationKernel.SecondaryDamage(
                primaryDamage, victim.Abilities, _magnitudes);

            if (damage > 0)
            {
                victim.HitPointsCurrent = Mathf.Max(0, victim.HitPointsCurrent - damage);
                ChallengeResult.ApplyBerserkOnHit(victim, _magnitudes);
            }

            bool died = victim.HitPointsCurrent <= 0;
            application.SecondaryHits.Add(new BattleSecondaryHit(victim, damage, died, skill));
            if (died)
                application.Removals.Add(RemoveUnit(victim, victim.Owner != attacker.Owner));
        }

        private static List<UnitData> HeroesOnly(ArmyData army) =>
            army?.Members.FindAll(m => m.IsHero) ?? new List<UnitData>();

        private static void RemoveHero(BattleState state, ArmyData army, UnitData hero)
        {
            if (hero == null)
                return;
            if (state?.Grid != null && state.Grid.TryFindPosition(hero, out int row, out int col))
                state.Grid.Set(row, col, null);
            state?.TurnOrder?.Remove(hero);
            army?.Members.Remove(hero);
            StealthSystem.OnUnitRemoved(hero);
        }

        private static bool TryImprison(BattleState state, UnitData hero, ArmyData heroArmy, ArmyData hunterArmy)
        {
            PlayerSetupData capturer = hunterArmy?.Owner;
            if (hero == null || heroArmy == null || capturer == null
                || !capturer.CitadelHexQ.HasValue || !capturer.CitadelHexR.HasValue)
                return false;

            var citadelHex = new HexCoord(capturer.CitadelHexQ.Value, capturer.CitadelHexR.Value);
            ArmyData prison = ArmyRegistry.AllAt(citadelHex).Find(a => a.IsPrison && a.Owner == capturer);
            if (prison == null)
                return false;

            if (state?.Grid != null && state.Grid.TryFindPosition(hero, out int row, out int col))
                state.Grid.Set(row, col, null);
            state?.TurnOrder?.Remove(hero);
            heroArmy.Members.Remove(hero);

            hero.CapturedFrom = hero.Owner;
            hero.Owner = capturer;
            hero.IsPrisoner = true;
            prison.Members.Add(hero);
            VisionSystem.NotifyContentChanged(citadelHex);
            return true;
        }

        private static int BuildRetreatSeed(ArmyData army, HexCoord battleHex)
        {
            unchecked
            {
                int seed = 17;
                seed = seed * 31 + battleHex.Q;
                seed = seed * 31 + battleHex.R;
                seed = seed * 31 + (army?.Id ?? 0);
                return seed;
            }
        }

        private static HexCoord? FindNearestOwnBarracksHex(PlayerSetupData owner, HexCoord from, bool excludeBattleHex)
        {
            HexCoord? best = null;
            int bestDist = int.MaxValue;
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != owner || !building.HasAbility(UnitAbilities.Barracks))
                    continue;
                if (excludeBattleHex && building.Hex.Equals(from))
                    continue;
                int dist = HexGridMath.Distance(from, building.Hex);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = building.Hex;
                }
            }
            return best;
        }

        private static bool TryPickNeighborToward(HexMap map, ArmyData army, HexCoord battleHex, HexCoord target,
            out HexCoord destination)
        {
            destination = default;
            int bestDist = int.MaxValue;
            bool found = false;
            foreach (HexCoord candidate in HexGridMath.Neighbors(battleHex))
            {
                if (!map.CanEnter(candidate, army))
                    continue;
                int dist = HexGridMath.Distance(candidate, target);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    destination = candidate;
                    found = true;
                }
            }
            return found;
        }

        private static void TryHandoverOrDestroyVacatedBuilding(HexCoord battleHex, BuildingData building,
            PlayerSetupData previousOwner, ArmyData survivingArmy, HexSelectionController hexSelectionController)
        {
            if (building == null || survivingArmy == null || survivingArmy.Owner == null
                || survivingArmy.Owner == previousOwner)
                return;
            if (!survivingArmy.Hex.Equals(battleHex) || !BattleInitiator.IsEngageable(survivingArmy))
                return;

            bool otherDefenderRemains = ArmyRegistry.AllAt(battleHex)
                .Any(resident => resident.Owner == previousOwner
                    && BattleInitiator.IsEngageable(resident, survivingArmy.Owner));
            if (!otherDefenderRemains)
                BuildingRegistry.CaptureOrDestroy(building, survivingArmy.Owner, hexSelectionController);
        }
    }
}
