using Game.Ai;
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

        // The STRONGEST enemy CONTACTABLE army at `hex`, if any — null if the hex is clear or
        // only holds friendly/empty armies. See IsEngageable for what counts. Ranked by raw
        // Defense+Attack (WorthIt.DefenseSum/AttackSum, non-hero members only, no hex bonus —
        // same flat power read GarrisonReorgTask.TotalNonHeroPower/AiDefencePlanner.
        // CheatEstimateRaiderThreat already use elsewhere) rather than whichever army the
        // registry happens to enumerate first (2026-08-21 fix, project owner's own report: an
        // attacking army moving onto a multi-army hex — e.g. a citadel with the garrison PLUS a
        // still-forming raid/patrol force sitting beside it — used to fight whatever ArmyRegistry
        // returned first, which could easily be the weakest, freshly-recruited force instead of
        // the garrison or the hex's real main body). Still only ever picks ONE defending army, not
        // a merged stack — see this class's own comment on why a real "stack defends together"
        // mechanic isn't built yet; this only fixes WHICH single army gets offered up.
        public static ArmyData FindEnemyAt(HexCoord hex, PlayerSetupData mover)
        {
            ArmyData strongest = null;
            float strongestPower = float.NegativeInfinity;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                // IsEngageable(army, mover) — a defender every member of which is hidden from
                // the mover is not a contact target (see this method's own stealth note).
                if (army.Owner == mover || !IsEngageable(army, mover))
                    continue;
                float power = WorthIt.DefenseSum(army) + WorthIt.AttackSum(army);
                if (strongest == null || power > strongestPower)
                {
                    strongest = army;
                    strongestPower = power;
                }
            }
            return strongest;
        }
    }
}
