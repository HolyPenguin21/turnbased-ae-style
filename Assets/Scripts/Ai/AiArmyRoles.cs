using System;
using System.Linq;
using Game.Cards;
using Game.Map;

namespace Game.Ai
{
    // Reads an army's scouting role straight from its current composition — no stored role flag
    // anywhere on ArmyData, per the AI architecture doc's own "role isn't stored, it's read from
    // the roster" call (see AI_ARCHITECTURE.html section 02). Stealth doesn't exist in this
    // project yet (see the project owner's own call) — Recce (Game.Cards.UnitAbilities.Recce,
    // already used by Game.Map.VisionSystem to widen an army's vision radius) stands in for the
    // design doc's "Scout" composition requirement instead.
    //
    // Three army shapes AiTurnController's own PlayCard tier deliberately steers cards toward
    // (the project owner's own spec): a solo Recce party (unit or hero, see IsEmptyDeployableArmy
    // — never diluted into a bigger roster, since a bigger army costs more AP to move and covers
    // fewer hexes per trip for the exact same Recce vision bonus), a hero-led combat escort
    // (IsHeroLedCombatArmy, no Recce) that doubles as a fallback explorer once full enough (see
    // IsMakeshiftScoutCapable), and the garrison itself as a stockpile for plain Unit cards that
    // don't yet have a hero to rally behind (see AiTurnController.TryPlayCard's own fallback).
    public static class AiArmyRoles
    {
        // A scout party for this pass: not the garrison, and carrying exactly one Recce member —
        // several would stack no mechanical benefit at all (see ArmyData.HasRecce's own comment),
        // so more than one is treated as "not worth calling a dedicated scout", same as zero.
        public static bool IsScoutCapable(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison)
                return false;
            return army.Members.Count(m => m.HasAbility(UnitAbilities.Recce)) == 1;
        }

        // Whether `army` has a real roster slot at all — not the garrison (nothing there is a
        // deployable "army" a card joins) and not a Prison, whose "room" is captured enemy
        // heroes' own Command Rating headroom (see ArmyData.ComputeCapacity), not a slot the AI
        // could ever deploy a card into (see the project owner's own report: without this check
        // the AI would try to "recruit" straight into its own Prison).
        public static bool HasOpenSlot(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison)
                return false;
            return army.HasRoom;
        }

        // A fresh, empty, non-garrison army — the only kind of army a Recce or Hero card ever
        // founds (see AiTurnController.TryPlayCard). Never an army with anything already in it:
        // per the project owner's own report, a Recce unit belongs SOLO (bigger armies cost more
        // AP to move and cover fewer hexes per trip for the same vision bonus), and a Hero always
        // leads its own force rather than folding into whatever already has room.
        public static bool IsEmptyDeployableArmy(ArmyData army)
        {
            return HasOpenSlot(army) && army.Members.Count == 0;
        }

        // A non-Recce hero's own escort — exactly one hero, no Recce member (that's
        // IsScoutCapable's job instead), not the garrison/prison. Where a plain Unit card tops up
        // first (see AiTurnController.TryPlayCard) so it grows toward a small but survivable
        // fighting force instead of spinning up yet another one-off army.
        public static bool IsHeroLedCombatArmy(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison)
                return false;
            return army.Members.Count(m => m.IsHero) == 1 && !army.HasRecce;
        }

        // Any hero-led army at all — bare, Recce-carrying, or already escorted, the only thing
        // that matters is "exactly one hero". Broader than IsHeroLedCombatArmy (excludes Recce)
        // and IsScoutCapable (excludes non-Recce escorts) on purpose: AiEconomyPlanner.
        // FindNearestHero's own "герой с армией или без (разведчик)" spec for Экономика · Задача
        // 1 — only a hero can build an extraction facility, and which hero is otherwise
        // unconstrained.
        public static bool IsHeroLed(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison)
                return false;
            return army.Members.Count(m => m.IsHero) == 1;
        }

        // See AiConfig.makeshiftScoutMinMembers for what this means and why — moved there so
        // it's tunable without recompiling.
        private static int MakeshiftScoutMinMembers => AiConfig.Current.makeshiftScoutMinMembers;

        // A hero-led army sturdy enough to explore even without a dedicated Recce member — the
        // project owner's own fallback for when cards accumulate into an army (see
        // IsHeroLedCombatArmy/AiTurnController's own PlayCard tier) before a Recce card ever gets
        // drawn: sitting on a serviceable roster while waiting on the deck is worse than sending
        // it out, since it can always be pulled back or reinforced into a full combat army later.
        public static bool IsMakeshiftScoutCapable(ArmyData army)
        {
            if (!IsHeroLedCombatArmy(army))
                return false;
            return army.Members.Count >= Math.Min(MakeshiftScoutMinMembers, army.Capacity);
        }

        // A hero-led army with no escorts AT ALL yet — too fragile for AiScoutPlanner's normal
        // into-the-fog search (that's IsMakeshiftScoutCapable's job, once it's Hero+2), but not
        // idle either: the project owner's own 1.1 spec has it walk down already-VISIBLE-but-
        // unvisited hexes near home (see AiTurnController's own requireVisible scout pass and
        // TryReturnHomeCandidates) instead of just sitting at the garrison doing nothing while it
        // waits on the deck for its first escort.
        public static bool IsSoloHeroAwaitingEscort(ArmyData army)
        {
            return IsHeroLedCombatArmy(army) && army.Members.Count == 1;
        }
    }
}
