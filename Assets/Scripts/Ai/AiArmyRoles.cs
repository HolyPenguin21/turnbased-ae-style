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
        // and the army stays whole. The single-hero army keeps IsHeroLed (whole army moves).
        public static bool IsDetachedFieldDelivery(ArmyData army, UnitData hero) =>
            army != null && hero != null && !army.IsGarrison && !army.IsPrison
            && !AviationRules.IsAirfield(army) && !AviationRules.IsAirArmy(army)
            && hero.IsHero && !hero.IsPrisoner && army.Members.Contains(hero)
            && army.Members.Count(m => m != null && m.IsHero) >= 2
            && army.Commander != null && !ReferenceEquals(army.Commander, hero)
            && army.CanLeaveWithoutOvercrowding(hero);

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
        // garrison: Housekeeping keeps it there and never pulls it out, and an active task takes
        // it only as a priced fallback (GarrisonHeroFallbackCost) when no other hero qualifies.
        public static bool IsGarrisonHero(UnitData unit) =>
            unit != null && unit.IsHero && unit.TypeTags.Contains(UnitTypeTag.Support);

        // A hero that operates an own Research/Production facility on `hex`: the facility's
        // role hero standing there (ResearchProductionSystem.FindActors — the same source the
        // facility's own eligibility and ArmyReorgAnalyzer.MarkDevelopmentOperators read).
        public static bool IsFacilityOperator(PlayerSetupData player, HexCoord hex, UnitData unit)
        {
            if (player == null || unit == null || !unit.IsHero)
                return false;
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner != player)
                return false;
            foreach (ResearchProductionMode mode in new[]
                     { ResearchProductionMode.Research, ResearchProductionMode.Production })
                if (building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode))
                    && unit.HasAbility(ResearchProductionSystem.RoleAbility(mode)))
                    return true;
            return false;
        }

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
            if (player == null || army == null || hero == null)
                return false;
            BuildingData building = BuildingRegistry.FindAt(army.Hex);
            if (building == null || building.Owner != player)
                return false;
            foreach (ResearchProductionMode mode in new[]
                     { ResearchProductionMode.Research, ResearchProductionMode.Production })
            {
                bool facility = building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode));
                HexCoord? site = facility ? null : preparationSite?.Invoke(mode);
                if ((!facility && !(site.HasValue && site.Value.Equals(army.Hex)))
                    || !hero.HasAbility(ResearchProductionSystem.RoleAbility(mode)))
                    continue;
                if (!ResearchProductionSystem.FindActors(player, army.Hex, mode)
                        .Any(a => a != hero && !(departingMembers ?? army.Members).Contains(a)))
                    return true;
            }
            return false;
        }

        // `unit` is an operator of an own facility on `hex`, or (preparationSite) of the preparation
        // site the player has selected there for a mode it qualifies for.
        public static bool IsDutyOperator(PlayerSetupData player, HexCoord hex, UnitData unit,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            if (IsFacilityOperator(player, hex, unit))
                return true;
            if (preparationSite == null || player == null || unit == null || !unit.IsHero)
                return false;
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner != player)
                return false;
            foreach (ResearchProductionMode mode in new[]
                     { ResearchProductionMode.Research, ResearchProductionMode.Production })
                if (!building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode))
                    && preparationSite(mode) is HexCoord site && site.Equals(hex)
                    && unit.HasAbility(ResearchProductionSystem.RoleAbility(mode)))
                    return true;
            return false;
        }

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
            IEnumerable<UnitData> units, bool allowCitadelEmergency = false)
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
            // of the player's whole ground force, at least one body, and among the legal choices
            // the strongest bodies go (SpareableBodies).
            List<UnitData> bodies = source.Members.Where(u => u != null && u.IsGroundCombatant).ToList();
            HashSet<int> spare = SpareableBodies(bodies,
                set => AiPower.EffectiveArmyPower(set.ToList()),
                GarrisonDefenceFloor(player, isCitadel));
            return removedBodies.All(u => spare.Contains(bodies.IndexOf(u)));
        }

        // The garrison defence floor on the player's live ground force (map + AI hand + remaining
        // deck, aviation excluded — PlayerForceAnalysis.AdditivePower, the mobilization scale).
        public static float GarrisonDefenceFloor(PlayerSetupData player, bool isCitadel)
        {
            AiHandData hand = AiHandRegistry.Peek(player);
            PlayerForceAnalysis.AdditivePower(player, ArmyRegistry.AllForOwner(player), hand?.Hand,
                hand?.RemainingDeck, out _, out float available);
            return GarrisonDefenceFloor(available, isCitadel);
        }

        // Same floor from an already measured ground force (SelfSnapshot.AvailablePower).
        public static float GarrisonDefenceFloor(float groundAvailablePower, bool isCitadel) =>
            Math.Max(0f, groundAvailablePower)
                * (isCitadel ? AiConfig.garrisonDefenceShareCitadel : AiConfig.garrisonDefenceShareBase);

        // Which garrison bodies may leave (indices into `bodies`), decided once for the roster:
        //   * how many — the most that still lets the strongest remaining bodies hold `floor`
        //     (at least one body always stays);
        //   * which — the kept set that maximizes (power released to the field) minus (shortfall of
        //     the kept set below the floor), both on the one AiPower scale. A strong tank leaves and
        //     a weak body stays even when that leaves the garrison slightly under the floor, when
        //     that shortfall is smaller than the attack power gained (Vex T22: floor 7.4, keep
        //     Ash Drifter 6.6 = -0.8, release MT Rust Tank ~11). Ties keep the stronger defender.
        // Pure; the planner and the executor read the same answer.
        public static HashSet<int> SpareableBodies<T>(IReadOnlyList<T> bodies,
            Func<IEnumerable<T>, float> setPower, float floor)
        {
            var spare = new HashSet<int>();
            int n = bodies?.Count ?? 0;
            if (n <= 1 || setPower == null)
                return spare;
            List<int> strongestFirst = Enumerable.Range(0, n)
                .OrderByDescending(i => setPower(new[] { bodies[i] }))
                .ThenBy(i => i)
                .ToList();
            int release = 0;
            for (int k = n - 1; k >= 1; k--)
                if (setPower(strongestFirst.Take(n - k).Select(i => bodies[i])) >= floor)
                {
                    release = k;
                    break;
                }
            if (release == 0)
                return spare;

            int keep = n - release;
            List<int> bestKept = null;
            float bestValue = float.NegativeInfinity, bestKeptPower = float.NegativeInfinity;
            IEnumerable<List<int>> keptSets = n <= 10
                ? Combinations(n, keep)
                : new[] { strongestFirst.Take(keep).ToList() };
            foreach (List<int> kept in keptSets)
            {
                float keptPower = setPower(kept.Select(i => bodies[i]));
                float released = setPower(Enumerable.Range(0, n).Where(i => !kept.Contains(i))
                    .Select(i => bodies[i]));
                float value = released - Math.Max(0f, floor - keptPower);
                if (value > bestValue + 0.001f
                    || (Math.Abs(value - bestValue) <= 0.001f && keptPower > bestKeptPower + 0.001f))
                {
                    bestKept = kept;
                    bestValue = value;
                    bestKeptPower = keptPower;
                }
            }
            for (int i = 0; i < n; i++)
                if (!bestKept.Contains(i))
                    spare.Add(i);
            return spare;
        }

        private static IEnumerable<List<int>> Combinations(int n, int k)
        {
            var current = new List<int>();
            IEnumerable<List<int>> Walk(int start)
            {
                if (current.Count == k)
                {
                    yield return new List<int>(current);
                    yield break;
                }
                for (int i = start; i <= n - (k - current.Count); i++)
                {
                    current.Add(i);
                    foreach (List<int> c in Walk(i + 1))
                        yield return c;
                    current.RemoveAt(current.Count - 1);
                }
            }
            return Walk(0);
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
            BestSparableHero(player, garrison, null, preparationSite);

        // Development and Economy use the SAME garrison protection and extraction eligibility.
        // The role filter only narrows the candidates; it cannot bypass the source's active
        // facility operators or CanSpareGarrisonMember's defender floor.
        public static UnitData BestSparableDevelopmentHero(PlayerSetupData player,
            ArmyData garrison, string requiredRole) =>
            string.IsNullOrEmpty(requiredRole) ? null : BestSparableHero(player, garrison, requiredRole);

        private static UnitData BestSparableHero(PlayerSetupData player, ArmyData garrison,
            string requiredRole, System.Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            if (player == null || garrison == null || !garrison.IsGarrison)
                return null;

            // Facility operators are refused by CanSpareGarrisonMember itself.
            return garrison.Members
                .Where(u => u != null && u.IsHero
                    && (requiredRole == null || u.HasAbility(requiredRole))
                    && CanSpareGarrisonMember(player, garrison, u)
                    && (requiredRole != null || !FacilityNeedsHero(player, garrison, u,
                        preparationSite, new[] { u })))
                // A garrison hero leaves for Economy only when no other hero qualifies; Development
                // (a required role) asks for exactly those heroes.
                .OrderBy(u => requiredRole == null && IsGarrisonHero(u))
                .ThenByDescending(u => u.MoveMax)
                .ThenByDescending(u => u.CommandRating)
                .ThenBy(u => u.Name)
                .FirstOrDefault();
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
                .OrderByDescending(u => u.MoveMax)
                .ThenBy(u => u.Name)
                .FirstOrDefault();
        }
    }
}
