using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
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
    // Поведение — 2026-08-23 redesign, project owner's own spec: three EXPLICIT, separately-named
    // regimes instead of one generic drain, tried in this fixed priority order every drain
    // iteration (see AiTurnController.RunGarrisonReorgPhase, which recomputes all three fresh each
    // iteration — nothing here is cached or batched across iterations):
    //
    //   CollapseTemporaryAssembly → HandleOverflow (unconditional) → IdleBalance
    //
    // CollapseTemporaryAssembly (FindCollapseMove) — narrowed 2026-08-23 (project owner's own
    // report/spec, after a real log showed the original blanket StillAssembling trigger erasing
    // healthy multi-turn recruiting progress every single end of turn — see IsCollapseEligible's
    // own comment for the full reasoning). No longer "any still-recruiting RaidWeakerArmy/
    // DefendCitadel army" — RaidWeakerArmy is excluded outright (a forming raid at home has nothing
    // to fold away FROM; a real siege already gets a stronger, dedicated response via
    // AiDefencePlanner.IsUnderSiege), and DefendCitadel only qualifies in its Active posture, and
    // only once AiTask.AssemblyProgressTurn confirms the roster genuinely didn't grow THIS turn —
    // Patrol has no elevated standing to protect (ordinary IdleBalance already owns it) and Turtle
    // (a real siege in progress) is exactly the case where an incomplete extra stack still has
    // defensive value, never a candidate for folding away just because the turn ran out. What DOES
    // still qualify returns to the garrison as ONE atomic move: either its whole roster (hero
    // included) fits and moves this call, or nothing moves at all — see FindCollapseMove's own
    // comment for why a partial per-unit trickle (the OLD tier 1b's own shape) defeats the point.
    // Task/TargetHex/StillAssembling are untouched — this is a safety fold, not progress on the
    // task — so the very next assemble step just recruits back out of garrison stock, hero included
    // if the shell went fully empty (RaidWeakerArmyTask.FindRecruitAt's own NeedsHero check). Tried
    // FIRST every iteration; when it can't act (garrison doesn't have literal room for the WHOLE
    // roster yet), it simply declines and HandleOverflow/IdleBalance get this same iteration
    // instead — see the safety-case note on FindCollapseMove itself for how a full-but-not-
    // overflowing garrison still eventually frees enough room via IdleBalance's own strength-
    // balance step.
    //
    // HandleOverflow (FindGarrisonOverflow/FindGarrisonOverflowDestination) — the mirror-image
    // capacity concern (garrison itself OVER capacity, evicting outward). Tried second, and
    // UNCONDITIONALLY — a hard constraint (the garrison is literally over its own capacity), never
    // optional the way Collapse/IdleBalance are, so it always gets a look regardless of what
    // Collapse just did or declined to do.
    //
    // IdleBalance (FindReorgMove/FindReorgSwap) — everything else: tiers 0-2 below, deliberately
    // cut down to genuinely idle/lone/disbalanced material only (2026-08-23 — see
    // IsProtectedTaskArmy's own comment for the exclusion this whole regime now respects: it has NO
    // authority over a task-owned Raid or Active/Turtle-Defence army's composition, assembling or
    // ready alike — that belongs exclusively to AiAggressionPlanner /
    // AiDefencePlanner.TryStrengthenCandidate). Tried last, only once Collapse/Overflow both come
    // up empty this same iteration:
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
    //      armies don't disappear at a barracks hex, see IsLoneArmyAtBase's own comment).
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
    //      IsProtectedTaskArmy-excluded (2026-08-23) — a lone hero anchoring a protected Raid/
    //      Active/Turtle-Defence army stays untouched here regardless of whether Collapse itself
    //      would also claim it (Collapse's own eligibility narrowed 2026-08-23 too — see
    //      IsCollapseEligible's own comment — it no longer claims Raid at all, or Turtle, or a
    //      still-progressing Active assembly), so this exclusion has to stand on its own rather
    //      than assume Collapse already handled every StillAssembling case upstream.
    //   2) FindHexBalanceMove — once no lone army is left to fold (or the garrison has no room and
    //      nothing else can absorb one) AND the garrison genuinely has no room, this ONE tier now
    //      covers both strength and composition (merged 2026-08-20, project owner's own follow-up —
    //      "нужно смотреть на все армии хекса и гарнизон", not just garrison-vs-one-field-army):
    //        - Strength — garrison vs the COMBINED power of every eligible field army at this hex,
    //          leveled toward AiConfig.garrisonPowerShareTarget/garrisonPowerShareTolerance (default
    //          70/10, project owner's own pick) — garrison's own strongest spare moves out to
    //          whichever field army with room currently needs it most (lowest average) when
    //          garrison is carrying more than its target share (the garrison ends up the bigger, not
    //          necessarily individually-stronger-per-unit, army over time just from capacity and
    //          hero accumulation — point 2.1.2). Exception (point 2.1.3): with only ONE hero
    //          anywhere on the roster and more non-hero units on this hex than even a hero-led
    //          garrison could ever hold, sort by TYPE instead of the usual "strongest out" — a
    //          plain long-range unit (Range==2, Defense<=2, Attack<=4) is cheap insurance sitting
    //          behind the base's own defense bonus, so it stays; a hard hitter (Defense>2,
    //          Attack>=4) is wasted sitting still, so it goes to a field army instead.
    //        - Composition — once strength has nothing left to correct, even out TYPE composition
    //          (melee vs ranged, ability-heavy vs plain) across every eligible army at this hex,
    //          garrison included (point 3.2) — the exact same roster-shape criteria
    //          AiManagementPlanner.UnitCompositionFitBonus already reads off the player's whole
    //          stock for Unit-card placement (point 3.1), just applied army-to-army here instead of
    //          card-to-roster. Gated on AiConfig.compositionImbalanceThreshold (2026-08-23, an
    //          initial tuning value, not yet checked against a real playtest log — a raw 1-count gap
    //          used to fire this every single drain call).
    //      "Eligible" everywhere above means IsProtectedTaskArmy-excluded (2026-08-23) — a Raid or
    //      Active/Turtle-Defence army, assembling or ready, never enters this tier's own pool as
    //      either donor or recipient. Gated on AiConfig.minHexUnitsForBalancing (point 3 — "один,
    //      два, три юнита на хексе балансировать нечего") — splitting a handful of units across
    //      several armies just to hit a ratio produces several forces too small to survive anything,
    //      worse than one coherent garrison-led stack; below that floor everything just stays put no
    //      matter how skewed the raw ratio looks. Ordering (point 2): fill the garrison first
    //      (tiers 0-1), only once it's genuinely full does anything get pushed OUT to field armies
    //      (this tier) — never a proactive "keep the ratio right" correction while the garrison
    //      still has spare room.
    //
    // FindReorgSwap is a separate, LAST-RESORT entry point (not one of FindReorgMove's own tiers
    // above — see AiManagementPlanner.TryConsolidationCandidate, which only calls it once
    // FindReorgMove itself returns nothing this same call) for when the whole hex is genuinely
    // packed — garrison full AND every eligible field army full too, so tier 2's own moves can never
    // find a free slot to land in no matter how skewed things are. ArmyActions.SwapMembers trades
    // two units 1-for-1 with no free slot needed at all (project owner's own 2026-08-20 call — "если
    // вообще нет места на хексе, то добавить функционал замены юнитов между армиями"), reading the
    // exact same strength/composition/2.1.3-exception/IsProtectedTaskArmy signals tier 2 does, just
    // executing a trade instead of a move wherever tier 2 itself would have been stuck.
    //
    // FindGarrisonOverflow/FindGarrisonOverflowDestination are HandleOverflow's own two halves —
    // kept here too since it's the same "капасити гарнизона" concern this whole task owns, just the
    // opposite direction. See the priority-order note above for where it sits relative to Collapse/
    // IdleBalance.
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
        // owner's own call). Null if the garrison already has room or doesn't exist; can also come
        // back EMPTY (not null) once the CanLeaveWithoutOvercrowding filter below removes every
        // over-capacity member from contention — same "decline, let a later drain call retry"
        // handling TryGarrisonSplitCandidate already gives an empty list.
        // Candidate-feasibility fix (2026-08-23, real log: Economy kept re-proposing
        // SplitGarrisonArmy on a hero whose OWN CommandRating was what kept the garrison's Capacity
        // above its member count in the first place — removing him collapses Capacity back to
        // GarrisonBaseCapacity, which ArmyData.CanLeaveWithoutOvercrowding already exists to catch
        // (ArmyActions.TransferMember already enforces it, see that method's own guard), but this
        // method never consulted it before picking candidates. Filtered out BEFORE the strongest-
        // first ordering below, not after — a hero excluded this way is invisible to `.Take`, so a
        // weaker but actually-removable unit gets picked in his place instead of the batch just
        // coming up short. Safe to check each member independently against the CURRENT roster
        // (rather than simulating sequential removal the way FindCollapseMove's own AP batch-check
        // has to): Capacity only ever depends on whether the retained roster still has a hero in
        // it, never on the plain-unit headcount, so evicting any number of non-hero members changes
        // nothing about whether a DIFFERENT member could also leave.
        public static IReadOnlyList<UnitData> FindGarrisonOverflow(ArmyData garrison)
        {
            if (garrison == null || garrison.HasRoom)
                return null;
            int overflow = garrison.Members.Count - garrison.Capacity;
            if (overflow <= 0)
                return null;
            return garrison.Members.Where(garrison.CanLeaveWithoutOvercrowding)
                .OrderByDescending(m => m.Defense).ThenByDescending(m => m.Attack)
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
        // never touched by this sweep. Does NOT itself exclude a task-claimed army (2026-08-21 fix,
        // superseded 2026-08-21 follow-up) — that exclusion lives one level up, in
        // IsProtectedTaskArmy, which every caller of this predicate also checks separately before
        // treating an army as fair fold/balance material. Historically this comment argued folding a
        // still-forming task army back into garrison was harmless either way, on the theory that
        // HexSelectionController.DeleteArmyIfEmptied refuses to tear down an empty shell sitting on
        // its own owner's Barracks hex, so the folded-empty task.Army would simply SURVIVE as a
        // registered shell for next turn's recruit to reuse. That stopped being true once
        // AiTurnController.RunEmptyArmyCleanup grew its own stale-task sweep (2026-08-24, see that
        // method's own comment): it now reads ANY zero-member army still carrying a task — including
        // one JUST emptied by this exact fold, not only a genuinely combat-wiped one — as an orphan
        // and deletes the task outright, losing TargetHex/StillAssembling progress instead of
        // preserving it (project owner's own report — this was the actual root cause of Defence
        // recruit/fold/delete/re-recruit thrashing on Patrol-postured armies). IsProtectedTaskArmy's
        // own StillAssembling branch is what actually prevents this now, by refusing to let this
        // tier's fold move even consider a still-forming DefendCitadel army in the first place,
        // regardless of posture — see that method's own comment for the full chain.
        private static bool IsLoneArmyAtBase(ArmyData army, HexCoord garrisonHex)
        {
            return army != null && !army.IsGarrison && !army.IsPrison
                && army.Members.Count == 1 && army.Hex.Equals(garrisonHex);
        }

        // IdleBalance's own no-go list (2026-08-23 redesign, project owner's own spec point 3;
        // narrowed again 2026-08-24, project owner's own report — see the StillAssembling branch
        // below): a task-owned RaidWeakerArmy army, a DefendCitadel army in Active or Turtle
        // posture, OR a DefendCitadel army of ANY posture still mid-recruit is off-limits to every
        // generic Reorg move/swap here — tier 0's hero-capacity-expansion source, tier 1's
        // lone-army fold, tier 2's strength/composition balance, and FindReorgSwap all skip it.
        // Composition ownership for these belongs exclusively to the task's own planner
        // (AiAggressionPlanner for Raid, AiDefencePlanner.TryStrengthenCandidate/
        // TryStartDefenceCandidates for Defence) — "не пускать generic balancing внутрь активных
        // специализированных задач". A READY (non-assembling) Patrol army is deliberately still NOT
        // protected (project owner's own call, 2026-08-23) — once recruiting is done it has no
        // standing strategic intent above the ordinary ratio (unlike Turtle), so it goes back to
        // being ordinary balancing material like any task-less army. RaidReinforce/BuildBase (the
        // other two Aggression-category Kinds) are likewise NOT protected — only RaidWeakerArmy
        // itself was ever in scope of the project owner's "Raid" wording here.
        private static bool IsProtectedTaskArmy(PlayerSetupData player, ArmyData army)
        {
            AiTask task = AiTaskRegistry.TaskFor(player, army);
            if (task == null)
                return false;
            // SecureBase's own courier (2026-08-24) — same reasoning as RaidWeakerArmy below: a
            // courier sitting at its destination base hex, cargo not yet handed over, is a lone
            // army "at base" by IsLoneArmyAtBase's own reading and would otherwise be a legal
            // ordinary reorg fold target — that would silently swallow SecureBase's own bookkeeping
            // (task.Army) without actually delivering the cargo into the RIGHT army if it landed on
            // some other field army at the hex instead of the garrison itself.
            if (task.Kind == AiTaskKind.RaidWeakerArmy || task.Kind == AiTaskKind.SecureBase)
                return true;
            if (task.Kind != AiTaskKind.DefendCitadel)
                return false;
            // 2026-08-24 fix (project owner's own root-cause report): a Patrol-postured DefendCitadel
            // task still mid-recruit (StillAssembling) used to fall through to the plain posture
            // check below and read as unprotected — tier 1's FindLoneArmyFoldMove then happily
            // folded its single recruit straight back into the garrison as an ordinary lone unit.
            // That leaves the task's own `.Army` pointing at a now-EMPTY shell, which
            // AiTurnController.RunEmptyArmyCleanup's own stale-task sweep (see that method's own
            // comment) reads as a combat-wiped orphan and deletes outright — the task, its
            // TargetHex, and all recruiting progress gone, not preserved as a registered shell (an
            // older draft of this file assumed the shell always survives; RunEmptyArmyCleanup's
            // later stale-task removal made that assumption false). AiDefencePlanner then sees no
            // task for this base next turn and starts a brand new one from scratch — recruit, fold,
            // delete, repeat, forever. Checked BEFORE the Active/Turtle posture check, and
            // independent of posture entirely — a still-forming Patrol army gets exactly the same
            // shield a still-forming Active one already had.
            if (task.StillAssembling)
                return true;
            return task.Posture == AiDefencePosture.Active || task.Posture == AiDefencePosture.Turtle;
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
                .Where(a => IsLoneArmyAtBase(a, garrisonHex) && a.Members[0].IsHero && !IsProtectedTaskArmy(player, a))
                .OrderByDescending(a => a.Members[0].CommandRating)
                .FirstOrDefault();
            if (loneHeroArmy == null)
                return null;

            UnitData hero = loneHeroArmy.Members[0];
            var withHero = new List<UnitData>(garrison.Members) { hero };
            if (ArmyData.ComputeCapacity(withHero, true) <= garrison.Members.Count)
                return null; // this hero wouldn't actually buy the garrison any room — not worth lending anything over

            UnitData weakest = garrison.Members.Where(m => !m.IsHero && AiArmyRoles.CanSpareGarrisonMember(player, garrison, m))
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
        //
        // Solo Recce excluded (2026-08-24, project owner's own root-cause report) — a freshly
        // assembled Recce carrier (AiScoutPlanner.AssembleRecceScoutRoutine) is a single member at
        // the garrison hex for exactly one drain call before it gets its own VisitHex task next
        // turn, which is also the one call IsProtectedTaskArmy can't yet shield it (no task
        // registered until TryStartVisitCandidates runs). Without this exclusion this tier read
        // that gap as an ordinary fragile lone-unit and folded it straight back into the garrison
        // every single time — create → transfer Recce → fold → RunEmptyArmyCleanup deletes the
        // shell → repeat next turn, burning ReserveArmy/AssembleRecceScout's own AP for zero net
        // scouting capacity. AiArmyRoles.IsSoloRecce is the same domain check FindStrandedWeakArmies
        // already trusts to recognize this composition as an intentional standalone army, not a
        // name/hex/extra-state special case.
        private static ConsolidationMove? FindLoneArmyFoldMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            List<ArmyData> loneArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => IsLoneArmyAtBase(a, garrisonHex) && !AiArmyRoles.IsSoloRecce(a) && !IsProtectedTaskArmy(player, a))
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

        // ---- CollapseTemporaryAssembly ----

        public readonly struct CollapseMove
        {
            public readonly ArmyData Source;
            public readonly ArmyData Garrison;
            public readonly IReadOnlyList<UnitData> UnitsToMove;
            public readonly AiTask Task;
            public readonly string Reason;

            public CollapseMove(ArmyData source, ArmyData garrison, IReadOnlyList<UnitData> unitsToMove, AiTask task, string reason)
            {
                Source = source;
                Garrison = garrison;
                UnitsToMove = unitsToMove;
                Task = task;
                Reason = reason;
            }
        }

        // CollapseTemporaryAssembly (2026-08-23 redesign, project owner's own spec) — replaces the
        // old tier 1b (FindAssemblingArmyFoldMove), which folded a still-assembling task army back
        // in one member at a time, several drain iterations and several log lines for what's really
        // one event: "this recruit isn't ready yet, stand down until next turn". Now it's a single
        // atomic move — either the WHOLE roster fits in the garrison this call, or nothing moves at
        // all (project owner's own explicit correction during planning: gating the move loop on
        // `garrison.HasRoom` per-unit would silently move SOME of the roster and stop, reproducing
        // the exact per-unit trickle this mechanism exists to replace, just inside one coroutine
        // instead of several drain iterations). First in the whole phase's priority order — tried
        // before HandleOverflow/IdleBalance every drain iteration (see AiTurnController.
        // RunGarrisonReorgPhase) — but never forces its way through: if the roster doesn't fit this
        // call, it simply declines and lets Overflow/IdleBalance run instead, which (see
        // FindHexBalanceMove's own strength-balance step) can donate a garrison slot to a field army
        // and free room for a LATER call to finally succeed.
        //
        // Hero included in the collapse (project owner's own confirmed call) — unlike the old tier
        // 1b, which kept the hero anchoring the field escort and only cycled non-hero members, this
        // can empty the source army down to nothing. Safe because RaidWeakerArmyTask.FindRecruitAt's
        // own NeedsHero check already re-picks a hero FIRST from garrison stock the next time this
        // same task needs one — an emptied shell just re-recruits from scratch, no different from
        // any other still-assembling army that never had a hero yet.
        //
        // Task/TargetHex/StillAssembling are all left completely untouched — the point of this move
        // is safety, not progress or regress on the task itself (project owner's own spec: "task не
        // завершается; target не теряется; StillAssembling сохраняется; армия остаётся привязана к
        // task"). HexSelectionController.DeleteArmyIfEmptied's own Barracks-hex exception (see
        // IsLoneArmyAtBase's own comment) is exactly why the now-possibly-empty Source object
        // survives as a registered shell instead of vanishing along with the task that owns it.
        // CollapseTemporaryAssembly's own eligibility gate (2026-08-23 narrowing, project owner's
        // own report/spec — a real Aggression/Defence log showed the old blanket StillAssembling
        // trigger erasing healthy multi-turn progress every single end of turn, not just genuinely
        // stalled assemblies):
        //   - RaidWeakerArmy is EXCLUDED outright. A forming raid sitting at its own garrison hex
        //     poses no safety question folding-into-garrison would even address — the home hex
        //     isn't threatened just because a raid hasn't left yet, and a REAL siege already has its
        //     own, stronger response (AiAggressionPlanner.TryRaidAssembleCandidates refuses to start
        //     new raids and TryContinueRaidTask force-recalls active ones the moment
        //     AiDefencePlanner.IsUnderSiege fires — never a Collapse fold). Collapsing it here only
        //     ever destroyed cross-turn recruiting progress for no offsetting benefit. IsProtectedTaskArmy
        //     (this class's own IdleBalance gate) already shields a StillAssembling Raid army from
        //     every OTHER generic reorg tier unconditionally, Collapse or not — see its own comment.
        //   - DefendCitadel Patrol posture is EXCLUDED too, but for a narrower reason than it used to
        //     be (2026-08-24 update): a still-forming Patrol army now gets shielded from ordinary
        //     IdleBalance by IsProtectedTaskArmy's own StillAssembling branch same as Active/Turtle —
        //     it's only a READY (non-assembling) Patrol army that falls back to plain unprotected
        //     balancing material. Either way a dedicated Collapse pass here would be redundant: a
        //     still-forming one is IsCollapseEligible-excluded by the Posture check below regardless
        //     (Collapse only ever admits Active), and a ready one has nothing left to "collapse".
        //   - DefendCitadel Turtle posture is EXCLUDED — a real siege in progress (Turtle only exists
        //     under IsUnderSiege) is exactly the case where an incomplete-but-real extra stack next to
        //     the garrison still has defensive value; folding it away purely because the turn ran out
        //     serves no one.
        //   - DefendCitadel Active posture is the only case left, and even then only once
        //     AiTask.AssemblyProgressTurn confirms nothing actually landed THIS turn — a composition
        //     that grew (recruit/strengthen/merge, tracked by AssembleRaidForceRoutine/
        //     StrengthenDefenceForceRoutine) this same turn is real progress, not something safety
        //     housekeeping gets to erase just because the target isn't fully met yet.
        private static bool IsCollapseEligible(AiTask task, AiTurnContext ctx)
        {
            if (task == null || !task.StillAssembling)
                return false;
            if (task.Kind != AiTaskKind.DefendCitadel)
                return false;
            if (task.Posture != AiDefencePosture.Active)
                return false;
            return task.AssemblyProgressTurn != ctx.TurnNumber;
        }

        public static CollapseMove? FindCollapseMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            if (garrison == null || !garrison.HasRoom)
                return null;

            ArmyData source = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => a != null && !a.IsGarrison && !a.IsPrison && a.Hex.Equals(garrisonHex)
                    && a.Members.Count > 0 && IsCollapseEligible(AiTaskRegistry.TaskFor(player, a), ctx));
            if (source == null)
                return null;

            AiTask task = AiTaskRegistry.TaskFor(player, source);

            // Non-hero weakest-first, then heroes — ordering only actually matters for the verbose
            // per-unit debug trace below (a real proposal here is all-or-nothing, see this method's
            // own comment), kept anyway since it reads naturally as "the rank and file file back in,
            // the leader last".
            List<UnitData> members = source.Members.Where(m => !m.IsHero).OrderBy(m => m.Defense).ThenBy(m => m.Attack)
                .Concat(source.Members.Where(m => m.IsHero))
                .ToList();

            // Atomicity gate — EVERY member has to individually clear the oscillation guard, the
            // WHOLE batch's AP cost has to fit the garrison's CURRENT pool simulated cumulatively,
            // AND the garrison has to have literal room for the whole headcount, or this proposes
            // nothing at all this call (see this method's own class comment on why a partial
            // collapse defeats the point).
            //
            // Deliberately NOT CanAffordTransferInto here (2026-08-23 correction) — that checks
            // each unit's AP cost independently against the SAME starting pool, which is fine for
            // every other tier here (they only ever move ONE unit), but wrong for a whole-roster
            // batch: two 2-AP units could each individually pass against a 3-AP pool even though
            // the batch actually needs 4. Simulated explicitly below instead, so the ACTUAL
            // sequential ArmyActions.TransferMember calls in CollapseTemporaryAssemblyRoutine can
            // never run out of AP partway through and leave a partial collapse behind. Only
            // matters at all when garrison.HasActivatedThisTurn is true — nothing in this codebase
            // ever issues the garrison a move order (every mover is gated on !a.IsGarrison), so
            // that's never actually true in practice, but this method doesn't get to assume that;
            // it checks it properly instead.
            if (members.Any(m => ctx.WouldRevisitArmy(m, garrison)))
                return null;
            if (garrison.HasActivatedThisTurn)
            {
                PlayerRoot targetRoot = PlayerRootRegistry.FindFor(garrison.Owner);
                if (targetRoot == null)
                    return null;
                int apBudget = targetRoot.ActionPoints;
                foreach (UnitData m in members)
                {
                    if (apBudget < m.ActivationApCost)
                        return null;
                    apBudget -= m.ActivationApCost;
                }
            }
            if (garrison.Capacity - garrison.Members.Count < members.Count)
                return null;

            // IsCollapseEligible above only ever admits DefendCitadel/Active now — no more "Raid"
            // label branch to pick between (2026-08-23 narrowing, see IsCollapseEligible's own
            // comment for why RaidWeakerArmy never reaches this method at all any more).
            return new CollapseMove(source, garrison, members, task,
                $"\"{source.Name}\" temporary Defence assembly collapsing into the garrison — {members.Count} unit(s) returning; "
                    + $"task preserved, target=({task.TargetHex.Q},{task.TargetHex.R})");
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

            // Excludes every protected task army (2026-08-23 redesign — see IsProtectedTaskArmy's
            // own comment) — a Raid or Active/Turtle-Defence force's composition belongs to its own
            // planner, and a Turtle "кулак" deliberately concentrating power above the ordinary
            // 70/30 garrison/field ratio would otherwise just get read as an imbalance and
            // rebalanced back down by this same method, working directly against the posture's own
            // purpose.
            List<ArmyData> fieldArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && !a.IsAirfield && a.Hex.Equals(garrisonHex) && a.Members.Count > 0
                    && !AviationRules.IsAirArmy(a) && !IsProtectedTaskArmy(player, a))
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
                UnitData strongInGarrison = garrison.Members.Where(m => !m.IsHero && m.Defense > 2 && m.Attack >= 4
                        && AiArmyRoles.CanSpareGarrisonMember(player, garrison, m))
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
                UnitData strongest = garrison.Members.Where(m => !m.IsHero && AiArmyRoles.CanSpareGarrisonMember(player, garrison, m))
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
            // Gated on AiConfig.compositionImbalanceThreshold (2026-08-23, project owner's own call
            // — an initial tuning value, not yet checked against a real playtest log) — a raw 1-unit
            // gap used to fire this every single drain call for a difference nobody would call
            // "imbalanced", the main source of the churn the project owner flagged; now the gap has
            // to actually be meaningful before this proposes shuffling anything.
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
                if (melee - ranged >= AiConfig.compositionImbalanceThreshold)
                    needs = m => m.Range > 1;
                else if (ranged - melee >= AiConfig.compositionImbalanceThreshold)
                    needs = m => m.Range <= 1;
                else if (abilityHeavy - simple >= AiConfig.compositionImbalanceThreshold)
                    needs = m => m.Abilities.Count == 0;
                else
                    continue;

                foreach (ArmyData donor in allArmies)
                {
                    if (donor == recipient || donor.Members.Count(m => !m.IsHero) <= 1)
                        continue;
                    UnitData spare = donor.Members.Where(m => !m.IsHero && needs(m) && AiArmyRoles.CanSpareGarrisonMember(player, donor, m))
                        .OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
                    if (spare == null || !CanAffordTransferInto(recipient, spare) || ctx.WouldRevisitArmy(spare, recipient))
                        continue;
                    return new ConsolidationMove(donor, spare, recipient,
                        $"{spare.Name} — evening out \"{recipient.Name}\"'s composition from \"{donor.Name}\"");
                }
            }
            return null;
        }

        // Composition-swap guard (2026-08-24, project owner's own root-cause report) — a real log
        // showed FindReorgSwap's own composition branch ping-ponging the same handful of unit
        // types (Flamer/Medium Infantry/BS Melee/...) between the same 3-4 armies on a hex for
        // several turns straight: it only ever asked "does this fix the RECIPIENT's imbalance",
        // never "did giving the donor `giveUp` in exchange for `spare` push the DONOR itself past
        // ITS OWN threshold" — so each swap could (and did) just relocate the imbalance instead of
        // resolving it, and the next drain call "fixed" the army it had just broken. Mirrors the
        // exact melee/ranged/ability-heavy/simple counts the composition branch above already reads
        // off `recipientUnits`, just summed into one comparable number per army so a proposed trade
        // can be judged by its NET effect on both sides at once. Asymmetric on the ability axis
        // (only abilityHeavy-over-simple is penalized) on purpose — matches every existing
        // composition-imbalance check in this file (FindHexBalanceMove/FindReorgSwap's own `needs`
        // selection above), which likewise only ever triggers that one direction.
        private static int CompositionPenalty(IEnumerable<UnitData> units)
        {
            List<UnitData> nonHero = units.Where(m => !m.IsHero).ToList();
            int melee = nonHero.Count(m => m.Range <= 1);
            int ranged = nonHero.Count(m => m.Range > 1);
            int abilityHeavy = nonHero.Count(m => m.Abilities.Count > 0);
            int simple = nonHero.Count(m => m.Abilities.Count == 0);
            int threshold = AiConfig.compositionImbalanceThreshold;
            return Math.Max(0, Math.Abs(melee - ranged) - threshold + 1)
                + Math.Max(0, abilityHeavy - simple - threshold + 1);
        }

        // Same penalty, evaluated on the hypothetical roster `army` would have after trading
        // `remove` away for `add` — never mutates `army` itself, just feeds CompositionPenalty a
        // simulated member list.
        private static int CompositionPenaltyAfterSwap(ArmyData army, UnitData remove, UnitData add)
        {
            List<UnitData> hypothetical = army.Members.Where(m => m != remove).ToList();
            hypothetical.Add(add);
            return CompositionPenalty(hypothetical);
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

            // Excludes every protected task army — see IsProtectedTaskArmy's own comment (same
            // exclusion FindHexBalanceMove uses).
            List<ArmyData> fieldArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && !a.IsAirfield && a.Hex.Equals(garrisonHex) && a.Members.Count > 0
                    && !AviationRules.IsAirArmy(a) && !IsProtectedTaskArmy(player, a))
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
                UnitData strongInGarrison = garrison.Members.Where(m => !m.IsHero && m.Defense > 2 && m.Attack >= 4
                        && AiArmyRoles.CanSpareGarrisonMember(player, garrison, m))
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
                UnitData strongest = garrison.Members.Where(m => !m.IsHero && AiArmyRoles.CanSpareGarrisonMember(player, garrison, m))
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
            // same AiConfig.compositionImbalanceThreshold gate, traded instead of moved since nobody
            // has room. `giveUp` is the recipient's own excess-type spare — the thing it trades
            // AWAY for the donor's needed-type unit.
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
                if (melee - ranged >= AiConfig.compositionImbalanceThreshold) { needs = m => m.Range > 1; excess = m => m.Range <= 1; }
                else if (ranged - melee >= AiConfig.compositionImbalanceThreshold) { needs = m => m.Range <= 1; excess = m => m.Range > 1; }
                else if (abilityHeavy - simple >= AiConfig.compositionImbalanceThreshold) { needs = m => m.Abilities.Count == 0; excess = m => m.Abilities.Count > 0; }
                else continue;

                UnitData giveUp = recipientUnits.Where(excess).OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
                if (giveUp == null)
                    continue;

                foreach (ArmyData donor in allArmies)
                {
                    if (donor == recipient)
                        continue;
                    UnitData spare = donor.Members.Where(m => !m.IsHero && needs(m) && AiArmyRoles.CanSpareGarrisonMember(player, donor, m))
                        .OrderBy(m => m.Defense).ThenBy(m => m.Attack).FirstOrDefault();
                    if (spare == null || !CanAffordTransferInto(recipient, spare) || !CanAffordTransferInto(donor, giveUp)
                        || ctx.WouldRevisitArmy(spare, recipient) || ctx.WouldRevisitArmy(giveUp, donor))
                        continue;

                    // Net-effect guard — see CompositionPenalty's own comment. A trade that "fixes"
                    // recipient by pushing donor into (or deeper past) its own imbalance threshold
                    // is exactly the structural ping-pong this guard exists to stop; only a trade
                    // that strictly improves the COMBINED penalty of both armies is allowed through.
                    int before = CompositionPenalty(recipient.Members) + CompositionPenalty(donor.Members);
                    int after = CompositionPenaltyAfterSwap(recipient, giveUp, spare) + CompositionPenaltyAfterSwap(donor, spare, giveUp);
                    if (after >= before)
                        continue;

                    return new SwapMove(recipient, giveUp, donor, spare,
                        $"swapping {giveUp.Name} for {spare.Name} with \"{donor.Name}\" — evening out \"{recipient.Name}\"'s "
                            + $"composition, no free slot to just move into (penalty {before}→{after})");
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
                ?? FindHexBalanceMove(player, garrisonHex, garrison, ctx);
        }

        // ---- Feature 4A — disposable/reusable empty army shells (2026-08-24) ----
        // Project owner's own report: turn-30 logs showed many field armies with roster size 0 —
        // empty shells that persist after ConsolidateUnitsRoutine/AssembleRaidForceRoutine/etc.
        // emptied them, inflating the army count for nothing (and, since several of these routines'
        // own DeleteArmyIfEmptied call refuses to tear one down sitting exactly on its own owner's
        // Barracks hex — see that method's own comment — a shell parked right at a garrison hex
        // never actually goes away that way at all).

        // An empty, task-less field army that isn't part of the small reserve buffer
        // AiManagementPlanner.GatherFallbackCandidates already deliberately keeps around (its own
        // spareArmies count, capped at AiConfig.maxSpareArmies — see that method's own comment) —
        // "surplus beyond the reserve buffer", not a flat "any empty army is disposable" rule,
        // deliberately: disposing of ALL of them would fight GatherFallbackCandidates' own logic,
        // which relies on up to maxSpareArmies of these existing so it doesn't keep re-spending AP
        // recreating the exact same buffer turn after turn. Ordered by Name for a stable,
        // deterministic pick of which specific instances count as "the kept reserve" —
        // ArmyRegistry's own enumeration order isn't guaranteed stable call to call.
        public static bool IsDisposableEmptyArmy(PlayerSetupData player, ArmyData army)
        {
            if (army == null || army.IsGarrison || army.IsPrison || army.IsAirfield || army.Members.Count != 0 || army.Controller == null)
                return false;
            if (AiTaskRegistry.TaskFor(player, army) != null)
                return false;
            return DisposableEmptyArmies(player).Contains(army);
        }

        // 2026-08-24 P2 fix (project owner's own report): used to skip the alphabetically-first
        // maxSpareArmies empties WHEREVER they happened to sit, which could — and did — pick an
        // empty army stranded out in the FIELD as "the kept reserve", something
        // GatherFallbackCandidates/FindDisposableEmptyArmyAt only ever actually reuses at a garrison
        // hex anyway (see their own comments) and which, unlike a base-hex shell, has no CurrentMovement
        // left to ever walk home and no ReturnForConsolidation task watching it — a permanent orphan
        // nothing in this codebase ever cleans up. The reserve now only ever comes from this player's
        // OWN garrison hexes (never a field army); a field empty is always disposable.
        private static IEnumerable<ArmyData> DisposableEmptyArmies(PlayerSetupData player)
        {
            List<ArmyData> empties = ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && a.Members.Count == 0 && AiTaskRegistry.TaskFor(player, a) == null)
                .ToList();

            var ownGarrisonHexes = new HashSet<HexCoord>(AiTurnController.OwnGarrisonHexes(player));
            var reserved = new HashSet<ArmyData>(empties
                .Where(a => ownGarrisonHexes.Contains(a.Hex))
                .OrderBy(a => a.Name)
                .Take(AiConfig.maxSpareArmies));

            return empties.Where(a => !reserved.Contains(a));
        }

        // Feature 4A's own reuse half — see IsDisposableEmptyArmy's own comment. `hex` is wherever
        // the caller was about to spend ArmyActions.CreateArmy's own AP on a brand-new one (see
        // AiAggressionPlanner.RequestRaidArmyRoutine/DispatchReinforcementRoutine, AiDefencePlanner.
        // RequestDefendArmyRoutine, AiScoutPlanner.SpawnReconArmyRoutine — all four now check this
        // FIRST). Null means no disposable shell exists there right now — callers fall back to
        // their own ordinary CreateArmy call, unchanged.
        public static ArmyData FindDisposableEmptyArmyAt(PlayerSetupData player, HexCoord hex) =>
            DisposableEmptyArmies(player).FirstOrDefault(a => a.Hex.Equals(hex));

        // Feature 4A's own base-hex deletion (2026-08-24 P1 fix, project owner's own code-review
        // report) — HexSelectionController.DeleteArmyIfEmptied (see that method's own comment)
        // deliberately refuses to tear down an empty shell sitting on its own owner's Barracks
        // hex, on the theory that a shell parked right at a base is free, instant reuse fodder for
        // the next RequestRaidArmy/RequestDefendArmy/SpawnReconArmy/DispatchReinforcement that
        // needs one there. That reasoning only holds up to the maxSpareArmies buffer
        // IsDisposableEmptyArmy already reserves — a shell SURPLUS beyond that buffer sitting at a
        // base hex forever means the "at most maxSpareArmies empty armies at end of turn"
        // invariant AiTurnController.RunEmptyArmyCleanup was supposed to enforce never actually
        // held for the base-hex case, only the stranded-in-the-field one DeleteArmyIfEmptied
        // already handled correctly. This is the narrowly-scoped counterpart: allowed to delete an
        // empty shell EVEN sitting on a base/garrison hex, gated on the exact same
        // IsDisposableEmptyArmy predicate every other Feature 4A reader already trusts — re-checked
        // fresh here, not assumed from the caller's own earlier read, same "checked right before
        // acting" rule this file follows everywhere else:
        //   - not IsGarrison/IsPrison, empty, task-less, has a live Controller (IsDisposableEmptyArmy
        //     itself)
        //   - no active AiTask directly owns it — AiTaskRegistry.TaskFor's own lookup inside
        //     IsDisposableEmptyArmy only ever matches a task's `.Army` field, NOT `.TargetArmy` (e.g.
        //     a RaidReinforce courier task's target); an empty army referenced only via TargetArmy
        //     can still slip through here. Not a gap this narrow fix closes — see
        //     AiTurnController.RunEmptyArmyCleanup's own stale-`.Army`-task invalidation for the case
        //     that fix actually targets.
        //   - strictly beyond the maxSpareArmies reserve, via the exact same DisposableEmptyArmies
        //     ordering/Skip IsDisposableEmptyArmy itself already uses, so the two can never disagree
        //     about which specific instances count as "the kept reserve"
        // Same two-step removal DeleteArmyIfEmptied itself performs internally (ArmyRegistry.
        // Unregister, then tear down the Controller's own marker) — deliberately NOT routed through
        // that method with some bypass flag: its own `_selectedArmy` deselect bookkeeping is a
        // human-UI concern (a human can never have another player's own empty AI shell selected in
        // the first place), so this narrower version skips it rather than pulling a
        // HexSelectionController reference into this otherwise UI-independent task class just for
        // that one check.
        public static bool DeleteDisposableArmyAtBase(ArmyData army, PlayerSetupData player)
        {
            if (!IsDisposableEmptyArmy(player, army))
                return false;

            ArmyRegistry.Unregister(army);
            if (army.Controller != null)
            {
                UnityEngine.Object.Destroy(army.Controller.gameObject);
                army.Controller = null;
            }
            return true;
        }

        // ---- Feature 4B — застрявшие одиночные полевые армии (2026-08-24) ----
        // Project owner's own report: the same turn-30 log showed many field armies with roster
        // size 1 — lone units stranded in the field, untasked, never folded into any garrison
        // (IdleBalance's own lone-army-fold tier, FindLoneArmyFoldMove above, only ever looks AT a
        // garrison hex — see IsLoneArmyAtBase's own comment — so a lone unit standing anywhere else
        // is simply invisible to it).
        //
        // An untasked (AiTaskRegistry.TaskFor == null — the closest concept this codebase has to
        // "no task or reservation", since a reservation is an AiTask-scoped accounting claim, not an
        // ArmyData-scoped one — see AiResourceReservation's own class comment) field army — not the
        // garrison/prison, and not already sitting on ANY of the player's own garrison hexes (see
        // AiTurnController.OwnGarrisonHexes) — with exactly one member. Two deliberate exclusions,
        // both because they already have their own dedicated handling elsewhere and this method must
        // not duplicate or fight it (project owner's own spec):
        //   - A solo Recce unit (AiArmyRoles.IsSoloRecce) — Recce operates solo BY DESIGN (see
        //     AiScoutPlanner's own class comment); walking it "home for consolidation" would undo the
        //     exact composition AiScoutPlanner deliberately keeps it in.
        //   - A hero awaiting escort (AiArmyRoles.IsSoloHeroAwaitingEscort) — already has its own
        //     return-home handling, AiScoutPlanner.TryReturnHomeCandidates (see
        //     AiManagementPlanner's own class comment: "The 'solo hero with nothing to visit walks
        //     home' fallback lives on AiScoutPlanner.TryReturnHomeCandidates instead"); this method
        //     would just be a slower, less specific duplicate of that same walk for this exact shape.
        //
        // Returns every matching army in one pass (not just one) — unlike most other Find* methods
        // in this file, there's no atomicity/ordering concern here (each stranded army gets its own
        // independent "walk home" move, see AiTurnController.RunStrandedArmyRecovery), so a single
        // sweep avoids recomputing this same scan once per candidate the way a one-at-a-time
        // "propose, re-evaluate fresh" shape would.
        public static IEnumerable<ArmyData> FindStrandedWeakArmies(PlayerSetupData player)
        {
            var ownGarrisonHexes = new HashSet<HexCoord>(AiTurnController.OwnGarrisonHexes(player));
            return ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && a.Controller != null && a.Members.Count == 1
                    && !ownGarrisonHexes.Contains(a.Hex)
                    && !AviationRules.IsAirArmy(a)
                    && !AiArmyRoles.IsSoloRecce(a) && !AiArmyRoles.IsSoloHeroAwaitingEscort(a)
                    && AiTaskRegistry.TaskFor(player, a) == null);
        }

        // Pre-checks the exact same AP-affordability ArmyActions.TransferMember will itself
        // enforce, so an unaffordable move is never proposed as a candidate in the first place
        // (same "checked before proposing" rule AiManagementPlanner.FindPlacement follows).
        // Internal, not private (2026-08-24, Feature 2) — AiAggressionPlanner.AdvanceGarrisonSeed
        // needs the exact same affordability read for its own single-unit transfer into a brand-new
        // base's garrison, rather than duplicating this check a second time.
        internal static bool CanAffordTransferInto(ArmyData target, UnitData unit)
        {
            if (!target.HasActivatedThisTurn)
                return true;
            PlayerRoot targetRoot = PlayerRootRegistry.FindFor(target.Owner);
            return targetRoot != null && targetRoot.CanSpendActionPoints(unit.ActivationApCost);
        }
    }
}
