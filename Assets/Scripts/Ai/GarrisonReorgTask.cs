using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Менеджмент · Реорганизация (AI architecture doc, section 02 · «Менеджмент») — split into its
    // own task class like every other Задача (see VisitHexTask's own class comment for why),
    // instead of living inline in AiManagementPlanner, which now only keeps the Менеджмент
    // primitives genuinely shared with OTHER Менеджмент concerns (card placement, Reserve/Draw
    // alternation) rather than this task's own.
    //
    // Цель — no persistent AiTask/target hex the way Разведка/Экономика/Агрессия have; every
    // candidate here is a one-shot reorg move, re-evaluated fresh every call (same "непрерывная
    // переоценка" every other planner already follows) instead of being tracked. The project
    // owner's own spec: garrison AND every other army stay balanced — nothing left weak or empty
    // that stock elsewhere could fix. Not called from Decide's own per-step arbitration any more
    // (2026-08-20) — see AiTurnController.RunGarrisonReorgPhase, which drains this fresh, once,
    // as the very last thing a turn does, unconditionally — no Score exists for any of this any
    // more (removed 2026-08-20 along with the old AiConfig.managementGarrisonBalanceScore). Never
    // spawns a new army and never touches the hand/deck (project owner's own 2026-08-20 spec,
    // point 4) — every move here is between armies that already exist.
    //
    // Композиция — n/a, doesn't run through one owned army.
    //
    // Поведение — FindReorgMove tries, in order, every call (2026-08-20 redesign, project owner's
    // own spec):
    //   0) FindHeroCapacityExpansionMove — a lone hero sitting alone at base can't fold into a
    //      genuinely full garrison the ordinary way (ArmyActions.TransferMember only ever checks
    //      the target's CURRENT capacity, never what the capacity would become once the mover
    //      itself joins), even when that hero's own CommandRating would raise the garrison's
    //      capacity well past its plain-unit baseline once seated. So this tier lends the
    //      garrison's own weakest member INTO the lone hero's army first (which has room — a
    //      bare hero's Capacity is its CommandRating) — opening one real garrison slot under the
    //      OLD capacity. The hero can then fold in normally next call (tier 1), which immediately
    //      raises the garrison's capacity, and the unit lent out a moment ago — now alone again —
    //      folds straight back in the call after that (tier 1 again). Net effect over 2-3 drain
    //      calls: the hero ends up leading the garrison, every unit that was ever involved ends up
    //      back inside it too, and the now-empty former hero army is left behind (fine — empty
    //      armies don't disappear at a barracks hex, see RunGarrisonReorgPhase's own comment).
    //   1) A lone (single-member) army — hero or plain unit alike, no distinction between the two
    //      (see IsLoneArmyAtBase's own comment) — folds back into garrison instead of sitting there
    //      a fragile single unit forever, gated on raw HasRoom, NOT AiManagementPlanner.
    //      HasGarrisonDepositRoom's reserved-slot buffer (2026-08-20, project owner's own call —
    //      that buffer exists for ordinary CARD deposits, not for protecting an already-exposed
    //      solo unit/hero, so this tier ignores it: "если гарнизон 3 из 4, и есть соло юнит/герой,
    //      то этот юнит должен попасть в гарнизон и быть защищённым"). If the garrison has no room
    //      at all, it falls into whichever OTHER field army at base has room
    //      instead (weakest-average-strength recipient first) rather than being left alone — a
    //      unit isn't required to specifically wait on the garrison (project owner's own "в
    //      гарнизон или другую армию" spec). No Recce carve-out any more (2026-08-20, project
    //      owner's own call, point 1.2) — this class no longer tracks scouting composition at all;
    //      Разведка/Агрессия each assemble their own dedicated army from scratch when they need
    //      one, so an idle Recce solo sitting at base is just another lone army to this class.
    //   1b) FindAssemblingArmyFoldMove — 2026-08-21 follow-up, project owner's own report: tier 1
    //      above only ever matches EXACTLY one member, so a RaidWeakerArmy/DefendCitadel army that's
    //      grown past its first recruit but is still AiTask.StillAssembling (not yet IsReady) sat
    //      at the garrison hex completely untouched — no different from before the task-claim guard
    //      was loosened at all. This tier picks up everything tier 1 can't: any
    //      still-assembling task-claimed army with 2+ members, garrison-hex only, folding its
    //      weakest member in at a time for as long as the garrison HasRoom. Garrison-only
    //      destination, no "other field army" fallback the way tier 1 has — if the garrison has no
    //      room, the member is exactly as safe staying put in its own still-forming army as it
    //      would be homeless in some unrelated host, nothing gained by scattering it. Reads
    //      StillAssembling rather than recomputing readiness itself (see that field's own comment)
    //      — this class has no idea how any one task category scores its own composition target.
    //   2) FindHexBalanceMove — once no lone/still-assembling army is left to fold (or the garrison has no room and
    //      nothing else can absorb one) AND the garrison genuinely has no room, this ONE tier now
    //      covers both strength and composition (merged 2026-08-20, project owner's own follow-up —
    //      "нужно смотреть на все армии хекса и гарнизон", not just garrison-vs-one-field-army):
    //        - Strength — garrison vs the COMBINED power of every field army at this hex, leveled
    //          toward AiConfig.garrisonPowerShareTarget/garrisonPowerShareTolerance (default 70/30,
    //          project owner's own pick) — garrison's own strongest spare moves out to whichever
    //          field army with room currently needs it most (lowest average) when garrison is
    //          carrying more than its target share (the garrison ends up the bigger, not
    //          necessarily individually-stronger-per-unit, army over time just from capacity and
    //          hero accumulation — point 2.1.2). Exception (point 2.1.3): with only ONE hero
    //          anywhere on the roster and more non-hero units on this hex than even a hero-led
    //          garrison could ever hold, sort by TYPE instead of the usual "strongest out" — a
    //          plain long-range unit (Range==2, Defense<=2, Attack<=4) is cheap insurance sitting
    //          behind the base's own defense bonus, so it stays; a hard hitter (Defense>2,
    //          Attack>=4) is wasted sitting still, so it goes to a field army instead.
    //        - Composition — once strength has nothing left to correct, even out TYPE composition
    //          (melee vs ranged, ability-heavy vs plain) across every army at this hex, garrison
    //          included (point 3.2) — the exact same roster-shape criteria AiManagementPlanner.
    //          UnitCompositionFitBonus already reads off the player's whole stock for Unit-card
    //          placement (point 3.1), just applied army-to-army here instead of card-to-roster.
    //      Gated on AiConfig.minHexUnitsForBalancing (point 3 — "один, два, три юнита на хексе
    //      балансировать нечего") — splitting a handful of units across several armies just to hit
    //      a ratio produces several forces too small to survive anything, worse than one coherent
    //      garrison-led stack; below that floor everything just stays put no matter how skewed the
    //      raw ratio looks. Ordering (point 2): fill the garrison first (tiers 0-1), only once it's
    //      genuinely full does anything get pushed OUT to field armies (this tier) — never a
    //      proactive "keep the ratio right" correction while the garrison still has spare room.
    //
    // FindReorgSwap is a separate, LAST-RESORT entry point (not one of FindReorgMove's own tiers
    // above — see AiManagementPlanner.TryConsolidationCandidate, which only calls it once
    // FindReorgMove itself returns nothing this same call) for when the whole hex is genuinely
    // packed — garrison full AND every field army full too, so tier 2's own moves can never find a
    // free slot to land in no matter how skewed things are. ArmyActions.SwapMembers trades two
    // units 1-for-1 with no free slot needed at all (project owner's own 2026-08-20 call — "если
    // вообще нет места на хексе, то добавить функционал замены юнитов между армиями"), reading the
    // exact same strength/composition/2.1.3-exception signals tier 2 does, just executing a trade
    // instead of a move wherever tier 2 itself would have been stuck.
    //
    // FindGarrisonOverflow/FindGarrisonOverflowDestination are the mirror-image concern (garrison
    // itself over capacity, evicting outward) — kept here too since it's the same "капасити
    // гарнизона" half of this one task, just the opposite direction. Tried before every tier above,
    // each drain call (see AiTurnController.RunGarrisonReorgPhase) — overflow is a hard constraint
    // (the garrison is literally over its own capacity), never optional the way 0-2 above are.
    public static class GarrisonReorgTask
    {
        // ---- Капасити гарнизона ----

        // Evicts exactly the excess (Members.Count - Capacity), settling the garrison at LITERAL
        // full capacity, not one below (2026-08-20, project owner's own fix — the old formula
        // subtracted an extra "-1" and settled one short of full on purpose, which meant the
        // garrison could sit there indefinitely reading as `HasRoom == true` even with nothing
        // genuinely spare going on; GarrisonReorgTask.FindHexBalanceMove's own precondition is raw
        // `!HasRoom`, so that leftover slot silently kept the whole balance tier from ever
        // engaging, and worse, let a folded-back lone army re-trigger this same eviction next
        // call — an eviction/fold ping-pong that only ever stopped once AiTurnContext.
        // WouldRevisitArmy happened to block the specific unit involved, not because anything
        // actually settled). Re-evaluated fresh every turn, so a garrison that fills up again next
        // turn just proposes another split rather than needing to guess a batch size up front.
        // WHICH members leave is the strongest first (Defense, then Attack as the tiebreak) — a
        // tank or artillery piece is worth fielding, a bare rifleman is worth stockpiling instead,
        // since a garrisoned unit gets the base's own defense bonus the field doesn't (the project
        // owner's own call). Null if the garrison already has room or doesn't exist.
        public static IReadOnlyList<UnitData> FindGarrisonOverflow(ArmyData garrison)
        {
            if (garrison == null || garrison.HasRoom)
                return null;
            int overflow = garrison.Members.Count - garrison.Capacity;
            if (overflow <= 0)
                return null;
            return garrison.Members.OrderByDescending(m => m.Defense).ThenByDescending(m => m.Attack)
                .Take(overflow).ToList();
        }

        // Where FindGarrisonOverflow's own pick actually goes — a hero-led escort with room first
        // (folds straight in), else an ALREADY-EXISTING plain reserve army (see AiArmyRoles.
        // IsPlainReserveArmy — empty or already growing, either way). Null means there's nowhere
        // for it to go this turn — garrison stays over capacity for now rather than spawning a
        // brand new reserve army of its own to make room.
        //
        // Deliberately never spawns one itself — that used to matter for interleaving with
        // TryConsolidationCandidate's own lone-army-pairing tier when both ran inside Decide's own
        // per-step loop (a turn where garrison overflow AND a lone-army pairing were BOTH available
        // the same step could pick the overflow "spawn new" candidate first and spend AP creating a
        // fresh empty army, when letting the lone-army pairing go first would have freed an
        // EXISTING one to reuse instead — the project owner's own report). Moot now that split and
        // consolidate both run from the same AiTurnController.RunGarrisonReorgPhase, split always
        // first (see this class's own class comment) — there is exactly one place that ever decides
        // to spend AP on a brand new spare army — AiTurnController.Decide's own ReserveArmy fallback,
        // right next to DrawCard at the very end of the main per-step loop, which always runs
        // BEFORE RunGarrisonReorgPhase — so overflow eviction here always sees the fullest possible
        // picture of what that fallback (and every earlier reorg tier this same phase already ran)
        // already freed up. Overflow eviction just waits its turn when nothing existing has room;
        // the next time FindGarrisonOverflow proposes a split, that freshly-created (or
        // freshly-freed) reserve army shows up as `existingReserve` above.
        public readonly struct GarrisonOverflowDestination
        {
            public readonly ArmyData ExistingArmy;
            public GarrisonOverflowDestination(ArmyData existingArmy) => ExistingArmy = existingArmy;
        }

        public static GarrisonOverflowDestination? FindGarrisonOverflowDestination(PlayerSetupData player, HexCoord garrisonHex, UnitData strongestUnit)
        {
            ArmyData heroEscort = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                AiArmyRoles.IsHeroLedCombatArmy(a) && a.Hex.Equals(garrisonHex) && a.HasRoom);
            if (heroEscort != null && CanAffordTransferInto(heroEscort, strongestUnit))
                return new GarrisonOverflowDestination(heroEscort);

            ArmyData existingReserve = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => AiArmyRoles.IsPlainReserveArmy(a) && a.Hex.Equals(garrisonHex));
            if (existingReserve != null && CanAffordTransferInto(existingReserve, strongestUnit))
                return new GarrisonOverflowDestination(existingReserve);

            return null;
        }

        // Which garrison-benched hero (if any) should lead a BRAND NEW spillover army when
        // FindGarrisonOverflowDestination above found nowhere existing to send the overflow (see
        // AiManagementPlanner.SplitGarrisonArmyRoutine's own comment on why this only applies to
        // that fresh-army branch, never an existing destination) — the project owner's own spec:
        // "если армия создана потому что место в гарнизоне заканчивается, то у неё должен быть
        // герой", so a spillover army gets a leader pulled off the bench instead of staying a
        // leaderless stockpile forever. No hero-army cap gates this any more (CanSupportAnother
        // HeroArmy/MaxActiveHeroArmies removed 2026-08-19, project owner's own call). Highest
        // CommandRating first when more than one hero is benched — the project owner's own "на
        // первом месте герой с наибольшим капасити по картам": that hero can carry the biggest
        // escort, so it's the one most worth spending this one hero-army slot on.
        public static UnitData FindGarrisonHeroToPromote(PlayerSetupData player, ArmyData garrison)
        {
            if (garrison == null)
                return null;
            return garrison.Members.Where(m => m.IsHero).OrderByDescending(m => m.CommandRating).FirstOrDefault();
        }

        // ---- Передача юнитов между армиями в базе ----

        public readonly struct ConsolidationMove
        {
            public readonly ArmyData Source;
            public readonly UnitData Unit;
            public readonly ArmyData Target;
            public readonly string Reason;

            public ConsolidationMove(ArmyData source, UnitData unit, ArmyData target, string reason)
            {
                Source = source;
                Unit = unit;
                Target = target;
                Reason = reason;
            }
        }

        // See FindReorgSwap's own comment — a direct 1-for-1 trade (ArmyActions.SwapMembers) for
        // when neither side of an otherwise-warranted move has a free slot to receive anything.
        public readonly struct SwapMove
        {
            public readonly ArmyData ArmyA;
            public readonly UnitData UnitA;
            public readonly ArmyData ArmyB;
            public readonly UnitData UnitB;
            public readonly string Reason;

            public SwapMove(ArmyData armyA, UnitData unitA, ArmyData armyB, UnitData unitB, string reason)
            {
                ArmyA = armyA;
                UnitA = unitA;
                ArmyB = armyB;
                UnitB = unitB;
                Reason = reason;
            }
        }

        // "Одиночка" at the citadel hex — a plain lone unit OR a lone hero, no distinction any more
        // (2026-08-20, point 1/1.2 — the old Recce carve-out is gone; see this class's own class
        // comment for why). Scoped to `garrisonHex` only — an army out in the field mid-task is
        // never touched by this sweep. No longer excludes a task-claimed army (2026-08-21 fix,
        // superseded 2026-08-21 follow-up, project owner's own report) — the original worry was a
        // still-forming DefendCitadel/RaidWeakerArmy army getting folded straight back into the
        // garrison and "undoing" the recruitment its own planner just did. Turns out that's not
        // actually destructive: HexSelectionController.DeleteArmyIfEmptied already refuses to tear
        // down an army left empty on its own owner's Barracks hex (the garrison hex always IS one —
        // see that method's own comment), so the folded-empty task.Army SURVIVES as a registered
        // shell instead of being deleted — the task itself stays alive (TryContinueRaidTask/
        // TryStartDefenceCandidates's own dangling-reference check never trips), and the very next
        // evaluation just recruits into that same shell again, no CreateArmyApCost repaid. AiTurn
        // Controller.Decide's own upfront pool.ClaimArmy sweep (see AiResourcePool's own comment)
        // also already keeps this now-empty shell reserved for its own task, so no OTHER category
        // can mistake it for a fresh IsEmptyDeployableArmy in the meantime. Net effect: the unit
        // spends a step folded into the (better-defended, now that BattleInitiator.FindEnemyAt picks
        // the hex's strongest army) garrison instead of sitting exposed in a still-fragile 1-member
        // army, then gets pulled back out — a small extra transfer, not a lost turn.
        private static bool IsLoneArmyAtBase(ArmyData army, HexCoord garrisonHex)
        {
            return army != null && !army.IsGarrison && !army.IsPrison
                && army.Members.Count == 1 && army.Hex.Equals(garrisonHex);
        }

        // The ONE task-claimed shape Tier 2 (FindHexBalanceMove) and FindReorgSwap still leave
        // alone — a DefendCitadel task actually in its Turtle posture (2026-08-21, narrowed from
        // "any task-claimed army" per the project owner's own report — see IsLoneArmyAtBase's own
        // comment on why the broader exclusion turned out unnecessary elsewhere: those two tiers
        // never fully drain an army the way Tier 1's fold-back can, so there's no deletion/AP-
        // oscillation risk to guard against here). Turtle's whole point is concentrating power at
        // the garrison hex ABOVE the ordinary garrisonPowerShareTarget/garrisonPowerShareTolerance
        // ratio (see AiDefencePlanner's own class comment) — without this one exclusion, Tier 2's
        // strength-balance step would read that deliberate over-concentration as an imbalance and
        // donate the Turtle "кулак"'s own strongest units back out to other field armies, directly
        // undoing the posture's own purpose. Every OTHER task-claimed army (a RaidWeakerArmy still
        // assembling, DefendCitadel in Patrol/Active, RaidReinforce, ...) has no such standing
        // strategic intent to protect, so it's ordinary balancing material like anything else here.
        private static bool IsProtectedTurtleFist(PlayerSetupData player, ArmyData army)
        {
            AiTask task = AiTaskRegistry.TaskFor(player, army);
            return task != null && task.Kind == AiTaskKind.DefendCitadel && task.Posture == AiDefencePosture.Turtle;
        }

        // Average non-hero unit strength (Defense+Attack) — the yardstick tier 1's "which field
        // army needs this lone unit most" fallback compares by. Average, not total: a garrison-
        // adjacent army with many weak units shouldn't out-rank a small army of two tanks just by
        // headcount. float.MinValue for an army with no non-hero members at all (a bare hero, or a
        // freshly spawned empty reserve) — treated as maximally in NEED of reinforcement.
        private static float AverageNonHeroStrength(ArmyData army)
        {
            List<UnitData> nonHero = army.Members.Where(m => !m.IsHero).ToList();
            return nonHero.Count == 0 ? float.MinValue : (float)nonHero.Average(m => m.Defense + m.Attack);
        }

        // Total non-hero power (Defense+Attack summed, not averaged) — tier 2's own yardstick for
        // "how big a slice of the base's combined strength does this army hold", since a bigger
        // garrison legitimately outweighing a small field army by headcount alone is exactly the
        // expected, not the imbalance being corrected (see this class's own class comment, point
        // 2.1.2).
        private static float TotalNonHeroPower(ArmyData army)
        {
            return (float)army.Members.Where(m => !m.IsHero).Sum(m => m.Defense + m.Attack);
        }

        // Tier 0 — see this class's own class comment for the full chain this sets up. Only
        // fires when the garrison is genuinely full (no raw room at all) AND heroless — a
        // hero-led garrison has already settled this, and a garrison with any room just takes the
        // hero the ordinary way (tier 1) without needing this maneuver first.
        private static ConsolidationMove? FindHeroCapacityExpansionMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            if (garrison == null || garrison.HasRoom || garrison.Members.Any(m => m.IsHero))
                return null;

            ArmyData loneHeroArmy = ArmyRegistry.AllForOwner(player)
                .Where(a => IsLoneArmyAtBase(a, garrisonHex) && a.Members[0].IsHero)
                .OrderByDescending(a => a.Members[0].CommandRating)
                .FirstOrDefault();
            if (loneHeroArmy == null)
                return null;

            UnitData hero = loneHeroArmy.Members[0];
            var withHero = new List<UnitData>(garrison.Members) { hero };
            if (ArmyData.ComputeCapacity(withHero, true) <= garrison.Members.Count)
                return null; // this hero wouldn't actually buy the garrison any room — not worth lending anything over

            UnitData weakest = garrison.Members.Where(m => !m.IsHero)
                .OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
            if (weakest == null || !loneHeroArmy.HasRoom || !CanAffordTransferInto(loneHeroArmy, weakest)
                || ctx.WouldRevisitArmy(weakest, loneHeroArmy))
                return null;

            return new ConsolidationMove(garrison, weakest, loneHeroArmy,
                $"lending {weakest.Name} to \"{loneHeroArmy.Name}\" so {hero.Name} can take over the garrison next");
        }

        // Tier 1 — see this class's own class comment. Weakest lone army first (Defense, then
        // Attack as the tiebreak) — picking whichever lone army happened to enumerate first would
        // risk folding a strong stray back into garrison while a genuinely weak one stays outside,
        // exactly backwards from this whole class's own point.
        private static ConsolidationMove? FindLoneArmyFoldMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            List<ArmyData> loneArmies = ArmyRegistry.AllForOwner(player).Where(a => IsLoneArmyAtBase(a, garrisonHex))
                .OrderBy(a => a.Members[0].Defense).ThenBy(a => a.Members[0].Attack)
                .ToList();

            foreach (ArmyData lone in loneArmies)
            {
                UnitData unit = lone.Members[0];

                // Raw HasRoom, NOT AiManagementPlanner.HasGarrisonDepositRoom's buffered check
                // (2026-08-20, project owner's own call) — that reserved slot exists for ordinary
                // CARD deposits, so a fresh Unit/Hero always has somewhere to land; it was never
                // meant to leave an exposed solo unit or hero standing outside a garrison that
                // still has one real slot open. A lone army is exactly the vulnerable case this
                // whole tier exists to fix, so it takes priority over keeping that slot in reserve.
                if (garrison != null && garrison.HasRoom
                    && CanAffordTransferInto(garrison, unit) && !ctx.WouldRevisitArmy(unit, garrison))
                    return new ConsolidationMove(lone, unit, garrison, $"{unit.Name} — a lone unit, folding into the garrison");

                // Garrison had no headroom (or doesn't exist) — falls into whichever OTHER field
                // army at base has room instead, preferring the one currently weakest on average
                // (needs it most), rather than leaving this unit alone all turn (project owner's
                // own "в гарнизон или другую армию" spec, point 1). Members.Count > 0 is required
                // (2026-08-20, project owner's own fix) — an EMPTY host has room too, but landing a
                // lone unit in an empty army just produces a DIFFERENT lone army, not a real
                // consolidation ("надо выполнить проверку до переезда одиночки в пустую армию, так
                // как результат одинаковый"); only an army that already has a real member makes
                // this unit any less exposed than it already was.
                ArmyData host = ArmyRegistry.AllForOwner(player)
                    .Where(a => a != lone && !a.IsGarrison && !a.IsPrison && a.Hex.Equals(garrisonHex)
                        && a.HasRoom && a.Members.Count > 0 && AiTaskRegistry.TaskFor(player, a) == null)
                    .OrderBy(AverageNonHeroStrength)
                    .FirstOrDefault(a => CanAffordTransferInto(a, unit) && !ctx.WouldRevisitArmy(unit, a));
                if (host != null)
                    return new ConsolidationMove(lone, unit, host,
                        $"{unit.Name} — a lone unit, merging into \"{host.Name}\" (garrison had no room)");
            }
            return null;
        }

        // Tier 1b — see this class's own class comment. Every member of every still-assembling
        // task-claimed army at the garrison hex, weakest unit first across the whole combined pool
        // (Defense, then Attack) — same "don't strip a strong stray while a weak one waits" rule
        // tier 1 already follows, just spanning several source armies' own rosters now instead of
        // one candidate per army. Garrison-only; returns null outright once the garrison itself has
        // no room rather than falling back to some other host the way tier 1 does — see this
        // method's own doc bullet in the class comment for why that fallback doesn't apply here.
        private static ConsolidationMove? FindAssemblingArmyFoldMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            if (garrison == null || !garrison.HasRoom)
                return null;

            // Non-hero members only (2026-08-21 fix, own re-simulation finding) — sorting the raw
            // candidate pool by Defense/Attack alone reads a hero as "weakest" (their value is
            // CommandRating/leadership, not combat stats), so an unfiltered sort would preferentially
            // evict the exact hero anchoring this still-forming RaidWeakerArmy/DefendCitadel escort —
            // backwards from the whole point (a raid/defense force needs its hero, see
            // AiArmyRoles.IsHeroLedCombatArmy's own comment). Same `!m.IsHero` convention every other
            // tier here already follows for ordinary rank-and-file shuffling (TotalNonHeroPower/
            // AverageNonHeroStrength) — a lone bare hero with nothing else yet is a different case,
            // already handled by tier 1 (IsLoneArmyAtBase's own "no distinction" is fine there since
            // there's no escort structure yet to protect).
            var candidates = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !a.IsGarrison && !a.IsPrison && a.Hex.Equals(garrisonHex) && a.Members.Count > 1
                    && AiTaskRegistry.TaskFor(player, a)?.StillAssembling == true)
                .SelectMany(a => a.Members.Where(m => !m.IsHero).Select(unit => (Army: a, Unit: unit)))
                .OrderBy(c => c.Unit.Defense).ThenBy(c => c.Unit.Attack);

            foreach ((ArmyData army, UnitData unit) in candidates)
            {
                if (CanAffordTransferInto(garrison, unit) && !ctx.WouldRevisitArmy(unit, garrison))
                    return new ConsolidationMove(army, unit, garrison,
                        $"{unit.Name} — \"{army.Name}\" still assembling, folding a member into the garrison meanwhile");
            }
            return null;
        }

        // Tier 2 — see this class's own class comment, points 2/2.1/2.1.2/2.1.3/3/3.1/3.2. Merged
        // strength+composition balance across garrison + EVERY field army at this hex (2026-08-20,
        // project owner's own follow-up — a single field-army comparison missed multi-army hexes).
        // Only ever proposes ONE move per call, same "re-evaluate fresh" rule as every tier here —
        // a hex with several imbalances just gets to the next one on a later drain call.
        private static ConsolidationMove? FindHexBalanceMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            if (garrison == null || garrison.HasRoom)
                return null; // fill the garrison first (tiers 0-1) — point 2's own ordering; nothing gets pushed OUT until it's genuinely full

            // Excludes only a Turtle-postured DefendCitadel "кулак" (2026-08-21 fix, simulation
            // report finding, NARROWED 2026-08-21 follow-up, project owner's own call — see
            // IsProtectedTurtleFist's own comment for why every OTHER task-claimed army is fair
            // game here now) — without this one exclusion, a Turtle "кулак" deliberately
            // concentrating power above the ordinary 70/30 garrison/field ratio would just get
            // rebalanced back down by this same method, working directly against the whole point
            // of Оборона's Turtle posture.
            List<ArmyData> fieldArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && a.Hex.Equals(garrisonHex) && a.Members.Count > 0
                    && !IsProtectedTurtleFist(player, a))
                .ToList();
            if (fieldArmies.Count == 0)
                return null;

            int totalNonHero = garrison.Members.Count(m => !m.IsHero) + fieldArmies.Sum(a => a.Members.Count(m => !m.IsHero));
            if (totalNonHero <= AiConfig.minHexUnitsForBalancing)
                return null; // too few units on this hex to split up — point 3: a handful of 1-unit armies survives nothing

            int heroCount = ArmyRegistry.AllForOwner(player).SelectMany(a => a.Members).Count(m => m.IsHero);

            // Exception 2.1.3 — a single hero on the whole roster with more non-hero units on this
            // hex than even a hero-led garrison could ever hold: sort by TYPE instead of the usual
            // strongest-out rule. A plain long-range unit is cheap insurance sitting behind the
            // base's own defense bonus, so it stays; a hard hitter is wasted standing still, so it
            // goes to a field army instead.
            if (heroCount == 1 && totalNonHero > garrison.Capacity)
            {
                UnitData strongInGarrison = garrison.Members.Where(m => !m.IsHero && m.Defense > 2 && m.Attack >= 4)
                    .OrderByDescending(m => m.Attack).ThenByDescending(m => m.Defense).FirstOrDefault();
                if (strongInGarrison != null)
                {
                    ArmyData recipient = fieldArmies.Where(a => a.HasRoom).OrderBy(AverageNonHeroStrength)
                        .FirstOrDefault(a => CanAffordTransferInto(a, strongInGarrison) && !ctx.WouldRevisitArmy(strongInGarrison, a));
                    if (recipient != null)
                        return new ConsolidationMove(garrison, strongInGarrison, recipient,
                            $"{strongInGarrison.Name} is too strong to leave garrisoned — moving to \"{recipient.Name}\"");
                }

                // The mirror move (a weak field unit moving INTO the garrison) is deliberately NOT
                // attempted as a plain move here any more (2026-08-20 — it used to check
                // garrison.HasRoom, which this method's own top guard has already guaranteed false
                // by this point, so it could never actually fire): FindReorgSwap below pairs this
                // exact unit with strongInGarrison as a direct trade instead, which is the only way
                // this move can ever really happen once the garrison has no free slot to receive
                // anything.
                return null;
            }

            // Strength — level garrison against the COMBINED field-army pool by total non-hero
            // power toward the target share (project owner's own 70/30 pick, point 2.1.1/2.1.2).
            // The garrison naturally ends up the bigger pile over time just from capacity and hero
            // accumulation; this only steps in once its share of the COMBINED power gets
            // meaningfully ahead of target.
            float garrisonPower = TotalNonHeroPower(garrison);
            float armiesPower = fieldArmies.Sum(TotalNonHeroPower);
            float combinedPower = garrisonPower + armiesPower;
            if (combinedPower > 0f && garrisonPower / combinedPower - AiConfig.garrisonPowerShareTarget > AiConfig.garrisonPowerShareTolerance)
            {
                UnitData strongest = garrison.Members.Where(m => !m.IsHero)
                    .OrderByDescending(m => m.Defense).ThenByDescending(m => m.Attack).FirstOrDefault();
                if (strongest != null)
                {
                    ArmyData recipient = fieldArmies.Where(a => a.HasRoom).OrderBy(AverageNonHeroStrength)
                        .FirstOrDefault(a => CanAffordTransferInto(a, strongest) && !ctx.WouldRevisitArmy(strongest, a));
                    if (recipient != null)
                        return new ConsolidationMove(garrison, strongest, recipient,
                            $"{strongest.Name} — garrison over its {AiConfig.garrisonPowerShareTarget:P0} power share, donating to \"{recipient.Name}\"");
                }
            }

            // Composition — once strength has nothing left to correct, even out TYPE composition
            // (melee vs ranged, ability-heavy vs plain) across garrison + every field army at this
            // hex (point 3.2) — the exact same roster-shape criteria AiManagementPlanner.
            // UnitCompositionFitBonus already reads off the player's whole stock for Unit-card
            // placement (point 3.1), just applied army-to-army here instead of card-to-roster.
            var allArmies = new List<ArmyData> { garrison };
            allArmies.AddRange(fieldArmies);
            foreach (ArmyData recipient in allArmies)
            {
                if (!recipient.HasRoom)
                    continue;

                List<UnitData> recipientUnits = recipient.Members.Where(m => !m.IsHero).ToList();
                int melee = recipientUnits.Count(m => m.Range <= 1);
                int ranged = recipientUnits.Count(m => m.Range > 1);
                int abilityHeavy = recipientUnits.Count(m => m.Abilities.Count > 0);
                int simple = recipientUnits.Count(m => m.Abilities.Count == 0);

                Func<UnitData, bool> needs;
                if (melee > ranged)
                    needs = m => m.Range > 1;
                else if (ranged > melee)
                    needs = m => m.Range <= 1;
                else if (abilityHeavy > simple)
                    needs = m => m.Abilities.Count == 0;
                else
                    continue;

                foreach (ArmyData donor in allArmies)
                {
                    if (donor == recipient || donor.Members.Count(m => !m.IsHero) <= 1)
                        continue;
                    UnitData spare = donor.Members.Where(m => !m.IsHero && needs(m))
                        .OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
                    if (spare == null || !CanAffordTransferInto(recipient, spare) || ctx.WouldRevisitArmy(spare, recipient))
                        continue;
                    return new ConsolidationMove(donor, spare, recipient,
                        $"{spare.Name} — evening out \"{recipient.Name}\"'s composition from \"{donor.Name}\"");
                }
            }
            return null;
        }

        // Last resort, tried only once FindReorgMove itself finds nothing this call (see
        // AiManagementPlanner.TryConsolidationCandidate) — every move FindHexBalanceMove proposes
        // needs a free slot in its destination, so once the garrison AND every field army at this
        // hex are all genuinely full, nothing above can ever fire again, no matter how skewed
        // things are (2026-08-20, project owner's own call — "если вообще нет места на хексе, то
        // добавить функционал замены юнитов между армиями"). ArmyActions.SwapMembers trades two
        // units 1-for-1 with no free slot needed at all, since a straight swap never changes
        // either army's headcount. Re-derives the same signals FindHexBalanceMove does (garrison
        // full, past the floor, hero count) rather than sharing state with it — the two are only
        // ever both consulted the SAME call when the first came up empty, so there's nothing to
        // keep in sync between them.
        public static SwapMove? FindReorgSwap(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            if (garrison == null || garrison.HasRoom)
                return null;

            // Excludes only a Turtle-postured DefendCitadel "кулак" — see IsProtectedTurtleFist's
            // own comment (same narrowed 2026-08-21 exclusion FindHexBalanceMove uses).
            List<ArmyData> fieldArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && a.Hex.Equals(garrisonHex) && a.Members.Count > 0
                    && !IsProtectedTurtleFist(player, a))
                .ToList();
            if (fieldArmies.Count == 0)
                return null;

            int totalNonHero = garrison.Members.Count(m => !m.IsHero) + fieldArmies.Sum(a => a.Members.Count(m => !m.IsHero));
            if (totalNonHero <= AiConfig.minHexUnitsForBalancing)
                return null;

            int heroCount = ArmyRegistry.AllForOwner(player).SelectMany(a => a.Members).Count(m => m.IsHero);

            // Exception 2.1.3's own pairing — strongInGarrison and weakInField are already exactly
            // what each other's army needs, so trade them directly instead of leaving either move
            // stranded for lack of a free slot (see FindHexBalanceMove's own comment on why the
            // plain "weak unit into garrison" move can never fire on its own here).
            if (heroCount == 1 && totalNonHero > garrison.Capacity)
            {
                UnitData strongInGarrison = garrison.Members.Where(m => !m.IsHero && m.Defense > 2 && m.Attack >= 4)
                    .OrderByDescending(m => m.Attack).ThenByDescending(m => m.Defense).FirstOrDefault();
                var weakInField = fieldArmies.SelectMany(a => a.Members.Select(m => (Army: a, Unit: m)))
                    .Where(x => !x.Unit.IsHero && x.Unit.Range == 2 && x.Unit.Defense <= 2 && x.Unit.Attack <= 4)
                    .OrderBy(x => x.Unit.Attack).ThenBy(x => x.Unit.Defense)
                    .FirstOrDefault();
                if (strongInGarrison != null && weakInField.Unit != null
                    && CanAffordTransferInto(weakInField.Army, strongInGarrison) && CanAffordTransferInto(garrison, weakInField.Unit)
                    && !ctx.WouldRevisitArmy(strongInGarrison, weakInField.Army) && !ctx.WouldRevisitArmy(weakInField.Unit, garrison))
                    return new SwapMove(garrison, strongInGarrison, weakInField.Army, weakInField.Unit,
                        $"swapping {strongInGarrison.Name} for {weakInField.Unit.Name} with \"{weakInField.Army.Name}\" — no free slot to just move into");
                return null;
            }

            // Default — the same target share FindHexBalanceMove's own strength step reads, traded
            // instead of moved since nobody has room.
            float garrisonPower = TotalNonHeroPower(garrison);
            float armiesPower = fieldArmies.Sum(TotalNonHeroPower);
            float combinedPower = garrisonPower + armiesPower;
            if (combinedPower > 0f && garrisonPower / combinedPower - AiConfig.garrisonPowerShareTarget > AiConfig.garrisonPowerShareTolerance)
            {
                UnitData strongest = garrison.Members.Where(m => !m.IsHero)
                    .OrderByDescending(m => m.Defense).ThenByDescending(m => m.Attack).FirstOrDefault();
                if (strongest != null)
                {
                    var partner = fieldArmies.OrderBy(AverageNonHeroStrength)
                        .SelectMany(a => a.Members.Where(m => !m.IsHero).OrderBy(m => m.Defense).ThenBy(m => m.Attack)
                            .Take(1).Select(m => (Army: a, Unit: m)))
                        .FirstOrDefault();
                    if (partner.Unit != null && CanAffordTransferInto(partner.Army, strongest) && CanAffordTransferInto(garrison, partner.Unit)
                        && !ctx.WouldRevisitArmy(strongest, partner.Army) && !ctx.WouldRevisitArmy(partner.Unit, garrison))
                        return new SwapMove(garrison, strongest, partner.Army, partner.Unit,
                            $"swapping {strongest.Name} for {partner.Unit.Name} with \"{partner.Army.Name}\" — over its "
                                + $"{AiConfig.garrisonPowerShareTarget:P0} power share, no free slot to just move into");
                }
            }

            // Composition — same roster-shape gap FindHexBalanceMove's own composition step reads,
            // traded instead of moved since nobody has room. `giveUp` is the recipient's own
            // excess-type spare — the thing it trades AWAY for the donor's needed-type unit.
            var allArmies = new List<ArmyData> { garrison };
            allArmies.AddRange(fieldArmies);
            foreach (ArmyData recipient in allArmies)
            {
                List<UnitData> recipientUnits = recipient.Members.Where(m => !m.IsHero).ToList();
                if (recipientUnits.Count == 0)
                    continue;
                int melee = recipientUnits.Count(m => m.Range <= 1);
                int ranged = recipientUnits.Count(m => m.Range > 1);
                int abilityHeavy = recipientUnits.Count(m => m.Abilities.Count > 0);
                int simple = recipientUnits.Count(m => m.Abilities.Count == 0);

                Func<UnitData, bool> needs;
                Func<UnitData, bool> excess;
                if (melee > ranged) { needs = m => m.Range > 1; excess = m => m.Range <= 1; }
                else if (ranged > melee) { needs = m => m.Range <= 1; excess = m => m.Range > 1; }
                else if (abilityHeavy > simple) { needs = m => m.Abilities.Count == 0; excess = m => m.Abilities.Count > 0; }
                else continue;

                UnitData giveUp = recipientUnits.Where(excess).OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
                if (giveUp == null)
                    continue;

                foreach (ArmyData donor in allArmies)
                {
                    if (donor == recipient)
                        continue;
                    UnitData spare = donor.Members.Where(m => !m.IsHero && needs(m))
                        .OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
                    if (spare == null || !CanAffordTransferInto(recipient, spare) || !CanAffordTransferInto(donor, giveUp)
                        || ctx.WouldRevisitArmy(spare, recipient) || ctx.WouldRevisitArmy(giveUp, donor))
                        continue;
                    return new SwapMove(recipient, giveUp, donor, spare,
                        $"swapping {giveUp.Name} for {spare.Name} with \"{donor.Name}\" — evening out \"{recipient.Name}\"'s "
                            + "composition, no free slot to just move into");
                }
            }
            return null;
        }

        // See this class's own "Поведение" comment for the full tier order.
        //
        // `ctx` carries this AI turn's own oscillation guard (AiTurnContext.WouldRevisitArmy,
        // shared across categories — see that method's own comment) — every tier above already
        // checks it before returning a move, so no tier here ever proposes sending a unit back to
        // an army it already sat in earlier THIS SAME turn. A blocked/empty tier simply falls
        // through to the next.
        public static ConsolidationMove? FindReorgMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            return FindHeroCapacityExpansionMove(player, garrisonHex, garrison, ctx)
                ?? FindLoneArmyFoldMove(player, garrisonHex, garrison, ctx)
                ?? FindAssemblingArmyFoldMove(player, garrisonHex, garrison, ctx)
                ?? FindHexBalanceMove(player, garrisonHex, garrison, ctx);
        }

        // Pre-checks the exact same AP-affordability ArmyActions.TransferMember will itself
        // enforce, so an unaffordable move is never proposed as a candidate in the first place
        // (same "checked before proposing" rule AiManagementPlanner.FindPlacement follows).
        private static bool CanAffordTransferInto(ArmyData target, UnitData unit)
        {
            if (!target.HasActivatedThisTurn)
                return true;
            PlayerRoot targetRoot = PlayerRootRegistry.FindFor(target.Owner);
            return targetRoot != null && targetRoot.CanSpendActionPoints(unit.ActivationApCost);
        }
    }
}
