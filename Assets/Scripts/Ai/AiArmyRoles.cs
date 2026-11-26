using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.Ai.V2;

namespace Game.Ai
{
    // Reads an army's scouting role straight from its current composition — no stored role flag
    // anywhere on ArmyData, per the AI architecture doc's own "role isn't stored, it's read from
    // the roster" call (see AI_ARCHITECTURE.html section 02). A Recce-tagged member (an r1sX
    // ability, read via Game.Cards.AbilityParams — already used by Game.Map.VisionSystem to
    // widen an army's vision radius) stands in for the design doc's "Scout" composition
    // requirement. Individual Stealth (UnitData.IsHidden, see Game.Map.StealthSystem) is a
    // separate per-unit concern and never a role signal here.
    //
    // Three army shapes AiTurnController's own PlayCard tier deliberately steers cards toward
    // (the project owner's own spec): a solo Recce party (unit or hero
    // — never diluted into a bigger roster, since a bigger army costs more AP to move and covers
    // fewer hexes per trip for the exact same Recce vision bonus), a hero-led combat escort
    // (IsHeroLedCombatArmy, no Recce), and the garrison itself as a stockpile for plain Unit cards that
    // don't yet have a hero to rally behind (see AiTurnController.TryPlayCard's own fallback).
    public static class AiArmyRoles
    {
        // A lone Recce carrier — not a "scout army" role/class, just what this exact composition
        // is: one member, and that member has Recce. Deliberately checks the TOTAL roster size,
        // not merely "how many Recce members" (project owner's own 2026-08-19 correction — the
        // old count-only check let a Recce unit buried inside a full combat army still match,
        // which then made VisitHexTask consider that combat force eligible for scouting duty AND
        // made AiAggressionPlanner treat it as an off-limits scout, neither of which is true: a
        // Recce member riding along in a bigger army is just cheap vision on a real combat force,
        // still fully usable for raids — see the project owner's own "recce-юнит с армией для
        // обзора хексов" note, not implemented yet, just no longer accidentally blocked).
        public static bool IsSoloRecce(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison || army.Members.Count != 1)
                return false;
            // Aviation is never ground Разведка's composition, whatever abilities a given aircraft
            // card happens to carry (see AiTask.AirRecon's own comment — aviation reconnaissance is
            // its own separate task/pipeline entirely, never VisitHexTask's).
            return !army.Members[0].IsAviation && AbilityParams.UnitHasAnyRecce(army.Members[0]);
        }

        // THE one AI role rule for "a body that fights a ground battle": a ground combatant
        // (UnitData.IsGroundCombatant — the battle's own rule, heroes excluded) that is not an
        // aircraft. Every body pool, escort roster, donor pick and "army has a fighting body" check
        // reads this instead of re-spelling the filter. Win-chance math needs no pre-filter at all:
        // WorthIt drops non-combatants itself.
        public static bool IsGroundBattleBody(UnitData unit) =>
            unit != null && unit.IsGroundCombatant && !unit.IsAviation;

        // Same rule on a WorthIt profile (a remembered enemy or a projected roster): the profile
        // carries IsGroundCombatant from its source; aircraft by type tag.
        public static bool IsGroundBattleBody(WorthIt.DefenderProfile profile) =>
            profile.IsGroundCombatant
            && (profile.TypeTags == null || !profile.TypeTags.Contains(UnitTypeTag.Aircraft));

        // A ground battle body that is also NOT a dedicated Recce — ATK §12's "combat body" for the
        // crude tactical-significance scalar. A role/importance choice only, never a win estimate:
        // a Recce inside an army still fights, and WorthIt counts it.
        public static bool IsGroundCombatBody(UnitData unit) =>
            IsGroundBattleBody(unit) && !AbilityParams.UnitHasAnyRecce(unit);

        public static bool IsGroundCombatBody(WorthIt.DefenderProfile profile) =>
            IsGroundBattleBody(profile) && !AbilityParams.AbilitiesHaveAnyRecce(profile.Abilities);

        // A lone resource-collector carrier — the same "belongs solo" shape as IsSoloRecce, for the
        // same reason (project owner's own 2026-09-21 call: a bigger army costs more AP to move for
        // no extra collection benefit, and a solo collector should be free to go anywhere a known
        // resource hex needs it without dragging combat units along). One member, non-hero,
        // non-aviation, carrying ANY CollectX ability (see UnitAbilities.CollectAbilities) —
        // resource-type match against the specific site is the caller's job, not this shape check's.
        public static bool IsSoloCollector(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison || army.Members.Count != 1)
                return false;
            UnitData member = army.Members[0];
            if (member.IsHero || member.IsAviation)
                return false;
            foreach (string collectAbility in UnitAbilities.CollectAbilities)
                if (member.HasAbility(collectAbility))
                    return true;
            return false;
        }

        // Garrison/prison, Recce, and hero-led-with-room armies all have their own dedicated
        // roles above — this is everything else with room: a stockpile army growing toward
        // becoming a real force, whether it's still empty or already holds a few plain units. Not
        // hero-led YET (a hero card joining one of these is exactly how it becomes
        // IsHeroLedCombatArmy instead — see AiManagementPlanner.FindPlacement's own Hero-role
        // tier), so a second hero card is never offered this same army once the first one lands.
        // Supersedes the old "only Members.Count == 0 counts" rule everywhere a card or garrison-
        // overflow unit looks for a reserve army to grow (see AiManagementPlanner.FindPlacement/
        // FindGarrisonOverflowDestination) — that rule meant a reserve army could only ever
        // receive its FIRST unit and then never another, since every path in only ever matched
        // Members.Count == 0 (the project owner's own "ИИ выставляет по одному юниту в армию"
        // report).
        public static bool IsPlainReserveArmy(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison || AbilityParams.ArmyHasAnyRecce(army)
                || AviationRules.IsAirfield(army) || AviationRules.IsAirArmy(army))
                return false;
            return army.Members.Count(m => m.IsHero) == 0 && army.HasRoom;
        }

        // A non-Recce hero's own escort — exactly one hero, no Recce member (that's
        // IsSoloRecce's job instead), not the garrison/prison. Where a plain Unit card tops up
        // first (see AiTurnController.TryPlayCard) so it grows toward a small but survivable
        // fighting force instead of spinning up yet another one-off army.
        public static bool IsHeroLedCombatArmy(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison
                || AviationRules.IsAirfield(army) || AviationRules.IsAirArmy(army))
                return false;
            return army.Members.Count(m => m.IsHero) == 1 && !AbilityParams.ArmyHasAnyRecce(army);
        }

        // A free field army with another commander lends one of its non-commander heroes to a
        // Development delivery: the hero is detached (CreateArmy/Transfer, like a garrison hero)
        // and the army stays whole. A single-hero army otherwise keeps IsHeroLed (whole army moves).
        // 2026-11-20 (owner, match Halden vs Orlan R2b) — except a home-vocation hero (a Support
        // garrison hero or a Researcher/Assembler/ApBonus/slow hero) leading bodies: its place is
        // its facility, not the head of a field formation, so it steps out alone and the bodies
        // stay (when the heroless remainder still fits). Halden Bolt Jax led #40 [Crawler] at Base
        // (-1,-3) while the Citadel Factory waited; the whole-army walk (activation 2-3 every turn,
        // move 2) was refused every turn T28-T35.
        public static bool IsDetachedFieldDelivery(ArmyData army, UnitData hero) =>
            army != null && hero != null && !army.IsGarrison && !army.IsPrison
            && !AviationRules.IsAirfield(army) && !AviationRules.IsAirArmy(army)
            && hero.IsHero && !hero.IsPrisoner && army.Members.Contains(hero)
            && army.CanLeaveWithoutOvercrowding(hero)
            && (army.Members.Count(m => m != null && m.IsHero) >= 2
                    && army.Commander != null && !ReferenceEquals(army.Commander, hero)
                || army.Members.Count(m => m != null && m.IsHero) == 1 && army.Members.Count > 1
                    && HeroRoleEvaluator.IsHomeHero(hero));

        // Any hero-led army at all — bare, Recce-carrying, or already escorted, the only thing
        // that matters is "exactly one hero". Broader than IsHeroLedCombatArmy (excludes Recce)
        // and IsSoloRecce (excludes non-Recce escorts) on purpose: AiEconomyPlanner.
        // FindNearestHero's own "герой с армией или без (разведчик)" spec for Экономика · Задача
        // 1 — only a hero can build an extraction facility, and which hero is otherwise
        // unconstrained.
        public static bool IsHeroLed(ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison
                || AviationRules.IsAirfield(army) || AviationRules.IsAirArmy(army))
                return false;
            return army.Members.Count(m => m.IsHero) == 1;
        }

        // A hero-led army with no escorts AT ALL yet — too fragile for AiScoutPlanner's normal
        // into-the-fog search. No longer
        // scouts on its own — the project owner dropped that composition from Разведка · Задача
        // 1 — it just walks home and waits at the garrison for its first escort instead (see
        // AiTurnController.TryReturnHomeCandidates).
        public static bool IsSoloHeroAwaitingEscort(ArmyData army)
        {
            return IsHeroLedCombatArmy(army) && army.Members.Count == 1;
        }

        // Structural field-force rule shared by Analysis snapshots and match diagnostics.
        public static bool IsStructuralGroundCombatActor(ArmyData army) =>
            army != null && !army.IsPrison && !army.IsGarrison && !army.IsAirArmy && !army.IsAirfield
            && !IsSoloRecce(army) && !IsSoloHeroAwaitingEscort(army) && army.Members.Count > 0;

        // Guards a second-base garrison's own defenders from ever being pulled below secure by a
        // Raid/Defence/reorg donor pull (project owner's own report: a fresh base's garrison could
        // get seeded, then immediately stripped back down by ordinary recruitment, leaving an
        // "unguarded enemy building" the AI itself created). Citadel-only exempt BY DEFAULT on
        // purpose — its own emergency defence (see AiDefencePlanner.TryDefencePreemptCandidates)
        // already has the right to strip anything, and every EXISTING caller (RaidWeakerArmyTask's
        // own recruit picks, GarrisonReorgTask's own balance/composition tiers) already relies on
        // that same unconditional citadel access — `allowCitadelEmergency` defaults to true so none
        // of them change behavior from this method's own signature change below.
        //
        // 2026-08-24 tightened (project owner's own SecureBase spec) from the original bare
        // "Members.Count > 1" (never take the literal last body) to the real secure floor —
        // secureBaseMinNonHeroUnits headcount: taking a NON-hero from a
        // non-citadel garrison is only allowed if it would still have that many non-hero members
        // left afterward, so recruitment can never pull an already-secure second base back down
        // below secure, and can never touch an already-fragile one at all (remaining count would
        // fall below the floor). A hero leaving is still governed by the old coarser "don't take
        // the literal last body" rule — heroes never count toward the secure headcount either way
        // (only non-heroes count toward secureBaseMinNonHeroUnits), so a lone hero minding a fresh base's garrison
        // stays put exactly like before, until AiManagementPlanner's own placement priority (see
        // GarrisonHexesForPlacement) routes a real replacement in.
        //
        // `allowCitadelEmergency` (2026-08-24 P0 fix, project owner's own report): SecureBaseTask's
        // own donor search is the first caller that must NOT get the citadel exemption — unlike an
        // occasional Raid/Reorg recruit, SecureBase actively loops "find the nearest donor with a
        // spareable unit" call after call until a base is secure, and the citadel is very often the
        // nearest one, so leaving it unconditionally exempt could drain it down to zero non-hero
        // defenders over a few of those trips. Passing false applies the SAME secureBaseMinNonHeroUnits
        // floor to the citadel that non-citadel bases already get.
        //
        // 2026-09-25 (V2 final audit F2): the default is now FALSE. Every V2 lane (Raid / Attack /
        // ActiveDefence donors, Economy / Development / Recon garrison extraction, Analysis' free-
        // power projection) called this without the argument, so the citadel had NO floor at all
        // and a single raid could leave it with zero non-hero defenders (Vashti T11: "+3 body from
        // 1 donor" took all three). One rule for every caller keeps planner == executor.
        public static bool CanSpareGarrisonMember(PlayerSetupData player, ArmyData source, UnitData unit, bool allowCitadelEmergency = false)
            => CanSpareGarrisonMembers(player, source,
                unit == null ? null : new[] { unit }, allowCitadelEmergency);

        // 2026-09-30 (user decision) — a garrison hero: a hero whose card carries the Support type
        // tag (ApBonus, Researcher and Assembler heroes are authored with it). Its place is the
        // hex of its Citadel / Base: the garrison, or (2026-11-18, owner) a hero-only bench on that
        // hex when the garrison's slots are needed for the defence (ReorgViability.IsHexBench) —
        // Housekeeping never puts it into a fighting formation, and an active task takes it only as
        // a priced fallback (GarrisonHeroFallbackCost) when no other hero qualifies.
        public static bool IsGarrisonHero(UnitData unit) =>
            unit != null && unit.IsHero && unit.TypeTags.Contains(UnitTypeTag.Support);

        // THE one enumeration of the modes whose duty on `hex` `unit` qualifies for: an own facility of
        // the mode on the hex, or (preparationSite, when given) the hex the player's preparation has
        // selected for that mode. Every operator predicate below is a filter over this list.
        private static IEnumerable<ResearchProductionMode> DutyModes(PlayerSetupData player, HexCoord hex,
            UnitData unit, System.Func<ResearchProductionMode, HexCoord?> preparationSite)
        {
            if (player == null || unit == null)
                yield break;
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner != player)
                yield break;
            foreach (ResearchProductionMode mode in new[]
                     { ResearchProductionMode.Research, ResearchProductionMode.Production })
            {
                if (!unit.HasAbility(ResearchProductionSystem.RoleAbility(mode)))
                    continue;
                bool facility = building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode));
                if (facility || (preparationSite?.Invoke(mode) is HexCoord site && site.Equals(hex)))
                    yield return mode;
            }
        }

        // A hero that operates an own Research/Production facility on `hex`: the facility's
        // role hero standing there (ResearchProductionSystem.FindActors — the same source the
        // facility's own eligibility and ArmyReorgAnalyzer.MarkDevelopmentOperators read).
        public static bool IsFacilityOperator(PlayerSetupData player, HexCoord hex, UnitData unit) =>
            unit != null && unit.IsHero && DutyModes(player, hex, unit, null).Any();

        // Does the Research/Production duty of `army`'s hex lose a mode if `hero` leaves together with
        // `army`? A duty is an own facility of the mode on the hex, or (preparationSite) the hex the
        // player's preparation has selected for that mode. False when another qualified hero stays
        // on the hex OUTSIDE `army` (an operator inside the leaving army is not a kept operator).
        // A single-member extraction supplies its actual departing set instead of the whole army.
        // THE one answer for Housekeeping's release, Economy's builder admissibility and Provisioning.
        public static bool FacilityNeedsHero(PlayerSetupData player, ArmyData army, UnitData hero,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null,
            IReadOnlyCollection<UnitData> departingMembers = null)
        {
            if (army == null)
                return false;
            return DutyModes(player, army.Hex, hero, preparationSite).Any(mode =>
                !ResearchProductionSystem.FindActors(player, army.Hex, mode)
                    .Any(a => a != hero && !(departingMembers ?? army.Members).Contains(a)));
        }

        // `unit` is an operator of an own facility on `hex`, or (preparationSite) of the preparation
        // site the player has selected there for a mode it qualifies for.
        public static bool IsDutyOperator(PlayerSetupData player, HexCoord hex, UnitData unit,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null) =>
            unit != null && unit.IsHero && DutyModes(player, hex, unit, preparationSite).Any();

        // The whole field army moving off its hex would leave a served duty without its operator.
        // A shape fact apart from IsHeroLed: whether this army MAY take a new remote assignment.
        public static bool DepartureStripsOperator(PlayerSetupData player, ArmyData army,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null) =>
            army != null && !army.IsGarrison && !army.IsPrison
            && army.Members.Any(m => m != null && m.IsHero && !m.IsPrisoner
                && FacilityNeedsHero(player, army, m, preparationSite));

        // Batch form is the canonical safety check for atomic ground-combat assembly. Checking
        // candidates one-by-one against the unchanged source could approve several removals that
        // collectively cross the protected garrison floor.
        public static bool CanSpareGarrisonMembers(PlayerSetupData player, ArmyData source,
            IEnumerable<UnitData> units, bool allowCitadelEmergency = false) =>
            GarrisonSpareCheck(player, source, allowCitadelEmergency)(units);

        // The same answer as CanSpareGarrisonMembers for any departing set from `source`, with the
        // garrison's floor and spareable bodies measured once, on the first set that removes a
        // body: a planner testing several sets against the SAME unchanged source pays the power
        // read (whole hand and deck, known threats, kept-set search) once. Discard it as soon as
        // `source`, the player's force or the known enemies may have changed.
        public static Func<IEnumerable<UnitData>, bool> GarrisonSpareCheck(PlayerSetupData player,
            ArmyData source, bool allowCitadelEmergency = false)
        {
            List<UnitData> bodies = null;
            float? floor = null;
            return units => CanSpareGarrisonMembers(player, source, units, allowCitadelEmergency,
                ref bodies, ref floor);
        }

        private static bool CanSpareGarrisonMembers(PlayerSetupData player, ArmyData source,
            IEnumerable<UnitData> units, bool allowCitadelEmergency,
            ref List<UnitData> bodies, ref float? floor)
        {
            if (player == null || source == null || units == null)
                return false;

            List<UnitData> selected = units.Where(u => u != null).Distinct().ToList();
            if (selected.Count == 0 || selected.Any(u => !source.Members.Contains(u)))
                return false;

            if (!source.IsGarrison)
                return true;

            if (source.Members.Count - selected.Count < 1)
                return false;

            // 2026-09-30 (user decision) — a hero serving this hex's Research/Production facility
            // is never released, by anyone: one rule for Economy, Development, Raid and Attack.
            if (selected.Any(u => IsFacilityOperator(player, source.Hex, u)))
                return false;

            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            bool isCitadel = source.Hex.Equals(citadelHex);
            if (isCitadel && allowCitadelEmergency)
                return true;

            // Heroes never count toward the garrison's defence, so a hero-only departure is governed
            // by the "never the literal last member" rule above alone. Without this, a garrison
            // below its floor could not release even its heroes (Economy / Development operator
            // extraction), which the floor was never meant to protect.
            List<UnitData> removedBodies = selected.Where(u => u.IsGroundCombatant).ToList();
            if (removedBodies.Count == 0)
                return true;
            // 2026-09-30 (user decision) — a power floor, not a headcount: the garrison keeps a share
            // of the player's whole ground force and at least one body.
            // 2026-11-16 (owner decision) — the rule judges the departing SET (MayReleaseBodies);
            // which bodies go is the caller's choice.
            if (!floor.HasValue)
            {
                bodies = source.Members.Where(u => u != null && u.IsGroundCombatant).ToList();
                floor = GarrisonDefenceFloor(player, source.Hex, isCitadel);
            }
            List<UnitData> b = bodies;
            return MayReleaseBodies(b, removedBodies.Select(u => b.IndexOf(u)).ToList(),
                set => AiPower.EffectiveArmyPower(set.ToList()), floor.Value);
        }

        // The garrison defence floor on the player's live ground force (map + AI hand + remaining
        // deck, aviation excluded — PlayerForceAnalysis.AdditivePower, the mobilization scale).
        public static float GarrisonDefenceFloor(PlayerSetupData player, HexCoord garrisonHex, bool isCitadel)
        {
            AiHandData hand = AiHandRegistry.Peek(player);
            PlayerForceAnalysis.AdditivePower(player, ArmyRegistry.AllForOwner(player), hand?.Hand,
                hand?.RemainingDeck, out _, out float available);
            return GarrisonDefenceFloor(player, available, garrisonHex, isCitadel);
        }

        // Same floor from an already measured ground force (SelfSnapshot.AvailablePower): the
        // share floor, raised to the known threat against this garrison (GarrisonThreatFloor).
        // 2026-11-13 (user decision, F1 variant A) — the floor is the HEX's need; the garrison's
        // own bodies answer only what the own ground armies standing on that hex do not
        // (ResidentFieldPower). Every reader goes through here: the spare rule
        // (CanSpareGarrisonMembers -> NonHeroSpareable, builder escort, SpareGarrisonPower),
        // FieldStrikePotential, AttackForcePool, Housekeeping's GarrisonPowerFloor, the hand
        // demand and GarrisonRelief (AggressionDemandEvaluator.HeldBaseGarrisonShortfall).
        // Match 2026-11-13: Brynn's Base (1,-6) asked 87.7 from hand with Fester 52.4 on the hex.
        public static float GarrisonDefenceFloor(PlayerSetupData player, float groundAvailablePower,
            HexCoord garrisonHex, bool isCitadel) =>
            Math.Max(0f, GarrisonFloorBeforeResidents(player, groundAvailablePower, garrisonHex, isCitadel)
                - ResidentFieldPower(player, garrisonHex));

        // The same floor before the resident field armies' credit (ResidentFieldPower): a planner
        // that moves bodies between resident and departing armies of the hex re-reads the credit
        // on its own virtual roster (Housekeeping, ReorgViability.GarrisonFloor).
        public static float GarrisonFloorBeforeResidents(PlayerSetupData player, HexCoord garrisonHex, bool isCitadel)
        {
            AiHandData hand = AiHandRegistry.Peek(player);
            PlayerForceAnalysis.AdditivePower(player, ArmyRegistry.AllForOwner(player), hand?.Hand,
                hand?.RemainingDeck, out _, out float available);
            return GarrisonFloorBeforeResidents(player, available, garrisonHex, isCitadel);
        }

        private static float GarrisonFloorBeforeResidents(PlayerSetupData player, float groundAvailablePower,
            HexCoord garrisonHex, bool isCitadel) =>
            Math.Max(GarrisonDefenceFloor(groundAvailablePower, isCitadel),
                GarrisonThreatFloor(player, garrisonHex, isCitadel));

        // 2026-11-19 (owner, match Tessek vs Draven) — the NEED side of the same floor: how much the
        // garrison must still be GIVEN (the hand demand, GarrisonRelief's recipient shortfall, the
        // Phase B no-overfill rule). Two credits apply here and only here:
        //  * the mobile reserve — the threat part is first answered by the own field armies standing
        //    on OTHER own Bases that reach this one before the earliest enemy raising it
        //    (MobileReserveCredit: "the fist covers every Base it reaches before the threat"); the
        //    peacetime share is never credited;
        //  * an Attack host gathering on its staging Base stands there as its defence.
        // The RELEASE floor above (who may leave: every donor, the strike measures, Housekeeping)
        // takes neither: bodies freed on the strength of the fist would join the fist and leave
        // with it (found-problem pattern 36), stripping the Base the credit was for.
        public static float GarrisonNeedFloor(PlayerSetupData player, float groundAvailablePower,
            HexCoord garrisonHex, bool isCitadel)
        {
            float threat = GarrisonThreatFloor(player, garrisonHex, isCitadel, out int enemyEta);
            if (threat > 0f)
                threat = Math.Max(0f, threat - MobileReserveCredit(player, garrisonHex, enemyEta));
            return Math.Max(0f, Math.Max(GarrisonDefenceFloor(groundAvailablePower, isCitadel), threat)
                - ResidentFieldPower(player, garrisonHex, gatheringHostResides: true));
        }

        // 2026-11-19 (owner, match Tessek vs Draven: "fist power counts toward the floor of every own
        // Base it reaches before the threatening enemy") — the power of the own ground field armies
        // that stand on another own Base, hold it (the resident rule: not moving for a departing
        // operation; an Attack host gathering there counts) and reach `garrisonHex` strictly
        // before the enemy (fewer turns than `enemyEta`: on an equal turn the enemy may move
        // first; hex distance against their MaxMovement, the same upper-bound reach the threat
        // floor gives the enemy). Each army
        // lends only what its own Base does not need: its power minus that Base's unmet threat
        // (threat floor without any credit, less the garrison there). 0 without a timed threat.
        public static float MobileReserveCredit(PlayerSetupData player, HexCoord garrisonHex, int enemyEta) =>
            MobileReserveCredit(player, garrisonHex, enemyEta, null);

        // `floorOf` (2026-11-25) — the garrison floor of an own Base (hex, isCitadel); a caller that
        // prices many sites in one frozen pass (DemandLayer base candidates) memoises it, since the
        // floor of an own Base does not depend on the site. Null computes it here.
        public static float MobileReserveCredit(PlayerSetupData player, HexCoord garrisonHex, int enemyEta,
            Func<HexCoord, bool, float> floorOf)
        {
            if (player == null || enemyEta == int.MaxValue)
                return 0f;
            IReadOnlyCollection<MissionIntent> intents = MissionIntentRegistry.Peek(player)?.All;
            List<ArmyData> own = ArmyRegistry.AllForOwner(player).Where(a => a != null).ToList();
            HashSet<HexCoord> bases = new HashSet<HexCoord>(own.Where(a => a.IsGarrison).Select(a => a.Hex));
            HexCoord citadel = AiTurnController.GarrisonHexFor(player);
            var unmetByBase = new Dictionary<HexCoord, float>();
            float credit = 0f;
            foreach (ArmyData a in own)
            {
                if (a.IsGarrison || a.IsAirfield || a.IsPrison || AviationRules.IsAirArmy(a)
                    || a.Hex.Equals(garrisonHex) || !bases.Contains(a.Hex))
                    continue;
                List<UnitData> bodies = a.Members.Where(u => u != null && !u.IsPrisoner && !u.IsSummoned
                    && IsGroundBattleBody(u)).ToList();
                if (bodies.Count == 0 || LeavesOnOperation(intents, a.Id, a.Hex, gatheringHostResides: true))
                    continue;
                int eta = (int)Math.Ceiling(HexGridMath.Distance(a.Hex, garrisonHex) / (double)Math.Max(1, a.MaxMovement));
                if (eta >= enemyEta)
                    continue;
                if (!unmetByBase.TryGetValue(a.Hex, out float unmet))
                {
                    ArmyData homeGarrison = own.FirstOrDefault(g => g.IsGarrison && g.Hex.Equals(a.Hex));
                    float held = homeGarrison == null ? 0f : AiPower.EffectiveArmyPower(homeGarrison.Members
                        .Where(u => u != null && u.IsGroundCombatant).ToList());
                    float floor = floorOf != null ? floorOf(a.Hex, a.Hex.Equals(citadel))
                        : GarrisonThreatFloor(player, a.Hex, a.Hex.Equals(citadel), out _);
                    unmet = Math.Max(0f, floor - held);
                    unmetByBase[a.Hex] = unmet;
                }
                float power = AiPower.EffectiveArmyPower(bodies);
                float lent = Math.Max(0f, power - unmet);
                unmetByBase[a.Hex] = Math.Max(0f, unmet - power);
                credit += lent;
            }
            return credit;
        }

        // The ground power of the player's own field armies standing on `hex` that hold it: not
        // air, prison or airfield containers, and not the mover of an Active operation that takes
        // it away (CountsAsHexResident — the same resident rule
        // ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset reads with its committed set). A
        // lifecycle leg (a return home) is not a departure, unless it heads to another hex (a
        // GarrisonRelief detachment walking away from its donor).
        // `gatheringHostResides` — the need side only (GarrisonNeedFloor): an Attack host still
        // gathering on its staging Base counts as standing there.
        public static float ResidentFieldPower(PlayerSetupData player, HexCoord hex,
            bool gatheringHostResides = false)
        {
            if (player == null)
                return 0f;
            IReadOnlyCollection<MissionIntent> intents = MissionIntentRegistry.Peek(player)?.All;
            float power = 0f;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || a.IsGarrison || a.IsAirfield || !a.Hex.Equals(hex))
                    continue;
                List<UnitData> bodies = a.Members.Where(u => u != null && !u.IsPrisoner && !u.IsSummoned
                    && IsGroundBattleBody(u)).ToList();
                if (!CountsAsHexResident(a.IsGarrison, AviationRules.IsAirArmy(a), a.IsPrison,
                        bodies.Count > 0, LeavesOnOperation(intents, a.Id, hex, gatheringHostResides)))
                    continue;
                power += AiPower.EffectiveArmyPower(bodies);
            }
            return power;
        }

        // Does `army` (a field army) stand as its hex's resident defence — the ResidentFieldPower
        // rule for one army, membership aside (no departing operation owns it).
        public static bool IsHexResidentArmy(PlayerSetupData player, ArmyData army) =>
            player != null && army != null && !army.IsGarrison && !army.IsAirfield
            && !AviationRules.IsAirArmy(army) && !army.IsPrison
            && !LeavesOnOperation(MissionIntentRegistry.Peek(player)?.All, army.Id, army.Hex);

        // THE "who holds this hex" rule (HoldChanceAtAsset, ResidentFieldPower): a non-air,
        // non-prison army with members; a field army only while no departing operation owns it.
        public static bool CountsAsHexResident(bool isGarrison, bool isAir, bool isPrison,
            bool hasMembers, bool committedElsewhere) =>
            !isAir && !isPrison && hasMembers && (isGarrison || !committedElsewhere);

        private static bool LeavesOnOperation(IEnumerable<MissionIntent> intents, int armyId, HexCoord hex,
            bool gatheringHostResides = false)
        {
            foreach (MissionIntent i in intents ?? Enumerable.Empty<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || i.PreferredMoverArmyId != armyId)
                    continue;
                // 2026-11-19 (owner, fist muster: "the gathering fist is its Base's defence") — on
                // the need side, the host of an Attack still gathering on its staging Base is not
                // leaving that hex.
                if (gatheringHostResides && i.Attack != null && i.Attack.Phase == AttackMissionPhase.Gather
                    && i.Attack.PrimaryArmyId == armyId && i.Attack.StagingHex.HasValue
                    && i.Attack.StagingHex.Value.Equals(hex))
                    continue;
                if (!i.IsLifecycleLeg)
                    return true;
                // A return leg heading to ANOTHER hex only passes through; a relief detachment
                // always walks away from its donor. 2026-11-25 — every leg's destination
                // (MissionIntent.LifecycleDestination): a Raid/Attack recovery return or an Economy
                // return passing through used to count as this hex's resident defence.
                ActiveDefenceIntent ad = i.ActiveDefence;
                HexCoord? destination = i.LifecycleDestination;
                if ((destination.HasValue && !destination.Value.Equals(hex))
                    || (ad != null && ad.ReturnPurpose == ActiveDefenceReturnPurpose.GarrisonRelief
                        && !destination.HasValue))
                    return true;
            }
            return false;
        }

        // The peacetime share of the ground force alone.
        public static float GarrisonDefenceFloor(float groundAvailablePower, bool isCitadel) =>
            Math.Max(0f, groundAvailablePower)
                * (isCitadel ? AiConfig.garrisonDefenceShareCitadel : AiConfig.garrisonDefenceShareBase);

        // 2026-10-15 (user decision) — a garrison under a known threat keeps the power to meet it.
        // Playtest 2026-10-15: Draven's Base (-1,3) held 18 power above its 5% share floor while
        // Sentinels (52) stood two turns away; nothing asked to reinforce it. Each known mobile
        // enemy army (AiMapMemory, the Known/Threat source) that can reach an own garrison within
        // activeDefenceWithdrawMaxEnemyEta turns (hex distance against its MaxMovement: an upper
        // bound of its reach) contributes its power x aggDefenceConfidenceMargin ONCE, to the
        // nearest such garrison — the DefensiveReserve "one force contributes once" rule.
        // 2026-10-17 (user decision) — two refinements from the playtest Cassia lost at T23:
        //   * the Citadel is the exception to "nearest only": every enemy that reaches it also
        //     raises its floor, since losing it ends the game (Rust Bite 72 stood nearest to Base
        //     (-3,4), the Citadel two hexes further kept its 14.4 share and was stripped to 11);
        //   * an army that dropped out of the live sightings but stays in the Recon history
        //     (AiReconMemory.Historical, the same LastKnown contacts Threat reads) still counts:
        //     its reach grows by MaxMovement per turn unseen and its power is weighted by the
        //     history's ConfidenceDecay.
        public static float GarrisonThreatFloor(PlayerSetupData player, HexCoord garrisonHex,
            bool isCitadel = false) =>
            GarrisonThreatFloor(player, garrisonHex, isCitadel, out _);

        // Same floor, with `enemyEta`: the earliest turn (hex reach / MaxMovement) any enemy that
        // raises it can stand on the garrison; int.MaxValue when none raises it.
        public static float GarrisonThreatFloor(PlayerSetupData player, HexCoord garrisonHex,
            bool isCitadel, out int enemyEta)
        {
            enemyEta = int.MaxValue;
            if (player == null)
                return 0f;
            List<HexCoord> garrisons = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a.IsGarrison).Select(a => a.Hex).Distinct().ToList();
            return GarrisonThreatFloor(KnownThreatContacts(player), garrisons,
                garrisonHex, isCitadel, out enemyEta, player);
        }

        // 2026-11-19 (owner, base founding next to the enemy) — the threat on a Base site not yet
        // founded: every known hostile ground force that reaches `site` within
        // activeDefenceWithdrawMaxEnemyEta turns, x aggDefenceConfidenceMargin, ENEMY GARRISONS
        // INCLUDED (a garrison next door sorties against a fresh Base). Not "nearest garrison
        // only": nothing stands there yet to share the threat with. `enemyEta` as above.
        public static float FoundingSiteThreat(PlayerSetupData player, HexCoord site, out int enemyEta) =>
            FoundingSiteThreat(player, null, site, out enemyEta);

        // `contacts` (2026-11-25) — ThreatContactsOf(player) built once by a caller that prices many
        // sites in one frozen pass; null reads them here.
        public static float FoundingSiteThreat(PlayerSetupData player,
            IReadOnlyList<(AiMapMemory.KnownEnemySighting Sighting, int UnseenTurns)> contacts, HexCoord site,
            out int enemyEta)
        {
            enemyEta = int.MaxValue;
            if (player == null)
                return 0f;
            return GarrisonThreatFloor(contacts ?? KnownThreatContacts(player), new[] { site }, site, true,
                out enemyEta, player, includeGarrisons: true);
        }

        // The known hostile contacts of the floors, materialised once (2026-11-25, base candidates).
        public static List<(AiMapMemory.KnownEnemySighting Sighting, int UnseenTurns)> ThreatContactsOf(
            PlayerSetupData player) =>
            player == null ? new List<(AiMapMemory.KnownEnemySighting, int)>() : KnownThreatContacts(player).ToList();

        // The own-garrison floor on explicit contacts (same rule as GarrisonThreatFloor(player, ...)).
        public static float GarrisonThreatFloor(PlayerSetupData player,
            IReadOnlyList<(AiMapMemory.KnownEnemySighting Sighting, int UnseenTurns)> contacts,
            HexCoord garrisonHex, bool isCitadel)
        {
            if (player == null)
                return 0f;
            List<HexCoord> garrisons = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a.IsGarrison).Select(a => a.Hex).Distinct().ToList();
            return GarrisonThreatFloor(contacts ?? KnownThreatContacts(player), garrisons,
                garrisonHex, isCitadel, out _, player);
        }

        // Every known hostile contact with the turns it has gone unobserved (0 = seen now): the live
        // sightings and the Recon history (AiReconMemory.Historical), the one source of the floors.
        private static IEnumerable<(AiMapMemory.KnownEnemySighting, int)> KnownThreatContacts(PlayerSetupData player)
        {
            List<AiMapMemory.KnownEnemySighting> live = AiMapMemory.AllKnownEnemySightings(player).ToList();
            int turn = AiReconMemory.ObservedTurn(player);
            IEnumerable<(AiMapMemory.KnownEnemySighting, int)> historical = turn == int.MinValue
                ? Enumerable.Empty<(AiMapMemory.KnownEnemySighting, int)>()
                : AiReconMemory.Historical(player, AiV2Util.KnownArmyIds(live))
                    .Select(o => (new AiMapMemory.KnownEnemySighting(o.LastObservedHex, o.Owner, null,
                            o.MemberCount, o.DefenseSum, o.AttackSum, o.Defenders,
                            seenTurn: o.LastObservedTurn, armyId: o.ArmyId, isGarrison: o.IsGarrison,
                            commander: o.Commander, isAir: o.IsAir, maxMovement: o.MaxMovement),
                        Math.Max(0, turn - o.LastObservedTurn)));
            // Known sightings also contain last-known positions under fog. Their container is
            // not evidence of a fresh observation: use the same age as Historical. A currently
            // visible observation remains exact even when its stored stamp predates this scan.
            return live.Select(s => (s,
                    turn == int.MinValue || VisionSystem.IsVisible(player, s.Hex)
                        ? 0 : Math.Max(0, turn - s.SeenTurn))).Concat(historical);
        }

        // Same rule on explicit facts: the known enemy sightings and the own garrison hexes.
        public static float GarrisonThreatFloor(IEnumerable<AiMapMemory.KnownEnemySighting> enemies,
            IEnumerable<HexCoord> garrisonHexes, HexCoord garrisonHex, bool isCitadel = false) =>
            GarrisonThreatFloor((enemies ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>())
                .Select(s => (s, 0)), garrisonHexes, garrisonHex, isCitadel);

        // `enemies` — each contact with the turns it has gone unobserved (0 = a live sighting).
        // `observer` (2026-11-13): an unseen contact stands only where that player does not see
        // now (AiReconMemory.PossibleDistance, the one unobserved-army model); null keeps the bare
        // MaxMovement-per-unseen-turn advance.
        public static float GarrisonThreatFloor(
            IEnumerable<(AiMapMemory.KnownEnemySighting Sighting, int UnseenTurns)> enemies,
            IEnumerable<HexCoord> garrisonHexes, HexCoord garrisonHex, bool isCitadel,
            PlayerSetupData observer = null) =>
            GarrisonThreatFloor(enemies, garrisonHexes, garrisonHex, isCitadel, out _, observer);

        public static float GarrisonThreatFloor(
            IEnumerable<(AiMapMemory.KnownEnemySighting Sighting, int UnseenTurns)> enemies,
            IEnumerable<HexCoord> garrisonHexes, HexCoord garrisonHex, bool isCitadel,
            out int enemyEta, PlayerSetupData observer = null, bool includeGarrisons = false)
        {
            enemyEta = int.MaxValue;
            List<HexCoord> garrisons = (garrisonHexes ?? Enumerable.Empty<HexCoord>()).Distinct().ToList();
            if (!garrisons.Contains(garrisonHex))
                garrisons.Add(garrisonHex);
            float floor = 0f;
            foreach ((AiMapMemory.KnownEnemySighting enemy, int unseen) in enemies
                ?? Enumerable.Empty<(AiMapMemory.KnownEnemySighting, int)>())
            {
                // 2026-11-15 — only a ground battle body takes the garrison's structure: an airfield
                // container (recorded IsAir=false at aircraft speed) is no garrison threat either
                // (same rule as WorldAnalysis.BuildThreat).
                if (enemy.Owner?.IsEliminated == true || (enemy.IsGarrison && !includeGarrisons)
                    || enemy.Owner?.IsNeutral == true && includeGarrisons
                    || enemy.IsAir || enemy.MaxMovement <= 0
                    || (enemy.Defenders?.Count > 0 && !enemy.Defenders.Any(IsGroundBattleBody)))
                    continue;
                float confidence = unseen <= 0 ? 1f : AiReconMemory.ConfidenceDecay(unseen);
                if (confidence <= 0f)
                    continue;
                int reach = enemy.MaxMovement * AiConfigV2.activeDefenceWithdrawMaxEnemyEta;
                bool hides = observer != null && AiReconMemory.HidesInSight(enemy.Defenders);
                // 2026-11-25 — a garrison container never leaves its Base: a sortie starts from the
                // known Base hex, so its position does not drift with the turns it went unseen
                // (only FoundingSiteThreat counts garrisons). Mobile armies keep the worst-case model.
                int drift = enemy.IsGarrison ? 0 : Math.Max(0, unseen);
                int Near(HexCoord h) => AiReconMemory.PossibleDistance(observer, enemy.Hex,
                    enemy.MaxMovement, drift, enemy.IsAir, h, hidesInSight: hides, limit: reach);
                if (Near(garrisonHex) > reach)
                    continue;
                HexCoord nearest = garrisons
                    .Select(h => (Hex: h, Near: Near(h)))
                    .Where(x => x.Near <= reach)
                    .OrderBy(x => x.Near).ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                    .First().Hex;
                if (isCitadel || nearest.Equals(garrisonHex))
                {
                    floor += AiPower.SightingPower(enemy) * AiConfigV2.aggDefenceConfidenceMargin * confidence;
                    enemyEta = Math.Min(enemyEta,
                        (int)Math.Ceiling(Near(garrisonHex) / (double)Math.Max(1, enemy.MaxMovement)));
                }
            }
            return floor;
        }

        // 2026-11-16 (owner decision) — THE garrison release rule: may the bodies `released`
        // (indices into `bodies`) leave TOGETHER? The bodies that stay must hold `floor` and at
        // least one body always stays. It never picks WHICH bodies go: every caller orders its own
        // candidates (Attack, Raid and sorties strongest first, an Economy escort cheapest first)
        // and asks this rule about the set it builds. Replaces the 2026-09-30 kept-set search:
        // released − shortfall equals held − floor for every set crossing the floor, so its
        // "keep the stronger defender" tie-break split the surplus over the MOST bodies, i.e. the
        // weakest (match 2026-11-16, Cassia T27: Mule + RC Vehicle left, Sandnought stayed),
        // and the Vex T22 small-shortfall exception is dropped with it.
        public static bool MayReleaseBodies<T>(IReadOnlyList<T> bodies, ICollection<int> released,
            Func<IEnumerable<T>, float> setPower, float floor)
        {
            if (released == null || released.Count == 0)
                return true;
            int n = bodies?.Count ?? 0;
            if (setPower == null || released.Any(i => i < 0 || i >= n))
                return false;
            List<int> kept = Enumerable.Range(0, n).Where(i => !released.Contains(i)).ToList();
            return kept.Count > 0 && setPower(kept.Select(i => bodies[i])) + 0.001f >= floor;
        }

        // The strike view of MayReleaseBodies (indices into `bodies`): strongest first, a body
        // joins while the whole set may still leave. The force measures (FieldStrikePotential,
        // AttackForcePool) and Housekeeping's roster sources read it, so the Attack plans around
        // the bodies its own strongest-first assembly will actually get. Pure.
        public static HashSet<int> SpareableBodies<T>(IReadOnlyList<T> bodies,
            Func<IEnumerable<T>, float> setPower, float floor)
        {
            var spare = new HashSet<int>();
            int n = bodies?.Count ?? 0;
            if (n <= 1 || setPower == null)
                return spare;
            foreach (int i in Enumerable.Range(0, n)
                         .OrderByDescending(i => setPower(new[] { bodies[i] }))
                         .ThenBy(i => i))
            {
                spare.Add(i);
                if (!MayReleaseBodies(bodies, spare, setPower, floor))
                    spare.Remove(i);
            }
            return spare;
        }

        // Live twin of the strike view: is `unit` among the bodies the garrison's strongest-first
        // release (SpareableBodies on its live floor) would give an Attack? Readers that pick strike
        // roster members (equipment for the roster) use it, so they agree with AttackForcePool.
        public static bool IsStrikeSpareBody(PlayerSetupData player, ArmyData garrison, UnitData unit)
        {
            if (player == null || garrison == null || unit == null || !garrison.IsGarrison
                || unit.IsHero || !garrison.Members.Contains(unit))
                return false;
            List<UnitData> bodies = garrison.Members.Where(u => u != null && u.IsGroundCombatant).ToList();
            bool isCitadel = garrison.Hex.Equals(AiTurnController.GarrisonHexFor(player));
            return SpareableBodies(bodies, set => AiPower.EffectiveArmyPower(set.ToList()),
                    GarrisonDefenceFloor(player, garrison.Hex, isCitadel))
                .Contains(bodies.IndexOf(unit));
        }

        // 2026-11-16 (owner decision) — THE garrison exchange rule: body `leaving` goes out and
        // `entering` comes in. The garrison may not end below both its floor and what it held
        // before (the same "only if currently above it" semantics as the release rule). Read by
        // Housekeeping's planner and executor for a preparation host's roster exchange.
        public static bool KeepsFloorAfterExchange<T>(IReadOnlyList<T> bodies, int leaving, T entering,
            Func<IEnumerable<T>, float> setPower, float floor)
        {
            int n = bodies?.Count ?? 0;
            if (setPower == null || leaving < 0 || leaving >= n)
                return false;
            float before = setPower(bodies);
            float after = setPower(bodies.Where((b, i) => i != leaving).Concat(new[] { entering }));
            return after + 0.001f >= Math.Min(before, floor);
        }

        // Live twin of KeepsFloorAfterExchange for a garrison ArmyData: `leaving` (its body) goes
        // out, `entering` (a body of another own army on the hex) comes in.
        public static bool GarrisonExchangeKeepsFloor(PlayerSetupData player, ArmyData garrison,
            UnitData leaving, UnitData entering)
        {
            if (player == null || garrison == null || !garrison.IsGarrison || leaving == null
                || entering == null || leaving.IsHero || entering.IsHero
                || !garrison.Members.Contains(leaving) || IsFacilityOperator(player, garrison.Hex, leaving))
                return false;
            List<UnitData> bodies = garrison.Members.Where(u => u != null && u.IsGroundCombatant).ToList();
            bool isCitadel = garrison.Hex.Equals(AiTurnController.GarrisonHexFor(player));
            return KeepsFloorAfterExchange(bodies, bodies.IndexOf(leaving), entering,
                set => AiPower.EffectiveArmyPower(set.ToList()),
                GarrisonDefenceFloor(player, garrison.Hex, isCitadel));
        }

        // The best hero Economy may pull straight out of `garrison` to travel to a resource site,
        // instead of spending a hand card to materialize a fresh one for a job an idle hero already
        // owned could do (project owner's own report, 2026-09-13 — WorldAnalysis.Economy.
        // EconomyBuilderRoutes never even considered a garrisoned hero a candidate). Deterministic
        // (fastest sparable hero first, ties by CommandRating then Name) so WorldAnalysis.Economy's
        // route/cost estimate and ProvisioningManager's later transactional re-validation
        // independently agree on the exact same candidate without passing a live UnitData reference
        // between the two phases. Reuses CanSpareGarrisonMember unchanged — no second "is it safe to
        // take this hero" answer — and skips any hero currently serving as this hex's Research/
        // Production operator, read from the same ResearchProductionSystem.FindActors source
        // ArmyReorgAnalyzer.MarkDevelopmentOperators already uses, so this never proposes pulling the
        // one hero a local facility depends on.
        public static UnitData BestSparableEconomyHero(PlayerSetupData player, ArmyData garrison,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null) =>
            EconomyHeroCandidates(player, garrison, preparationSite).FirstOrDefault();

        // 2026-11-19 (fist muster) — the hero the garrison may hand to a heroless fist on its hex
        // as its commander: a sparable hero (CanSpareGarrisonMember — never a facility operator,
        // never the last member, no facility it is needed for) that is no Support garrison hero and
        // has no support vocation (both stay home by rule), the widest command first; an equal
        // command goes to the higher Fate (2026-11-25, owner: the commander's battle Fate).
        internal static UnitData BestSparableCommandHero(PlayerSetupData player, ArmyData garrison) =>
            SparableHeroes(player, garrison, null, null)
                .Where(u => !HeroRoleEvaluator.IsHomeHero(u))
                .OrderByDescending(u => u.CommandRating).ThenByDescending(u => u.Fate).ThenBy(u => u.Name)
                .FirstOrDefault();

        internal static IEnumerable<UnitData> EconomyHeroCandidates(PlayerSetupData player,
            ArmyData garrison, System.Func<ResearchProductionMode, HexCoord?> preparationSite = null) =>
            SparableHeroes(player, garrison, null, preparationSite)
                .OrderBy(u => HeroRoleEvaluator.IsHomeHero(u))
                .ThenByDescending(u => u.MoveMax).ThenByDescending(u => u.CommandRating).ThenBy(u => u.Name);

        // Development and Economy use the SAME garrison protection and extraction eligibility.
        // The role filter only narrows the candidates; it cannot bypass the source's active
        // facility operators or CanSpareGarrisonMember's defender floor.
        public static UnitData BestSparableDevelopmentHero(PlayerSetupData player,
            ArmyData garrison, string requiredRole) =>
            string.IsNullOrEmpty(requiredRole) ? null : BestSparableHero(player, garrison, requiredRole);

        private static UnitData BestSparableHero(PlayerSetupData player, ArmyData garrison,
            string requiredRole, System.Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            return SparableHeroes(player, garrison, requiredRole, preparationSite)
                .OrderBy(u => requiredRole == null && IsGarrisonHero(u))
                .ThenByDescending(u => u.MoveMax).ThenByDescending(u => u.CommandRating)
                .ThenBy(u => u.Name).FirstOrDefault();
        }

        private static IEnumerable<UnitData> SparableHeroes(PlayerSetupData player, ArmyData garrison,
            string requiredRole, System.Func<ResearchProductionMode, HexCoord?> preparationSite)
        {
            if (player == null || garrison == null || !garrison.IsGarrison)
                return Enumerable.Empty<UnitData>();
            return garrison.Members
                .Where(u => u != null && u.IsHero
                    && (requiredRole == null || u.HasAbility(requiredRole))
                    && garrison.CanLeaveWithoutOvercrowding(u)
                    && CanSpareGarrisonMember(player, garrison, u)
                    && (requiredRole != null || !FacilityNeedsHero(player, garrison, u,
                        preparationSite, new[] { u })));
        }

        // The best Recce-capable, non-aviation unit or hero Recon may pull straight out of
        // `garrison` to explore/refresh, the same gap BestSparableEconomyHero closes for Economy
        // (project owner's own follow-up report, 2026-09-13 — ScoutMoverSelector never considered a
        // garrisoned Recce carrier a candidate either, via IsSoloRecce's own army.IsGarrison
        // exclusion). Deterministic (fastest sparable carrier first, ties by Name) so
        // ScoutMoverSelector.EligibleGarrisonExtraction's cost estimate and ProvisioningManager's
        // later transactional re-validation independently agree on the same candidate. Reuses
        // CanSpareGarrisonMember unchanged — no second "is it safe to take this unit" answer.
        public static UnitData BestSparableGarrisonRecce(PlayerSetupData player, ArmyData garrison)
        {
            if (player == null || garrison == null || !garrison.IsGarrison)
                return null;

            return garrison.Members
                .Where(u => u != null && !u.IsAviation && AbilityParams.UnitHasAnyRecce(u)
                    && CanSpareGarrisonMember(player, garrison, u))
                // The carrier stays in the garrison, so its Transport support does not leave with u.
                .OrderByDescending(u => u.PermanentMoveMax)
                .ThenBy(u => u.Name)
                .FirstOrDefault();
        }
    }
}
