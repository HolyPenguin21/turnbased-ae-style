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

        // Brand-new game/session (see ArmyRegistry.Clear, called from CitadelSetupController.
        // Start alongside every other registry wipe) — without this the id sequence just kept
        // climbing across sessions in the same process. Never compared across sessions today, so
        // purely cosmetic, but kept in sync with ArmyRegistry.Clear for the same reason every
        // other static registry gets a full wipe there.
        internal static void ResetIdentitySequence() => _nextId = 0;
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
        // Private setter — mutate only through MarkActivated/ResetActivationForNewTurn below,
        // which keep this flag and _activationCoveredUnits (the actual per-unit ledger) in sync.
        public bool HasActivatedThisTurn { get; private set; }

        // Which members' own ActivationApCost has already been paid FOR THIS ARMY this turn —
        // either in bulk, when the army itself first activated (MarkActivated covers every
        // current member at once), or individually, when a unit joined an already-activated
        // army and paid its own share on the way in (see ArmyActions.TransferMember/
        // TransferMembersAtomic/SwapMembers, all now via MarkUnitActivationPaid). Consulted by
        // RequiresActivationCharge so a unit that leaves this army and comes back later THE
        // SAME TURN is never charged its ActivationApCost twice — the original bug report
        // (project owner, 2026-09-12): cycling a hero A→B→A "collapsed" AP because the old code
        // only ever checked the coarse HasActivatedThisTurn flag on the destination, with no
        // memory of which specific units its lump activation payment already covered.
        private readonly HashSet<UnitData> _activationCoveredUnits = new HashSet<UnitData>();

        // Whether `unit` joining this army RIGHT NOW would need to pay its own ActivationApCost
        // again. False whenever the army hasn't activated yet this turn (its eventual first
        // move order will sweep `unit` into that lump payment for free, same as any other
        // current member — see MarkActivated) OR `unit` is already in the covered ledger above
        // (it — or this exact join — already paid for a spot in THIS army earlier this turn).
        public bool RequiresActivationCharge(UnitData unit)
            => HasActivatedThisTurn && !_activationCoveredUnits.Contains(unit);

        // Read-only projection boundary for AI snapshots. The authoritative ledger remains private;
        // Analysis only asks whether this exact live unit has already paid for this exact army.
        public bool HasActivationCoverageFor(UnitData unit)
            => unit != null && _activationCoveredUnits.Contains(unit);

        // Called once, the moment this army is actually given its first move order of the turn
        // (see HexSelectionController.Movement.TryIssueMoveOrder) — the lump ActivationApCost
        // payment made right then already covers every CURRENT member, so all of them become
        // ledger-covered together instead of each needing an individual charge later.
        public void MarkActivated()
        {
            HasActivatedThisTurn = true;
            foreach (UnitData member in Members)
                _activationCoveredUnits.Add(member);
        }

        // Records that `unit`'s own activation share has now been paid for THIS army — call
        // right after actually charging it for a fresh join into an already-activated army (see
        // ArmyActions.TransferMember and friends). Idempotent and safe to call unconditionally
        // on every successful join (a join into a not-yet-activated army just pre-marks a unit
        // MarkActivated would have covered anyway): once set, this unit can leave and return to
        // THIS SAME army as many times as the player likes for the rest of the turn without ever
        // being charged again.
        public void MarkUnitActivationPaid(UnitData unit) => _activationCoveredUnits.Add(unit);

        // Start of a fresh turn (see GameTurnController.ReplenishMoveForOwner) — both the flag
        // and the per-unit coverage ledger reset together; nothing paid last turn carries over.
        public void ResetActivationForNewTurn()
        {
            HasActivatedThisTurn = false;
            _activationCoveredUnits.Clear();
        }

        // Repeat-strike bookkeeping (2026-08-26 follow-up) — which hex, and whether it actually
        // landed a strike, the most recent AviationCombatPresenter.ResolveAirStrikeAtCurrentHex
        // call resolved for THIS army. Overwritten every time that resolves (whether or not it
        // actually attacked), so these only ever describe the last hex-entry resolved — read them
        // immediately after a move that might have struck, same "read once right after" pattern
        // HasActivatedThisTurn's own callers already follow (see AiAggressionPlanner.
        // TryContinueAirStrikeTask). Aviation only; a ground army never sets these.
        public HexCoord? LastAirStrikeHex;
        public bool LastAirStrikeAttacked;
        // One-shot mission-owned endpoint policy. AviationCombatPresenter consumes and clears it;
        // ordinary movement leaves it null and therefore retains Standard strike behaviour.
        public AirStrikePolicy? PendingAirStrikePolicy;

        // How much AP it costs to activate this army for its first move order of the turn —
        // the sum of every member's own ActivationApCost (a bigger army costs more to get
        // moving as a whole, not just as much as its single heaviest member).
        public int ActivationApCost => ComputeActivationApCost(Members);

        // The SAME game rule as ActivationApCost, expressed as a pure function of a (candidate)
        // roster rather than always reading this instance's own Members — exactly the shape
        // ComputeCapacity already has, and for the same reason: AI planning has to price the
        // roster an assembly WOULD produce (host members + every body about to be transferred in)
        // before actually committing to the transfers. Pricing the untouched host instead
        // systematically underprices an assembled force (AI-01: a raid funded 1 AP that really
        // cost 4 once three bodies had joined the host).
        public static int ComputeActivationApCost(IEnumerable<UnitData> members)
        {
            if (members == null) return 0;
            int total = 0;
            foreach (UnitData member in members)
                if (member != null) total += member.ActivationApCost;
            return total;
        }

        // What THIS army would really be charged for its first move order of the turn if its
        // roster were `projectedMembers`. Activation semantics for an already-activated host are
        // unchanged: the lump payment was already made, so its first-move charge stays zero and a
        // transfer's own per-unit activation share (see RequiresActivationCharge) is a separate
        // cost this deliberately does not fold in.
        public int ProjectedActivationApCost(IEnumerable<UnitData> projectedMembers)
            => HasActivatedThisTurn ? 0 : ComputeActivationApCost(projectedMembers);

        // Energy is part of activation only for a real airborne stack. Keeping it on ArmyData
        // makes the move preview, move order and future AI use the same amount.
        public int ActivationEnergyCost => AviationRules.IsAirArmy(this)
            ? Members.Sum(m => m.LaunchEnergyCost)
            : 0;

        // Shared movement — every member advances in lockstep, capped by whichever one has the
        // least left (see ArmyController.MoveRoutine); Max is the same rule applied to MoveMax,
        // i.e. the army's per-turn movement budget before anything's been spent. Both 0 for an
        // empty army rather than throwing on Members[0].
        public int CurrentMovement => ComputeCurrentMovement(Members);
        // The fuel penalty reduces this turn's remaining MP only. Keep the printed maximum
        // unmodified so UI correctly reads, for example, 5/10 rather than 5/5.
        public int MaxMovement => ComputeMaxMovement(Members);

        // Same slowest-member rule as the two properties above, as a pure function of a candidate
        // roster — a planner that projects a transfer has to know that recruiting a slower body
        // lowers the WHOLE army's movement before it promises a first step (AI-01).
        public static int ComputeCurrentMovement(IReadOnlyList<UnitData> members)
        {
            if (members == null || members.Count == 0) return 0;
            int min = int.MaxValue;
            foreach (UnitData member in members)
                if (member != null) min = System.Math.Min(min, AviationRules.EffectiveMoveCurrent(member));
            return min == int.MaxValue ? 0 : min;
        }

        public static int ComputeMaxMovement(IReadOnlyList<UnitData> members)
        {
            if (members == null || members.Count == 0) return 0;
            int min = int.MaxValue;
            foreach (UnitData member in members)
                if (member != null) min = System.Math.Min(min, member.MoveMax);
            return min == int.MaxValue ? 0 : min;
        }

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

        // THE army's commander: its first hero (heroes are kept as a contiguous prefix, see
        // TryReorderCommander). It alone gives the army its capacity (ComputeCapacity), its battle
        // Fate and its initiative bonus (BattleTurnOrder). Every "which hero leads this army"
        // question in battle, UI and AI reads this one rule. Null when the army has no hero.
        public UnitData Commander => CommanderOf(Members);

        public static UnitData CommanderOf(IEnumerable<UnitData> members)
        {
            if (members != null)
                foreach (UnitData member in members)
                    if (member != null && member.IsHero)
                        return member;
            return null;
        }

        // The Capacity rule as a pure function of a (candidate) member list, rather than always
        // reading this instance's own Members — lets a caller ask "what would capacity become
        // if the roster looked like THIS" before actually committing to an order.
        public static int ComputeCapacity(IEnumerable<UnitData> members, bool isGarrison)
        {
            int nominalCapacity = isGarrison ? GarrisonBaseCapacity : BaseCapacity;
            UnitData commander = CommanderOf(members);
            return ComputeProjectedCapacity(nominalCapacity, hasExistingHero: false,
                addedHeroCount: commander != null ? 1 : 0,
                firstAddedHeroCommandRating: commander != null ? commander.CommandRating : 0);
        }

        // Canonical capacity projection for BOTH live gameplay and read-only planning. A capacity
        // already governed by an existing commander stays as-is; otherwise the first added Hero
        // replaces the nominal field/garrison capacity with its CommandRating verbatim, including
        // zero. All deployment/planning callers must use this domain rule rather than restating it.
        public static int ComputeProjectedCapacity(int nominalCapacity, bool hasExistingHero,
            int addedHeroCount, int firstAddedHeroCommandRating)
        {
            if (hasExistingHero || addedHeroCount <= 0)
                return nominalCapacity;
            return firstAddedHeroCommandRating;
        }

        public static int ComputeProjectedCapacity(int nominalCapacity, bool hasExistingHero,
            CardDefinition incoming)
        {
            bool incomingHero = incoming != null && incoming.cardType == CardType.Hero;
            return ComputeProjectedCapacity(nominalCapacity, hasExistingHero,
                incomingHero ? 1 : 0, incomingHero ? incoming.commandRating : 0);
        }

        public static bool ProjectedRosterFits(int nominalCapacity, bool hasExistingHero,
            int projectedMemberCount, int addedHeroCount, int firstAddedHeroCommandRating)
            => projectedMemberCount <= ComputeProjectedCapacity(
                nominalCapacity, hasExistingHero, addedHeroCount, firstAddedHeroCommandRating);

        public static bool ProjectedRosterFits(int nominalCapacity, bool hasExistingHero,
            int projectedMemberCount, CardDefinition incoming)
            => projectedMemberCount <= ComputeProjectedCapacity(
                nominalCapacity, hasExistingHero, incoming);

        public bool CanFitAdditionalCard(CardDefinition incoming)
            => incoming != null
                && ProjectedRosterFits(Capacity, Members.Any(m => m != null && m.IsHero),
                    Members.Count + 1, incoming);

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

        // HasRecce was removed when Recce became parameterized (r1s0/r1s4/...) — read
        // Game.Cards.AbilityParams.ArmyHasAnyRecce(army) / GetBestRecceRadius(army) instead.

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

        // Canonical zero-AP roster-order operation: make `hero` the FIRST member, so
        // ComputeCapacity reads ITS CommandRating (see that method — first hero wins). Pure
        // reorder — membership, unit identities and count are unchanged, nothing is spent, and
        // the very next Capacity read reflects the new order. Heroes stay a contiguous prefix
        // because `hero` is itself a hero moving to the front. Rejects a non-hero, a unit not in
        // this army, or a hero that is already first (no-op).
        public bool TryReorderCommander(UnitData hero, out string fail)
        {
            fail = null;
            if (hero == null || !hero.IsHero) { fail = "not a hero"; return false; }
            int idx = Members.IndexOf(hero);
            if (idx < 0) { fail = "hero not in this army"; return false; }
            if (idx == 0) { fail = "hero is already the commander"; return false; }
            Members.RemoveAt(idx);
            Members.Insert(0, hero);
            return true;
        }
    }
}
