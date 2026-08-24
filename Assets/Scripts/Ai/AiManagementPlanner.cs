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
    // GatherFallbackCandidates), same role AiScoutPlanner/AiEconomyPlanner/AiAggressionPlanner play
    // for their own categories, plus this category's own execution routines (ReserveArmyRoutine/
    // DrawCardRoutine/SplitGarrisonArmyRoutine/ConsolidateUnitsRoutine — see AiTurnController's own
    // class comment on the execution split). TryGarrisonSplitCandidate/TryConsolidationCandidate are
    // the odd ones out here (2026-08-20) — NOT called from Decide's own per-step loop any more, only
    // from AiTurnController.RunGarrisonReorgPhase, once, after that whole loop is done (see that
    // method's own comment for why). The garrison/army reorg logic itself is its own task class —
    // see GarrisonReorgTask's own class comment for why. The "solo hero with nothing to visit walks
    // home" fallback lives on AiScoutPlanner.TryReturnHomeCandidates instead — that one is really
    // AiArmyRoles.IsSoloHeroAwaitingEscort's own other half, a Разведка concern, not a
    // card/army-bookkeeping one.
    public static class AiManagementPlanner
    {
        // What a hand card is FOR, as far as scoring/alternation cares — see AiArmyRoles's own
        // class comment for the three army shapes these map to. Recce is still its own role for
        // that purpose (RoleOf/NotifyCardRolePlayed), even though FindPlacement below no longer
        // ever sees a Recce card at all (AiScoutPlanner places those itself — see FindPlacement's
        // own comment).
        public enum CardRole
        {
            Recce, // solo Recce party — placed by AiScoutPlanner, never by FindPlacement
            Hero, // non-Recce hero — garrison first, else an army (see FindPlacement)
            Unit, // plain non-Recce unit — garrison first, else an army (see FindPlacement)
        }

        // ---- Разыгрывание карты из руки ----

        public readonly struct CardPlacement
        {
            // null => no existing army has room; caller spawns a fresh one instead.
            public readonly ArmyData ExistingArmy;
            public CardPlacement(ArmyData existingArmy)
            {
                ExistingArmy = existingArmy;
            }
        }

        // ---- Многобазовая маршрутизация (2026-08-21) ----

        // Every one of this player's own garrisoned hexes, busiest first — "busy" meaning the
        // TOTAL KNOWN (AiMapMemory, fog-of-war honest) non-neutral threat within
        // AiConfig.managementActivityRadius, each sighting weighted by proximity and strength and
        // then summed (2026-08-23, project owner's own call — was the single strongest sighting's
        // own score, which routed reinforcements the same whether one raider or five sat nearby;
        // "куда направлять пополнение" cares about the base's total threat load, not just its
        // scariest single visitor). Deliberately NOT the raw-ArmyData cheat AiDefencePlanner.
        // CheatEstimateRaiderThreat/DynamicPatrolUrgencyScore use for Оборона's own proactive
        // patrol sizing — Менеджмент's own routing only reacts to what this player has actually
        // scouted (project owner's own "известных врагов" call). Ties (including "nothing known
        // anywhere") favor the citadel, same tie-break direction Агрессия/Оборона's own hard
        // citadel priority already uses.
        internal static IEnumerable<HexCoord> OwnGarrisonHexesByActivity(PlayerSetupData player)
        {
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            return AiTurnController.OwnGarrisonHexes(player)
                .OrderByDescending(h => ActivityScore(player, h))
                .ThenBy(h => h.Equals(citadelHex) ? 0 : 1);
        }

        // Feature 2's own small nudge (2026-08-24) — see AiTask.AwaitingGarrisonSeed's own comment.
        // A base whose own garrison-seed transfer hasn't landed yet (AiAggressionPlanner.
        // AdvanceGarrisonSeed is still trying, or has already given up and is waiting on exactly
        // this fallback) jumps to the FRONT of FindPlacement's own garrison-hex order, ahead of
        // whatever OwnGarrisonHexesByActivity's own real-threat-activity ranking would otherwise
        // say — a brand-new EMPTY garrison is precisely the "unguarded" opportunity
        // RaidWeakerArmyTask.FindTarget's own Section 5 looks for, so a card landing there even one
        // step sooner matters more than the usual busiest-base-first heuristic. A stable secondary
        // sort on top of OwnGarrisonHexesByActivity's own existing order (LINQ OrderBy/
        // OrderByDescending is stable) — every OTHER hex keeps its original relative order
        // untouched, so this is a small, well-scoped reorder, not a rewrite of that method's own
        // scoring.
        // 2026-08-24 P1 follow-up (project owner's own spec) — a plain Unit card additionally
        // jumps any OTHER (non-citadel) garrison ahead of the ordinary activity ranking whenever
        // that base's own garrison isn't secure yet (see AiArmyRoles.IsBaseGarrisonSecure's own
        // comment — empty, hero-only, or simply short of the secureBaseMinNonHeroUnits floor), so
        // the hero AiAggressionPlanner.AdvanceGarrisonSeed left minding a fresh base alone gets
        // relieved by the very next Unit card instead of staying pinned there indefinitely once
        // seeding itself has already timed out (see FindGarrisonSeedUnit's own "no unit to spare"
        // fallback). 2026-08-24 SecureBase follow-up (same spec) — this nudge used to stop applying
        // the moment a garrison held any single non-hero member at all (IsWeakSecondaryGarrison's
        // own old bare-presence check); now it keeps steering Unit cards there until the garrison
        // actually clears the same secure floor SecureBaseTask itself works toward, so cards and
        // SecureBase's own courier deliveries both drain the same real gap instead of the card side
        // declaring victory one unit early. Hero cards deliberately never get this nudge — stacking
        // a second hero into that same slot is never the fix an insecure garrison needs; ordinary
        // AiArmyRoles.IsHeroLed placement rules already keep a second hero card away from a
        // hero-led destination on their own. AwaitingGarrisonSeed's own existing top priority
        // stays strictly above this — applied AFTER (LINQ OrderBy is stable, so the LAST call
        // wins ties first), same "well-scoped reorder on top of the existing order" shape that
        // nudge's own comment already established.
        private static IEnumerable<HexCoord> GarrisonHexesForPlacement(PlayerSetupData player, CardType cardType)
        {
            IEnumerable<HexCoord> order = OwnGarrisonHexesByActivity(player);

            if (cardType == CardType.Unit)
            {
                HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
                order = order.OrderByDescending(h => !h.Equals(citadelHex) && !AiArmyRoles.IsBaseGarrisonSecure(player, h) ? 1 : 0);
            }

            HexCoord? awaitingSeedHex = AiTaskRegistry.TasksFor(player)
                .FirstOrDefault(t => t.Kind == AiTaskKind.BuildBase && t.AwaitingGarrisonSeed)?.TargetHex;
            if (awaitingSeedHex.HasValue)
                order = order.OrderByDescending(h => h.Equals(awaitingSeedHex.Value) ? 1 : 0);

            return order;
        }

        internal static HexCoord MostActiveOwnGarrisonHex(PlayerSetupData player)
        {
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            return OwnGarrisonHexesByActivity(player).DefaultIfEmpty(citadelHex).First();
        }

        private static float ActivityScore(PlayerSetupData player, HexCoord homeHex)
        {
            float total = 0f;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { homeHex }, AiConfig.managementActivityRadius))
            {
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                int distance = HexGridMath.Distance(homeHex, sighting.Hex);
                float proximity = 1f - (float)distance / AiConfig.managementActivityRadius;
                float strength = sighting.DefenseSum + sighting.AttackSum;
                total += proximity * strength;
            }
            return total;
        }

        // Checked BEFORE the caller proposes a candidate, not after (see the project owner's own
        // report: a Hero card's own apCost can run well above a plain Unit's, and
        // DeployUnitFromCard failing after the fact just left Decide re-proposing the exact same
        // unaffordable card every step for the rest of the turn — nothing about root's AP changes
        // between retries, so it never would have succeeded). Null if `card` can't be placed
        // anywhere right now.
        //
        // Hero and Unit cards both follow the exact same rule now (2026-08-19, project owner's
        // own call — "не нужно тут это разделение, карты юнитов как и карты героев мы ложим в
        // гарнизон, другая задача менеджера занимается управлением карт, PlayCard только
        // разыгрывает карту"): garrison first, while it has more than AiConfig.garrisonReserved
        // Slots free — growing armies OUT of garrison stock (hero escorts, plain-army top-up) is
        // GarrisonReorgTask's own job on a LATER step, not this method's. Only once the garrison
        // has no room does this fall back to an army — an existing plain reserve army
        // (AiArmyRoles.IsPlainReserveArmy) if one has room, else a fresh one. Recce cards never
        // reach this method at all any more — AiScoutPlanner places its own Recce cards directly
        // (see that class's own TryStartReconAssemblyCandidatesFor), so this is Hero/Unit only.
        //
        // Unit-only extra tier (2026-08-22, project owner's own call): once the plain-reserve
        // fallback above also comes up empty, a Unit card can still top up an existing hero-led
        // combat army with room (AiArmyRoles.IsHeroLedCombatArmy) — a raid/defence army already
        // hero-led but still short a body, same "grows toward a small but survivable fighting
        // force" intent that army shape's own comment describes. A Hero card never gets this tier
        // — IsHeroLedCombatArmy is deliberately excluded from a second hero, unchanged. Without
        // this, a Unit card could only ever reach a hero-led army indirectly through garrison
        // stock (RaidWeakerArmyTask.FindRecruitAt) — this method's own fallback used to dead-end
        // at a fresh, redundant army instead whenever the Garrison itself had no deposit room, the
        // same "spins up a duplicate instead of reusing what's already forming" pattern already
        // fixed once for Агрессия's own hero recruit (see AiAggressionPlanner.TryHeroCardForRaid).
        //
        // Multi-base (2026-08-21) — tries every one of this player's own garrisons, busiest first
        // (see OwnGarrisonHexesByActivity), not just "the" garrison: a card routes toward whichever
        // base currently has more real enemy activity nearby, project owner's own "разыгрывает
        // карты в ту базу где сейчас большая активность" call, falling through to the next-busiest
        // garrison only if the busiest one genuinely has no room right now.
        public static CardPlacement? FindPlacement(PlayerSetupData player, PlayerRoot root, CardData card)
        {
            CardDefinition definition = card.Definition;
            int deployApCost = ArmyActions.EffectiveDeployApCost(definition);
            // AiResourceReservation.CanAfford, not definition.resourceCost.CanAfford(root)
            // directly — a card must never spend what an active BuildFacility task has already
            // claimed toward its own facility (see the owner's own "все траты ИИ" call).
            if (!root.CanSpendActionPoints(deployApCost) || !AiResourceReservation.CanAfford(root, player, definition.resourceCost))
                return null;

            foreach (HexCoord homeHex in GarrisonHexesForPlacement(player, definition.cardType))
            {
                ArmyData garrison = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison && a.Hex.Equals(homeHex));
                if (garrison != null && HasGarrisonDepositRoom(garrison) && IsAtRequiredBuilding(garrison, player, definition))
                    return new CardPlacement(garrison);
            }

            ArmyData existingReserve = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => AiArmyRoles.IsPlainReserveArmy(a) && IsAtRequiredBuilding(a, player, definition));
            if (existingReserve != null)
                return new CardPlacement(existingReserve);

            if (definition.cardType == CardType.Unit)
            {
                ArmyData heroLedWithRoom = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => AiArmyRoles.IsHeroLedCombatArmy(a) && a.HasRoom && IsAtRequiredBuilding(a, player, definition));
                if (heroLedWithRoom != null)
                    return new CardPlacement(heroLedWithRoom);
            }

            return root.ActionPoints >= ArmyActions.CreateArmyApCost + deployApCost
                ? new CardPlacement(null)
                : (CardPlacement?)null;
        }

        // Garrison keeps at least AiConfig.garrisonReservedSlots open at all times as far as
        // ordinary card deposits are concerned — see AiConfig.garrisonReservedSlots's own comment
        // for why this is a stricter gate than the raw HasRoom the overflow-eviction side
        // (GarrisonReorgTask.FindGarrisonOverflow) still uses. GarrisonReorgTask's own lone-army-
        // fold tier used to share this same check, but doesn't any more (2026-08-20, project
        // owner's own call — see that tier's own comment): a solo unit/hero standing exposed
        // outside the garrison is exactly the case this buffer shouldn't block, so that tier now
        // reads raw HasRoom instead. Left public regardless — no reason to force this back to
        // private over that.
        public static bool HasGarrisonDepositRoom(ArmyData garrison) =>
            garrison != null && garrison.Capacity - garrison.Members.Count > AiConfig.garrisonReservedSlots;

        // Same rule CardHandUI.IsValidDropTarget enforces for a human's drag-drop — a Unit/Hero
        // card can only join an army sitting on a hex with one of the player's own buildings that
        // grants definition.requiredBuildingAbility (Barracks, in practice). Internal, not
        // private — AiScoutPlanner's own Recce placement (see its own TryStartReconAssembly
        // CandidatesFor) needs the identical check and shouldn't have to duplicate it.
        internal static bool IsAtRequiredBuilding(ArmyData army, PlayerSetupData player, CardDefinition definition)
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

        // ---- Экономика · спрос руки на ресурс (Feature 1, 2026-08-24) ----
        // Project owner's own root-cause report: Thane ended a turn with 8 unplayable Unit/Hero
        // cards sitting in hand (energy=67, materials=4) while Economy kept building whichever local
        // hex ranked best on ScarcityBonus/CitadelDistanceScore alone (see BuildFacilityTask.RankHex)
        // — nothing in that ranking ever asked "which resource does the unplayed hand actually
        // need". This is that missing signal: for every unplayed Unit/Hero card in hand (Base/
        // Facility/Recce excluded — see the skip below, same exclusions TryPlayCardCandidates
        // already applies to this same hand), deficit(resource) = max(0, cardCost(resource) -
        // AiResourceReservation.Available(resource)) — Available, never root.GetResource directly,
        // for the exact reason FindPlacement's own comment gives: must not double-count what an
        // active BuildFacility/BuildBase task has already claimed toward its own build. Each card's
        // own contribution is capped at AiConfig.handDemandPerCardCap (see that constant's own
        // comment) before being summed into the running per-resource total.
        //
        // INTERNAL ranking signal only — same "computed fresh, used only to break/bias an internal
        // choice, never leaks into the cross-category AiDecision.Score" scoping
        // UnitCompositionFitBonus/RosterShape already keep to themselves in this same file. Wired
        // into BuildFacilityTask.RankHex as a small bonus toward whichever resourceType currently
        // carries the hand's own single highest demand (see that method's own HandDemandBonus) —
        // must never change Economy's own priority relative to Defence/Aggression in the Arbiter.
        public static Dictionary<ResourceType, float> ComputeHandResourceDemand(PlayerSetupData player, PlayerRoot root, AiHandData hand)
        {
            var demand = new Dictionary<ResourceType, float>
            {
                [ResourceType.Human] = 0f, [ResourceType.Energy] = 0f, [ResourceType.Materials] = 0f, [ResourceType.Tech] = 0f,
            };
            if (hand == null || root == null || player == null)
                return demand;

            foreach (CardData card in hand.Hand)
            {
                // Base/Facility cards never reach IsUnitOrHeroCard at all; Recce is excluded the
                // same way TryPlayCardCandidates already excludes it from this same hand read (see
                // that method's own comment — a Recce card competes for AiScoutPlanner's own pipeline,
                // not this one, and doesn't compete for the resource categories this demand signal
                // cares about the way an ordinary Unit/Hero card does).
                if (!IsUnitOrHeroCard(card) || IsRecceCard(card))
                    continue;
                ResourceCost cost = card.Definition.resourceCost;
                AddCappedDeficit(demand, ResourceType.Human, cost.human, root, player);
                AddCappedDeficit(demand, ResourceType.Energy, cost.energy, root, player);
                AddCappedDeficit(demand, ResourceType.Materials, cost.materials, root, player);
                AddCappedDeficit(demand, ResourceType.Tech, cost.tech, root, player);
            }
            return demand;
        }

        private static void AddCappedDeficit(Dictionary<ResourceType, float> demand, ResourceType type, int cardCost,
            PlayerRoot root, PlayerSetupData player)
        {
            if (cardCost <= 0)
                return;
            int available = AiResourceReservation.Available(root, player, type);
            int deficit = System.Math.Max(0, cardCost - available);
            if (deficit <= 0)
                return;
            demand[type] += System.Math.Min(deficit, AiConfig.handDemandPerCardCap);
        }

        // ---- Разыгрывание карты — чередование ролей (Hero/Unit) ----

        private static readonly Dictionary<PlayerSetupData, CardRole> LastPlayedCardRole = new Dictionary<PlayerSetupData, CardRole>();

        // Alternation state for TryPlayCardCandidates's own cardRoleAlternationDamping (see that
        // field's own comment) — Recce never participates (AiScoutPlanner scores/places it
        // entirely on its own, see FindPlacement's own comment), so callers only ever pass Hero
        // or Unit here. No stored-state default needed the way FallbackKind's IsPreferred has
        // one: absent from the dictionary simply means "nothing of this role has been played yet
        // this game", which correctly cools down neither role.
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

        // One candidate per affordable Hero/Unit card in hand — Base/Facility cards are skipped
        // entirely, same as before (see AiTurnController.Decide's own class comment, point 4).
        // Recce cards are skipped too, always (2026-08-19, project owner's own call — "тут никто
        // не гонится, ScoutPlanner выполняет свою задачу, PlayCard выполняет свою"): a Recce card
        // is placed and scored entirely by AiScoutPlanner.TryStartReconAssemblyCandidatesFor now,
        // never proposed here at all, so the two categories no longer compete for the same card.
        // Removing a Recce card from hand still lowers hand.Hand's own count for the next step's
        // unplayedUnitCards/unplayedHeroCards read below, regardless of which category played it
        // — that's just hand.Hand.Count, nothing here has to special-case who did the playing
        // (this is the "ScoutPlanner разыгрывая карту влияет на скор PlayCard-задачи" cross-effect
        // the project owner flagged — it already falls out of the shared hand for free, and the
        // same will hold the day AiAggressionPlanner ever grows its own direct card-hand pipeline
        // the way Разведка has).
        //
        // Placement (who has room, whether it's even affordable) lives in FindPlacement — this
        // only assigns the Score and builds the AiDecision.
        //
        // Hero and Unit share one score formula (2026-08-19 — playHeroCardBonus removed, project
        // owner's own call: "убираем playHeroCardBonus, альтернейшен закроет необходимость в
        // героях"): managementBaseWeight plus a backlog term scaled by that role's own SHARE of
        // the unplayed Hero+Unit pool (cardRoleBacklogShareWeight — see that constant's own
        // comment for why this reads as a bounded share, not a raw per-card count, since
        // 2026-08-21), damped by cardRoleAlternationDamping (IsCardRoleCoolingDown) for whichever
        // role just had a card played, so the AI alternates Hero/Unit turn to turn instead of
        // exhausting one role's entire backlog before touching the other (the project owner's own
        // 2026-08-17 report). Only ONE Hero card is ever proposed per step now (2026-08-24 —
        // see the Hero pre-pass below, added to match the Unit pre-pass's own principle once
        // UnitAbilities.ApBonus gave Hero cards a reason to actually differ in value from each
        // other — a flat-statted "Attack 0/Defense 0 + ApBonus" hero used to tie every ordinary
        // combat hero on the exact same heroScore, so which one got played came down to arbitrary
        // hand order); every other affordable Hero card gets its own look once this one's played
        // (or fails to place) and hand is re-read fresh next step, same as Unit cards already do.
        //
        // Unit cards are handled the same way (2026-08-19, project owner's own explicit check —
        // "надеюсь это внутренний скоринг этой задачи и он не влияет на скоринг между остальными
        // задачами": confirmed, and the first version of this DIDN'T honor that — UnitComposition
        // FitBonus used to be added straight into the externally-competing Score, the exact
        // "internal ranking leaking into the cross-category arbiter" mistake VisitHexTask.
        // FindTarget's own Score and BuildFacilityTask.RankHex both go out of their way to avoid,
        // see their own comments). Fixed the same way those two already solve it: an internal-
        // only pre-pass picks ONE preferred Unit card among every currently-placeable one in hand
        // (UnitCompositionFitBonus is the ranking key, never seen outside this method), and ONLY
        // that single winner becomes a candidate — scored on the exact same plain
        // managementBaseWeight+backlog formula every other Unit card would have gotten, with zero
        // trace of the fit bonus in it. Every other Unit card this step simply isn't proposed at
        // all; it gets its own look once this one's played (or fails to place) and hand/roster are
        // re-read fresh next step — see this method's own re-invocation every Decide() call, which
        // is also what keeps RosterShape from ever going stale between one played card and the next.
        // The Hero pre-pass just below reuses CardCombatValue (not a fresh ranking key of its own)
        // as ITS internal-only tie-break, same "never seen outside this method" scoping — only its
        // ranking role is new, the value formula itself was already shared with the Unit pre-pass's
        // own tie-break (see CardCombatValue's own comment).
        public static List<AiDecision> TryPlayCardCandidates(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var results = new List<AiDecision>();
            if (hand == null)
                return results;

            int unplayedUnitCards = hand.Hand.Count(c => IsUnitOrHeroCard(c) && !IsRecceCard(c)
                && c.Definition.cardType == CardType.Unit);
            int unplayedHeroCards = hand.Hand.Count(c => IsUnitOrHeroCard(c) && !IsRecceCard(c)
                && c.Definition.cardType == CardType.Hero);
            // Share of the unplayed Hero+Unit pool, not a raw count (2026-08-21 fix — see
            // AiConfig.cardRoleBacklogShareWeight's own comment for why a raw count structurally
            // favored Unit, the deck's more numerous role, regardless of how long Hero cards sat
            // unplayed). The two shares always sum to 1; 0 unplayed total means neither role has
            // anything to be urgent about.
            int totalRoleCards = unplayedUnitCards + unplayedHeroCards;
            float heroShare = totalRoleCards > 0 ? (float)unplayedHeroCards / totalRoleCards : 0f;
            float unitShare = totalRoleCards > 0 ? (float)unplayedUnitCards / totalRoleCards : 0f;
            float heroAlternationDamping = IsCardRoleCoolingDown(player, CardRole.Hero) ? AiConfig.cardRoleAlternationDamping : 1f;
            float unitAlternationDamping = IsCardRoleCoolingDown(player, CardRole.Unit) ? AiConfig.cardRoleAlternationDamping : 1f;
            // Same formula every Hero/every Unit candidate this step actually scores at (see this
            // method's own comment) — computed once here, up front, so both sides of the
            // alternation can be logged on EVERY PlayCard candidate this step produces, not just
            // whichever role happened to win (project owner's own 2026-08-19 debug-visibility ask).
            float heroScore = AiConfig.managementBaseWeight
                + heroAlternationDamping * AiConfig.cardRoleBacklogShareWeight * heroShare;
            float unitScore = AiConfig.managementBaseWeight
                + unitAlternationDamping * AiConfig.cardRoleBacklogShareWeight * unitShare;

            // Absolute deployment pressure on top of the share-based score above (2026-08-24,
            // project owner's own report — see AiConfig.managementBacklogSoftLimit's own comment):
            // a deep total Hero+Unit backlog pushes BOTH roles' scores up together, independent of
            // how that backlog happens to split between the two roles. Never lowers either score
            // (a small/empty backlog contributes zero) and never crosses managementDeploymentScoreCap.
            float backlogPressure = Mathf.Min(AiConfig.managementBacklogBonusCap,
                Mathf.Max(0, totalRoleCards - AiConfig.managementBacklogSoftLimit) * AiConfig.managementBacklogPerCardBonus);
            heroScore = ApplyDeploymentPressure(heroScore, backlogPressure);
            unitScore = ApplyDeploymentPressure(unitScore, backlogPressure);

            string roleBalance = $" [Hero/Unit alternation: hero={heroScore:0.0} (backlog={unplayedHeroCards}"
                + (heroAlternationDamping < 1f ? ", cooling down" : "") + $"), unit={unitScore:0.0} (backlog={unplayedUnitCards}"
                + (unitAlternationDamping < 1f ? ", cooling down" : "") + ")]";

            // Internal-only pre-pass — see this method's own class comment. CardCombatValue is
            // the ranking key (now folding in UnitAbilities.ApBonus — see its own comment), never
            // seen outside this loop; only the single winner becomes a candidate, at the plain
            // heroScore, with zero trace of its own value in it.
            CardData bestHeroCard = null;
            CardPlacement? bestHeroPlacement = null;
            float bestHeroValue = float.NegativeInfinity;
            foreach (CardData card in hand.Hand)
            {
                if (!IsUnitOrHeroCard(card) || IsRecceCard(card) || RoleOf(card) != CardRole.Hero)
                    continue; // Recce is AiScoutPlanner's own; Unit cards are the pre-pass below
                CardPlacement? heroPlacement = FindPlacement(player, root, card);
                if (!heroPlacement.HasValue)
                    continue;
                float value = CardCombatValue(card.Definition);
                if (bestHeroCard == null || value > bestHeroValue)
                {
                    bestHeroCard = card;
                    bestHeroPlacement = heroPlacement;
                    bestHeroValue = value;
                }
            }
            if (bestHeroCard != null)
            {
                AiDecision decision = AiDecision.PlayCard(bestHeroPlacement.Value.ExistingArmy, bestHeroCard, CardRole.Hero, heroScore, AiTaskCategory.Management);
                decision.Reason += roleBalance;
                results.Add(decision);
            }

            // Internal-only pre-pass — see this method's own comment. RosterShape is recomputed
            // fresh every call (every Decide() step), so it always reflects whatever the LAST
            // played card actually did to the roster, never a stale snapshot from earlier this turn.
            RosterShape roster = RosterShape.For(player);
            CardData bestUnitCard = null;
            CardPlacement? bestUnitPlacement = null;
            float bestUnitFit = float.NegativeInfinity;
            float bestUnitValue = float.NegativeInfinity;
            foreach (CardData card in hand.Hand)
            {
                if (!IsUnitOrHeroCard(card) || IsRecceCard(card) || RoleOf(card) != CardRole.Unit)
                    continue;
                CardPlacement? placement = FindPlacement(player, root, card);
                if (!placement.HasValue)
                    continue;
                float fit = UnitCompositionFitBonus(player, card.Definition, roster, ctx);
                float value = CardCombatValue(card.Definition);
                // FitBonus is a sum of flat gate bonuses, so exact ties are common; break them by
                // raw combat value instead of falling back to hand order.
                if (bestUnitCard == null || fit > bestUnitFit || (fit == bestUnitFit && value > bestUnitValue))
                {
                    bestUnitCard = card;
                    bestUnitPlacement = placement;
                    bestUnitFit = fit;
                    bestUnitValue = value;
                }
            }
            if (bestUnitCard != null)
            {
                AiDecision decision = AiDecision.PlayCard(bestUnitPlacement.Value.ExistingArmy, bestUnitCard, CardRole.Unit, unitScore, AiTaskCategory.Management);
                decision.Reason += roleBalance;
                results.Add(decision);
            }
            return results;
        }

        // AiConfig.managementBacklogBonusCap's own comment — adds `pressure` without ever crossing
        // managementDeploymentScoreCap. A `score` already at or above the cap (shouldn't happen
        // today, since heroScore/unitScore never reach it on the share term alone, but kept honest
        // for whatever future term might) is left untouched rather than pulled down.
        private static float ApplyDeploymentPressure(float score, float pressure)
        {
            if (score >= AiConfig.managementDeploymentScoreCap)
                return score;
            return Mathf.Min(AiConfig.managementDeploymentScoreCap, score + pressure);
        }

        // Snapshot of the player's own current non-hero unit roster — garrison AND every field
        // army combined (2026-08-19, project owner's own call: "смотрим на составы всех армий
        // игрока-ИИ", this player's own, never an opponent's — see UnitCompositionFitBonus's own
        // criterion 7 for the one honest exception, read through AiMapMemory instead of this
        // struct). This class doesn't decide which army a Unit card ends up in — FindPlacement
        // already always tries the garrison first (see its own comment) — so there's no "which
        // army's composition" question here, only "what does the player's stock as a whole look
        // like".
        private readonly struct RosterShape
        {
            public readonly float AvgDefense;
            public readonly float AvgAttack;
            public readonly int MeleeCount;
            public readonly int RangedCount;
            public readonly int AbilityHeavyCount;
            public readonly int SimpleCount;

            private RosterShape(float avgDefense, float avgAttack, int meleeCount, int rangedCount, int abilityHeavyCount, int simpleCount)
            {
                AvgDefense = avgDefense;
                AvgAttack = avgAttack;
                MeleeCount = meleeCount;
                RangedCount = rangedCount;
                AbilityHeavyCount = abilityHeavyCount;
                SimpleCount = simpleCount;
            }

            public static RosterShape For(PlayerSetupData player)
            {
                List<UnitData> roster = ArmyRegistry.AllForOwner(player).SelectMany(a => a.Members).Where(m => !m.IsHero).ToList();
                if (roster.Count == 0)
                    return new RosterShape(0f, 0f, 0, 0, 0, 0);
                return new RosterShape(
                    (float)roster.Average(m => m.Defense),
                    (float)roster.Average(m => m.Attack),
                    roster.Count(m => m.Range <= 1),
                    roster.Count(m => m.Range > 1),
                    roster.Count(m => m.Abilities.Count > 0),
                    roster.Count(m => m.Abilities.Count == 0));
            }
        }

        // Composition-gap scoring for a Unit card, on top of TryPlayCardCandidates' own backlog
        // term (2026-08-19, project owner's own spec) — a flat AiConfig.unitCompositionGapBonus
        // per gap this specific card actually addresses, summed (real scoring, not a first-match
        // branch chain — several gaps can be true at once, and a card closing more than one of
        // them should outscore one closing only one):
        //  1/2) Defense/Attack imbalance — if the roster hits softer than it takes hits
        //       (AvgDefense < AvgAttack), a card tankier than that average helps; the mirror case
        //       (AvgAttack < AvgDefense) rewards a card that hits harder than average instead.
        //       Mutually exclusive by construction — the roster can't be both at once.
        //  3/4) Melee/ranged imbalance — Range <= 1 must stand in the Front row to attack at all
        //       (see UnitData.Range's own comment), so a roster with more melee than ranged wants
        //       a ranged card and vice versa. Also mutually exclusive by construction.
        //  5)   Too many ability-heavy units already (Abilities.Count > 0) — a plain card with no
        //       granted abilities evens that back out.
        //  6)   A critically wounded member (RaidWeakerArmyTask.IsCriticallyWounded's own ≤50%HP
        //       threshold) of an active raid force this card shares a type shape with (TypeTag
        //       overlap or same melee/ranged class) — that member already contributes little to the
        //       task's own WorthIt.IsReady read (its low HP has it die early in the simulated fight,
        //       see IsReady's own comment), so a like-for-like replacement is exactly the
        //       "force shortage" the project owner asked to watch for.
        //  7)   Counter-tech — Hyperkinetic once AiMapMemory.KnownEnemyTypeTagCount reports a
        //       real, actually-scouted Armored sighting; Pyrokinetic the same way for Bio. Honest
        //       by construction — that count is never anything but an observed sighting.
        //  8)   TaskNeedBonus — closes a StillAssembling combat task's own gap against its ACTUAL
        //       target/threat (2026-08-23, project owner's own spec, generalizing what used to be
        //       a Raid-only criterion into one shared read across every still-recruiting combat
        //       task — Raid and Defence today, "позднее другие подобные задачи" per the project
        //       owner's own note). Narrower than criterion 7 above: criterion 7 only ever fires for
        //       the two hard-coded Hyperkinetic/Pyrokinetic counters, and reads AiMapMemory
        //       globally (any known Armored/Bio anywhere), not specifically what a given task is
        //       stuck on. This one asks the real question directly — would THIS card cover a
        //       defender at the task's own actual target/threat that nobody currently in its army
        //       can already damage (WorthIt.CanDamage, the same coverage math RaidWeakerArmyTask.
        //       IsReady's own CanDamageAll gate uses) — so a plain stat/type gap the roster-wide
        //       heuristics above have no gate for still gets recognized. Defence's own share of
        //       this outweighs Raid's whenever a REAL threat (a known Active sighting) is actually
        //       driving that assembly — see TaskNeedBonus's own comment for the full split. Only
        //       ever checked against a task that's still recruiting (StillAssembling, not
        //       Retreating) — a task already moving out doesn't recruit through the hand any more.
        private static float UnitCompositionFitBonus(PlayerSetupData player, CardDefinition definition, RosterShape roster, AiTurnContext ctx)
        {
            float bonus = 0f;

            if (roster.AvgDefense < roster.AvgAttack && definition.defenseRating > roster.AvgDefense)
                bonus += AiConfig.unitCompositionGapBonus;
            else if (roster.AvgAttack < roster.AvgDefense && definition.attack > roster.AvgAttack)
                bonus += AiConfig.unitCompositionGapBonus;

            if (roster.MeleeCount > roster.RangedCount && definition.range > 1)
                bonus += AiConfig.unitCompositionGapBonus;
            else if (roster.RangedCount > roster.MeleeCount && definition.range <= 1)
                bonus += AiConfig.unitCompositionGapBonus;

            if (roster.AbilityHeavyCount > roster.SimpleCount
                && (definition.grantedAbilities == null || definition.grantedAbilities.Count == 0))
                bonus += AiConfig.unitCompositionGapBonus;

            foreach (AiTask task in AiTaskRegistry.TasksFor(player))
            {
                if (task.Kind != AiTaskKind.RaidWeakerArmy || task.Retreating || task.Army == null
                    || !RaidWeakerArmyTask.IsCriticallyWounded(task.Army))
                    continue;
                bool matchesWounded = task.Army.Members.Any(m => !m.IsHero && m.HitPointsCurrent <= m.HitPointsMax / 2
                    && (m.TypeTags.Overlaps(definition.unitTypeTags) || (m.Range <= 1) == (definition.range <= 1)));
                if (matchesWounded)
                {
                    bonus += AiConfig.unitCompositionGapBonus;
                    break; // one matching wounded army is enough — don't stack per additional one
                }
            }

            if (definition.grantedAbilities != null)
            {
                if (definition.grantedAbilities.Contains(UnitAbilities.Hyperkinetic)
                    && AiMapMemory.KnownEnemyTypeTagCount(player, UnitTypeTag.Armored) > 0)
                    bonus += AiConfig.unitCompositionGapBonus;
                if (definition.grantedAbilities.Contains(UnitAbilities.Pyrokinetic)
                    && AiMapMemory.KnownEnemyTypeTagCount(player, UnitTypeTag.Bio) > 0)
                    bonus += AiConfig.unitCompositionGapBonus;
            }

            bonus += TaskNeedBonus(player, definition, ctx);

            return bonus;
        }

        // Criterion 8's own implementation (2026-08-23, project owner's own spec — "TaskNeedBonus
        // который смотрит на незавершённые боевые compositions: assembling Raid; assembling
        // Defence; позднее другие подобные задачи") — generalizes what used to be a Raid-only
        // "closes the assembling task's own CanDamageAll gap" check into ONE shared read, so a
        // future third combat-assembly task only ever needs its own case added here, nothing else
        // in this file. Not a sum of RaidNeedBonus + DefenceNeedBonus — the STRONGER of the two,
        // deliberately (project owner's own explicit priority call: "приоритет Defence должен быть
        // выше Raid, если есть реальная угроза базе"). A max, not an early-return-on-first-match,
        // is what actually enforces that: DefenceNeedBonus already scores higher than Raid's own
        // flat tier whenever a real threat drives it (see that method's own comment), so whichever
        // task's own need is genuinely bigger this step always wins, with no hand-coded "check
        // Defence before Raid" ordering to keep in sync as more task kinds get added later.
        private static float TaskNeedBonus(PlayerSetupData player, CardDefinition definition, AiTurnContext ctx)
        {
            return System.Math.Max(RaidNeedBonus(player, definition, ctx.Map), DefenceNeedBonus(player, definition, ctx));
        }

        // Raid's own share of TaskNeedBonus — unchanged in substance from the pre-2026-08-23
        // criterion 8 this was split out of, just its own named method now. Flat
        // unitCompositionGapBonus, same tier every other criterion in UnitCompositionFitBonus uses.
        private static float RaidNeedBonus(PlayerSetupData player, CardDefinition definition, HexMap map)
        {
            foreach (AiTask task in AiTaskRegistry.TasksFor(player))
            {
                if (task.Kind != AiTaskKind.RaidWeakerArmy || !task.StillAssembling || task.Retreating || task.Army == null)
                    continue;
                RaidWeakerArmyTask.ThreatStrength required = RaidWeakerArmyTask.RequiredStrengthAt(player, task.TargetHex, map);
                if (ClosesDamageGap(definition, task.Army, required.Defenders, required.HexBonus))
                    return AiConfig.unitCompositionGapBonus; // one matching StillAssembling raid is enough
            }
            return 0f;
        }

        // Defence's own share of TaskNeedBonus (2026-08-23, new) — mirrors RaidNeedBonus's own
        // "closes the gap against the task's real target" check, but Defence has TWO distinct
        // assembling shapes (see AiDefencePlanner's own class comment): Patrol's fixed headcount
        // target (nothing specific sighted yet — AiDefencePlanner.CurrentActiveThreat returns null)
        // vs Active's dynamic composition against a SPECIFIC known sighting. The headcount case has
        // no "defender to cover" to check against — any Unit card fills a genuinely open slot, so
        // it scores Raid's own flat tier, same routine-buildup weight. The sighted case runs the
        // exact same ClosesDamageGap coverage check RaidNeedBonus does, but against the sighting's
        // OWN Defenders — and scores AiConfig.defenceNeedBonusMultiplier times higher, because a
        // real, currently-known threat to a base outranks routine raid buildup on its own terms
        // (the project owner's own explicit priority call — see TaskNeedBonus's own comment).
        // Takes the MAX across every DefendCitadel task, not the first match — unlike Raid
        // (maxConcurrentRaid caps it at one, so "first" and "best" always coincide), Defence can
        // have up to AiConfig.maxConcurrentDefend(2) tasks assembling at once (citadel + a second
        // base); returning on the first one found in registry order could report a routine Patrol
        // headcount gap for one base while a genuinely under-threat Active assembly on the other sits
        // unseen right behind it — exactly the priority inversion this bonus exists to prevent.
        //
        // A THIRD tier on top of the two above (2026-08-23, project owner's own follow-up — "Turtle
        // need > Active Defence need > Raid need > generic composition need"): the sighted branch
        // scores AiConfig.turtleNeedBonusMultiplier instead of the plain defenceNeedBonusMultiplier
        // whenever the task in question is the CITADEL's own AND AiDefencePlanner.IsUnderSiege is
        // true right now — a real siege outranks an ordinary Active sighting at some other base the
        // same way it already outranks everything else in this codebase (defencePreemptScore/
        // defenceTurtleScore's own 130 cross-category tier). Checked once per call, not per task
        // (IsUnderSiege is already citadel-scoped internally, so re-checking it once per iteration
        // would just repeat the exact same read for a second base's own task).
        private static float DefenceNeedBonus(PlayerSetupData player, CardDefinition definition, AiTurnContext ctx)
        {
            float best = 0f;
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            bool turtle = AiDefencePlanner.IsUnderSiege(player, ctx);
            foreach (AiTask task in AiTaskRegistry.TasksFor(player))
            {
                if (task.Kind != AiTaskKind.DefendCitadel || !task.StillAssembling || task.Retreating || task.Army == null)
                    continue;

                AiMapMemory.KnownEnemySighting? threat = AiDefencePlanner.CurrentActiveThreat(player, task.HomeHex);
                if (!threat.HasValue)
                {
                    best = System.Math.Max(best, AiConfig.unitCompositionGapBonus); // Patrol's own headcount gap — routine buildup
                    continue;
                }

                float hexBonus = WorthIt.HexDefenseBonus(threat.Value.Hex, ctx.Map);
                if (ClosesDamageGap(definition, task.Army, threat.Value.Defenders, hexBonus))
                {
                    float multiplier = turtle && task.HomeHex.Equals(citadelHex)
                        ? AiConfig.turtleNeedBonusMultiplier : AiConfig.defenceNeedBonusMultiplier;
                    best = System.Math.Max(best, AiConfig.unitCompositionGapBonus * multiplier); // a REAL threat is driving this
                }
            }
            return best;
        }

        // Would `definition` cover a defender at a StillAssembling task's own target that nobody
        // currently in `army` can already damage? See UnitCompositionFitBonus's own criterion 8
        // comment (TaskNeedBonus) for why this reads the task's ACTUAL target/threat instead of the
        // roster-wide gap heuristics above. Routes through WorthIt.CanDamage — the exact same per-unit coverage
        // check RaidWeakerArmyTask.IsReady's own CanDamageAll gate uses — so this can never
        // disagree with what actually decides the raid ready to fight.
        private static bool ClosesDamageGap(CardDefinition definition, ArmyData army,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float hexBonus)
        {
            if (defenders == null || defenders.Count == 0)
                return false; // nothing guarding the target — no gap to close
            List<UnitData> ours = army.Members.Where(m => !m.IsHero).ToList();
            foreach (WorthIt.DefenderProfile defender in defenders)
            {
                bool alreadyCovered = ours.Any(u => WorthIt.CanDamage(u.Attack, defender, hexBonus));
                if (!alreadyCovered && WorthIt.CanDamage(definition.attack, defender, hexBonus))
                    return true;
            }
            return false;
        }

        // Raw combat value of a Unit card, used only to break exact UnitCompositionFitBonus ties
        // (that bonus is a sum of flat gate values, so ties are common) — not a scoring term in
        // its own right. Attack/Defense weighted 1:1, HP and Initiative scaled down (0.25/0.5) so
        // they nudge the tie-break without swamping Attack/Defense given this project's card stat
        // ranges (HP up to ~12 vs. Attack/Defense up to ~6).
        private static float CardCombatValue(CardDefinition definition)
        {
            float value = definition.attack + definition.defenseRating
                + definition.hitPoints * 0.25f + definition.initiative * 0.5f;
            // UnitAbilities.ApBonus (see AiConfig.apBonusCardStrategicValue's own comment) — a
            // card built around this skill can look worthless by raw stats alone (e.g. Attack 0/
            // Defense 0) while still being the stronger strategic pick, so it needs its own term
            // here rather than reading as strictly worse than any ordinary combat card.
            if (definition.grantedAbilities != null && definition.grantedAbilities.Contains(UnitAbilities.ApBonus))
                value += AiConfig.apBonusCardStrategicValue;
            return value;
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
                return AiDecision.Wait(task, $"\"{task.Army.Name}\" is saving up to repair {task.TargetUnit.Name}");

            if (WouldBlockAffordableCard(root, hand, apCost, cost))
                return AiDecision.Wait(task, $"repairing {task.TargetUnit.Name} can wait — "
                    + "otherwise a pricier card in hand would go unaffordable");

            return AiDecision.RepairUnit(task, AiConfig.repairUnitBaseWeight);
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

        // Менеджмент · CollapseTemporaryAssembly — see GarrisonReorgTask.FindCollapseMove's own
        // comment. First in the three-way priority (Collapse → Overflow → IdleBalance, see
        // AiTurnController.RunGarrisonReorgPhase) — FindCollapseMove itself already does all the
        // "checked before proposing" work (atomic fit, AP affordability, oscillation guard), so
        // there's nothing left for this method to gate on its own.
        public static AiDecision TryCollapseCandidate(PlayerSetupData player, ArmyData garrison, AiTurnContext ctx)
        {
            if (garrison == null)
                return null;
            GarrisonReorgTask.CollapseMove? move = GarrisonReorgTask.FindCollapseMove(player, garrison.Hex, garrison, ctx);
            return move.HasValue ? AiDecision.Collapse(move.Value) : null;
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
            return AiDecision.SplitGarrison(garrison, overflow, destination.Value.ExistingArmy);
        }

        // Менеджмент · передача юнитов между армиями в базе — see
        // GarrisonReorgTask.FindReorgMove's own comment for scope/exclusions. Falls back to
        // GarrisonReorgTask.FindReorgSwap (2026-08-20) only once a plain move finds nothing this
        // same call — a swap is strictly a last resort for when nobody involved has a free slot,
        // never a substitute for a plain move that's actually available.
        public static AiDecision TryConsolidationCandidate(PlayerSetupData player, ArmyData garrison, AiTurnContext ctx)
        {
            // `garrison`'s OWN hex, not AiTurnController.GarrisonHexFor(player) — that always
            // resolves to the citadel specifically now (see its own comment), which silently
            // consolidated against the citadel hex even when `garrison` was a later-founded base's
            // own garrison (bug introduced 2026-08-21 alongside multi-base support, caught before
            // it ever shipped: RunGarrisonReorgPhase now calls this once per garrison).
            HexCoord garrisonHex = garrison.Hex;
            GarrisonReorgTask.ConsolidationMove? move = GarrisonReorgTask.FindReorgMove(player, garrisonHex, garrison, ctx);
            if (move.HasValue)
                return AiDecision.Consolidate(move.Value);
            GarrisonReorgTask.SwapMove? swap = GarrisonReorgTask.FindReorgSwap(player, garrisonHex, garrison, ctx);
            return swap.HasValue ? AiDecision.Swap(swap.Value) : null;
        }

        // ---- Менеджмент · Feature 4B (stranded lone field army) — P0 fix, 2026-08-24 ----
        // See AiTaskKind.ReturnForConsolidation's own comment for the full story: this task gets
        // REGISTERED once, at the very end of a previous turn, by AiTurnController.
        // RunStrandedArmyRecovery (Stage B of its own end-of-turn reorg phase) — this method is
        // its ordinary per-step CONTINUATION, wired into AiTurnController.Decide's own ROOT
        // per-task loop exactly like BuildFacility/VisitHex/RaidWeakerArmy/DefendCitadel already
        // are, so the walk home now competes for AP/step budget on the same honest footing as
        // everything else instead of being silently skipped whenever the turn had already spent
        // itself down by the time the old end-of-phase-only sweep got a look.
        //
        // HomeHex is recomputed FRESH every call, never trusted from task.TargetHex (same "always
        // whichever base is genuinely closest RIGHT NOW, not whichever one it happened to start
        // near" rule AiAggressionPlanner.TryContinueRaidTask's own homeHex already follows for a
        // fleeing raid) — a later-founded base can end up closer than whichever garrison was
        // nearest when this task was first registered. task.TargetHex is kept in sync purely for
        // AiTurnController.LogActiveTasks/BuildArmyBreakdownLog's own benefit (both just read it
        // for display), never re-read by this method itself.
        //
        // Arrival is this task's own completion condition — the moment the army reaches its home
        // hex, the task is simply removed; GarrisonReorgTask's EXISTING FindLoneArmyFoldMove/
        // FindReorgMove consolidation pass (already running earlier in the SAME end-of-turn phase
        // this task was originally born from — see AiTurnController.RunGarrisonReorgPhase) picks
        // the now-arrived lone unit up automatically on a LATER pass/turn — this method never folds
        // it in itself, per the project owner's own original spec (no new consolidation logic
        // needed here at all).
        public static AiDecision AdvanceReturnForConsolidationTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            HexCoord homeHex = AiTurnController.NearestOwnGarrisonHex(player, task.Army.Hex);
            if (task.Army.Hex.Equals(homeHex))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — reached ({homeHex.Q},{homeHex.R}), "
                    + "ReturnForConsolidation task complete.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            task.TargetHex = homeHex;

            if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, homeHex))
                return null;

            var target = new AiScoutPlanner.ScoutTarget(homeHex, 0f, "stranded alone — returns for consolidation");
            return AiDecision.Move(task.Army, target, task, AiConfig.returnForConsolidationWeight, AiTaskCategory.Management);
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
            if (spareArmies < AiConfig.maxSpareArmies && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                results.Add(AiDecision.Reserve(MostActiveOwnGarrisonHex(player), spareArmies, reservePreferred
                    ? AiConfig.managementFallbackHighScore : AiConfig.managementFallbackLowScore));

            if (hand != null && hand.HasCardsLeftToDraw && root.CanSpendActionPoints(ctx.DrawApCost))
                results.Add(AiDecision.Draw(reservePreferred
                    ? AiConfig.managementFallbackLowScore : AiConfig.managementFallbackHighScore));
            return results;
        }

        // ---- Execution ----

        // `homeHex` — which of the player's own garrisoned hexes to spawn at (see
        // GatherFallbackCandidates, the only caller that ever proposes this decision), carried
        // through AiDecision.TargetHex since this routine doesn't otherwise receive the decision.
        public static IEnumerator ReserveArmyRoutine(PlayerSetupData player, AiTurnContext ctx, HexCoord homeHex)
        {
            HexCoord hex = homeHex;
            yield return AiTurnController.PanTo(ctx, hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            ArmyData army = ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write(army != null
                ? $"[AI] {player.Nickname}: creates spare reserve army \"{army.Name}\".{delta}"
                : $"[AI] {player.Nickname}: not enough AP for a reserve army.");
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
                int ap0 = root.ActionPoints;
                int human0 = root.GetResource(ResourceType.Human);
                int energy0 = root.GetResource(ResourceType.Energy);
                int materials0 = root.GetResource(ResourceType.Materials);
                int tech0 = root.GetResource(ResourceType.Tech);
                CardData card = hand.DrawOne();
                if (card != null)
                {
                    root.SpendActionPoints(ctx.DrawApCost);
                    string delta = AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0);
                    AiDebugLog.Write($"[AI] {player.Nickname}: draws a card — {card.Definition.displayName}.{delta}");
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

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;

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
                    AiDebugLog.Write($"[AI] {player.Nickname}: not enough AP for a new army for the overflowing garrison.");
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
                        AiDebugLog.Write($"[AI] {player.Nickname}: {promotedHero.Name} steps off the garrison bench to lead new army \"{destination.Name}\".");
                    else
                        AiDebugLog.Write($"[AI] {player.Nickname}: couldn't put {promotedHero.Name} in charge of \"{destination.Name}\" — {heroFailReason}");
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
                    AiDebugLog.Write($"[AI] {player.Nickname}: couldn't transfer {unit.Name} out of the garrison — {failReason}");
            }
            string splitDelta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write($"[AI] {player.Nickname}: garrison was full — {moved} unit(s) moved into \"{destination.Name}\".{splitDelta}");

            // Экономика's own hero-detach case (see AiDecision.EconomyBuildHex's own comment) —
            // register the BuildFacility task right here, the moment "destination" is actually a
            // real hero-led army, so the very next Decide() call's own "claim every army already
            // committed to a persistent task" sweep (AiTurnController.Decide) protects her from
            // Aggression/Defence's own recruiters immediately, instead of leaving her "available"
            // until Economy's own separate next-step rediscovery (FindNearestHeroAnywhere) gets
            // around to claiming her the normal way. IsHeroLed re-check covers the (rare, multi-hero
            // garrison) case where the transfer above didn't actually land a hero in her — nothing
            // to build with otherwise.
            if (decision.EconomyBuildHex.HasValue && AiArmyRoles.IsHeroLed(destination))
            {
                AiTaskRegistry.Add(player, new AiTask
                {
                    Kind = AiTaskKind.BuildFacility,
                    Army = destination,
                    TargetHex = decision.EconomyBuildHex.Value,
                    ResourceType = decision.EconomyResourceType,
                });
                // Same "starts Economy task" wording Commit's own generic task-registration path
                // logs for every OTHER BuildFacility task — this one registers directly instead
                // (see this branch's own comment above) so it needs its own line, or a log audit
                // would see the hero detach but never see the resulting task actually start.
                AiHandData handForLog = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
                AiDebugLog.Write($"[AI] {player.Nickname}: starts Economy task — \"{destination.Name}\" heads out "
                    + $"to build {decision.EconomyResourceType} at ({decision.EconomyBuildHex.Value.Q},{decision.EconomyBuildHex.Value.R})."
                    + AiTurnController.HandDemandLogSuffix(player, decision.EconomyResourceType, root, handForLog));
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(destination);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Менеджмент · CollapseTemporaryAssembly — see GarrisonReorgTask.FindCollapseMove's own
        // comment and TryCollapseCandidate above. move.UnitsToMove is already fully verified atomic
        // (every member individually checked for AP affordability/oscillation, and the garrison's
        // own free-slot count already covers the whole list) — this loop is defensive only (still
        // reads ArmyActions.TransferMember's own return value per unit rather than assuming success,
        // same "never trust a stale precondition through a yield" caution every other routine here
        // already follows), never the mechanism the atomicity guarantee itself relies on. Per-unit
        // detail only logs when AiConfig.verboseGarrisonReorgLogging is on (project owner's own
        // debug-flag ask) — the one line every collapse always writes, win or partial, is the
        // "{N} unit(s) returned" summary below, replacing what the old per-unit tier 1b would have
        // logged as several separate ConsolidateUnits lines for the exact same event.
        public static IEnumerator CollapseTemporaryAssemblyRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            GarrisonReorgTask.CollapseMove move = decision.CollapseMove;
            yield return AiTurnController.PanTo(ctx, move.Source.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;

            int moved = 0;
            foreach (UnitData unit in move.UnitsToMove)
            {
                if (!move.Garrison.HasRoom)
                    break;
                if (ArmyActions.TransferMember(unit, move.Source, move.Garrison, ctx.HexSelection, out string failReason))
                {
                    moved++;
                    ctx.RecordArmyVisit(unit, move.Source, move.Garrison);
                    if (AiConfig.verboseGarrisonReorgLogging)
                        AiDebugLog.Write($"[AI] {player.Nickname}: {unit.Name} returns from \"{move.Source.Name}\" to the garrison (collapse).");
                }
                else if (AiConfig.verboseGarrisonReorgLogging)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: couldn't return {unit.Name} to the garrison — {failReason}");
                }
            }

            string label = move.Task.Kind == AiTaskKind.RaidWeakerArmy ? "Raid" : "Defence";
            string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{move.Source.Name}\" temporary {label} assembly collapsed into the garrison — "
                + $"{moved} unit(s) returned; task preserved, target=({move.Task.TargetHex.Q},{move.Task.TargetHex.R}).{delta}");

            ctx.HexSelection?.DeleteArmyIfEmptied(move.Source);

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.Garrison);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Менеджмент · передача юнитов между армиями в базе — see GarrisonReorgTask.FindReorgMove
        // and TryConsolidationCandidate.
        public static IEnumerator ConsolidateUnitsRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            GarrisonReorgTask.ConsolidationMove move = decision.ConsolidationMove;
            yield return AiTurnController.PanTo(ctx, move.Source.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            bool moved = ArmyActions.TransferMember(move.Unit, move.Source, move.Target, ctx.HexSelection, out string failReason);
            if (moved)
            {
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.{delta}");
                ctx.HexSelection?.DeleteArmyIfEmptied(move.Source);
                // Feeds FindReorgMove's own oscillation guard (see AiTurnContext.WouldRevisitArmy's
                // own comment) — only a move that actually landed counts as "visited", same as
                // every other piece of AI per-turn state here (a candidate that was merely
                // proposed but never picked this step leaves no trace).
                ctx.RecordArmyVisit(move.Unit, move.Source, move.Target);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't merge {move.Unit.Name} — {failReason}");
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.Target);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Менеджмент · обмен юнитов между армиями в базе — see GarrisonReorgTask.FindReorgSwap and
        // TryConsolidationCandidate. Only ever reached once a plain ConsolidateUnits move already
        // came up empty this same call — see AiDecision.Swap's own comment.
        public static IEnumerator ConsolidateSwapRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            GarrisonReorgTask.SwapMove move = decision.SwapMove;
            yield return AiTurnController.PanTo(ctx, move.ArmyA.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            bool swapped = ArmyActions.SwapMembers(move.UnitA, move.ArmyA, move.UnitB, move.ArmyB, ctx.HexSelection, out string failReason);
            if (swapped)
            {
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.{delta}");
                // Same oscillation guard every other reorg move here feeds — both units, both
                // directions, so neither leg of this exact trade can undo itself later this turn.
                ctx.RecordArmyVisit(move.UnitA, move.ArmyA, move.ArmyB);
                ctx.RecordArmyVisit(move.UnitB, move.ArmyB, move.ArmyA);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't swap {move.UnitA.Name} for {move.UnitB.Name} — {failReason}");
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.ArmyB);
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
            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(army);
            yield return AiTurnController.WaitStep(ctx);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            if (UnitRepair.TryRepair(task.TargetUnit, army.Hex, root, out string failReason))
            {
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" repaired {task.TargetUnit.Name} at "
                    + $"({army.Hex.Q},{army.Hex.R}) — Management task complete.{delta}");
            }
            else
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't repair {task.TargetUnit.Name} — {failReason}.");
            AiTaskRegistry.Remove(player, task);

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
