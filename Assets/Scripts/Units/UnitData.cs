using System.Collections.Generic;
using Game.Players;
using UnityEngine;

namespace Game.Units
{
    // A unit's core stats — pure data, no map presence of its own at all (only its ArmyData
    // does — see ArmyData.Controller). A unit is always exactly wherever its army is; there is
    // deliberately no per-unit Hex field to keep in sync with that.
    public class UnitData
    {
        public string Name;
        public PlayerSetupData Owner;

        // Carried over from CardDefinition.grantedAbilities at spawn time — currently only ever
        // populated for Base cards (see CardDefinition), so this is empty for every Unit/Hero
        // today, but ArmyUnitCardUI/ArmyViewerModalUI already display whatever's here so a
        // future passive-skill system just has to populate this, not add new display code.
        public readonly HashSet<string> Abilities = new HashSet<string>();
        public bool HasAbility(string ability) => Abilities.Contains(ability);

        // Restored at the start of every turn. A move never spends more than what's left — an
        // army stops short of any hex it can't fully afford (see ArmyController.MoveRoutine) —
        // so this never goes negative; only ever capped from above at Max.
        public int MoveMax;
        public int MoveCurrent;

        // How much AP it costs to activate THIS unit's army for its first move order of the
        // turn — per-unit, not a shared constant, so heavier units (tanks, etc.) cost more to
        // activate than light infantry (see ArmyData.ActivationApCost, which takes the max
        // across an army's members). Carried over from CardDefinition.activationApCost at
        // spawn time (see HexSelectionController.SpawnUnit). The activation itself — whether
        // it's already been paid this turn — is tracked per-ARMY now (ArmyData.
        // HasActivatedThisTurn), not per-unit: a unit never moves on its own, only as part of
        // an army, so there's no such thing as "this one unit already activated" any more.
        public int ActivationApCost = 1;

        // Whether this unit is a Hero card, and if so, how many army slots it unlocks (see
        // ArmyData.Capacity) — a hero-led army's capacity comes from this instead of the flat
        // no-hero default. Meaningless when IsHero is false. Carried over from
        // CardDefinition.commandRating at spawn time, same as MoveMax/ActivationApCost.
        public bool IsHero;
        public int CommandRating;

        // Meaningful only for a hero (same as CommandRating) — spent during a battle challenge
        // to re-roll an unsuccessful die in the attacker's or defender's own pool (see the
        // manual's Ground-to-Ground Combat). Not yet consumed anywhere — battle resolution
        // doesn't exist yet — just the stat, carried over from CardDefinition.fate at spawn
        // time same as everything else here.
        public int Fate;

        // The card art this unit was spawned from (see CardDefinition.art), carried over at
        // spawn time same as MoveMax/CommandRating — the map marker itself only ever shows the
        // shared army/stack icon (see GameConfig.armyIconSprite), but ArmyViewerModalUI's
        // card grid shows this real per-unit art instead.
        public Sprite Art;

        // Combat stats (see Game.Combat) — carried over from CardDefinition at spawn time, same
        // as everything else above. Range is how many rows ahead of this unit's own row it can
        // reach during Ground Combat (see the manual); a unit with Range 1 must be in the Front
        // row to attack at all.
        public int Attack;
        // The defender's own dice-pool size in a Ground-to-Ground Challenge (see the manual) —
        // carried over from CardDefinition.defenseRating, named to avoid colliding with that
        // same class's unrelated BuildingData-facing `defense` field.
        public int Defense = 1;
        // A separate damage-reduction stat from Defense (see the manual — several abilities key
        // off it specifically, e.g. "-X damage against biologic units"). Same
        // BuildingData-facing-field naming collision as Defense — carried over from
        // CardDefinition.resistanceRating.
        public int Resistance = 1;
        public int Range = 1;
        public int HitPointsMax = 1;
        public int HitPointsCurrent = 1;
        public BattleRow Row = BattleRow.Front;

        // Determines turn order within a battle round (see project_combat_design_decisions
        // memory — not yet consumed anywhere, battle resolution doesn't exist yet). A
        // non-hero unit's effective initiative is its own value here; a hero's is added as a
        // bonus on top of every unit in its army (see the manual's own base+bonus+hero fate
        // formula, simplified to a flat per-unit stat instead of an army-level sum).
        public int Initiative = 1;

        // Set when a Capture Kill Challenge captures this hero (see BattleScreenUI.Combat.cs's
        // TryImprison) — Owner has already been reassigned to the captor at that point (so
        // ArmyRegistry.AllForOwner/UI ownership tags all read correctly for a card sitting in
        // the captor's Prison army, see ArmyData.IsPrison), CapturedFrom remembers who it
        // belonged to before that, for whenever a "return prisoners" mechanic exists to read it
        // (not implemented yet — see the user's own note; no such mechanic exists in this
        // project today, only the data needed to support one later).
        public bool IsPrisoner;
        public PlayerSetupData CapturedFrom;

        public void ReplenishMoveForNewTurn()
        {
            MoveCurrent = Mathf.Min(MoveCurrent + MoveMax, MoveMax);
        }
    }
}
