using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // Development-lane strategic-admission fingerprint. A mechanical partial of Pipeline,
    // not a second admission owner: RunTurn (AiStrategyV2Pipeline.cs) is still the only
    // caller, through DevelopmentAdmissionFingerprint.
    //
    // The key carries exactly what DevelopmentOpportunityEvaluator.Enumerate reads, so an
    // unchanged key provably cannot change a Development decision:
    //   * the investment window (DevelopmentInvestmentGate) — fixed for the turn once observed;
    //   * AP thresholds, resources and hand version;
    //   * facilities, offerings and bases;
    //   * every own army's composition (any unit may be the best Equipment recipient) and, for
    //     Research/Production operator armies, their position (remote-hero delivery cost);
    //   * actor occupancy (which heroes are free to travel to a facility);
    //   * the composition of every known threat EquipmentMatchupFit evaluates against.
    public static partial class Pipeline
    {
        // AP enters as the set of affordability thresholds Development can cross, not a raw number:
        //   * per offering: ResearchProductionSystem.AttemptApCost(card) + card.activationApCost
        //     (the READY Challenge+attach gate), and
        //   * per Unit card in hand: CardData.EffectivePlayApCost.
        internal static string DevelopmentAdmissionFingerprint(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, int actionPoints,
            string resources, int handVersion, AiHandData hand = null, PlayerSetupData player = null) =>
            $"axis={DesireAxis.Development}"
            + $"|window={(player != null && snapshot != null && DevelopmentInvestmentGate.IsOpen(player, snapshot.TurnNumber) ? 1 : 0)}"
            + $"|apfit={DevelopmentApAffordability(snapshot, hand, actionPoints)}"
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
            // Every army is a possible Equipment recipient, so every roster is exact (WorthIt reads
            // each profile individually — aggregates could hide a changed verdict). Position only
            // matters for an army carrying a Research/Production operator: it prices the remote
            // hero's delivery to a facility.
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
                    return $"{a.ArmyId}:{a.MemberCount}:{(a.HasHero ? 1 : 0)}:"
                        + $"{(a.HasResearchOperator ? 1 : 0)}:{(a.HasProductionOperator ? 1 : 0)}:"
                        + $"roster={DefenderFingerprint(a.MembersWithHeroes)}{operatorState}";
                }));
            string bases = string.Join(";", (snapshot?.Self?.BaseHexes
                    ?? System.Array.Empty<Game.HexGrid.HexCoord>())
                .OrderBy(h => h.Q).ThenBy(h => h.R).Select(h => $"{h.Q},{h.R}"));
            // Actor occupancy: preparation builds ActorCommitments over ALL intents, so each kind
            // contributes only the inputs that produce a claim — never intent identity.
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
            // EquipmentMatchupFit's threat set: enemy army compositions (composition only crosses
            // the TrueWorld boundary), remembered neutral defenders and known event guards.
            // Positions are not read by the matchup, so they are not part of the key.
            IEnumerable<string> threatRows = (snapshot?.TrueWorld?.EnemyArmies
                    ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a?.Members != null && a.Members.Count > 0)
                .Select(a => "e" + DefenderFingerprint(a.Members))
                .Concat((snapshot?.Known?.NeutralSightings
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Where(x => x.Defenders != null && x.Defenders.Count > 0)
                    .Select(x => "n" + DefenderFingerprint(x.Defenders)))
                .Concat((snapshot?.Known?.EventGuards
                        ?? System.Array.Empty<KnownEventGuardSnapshot>())
                    .Where(g => g.Defenders != null && g.Defenders.Count > 0)
                    .Select(g => "g" + DefenderFingerprint(g.Defenders)));
            string threats = string.Join(";", threatRows.OrderBy(x => x, System.StringComparer.Ordinal));
            return $"fac={facilities}|off={offerings}|bases={bases}|armies={armies}|claims={claims}"
                + $"|threats={threats}"
                + $"|ready={(rd?.AnyFacilityWithHero == true ? 1 : 0)}:"
                + $"{(rd?.AnyOperatorlessFacility == true ? 1 : 0)}:"
                + $"{(rd?.ResearcherCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.AssemblerCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.DevPathViable == true ? 1 : 0)}:{rd?.UpgradeTargetCount ?? 0}:"
                + $"{(rd?.BestSuccessChance ?? 0f):0.###}";
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
