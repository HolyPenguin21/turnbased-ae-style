using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // T03 — Aggression-lane strategic-admission fingerprint. A mechanical partial of Pipeline,
    // not a second admission owner: RunTurn (AiStrategyV2Pipeline.cs) is still the only caller,
    // through StrategicAdmissionFingerprint.
    //
    // Aggression re-enters the typed strategic loop on its existing invalidation mask
    // (DesireAxes.InvalidationMaskFor: Contact / Actor / EventState / Threat / Infrastructure).
    // The key carries what AggressionDemandEvaluator (Raid, Attack, ActiveDefence, held-base
    // garrison) reads, so an unchanged key cannot change an Aggression demand and a Recon scout
    // stepping its waypoint does not re-run the lane:
    //   * every own ground combat army's composition, power, readiness and — for structural
    //     field armies and garrisons only — position (assembly, fist-on-base, ETA to a threat);
    //   * every intent's occupancy and ground-combat lifecycle (phase, primary, support, gather
    //     supports, preparation, reinforcement dedup stamp), read from the live intent registry
    //     because the loop's intent list is only refreshed at the next admission;
    //   * honest knowledge (AiMapMemory revision), the threat contacts built from it and every
    //     asset threat ActiveDefence answers (severity, enemy / response ETA);
    //   * the force ceiling, bases, held Citadel, the reserve hand/deck can still add, the hand
    //     version (CanDeliverIndependentFieldArmy) and the allocator's live cooldowns.
    // Intel ages (SeenTurn) only change at a turn boundary, which rebuilds everything anyway.
    public static partial class Pipeline
    {
        internal static string AggressionAdmissionFingerprint(WorldSnapshot snapshot,
            PlayerSetupData player, int handVersion)
        {
            if (snapshot?.Self == null)
                return $"axis={DesireAxis.Aggression}|none";
            CultureInfo inv = CultureInfo.InvariantCulture;
            SelfSnapshot self = snapshot.Self;
            string armies = string.Join(";", (self.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && !a.IsAir)
                .OrderBy(a => a.ArmyId)
                .Select(a => $"{a.ArmyId}:{a.MemberCount}:{a.EffectiveArmyPower.ToString("0.##", inv)}"
                    + $":{(a.IsStructuralRaidActor ? "S" : "")}{(a.IsGarrison ? "G" : "")}"
                    + $":{(a.CurrentMovement > 0 ? 1 : 0)}{(a.HasActivatedThisTurn ? 1 : 0)}"
                    + (a.IsStructuralRaidActor || a.IsGarrison ? $":{a.Hex.Q},{a.Hex.R}" : "")));
            string intents = string.Join(";", MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null)
                .Select(i => $"{i.IntentKey}:{i.Status}:{i.PreferredMoverArmyId}"
                    + $":{i.Raid?.Target.DiagnosticLabel}"
                    + $":{i.Raid?.Phase}{i.Attack?.Phase}{i.ActiveDefence?.Phase}"
                    + $":{i.Raid?.SupportArmyId}{i.Attack?.SupportArmyId}"
                    + $":{i.Raid?.ReinforcementRequestedTurn}{i.Attack?.ReinforcementRequestedTurn}"
                    + $":{(i.Attack?.Preparation == true ? "P" : "")}{(i.Attack?.AssaultStarted == true ? "A" : "")}"
                    + $":sup{string.Join(",", GroundCombatLegs.HeldGroundSupportArmyIds(i))}")
                .OrderBy(x => x, System.StringComparer.Ordinal));
            string threats = string.Join(";", (snapshot.Threat?.Contacts
                    ?? System.Array.Empty<EnemyContactSnapshot>())
                .Where(c => c?.Army != null)
                .Select(c => $"{c.Army.ArmyId}:{c.Knowledge}:{c.Position?.Q},{c.Position?.R}")
                .OrderBy(x => x, System.StringComparer.Ordinal));
            // ActiveDefence's actual input: every honest threat against a concrete own asset —
            // including non-combat armies (collectors, builders, scouts) whose positions the army
            // row above omits — with its severity and both ETAs (AssessResponse).
            string assetThreats = $"siege={(snapshot.Threat?.UnderSiege == true ? 1 : 0)};"
                + string.Join(";", (snapshot.Threat?.Threats ?? System.Array.Empty<AssetThreatSnapshot>())
                    .Select(WorldAnalysis.ThreatKey)
                    .OrderBy(x => x, System.StringComparer.Ordinal));
            string bases = string.Join(";", (self.BaseHexes ?? System.Array.Empty<Game.HexGrid.HexCoord>())
                .Select(h => $"{h.Q},{h.R}").OrderBy(x => x, System.StringComparer.Ordinal));
            return $"axis={DesireAxis.Aggression}"
                + $"|know={snapshot.KnowledgeVersion}"
                + $"|peak={self.TotalMilitaryPotential.ToString("0.##", inv)}"
                + $"|reserve={(self.Reserve.Units + self.Reserve.Hero).ToString("0.##", inv)}"
                + $"|share={self.DeployedPower.ToString("0.##", inv)}/{self.AvailablePower.ToString("0.##", inv)}"
                + $"|citadel={(self.HoldsStartingCitadel ? 1 : 0)}|bases={bases}"
                + $"|hand={handVersion}"
                + $"|cd={AiAllocatorStateRegistry.Peek(player)?.CooldownDigest(snapshot.TurnNumber) ?? "-"}"
                + $"|armies={armies}|intents={intents}|threats={threats}|assets={assetThreats}";
        }
    }
}
