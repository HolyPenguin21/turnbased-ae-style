using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // Development-lane strategic-admission fingerprint. A mechanical partial of Pipeline,
    // not a second admission owner: RunTurn (AiStrategyV2Pipeline.cs) is still the only
    // caller, through DevelopmentAdmissionFingerprint.

    public static partial class Pipeline
    {
        // AI-02 — AP no longer enters this fingerprint as a raw number. Development consults the
        // AP pool through exactly two affordability predicates, both of the form
        // `root.CanSpendActionPoints(x)` == `ActionPoints >= x`:
        //   * per offering: ResearchProductionSystem.AttemptApCost(card) + card.activationApCost
        //     (DevelopmentOpportunityEvaluator.Enumerate's Challenge+attach gate), and
        //   * per Unit card in hand: CardData.EffectivePlayApCost
        //     (BestAffordableHandUnitPower, which feeds AlternativeValue and therefore EV ordering).
        // Emitting which of those thresholds the current AP clears is therefore EXACTLY as
        // discriminating as the raw number, and an AP delta that crosses none of them provably
        // cannot change any Development decision. Resources are deliberately NOT narrowed: they
        // shift BestAffordableHandUnitPower's displaced-alternative term continuously, so any
        // resource change can reorder EV — correctness over savings, as required.
        internal static string DevelopmentAdmissionFingerprint(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, int actionPoints,
            string resources, int handVersion, AiHandData hand = null) =>
            $"axis={DesireAxis.Development}|apfit={DevelopmentApAffordability(snapshot, hand, actionPoints)}"
            + $"|res={resources}"
            + $"|hand={handVersion}|{DevelopmentAdmissionFacts(snapshot, activeIntents)}";

        // The complete, ordered set of AP thresholds Development can cross (see above).
        internal static string DevelopmentApAffordability(WorldSnapshot snapshot, AiHandData hand,
            int actionPoints)
        {
            var thresholds = new List<int>();
            foreach (DevelopmentOffering off in snapshot?.Development?.Offerings
                         ?? (IReadOnlyList<DevelopmentOffering>)System.Array.Empty<DevelopmentOffering>())
                if (off.Card != null)
                    thresholds.Add(ResearchProductionSystem.AttemptApCost(off.Card)
                        + UnityEngine.Mathf.Max(0, off.Card.activationApCost));
            foreach (CardData c in hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
                if (c?.Definition != null && c.Definition.cardType == CardType.Unit)
                    thresholds.Add(c.EffectivePlayApCost);
            if (thresholds.Count == 0)
                return $"raw:{actionPoints}";   // nothing enumerable — never guess, keep the raw fact
            return string.Join("", thresholds.Distinct().OrderBy(x => x)
                .Select(x => actionPoints >= x ? "1" : "0"));
        }

        // Development's actor dependency is narrower than the operational Actor invalidation.
        // Every army movement must still wake Recon/Aggression, but only a Researcher/Assembler
        // move can change operator delivery. Composition/capability remains represented for every
        // army because equipment recipient selection genuinely depends on it.
        internal static string DevelopmentAdmissionFacts(WorldSnapshot snapshot,
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
            // AI-02 — only an army that can actually HOST a need-supporting equipment recipient
            // (AI-03's finalized dependency set) contributes composition/combat detail; every other
            // army contributes only its identity, so an unrelated army's stat change no longer
            // forces a full Development re-enumeration. The pre-existing operator-position
            // optimization is preserved verbatim for Research/Production operator armies.
            //
            // Composition and position are tracked SEPARATELY because they answer different proofs:
            // Economy's delivery proof (AI-03) compares an army's route and shared movement
            // bottleneck before/after a grant, so an Economy mover's position/movement/activation
            // has to invalidate Development. Raid's combat proof (WorthIt via
            // ImprovesGroundCombatOutcome) reads army.Members composition but never position. Recon's
            // proof (ImprovesReconCapability) reads only the OFFERED equipment/recipient card's own
            // static abilities/MoveMax/ActivationApCost — never anything about the scout army
            // itself — so a Scout-only mover needs neither block: a plain scout patrolling its
            // waypoint must not force a full re-enumeration on every step.
            var positionRelevantArmyIds = DevelopmentEconomyRelevantArmyIds(snapshot, activeIntents);
            var raidRelevantArmyIds = DevelopmentRaidRelevantArmyIds(activeIntents);
            string armies = string.Join(";", (snapshot?.Self?.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).OrderBy(a => a.ArmyId)
                .Select(a =>
                {
                    bool developmentOperator = a.HasResearchOperator || a.HasProductionOperator;
                    string operatorState = developmentOperator
                        ? $":operator={a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:"
                          + $"{(a.HasActivatedThisTurn ? 1 : 0)}:{(a.IsGarrison ? 1 : 0)}"
                        : string.Empty;
                    // Operator armies keep exactly the pre-existing full block (composition +
                    // position) regardless of the narrowed sets below — that optimization is
                    // untouched by this pass.
                    bool wantsPosition = developmentOperator || positionRelevantArmyIds.Contains(a.ArmyId);
                    bool wantsComposition = wantsPosition || raidRelevantArmyIds.Contains(a.ArmyId);
                    if (!wantsComposition && !wantsPosition)
                        return a.ArmyId.ToString(CultureInfo.InvariantCulture);
                    // The ATTACKING side of the same proof needs the same precision as
                    // the defending side: ImprovesGroundCombatOutcome builds `before`/`after` from
                    // this army's individual non-hero members (WorthIt.FromLiveUnit each) and
                    // replaces exactly one of them with the equipped projection. Aggregates alone
                    // (MemberCount/AttackSum/DefenseSum/power/quality) cannot distinguish two
                    // rosters whose per-unit coverage, abilities or initiative order differ, so a
                    // relevant recipient-side change could go unnoticed. `a.Members` is already the
                    // non-hero profile list this proof iterates — the same canonical serialization
                    // as the defender side, no second representation. StrategicCoverage contributes
                    // its lossless bitmask: a long-lived key must never rest on a hash.
                    string composition = wantsComposition
                        ? $":{a.MemberCount}:{(a.HasHero ? 1 : 0)}:"
                          + $"{a.AttackSum:0.###}:{a.DefenseSum:0.###}:"
                          + $"{a.EffectiveArmyPower:0.###}:{a.CompositionQuality:0.###}:"
                          + $"{a.Capacity}:{a.OccupiedBattleSlots}:{a.StrategicCoverage.Bits}:"
                          + $"{(a.HasResearchOperator ? 1 : 0)}:{(a.HasProductionOperator ? 1 : 0)}:"
                          + $"roster={DefenderFingerprint(a.Members)}"
                        : string.Empty;
                    // Position/movement/activation are part of the recipient facts now: AI-03's
                    // delivery proof compares this army's route and shared movement bottleneck
                    // before/after the grant, so they can change the admission answer.
                    string position = wantsPosition
                        ? $":at={a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:{a.MaxMovement}:"
                          + $"{a.ActivationApCost}:{(a.HasActivatedThisTurn ? 1 : 0)}:"
                          + $"{a.CollectionCapacity.Human:0.###},{a.CollectionCapacity.Energy:0.###},"
                          + $"{a.CollectionCapacity.Materials:0.###},{a.CollectionCapacity.Tech:0.###}"
                        : string.Empty;
                    return $"{a.ArmyId}{composition}{position}{operatorState}";
                }));
            string bases = string.Join(";", (snapshot?.Self?.BaseHexes
                    ?? System.Array.Empty<Game.HexGrid.HexCoord>())
                .OrderBy(h => h.Q).ThenBy(h => h.R).Select(h => $"{h.Q},{h.R}"));
            // AI-02 — the old field was `{IntentKey}:{Kind}:{Status}:{PreferredMoverArmyId}` for
            // EVERY intent, so any unrelated mission retargeting (a new IntentKey for the same
            // work), retiring or being created rewrote it and forced a full re-enumeration — the
            // single biggest source of the 571/88 repeated NO-recipient passes. Development reads
            // intents through exactly two channels, and each now contributes only its own facts:
            //
            //  (a) ACTOR OCCUPANCY. DevelopmentOpportunityEvaluator.EnumeratePreparation builds
            //      ActorCommitments.FromIntents over ALL intents, so every kind still has to be
            //      represented — but only through the inputs that produce a claim (kind, status
            //      and the claimed actor ids), never through intent identity. Two different intent
            //      keys that occupy the same actors are, to Development, the same world.
            string claims = string.Join(";", (activeIntents ?? new List<MissionIntent>())
                .Where(i => i != null)
                .SelectMany(i =>
                {
                    var rows = new List<string>();
                    string k = $"{i.Kind}:{i.Status}";
                    if (i.PreferredMoverArmyId.HasValue)
                        rows.Add($"{k}:{i.PreferredMoverArmyId.Value}");
                    if (i.Raid?.SupportArmyId != null)
                        rows.Add($"{k}:sup{i.Raid.SupportArmyId.Value}:{(int)i.Raid.Phase}");
                    if (i.Raid?.AirSupportArmyId != null)
                        rows.Add($"{k}:air{i.Raid.AirSupportArmyId.Value}:{(int)i.Raid.Phase}");
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
            //  (b) SUPPORTED NEED. Only an Economy obligation, a Scout mover or an
            //      Assault/Reinforcement Raid primary can witness a need
            //      (DemandLayer.HasSupportedDevelopmentAxisDemand), and each only through the
            //      fields the proof actually reads.
            string owners = string.Join(";", (activeIntents ?? new List<MissionIntent>())
                .Where(DevelopmentRelevantIntent)
                .Select(i =>
                {
                    string extra = string.Empty;
                    if (i.Kind == MissionKind.Economy && i.Economy != null)
                        // The specific economic obligation AI-03 proves against.
                        extra = $":{(int)i.Economy.Kind}:{i.Economy.TargetHex.Q},{i.Economy.TargetHex.R}"
                            + $":{(i.Economy.ResourceType.HasValue ? ((int)i.Economy.ResourceType.Value).ToString(CultureInfo.InvariantCulture) : "-")}"
                            + $":{i.Economy.BuilderArmyId}:{i.Economy.CollectorArmyId}"
                            + $":{(i.Economy.SafeReturnHex.HasValue ? $"{i.Economy.SafeReturnHex.Value.Q},{i.Economy.SafeReturnHex.Value.R}" : "-")}";
                    else if (i.Kind == MissionKind.Raid && i.Raid != null)
                        // The exact target/combat-viability feed into ImprovesGroundCombatOutcome.
                        // A Scout intent contributes nothing beyond its mover: ImprovesReconCapability
                        // reads only the recipient's own abilities, never the scout's target.
                        extra = $":{(int)i.Raid.Phase}:{i.Raid.PrimaryArmyId}"
                            + $":{i.Raid.LastKnownHex.Q},{i.Raid.LastKnownHex.R}"
                            + $":{DefenderFingerprint(AiV2Util.KnownDefenders(snapshot, i.Raid.Target))}";
                    return $"{i.Kind}:{i.PreferredMoverArmyId}{extra}";
                })
                .Distinct().OrderBy(x => x, System.StringComparer.Ordinal));
            // AI-03's economy admission reads the site's own income physics (extraction proof) and
            // the remembered sightings on the mission's route (protection proof), so those facts
            // must invalidate Development too — they did not before, which was a staleness bug in
            // the other direction. Own-force and remembered-sighting facts only; nothing hidden.
            string econ = "|sites=" + string.Join(";", (snapshot?.Economy?.CollectorSites
                    ?? System.Array.Empty<EconomyExtractionOpportunity>())
                .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R).ThenBy(x => (int)x.ResourceType)
                .Select(x => $"{x.Hex.Q},{x.Hex.R}:{(int)x.ResourceType}:{x.EffectiveYield}:"
                    + $"{x.CurrentBuildingCollection}:{x.MarginalIncomeGain}"))
                + "|threats=" + string.Join(";", (snapshot?.Known?.EnemySightings
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Concat(snapshot?.Known?.NeutralSightings
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R).ThenBy(x => x.ArmyId)
                    .Select(x => $"{x.Hex.Q},{x.Hex.R}:{DefenderFingerprint(x.Defenders)}"))
                // Both combat proofs take their hexBonus from remembered building
                // defence (AiMapMemory.KnownHexDefenseBonus), so a re-observed Base appearing,
                // being upgraded, changing owner or being razed genuinely changes the cached
                // answer and must invalidate it. Knowledge only: these are this player's own
                // observations, never a live BuildingRegistry sweep.
                + "|knownbases=" + string.Join(";", (snapshot?.Known?.Buildings
                        ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                    .Where(b => b.IsBase)
                    .OrderBy(b => b.Hex.Q).ThenBy(b => b.Hex.R)
                    .Select(b => $"{b.Hex.Q},{b.Hex.R}:{b.Defense:0.###}"));
            return $"fac={facilities}|off={offerings}|bases={bases}|armies={armies}|claims={claims}|owners={owners}{econ}"
                + $"|ready={(rd?.AnyFacilityWithHero == true ? 1 : 0)}:"
                + $"{(rd?.AnyOperatorlessFacility == true ? 1 : 0)}:"
                + $"{(rd?.ResearcherCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.AssemblerCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.DevPathViable == true ? 1 : 0)}:{rd?.UpgradeTargetCount ?? 0}:"
                + $"{(rd?.BestSuccessChance ?? 0f):0.###}:"
                + $"{(rd?.SurplusFraction ?? 0f):0.###}:"
                + $"{(rd?.ProductionSupport ?? 0f):0.###}";
        }

        // AI-02/AI-03 — the ONLY intents a Development decision can depend on:
        // DemandLayer.HasSupportedDevelopmentAxisDemand witnesses a need through an Economy
        // obligation, a Scout mover, or an Assault/Reinforcement Raid primary. Anything else
        // (a Return leg of someone else's raid, an ActiveDefence, a Development intent of its own)
        // cannot change whether an equipment offering is admitted.
        internal static bool DevelopmentRelevantIntent(MissionIntent i)
        {
            if (i == null || i.Status != IntentStatus.Active)
                return false;
            switch (i.Kind)
            {
                case MissionKind.Economy:
                    return i.Economy != null;
                case MissionKind.Scout:
                    return true;
                case MissionKind.Raid:
                    return i.Raid != null
                        && (i.Raid.Phase == RaidMissionPhase.Assault
                            || i.Raid.Phase == RaidMissionPhase.Reinforcement);
                default:
                    return false;
            }
        }

        // Armies whose POSITION/movement/activation can change a Development decision: only
        // Economy's delivery proof (AI-03) compares an army's route and shared movement bottleneck
        // before/after a grant. Includes every army the Economy analysis already advertises as a
        // possible builder/collector — those become the EconomyPreferredBuilderArmyId witness a
        // fresh Economy demand carries, and protection/delivery proofs may run against them too.
        // Operator armies are handled separately by the caller (pre-existing optimization).
        internal static HashSet<int> DevelopmentEconomyRelevantArmyIds(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var ids = new HashSet<int>();
            foreach (MissionIntent i in activeIntents ?? new List<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || i.Kind != MissionKind.Economy
                    || i.Economy == null)
                    continue;
                if (i.PreferredMoverArmyId.HasValue) ids.Add(i.PreferredMoverArmyId.Value);
                if (i.Economy.BuilderArmyId != null) ids.Add(i.Economy.BuilderArmyId.Value);
                if (i.Economy.CollectorArmyId != null) ids.Add(i.Economy.CollectorArmyId.Value);
            }
            EconomyStanding eco = snapshot?.Economy;
            if (eco != null)
            {
                void AddRoutes(IReadOnlyList<EconomyBuilderRouteSnapshot> routes)
                {
                    foreach (EconomyBuilderRouteSnapshot r in routes
                                 ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
                        ids.Add(r.ArmyId);
                }
                foreach (EconomyExtractionOpportunity x in eco.ExtractionOpportunities
                             ?? System.Array.Empty<EconomyExtractionOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (EconomyExtractionOpportunity x in eco.CollectorSites
                             ?? System.Array.Empty<EconomyExtractionOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (EconomyBaseOpportunity x in eco.BaseOpportunities
                             ?? System.Array.Empty<EconomyBaseOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (MobileCollectionOpportunity x in eco.MobileCollectionOpportunities
                             ?? System.Array.Empty<MobileCollectionOpportunity>())
                    ids.Add(x.CollectorArmyId);
            }
            return ids;
        }

        // Armies whose COMPOSITION (beyond Economy's — see DevelopmentEconomyRelevantArmyIds
        // above, unioned in by the caller) can change a Development decision: only Raid's combat
        // proof (ImprovesGroundCombatOutcome via WorthIt) reads army.Members. Recon's proof
        // (ImprovesReconCapability) reads only the offered equipment/recipient card's own static
        // abilities/MoveMax/ActivationApCost — never anything about the scout army itself — so a
        // Scout-only mover contributes nothing here, and a plain scout stepping its waypoint no
        // longer forces a full Development re-enumeration.
        internal static HashSet<int> DevelopmentRaidRelevantArmyIds(
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var ids = new HashSet<int>();
            foreach (MissionIntent i in activeIntents ?? new List<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || i.Kind != MissionKind.Raid
                    || i.Raid == null
                    || (i.Raid.Phase != RaidMissionPhase.Assault
                        && i.Raid.Phase != RaidMissionPhase.Reinforcement))
                    continue;
                if (i.Raid.PrimaryArmyId != null) ids.Add(i.Raid.PrimaryArmyId.Value);
            }
            return ids;
        }

        // EXACT, order-stable digest of a combat roster. Aggregates (Count plus Attack/Defense/HP/
        // Initiative sums) are strictly weaker than what the cached decision depends on:
        // DemandLayer.Development.ImprovesGroundCombatOutcome runs
        // WorthIt.CanDamageAll and WorthIt.Estimate, and those read each profile INDIVIDUALLY —
        // per-defender Defense, CeramicArmor, ability list, unit type tags, current AND max HP, and
        // Initiative (which sets the turn order, not a sum). Two genuinely different rosters can
        // therefore share every aggregate while giving different WorthIt answers (the canonical
        // example: one defender carrying CeramicArmor instead of none — identical sums, different
        // coverage verdict), so an aggregate fingerprint would fail to invalidate a changed decision.
        //
        // Every field WorthIt reads is emitted verbatim; nothing is hashed (a long-lived key must
        // not be built on GetHashCode). Per-profile rows are sorted ordinally so a
        // pure REORDERING of the same roster keeps the same key: WorthIt orders combat by
        // Initiative, never by list position, so order carries no information here.
        private static string DefenderFingerprint(IReadOnlyList<Game.Combat.WorthIt.DefenderProfile> defenders)
        {
            if (defenders == null || defenders.Count == 0)
                return "0";
            var rows = new List<string>(defenders.Count);
            foreach (Game.Combat.WorthIt.DefenderProfile d in defenders)
            {
                string tags = d.TypeTags == null ? string.Empty
                    : string.Join(",", d.TypeTags.Select(t => ((int)t).ToString(CultureInfo.InvariantCulture))
                        .OrderBy(t => t, System.StringComparer.Ordinal));
                string abilities = d.Abilities == null ? string.Empty
                    : string.Join(",", d.Abilities.Where(x => x != null)
                        .OrderBy(x => x, System.StringComparer.Ordinal));
                rows.Add($"{d.Attack:0.###}/{d.Defense:0.###}/{d.HitPoints:0.###}/"
                    + $"{d.MaxHitPoints:0.###}/{d.Initiative}/{(d.HasCeramicArmor ? 1 : 0)}/"
                    + $"[{tags}]/[{abilities}]");
            }
            rows.Sort(System.StringComparer.Ordinal);
            return $"{defenders.Count}:" + string.Join("|", rows);
        }
    }
}
