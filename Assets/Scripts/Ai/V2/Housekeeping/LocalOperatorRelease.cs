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
            UnitData hero, ArmyData garrison, out string why)
        {
            why = null;
            if (player == null || from == null || hero == null || garrison == null)
            { why = "missing actor"; return false; }
            if (from.IsGarrison || from.IsPrison || !garrison.IsGarrison || garrison.IsPrison
                || garrison.Owner != player || from.Owner != player || !garrison.Hex.Equals(from.Hex))
            { why = "no own local garrison"; return false; }
            if (hero.Owner != player || !hero.IsHero || hero.IsPrisoner || !from.Members.Contains(hero))
            { why = "hero not a free member"; return false; }
            if (!AiArmyRoles.IsFacilityOperator(player, from.Hex, hero))
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

        // Before a ground operation moves `army` away from its base: every operator the facility
        // needs is left in the local garrison when that is legal and costs no AP. Where it is not
        // (sole commander, full garrison, activation charge, claimed garrison) the departure goes
        // on unchanged — the fallback commander stays legal — and the fact is logged. Returns the
        // number of heroes left at home.
        internal static int ReleaseBeforeDeparture(PlayerSetupData player, AiTurnContext ctx,
            ArmyData army, ActorCommitments commitments)
        {
            if (player == null || army == null || army.IsGarrison || army.IsPrison
                || army.Owner != player || !army.Members.Any(m => m != null && m.IsHero))
                return 0;
            List<UnitData> operators = army.Members
                .Where(m => m != null && m.IsHero && AiArmyRoles.IsFacilityOperator(player, army.Hex, m)
                    && AiArmyRoles.FacilityNeedsHero(player, army, m))
                .ToList();
            if (operators.Count == 0)
                return 0;
            ArmyData garrison = ArmyRegistry.AllAt(army.Hex)
                .FirstOrDefault(a => a != null && a.Owner == player && a.IsGarrison && !a.IsPrison);
            int released = 0;
            foreach (UnitData hero in operators)
            {
                string why = null;
                UnitData newLead = null;
                // The operator leads the army: another hero of the army takes command first (the same
                // zero-AP reorder Housekeeping uses) when the army keeps its capacity under it.
                bool leads = ReferenceEquals(hero, army.Commander);
                if (leads)
                {
                    newLead = BestOtherCommander(army, hero, player);
                    if (newLead == null) { Log(hero, army, "operator is the army's only legal commander"); continue; }
                }
                if (garrison == null) why = "no local garrison";
                else if (commitments != null && commitments.IsArmyClaimed(garrison.Id)) why = "garrison is claimed";
                else if (!leads && !MayLeaveForLocalGarrison(player, army, hero, garrison, out why)) { }
                else if (hero.ActivationApCost > 0 && garrison.RequiresActivationCharge(hero))
                    why = "transfer would spend AP";
                else if (ArmyData.ComputeCapacity(new List<UnitData>(garrison.Members) { hero }, true)
                         < garrison.Members.Count + 1)
                    why = "garrison full";
                else if (!leads && !army.CanLeaveWithoutOvercrowding(hero)) why = "army would overcrowd";
                else
                {
                    if (leads && !army.TryReorderCommander(newLead, out why)) { Log(hero, army, why); continue; }
                    if (leads && (!MayLeaveForLocalGarrison(player, army, hero, garrison, out why)
                                  || !army.CanLeaveWithoutOvercrowding(hero)))
                    { Log(hero, army, why ?? "army would overcrowd"); continue; }
                    if (!ArmyActions.TransferMember(hero, army, garrison, ctx?.HexSelection, out why))
                    { Log(hero, army, why); continue; }
                    WorldDeltaLifecycle.CommitMutation();
                    released++;
                    AiDebugLog.Write($"[AI][V2][OperatorRelease] {hero.Name} left army #{army.Id} for garrison "
                        + $"#{garrison.Id} at ({army.Hex.Q},{army.Hex.R}) before departure; commander="
                        + $"{army.Commander?.Name}{(leads ? " (reordered)" : "")} ap=0");
                    continue;
                }
                Log(hero, army, why);
            }
            return released;
        }

        private static void Log(UnitData hero, ArmyData army, string why) =>
            AiDebugLog.WriteDeduped("operator-release",
                $"[AI][V2][OperatorRelease] {hero.Name} stays in army #{army.Id} at ({army.Hex.Q},{army.Hex.R}): {why}");

        // The hero that takes command when `operatorHero` leaves: a non-operator, non-support hero
        // under whom the whole current roster still fits, best combat leadership first.
        private static UnitData BestOtherCommander(ArmyData army, UnitData operatorHero, PlayerSetupData player)
        {
            return army.Members
                .Where(u => u != null && u.IsHero && !u.IsPrisoner && !ReferenceEquals(u, operatorHero)
                    && !AiArmyRoles.IsFacilityOperator(player, army.Hex, u))
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
