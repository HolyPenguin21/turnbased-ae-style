using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Менеджмент's own "who/what" half — pure and read-only, same style as AiEconomyPlanner/
    // AiScoutPlanner: picks targets/actors, never builds an AiDecision or touches AiTaskRegistry/
    // AiResourcePool itself (see AiTurnController.Decide's own class comment on why scoring and
    // mutation both stay in the orchestrator). Covers every Менеджмент sub-task except the
    // "solo hero with nothing to visit walks home" fallback, which stays in AiTurnController.
    // TryReturnHomeCandidates — that one is really AiArmyRoles.IsSoloHeroAwaitingEscort's own
    // other half, not a card/army-bookkeeping concern.
    public static class AiManagementPlanner
    {
        // What a hand card is FOR, as far as card placement cares — see AiArmyRoles's own class
        // comment for the three army shapes these map to.
        public enum CardRole
        {
            Recce, // solo Recce party — unit or hero, always a fresh empty army
            Hero, // non-Recce hero — always a fresh empty army, to lead its own force
            Unit, // plain non-Recce unit — tops up a hero's escort, else stockpiles in garrison
        }

        // ---- Разыгрывание карты из руки ----

        public readonly struct CardPlacement
        {
            // null => no existing army has room; caller spawns a fresh one instead.
            public readonly ArmyData ExistingArmy;
            public CardPlacement(ArmyData existingArmy) => ExistingArmy = existingArmy;
        }

        // Checked BEFORE the caller proposes a candidate, not after (see the project owner's own
        // report: a Hero card's own apCost can run well above a plain Unit's, and
        // DeployUnitFromCard failing after the fact just left Decide re-proposing the exact same
        // unaffordable card every step for the rest of the turn — nothing about root's AP changes
        // between retries, so it never would have succeeded). Null if `card` can't be placed
        // anywhere right now. Target army depends entirely on `role` — see AiArmyRoles's own
        // class comment for the three shapes: Recce/Hero always found a fresh empty army of their
        // own, a plain Unit tops up an existing hero's escort first and only stockpiles in the
        // garrison once no such escort has room (never the other way around — the garrison is the
        // fallback, not the first stop).
        public static CardPlacement? FindPlacement(PlayerSetupData player, PlayerRoot root, CardData card, CardRole role)
        {
            CardDefinition definition = card.Definition;
            int deployApCost = ArmyActions.EffectiveDeployApCost(definition);
            // AiResourceReservation.CanAfford, not definition.resourceCost.CanAfford(root)
            // directly — a card must never spend what an active BuildFacility task has already
            // claimed toward its own facility (see the owner's own "все траты ИИ" call).
            if (!root.CanSpendActionPoints(deployApCost) || !AiResourceReservation.CanAfford(root, player, definition.resourceCost))
                return null;

            ArmyData existing;
            if (role == CardRole.Unit)
            {
                existing = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => AiArmyRoles.IsHeroLedCombatArmy(a) && a.HasRoom && IsAtRequiredBuilding(a, player, definition));
                if (existing == null)
                    existing = ArmyRegistry.AllForOwner(player)
                        .FirstOrDefault(a => a.IsGarrison && a.HasRoom && IsAtRequiredBuilding(a, player, definition));
            }
            else
            {
                existing = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && IsAtRequiredBuilding(a, player, definition));
            }

            if (existing != null)
                return new CardPlacement(existing);

            return root.ActionPoints >= ArmyActions.CreateArmyApCost + deployApCost
                ? new CardPlacement(null)
                : (CardPlacement?)null;
        }

        // Same rule CardHandUI.IsValidDropTarget enforces for a human's drag-drop — a Unit/Hero
        // card can only join an army sitting on a hex with one of the player's own buildings that
        // grants definition.requiredBuildingAbility (Barracks, in practice).
        private static bool IsAtRequiredBuilding(ArmyData army, PlayerSetupData player, CardDefinition definition)
        {
            if (string.IsNullOrEmpty(definition.requiredBuildingAbility))
                return false;
            BuildingData building = BuildingRegistry.FindAt(army.Hex);
            return building != null && building.Owner == player && building.HasAbility(definition.requiredBuildingAbility);
        }

        public static bool IsUnitOrHeroCard(CardData card)
        {
            CardDefinition definition = card?.Definition;
            return definition != null && (definition.cardType == CardType.Unit || definition.cardType == CardType.Hero);
        }

        public static bool IsRecceCard(CardData card)
        {
            if (!IsUnitOrHeroCard(card))
                return false;
            CardDefinition definition = card.Definition;
            return definition.grantedAbilities != null && definition.grantedAbilities.Contains(UnitAbilities.Recce);
        }

        // ---- Капасити гарнизона ----

        // The trailing members that no longer fit once the garrison is over capacity — just
        // enough to open ONE slot (Members.Count - (Capacity - 1)), not an arbitrary "move half":
        // re-evaluated fresh every turn (same "непрерывная переоценка" principle every other
        // planner already follows), so a garrison that fills up again next turn just proposes
        // another small split rather than needing to guess the right batch size up front. Null if
        // the garrison already has room or doesn't exist.
        public static IReadOnlyList<UnitData> FindGarrisonOverflow(ArmyData garrison)
        {
            if (garrison == null || garrison.HasRoom)
                return null;
            int overflow = garrison.Members.Count - (garrison.Capacity - 1);
            if (overflow <= 0)
                return null;
            return garrison.Members.Skip(garrison.Members.Count - overflow).ToList();
        }

        // ---- Передача юнитов между армиями в базе ----

        public readonly struct ConsolidationMove
        {
            public readonly ArmyData Source;
            public readonly UnitData Unit;
            public readonly ArmyData Target;

            public ConsolidationMove(ArmyData source, UnitData unit, ArmyData target)
            {
                Source = source;
                Unit = unit;
                Target = target;
            }
        }

        // "Одиночка" at the citadel hex — a plain lone unit OR a lone hero waiting for an escort
        // (AiArmyRoles.IsSoloHeroAwaitingEscort) — but never a dedicated Recce solo (AiArmyRoles.
        // IsScoutCapable, excluded via !HasRecce): that composition is deliberately kept single-
        // member forever, not "waiting to grow" (see AiArmyRoles's own class comment). A lone
        // hero is included in this sweep too, but FindConsolidationMove's own
        // FindHeroEscortFromGarrison check runs first and pulls a spare garrison unit straight
        // into the hero instead whenever one is available — folding the hero into the garrison
        // (this sweep's own fallback) only ever happens when the garrison has no non-hero unit
        // to offer it yet. Scoped to `garrisonHex` only — an army out in the field mid-task is
        // never touched by this sweep.
        private static bool IsLoneArmyAtBase(ArmyData army, HexCoord garrisonHex)
        {
            return army != null && !army.IsGarrison && !army.IsPrison && !army.HasRecce
                && army.Members.Count == 1 && army.Hex.Equals(garrisonHex);
        }

        // A hero at the garrison hex with spare non-hero units ALREADY sitting in the garrison
        // forms its escort straight out of that stock — supersedes the older "fold the hero into
        // the garrison, reform next turn" call (see IsLoneArmyAtBase's own comment): with units
        // right there waiting, there is nothing to gain by parking the hero for a turn first.
        // Checked before either fallback below, so this always wins over folding the hero away.
        // Deliberately NOT scoped to `loneArmies`/IsLoneArmyAtBase's one-member restriction — a
        // hero that has already picked up its first escort this same turn must keep being
        // offered here too (see FindConsolidationMove's own step-by-step Decide loop), otherwise
        // it would stop at Hero+1 and only resume next turn even with the garrison still stocked.
        // Stops once the hero's own roster is full (HasRoom false) or IsMakeshiftScoutCapable's
        // own Hero+2 floor is reached — a full army escort forming is the goal, not endless
        // top-up past the point AiScoutPlanner would already be willing to send it out.
        private static ConsolidationMove? FindHeroEscortFromGarrison(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison)
        {
            if (garrison == null)
                return null;
            ArmyData heroArmy = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                AiArmyRoles.IsHeroLedCombatArmy(a) && a.Hex.Equals(garrisonHex) && a.HasRoom
                && !AiArmyRoles.IsMakeshiftScoutCapable(a));
            if (heroArmy == null)
                return null;
            UnitData garrisonUnit = garrison.Members.FirstOrDefault(m => !m.IsHero);
            if (garrisonUnit == null)
                return null;
            return CanAffordTransferInto(heroArmy, garrisonUnit)
                ? new ConsolidationMove(garrison, garrisonUnit, heroArmy)
                : (ConsolidationMove?)null;
        }

        // Garrison has room → the first lone army found feeds it directly. Garrison is full →
        // pairs two lone armies together instead (preferring a hero-led one as the merge target,
        // so hero+escort forms the way a Unit card would otherwise have had to wait to do) — null
        // if fewer than two lone armies exist to pair up. Either way, pre-checks the exact same
        // AP-affordability ArmyActions.TransferMember will itself enforce, so an unaffordable
        // move is never proposed as a candidate in the first place (same "checked before
        // proposing" rule FindPlacement above follows).
        public static ConsolidationMove? FindConsolidationMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison)
        {
            // Independent of `loneArmies` below on purpose — a hero mid-way through escorting up
            // (Hero+1, say) is no longer a lone army, but still needs to keep drawing garrison
            // stock in on later steps this same turn (see FindHeroEscortFromGarrison's own
            // comment), so this must never be gated on loneArmies being non-empty.
            ConsolidationMove? heroEscort = FindHeroEscortFromGarrison(player, garrisonHex, garrison);
            if (heroEscort != null)
                return heroEscort;

            List<ArmyData> loneArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => IsLoneArmyAtBase(a, garrisonHex))
                .ToList();
            if (loneArmies.Count == 0)
                return null;

            if (garrison != null && garrison.HasRoom)
            {
                ArmyData source = loneArmies[0];
                UnitData unit = source.Members[0];
                return CanAffordTransferInto(garrison, unit) ? new ConsolidationMove(source, unit, garrison) : (ConsolidationMove?)null;
            }

            ArmyData target = loneArmies.FirstOrDefault(AiArmyRoles.IsHeroLedCombatArmy) ?? loneArmies[0];
            ArmyData mergeSource = loneArmies.FirstOrDefault(a => a != target);
            if (mergeSource == null)
                return null; // only one lone army exists and garrison has no room — nothing to pair it with

            UnitData mergeUnit = mergeSource.Members[0];
            return CanAffordTransferInto(target, mergeUnit) ? new ConsolidationMove(mergeSource, mergeUnit, target) : (ConsolidationMove?)null;
        }

        private static bool CanAffordTransferInto(ArmyData target, UnitData unit)
        {
            if (!target.HasActivatedThisTurn)
                return true;
            PlayerRoot targetRoot = PlayerRootRegistry.FindFor(target.Owner);
            return targetRoot != null && targetRoot.CanSpendActionPoints(unit.ActivationApCost);
        }

        // ---- Резерв / добор — чередование ----

        public enum FallbackKind
        {
            ReserveArmy,
            DrawCard,
        }

        private static readonly Dictionary<PlayerSetupData, FallbackKind> PreferredFallback = new Dictionary<PlayerSetupData, FallbackKind>();

        public static void Clear() => PreferredFallback.Clear();

        // Alternation state for Менеджмент's own leftover-AP fallbacks (see AiTurnController's
        // own Reserve/Draw candidate block) — the project owner's own call: a reserve army and a
        // card draw should trade off turn by turn rather than one flat score always outscoring
        // the other. Defaults to ReserveArmy (default(FallbackKind) == 0) the very first time
        // either ever fires for a given player — arbitrary, just needs a starting point.
        public static bool IsPreferred(PlayerSetupData player, FallbackKind kind)
        {
            PreferredFallback.TryGetValue(player, out FallbackKind preferred);
            return preferred == kind;
        }

        // Called once whichever fallback Decide actually committed this step has really executed
        // (see AiTurnController's own ReserveArmyRoutine/DrawCardRoutine) — flips to the OTHER
        // kind regardless of why this one ran, so a kind that only fired because the other wasn't
        // available at all this turn still correctly becomes non-preferred next time.
        public static void NotifyFallbackUsed(PlayerSetupData player, FallbackKind kind)
        {
            if (player != null)
                PreferredFallback[player] = kind == FallbackKind.ReserveArmy ? FallbackKind.DrawCard : FallbackKind.ReserveArmy;
        }
    }
}
