using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Economy-lane strategic-admission fingerprint. The domain owner of the Economy key (it was
    // inline in Pipeline.RunTurn); the common readmission (StrategicReadmission) only compares keys.
    internal static class EconomyAdmission
    {
        // `resources` is the physical stock digest shared with the Development key.
        internal static string Fingerprint(DesireAxis axis, WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, PlayerRoot root, AiHandData hand,
            PlayerSetupData player, AiTurnContext ctx, string resources)
        {
            // Economy only from here on (Development returned above). The key carries what
            // Economy's decision reads and nothing that ticks on every executed step: no
            // global state version, and position/movement/activation only for armies the
            // economy analysis advertises as builders/collectors (or an Economy intent
            // holds) — a scout stepping its waypoint cannot change a build decision. Every
            // army still contributes identity, size and hero presence, so an army gaining a
            // hero (a new builder candidate) or being formed/destroyed re-admits Economy.
            HashSet<int> economyArmyIds = RelevantArmyIds(snapshot, activeIntents);
            string armies = string.Join(";", (snapshot?.Self?.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).OrderBy(a => a.ArmyId)
                .Select(a => $"{a.ArmyId}:{a.MemberCount}:{(a.HasHero ? 1 : 0)}"
                    // A served facility's / selected site's operator duty decides builder admissibility.
                    + $":duty{(a.OperatorDutyBlocksDeparture ? 1 : 0)}"
                    // Availability and prices can change when a legal zero-AP release
                    // becomes possible, even though the real army's roster is unchanged.
                    + (a.EconomyDeparture != null
                        ? $":eco{a.EconomyDeparture.MemberCount}:{a.EconomyDeparture.Capacity}"
                            + $":{a.EconomyDeparture.CurrentMovement}:{a.EconomyDeparture.MaxMovement}"
                            + $":{a.EconomyDeparture.ActivationApCost}:{a.EconomyDeparture.EffectiveArmyPower:0.###}"
                        : ":eco0")
                    + (economyArmyIds.Contains(a.ArmyId)
                        ? $":{a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:{a.ActivationApCost}"
                        : string.Empty)));
            // Actor occupancy by ANY mission (a builder claimed by a raid is unavailable),
            // by kind/status/claimed actor only — never intent identity, so a scout
            // retargeting its waypoint keeps the same key.
            string claims = string.Join(";", (activeIntents ?? new List<MissionIntent>())
                .Where(i => i != null)
                .Select(i => $"{i.Kind}:{i.Status}:{i.PreferredMoverArmyId}"
                    + $":{i.Raid?.AirSupportArmyId}"
                    // Held ground supports (convoys, gathers) — the same list
                    // ActorCommitments claims (GroundCombatLegs).
                    + $":sup{string.Join(",", GroundCombatLegs.HeldGroundSupportArmyIds(i))}")
                .Distinct().OrderBy(x => x, System.StringComparer.Ordinal));
            // The fingerprint's site facts are produced by the SAME
            // WorldAnalysis.EconomyOpportunityRows the typed invalidation is derived from,
            // so a "this known site became usable" event is never published and then
            // suppressed here on an unchanged key: the trigger and the admission gate
            // describe the same world.
            string economyFacts = "|sites=" + string.Join(";",
                    WorldAnalysis.EconomyOpportunityRows(snapshot)
                    .OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={kv.Value}"))
                + "|bases=" + string.Join(";", (snapshot?.Economy?.BaseOpportunities
                    ?? System.Array.Empty<EconomyBaseOpportunity>())
                    .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                    .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.HexYield.Sum:0.###}"))
                + "|threats=" + string.Join(";", (snapshot?.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Concat(snapshot?.Known?.NeutralSightings
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                    .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.SeenTurn}:{x.Defenders?.Count ?? 0}"))
                + "|owners=" + string.Join(";", (activeIntents
                    ?? new List<MissionIntent>())
                    .Where(i => i?.Kind == MissionKind.Economy)
                    .OrderBy(i => i.IntentKey)
                    .Select(i => $"{i.IntentKey}:{i.Status}:{i.PreferredMoverArmyId}"));
            // Raw AP stays: Economy's AP reads (chain sums, ledger-aware SpendableAp) have
            // no small exact threshold set like Development's apfit, and AP only moves on
            // an activation or a play, not on every step. Other lanes' holds shrink what
            // Economy may spend, so they are input; Economy's own rows are its output and
            // stay out (they would re-admit the axis on its own writes).
            return $"axis={axis}|ap={root?.ActionPoints ?? 0}"
                + $"|res={resources}|hand={hand?.MutationVersion ?? -1}"
                + "|held=" + StrategicResourceReservationLedger.ReasonDigest(player,
                    ctx.TurnNumber, StrategicReservationReason.StrategicReactionPass)
                + $"|armies={armies}|claims={claims}"
                + economyFacts;
        }

        // Armies whose POSITION/movement/activation can change an Economy decision (the Economy
        // admission fingerprint above): every Economy intent's mover/builder/collector plus every
        // army the Economy analysis advertises as a possible builder/collector.
        internal static HashSet<int> RelevantArmyIds(WorldSnapshot snapshot,
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
    }
}
