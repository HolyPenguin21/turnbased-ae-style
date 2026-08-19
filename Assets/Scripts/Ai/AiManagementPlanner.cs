using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Level-1 category for Менеджмент (AiTaskCategory.Management) — shared primitives (card
    // placement via FindPlacement, the Reserve/Draw fallback alternation) plus the candidate-
    // gathering orchestration AiTurnController.Decide calls into each step (TryPlayCardCandidates/
    // TryGarrisonSplitCandidate/TryConsolidationCandidate/GatherFallbackCandidates), same role
    // AiScoutPlanner/AiEconomyPlanner/AiAggressionPlanner play for their own categories, plus this
    // category's own execution routines (ReserveArmyRoutine/DrawCardRoutine/
    // SplitGarrisonArmyRoutine/ConsolidateUnitsRoutine — see AiTurnController's own class comment
    // on the execution split). The garrison/army reorg logic itself is its own task class — see
    // GarrisonReorgTask's own class comment for why. The "solo hero with nothing to visit walks
    // home" fallback lives on AiScoutPlanner.TryReturnHomeCandidates instead — that one is really
    // AiArmyRoles.IsSoloHeroAwaitingEscort's own other half, a Разведка concern, not a
    // card/army-bookkeeping one.
    public static class AiManagementPlanner
    {
        // What a hand card is FOR, as far as card placement cares — see AiArmyRoles's own class
        // comment for the three army shapes these map to.
        public enum CardRole
        {
            Recce, // solo Recce party — unit or hero, always a fresh empty army
            Hero, // non-Recce hero — garrison, else a reserve army, else founds a fresh one
            Unit, // plain non-Recce unit — hero escort, else garrison, else a reserve army
        }

        // ---- Разыгрывание карты из руки ----

        public readonly struct CardPlacement
        {
            // null => no existing army has room; caller spawns a fresh one instead.
            public readonly ArmyData ExistingArmy;
            // Hero role only — non-null means ExistingArmy is a FULL plain army FindPlacement
            // picked anyway (see its own last-resort tier's comment): PlayCardRoutine evicts
            // this member to the garrison BEFORE deploying the card, opening the room the hero
            // needs, both in the one same action.
            public readonly UnitData EvictUnit;
            public CardPlacement(ArmyData existingArmy, UnitData evictUnit = null)
            {
                ExistingArmy = existingArmy;
                EvictUnit = evictUnit;
            }
        }

        // Checked BEFORE the caller proposes a candidate, not after (see the project owner's own
        // report: a Hero card's own apCost can run well above a plain Unit's, and
        // DeployUnitFromCard failing after the fact just left Decide re-proposing the exact same
        // unaffordable card every step for the rest of the turn — nothing about root's AP changes
        // between retries, so it never would have succeeded). Null if `card` can't be placed
        // anywhere right now.
        //
        // Recce always founds its own fresh empty army (AiArmyRoles.IsEmptyDeployableArmy) — the
        // one composition deliberately kept solo forever (see that method's own comment). Unit
        // and Hero now share the SAME fallback chain instead of Hero always founding its own
        // army: an existing hero escort with room (Unit only — a second hero never joins one it
        // didn't lead itself), then the garrison (while it has more than
        // AiConfig.garrisonReservedSlots free), then an existing plain reserve army
        // (AiArmyRoles.IsPlainReserveArmy — a Hero card landing on one of these is exactly how it
        // becomes hero-led), and only once none of those has room does a fresh army get spawned.
        // The project owner's own spec: "один герой на армию — такого быть не должно вообще, если
        // такого не требует задача" — a solo hero should be the rare last resort, not the default.
        //
        // `canSupportAnotherHeroArmy` — GarrisonReorgTask.CanSupportAnotherHeroArmy's own answer
        // for this player, computed once by the caller (AiTurnController) and passed in rather
        // than called from here, so this shared card-placement primitive doesn't reach UP into a
        // task class (GarrisonReorgTask already depends on THIS class the other way, via
        // HasGarrisonDepositRoom — keeping it one-directional). Irrelevant for Unit/Recce roles;
        // for Hero, false means founding/taking over yet another hero-led army would spread
        // combat strength thinner than AiConfig.minArmyStrengthShare allows — the card is only
        // ever offered the garrison itself then (bypassing the normal reserved-slot headroom, a
        // hero only takes one slot and benching it here IS the point), never a fresh/plain-reserve
        // army, and simply waits if even that has no room (the project owner's own "лучше держать
        // несколько героев в гарнизоне, чем плодить слабые армии" spec).
        public static CardPlacement? FindPlacement(PlayerSetupData player, PlayerRoot root, CardData card, CardRole role,
            bool canSupportAnotherHeroArmy = true)
        {
            CardDefinition definition = card.Definition;
            int deployApCost = ArmyActions.EffectiveDeployApCost(definition);
            // AiResourceReservation.CanAfford, not definition.resourceCost.CanAfford(root)
            // directly — a card must never spend what an active BuildFacility task has already
            // claimed toward its own facility (see the owner's own "все траты ИИ" call).
            if (!root.CanSpendActionPoints(deployApCost) || !AiResourceReservation.CanAfford(root, player, definition.resourceCost))
                return null;

            ArmyData existing = null;
            if (role == CardRole.Recce)
            {
                existing = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && IsAtRequiredBuilding(a, player, definition));
            }
            else if (role == CardRole.Hero && !canSupportAnotherHeroArmy)
            {
                ArmyData garrisonBench = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => a.IsGarrison && a.HasRoom && IsAtRequiredBuilding(a, player, definition));
                return garrisonBench != null ? new CardPlacement(garrisonBench) : (CardPlacement?)null;
            }
            else
            {
                if (role == CardRole.Unit)
                    existing = ArmyRegistry.AllForOwner(player)
                        .FirstOrDefault(a => AiArmyRoles.IsHeroLedCombatArmy(a) && a.HasRoom && IsAtRequiredBuilding(a, player, definition));

                if (existing == null)
                    existing = ArmyRegistry.AllForOwner(player)
                        .FirstOrDefault(a => a.IsGarrison && HasGarrisonDepositRoom(a) && IsAtRequiredBuilding(a, player, definition));

                if (existing == null)
                    existing = ArmyRegistry.AllForOwner(player)
                        .FirstOrDefault(a => AiArmyRoles.IsPlainReserveArmy(a) && IsAtRequiredBuilding(a, player, definition));

                // Hero-only last resort before founding a fresh army: a FULL plain-army-shaped
                // stockpile (IsPlainReserveArmy above already skipped it — that check requires
                // HasRoom) still deserves a hero over spinning up yet another empty one. Evicts
                // its own weakest member back to the garrison first — same reasoning as
                // GarrisonReorgTask.FindHeroPromotionMakeRoom, just reached from card-placement
                // time instead of waiting for a LATER Decide() step's reorg tier to notice (the
                // project owner's own "должен уметь выкладывать карты героев прямо в готовую
                // армию" spec). Requires the garrison to actually have headroom to receive the
                // evictee AND be affordable — otherwise this card simply finds no placement here
                // either, same as every other tier above.
                if (existing == null && role == CardRole.Hero)
                {
                    ArmyData evictTarget = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                        a != null && !a.IsGarrison && !a.IsPrison && !a.HasRecce && !a.HasRoom
                        && a.Members.Count(m => m.IsHero) == 0 && IsAtRequiredBuilding(a, player, definition));
                    if (evictTarget != null)
                    {
                        ArmyData garrisonArmy = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison);
                        UnitData evictUnit = evictTarget.Members.OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
                        bool canAffordEvict = evictUnit != null && garrisonArmy != null && HasGarrisonDepositRoom(garrisonArmy)
                            && (!garrisonArmy.HasActivatedThisTurn || root.CanSpendActionPoints(evictUnit.ActivationApCost));
                        if (canAffordEvict)
                            return new CardPlacement(evictTarget, evictUnit);
                    }
                }
            }

            if (existing != null)
                return new CardPlacement(existing);

            return root.ActionPoints >= ArmyActions.CreateArmyApCost + deployApCost
                ? new CardPlacement(null)
                : (CardPlacement?)null;
        }

        // Garrison keeps at least AiConfig.garrisonReservedSlots open at all times as far as
        // ordinary card deposits are concerned — see AiConfig.garrisonReservedSlots's own comment
        // for why this is a stricter gate than the raw HasRoom the overflow-eviction side
        // (GarrisonReorgTask.FindGarrisonOverflow) still uses. Public — GarrisonReorgTask's own
        // lone-army-fold tier needs the identical headroom check (see its own comment on why).
        public static bool HasGarrisonDepositRoom(ArmyData garrison) =>
            garrison != null && garrison.Capacity - garrison.Members.Count > AiConfig.Current.garrisonReservedSlots;

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

        // Shared by TryPlayCardCandidates (scoring) and AiTurnController.PlayCardRoutine
        // (NotifyCardRolePlayed, once a card actually deploys) — both need the identical read so
        // the alternation state below can never disagree with what a card was actually scored as.
        public static CardRole RoleOf(CardData card) => IsRecceCard(card) ? CardRole.Recce
            : card.Definition.cardType == CardType.Hero ? CardRole.Hero
            : CardRole.Unit;

        // ---- Разыгрывание карты — чередование ролей (Hero/Unit) ----

        private static readonly Dictionary<PlayerSetupData, CardRole> LastPlayedCardRole = new Dictionary<PlayerSetupData, CardRole>();

        // Alternation state for TryPlayCardCandidates's own cardRoleAlternationDamping (see that
        // field's own comment) — Recce never participates (it already has its own separate
        // scoring path via reconHandDemandActive), so callers only ever pass Hero or Unit here.
        // No stored-state default needed the way FallbackKind's IsPreferred has one: absent from
        // the dictionary simply means "nothing of this role has been played yet this game",
        // which correctly cools down neither role.
        public static bool IsCardRoleCoolingDown(PlayerSetupData player, CardRole role) =>
            LastPlayedCardRole.TryGetValue(player, out CardRole last) && last == role;

        // Called once a PlayCard decision has actually deployed successfully (see
        // AiTurnController.PlayCardRoutine, right alongside its own hand.Hand.Remove) — a failed
        // deploy attempt didn't really change the hand's Hero/Unit balance, so it shouldn't flip
        // this either, unlike NotifyFallbackUsed's own "regardless of success" rule (that one's
        // about a resource that's unavailable outright, not a mid-execution failure).
        public static void NotifyCardRolePlayed(PlayerSetupData player, CardRole role)
        {
            if (player != null && role != CardRole.Recce)
                LastPlayedCardRole[player] = role;
        }

        // ---- Резерв / добор — чередование ----

        public enum FallbackKind
        {
            ReserveArmy,
            DrawCard,
        }

        private static readonly Dictionary<PlayerSetupData, FallbackKind> PreferredFallback = new Dictionary<PlayerSetupData, FallbackKind>();

        public static void Clear()
        {
            PreferredFallback.Clear();
            LastPlayedCardRole.Clear();
        }

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

        // ---- Cards / reserve / draw / base upkeep (Менеджмент's own "мелкие" steps) ----

        // One candidate per affordable Unit/Hero/Recce card in hand — Base/Facility cards are
        // skipped entirely, same as before (see AiTurnController.Decide's own class comment,
        // point 4). Every card gets its own role read straight off IsRecceCard/cardType (not just
        // the first Recce card found in hand, unlike the old fixed-tier version) — a second Recce
        // card in hand now correctly gets routed as a solo Recce party too, instead of falling
        // through to plain Unit/Hero placement rules. Placement itself (who has room, whether it's
        // even affordable) lives in FindPlacement — this only assigns the Score and builds the
        // AiDecision.
        //
        // `reconHandDemandActive` — true when Разведка already has a SpawnReconArmy/
        // AssembleRecceScout candidate this same step (computed by AiTurnController.Decide off
        // its own AiScoutPlanner.TryStartReconAssemblyCandidates call, BEFORE this one runs — see
        // its own call site comment), i.e. it's already mid-pursuit of a matching Recce card from
        // THIS hand. Two effects while active, both from the project owner's own 2026-08-17
        // reports (Recce cards losing out first to a Unit backlog, then again to a Hero backlog
        // even after the first fix):
        //  1) Damps (reconHandDemandBacklogDamping) the Unit/Hero backlog terms below — the
        //     backlog's whole point is forcing urgency for a card that would otherwise never get
        //     played, and that's simply not true right now for the Recce card Разведка is already
        //     chasing.
        //  2) Bumps a Recce card's OWN score up to match Разведка's own valuation of this exact
        //     situation (reconBaseWeight + reconRequestCardPenalty — the same number
        //     TryStartReconAssemblyCandidatesFor would score this identical card at if an empty
        //     army already existed) instead of Менеджмент's own flat playRecceCardBonus. Damping
        //     alone caps the Unit/Hero side but never actually raises Recce's own number, so a
        //     large enough backlog still eventually out-scores it — this closes that gap for good
        //     rather than chasing it with ever-larger damping.
        //
        // Independently of both of the above, a role that just had a card of its own played also
        // gets cardRoleAlternationDamping applied (IsCardRoleCoolingDown — see that method's own
        // comment) — the project owner's own 2026-08-17 follow-up: without this, whichever of
        // Hero/Unit had the taller backlog pile kept winning every single step, so the AI played
        // out its ENTIRE hand of heroes before touching a single unit (or vice versa) instead of
        // alternating.
        public static List<AiDecision> TryPlayCardCandidates(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            bool reconHandDemandActive = false)
        {
            var results = new List<AiDecision>();
            if (hand == null)
                return results;

            // Computed once per step, not per card — GarrisonReorgTask.CanSupportAnotherHeroArmy's
            // own answer doesn't change while this loop just reads state (see FindPlacement's own
            // comment on why this is passed in rather than looked up from here directly).
            bool canSupportAnotherHeroArmy = GarrisonReorgTask.CanSupportAnotherHeroArmy(player);

            // Plain Unit cards (Recce/Hero excluded — those already have their own growth path
            // via reconAssembleBonus/reconRequestCardPenalty and generally get played anyway, see
            // the project owner's own "won't deploy units" report) otherwise sit at a permanent
            // flat managementBaseWeight forever, tied with — and routinely losing the tie-break
            // to — RequestRaidArmy/SpawnReconArmy's own flat 50 (first-found wins ties, see
            // AiTurnController.Decide's own comment). The backlog itself is the pressure valve:
            // the more Unit cards pile up unplayed, the more urgent playing ANY of them becomes,
            // so this grows with hand size rather than being a fixed bump — see
            // unitCardBacklogWeight's own comment.
            int unplayedUnitCards = hand.Hand.Count(c => IsUnitOrHeroCard(c) && !IsRecceCard(c)
                && c.Definition.cardType == CardType.Unit);
            // Same pressure-valve idea, for non-Recce Hero cards, own separate pile/count — see
            // playHeroCardBonus's own comment for why the per-card weight is shared with Unit's
            // rather than its own steeper constant.
            int unplayedHeroCards = hand.Hand.Count(c => IsUnitOrHeroCard(c) && !IsRecceCard(c)
                && c.Definition.cardType == CardType.Hero);
            float backlogDamping = reconHandDemandActive ? AiConfig.Current.reconHandDemandBacklogDamping : 1f;
            float recceScore = reconHandDemandActive
                ? AiConfig.Current.reconBaseWeight + AiConfig.Current.reconRequestCardPenalty
                : AiConfig.Current.managementBaseWeight + AiConfig.Current.playRecceCardBonus;
            float heroAlternationDamping = IsCardRoleCoolingDown(player, CardRole.Hero) ? AiConfig.Current.cardRoleAlternationDamping : 1f;
            float unitAlternationDamping = IsCardRoleCoolingDown(player, CardRole.Unit) ? AiConfig.Current.cardRoleAlternationDamping : 1f;

            foreach (CardData card in hand.Hand)
            {
                if (!IsUnitOrHeroCard(card))
                    continue;
                CardRole role = RoleOf(card);
                float score = role == CardRole.Recce ? recceScore
                    : AiConfig.Current.managementBaseWeight
                        + (role == CardRole.Hero
                            ? heroAlternationDamping * (AiConfig.Current.playHeroCardBonus
                                + AiConfig.Current.unitCardBacklogWeight * backlogDamping * Mathf.Max(0, unplayedHeroCards - 1))
                            : unitAlternationDamping * AiConfig.Current.unitCardBacklogWeight * backlogDamping * Mathf.Max(0, unplayedUnitCards - 1));
                CardPlacement? placement = FindPlacement(player, root, card, role, canSupportAnotherHeroArmy);
                if (placement.HasValue)
                    results.Add(AiDecision.PlayCard(placement.Value.ExistingArmy, card, role, score, placement.Value.EvictUnit));
            }
            return results;
        }

        // ---- Менеджмент · Починка юнита ----
        // Owned here, not by AiEconomyPlanner (which only builds/scraps resource facilities) — this
        // is squarely Менеджмент's own domain: it already reads the hand (TryPlayCardCandidates,
        // just above) to decide what's worth playing, and already oversees armies sitting at the
        // player's own base/garrison hexes (TryGarrisonSplitCandidate/TryConsolidationCandidate,
        // just below). Repair's own dynamic-priority call (WouldBlockAffordableCard) needs the
        // exact same hand read TryPlayCardCandidates already does, which is the project owner's own
        // reasoning for moving it here (2026): "это менеджер тянет карты из руки... это менеджер
        // руководит армиями на хексах с гарнизонами."

        // Тригер: раненый юнит whose army is ALREADY sitting on the player's own Base (see
        // UnitRepair.CanRepairAt) — no travel stage at all, "arrived" is the trigger itself. One
        // task per wounded unit, not per army — an army with several wounded members gets repaired
        // one at a time (AdvanceRepairTask below re-checks affordability/card-blocking fresh for
        // whichever units still have an active task each step).
        public static List<AiDecision> TryStartRepairCandidates(PlayerSetupData player, PlayerRoot root, AiHandData hand)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<UnitData>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.RepairUnit)
                .Select(t => t.TargetUnit));

            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                // IsPrison excluded same as everywhere else a captured hero shouldn't be treated
                // as a normal own-army member (see FindCollectorDetachPlan's own IsPrison skip) —
                // mirrors ArmyViewerModalUI.IsReadOnly already blocking the human-side button for
                // a Prison army the same way.
                if (army.IsPrison || !UnitRepair.CanRepairAt(army.Hex, player))
                    continue;
                foreach (UnitData unit in army.Members)
                {
                    if (!UnitRepair.IsWounded(unit) || alreadyTargeted.Contains(unit))
                        continue;
                    var task = new AiTask { Kind = AiTaskKind.RepairUnit, Army = army, TargetHex = army.Hex, TargetUnit = unit };
                    AiDecision decision = AdvanceRepairTask(player, root, hand, task);
                    if (decision != null)
                        results.Add(decision);
                }
            }
            return results;
        }

        // Drives an active repair task — no travel stage (see TryStartRepairCandidates' own
        // comment), so this either finishes in one call or waits (short on AP/resources, or
        // deliberately yielding to a pricier card this step — see WouldBlockAffordableCard).
        public static AiDecision AdvanceRepairTask(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army)
                || task.TargetUnit == null || !task.Army.Members.Contains(task.TargetUnit) || !UnitRepair.IsWounded(task.TargetUnit))
            {
                // Self-heal, same pattern as AiEconomyPlanner.AdvanceEconomyTask — army/unit gone,
                // or already healed some other way (nothing else heals today, but a live re-check
                // costs nothing and keeps this task from lingering if that ever changes).
                if (AiTaskRegistry.TasksFor(player).Contains(task))
                    AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (!UnitRepair.CanRepairAt(task.Army.Hex, player))
            {
                // Army moved off its own Base since the task started — nothing left to do here.
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            int apCost = UnitRepair.ApCost(task.TargetUnit);
            ResourceCost cost = UnitRepair.ResourceCost(task.TargetUnit);
            if (!root.CanSpendActionPoints(apCost) || !cost.CanAfford(root))
                return AiDecision.Wait(task, $"задача «Менеджмент»: {task.Army.Name} копит на починку {task.TargetUnit.Name}");

            if (WouldBlockAffordableCard(root, hand, apCost, cost))
                return AiDecision.Wait(task, $"задача «Менеджмент»: чинить {task.TargetUnit.Name} подождёт — "
                    + "иначе не хватит на более дорогую карту в руке");

            return AiDecision.RepairUnit(task, AiConfig.Current.repairUnitBaseWeight);
        }

        // The project owner's own dynamic-priority call: repair is cheap and should usually go
        // first, but yields for a turn if paying for it would specifically make a pricier,
        // otherwise-affordable Unit/Hero card unaffordable — a simple cost-based proxy for "much
        // stronger card" (this AI's own card scoring has no real "power" stat to compare against
        // — see TryPlayCardCandidates, scored purely off hand backlog/role, not card strength;
        // the project owner is aware that scoring isn't final and plans to revisit/reuse this
        // same mechanism later). Only a card strictly PRICIER than the repair itself counts —
        // a cheap card going from affordable to unaffordable off this same spend isn't the
        // "much stronger card" case the owner described.
        private static bool WouldBlockAffordableCard(PlayerRoot root, AiHandData hand, int repairApCost, ResourceCost repairCost)
        {
            if (hand == null)
                return false;
            int repairTotal = repairApCost + repairCost.human + repairCost.energy + repairCost.materials + repairCost.tech;
            foreach (CardData card in hand.Hand)
            {
                if (!IsUnitOrHeroCard(card))
                    continue;
                CardDefinition definition = card.Definition;
                if (!root.CanSpendActionPoints(definition.apCost) || !definition.resourceCost.CanAfford(root))
                    continue; // not affordable even without the repair — repair isn't what's blocking it

                int cardTotal = definition.apCost + definition.resourceCost.human + definition.resourceCost.energy
                    + definition.resourceCost.materials + definition.resourceCost.tech;
                if (cardTotal <= repairTotal)
                    continue;

                bool stillAffordableAfterRepair =
                    root.ActionPoints - repairApCost >= definition.apCost
                    && root.GetResource(ResourceType.Human) - repairCost.human >= definition.resourceCost.human
                    && root.GetResource(ResourceType.Energy) - repairCost.energy >= definition.resourceCost.energy
                    && root.GetResource(ResourceType.Materials) - repairCost.materials >= definition.resourceCost.materials
                    && root.GetResource(ResourceType.Tech) - repairCost.tech >= definition.resourceCost.tech;
                if (!stillAffordableAfterRepair)
                    return true;
            }
            return false;
        }

        // Менеджмент · капасити гарнизона — see GarrisonReorgTask.FindGarrisonOverflow's own
        // comment for why this moves just enough members to open one slot rather than an
        // arbitrary batch, and FindGarrisonOverflowDestination's own comment for where those
        // members actually go (hero escort > existing reserve army > nothing this turn — it never
        // spawns a fresh reserve army itself any more, see that method's own comment for why).
        // Moving into an already-existing army is GarrisonReorgTask's own CanAffordTransferInto's
        // job, already folded into FindGarrisonOverflowDestination itself (same "checked before
        // proposing" rule every other candidate in this file already follows), so there's nothing
        // left for this method to check on its own.
        public static AiDecision TryGarrisonSplitCandidate(PlayerSetupData player, ArmyData garrison)
        {
            if (garrison == null)
                return null;
            IReadOnlyList<UnitData> overflow = GarrisonReorgTask.FindGarrisonOverflow(garrison);
            if (overflow == null || overflow.Count == 0)
                return null;
            GarrisonReorgTask.GarrisonOverflowDestination? destination =
                GarrisonReorgTask.FindGarrisonOverflowDestination(player, garrison.Hex, overflow[0]);
            if (!destination.HasValue)
                return null;
            return AiDecision.SplitGarrison(garrison, overflow, destination.Value.ExistingArmy, AiConfig.Current.managementGarrisonBalanceScore);
        }

        // Менеджмент · передача юнитов между армиями в базе — see
        // GarrisonReorgTask.FindReorgMove's own comment for scope/exclusions.
        public static AiDecision TryConsolidationCandidate(PlayerSetupData player, ArmyData garrison, AiTurnContext ctx)
        {
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            GarrisonReorgTask.ConsolidationMove? move = GarrisonReorgTask.FindReorgMove(player, garrisonHex, garrison, ctx);
            return move.HasValue ? AiDecision.Consolidate(move.Value, AiConfig.Current.managementGarrisonBalanceScore) : null;
        }

        // Менеджмент's own leftover-AP fallbacks — a spare reserve army (up to maxSpareArmies) and
        // a fresh card draw, trading off turn by turn via IsPreferred/NotifyFallbackUsed (see that
        // pair's own comment). Whichever of the two routines below actually executes calls
        // NotifyFallbackUsed itself once it's really run — this only proposes the candidates,
        // never flips the alternation on its own.
        public static List<AiDecision> GatherFallbackCandidates(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var results = new List<AiDecision>();
            int spareArmies = ArmyRegistry.AllForOwner(player).Count(a => !a.IsGarrison && !a.IsPrison && a.Members.Count == 0);
            bool reservePreferred = IsPreferred(player, FallbackKind.ReserveArmy);
            if (spareArmies < AiConfig.Current.maxSpareArmies && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                results.Add(AiDecision.Reserve(spareArmies, reservePreferred
                    ? AiConfig.Current.managementFallbackHighScore : AiConfig.Current.managementFallbackLowScore));

            if (hand != null && hand.HasCardsLeftToDraw && root.CanSpendActionPoints(ctx.DrawApCost))
                results.Add(AiDecision.Draw(reservePreferred
                    ? AiConfig.Current.managementFallbackLowScore : AiConfig.Current.managementFallbackHighScore));
            return results;
        }

        // ---- Execution ----

        public static IEnumerator ReserveArmyRoutine(PlayerSetupData player, AiTurnContext ctx)
        {
            HexCoord hex = AiTurnController.GarrisonHexFor(player);
            yield return AiTurnController.PanTo(ctx, hex);

            ArmyData army = ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            AiDebugLog.Write(army != null
                ? $"[AI] {player.Nickname}: создаёт резервную армию {army.Name} про запас."
                : $"[AI] {player.Nickname}: не хватило AP на резервную армию.");
            // Flips which of Reserve/Draw is preferred next time, regardless of success — see
            // NotifyFallbackUsed's own comment.
            NotifyFallbackUsed(player, FallbackKind.ReserveArmy);

            yield return AiTurnController.WaitStep(ctx);
        }

        public static IEnumerator DrawCardRoutine(PlayerSetupData player, AiTurnContext ctx)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
            if (root != null && hand != null && root.CanSpendActionPoints(ctx.DrawApCost))
            {
                CardData card = hand.DrawOne();
                if (card != null)
                {
                    root.SpendActionPoints(ctx.DrawApCost);
                    AiDebugLog.Write($"[AI] {player.Nickname}: берёт карту — {card.Definition.displayName}.");
                }
            }
            NotifyFallbackUsed(player, FallbackKind.DrawCard);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Менеджмент · капасити гарнизона — see GarrisonReorgTask.FindGarrisonOverflow/
        // FindGarrisonOverflowDestination and TryGarrisonSplitCandidate. Reuses whichever existing
        // army FindGarrisonOverflowDestination already picked when there is one; only creates a
        // fresh army (bailing before moving anything if THAT alone is unaffordable) when it left
        // the destination null.
        public static IEnumerator SplitGarrisonArmyRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData garrison = decision.ExistingArmy;
            yield return AiTurnController.PanTo(ctx, decision.TargetHex);

            // decision.MergeTarget is the destination GarrisonReorgTask.FindGarrisonOverflow
            // Destination already picked (hero escort or an existing reserve army) — only spawn a
            // fresh one when it left that null (no such destination existed, but still under the
            // reserve-army cap).
            ArmyData destination = decision.MergeTarget;
            UnitData promotedHero = null;
            if (destination == null)
            {
                destination = ArmyActions.CreateArmy(player, decision.TargetHex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
                if (destination == null)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: не хватило AP на новую армию для перегруженного гарнизона.");
                    yield break;
                }

                // A fresh spillover army gets a leader off the garrison bench, not just a pile of
                // units — see GarrisonReorgTask.FindGarrisonHeroToPromote's own comment. Transferred
                // BEFORE the overflow units below so destination's own Capacity already reflects
                // the hero's CommandRating by the time they're moved in.
                promotedHero = GarrisonReorgTask.FindGarrisonHeroToPromote(player, garrison);
                if (promotedHero != null)
                {
                    if (ArmyActions.TransferMember(promotedHero, garrison, destination, ctx.HexSelection, out string heroFailReason))
                        AiDebugLog.Write($"[AI] {player.Nickname}: {promotedHero.Name} снимается со скамейки в гарнизоне и возглавляет новую армию {destination.Name}.");
                    else
                        AiDebugLog.Write($"[AI] {player.Nickname}: не смог поставить {promotedHero.Name} во главе {destination.Name} — {heroFailReason}");
                }
            }

            int moved = 0;
            foreach (UnitData unit in decision.UnitsToMove)
            {
                if (unit == promotedHero)
                    continue;
                if (ArmyActions.TransferMember(unit, garrison, destination, ctx.HexSelection, out string failReason))
                    moved++;
                else
                    AiDebugLog.Write($"[AI] {player.Nickname}: не смог перевести {unit.Name} из гарнизона — {failReason}");
            }
            AiDebugLog.Write($"[AI] {player.Nickname}: гарнизон был полон — {moved} юнит(ов) переведены в {destination.Name}.");

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(destination);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Менеджмент · передача юнитов между армиями в базе — see GarrisonReorgTask.FindReorgMove
        // and TryConsolidationCandidate.
        public static IEnumerator ConsolidateUnitsRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            GarrisonReorgTask.ConsolidationMove move = decision.ConsolidationMove;
            yield return AiTurnController.PanTo(ctx, move.Source.Hex);

            bool moved = ArmyActions.TransferMember(move.Unit, move.Source, move.Target, ctx.HexSelection, out string failReason);
            if (moved)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.");
                ctx.HexSelection?.DeleteArmyIfEmptied(move.Source);
                // Feeds FindReorgMove's own oscillation guard (see AiTurnContext.WouldRevisitArmy's
                // own comment) — only a move that actually landed counts as "visited", same as
                // every other piece of AI per-turn state here (a candidate that was merely
                // proposed but never picked this step leaves no trace).
                ctx.RecordArmyVisit(move.Unit, move.Source, move.Target);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог объединить {move.Unit.Name} — {failReason}");
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.Target);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Менеджмент · Починка юнита's own execution — no BuildAttempts-style retry counter needed
        // (unlike AiEconomyPlanner.BuildFacilityRoutine, which can fail for reasons beyond
        // affordability): AdvanceRepairTask already checked AP/resources before ever proposing
        // this decision, so UnitRepair.TryRepair always succeeds here.
        public static IEnumerator RepairUnitRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiTask task = decision.Task;
            ArmyData army = task?.Army;
            if (army?.Controller == null || task.TargetUnit == null)
            {
                AiTaskRegistry.Remove(player, task);
                yield break;
            }

            yield return AiTurnController.PanTo(ctx, army.Hex);
            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(army);
            yield return AiTurnController.WaitStep(ctx);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            if (UnitRepair.TryRepair(task.TargetUnit, army.Hex, root, out string failReason))
                AiDebugLog.Write($"[AI] {player.Nickname}: {army.Name} починил {task.TargetUnit.Name} на "
                    + $"({army.Hex.Q},{army.Hex.R}) — задача «Менеджмент» завершена.");
            else
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог починить {task.TargetUnit.Name} — {failReason}.");
            AiTaskRegistry.Remove(player, task);

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
