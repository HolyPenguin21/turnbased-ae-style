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
    // candidate here is a one-shot reorg move, re-evaluated fresh every Decide() step (same
    // "непрерывная переоценка" every other planner already follows) instead of being tracked. The
    // project owner's own spec: garrison AND every other army stay balanced — nothing left weak
    // or empty that stock elsewhere could fix.
    //
    // Композиция — n/a, doesn't run through one owned army.
    //
    // Поведение — FindReorgMove tries, in order, every step. The project owner's own invariant
    // ties them together: across garrison + every field army, garrison stays the WEAKEST
    // formation, and field armies stay roughly level WITH EACH OTHER too, not just each
    // individually topped up from garrison in isolation. A separate, earlier gate — how MANY
    // hero-led armies are even allowed to exist at once — lives in CanSupportAnotherHeroArmy
    // (see its own comment): "лучше держать несколько героев в гарнизоне, чем плодить слабые
    // армии" — past AiConfig.MaxActiveHeroArmies, a fresh hero stays benched inside garrison
    // rather than founding (or getting escorted into) yet another thin army.
    //   1) FindHeroEscortFromGarrison — a hero-led army (bare or already escorted) with room pulls
    //      the strongest available garrison unit. A hero can't grow itself, so this always wins
    //      over merely fattening up a leaderless stockpile.
    //   2) FindPlainArmyReinforcement — same pull, for a plain (no hero) army with room, once no
    //      hero-led army needs the stock more — "свободные герои расширяют армии" already covers
    //      the hero case above, this is the same idea for a stockpile nobody's leading yet.
    //   3) A lone (single-member) army folds back into garrison instead of sitting there a
    //      fragile single unit forever — but ONLY once garrison has nothing left to spare it (1/2
    //      above) AND garrison has real headroom to receive it without immediately overflowing
    //      again (AiManagementPlanner.HasGarrisonDepositRoom). Skipping that headroom check is
    //      exactly what used to recreate an endless split-out/fold-back cycle (the project
    //      owner's own "21 пустая армия" / "ИИ перестал создавать героев" report) — garrison
    //      stays over capacity is FindGarrisonOverflow's own separate concern, not this tier's.
    //   4) Garrison has nothing to spare AND no headroom to absorb a stray either — pairs two
    //      lone armies together instead (preferring a hero-led one as the merge target).
    //   5) FindArmyRebalance — every одиночка has now had its chance to fold or pair (see the
    //      project owner's own "почему 3 пунктом а не последним" call — resolving одиночки comes
    //      first), so only now do already-SETTLED field armies (2+ members) get compared and
    //      levelled: one hoarding several strong units while another sits thin donates its
    //      weakest spare to whichever is currently weakest-on-average — "армии тоже могут
    //      меняться юнитами между собой" (the project owner's own Example 2).
    //
    // FindGarrisonOverflow/FindGarrisonOverflowDestination are the mirror-image concern (garrison
    // itself over capacity, evicting outward) — kept here too since it's the same "капасити
    // гарнизона" half of this one task, just the opposite direction.
    public static class GarrisonReorgTask
    {
        // ---- Капасити гарнизона ----

        // Just enough members to open ONE slot (Members.Count - (Capacity - 1)), not an arbitrary
        // "move half" — re-evaluated fresh every turn, so a garrison that fills up again next turn
        // just proposes another small split rather than needing to guess the right batch size up
        // front. WHICH members leave is the strongest first (Defense, then Attack as the
        // tiebreak) — a tank or artillery piece is worth fielding, a bare rifleman is worth
        // stockpiling instead, since a garrisoned unit gets the base's own defense bonus the field
        // doesn't (the project owner's own call). Null if the garrison already has room or doesn't
        // exist.
        public static IReadOnlyList<UnitData> FindGarrisonOverflow(ArmyData garrison)
        {
            if (garrison == null || garrison.HasRoom)
                return null;
            int overflow = garrison.Members.Count - (garrison.Capacity - 1);
            if (overflow <= 0)
                return null;
            return garrison.Members.OrderByDescending(m => m.Defense).ThenByDescending(m => m.Attack)
                .Take(overflow).ToList();
        }

        // Where FindGarrisonOverflow's own pick actually goes — a hero-led escort with room first
        // (folds straight in, same target FindHeroEscortFromGarrison already favours for the pull
        // direction), else an ALREADY-EXISTING plain reserve army (see AiArmyRoles.
        // IsPlainReserveArmy — empty or already growing, either way). Null means there's nowhere
        // for it to go this turn — garrison stays over capacity for now rather than spawning a
        // brand new reserve army of its own to make room.
        //
        // Deliberately never spawns one itself (used to, via its own separate spareArmies <
        // maxSpareArmies check) — that made this the FIRST of two independent, differently-timed
        // "do we need a new spare army" checks each turn, since TryGarrisonSplitCandidate runs
        // ahead of TryConsolidationCandidate in AiTurnController.Decide's own candidate order. A
        // turn where garrison overflow AND a lone-army pairing (GarrisonReorgTask.FindReorgMove's
        // own tier 4 — see its "Поведение" comment) were BOTH available this same step could pick
        // the overflow "spawn new" candidate first (same managementGarrisonBalanceScore, added
        // earlier so it wins ties) and spend AP creating a fresh empty army, when letting the
        // lone-army pairing go first would have freed an EXISTING one to reuse instead (the
        // project owner's own report). Now there is exactly one place that ever decides to spend
        // AP on a brand new spare army — AiTurnController.Decide's own ReserveArmy fallback, right
        // next to DrawCard at the very end — so it always runs with the fullest possible picture of
        // what this turn's own reorg tiers already freed up. Overflow eviction just waits its turn
        // when nothing existing has room; the next time FindGarrisonOverflow proposes a split, that
        // freshly-created (or freshly-freed) reserve army shows up as `existingReserve` above.
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
        // leaderless stockpile forever. CanSupportAnotherHeroArmy still gates this exactly like
        // every other path that founds/grows a hero-led army — overflow doesn't get to bypass the
        // "лучше держать несколько героев в гарнизоне" cap just because it's forced to spawn a new
        // army anyway. Highest CommandRating first when more than one hero is benched — the
        // project owner's own "на первом месте герой с наибольшим капасити по картам": that hero
        // can carry the biggest escort once it starts pulling from FindHeroEscortFromGarrison
        // above, so it's the one most worth spending this one hero-army slot on.
        public static UnitData FindGarrisonHeroToPromote(PlayerSetupData player, ArmyData garrison)
        {
            if (garrison == null || !CanSupportAnotherHeroArmy(player))
                return null;
            return garrison.Members.Where(m => m.IsHero).OrderByDescending(m => m.CommandRating).FirstOrDefault();
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
        // member forever, not "waiting to grow" (see AiArmyRoles's own class comment). Scoped to
        // `garrisonHex` only — an army out in the field mid-task is never touched by this sweep.
        private static bool IsLoneArmyAtBase(ArmyData army, HexCoord garrisonHex)
        {
            return army != null && !army.IsGarrison && !army.IsPrison && !army.HasRecce
                && army.Members.Count == 1 && army.Hex.Equals(garrisonHex);
        }

        // A hero at the garrison hex with spare non-hero units ALREADY sitting in the garrison
        // forms its escort straight out of that stock, bare hero or already-escorted alike — with
        // units right there waiting, there is nothing to gain by parking the hero for a turn
        // first. Checked before every other tier in FindReorgMove, so this always wins. Not scoped
        // to any "lone army" restriction on purpose — a hero that has already picked up its first
        // escort this same turn must keep being offered here too (see FindReorgMove's own step-by-
        // step Decide loop), otherwise it would stop at Hero+1 and only resume next turn even with
        // the garrison still stocked. Stops once the hero's own roster is full (HasRoom false) or
        // IsMakeshiftScoutCapable's own Hero+2 floor is reached — a full army escort forming is
        // the goal, not endless top-up past the point AiScoutPlanner would already send it out. A
        // still-bare hero (Members.Count == 1, no escort yet) additionally needs
        // CanSupportAnotherHeroArmy's own go-ahead — normally already enforced at card-placement
        // time (see that method's own comment), this is just the defensive backstop so a bare
        // hero that somehow ended up over the cap anyway never starts growing either; an already-
        // escorted hero is grandfathered in and keeps growing regardless.
        private static ConsolidationMove? FindHeroEscortFromGarrison(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison)
        {
            if (garrison == null)
                return null;
            ArmyData heroArmy = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                AiArmyRoles.IsHeroLedCombatArmy(a) && a.Hex.Equals(garrisonHex) && a.HasRoom
                && !AiArmyRoles.IsMakeshiftScoutCapable(a)
                && (a.Members.Count > 1 || CanSupportAnotherHeroArmy(player, a)));
            if (heroArmy == null)
                return null;
            UnitData garrisonUnit = StrongestNonHero(garrison);
            if (garrisonUnit == null)
                return null;
            return CanAffordTransferInto(heroArmy, garrisonUnit)
                ? new ConsolidationMove(garrison, garrisonUnit, heroArmy)
                : (ConsolidationMove?)null;
        }

        // "лучше держать несколько героев в гарнизоне, чем плодить слабые армии" (the project
        // owner's own spec — see AiConfig.minArmyStrengthShare's own comment for the math behind
        // MaxActiveHeroArmies). Counts EVERY existing hero-led army regardless of escort level —
        // a still-bare solo hero already claims one of the limited "army slots" the moment it
        // exists, not just once it's grown, so a THIRD hero can't sneak in just because the first
        // two haven't picked up an escort yet either. `excluding` lets a caller ask "if THIS
        // specific army weren't counted, is there still room for one more" — used by
        // FindHeroEscortFromGarrison above to check whether ITS OWN candidate is within the cap,
        // and left null by AiManagementPlanner.FindPlacement, which is asking about a card that
        // isn't an army yet at all.
        public static bool CanSupportAnotherHeroArmy(PlayerSetupData player, ArmyData excluding = null)
        {
            int existing = ArmyRegistry.AllForOwner(player)
                .Count(a => a != excluding && AiArmyRoles.IsHeroLedCombatArmy(a));
            return existing < AiConfig.Current.MaxActiveHeroArmies;
        }

        // Same pull as FindHeroEscortFromGarrison, just for a plain (no hero yet) army with room
        // instead of an already hero-led one — "к ним [одиночкам] добавятся юниты чтобы
        // сбалансировать армии" (the project owner's own spec): a stockpile nobody's leading yet
        // still deserves to grow from garrison stock, not just sit at whatever size a PlayCard
        // deposit last left it. Checked only once FindReorgMove's own hero tier has nothing left
        // to do — a hero escort forming still outranks merely fattening up a leaderless army.
        //
        // Requires the target to already have at least one member — a still-EMPTY plain reserve
        // army is deliberately left alone here, because role isn't stored anywhere (see
        // AiArmyRoles's own class comment): an empty army fresh out of SpawnReconArmyRoutine
        // ("под Recce-состав") is indistinguishable from a generic empty spare from
        // ReserveArmyRoutine until something actually lands in it. Auto-reinforcing it here first
        // was stealing the slot out from under a still-pending Recce card AND making the army
        // register as non-Recce for IsLoneArmyAtBase's own !HasRecce check — the very next Decide()
        // step then folded that "one lone unit" straight back into garrison, which made it look
        // like a plain reserve army again next step, pulling the same unit right back out: an
        // endless reinforce/fold ping-pong that burned an entire turn's worth of steps without ever
        // resolving (the project owner's own report). A first unit still reaches an empty reserve
        // army fine via AiManagementPlanner.FindPlacement's own Unit/Hero fallback (a card in hand
        // has to go SOMEWHERE); only this background top-up tier waits for that seed to land first.
        private static ConsolidationMove? FindPlainArmyReinforcement(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison)
        {
            if (garrison == null)
                return null;
            ArmyData target = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => AiArmyRoles.IsPlainReserveArmy(a) && a.Members.Count > 0 && a.Hex.Equals(garrisonHex));
            if (target == null)
                return null;
            UnitData garrisonUnit = StrongestNonHero(garrison);
            if (garrisonUnit == null)
                return null;
            return CanAffordTransferInto(target, garrisonUnit)
                ? new ConsolidationMove(garrison, garrisonUnit, target)
                : (ConsolidationMove?)null;
        }

        // Strongest first (Defense, then Attack as the tiebreak) — same "сильных в армию" rule
        // FindGarrisonOverflow follows for the eviction direction, applied here to the pull
        // direction too (FindHeroEscortFromGarrison/FindPlainArmyReinforcement both pull, never
        // push, so both want the same ordering).
        private static UnitData StrongestNonHero(ArmyData garrison) =>
            garrison.Members.Where(m => !m.IsHero).OrderByDescending(m => m.Defense).ThenByDescending(m => m.Attack).FirstOrDefault();

        // Average non-hero unit strength (Defense+Attack) — the yardstick FindArmyRebalance
        // compares armies by. Average, not total: a garrison-adjacent army with many weak units
        // shouldn't out-rank a small army of two tanks just by headcount. float.MinValue for an
        // army with no non-hero members at all (a bare hero, or a freshly spawned empty reserve)
        // — treated as maximally in NEED of reinforcement, never as a donor (see FindArmyRebalance
        // below, which additionally requires >1 non-hero member to ever act as one anyway).
        private static float AverageNonHeroStrength(ArmyData army)
        {
            List<UnitData> nonHero = army.Members.Where(m => !m.IsHero).ToList();
            return nonHero.Count == 0 ? float.MinValue : (float)nonHero.Average(m => m.Defense + m.Attack);
        }

        // Field armies levelling with EACH OTHER, independent of garrison — see this class's own
        // "Поведение" comment, the LAST tier tried. Scoped to every non-garrison, non-Recce ARMY
        // OF TWO OR MORE MEMBERS at `garrisonHex` (hero-led and plain alike, per the project
        // owner's own "все армии" answer) — an army already out on a task elsewhere is never
        // touched. Deliberately excludes single-member armies on BOTH sides — those are the fold/
        // pair tiers' own job (see FindReorgMove's own comment on why this runs only once those
        // have already had their turn: comparing a still-unsettled "одиночка" here would let it
        // skip straight to being reinforced by a strong donor instead of first being resolved the
        // simpler way those tiers already handle, undoing their own ordering). Garrison itself is
        // ALSO excluded from this comparison: it already has its own dedicated "always donates its
        // strongest, keeps the weakest" rule (FindHeroEscortFromGarrison/FindPlainArmyReinforcement/
        // FindGarrisonOverflow) — folding it into a generic average comparison here could hand
        // something back to it, undoing that and breaking the "garrison stays weakest of all"
        // invariant this whole class is built around.
        //
        // Picks the weakest-average army WITH ROOM as recipient and the strongest-average OTHER
        // army as donor — but only one with more than one non-hero member, so a donor is never
        // stripped down to bare-hero/empty over this. The donor's own WEAKEST non-hero unit moves
        // (the project owner's own call — the donor keeps its best, the recipient still gets
        // something real), and only if the gap is real (donor's average genuinely higher).
        private static ConsolidationMove? FindArmyRebalance(PlayerSetupData player, HexCoord garrisonHex)
        {
            List<ArmyData> armies = ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && !a.HasRecce && a.Members.Count > 1 && a.Hex.Equals(garrisonHex))
                .ToList();
            if (armies.Count < 2)
                return null;

            ArmyData recipient = null;
            float lowestAverage = float.MaxValue;
            foreach (ArmyData army in armies)
            {
                if (!army.HasRoom)
                    continue;
                float average = AverageNonHeroStrength(army);
                if (average < lowestAverage)
                {
                    lowestAverage = average;
                    recipient = army;
                }
            }
            if (recipient == null)
                return null;

            ArmyData donor = null;
            float highestAverage = float.MinValue;
            foreach (ArmyData army in armies)
            {
                if (army == recipient || army.Members.Count(m => !m.IsHero) <= 1)
                    continue;
                float average = AverageNonHeroStrength(army);
                if (average > highestAverage)
                {
                    highestAverage = average;
                    donor = army;
                }
            }
            if (donor == null || !(highestAverage > lowestAverage))
                return null;

            UnitData weakestSpare = donor.Members.Where(m => !m.IsHero)
                .OrderBy(m => m.Defense).ThenBy(m => m.Attack).First();
            return CanAffordTransferInto(recipient, weakestSpare)
                ? new ConsolidationMove(donor, weakestSpare, recipient)
                : (ConsolidationMove?)null;
        }

        // See this class's own "Поведение" comment for the full tier order. A lone HERO folds
        // into garrison to wait for an escort a little differently from a lone plain unit —
        // FindHeroEscortFromGarrison above already tries to pull it OUT of garrison first, so by
        // the time the fold tier is even reached, "the hero has nowhere to pull FROM" already
        // holds for both compositions alike, and both are treated the same way here.
        //
        // FindArmyRebalance runs LAST, after every одиночка has already had its chance to fold or
        // pair — not 3rd, even though it could technically also resolve a lone army (the project
        // owner's own question: "почему 3 пунктом а не последним, после того как всех одиночек
        // распределили"). Running it earlier would let a still-unsettled одиночка skip straight to
        // being reinforced by whichever field army happens to be strongest, instead of first going
        // through the simpler, more deliberate fold/pair resolution those tiers exist for —
        // FindArmyRebalance's own scope is now restricted to armies of 2+ members for exactly this
        // reason, so it only ever compares already-settled formations against each other.
        //
        // `ctx` carries this AI turn's own oscillation guard (AiTurnContext.WouldRevisitArmy,
        // shared across categories — see that method's own comment) — every candidate below is
        // additionally checked against it before being returned, so no tier here ever proposes
        // sending a unit back to an army it already sat in earlier
        // THIS SAME turn. Without it, two tiers that pull in opposite directions (say, a pull tier
        // above and the lone-army fold-back below) can keep undoing each other's move every single
        // Decide() step — the exact same unit shuttling back and forth — burning the whole turn's
        // step budget without ever settling (the project owner's own report, previously seen via
        // the now-removed FindHeroPromotion tier, but the guard is kept general here since any
        // future tier added to this chain could reproduce the same shape of bug). A blocked
        // candidate simply falls through to the NEXT tier below, same as "this tier found nothing"
        // — each tier already computes its own candidate independently of the others, so skipping
        // one cyclical option here never hides a genuinely different, non-cyclical one further down.
        public static ConsolidationMove? FindReorgMove(PlayerSetupData player, HexCoord garrisonHex, ArmyData garrison, AiTurnContext ctx)
        {
            ConsolidationMove? heroEscort = FindHeroEscortFromGarrison(player, garrisonHex, garrison);
            if (heroEscort != null && !ctx.WouldRevisitArmy(heroEscort.Value.Unit, heroEscort.Value.Target))
                return heroEscort;

            ConsolidationMove? plainReinforce = FindPlainArmyReinforcement(player, garrisonHex, garrison);
            if (plainReinforce != null && !ctx.WouldRevisitArmy(plainReinforce.Value.Unit, plainReinforce.Value.Target))
                return plainReinforce;

            // No generic "bench hero takes over any room-having plain army" tier here on purpose —
            // the project owner's own spec: a hero leaves garrison only (1) to lead a spillover
            // army forced out by garrison overflow (FindGarrisonHeroToPromote, called directly from
            // AiManagementPlanner.SplitGarrisonArmyRoutine, not from here) or (2) to crew an army a
            // specific task is actually about to deploy (a Recce composition via
            // AiScoutPlanner.FindBuriedRecceUnit, a raid force via RaidWeakerArmyTask.FindRecruitAt,
            // an Экономика build trip via AiEconomyPlanner.FindNearestHeroAnywhere) — no other
            // reason. A used-to-exist FindHeroPromotion tier here promoted a bench hero into ANY
            // room-having plain army regardless of why that army existed, which is exactly what
            // grabbed a still-empty army AiScoutPlanner had just spawned "под Recce-состав" and
            // read it as a generic reserve slot — the hero then stood there alone, folded straight
            // back to garrison on the very next tier below, and got promoted right back in on the
            // step after that: an endless promote/fold loop that burned a whole turn's step budget
            // for nothing (the project owner's own report).

            // Garrison has nothing non-hero left to spare (both pulls above bailed on
            // StrongestNonHero returning null) — a lone army left over at this point genuinely has
            // nowhere to grow FROM garrison, so it folds back into garrison instead, but only with
            // real headroom to receive it without immediately overflowing again (see this class's
            // own "Поведение" comment on why the headroom check matters here).
            if (garrison != null && AiManagementPlanner.HasGarrisonDepositRoom(garrison))
            {
                // Weakest first (Defense, then Attack as the tiebreak) — same "слабых в гарнизон"
                // rule as everywhere else in this class. Picking whichever lone army happened to
                // enumerate first here would risk folding a strong stray (a tank sitting alone
                // between turns, say) back into garrison while a genuinely weak one stays outside
                // — exactly backwards from this whole class's own point.
                ArmyData loneArmy = ArmyRegistry.AllForOwner(player).Where(a => IsLoneArmyAtBase(a, garrisonHex))
                    .OrderBy(a => a.Members[0].Defense).ThenBy(a => a.Members[0].Attack)
                    .FirstOrDefault();
                if (loneArmy != null)
                {
                    UnitData unit = loneArmy.Members[0];
                    if (CanAffordTransferInto(garrison, unit) && !ctx.WouldRevisitArmy(unit, garrison))
                        return new ConsolidationMove(loneArmy, unit, garrison);
                }
            }

            // Garrison has nothing to spare AND no headroom to absorb a stray either — pairs two
            // lone armies together instead (preferring a hero-led one as the merge target, so
            // hero+escort forms the way a Unit card would otherwise have had to wait to do), still
            // ahead of FindArmyRebalance below — resolving одиночки into real formations comes
            // first, levelling those formations against each other comes only once that's settled.
            List<ArmyData> loneArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => IsLoneArmyAtBase(a, garrisonHex))
                .ToList();
            if (loneArmies.Count >= 2)
            {
                // Strongest first (Defense, then Attack) for BOTH target (when no hero exists to
                // anchor it) and merge source — a hero always leads if one's among the lone
                // armies, but the unit that actually JOINS it must be the strongest available
                // stray, not just whichever the registry happens to enumerate first. Without this,
                // a lone tank could sit untouched forever while two weak lone units merge into the
                // hero ahead of it purely by iteration order (the project owner's own "не будет
                // такого что две слабых одиночки присоединятся к герою а танк останется в
                // гарнизоне" question) — this makes sure it can't.
                ArmyData target = loneArmies.FirstOrDefault(AiArmyRoles.IsHeroLedCombatArmy)
                    ?? loneArmies.OrderByDescending(a => a.Members[0].Defense).ThenByDescending(a => a.Members[0].Attack).First();
                ArmyData mergeSource = loneArmies.Where(a => a != target)
                    .OrderByDescending(a => a.Members[0].Defense).ThenByDescending(a => a.Members[0].Attack)
                    .FirstOrDefault();
                if (mergeSource != null)
                {
                    UnitData mergeUnit = mergeSource.Members[0];
                    if (CanAffordTransferInto(target, mergeUnit) && !ctx.WouldRevisitArmy(mergeUnit, target))
                        return new ConsolidationMove(mergeSource, mergeUnit, target);
                }
            }

            // Every одиночка has now had its chance to fold or pair (or there simply aren't any
            // left) — only now do already-settled field armies (2+ members each) get compared
            // against each other and levelled (see FindArmyRebalance's own comment).
            ConsolidationMove? rebalance = FindArmyRebalance(player, garrisonHex);
            return rebalance != null && !ctx.WouldRevisitArmy(rebalance.Value.Unit, rebalance.Value.Target) ? rebalance : null;
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
