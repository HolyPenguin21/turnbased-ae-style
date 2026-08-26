using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // A container of units on one hex — same data/visual split as BuildingData (see Controller
    // below for the visual half). One player can have several armies on the same hex; only one
    // marker is ever visible per (hex, owner) at a time — see HexSelectionController.
    // RestackArmiesOn — even though every non-empty army still has its own ArmyController
    // underneath. IsGarrison marks the automatically created default one every citadel starts
    // with (see CitadelSetupController) — deployed Unit/Hero cards land in the garrison first
    // (see CardHandUI.TryPlayCard), same as the original game's manual describes it, rather than
    // needing a separate "unassigned pile" data structure of its own.
    public class ArmyData
    {
        // Stable identity across the army's whole lifetime, independent of Hex — a move only ever
        // updates Hex on this SAME instance (see ArmyRegistry.MoveArmy), never recreates the object,
        // so this Id is safe for another player's own memory of it to key on. Added 2026-08-23
        // (project owner's own call) so AiMapMemory can recognize "same physical army, new
        // position" and update its sighting of it in place instead of leaving a stale record
        // behind under the army's old hex — see AiMapMemory.EnemySighting's own comment for the
        // bug this fixes.
        private static int _nextId;
        public readonly int Id;
        public ArmyData() : this(assignIdentity: true) { }
        private ArmyData(bool assignIdentity) => Id = assignIdentity ? _nextId++ : -1;
        // A last-seen roster is display-only and never enters ArmyRegistry, so it must not burn
        // a live identity merely because a human observer refreshed the same sighting.
        internal static ArmyData CreateVisualSnapshot() => new ArmyData(assignIdentity: false);
        // Populated only on CreateVisualSnapshot instances so the read-only last-seen modal
        // never consults a building that may have changed later behind fog.
        public int? VisualSnapshotDefenseBonus;
        public int? VisualSnapshotConstructionDefense;
        public string Name;
        public HexCoord Hex;
        public PlayerSetupData Owner;
        public bool IsGarrison;
        // Marks the one Prison army every citadel starts with (see CitadelSetupController) —
        // holds Captured heroes (see BattleScreenUI.Combat.cs's TryImprison), immobile like the
        // garrison but additionally: never gets a map marker (see HexSelectionController.
        // NonEmptyArmiesAt), never appears on the hex-side "pick an army to move" row (see
        // HexSelectionController.RefreshArmyButtonRow), and its contents can't be dragged/moved
        // at all once shown (see ArmyViewerModalUI.IsReadOnly folding this in).
        public bool IsPrison;
        // An airfield is an immobile aircraft container created lazily at an owned Barracks hex.
        // An air army is the mobile counterpart.  Both remain ArmyData so the registry, FOW and
        // modal stack keep one source of truth instead of a parallel aviation collection.
        public bool IsAirfield;
        public bool IsAirArmy;
        public readonly List<UnitData> Members = new List<UnitData>();

        // The player's own last battle-grid layout for this specific army (see
        // BattleScreenUI's Arrangement phase) — keyed by unit reference so it survives members
        // being added/removed; a member missing from this map on the next battle just falls back
        // to auto-fill (same rule BattleGrid.FromArmies already uses), same as a brand new army
        // with no saved layout at all.
        public readonly Dictionary<UnitData, (int row, int col)> SavedArrangement = new Dictionary<UnitData, (int row, int col)>();

        // The map-level marker for this army (see ArmyController/HexSelectionController.
        // CreateArmyMarker) — created once, alongside the ArmyData itself, and kept for its
        // whole lifetime. A unit has no marker of its own any more; this is the only one.
        public ArmyController Controller;

        // A unit never moves on its own any more — only its army does (see
        // HexSelectionController.TryIssueMoveOrder), and the garrison specifically can never
        // move at all (IsGarrison). Activation is tracked here instead of per-unit for the
        // same reason: "has this army already spent its first-move AP this turn" is a
        // per-army question now, not a per-unit one. Reset alongside every member's own
        // MoveCurrent at the start of a turn (see GameTurnController.ReplenishMoveForOwner).
        public bool HasActivatedThisTurn;

        // How much AP it costs to activate this army for its first move order of the turn —
        // the sum of every member's own ActivationApCost (a bigger army costs more to get
        // moving as a whole, not just as much as its single heaviest member).
        public int ActivationApCost => Members.Count > 0 ? Members.Sum(m => m.ActivationApCost) : 0;

        // Energy is part of activation only for a real airborne stack. Keeping it on ArmyData
        // makes the move preview, move order and future AI use the same amount.
        public int ActivationEnergyCost => AviationRules.IsAirArmy(this)
            ? Members.Sum(m => m.LaunchEnergyCost)
            : 0;

        // Shared movement — every member advances in lockstep, capped by whichever one has the
        // least left (see ArmyController.MoveRoutine); Max is the same rule applied to MoveMax,
        // i.e. the army's per-turn movement budget before anything's been spent. Both 0 for an
        // empty army rather than throwing on Members[0].
        public int CurrentMovement => Members.Count > 0
            ? Members.Min(AviationRules.EffectiveMoveCurrent) : 0;
        // The fuel penalty reduces this turn's remaining MP only. Keep the printed maximum
        // unmodified so UI correctly reads, for example, 5/10 rather than 5/5.
        public int MaxMovement => Members.Count > 0
            ? Members.Min(unit => unit.MoveMax) : 0;

        // Canon capacity rule, computed fresh (never cached) so it's always correct as members
        // come and go: no hero -> 2; garrison without a hero -> a higher default since it's
        // meant to catch everything fresh off a card before it's sorted; a hero present ->
        // that hero's own CommandRating overrides both. Hard cap — no overflow-with-penalty
        // like the original (see project_armageddon_army_mechanic memory: user explicitly
        // dropped the soft-cap penalty). This is the nominal/target number — governs whether
        // something new may be ADDED (HasRoom, GarrisonReorgTask.FindGarrisonOverflow's
        // "shrink back toward this size" target) — NOT how many of the current Members are
        // shown; see EffectiveCapacity for that.
        private const int BaseCapacity = 2;
        private const int GarrisonBaseCapacity = 4;

        public int Capacity => ComputeCapacity(Members, IsGarrison);

        // The Capacity rule as a pure function of a (candidate) member list, rather than always
        // reading this instance's own Members — lets a caller ask "what would capacity become
        // if the roster looked like THIS" before actually committing to an order.
        public static int ComputeCapacity(IEnumerable<UnitData> members, bool isGarrison)
        {
            foreach (UnitData member in members)
                if (member.IsHero)
                    return member.CommandRating;
            return isGarrison ? GarrisonBaseCapacity : BaseCapacity;
        }

        // The cap only ever bites when something is about to be ADDED (see Capacity's own
        // comment) — an already-formed roster must never shrink or go partly invisible because
        // of it. Covers both a hand-authored map army built straight past the normal cap (see
        // CitadelSetupController.SpawnNeutralArmy) and a hero dying in battle leaving more
        // survivors than the no-hero baseline alone would show — the user's own call. Anything
        // that renders "how many slots does this army have" (ArmyViewerModalUI's grid/label)
        // should read this, not the raw Capacity.
        public int EffectiveCapacity => System.Math.Max(Capacity, Members.Count);

        public bool HasRoom => Members.Count < Capacity;

        // Whether `unit` can leave this army without stranding its own remaining roster over
        // capacity — the exact guard ArmyActions.TransferMember enforces before actually
        // committing a move (see that method's own "without room for everyone else" failReason),
        // exposed here so candidate-generation code can check BEFORE proposing a transfer that's
        // guaranteed to fail. Matters most for the Garrison: a hero standing in it can be the
        // only thing keeping an otherwise-over-stuffed roster legal (see ComputeCapacity — pull
        // that hero out and capacity falls back to GarrisonBaseCapacity), so recruiting them into
        // a field army can silently violate the very capacity rule TransferMember polices.
        public bool CanLeaveWithoutOvercrowding(UnitData unit)
        {
            // Airfield capacity comes from its building, not the ordinary army/hero rule; an
            // aircraft may always leave its storage container for a compatible air army.
            if (IsAirfield)
                return Members.Contains(unit);
            var remaining = new List<UnitData>(Members);
            remaining.Remove(unit);
            return ComputeCapacity(remaining, IsGarrison) >= remaining.Count;
        }

        // Whether any member carries UnitAbilities.Recce — computed fresh every time, same
        // "never cached" rule as Capacity/ActivationApCost above, so it's always correct as
        // members come and go. The magnitude itself is a single shared UnitAbilityCatalog value
        // now (like every other ability), not per-member, so having several Recce-tagged members
        // doesn't stack anything — see Game.Map.VisionSystem.RecomputeFor, which reads this flag
        // to expand this army's own vision beyond GameConfig.armyVisionRadius's flat default.
        public bool HasRecce => Members.Exists(m => m.HasAbility(UnitAbilities.Recce));

        // Heroes always sit at the front of the roster (ArmyViewerModalUI's grid keeps them
        // there even as the player freely drags cards to reorder — see its hero-first reorder
        // clamp). A new hero goes in right after whichever heroes are already there; a regular
        // unit always goes to the very end, which trivially keeps heroes a contiguous prefix
        // without needing to re-sort the whole list.
        public void AddMemberSorted(UnitData unit)
        {
            int index = unit.IsHero ? Members.Count(m => m.IsHero) : Members.Count;
            Members.Insert(index, unit);
        }
    }
}
