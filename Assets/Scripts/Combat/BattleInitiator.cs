using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Combat
{
    // Strategic ground-contact eligibility and opponent selection. Tactical battle and
    // hero-only Capture/Kill share this entry; stealth and aviation filter both paths.
    public static class BattleInitiator
    {
        // "Not Combat Capable": a hero-only army (or an empty one) can't fight a Ground Combat
        // round — heroes never act in BattleTurnOrder's own acting queue, even though a hero
        // standing on the tactical grid remains a legal passive attack target. At least one
        // non-hero ground combatant is required. Still used for exactly that narrower question (can this
        // army take part in the Tactical Battle Module / does it still have a real fighting
        // force) — see CheckBattleEnd, and the hunter-needs-units rule in
        // BattleScreenUI.Combat.cs.
        public static bool IsCombatCapable(ArmyData army)
        {
            if (army == null || AviationRules.IsAirArmy(army) || AviationRules.IsAirfield(army))
                return false;
            foreach (UnitData member in army.Members)
                if (member.IsGroundCombatant)
                    return true;
            return false;
        }

        // Whether `army` is a valid CONTACT target at all — merely non-empty, hero-only
        // included. Broader than IsCombatCapable on purpose: this used to just BE
        // IsCombatCapable, which made a hero-only army completely untouchable — that was only
        // ever a stand-in for not having built the Capture Kill Challenge yet (see the user's
        // own note), not a permanent rule. A hero-only army found this way never enters the
        // Tactical Battle Module (see HexSelectionController.Movement.cs's own branch) — it goes
        // straight to BattleScreenUI.BeginCaptureKillEncounter instead, since there's nothing
        // for a normal battle round to actually do against it.
        // An air army/airfield is likewise never a CONTACT target here — a ground army arriving
        // on a hex that only holds one of those must not open the ordinary battle screen against
        // aircraft; aviation has its own separate AA/air-strike resolution instead. An airfield
        // specifically is only ever emptied by capturing the Base building underneath it (see
        // BuildingRegistry.CaptureOrDestroyIfUndefended -> AviationActions.ReturnAircraftToDeck),
        // never fought directly.
        public static bool IsEngageable(ArmyData army) => army != null && army.Members.Count > 0
            && !AviationRules.IsAirArmy(army) && !AviationRules.IsAirfield(army);

        // Individual stealth (see Game.Map.StealthSystem): the observer-aware forms every
        // ground-contact/target query MUST use instead of the bare ones above. A member
        // hidden from `observer` is not on the hex as far as they're concerned — a mixed
        // army is engageable through its visible members only, and an army every one of
        // whose members is hidden from `observer` is not engageable / not combat-capable
        // to them at all (never an auto-reveal — the contact simply doesn't happen).
        public static bool IsEngageable(ArmyData army, PlayerSetupData observer)
            => army != null && !AviationRules.IsAirArmy(army) && !AviationRules.IsAirfield(army)
               && Game.Map.StealthSystem.HasAnyTargetableMember(army, observer);

        public static bool IsCombatCapable(ArmyData army, PlayerSetupData observer)
            => army != null && !AviationRules.IsAirArmy(army) && !AviationRules.IsAirfield(army)
               && Game.Map.StealthSystem.HasTargetableCombatMember(army, observer);

        // A visible combatant starts ground contact. A visible hero-only army also triggers
        // contact, but is the hunted side of a Capture/Kill encounter, not a tactical attacker.
        // Mixed armies still need a visible combatant; a hidden force stays passive.
        public static bool CanInitiateContact(ArmyData mover)
        {
            if (mover == null || AviationRules.IsAirArmy(mover) || AviationRules.IsAirfield(mover))
                return false;
            foreach (UnitData member in mover.Members)
                if (member.IsGroundCombatant && !member.IsHidden)
                    return true;
            return !IsCombatCapable(mover) && mover.Members.Any(member => member.IsHero && !member.IsHidden);
        }

        // Contact selection has two forms because many callers only ask the occupancy question
        // ("does any enemy stand here?"), while a committed encounter also knows the concrete
        // mover. Only the latter can answer "which defender is hardest for THIS attacker" without
        // inventing a context-free power scalar.
        public static ArmyData FindEnemyAt(HexCoord hex, PlayerSetupData observer) =>
            FindEnemyAt(hex, observer, null);

        public static ArmyData FindEnemyAt(HexCoord hex, ArmyData mover) =>
            FindEnemyAt(hex, mover?.Owner, mover);

        private static ArmyData FindEnemyAt(HexCoord hex, PlayerSetupData observer, ArmyData mover)
        {
            ArmyData best = null;
            WorthIt.BattleEstimate bestEstimate = default;
            bool heroOnlyMover = mover != null && !IsCombatCapable(mover);

            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner == observer || !IsEngageable(army, observer))
                    continue;
                // Heroes cannot hunt each other. A moving solo hero needs a real, visible
                // ground force on the other side; its empty tactical roster has no battle odds
                // with which to rank hunters, so use the occupancy query's stable ordering.
                if (heroOnlyMover && !IsCombatCapable(army, observer))
                    continue;

                // An occupancy-only caller never consumes combat ranking. Keep its result stable
                // without paying for a Monte Carlo estimate or reviving Attack+Defense.
                if (mover == null || heroOnlyMover)
                {
                    if (best == null || army.Id < best.Id)
                        best = army;
                    continue;
                }

                // Whole visible roster — WorthIt keeps only ground combatants itself. The defending
                // army's commander counts when the mover can see it (its initiative and Fate are part
                // of how hard that army is to beat).
                var visibleMembers = Game.Map.StealthSystem.TargetableMembersFor(army, observer)
                    .Where(member => member != null)
                    .ToList();
                var visibleRoster = visibleMembers.Select(WorthIt.FromLiveUnit).ToList();
                UnitData commander = army.Commander;
                WorthIt.BattleEstimate estimate = WorthIt.Estimate(mover, visibleRoster, 0f,
                    commander != null && visibleMembers.Contains(commander)
                        ? WorthIt.SideCommander.Of(commander) : default);

                if (best == null || IsHarderDefender(estimate, army.Id, bestEstimate, best.Id))
                {
                    best = army;
                    bestEstimate = estimate;
                }
            }
            return best;
        }

        private static bool IsHarderDefender(in WorthIt.BattleEstimate candidate, int candidateId,
            in WorthIt.BattleEstimate incumbent, int incumbentId) =>
            CompareDefenderHardness(candidate, candidateId, incumbent, incumbentId) < 0;

        // Canonical attacker-specific defender tie-break, exposed so no second copy of this
        // comparison exists anywhere in the assembly (2026-09-14, Housekeeping contact-selection
        // sync — see project owner's own report). Negative = `a` is the harder defender (lower
        // attacker WinChance, then lower surviving-HP ratio on win, then higher critical-after-win
        // chance, then lower stable army id).
        internal static int CompareDefenderHardness(in WorthIt.BattleEstimate a, int aId,
            in WorthIt.BattleEstimate b, int bId)
        {
            const float eps = 0.0001f;
            if (a.WinChance < b.WinChance - eps) return -1;
            if (a.WinChance > b.WinChance + eps) return 1;
            if (a.ExpectedSurvivingHpRatioOnWin < b.ExpectedSurvivingHpRatioOnWin - eps) return -1;
            if (a.ExpectedSurvivingHpRatioOnWin > b.ExpectedSurvivingHpRatioOnWin + eps) return 1;
            if (a.CriticalAfterBattleChance > b.CriticalAfterBattleChance + eps) return -1;
            if (a.CriticalAfterBattleChance < b.CriticalAfterBattleChance - eps) return 1;
            return aId.CompareTo(bId);
        }
    }
}
