using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Combat
{
    // "Initiating Battle" (see the manual) — for now, just the simplest of its seven listed
    // triggers: a non-stealthed combat capable army moves into a hex containing another
    // non-stealthed combat capable army. Stealth doesn't exist yet in this project, so every
    // army counts as non-stealthed — this reduces to "any enemy combat-capable army on the
    // hex". Siege and delay-attack aren't handled yet either — see
    // HexSelectionController.TryIssueMoveOrder for where this gets called. The empty-garrison
    // rule IS now partially covered — see FindEnemyAt's own comment — every army on the hex is
    // a real defense candidate, not just the garrison, since a full "stack" mechanic (merging
    // every defender into one combined battle) isn't built yet.
    public static class BattleInitiator
    {
        // "Not Combat Capable": a hero-only army (or an empty one) can't fight a Ground Combat
        // round — see the manual's Hero section; heroes never act in BattleTurnOrder's own
        // acting queue and can't be targeted as a regular grid unit either. At least one
        // non-hero unit is required. Still used for exactly that narrower question (can this
        // army take part in the Tactical Battle Module / does it still have a real fighting
        // force) — see CheckBattleEnd, and the hunter-needs-units rule in
        // BattleScreenUI.Combat.cs.
        public static bool IsCombatCapable(ArmyData army)
        {
            if (army == null || AviationRules.IsAirArmy(army) || AviationRules.IsAirfield(army))
                return false;
            foreach (UnitData member in army.Members)
                if (!member.IsHero)
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

        // Whether `mover` has any member that may actually START a fight — a non-hero unit
        // that is NOT itself hidden (a hidden unit never initiates auto-contact, §5; an army
        // every combat member of which is hidden just walks through, §10.11). Mixed armies
        // still initiate through their visible non-hero members.
        public static bool CanInitiateContact(ArmyData mover)
        {
            if (mover == null || AviationRules.IsAirArmy(mover) || AviationRules.IsAirfield(mover))
                return false;
            foreach (UnitData member in mover.Members)
                if (!member.IsHero && !member.IsHidden)
                    return true;
            return false;
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

            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner == observer || !IsEngageable(army, observer))
                    continue;

                // An occupancy-only caller never consumes combat ranking. Keep its result stable
                // without paying for a Monte Carlo estimate or reviving Attack+Defense.
                if (mover == null)
                {
                    if (best == null || army.Id < best.Id)
                        best = army;
                    continue;
                }

                var visibleRoster = Game.Map.StealthSystem.TargetableMembersFor(army, observer)
                    .Where(member => member != null && !member.IsHero)
                    .Select(WorthIt.FromLiveUnit)
                    .ToList();
                WorthIt.BattleEstimate estimate = WorthIt.Estimate(mover, visibleRoster, 0f);

                if (best == null || IsHarderDefender(estimate, army.Id, bestEstimate, best.Id))
                {
                    best = army;
                    bestEstimate = estimate;
                }
            }
            return best;
        }

        private static bool IsHarderDefender(in WorthIt.BattleEstimate candidate, int candidateId,
            in WorthIt.BattleEstimate incumbent, int incumbentId)
        {
            const float eps = 0.0001f;
            if (candidate.WinChance < incumbent.WinChance - eps) return true;
            if (candidate.WinChance > incumbent.WinChance + eps) return false;
            if (candidate.ExpectedSurvivingHpRatioOnWin < incumbent.ExpectedSurvivingHpRatioOnWin - eps) return true;
            if (candidate.ExpectedSurvivingHpRatioOnWin > incumbent.ExpectedSurvivingHpRatioOnWin + eps) return false;
            if (candidate.CriticalAfterBattleChance > incumbent.CriticalAfterBattleChance + eps) return true;
            if (candidate.CriticalAfterBattleChance < incumbent.CriticalAfterBattleChance - eps) return false;
            return candidateId < incumbentId;
        }
    }
}
