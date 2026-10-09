using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  LOCAL OPERATOR RELEASE — THE one rule for a Research/Production operator that stands in a
    //  field army on the hex of its own facility and may stay home when that army works elsewhere.
    //  The hero moves to the own garrison of the SAME hex, zero AP, so the facility keeps its
    //  operator (ResearchProductionSystem.FindActors reads every own army on the hex).
    //  Read by the Housekeeping planner's twin (HousekeepingExecutor live preflight) and by the
    //  pre-departure step of every ground operation (TaskExecutor); never a second predicate.
    //  Cross-hex donation of a needed operator stays forbidden (AiArmyRoles.IsFacilityOperator).
    // ===========================================================================================
    internal static class LocalOperatorRelease
    {
        // The hero may leave `from` for the local `garrison`: an operator of a facility on this hex,
        // never the army's commander (the army keeps a legal leader), the garrison is own, same hex.
        internal static bool MayLeaveForLocalGarrison(PlayerSetupData player, ArmyData from,
            UnitData hero, ArmyData garrison, out string why,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            why = null;
            if (player == null || from == null || hero == null || garrison == null)
            { why = "missing actor"; return false; }
            if (from.IsGarrison || from.IsPrison || !garrison.IsGarrison || garrison.IsPrison
                || garrison.Owner != player || from.Owner != player || !garrison.Hex.Equals(from.Hex))
            { why = "no own local garrison"; return false; }
            if (hero.Owner != player || !hero.IsHero || hero.IsPrisoner || !from.Members.Contains(hero))
            { why = "hero not a free member"; return false; }
            if (!AiArmyRoles.IsDutyOperator(player, from.Hex, hero, preparationSite))
            { why = "not an operator of a facility on this hex"; return false; }
            UnitData commander = from.Commander;
            if (commander == null || ReferenceEquals(commander, hero) || commander.IsPrisoner)
            { why = "operator is the army's only legal commander"; return false; }
            return true;
        }

        // The hero is the only qualified operator left on its facility's hex for some served mode
        // (wherever it stands now): moving it anywhere but the local garrison would risk the site.
        internal static bool IsSoleOperator(PlayerSetupData player, HexCoord hex, UnitData hero)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner != player)
                return false;
            foreach (ResearchProductionMode mode in new[]
                     { ResearchProductionMode.Research, ResearchProductionMode.Production })
                if (building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode))
                    && hero.HasAbility(ResearchProductionSystem.RoleAbility(mode))
                    && !ResearchProductionSystem.FindActors(player, hex, mode).Any(a => a != hero))
                    return true;
            return false;
        }

        // The heroes whose departure with `army` would leave a served duty without an operator.
        private static List<UnitData> DutyOperators(PlayerSetupData player, ArmyData army,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite) =>
            army.Members
                .Where(m => m != null && m.IsHero
                    && AiArmyRoles.FacilityNeedsHero(player, army, m, preparationSite))
                .ToList();

        internal static IReadOnlyList<UnitData> OperatorsNeededHome(PlayerSetupData player, ArmyData army,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite) =>
            DutyOperators(player, army, preparationSite);

        private static ArmyData LocalGarrison(PlayerSetupData player, ArmyData army) =>
            ArmyRegistry.AllAt(army.Hex)
                .FirstOrDefault(a => a != null && a.Owner == player && a.IsGarrison && !a.IsPrison);

        // THE gate of one operator staying home, shared by the departure step and every planner that
        // must know beforehand whether the step will succeed: null when the hero may be left in the
        // local garrison for free and the army keeps a legal commander and its capacity, otherwise the
        // reason. `alsoJoining` are operators already planned into the same garrison.
        private static string KeepHomeObstacle(PlayerSetupData player, ArmyData army, UnitData hero,
            ArmyData garrison, ActorCommitments commitments, IReadOnlyCollection<UnitData> alsoJoining,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite, out UnitData newLead)
        {
            newLead = null;
            bool leads = ReferenceEquals(hero, army.Commander);
            if (leads)
            {
                newLead = BestOtherCommander(army, hero, player, preparationSite);
                if (newLead == null) return "operator is the army's only legal commander";
            }
            string why = null;
            if (garrison == null) return "no local garrison";
            if (commitments != null && commitments.IsArmyClaimed(garrison.Id)) return "garrison is claimed";
            if (!leads && !MayLeaveForLocalGarrison(player, army, hero, garrison, out why, preparationSite))
                return why;
            if (hero.ActivationApCost > 0 && garrison.RequiresActivationCharge(hero))
                return "transfer would spend AP";
            // The garrison's final roster is built by the same ProjectAdd the real transfer uses
            // (normalized by CommandRating), never a raw append read through the first hero.
            var joined = new List<UnitData>(garrison.Members);
            if (alsoJoining != null)
                foreach (UnitData joining in alsoJoining)
                    joined = ArmyData.ProjectAdd(joined, joining, true);
            joined = ArmyData.ProjectAdd(joined, hero, true);
            if (!ArmyData.RosterFits(joined, true))
                return "garrison full";
            if (!leads && !army.CanLeaveWithoutOvercrowding(hero)) return "army would overcrowd";
            return null;
        }

        // Dry run of ReleaseBeforeDeparture for ALL operators the army would take away: true when
        // every one of them can stay home. Demand, Provisioning and the departure itself read this
        // one answer, so a plan is never funded for a departure that Execution would refuse.
        internal static bool CanKeepOperatorsHome(PlayerSetupData player, ArmyData army,
            ActorCommitments commitments, System.Func<ResearchProductionMode, HexCoord?> preparationSite,
            out string why)
            => CanKeepOperatorsHome(player, army, commitments, preparationSite, out why, out _);

        // The same dry run also supplies the actual departing roster to Economy. This preview
        // never enters the registry, changes a live member, or consumes an army identity.
        internal static bool CanKeepOperatorsHome(PlayerSetupData player, ArmyData army,
            ActorCommitments commitments, System.Func<ResearchProductionMode, HexCoord?> preparationSite,
            out string why, out ArmyData departure)
        {
            why = null;
            departure = army;
            if (player == null || army == null || army.IsGarrison || army.IsPrison || army.Owner != player)
                return true;
            List<UnitData> operators = DutyOperators(player, army, preparationSite);
            if (operators.Count == 0)
                return true;
            ArmyData garrison = LocalGarrison(player, army);
            departure = ArmyData.CreateVisualSnapshot();
            departure.Owner = army.Owner;
            departure.Hex = army.Hex;
            departure.IsAirArmy = army.IsAirArmy;
            departure.IsAirfield = army.IsAirfield;
            departure.Members.AddRange(army.Members);
            if (army.HasActivatedThisTurn) departure.MarkActivated();
            var joining = new List<UnitData>();
            foreach (UnitData hero in operators)
            {
                why = KeepHomeObstacle(player, departure, hero, garrison, commitments, joining,
                    preparationSite, out UnitData newLead);
                if (why != null)
                    return false;
                if (newLead != null && !departure.TryReorderCommander(newLead, out why))
                    return false;
                if (!MayLeaveForLocalGarrison(player, departure, hero, garrison, out why, preparationSite)
                    || !departure.CanLeaveWithoutOvercrowding(hero))
                { why = why ?? "army would overcrowd"; return false; }
                departure.Members.Remove(hero);
                joining.Add(hero);
            }
            return true;
        }

        // Before a ground operation moves `army` away from its base: every operator the facility
        // needs is left in the local garrison when that is legal and costs no AP. Where it is not
        // (sole commander, full garrison, activation charge, claimed garrison) the departure goes
        // on unchanged — the fallback commander stays legal — and the fact is logged. Returns the
        // number of heroes left at home. (Economy refuses such a departure itself, see
        // MissionRevalidator / OperatorDutyBlocksDeparture.)
        internal static int ReleaseBeforeDeparture(PlayerSetupData player, AiTurnContext ctx,
            ArmyData army, ActorCommitments commitments,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            if (player == null || army == null || army.IsGarrison || army.IsPrison
                || army.Owner != player || !army.Members.Any(m => m != null && m.IsHero))
                return 0;
            List<UnitData> operators = DutyOperators(player, army, preparationSite);
            if (operators.Count == 0)
                return 0;
            ArmyData garrison = LocalGarrison(player, army);
            int released = 0;
            foreach (UnitData hero in operators)
            {
                string why = KeepHomeObstacle(player, army, hero, garrison, commitments, null,
                    preparationSite, out UnitData newLead);
                if (why != null) { Log(hero, army, why); continue; }
                bool leads = ReferenceEquals(hero, army.Commander);
                if (leads && !army.TryReorderCommander(newLead, out why)) { Log(hero, army, why); continue; }
                if (leads && (!MayLeaveForLocalGarrison(player, army, hero, garrison, out why, preparationSite)
                              || !army.CanLeaveWithoutOvercrowding(hero)))
                { Log(hero, army, why ?? "army would overcrowd"); continue; }
                if (!ArmyActions.TransferMember(hero, army, garrison, ctx?.HexSelection, out why))
                { Log(hero, army, why); continue; }
                WorldDeltaLifecycle.CommitMutation();
                released++;
                AiDebugLog.Write($"[AI][V2][OperatorRelease] {hero.Name} left army #{army.Id} for garrison "
                    + $"#{garrison.Id} at ({army.Hex.Q},{army.Hex.R}) before departure; commander="
                    + $"{army.Commander?.Name}{(leads ? " (reordered)" : "")} ap=0");
            }
            return released;
        }

        private static void Log(UnitData hero, ArmyData army, string why) =>
            AiDebugLog.WriteDeduped("operator-release",
                $"[AI][V2][OperatorRelease] {hero.Name} stays in army #{army.Id} at ({army.Hex.Q},{army.Hex.R}): {why}");

        // The hero that takes command when `operatorHero` leaves: a non-operator, non-support hero
        // under whom the whole current roster still fits, best combat leadership first.
        private static UnitData BestOtherCommander(ArmyData army, UnitData operatorHero, PlayerSetupData player,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            return army.Members
                .Where(u => u != null && u.IsHero && !u.IsPrisoner && !ReferenceEquals(u, operatorHero)
                    && !AiArmyRoles.IsDutyOperator(player, army.Hex, u, preparationSite))
                .Where(u => ArmyData.ComputeCapacity(
                    new[] { u }.Concat(army.Members.Where(m => !ReferenceEquals(m, u))), false)
                    >= army.Members.Count - 1)
                .OrderBy(u => HeroRoleEvaluator.Classify(u) == HeroOperationalRole.SupportOperator ? 1 : 0)
                .ThenByDescending(u => HeroRoleEvaluator.CombatLeadershipScore(u))
                .ThenBy(u => u.Name, System.StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }
}
