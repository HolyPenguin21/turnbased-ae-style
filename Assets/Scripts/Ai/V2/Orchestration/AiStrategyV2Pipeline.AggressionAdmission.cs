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
    //   * the force ceiling, bases, held Citadel, the reserve hand/deck can still add, the held
    //     Unit/Hero/Equipment cards (the only hand inputs of the demand and of its field-power
    //     chains — drawing a Base or Economy card does not re-run the lane), the open ground
    //     Research/Production outputs (CanDeliverIndependentFieldArmy) and the allocator's live
    //     cooldowns.
    // Resources are deliberately absent: money changes whether a chain is affordable, never the
    // measured shortage, and the existing funding path re-reads the ledger when it plays.
    // Intel ages (SeenTurn) only change at a turn boundary, which rebuilds everything anyway.
    public static partial class Pipeline
    {
        internal static string AggressionAdmissionFingerprint(WorldSnapshot snapshot,
            PlayerSetupData player)
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
                    // A hero's hex decides the preparation host's capacity hero and a lone-hero
                    // host (2026-09-30), so a hero-carrying army is positioned too.
                    + (a.IsStructuralRaidActor || a.IsGarrison || a.HasHero ? $":{a.Hex.Q},{a.Hex.R}" : "")));
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
            string generation = string.Join(",", GroundCombatDemandPolicy.GroundGenerationOffers(snapshot)
                .Select(o => $"{o.Card.displayName}:{o.Mode}@{o.FacilityHex.Q},{o.FacilityHex.R}")
                .OrderBy(x => x, System.StringComparer.Ordinal));
            string handCards = string.Join(",", (self.Hand
                    ?? (IReadOnlyList<Game.Cards.CardData>)System.Array.Empty<Game.Cards.CardData>())
                .Where(c => c?.Definition != null && (c.Definition.cardType == Game.Cards.CardType.Unit
                    || c.Definition.cardType == Game.Cards.CardType.Hero
                    || c.Definition.cardType == Game.Cards.CardType.Equipment))
                .Select(c => $"{c.Definition.displayName}+{c.Equipment?.displayName}"
                    + (c.ResearchProductionCreated ? "*" : ""))
                .OrderBy(x => x, System.StringComparer.Ordinal));
            string bases = string.Join(";", (self.BaseHexes ?? System.Array.Empty<Game.HexGrid.HexCoord>())
                .Select(h => $"{h.Q},{h.R}").OrderBy(x => x, System.StringComparer.Ordinal));
            return $"axis={DesireAxis.Aggression}"
                + $"|know={snapshot.KnowledgeVersion}"
                + $"|peak={self.TotalMilitaryPotential.ToString("0.##", inv)}"
                + $"|atkPeak={self.AttackPeak.ToString("0.##", inv)}"
                + $"|reserve={(self.Reserve.Units + self.Reserve.Hero).ToString("0.##", inv)}"
                + $"|share={self.DeployedPower.ToString("0.##", inv)}/{self.AvailablePower.ToString("0.##", inv)}"
                // Mobilization start (B): garrison floors and operators are not in the army rows.
                + $"|field={self.FieldStrikePotential.ToString("0.##", inv)}"
                + $"|citadel={(self.HoldsStartingCitadel ? 1 : 0)}|bases={bases}"
                + $"|hand={handCards}"
                + $"|gen={generation}"
                + $"|prepNoChain={PreparationDeliveryMemory.Digest(player, snapshot.TurnNumber)}"
                + $"|cd={AiAllocatorStateRegistry.Peek(player)?.CooldownDigest(snapshot.TurnNumber) ?? "-"}"
                + $"|armies={armies}|intents={intents}|threats={threats}|assets={assetThreats}";
        }
    }
}

