using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.Map;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // Development-lane strategic-admission fingerprint. The domain owner of the
    // Development key; the common readmission (StrategicReadmission) only compares keys.
    //
    // The key carries exactly what DevelopmentOpportunityEvaluator.Enumerate reads, so an
    // unchanged key provably cannot change a Development decision:
    //   * which resources are inside the investment window (DevelopmentInvestmentGate) —
    //     fixed for the turn once observed;
    //   * AP thresholds, resources and hand version;
    //   * the resource facts the ONE card price reads (StrategicCardEvaluator.ResourceCost via
    //     ScoreGeneratedEquipmentUpgrade / ScoreSurplus for PREPARE outputs): owner-aware
    //     spendable amount, per-turn income and this turn's verified starvation block;
    //   * facilities, offerings and bases;
    //   * every own army's composition (any unit may be the best Equipment recipient) and, for
    //     Research/Production operator armies, their position (remote-hero delivery cost);
    //   * actor occupancy (which heroes are free to travel to a facility);
    //   * the composition of every known threat EquipmentDeltaParts evaluates against;
    //   * ForceNeedModel.JustifiedForceNeed (asset threats, edge, idle-stock surplus) — the need
    //     every minted-output score is weighted by. Preparation has independent structural facts.
    internal static class DevelopmentAdmission
    {
        // AP retains both the exact pool and the known offering affordability thresholds:
        //   * per offering: ResearchProductionSystem.AttemptApCost(card)
        //     (the READY creation gate), and
        //   * per Unit/Equipment card in hand: CardData.EffectivePlayApCost.
        internal static string Fingerprint(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, int actionPoints,
            string resources, int handVersion, AiHandData hand = null, PlayerSetupData player = null,
            PlayerRoot root = null, AiTurnContext ctx = null) =>
            $"axis={DesireAxis.Development}"
            + $"|window={(snapshot != null ? DevelopmentInvestmentGate.OpenMask(player, snapshot.TurnNumber) : "----")}"
            + "|apfit=" + ApAffordability(snapshot, hand, actionPoints,
                root != null && ctx != null ? StrategicSpendability.SpendableAp(player, root, ctx) : (float?)null)
            + $"|res={resources}"
            + $"|price={PriceInputs(snapshot, player, root, ctx)}"
            + $"|operatorForecast={OperatorForecastFacts(snapshot, player)}"
            + $"|equipmentPriority={EquipmentUpgradeValuation.RecipientPriorityFacts(snapshot)}"
            + $"|hand={handVersion}"
            + $"|recipients={RecipientFacts(player, hand)}"
            // The need's exact inputs, not its value: computing the value runs the Monte Carlo
            // behind every known fight, which this "did anything change" key must not pay for.
            + $"|need={(snapshot != null ? ForceNeedModel.ChangeKey(snapshot) : "none")}"
            // Production's price of an output reads how few cards are left (deck + hand) and which
            // cards this player already made lately (DevelopmentDiversity).
            + $"|supply={(snapshot?.Self != null ? (snapshot.Self.Deck?.Count ?? 0) + (snapshot.Self.Hand?.Count ?? 0) : -1)}"
            + $"|repeat={DevelopmentDiversity.HistoryKey(player, snapshot?.TurnNumber ?? ctx?.TurnNumber ?? 0)}"
            + $"|{Facts(snapshot, activeIntents)}";

        // Everything one (equipment, recipient) verdict of DevelopmentOpportunityEvaluator reads:
        // legality, the signed delta, the purpose label and the pending-hand check. Unlike
        // Most recipients do not read prices. Fate now forecasts affordable operator outputs,
        // so its verdict also depends on AP, bank-aware stock, pending demand and repeat history.
        // On top of Facts/RecipientFacts it carries the live inputs the delta reads that the
        // admission key does not: own vision of known air contacts, own AA reaction state, unit
        // stealth/AA radius/permanent movement, member order (AA firing order), the strike roster.
        // The memory epoch and knowledge version keep a reused player or new knowledge apart.
        internal static string RecipientVerdictKey(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, PlayerSetupData player, AiHandData hand)
        {
            var live = new List<string>();
            var aaUnits = new List<Game.Units.UnitData>();
            if (player != null)
                foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
                {
                    if (a == null) continue;
                    live.Add($"a{a.Id}:" + string.Join(",", a.Members.Select(u => u == null ? "-"
                        : $"{u.RuntimeId}/{(u.IsHidden ? 1 : 0)}/{u.AntiAirRadius}/{u.PermanentMoveMax}")));
                    foreach (Game.Units.UnitData u in a.Members)
                        if (u != null && Game.Aviation.AntiAirRules.TryGetRadius(u, out _))
                            aaUnits.Add(u);
                }
            foreach (AiMapMemory.KnownEnemySighting s in snapshot?.Known?.EnemySightings
                         ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
            {
                if (!s.IsAir) continue;
                live.Add($"air{s.ArmyId}:{(snapshot.Own.IsVisible(s.Hex) ? 1 : 0)}:"
                    + string.Join("", aaUnits.Select(u =>
                        Game.Aviation.AntiAirState.CanReact(u, s.ArmyId) ? "1" : "0")));
            }
            string strike = string.Join(",", (snapshot?.Self?.StrikeRoster
                    ?? System.Array.Empty<StrikeRosterSlot>())
                .Select(r => r.Key).OrderBy(x => x, System.StringComparer.Ordinal));
            // The purpose lookup (EquipmentUpgradeValuation.FindEquipmentPurpose) reads the kind and
            // the held ground supports of every snapshot intent; Facts keys supports by activeIntents.
            string held = string.Join(";", (snapshot?.AllIntents ?? System.Array.Empty<MissionIntent>())
                .Where(i => i != null).OrderBy(i => i.IntentKey)
                .Select(i => $"{i.IntentKey}:{i.Kind}:"
                    + string.Join(",", GroundCombatLegs.HeldGroundSupportArmyIds(i).OrderBy(x => x))));
            return $"turn={snapshot?.TurnNumber}"
                + $"|operatorForecast={OperatorForecastFacts(snapshot, player)}"
                + $"|equipmentPriority={EquipmentUpgradeValuation.RecipientPriorityFacts(snapshot)}"
                + $"|held={held}"
                + $"|memory={AiMapMemory.RouteMemoryVersionFor(player)}"
                + $"|know={snapshot?.KnowledgeVersion}"
                + $"|recipients={RecipientFacts(player, hand)}"
                + $"|live={string.Join(";", live)}"
                + $"|strike={strike}"
                + $"|facAA={(snapshot?.Self?.HasFacilityAntiAir == true ? 1 : 0)}"
                + $"|{Facts(snapshot, activeIntents)}";
        }

        // 2026-11-15 (late-game perf, owner option A) — the part of an equipment delta's inputs
        // that is the same for every recipient (EquipmentDeltaCache CORE entries): the honest
        // opposition and its weather, the reference targets (deck, hand, own rosters without
        // positions), map and route knowledge, own vision of known air armies, facility AA.
        // Positions, rosters-as-recipients and purposes are in each recipient's stamp; prices,
        // AP and intents are not read by a CORE computation (a read marks the entry FULL).
        internal static string RecipientDeltaCoreKey(WorldSnapshot snapshot, PlayerSetupData player,
            AiHandData hand)
        {
            var hands = new List<string>();
            foreach (CardData c in hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
                if (c?.Definition != null) hands.Add(HandRow(c));
            hands.Sort(System.StringComparer.Ordinal);
            var rosters = new List<string>();
            foreach (ArmySnapshot a in snapshot?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                if (a != null) rosters.Add(DefenderFingerprint(a.Members) + "/" + DefenderFingerprint(a.MembersWithHeroes)
                    + "/ground=" + (a.Members?.Count(p => p.IsGroundCombatant) ?? 0));
            rosters.Sort(System.StringComparer.Ordinal);
            var air = new List<string>();
            var weather = new List<string>();
            foreach (AiMapMemory.KnownEnemySighting s in (snapshot?.Known?.EnemySightings
                         ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                     .Concat(snapshot?.Known?.NeutralSightings ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>()))
            {
                if (s.IsAir)
                    air.Add($"{s.ArmyId}:{s.Hex}:{(snapshot.Own != null && snapshot.Own.IsVisible(s.Hex) ? 1 : 0)}");
                weather.Add($"{s.Hex}:{Game.Combat.BattleEnvironment.At(s.Hex, snapshot.Map)}");
            }
            foreach (KnownEventGuardSnapshot g in snapshot?.Known?.EventGuards
                         ?? System.Array.Empty<KnownEventGuardSnapshot>())
                weather.Add($"{g.Hex}:{Game.Combat.BattleEnvironment.At(g.Hex, snapshot.Map)}");
            air.Sort(System.StringComparer.Ordinal);
            weather.Sort(System.StringComparer.Ordinal);
            return $"turn={snapshot?.TurnNumber}"
                + $"|known={KnownTargetsFacts(snapshot)}"
                + $"|threats={ThreatFacts(snapshot)}"
                + $"|bench={Benchmarks(snapshot?.Self?.Deck)}"
                + $"|hand={string.Join(";", hands)}"
                + $"|rosters={string.Join(";", rosters)}"
                + $"|route={RouteMapFacts(snapshot)}"
                + $"|memory={AiMapMemory.RouteMemoryVersionFor(player)}"
                + $"|air={string.Join(";", air)}"
                + $"|weather={string.Join(";", weather)}"
                + $"|facAA={(snapshot?.Self?.HasFacilityAntiAir == true ? 1 : 0)}";
        }

        // A deployed recipient's own inputs (EquipmentDeltaCache CORE entries): its army in
        // member order (AA firing order, allies, commander, activation AP), each member's row
        // and AA reaction state against every known air army, the army's route/movement state.
        // null = not an own army member: never reused.
        internal static string RecipientDeltaStamp(WorldSnapshot snapshot, Game.Units.UnitData unit,
            MissionIntent purpose)
        {
            ArmyData a = unit == null ? null : OwnWorld.Of(snapshot?.Observer).ArmyContaining(unit);
            if (a == null) return null;
            var airIds = new List<int>();
            foreach (AiMapMemory.KnownEnemySighting s in snapshot?.Known?.EnemySightings
                         ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                if (s.IsAir) airIds.Add(s.ArmyId);
            var sb = new System.Text.StringBuilder();
            sb.Append("host=").Append(unit.RuntimeId);
            ArmySnapshot actor = null;
            foreach (ArmySnapshot s in snapshot?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                if (s != null && s.ArmyId == a.Id) { actor = s; break; }
            sb.Append("|army=").Append(actor != null ? ArmyRow(actor) : "-");
            sb.Append($"|data={a.Hex}:{a.IsGarrison}:{a.IsPrison}:{a.IsAirfield}:{a.IsAirArmy}:{a.MaxMovement}");
            foreach (Game.Units.UnitData m in a.Members)
            {
                if (m == null) { sb.Append("|m=-"); continue; }
                sb.Append("|m=").Append(UnitRow(a, m))
                    .Append($":{(m.IsHidden ? 1 : 0)}:{m.AntiAirRadius}:{m.PermanentMoveMax}:");
                foreach (int id in airIds)
                    sb.Append(Game.Aviation.AntiAirState.CanReact(m, id) ? '1' : '0');
            }
            sb.Append("|purpose=").Append(PurposeFacts(snapshot, purpose));
            return sb.ToString();
        }

        // A hand-card recipient: its own row and purpose (no army, no route).
        internal static string RecipientDeltaStamp(WorldSnapshot snapshot, CardData card, MissionIntent purpose) =>
            card?.Definition == null ? null : HandRow(card) + "|purpose=" + PurposeFacts(snapshot, purpose);

        // Everything the efficiency core reads from a recipient's purpose: its kind, the honest
        // target (Attack/Raid site, Raid/ActiveDefence army, Scout focus) and the known defence
        // bonus of the target hex.
        private static string PurposeFacts(WorldSnapshot snapshot, MissionIntent i)
        {
            if (i == null) return "none";
            Game.HexGrid.HexCoord? hex = i.Attack?.Target.HasValue == true ? i.Attack.Target.Hex
                : i.Raid?.Target.HasValue == true ? i.Raid.TargetHex : (Game.HexGrid.HexCoord?)null;
            float bonus = hex.HasValue && snapshot?.Observer != null
                ? AiMapMemory.KnownHexDefenseBonusFor(snapshot.Observer, hex.Value,
                    i.Attack?.Target.HasValue == true ? i.Attack.Target.ExpectedOwner : null)
                : 0f;
            return $"{i.IntentKey}:{i.Kind}:{i.Status}:attack={i.Attack?.Target}:{i.Attack?.Phase}"
                + $":raid={i.Raid?.Target}:{i.Raid?.TargetHex}:{i.Raid?.TargetArmyId}"
                + $":def={i.ActiveDefence?.EnemyArmyId}"
                + $":scout={i.Scout?.Kind}:{i.Scout?.FocusHex}:{i.Scout?.RequiresStealth}"
                + $":hex={hex}:{bonus.ToString("R", CultureInfo.InvariantCulture)}";
        }

        internal static string OperatorForecastFacts(WorldSnapshot snap, PlayerSetupData player)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            var ctx = root == null ? null : new AiTurnContext { Map = snap?.Map, TurnNumber = snap?.TurnNumber ?? 0 };
            string budget = string.Join(",", ResourceBundle.All.Select(t =>
                $"{snap?.Economy?.SpendableStockpile.Get(t)}:{snap?.Self?.PerTurnIncome.Get(t)}:"
                + ActionPrice.PendingCardResourceDemand(snap, t).ToString("R", CultureInfo.InvariantCulture)));
            string sources = string.Join(",", (snap?.Development?.ForecastSources
                    ?? System.Array.Empty<GenerationStep>())
                .Select(g => $"{g.CardKey}:{g.CardDef?.authoredKey}:{g.ChallengeCard?.fate}:{g.Hero?.Fate}:{g.Hero?.FateMax}:{g.Hero?.RuntimeId}:{g.Hero?.Mutator?.authoredKey}:"
                    + $"{string.Join("/", g.Hero?.TypeTags.OrderBy(t => t) ?? System.Linq.Enumerable.Empty<UnitTypeTag>())}:"
                    + $"{string.Join("/", g.Hero?.Abilities.OrderBy(a => a, System.StringComparer.Ordinal) ?? System.Linq.Enumerable.Empty<string>())}:{g.Mode}:{g.FacilityHex}")
                .OrderBy(x => x, System.StringComparer.Ordinal));
            string airfields = player == null ? "" : string.Join(";",
                Game.Aviation.AviationRules.OwnedAirfieldHexes(player).OrderBy(h => h.Q).ThenBy(h => h.R)
                    .Select(h => $"{h}:{Game.Aviation.AviationRules.FreeAirfieldCapacity(h, player)}"));
            ApActionEconomySnapshot ap = snap?.Self?.ApEconomy;
            return $"{snap?.Self?.ActionPoints}:{snap?.Development?.ForecastActionPoints}:{ap?.BaseActionPoints}:{ap?.WitnessedApDemand}:"
                + $"{ap?.EstimatedArmyApDemand}:{ap?.EstimatedCardApDemand}:{budget}:{sources}:"
                + DevelopmentDiversity.HistoryKey(player, snap?.TurnNumber ?? 0)
                + $"|airfields={airfields}|path={snap?.MapPathingVersion}"
                + $"|missileStock={GroundCombatAirSupport.MissileStockFacts(snap, player)}"
                + $"|liveAp={(root == null ? "-" : StrategicSpendability.SpendableAp(player, root, ctx).ToString("R", CultureInfo.InvariantCulture))}"
                + $"|handCapacity={AiHandRegistry.Peek(player)?.HasFreeSlot}"
                + "|preservation=" + PriceInputs(snap, player, root, ctx);
        }

        // Per resource: spendable (stock net of other owners' holds), income, and the current
        // verified starvation block — every resource fact the canonical card price reads.
        internal static string PriceInputs(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx)
        {
            int turn = snapshot?.TurnNumber ?? ctx?.TurnNumber ?? 0;
            return string.Join(";", ResourceBundle.All.Select(t =>
            {
                string spend = ctx != null && root != null
                    ? StrategicSpendability.SpendableAmount(player, root, ctx, t)
                        .ToString("0.###", CultureInfo.InvariantCulture)
                    : "-";
                string income = snapshot?.Self != null
                    ? snapshot.Self.PerTurnIncome.Get(t).ToString("0.###", CultureInfo.InvariantCulture)
                    : "-";
                string block = ResourceStarvationRegistry.TryGetCurrentBlock(
                        player, t, turn, out ResourceBlockEvidence b)
                    ? string.Format(CultureInfo.InvariantCulture, "{0:0.###}/{1:0.###}/{2:0.###}",
                        b.Required, b.IncomePerTurn, b.DemandValue)
                    : "0";
                return $"{spend}:{income}:{block}";
            }));
        }

        // The known offering thresholds plus the exact AP pool needed by preparation.
        internal static string ApAffordability(WorldSnapshot snapshot, AiHandData hand,
            int actionPoints, float? spendableAp = null)
        {
            // Physical AP can stay unchanged when another owner's hold is released/placed.
            // Phase A revalidates against the bank, so its admission key must see that too.
            string pool = $"raw:{actionPoints}"
                + (spendableAp.HasValue
                    ? ":free:" + spendableAp.Value.ToString("R", CultureInfo.InvariantCulture)
                    : string.Empty);
            var thresholds = new List<int>();
            foreach (DevelopmentOffering off in snapshot?.Development?.Offerings
                         ?? (IReadOnlyList<DevelopmentOffering>)System.Array.Empty<DevelopmentOffering>())
                if (off.Card != null)
                    thresholds.Add(ResearchProductionSystem.AttemptApCost(off.Card));
            foreach (CardData c in hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
                if (c?.Definition != null && (c.Definition.cardType == CardType.Unit || c.Definition.cardType == CardType.Equipment))
                    thresholds.Add(c.EffectivePlayApCost);
            if (thresholds.Count == 0)
                return pool;   // nothing enumerable — never guess, keep both pool facts
            // Facility capacity, hero delivery and operator preparation have additional AP
            // thresholds. Retain the exact pool until every complete prerequisite is enumerated.
            return pool + ":" + string.Join("", thresholds.Distinct().OrderBy(x => x)
                .Select(x => actionPoints >= x ? "1" : "0"));
        }

        // Enumerate reads these live own recipients too. Occupancy alone can change legality
        // without changing power; equal aggregate stats do not identify the physical recipient.
        internal static string RecipientFacts(PlayerSetupData player, AiHandData hand)
        {
            var rows = new List<string>();
            foreach (CardData c in hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
            {
                if (c?.Definition == null) continue;
                rows.Add(HandRow(c));
            }
            if (player != null)
                foreach (var a in ArmyRegistry.AllForOwner(player))
                {
                    if (a == null) continue;
                    foreach (var u in a.Members)
                    {
                        if (u == null) continue;
                        rows.Add(UnitRow(a, u));
                    }
                }
            return string.Join(";", rows.OrderBy(x => x, System.StringComparer.Ordinal));
        }

        // One own hand card as a recipient (its projected stats, abilities, attachments, price).
        private static string HandRow(CardData c)
        {
            PredictedEquipmentState p = EquipmentSystem.Project(c);
            return $"h:{GenerationSource.StableCardKey(c)}:{c.Definition.authoredKey}:"
                + $"{c.EffectivePlayApCost}:{c.Equipment?.authoredKey}:{c.Mutator?.authoredKey}:"
                + $"{(c.Equipment != null ? 1 : 0)}:{(c.Mutator != null ? 1 : 0)}:"
                + string.Join(",", p.Stats.OrderBy(x => (int)x.Key).Select(x => $"{(int)x.Key}={x.Value}"))
                + ":" + string.Join(",", p.Abilities.OrderBy(x => x, System.StringComparer.Ordinal))
                + ":consume=" + EquipmentSystem.Consumption(c.Definition, c.Equipment, c.Mutator)?.Key;
        }

        // One live own unit as a recipient, with the army facts its legality reads.
        private static string UnitRow(ArmyData a, Game.Units.UnitData u) =>
            $"u:{u.RuntimeId}:{a.Id}:{a.IsPrison}:{u.IsPrisoner}:{u.IsHero}:"
            + $"{a.Hex}:{a.IsGarrison}:{a.IsAirfield}:{a.IsAirArmy}:{u.IsAviation}:{u.IsSummoned}:{StrikeRoster.UnitKey(u)}:"
            + $"{u.Equipment != null}:{u.Mutator != null}:"
            + $"{u.Equipment?.authoredKey}:{u.Mutator?.authoredKey}:{u.Attack}:{u.Defense}:"
            + $"{u.Range}:{u.HitPointsMax}:{u.HitPointsCurrent}:"
            + $"{u.MoveMax}:{u.Initiative}:{u.ActivationApCost}:"
            + $"{u.CommandRating}:{u.Fate}:{u.FateMax}:"
            + string.Join(",", u.Abilities.OrderBy(x => x, System.StringComparer.Ordinal))
            + ":" + string.Join(",", u.TypeTags.OrderBy(x => (int)x))
            + ":consume=" + EquipmentSystem.Consumption(u)?.Key;

        internal static string Facts(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            DevelopmentReadiness rd = snapshot?.Development;
            string facilities = string.Join(";", (rd?.Facilities
                    ?? System.Array.Empty<DevelopmentFacility>())
                .OrderBy(f => f.Hex.Q).ThenBy(f => f.Hex.R).ThenBy(f => (int)f.Mode)
                .Select(f => $"{f.Hex.Q},{f.Hex.R}:{(int)f.Mode}:{(f.HasHero ? 1 : 0)}:"
                    + $"{(f.Contested ? 1 : 0)}:{f.HeroFate}:{f.HeroCommandRating}"));
            string offerings = string.Join(";", (rd?.Offerings
                    ?? System.Array.Empty<DevelopmentOffering>())
                .OrderBy(o => o.FacilityHex.Q).ThenBy(o => o.FacilityHex.R)
                .ThenBy(o => (int)o.Mode)
                .ThenBy(o => o.Card?.authoredKey ?? o.Card?.displayName,
                    System.StringComparer.Ordinal)
                .Select(o => $"{o.FacilityHex.Q},{o.FacilityHex.R}:{(int)o.Mode}:"
                    + $"{o.Card?.authoredKey ?? o.Card?.displayName}:{o.SuccessChance:0.###}:"
                    + $"{(o.ProducesEquipment ? 1 : 0)}:{o.StakeCost.Human:0.###},"
                    + $"{o.StakeCost.Energy:0.###},{o.StakeCost.Materials:0.###},"
                    + $"{o.StakeCost.Tech:0.###}"));
            // Every army is a possible Equipment recipient, so every roster is exact (WorthIt reads
            // each profile individually — aggregates could hide a changed verdict). Operator
            // delivery and an assigned scout's attachment utility also read position/route state.
            string armies = string.Join(";", (snapshot?.Self?.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).OrderBy(a => a.ArmyId)
                .Select(ArmyRow));
            string bases = string.Join(";", (snapshot?.Self?.BaseHexes
                    ?? System.Array.Empty<Game.HexGrid.HexCoord>())
                .OrderBy(h => h.Q).ThenBy(h => h.R).Select(h =>
                {
                    // Level and a free unlocked slot decide whether a facility stage must first
                    // buy a capacity tier (StrategicMaintenancePolicy.CapacityUnlockTierAt).
                    BuildingData b = BuildingRegistry.FindAt(h);
                    return b == null ? $"{h.Q},{h.R}"
                        : $"{h.Q},{h.R}:{b.Level}:{(b.FindFirstAvailableFacilitySlot() >= 0 ? 1 : 0)}"
                            + $":own={b.Owner == snapshot?.Observer}:base={b.IsBase}"
                            + $":contested={Game.Combat.BattleInitiator.FindEnemyAt(h, snapshot?.Observer) != null}";
                }));
            // Actor occupancy: preparation builds ActorCommitments over ALL intents, so each kind
            // contributes only the inputs that produce a claim — never intent identity.
            string claims = string.Join(";", (activeIntents ?? new List<MissionIntent>())
                .Where(i => i != null)
                .SelectMany(i =>
                {
                    var rows = new List<string>();
                    string k = $"{i.Kind}:{i.Status}:{i.DisplacementValue.ToString("R", CultureInfo.InvariantCulture)}";
                    if (i.PreferredMoverArmyId.HasValue)
                        rows.Add($"{k}:{i.PreferredMoverArmyId.Value}");
                    if (i.Raid?.AirSupportArmyId != null)
                        rows.Add($"{k}:air{i.Raid.AirSupportArmyId.Value}:{(int)i.Raid.Phase}");
                    // Held ground supports — the same list ActorCommitments claims.
                    foreach (int supportId in GroundCombatLegs.HeldGroundSupportArmyIds(i))
                        rows.Add($"{k}:sup{supportId}");
                    if (i.Economy?.BuilderArmyId != null)
                        rows.Add($"{k}:bld{i.Economy.BuilderArmyId.Value}");
                    if (i.Economy?.CollectorArmyId != null)
                        rows.Add($"{k}:col{i.Economy.CollectorArmyId.Value}");
                    if (i.Development?.Hero != null)
                        rows.Add($"{k}:dev{i.Development.HeroKey}");
                    if (rows.Count == 0)
                        rows.Add(k);
                    return rows;
                })
                .Distinct().OrderBy(x => x, System.StringComparer.Ordinal));
            // EquipmentOpposition's composition fallback: enemy army compositions (composition only crosses
            // the TrueWorld boundary), remembered neutral defenders and known event guards.
            // Hidden positions stay excluded; honest target positions are in knownTargets below.
            string threats = ThreatFacts(snapshot);
            string purposes = string.Join(";", (snapshot?.AllIntents
                    ?? System.Array.Empty<MissionIntent>()).Where(i => i != null)
                .OrderBy(i => i.IntentKey).Select(i =>
                    $"{i.IntentKey}:{i.Status}:actor={i.PreferredMoverArmyId}:life={i.IsLifecycleLeg}:"
                    + $"attack={i.Attack?.Phase}:{i.Attack?.Preparation}:{i.Attack?.Target}:"
                    + string.Join(",", (i.Attack?.TargetRoster ?? new List<StrikeRosterSlot>())
                        .Select(r => r.Key).OrderBy(x => x, System.StringComparer.Ordinal))
                    + ":gather=" + string.Join(",", (i.Attack?.GatherSupportArmyIds ?? new List<int>()).OrderBy(x => x))
                    + $":devHero={i.Development?.Hero?.RuntimeId}:scout={i.Scout?.Kind}:{i.Scout?.RequiresStealth}:{i.Scout?.FocusHex}:raid={i.Raid?.Target}:def={i.ActiveDefence?.EnemyArmyId}"));
            string benchmarks = Benchmarks(snapshot?.Self?.Deck);
            string knownTargets = KnownTargetsFacts(snapshot);
            // Raw inputs of the shared Scout route/vantage owner; no route or combat calculation
            // in the cache key. Terrain changes use the snapshot's pathing version. Set order is
            // irrelevant, while coverage and either kind of arrival block can change utility.
            string routeMap = RouteMapFacts(snapshot);
            string trimmedScouts = snapshot?.Observer == null ? "-" : string.Join(",",
                ReconTurnStateStore.For(snapshot.Observer, snapshot.TurnNumber)
                    .ReconActorsTrimmedThisTurn(snapshot.TurnNumber).OrderBy(x => x));
            return $"fac={facilities}|off={offerings}|bases={bases}|armies={armies}|claims={claims}"
                + $"|threats={threats}|purposes={purposes}|benchmarks={benchmarks}|knownTargets={knownTargets}"
                + $"|routeMap={routeMap}|trimmedScouts={trimmedScouts}"
                + $"|operatorClaims={snapshot?.Intents?.Development.GeneratedOperatorFacts(snapshot?.TurnNumber ?? 0)}"
                + $"|prepSite={rd?.ResearchPreparationSite}:{rd?.ProductionPreparationSite}"
                + $"|mobilization={AttackForceReadiness.MobilizationOpen(snapshot?.Self)}"
                + $"|ready={(rd?.AnyFacilityWithHero == true ? 1 : 0)}:"
                + $"{(rd?.AnyOperatorlessFacility == true ? 1 : 0)}:"
                + $"{(rd?.ResearcherCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.AssemblerCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.DevPathViable == true ? 1 : 0)}:prep={(rd?.HasPreparationStep == true ? 1 : 0)}:{rd?.PreparationHeadroom ?? 0f}:modes={rd?.ResearchPreparationViable}:{rd?.ProductionPreparationViable}:targets={rd?.UpgradeTargetCount ?? 0}:"
                + $"{(rd?.BestSuccessChance ?? 0f):0.###}";
        }

        // One own army snapshot: roster, commander, Mutator slots, route/movement state.
        private static string ArmyRow(ArmySnapshot a)
        {
            bool developmentOperator = a.HasResearchOperator || a.HasProductionOperator;
            string operatorState = developmentOperator
                ? $":operator={a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:"
                  + $"{(a.HasActivatedThisTurn ? 1 : 0)}:{(a.IsGarrison ? 1 : 0)}"
                : string.Empty;
            return $"{a.ArmyId}:{a.MemberCount}:{(a.HasHero ? 1 : 0)}:"
                + $"{(a.HasResearchOperator ? 1 : 0)}:{(a.HasProductionOperator ? 1 : 0)}:"
                + $"roster={DefenderFingerprint(a.MembersWithHeroes)}"
                + $":commander={CommanderFingerprint(a.Commander)}"
                + $":mutators={string.Join(",", (a.NonHeroMutatorOccupied ?? System.Array.Empty<bool>()).Select(x => x ? "1" : "0"))}"
                + $":route={a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:{a.MaxMovement}:{a.ActivationApCost}:"
                + $"{a.HasActivatedThisTurn}:{a.IsSoloRecce}:{a.IsPrison}:{a.IsAir}:{a.IsHidden}:{a.CanEnterStealth}:{a.EffectiveVisionRadius}"
                + operatorState;
        }

        // EquipmentOpposition's composition fallback rows (see Facts).
        private static string ThreatFacts(WorldSnapshot snapshot)
        {
            IEnumerable<string> threatRows = (snapshot?.TrueWorld?.EnemyArmies
                    ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a?.Members != null && a.Members.Count > 0)
                .Select(a => "e" + DefenderFingerprint(a.Members) + CommanderFingerprint(a.Commander))
                .Concat((snapshot?.Known?.NeutralSightings
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Where(x => x.Defenders != null && x.Defenders.Count > 0)
                    .Select(x => "n" + DefenderFingerprint(x.Defenders) + CommanderFingerprint(x.Commander)))
                .Concat((snapshot?.Known?.EventGuards
                        ?? System.Array.Empty<KnownEventGuardSnapshot>())
                    .Where(g => g.Defenders != null && g.Defenders.Count > 0)
                    .Select(g => "g" + DefenderFingerprint(g.Defenders) + CommanderFingerprint(g.Commander)));
            return string.Join(";", threatRows.OrderBy(x => x, System.StringComparer.Ordinal));
        }

        // Honest positions and profiles of every known enemy/neutral army and event guard.
        private static string KnownTargetsFacts(WorldSnapshot snapshot) =>
            string.Join(";", (snapshot?.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snapshot?.Known?.NeutralSightings ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Select(x => $"{x.ArmyId}:{x.Hex}:recce={x.RecceRadius},{x.RecceSpotStrength}:"
                    + DefenderFingerprint(x.Defenders) + CommanderFingerprint(x.Commander))
                .Concat((snapshot?.Known?.EventGuards ?? System.Array.Empty<KnownEventGuardSnapshot>())
                    .Select(g => $"guard:{g.Hex}:" + DefenderFingerprint(g.Defenders)))
                .OrderBy(x => x, System.StringComparer.Ordinal));

        // Raw inputs of the shared Scout route/vantage owner and of route costs (see Facts).
        private static string RouteMapFacts(WorldSnapshot snapshot)
        {
            MapKnowledgeSnapshot map = snapshot?.MapKnowledge;
            string routeMap = $"{snapshot?.Map != null}:{snapshot?.MapPathingVersion}:"
                + (map?.ExplorableUnknownFrac.ToString("R", CultureInfo.InvariantCulture) ?? "-")
                + ":hexes=" + string.Join(";", (map?.AllHexes ?? System.Array.Empty<Game.HexGrid.HexCoord>())
                    .Select(h => $"{h.Q},{h.R}").OrderBy(x => x, System.StringComparer.Ordinal))
                + ":hard=" + string.Join(";", (map?.ScoutHardBlockedHexes ?? new HashSet<Game.HexGrid.HexCoord>())
                    .Select(h => $"{h.Q},{h.R}").OrderBy(x => x, System.StringComparer.Ordinal))
                + ":visible=" + string.Join(";", (map?.VisibleArrivalBlockedHexes ?? new HashSet<Game.HexGrid.HexCoord>())
                    .Select(h => $"{h.Q},{h.R}").OrderBy(x => x, System.StringComparer.Ordinal));
            return routeMap;
        }

        // EXACT, order-stable digest of a combat roster. Aggregates (Count plus Attack/Defense/HP/
        // Initiative sums) are strictly weaker than what the cached decision depends on: WorthIt
        // reads each profile INDIVIDUALLY — per-defender Defense, CeramicArmor, ability list, unit
        // type tags, current AND max HP, and Initiative — so two genuinely different rosters can
        // share every aggregate while giving different WorthIt answers.
        //
        // Every field WorthIt reads is emitted verbatim; nothing is hashed (a long-lived key must
        // not be built on GetHashCode). Per-profile rows are sorted ordinally so a
        // pure REORDERING of the same roster keeps the same key: WorthIt orders combat by
        // Initiative, never by list position, so order carries no information here.
        private static string DefenderFingerprint(IReadOnlyList<Game.Combat.WorthIt.DefenderProfile> defenders)
        {
            if (defenders == null || defenders.Count == 0)
                return "0";
            // The same roster list holding the same profiles (Equals) has the same digest.
            RosterMemo memo = s_rosters.GetValue(defenders, _ => new RosterMemo());
            lock (memo)
            {
                if (memo.Digest != null && memo.Profiles.Length == defenders.Count)
                {
                    bool same = true;
                    for (int i = 0; i < defenders.Count && same; i++)
                        same = memo.Profiles[i].Equals(defenders[i]);
                    if (same) return memo.Digest;
                }
                memo.Profiles = defenders.ToArray();
                return memo.Digest = BuildDefenderFingerprint(defenders);
            }
        }

        private sealed class RosterMemo
        {
            internal Game.Combat.WorthIt.DefenderProfile[] Profiles;
            internal string Digest;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RosterMemo> s_rosters =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, RosterMemo>();

        private static string BuildDefenderFingerprint(IReadOnlyList<Game.Combat.WorthIt.DefenderProfile> defenders)
        {
            var rows = new List<string>(defenders.Count);
            foreach (Game.Combat.WorthIt.DefenderProfile d in defenders)
                rows.Add(DefenderRow(d));
            rows.Sort(System.StringComparer.Ordinal);
            return $"{defenders.Count}:" + string.Join("|", rows);
        }

        // The deck's projected reference profiles read only these definition fields
        // (EquipmentSystem.Project with no attachment: DefinitionStats + granted abilities). The
        // text is reused while the deck holds the same definitions in the same order AND each
        // still carries the same field values, so an in-place edit is still seen.
        private readonly struct DefinitionFacts
        {
            private readonly Game.Cards.CardDefinition _def;
            private readonly Game.Cards.CardType _type;
            private readonly int _attack, _defense, _range, _hp, _move, _initiative, _activationAp, _command, _fate;
            private readonly Game.Cards.UnitTypeTag[] _tags;
            private readonly string[] _abilities;

            internal DefinitionFacts(Game.Cards.CardDefinition d)
            {
                _def = d;
                _type = d?.cardType ?? default;
                _attack = d?.attack ?? 0; _defense = d?.defenseRating ?? 0; _range = d?.range ?? 0;
                _hp = d?.hitPoints ?? 0; _move = d?.moveMax ?? 0; _initiative = d?.initiative ?? 0;
                _activationAp = d?.activationApCost ?? 0; _command = d?.commandRating ?? 0; _fate = d?.fate ?? 0;
                _tags = d?.unitTypeTags?.ToArray();
                _abilities = d?.grantedAbilities?.ToArray();
            }

            internal bool Matches(Game.Cards.CardDefinition d) =>
                ReferenceEquals(_def, d) && (d == null
                    || _type == d.cardType && _attack == d.attack && _defense == d.defenseRating
                    && _range == d.range && _hp == d.hitPoints && _move == d.moveMax
                    && _initiative == d.initiative && _activationAp == d.activationApCost
                    && _command == d.commandRating && _fate == d.fate
                    && SameItems(_tags, d.unitTypeTags) && SameItems(_abilities, d.grantedAbilities));

            private static bool SameItems<T>(T[] captured, List<T> current)
            {
                if (captured == null || current == null) return captured == null && current == null;
                if (captured.Length != current.Count) return false;
                var eq = EqualityComparer<T>.Default;
                for (int i = 0; i < captured.Length; i++)
                    if (!eq.Equals(captured[i], current[i])) return false;
                return true;
            }
        }

        private sealed class BenchmarkMemo
        {
            internal DefinitionFacts[] Deck;
            internal string Value;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BenchmarkMemo> s_benchmarks =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, BenchmarkMemo>();

        private static string Benchmarks(IReadOnlyList<Game.Cards.CardDefinition> deck)
        {
            deck = deck ?? System.Array.Empty<Game.Cards.CardDefinition>();
            BenchmarkMemo memo = s_benchmarks.GetValue(deck, _ => new BenchmarkMemo());
            lock (memo)
            {
                if (memo.Value != null && memo.Deck.Length == deck.Count)
                {
                    bool same = true;
                    for (int i = 0; i < deck.Count && same; i++)
                        same = memo.Deck[i].Matches(deck[i]);
                    if (same) return memo.Value;
                }
                memo.Deck = deck.Select(d => new DefinitionFacts(d)).ToArray();
                memo.Value = string.Join(";", deck.Where(d => d != null)
                    .Select(d => d.cardType + ":" + string.Join(",", EquipmentSystem.Project(d, null, null).Stats
                            .OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"))
                        + ":" + string.Join(",", d.unitTypeTags ?? new List<Game.Cards.UnitTypeTag>())
                        + ":" + string.Join(",", MaterializationChainMatching.EffectiveAbilities(d, null)
                            .OrderBy(x => x, System.StringComparer.Ordinal)))
                    .OrderBy(x => x, System.StringComparer.Ordinal));
                return memo.Value;
            }
        }

        private static string DefenderRow(Game.Combat.WorthIt.DefenderProfile d) =>
            ProfileRowCache.Get(d, ProfileRowCache.Format.DevelopmentAdmission, BuildDefenderRow);

        private static string BuildDefenderRow(Game.Combat.WorthIt.DefenderProfile d)
        {
            string tags = d.TypeTags == null ? string.Empty
                : string.Join(",", d.TypeTags.Select(t => ((int)t).ToString(CultureInfo.InvariantCulture))
                    .OrderBy(t => t, System.StringComparer.Ordinal));
            string abilities = d.Abilities == null ? string.Empty
                : string.Join(",", d.Abilities.Where(x => x != null)
                    .OrderBy(x => x, System.StringComparer.Ordinal));
            return $"{d.Attack:0.###}/{d.Defense:0.###}/{d.HitPoints:0.###}/"
                + $"{d.MaxHitPoints:0.###}/{d.Initiative}/{(d.HasCeramicArmor ? 1 : 0)}/r{d.Range}/"
                + $"[{tags}]/[{abilities}]/consume={d.Consumption?.Key}/singleUnit={d.SingleUseUnit}/disruptEligible={d.CanDisruptBuilding}";
        }

        private static string CommanderFingerprint(Game.Combat.WorthIt.SideCommander commander)
            => $"/cmd:{commander.Present}:{commander.Initiative}:{commander.Fate}:"
                + DefenderFingerprint(commander.SupportHeroes);
    }

    // Exact text rows of DefenderProfiles for the "did anything change" keys. A profile owns
    // private read-only copies of its lists, so the identity of its Abilities list names one
    // constructed profile, and its Equals (fields, lists by identity) proves a remembered row
    // still describes it. Sightings keep their profiles across snapshots and every key of one
    // pass reads the same ones: each row is formatted once instead of once per key.
    internal static class ProfileRowCache
    {
        internal enum Format { DevelopmentAdmission, MissileStock, Count }

        private sealed class Rows
        {
            internal Game.Combat.WorthIt.DefenderProfile Profile;
            internal readonly string[] Text = new string[(int)Format.Count];
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Rows> s_rows =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, Rows>();

        internal static string Get(Game.Combat.WorthIt.DefenderProfile profile, Format format,
            System.Func<Game.Combat.WorthIt.DefenderProfile, string> build)
        {
            if (profile.Abilities == null)
                return build(profile);
            Rows rows = s_rows.GetValue(profile.Abilities, _ => new Rows());
            lock (rows)
            {
                if (!rows.Profile.Equals(profile))
                {
                    rows.Profile = profile;
                    System.Array.Clear(rows.Text, 0, rows.Text.Length);
                }
                return rows.Text[(int)format] ?? (rows.Text[(int)format] = build(profile));
            }
        }
    }
}

