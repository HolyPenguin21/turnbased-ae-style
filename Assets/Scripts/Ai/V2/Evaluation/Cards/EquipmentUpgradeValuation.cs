using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // Equipment upgrade valuation, split from StrategicCardEvaluator: the value of attaching
    // equipment to a recipient against its mission's known opposition. Costs stay with the evaluator.
    internal static class EquipmentUpgradeValuation
    {
        [System.ThreadStatic] private static bool s_operatorForecast;
        internal static bool IsOperatorForecast => s_operatorForecast;
        // =======================================================================================
        //  EQUIPMENT UPGRADE VALUE — the ONE value of attaching equipment E to recipient R
        // =======================================================================================
        //  Every consumer reads this and nothing else: hand Equipment onto a deployed unit
        //  (NonCombatCardPlayer / ScoreNonCombat), equipment riding a deploy (AttachDeploy /
        //  GenerateAttachDeploy Synergy + EquipmentUpgrade RoleFit), a READY generated upgrade
        //  (ScoreGeneratedEquipmentUpgrade) and Development PREPARE (which prices its output through
        //  ScoreGeneratedEquipmentUpgrade). Costs stay with ResourceCost; this is value only.
        // Operational delta is a signed before/after projection against a mission's known
        // target or composition/deck reference. Useful in-advance upgrades keep their value.
        // Costs and challenge probability have separate owners; neither is added here.
        internal static float EquipmentUpgradeValue(EquipmentDelta delta)
        {
            // Contextual projection already prices penetration and effects. No percentage
            // amplifier and no contact gate: useful reserve upgrades are legal investments.
            float combat = delta.Combat;
            return (combat + delta.Tactical) * AiConfigV2.equipmentUpgradePersistence;
        }

        // Development's bound opportunity: ExpectedGain is the recipient's whole delta and
        // TacticalGain its tactical share, both in AiPower units.
        internal static float EquipmentUpgradeValue(DevelopmentOpportunity op)
        {
            if (op == null)
                return 0f;
            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            float tactical = op.TacticalGain / powerUnit;
            var delta = new EquipmentDelta(op.ExpectedGain / powerUnit - tactical, tactical);
            return EquipmentUpgradeValue(delta);
        }

        // A materialization chain carrying equipment onto the body it deploys.
        internal static float EquipmentUpgradeValue(MaterializationPlan p, WorldSnapshot snap = null,
            CapabilityInventory inv = null)
        {
            CardDefinition host = p?.BaseCardInHand?.Definition ?? p?.GeneratedBaseDef;
            CardDefinition eq = p?.GeneratedEquipmentDef ?? p?.EquipmentInHand?.Definition;
            if (host == null || eq?.equipment == null)
                return 0f;
            // A deployment's SynergyValue already prices its final Stealth trait. Bound
            // existing-recipient upgrades need the marginal trait; deploy chains must not add it twice.
            EquipmentDelta delta = EquipmentDeltaParts(eq, p?.BaseCardInHand, host, snap, inv,
                includeStealthTrait: false, deployment: p);
            // The same production-only repeat rule used by bound READY recipients. A generated
            // body carrying an existing hand item must not damp that item's own utility.
            float productionScale = p.Generation?.ProducesEquipment == true
                && object.ReferenceEquals(p.Generation.CardDef, eq)
                ? DevelopmentDiversity.RepeatFactor(snap?.Observer ?? p.Generation.Hero?.Owner,
                    snap?.TurnNumber ?? 0, eq, out _)
                : 1f;
            return EquipmentUpgradeValue(delta) * productionScale;
        }

        private static List<WorthIt.DefendingArmy> EquipmentValuationThreats(WorldSnapshot snap)
        {
            var result = new List<WorthIt.DefendingArmy>();
            // Only composition crosses the TrueWorld boundary. Ground and aviation rosters are
            // both legitimate Production valuation inputs; neither hidden coordinates nor army
            // identity is passed to recipient selection or Mission planning.
            if (snap?.TrueWorld?.EnemyArmies != null)
                result.AddRange(snap.TrueWorld.EnemyArmies
                    .Where(a => a != null && a.Members != null && a.Members.Count > 0)
                    .Select(a => new WorthIt.DefendingArmy(a.Members, a.Commander)));

            // A neutral's last honestly observed defender profiles are the only permitted
            // composition witness. After it disappears into fog, its hidden live roster may
            // change; matching a known ArmyId back into TrueWorld would silently cheat.
            if (snap?.Known?.NeutralSightings != null)
                result.AddRange(snap.Known.NeutralSightings
                    .Where(s => s.Defenders != null && s.Defenders.Count > 0)
                    .Select(s => new WorthIt.DefendingArmy(s.Defenders, s.Commander)));

            // An event guard is not a live ArmyData until triggered. Its legitimately observed
            // defender profiles already belong to Known, so use those directly for WorthIt;
            // never invent a synthetic army or read hidden live event state/positions.
            if (snap?.Known?.EventGuards != null)
                result.AddRange(snap.Known.EventGuards
                    .Where(g => g.Defenders != null && g.Defenders.Count > 0)
                    .Select(g => new WorthIt.DefendingArmy(g.Defenders, g.Commander)));
            return result;
        }

        internal static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, CardData host,
            WorldSnapshot snap = null, CapabilityInventory inv = null)
            => EquipmentDeltaParts(equipDef, host, host?.Definition, snap, inv);

        // A host that is still only a definition (a deck card): nothing is attached to it yet.
        internal static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, CardDefinition host)
            => EquipmentDeltaParts(equipDef, null, host, null, null);

        private static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, CardData hostCard,
            CardDefinition host, WorldSnapshot snap, CapabilityInventory inv, bool includeStealthTrait = true,
            MaterializationPlan deployment = null)
        {
            EquipmentGrant grant = equipDef?.equipment;
            if (grant == null || host == null)
                return default;
            var current = EquipmentSystem.Project(host, hostCard?.Equipment, hostCard?.Mutator);
            var before = current.Stats.ToDictionary(kv => kv.Key, kv => kv.Value);
            return WithSlotWeight(equipDef, ScoreEquipmentDelta(grant, before, current.Abilities,
                host.cardType == CardType.Hero, snap, inv,
                EquipmentSystem.Project(host, hostCard?.Equipment, hostCard?.Mutator, equipDef), includeStealthTrait,
                host.unitTypeTags, EquipmentPurpose(snap, hostCard, null), deployment?.Deploy.Army, deployment,
                beforeConsumption: EquipmentSystem.Consumption(host, hostCard?.Equipment, hostCard?.Mutator),
                afterConsumption: EquipmentSystem.Consumption(host,
                    equipDef.attachmentSlot == AttachmentSlot.Equipment ? equipDef : hostCard?.Equipment,
                    equipDef.attachmentSlot == AttachmentSlot.Mutator ? equipDef : hostCard?.Mutator)));
        }

        internal static EquipmentDelta EquipmentDeltaParts(CardDefinition equipDef, UnitData host,
            WorldSnapshot snap = null, CapabilityInventory inv = null)
        {
            EquipmentGrant grant = equipDef?.equipment;
            if (grant == null || host == null)
                return default;
            var before = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = host.Attack,
                [EquipmentStat.Defense] = host.Defense,
                [EquipmentStat.Range] = host.Range,
                [EquipmentStat.HitPoints] = host.HitPointsMax,
                [EquipmentStat.MoveMax] = host.PermanentMoveMax,
                [EquipmentStat.Initiative] = host.Initiative,
                [EquipmentStat.ActivationApCost] = host.ActivationApCost,
                [EquipmentStat.CommandRating] = host.CommandRating,
                [EquipmentStat.Fate] = host.Fate,
            };
            IReadOnlyList<string> ab = host.Abilities != null
                ? new List<string>(host.Abilities) : (IReadOnlyList<string>)System.Array.Empty<string>();
            var next = EquipmentSystem.PredictAttachment(equipDef, host);
            var stats = next.Stats.ToDictionary(kv => kv.Key, kv => kv.Value);
            // Compare current Fate with current Fate after attachment, preserving spent Fate.
            // PredictAttachment returns the full projected maximum-stat state.
            if (stats.TryGetValue(EquipmentStat.Fate, out int projectedFate))
                stats[EquipmentStat.Fate] = EquipmentSystem.CurrentAfterAttachment(host,
                    EquipmentStat.Fate, projectedFate);
            var projected = new PredictedEquipmentState(stats, next.Abilities);
            return WithSlotWeight(equipDef, ScoreEquipmentDelta(grant, before, ab, host.IsHero, snap, inv,
                projected, true, host.TypeTags?.ToList(), EquipmentPurpose(snap, null, host),
                OwnWorld.Of(snap?.Observer).ArmyContaining(host), null,
                Mathf.Max(0, host.HitPointsMax - host.HitPointsCurrent), host,
                EquipmentSystem.Consumption(host), EquipmentSystem.Consumption(host, equipDef)));
        }

        // The one slot weight (AiConfigV2.equipMutatorValueMult): only a positive gain of a
        // Mutator-slot attachment is scaled; losses and capability rejects keep their value.
        private static EquipmentDelta WithSlotWeight(CardDefinition attachment, EquipmentDelta delta)
        {
            if (attachment?.attachmentSlot != AttachmentSlot.Mutator)
                return delta;
            float m = AiConfigV2.equipMutatorValueMult;
            return new EquipmentDelta(delta.Combat > 0f ? delta.Combat * m : delta.Combat,
                delta.Tactical > 0f ? delta.Tactical * m : delta.Tactical,
                () => delta.Detail + $" mutator x{m:0.##}");
        }

        // Combat = what WorthIt can witness (Attack/Defense/HP/Initiative, hero Fate,
        // added/lost damage abilities); Tactical = what it cannot (Move, Range, activation AP,
        // Command, strategic roles - roles already gate themselves on a present threat).
        private static EquipmentDelta ScoreEquipmentDelta(EquipmentGrant grant, Dictionary<EquipmentStat, int> before,
            IReadOnlyList<string> hostAbilities, bool isHero, WorldSnapshot snap, CapabilityInventory inv,
            PredictedEquipmentState? projected = null, bool includeStealthTrait = true,
            IReadOnlyList<UnitTypeTag> hostTags = null, MissionIntent purpose = null, ArmyData army = null,
            MaterializationPlan deployment = null, int hpSpent = 0, UnitData hostUnit = null,
            EquipmentConsumption beforeConsumption = null, EquipmentConsumption afterConsumption = null)
        {
            using var __scope = new Game.Core.ProfileScope("AI/Equip.ScoreDelta");
            PredictedEquipmentState predicted = projected ?? EquipmentSystem.Predict(grant, before, hostAbilities);
            int After(EquipmentStat stat) =>
                predicted.Stats != null && predicted.Stats.TryGetValue(stat, out int value) ? value : before[stat];

            // A combat gain cannot compensate for disabling the assignment's capabilities.
            if ((purpose?.Kind == MissionKind.Economy || purpose?.Kind == MissionKind.Development)
                && (After(EquipmentStat.MoveMax) < before[EquipmentStat.MoveMax]
                    || After(EquipmentStat.ActivationApCost) > before[EquipmentStat.ActivationApCost]
                    || hostAbilities?.Any(a => !predicted.Abilities.Contains(a)) == true)
                || purpose?.Kind == MissionKind.Scout
                && (AbilityParams.GetBestRecceRadius(predicted.Abilities) < AbilityParams.GetBestRecceRadius(hostAbilities)
                    || AbilityParams.GetBestRecceSpotStrength(predicted.Abilities) < AbilityParams.GetBestRecceSpotStrength(hostAbilities)
                    || purpose.Scout?.RequiresStealth == true && !AbilityParams.AbilitiesHaveAnyStealth(predicted.Abilities)))
                return new EquipmentDelta(-1.5f, 0f, "reject=assignment-capability-loss");

            // ONE formula for every caller. A snapshot-free call (Self equipment-reserve estimate) is the same
            // calculation in an explicit REFERENCE context: no known enemies (catalog prior profile), no mission,
            // no army, no world effects (a reserve has none to read). The consumer converts the result to its
            // own unit at its own boundary (WorldAnalysis.Self.EquipmentReserve).
            return ScoreEquipmentByEfficiency(before, predicted, hostAbilities, isHero, snap, inv,
                includeStealthTrait, purpose, army, hostUnit, hostTags, deployment, hpSpent,
                beforeConsumption, afterConsumption);
        }

        // Operational equipment value: the change in the host's efficiency (EquipmentEfficiency),
        // read against what the AI actually knows (target shares, mission context). The result keeps
        // the EquipmentDelta plumbing: Combat + Tactical = card value / equipmentUpgradePersistence,
        // so EquipmentUpgradeValue (x persistence) returns the card value.
        private static EquipmentDelta ScoreEquipmentByEfficiency(Dictionary<EquipmentStat, int> before,
            PredictedEquipmentState predicted, IReadOnlyList<string> hostAbilities, bool isHero,
            WorldSnapshot snap, CapabilityInventory inv, bool includeStealthTrait, MissionIntent purpose, ArmyData army,
            UnitData hostUnit, IReadOnlyList<UnitTypeTag> hostTags, MaterializationPlan deployment, int hpSpent,
            EquipmentConsumption beforeConsumption, EquipmentConsumption afterConsumption)
        {
            using var __eff = new Game.Core.ProfileScope("AI/Equip.Efficiency");
            int After(EquipmentStat stat) =>
                predicted.Stats != null && predicted.Stats.TryGetValue(stat, out int value) ? value : before[stat];
            EfficiencyStats b = new EfficiencyStats(before[EquipmentStat.Attack], before[EquipmentStat.Defense],
                before[EquipmentStat.HitPoints], before[EquipmentStat.Range], before[EquipmentStat.MoveMax],
                before[EquipmentStat.Initiative], before[EquipmentStat.ActivationApCost], before[EquipmentStat.Fate]);
            EfficiencyStats a = new EfficiencyStats(After(EquipmentStat.Attack), After(EquipmentStat.Defense),
                After(EquipmentStat.HitPoints), After(EquipmentStat.Range), After(EquipmentStat.MoveMax),
                After(EquipmentStat.Initiative), After(EquipmentStat.ActivationApCost), After(EquipmentStat.Fate));

            List<WorthIt.DefendingArmy> opposition;
            List<WorthIt.DefenderProfile> targets;
            using (new Game.Core.ProfileScope("AI/Equip.Targets"))
            {
                opposition = EquipmentOpposition(snap, purpose);
                targets = EquipmentTargetsFor(snap, purpose);
            }

            bool aviation = hostTags != null && hostTags.Contains(UnitTypeTag.Aircraft);
            var ctx = new EfficiencyContext
            {
                IsHero = isHero,
                IncludeStealthTrait = includeStealthTrait,
                StealthUsable = AbilityParams.AbilitiesHaveAnyRecce(predicted.Abilities)
                    || purpose?.Kind == MissionKind.Scout,
                Targets = targets,
                HostTags = hostTags,
                HpSpent = hpSpent,
                BeforeConsumption = beforeConsumption,
                AfterConsumption = afterConsumption,
            };
            if (targets.Count > 0 && aviation)
            {
                // Legacy proxy inputs (aviation hosts only): shares of the known enemies.
                var shares = EquipmentTargetMemo.Shares(targets);
                ctx.ArmoredShare = shares.Armored;
                ctx.BioShare = shares.Bio;
                ctx.MechanicalShare = shares.Mechanical;
                ctx.AirShare = shares.Air;
            }
            if (opposition.Count > 0)
            {
                ctx.SecondaryNeighbors = Mathf.Max(0f, (float)opposition.Average(o => o.Units?.Count ?? 0) - 1f);
                ctx.SplashTargets = Mathf.Min(2f, ctx.SecondaryNeighbors);
                ctx.EnemyCommanderInitiative = Mathf.RoundToInt((float)opposition.Average(o => o.Commander.Initiative));
            }
            if (opposition.Count > 0)
                // Real neighbours for Splash / Scorcher: the ground bodies of each known enemy army.
                ctx.Battles = EquipmentTargetMemo.AttachmentTargets(snap, purpose, opposition, targets).Battles;
            if (hostUnit != null && army != null)
            {
                // The contact list depends on the recipient only: one Enumerate builds it once
                // per recipient (AirContactsFor also sets the host's own AA facts on ctx).
                if (EquipmentTargetMemo.TryGetAirContacts(snap, army, hostUnit, out List<AirContact> contacts))
                {
                    ctx.AntiAirRadius = hostUnit.AntiAirRadius;
                    ctx.HostHidden = hostUnit.IsHidden;
                }
                else
                {
                    contacts = AirContactsFor(snap, army, hostUnit, ctx);
                    EquipmentTargetMemo.StoreAirContacts(snap, army, hostUnit, contacts);
                }
                ctx.AirContacts = contacts;
            }
            if (isHero)
            {
                var fighters = army?.Members?.Where(m => m != null && !m.IsHero).ToList();
                if (fighters != null && fighters.Count > 0)
                    ctx.ArmyAttack = (float)fighters.Average(m => m.Attack);
                // A garrison is not a field army: its hero is not a battle commander.
                ctx.CommandsFieldArmy = army != null && !army.IsGarrison && fighters != null && fighters.Count > 0;
            }
            if (army?.Members != null)
            {
                ctx.HostCommanderInitiative = WorthIt.SideCommander.Of(army.Members).Initiative;
                foreach (UnitData member in army.Members)
                {
                    if (member == null || hostUnit != null && ReferenceEquals(member, hostUnit)) continue;
                    if (hostUnit != null) ctx.OtherSpeedMin = Mathf.Min(ctx.OtherSpeedMin, member.MoveMax);
                    ctx.OtherRecceRadius = Mathf.Max(ctx.OtherRecceRadius, AbilityParams.GetBestRecceRadius(member));
                    ctx.OtherSpotStrength = Mathf.Max(ctx.OtherSpotStrength, AbilityParams.GetBestRecceSpotStrength(member));
                    if (member.HasAbility(UnitAbilities.AntiAir)) ctx.OtherAntiAirCarriers++;
                }
            }
            HexCoord? targetHex = purpose?.Attack?.Target.HasValue == true ? purpose.Attack.Target.Hex
                : purpose?.Raid?.Target.HasValue == true ? purpose.Raid.TargetHex : null;
            float hexBonus = targetHex.HasValue
                ? AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, targetHex.Value,
                    purpose?.Attack?.Target.HasValue == true ? purpose.Attack.Target.ExpectedOwner : null)
                : 0f;
            EquipmentEfficiency.ApplyMission(ctx, purpose?.Kind, hexBonus);
            ApplyKnownRouteAndCoverage(ctx, snap, army, hostUnit, targetHex, purpose);
            if (isHero && a.Fate != b.Fate && snap?.Development != null)
                ctx.OperatorOutputs = OperatorOutputForecast(snap, inv, hostUnit, b.Fate, a.Fate);

            // ONE unit boundary: U is card score over the reserve horizon; EquipmentDelta stores
            // U / equipmentUpgradePersistence so EquipmentUpgradeValue (x persistence) returns U exactly.
            float perU = 1f / AiConfigV2.equipmentUpgradePersistence;
            float combat, tactical;
            string breakdown;
            using (new Game.Core.ProfileScope("AI/Equip.EffDelta"))
            {
                if (aviation)
                {
                    // Aviation hosts never use the ground contact model: Attack and damage abilities are priced as
                    // an air strike (kernel exchange, no return fire); Defense/HP/speed/AP stay on the legacy
                    // linear table, explicitly a PROXY (AA fire is not modelled).
                    ctx.Carriers = null;
                    EfficiencyBreakdown legacy = EquipmentEfficiency.Delta(b, hostAbilities, a, predicted.Abilities, ctx);
                    float perE = AiConfigV2.equipCardValuePerE;
                    float strike = EquipmentEfficiency.AviationOffenseDelta(b, hostAbilities, a, predicted.Abilities, ctx)
                        * ctx.OffenseMult;
                    combat = (strike + legacy.Defense * perE) * perU;
                    tactical = legacy.Tactical * perE * perU;
                    breakdown = $"proxy(aviation) air-strike={strike:0.###} " + legacy;
                }
                else
                {
                    UtilityBreakdown u = EquipmentEfficiency.Utility(b, hostAbilities, a, predicted.Abilities, ctx);
                    combat = u.Combat * perU;
                    tactical = u.Tactical * perU;
                    breakdown = u + " context=" + (opposition.Count > 0 ? "known-composition" : targets.Count > 0
                        ? "reference-deck" : "catalog-prior") + $" targets={targets.Count} hpSpent={hpSpent}";
                }
            }

            // Abilities the table does not price (global income, auras, summons, ...) keep their
            // contextual registry value, exactly as before.
            List<string> unpricedBefore = hostAbilities.Where(x => !EquipmentEfficiency.IsPriced(x)).ToList();
            List<string> unpricedAfter = predicted.Abilities.Where(x => !EquipmentEfficiency.IsPriced(x)).ToList();
            if (snap != null && (unpricedBefore.Count > 0 || unpricedAfter.Count > 0))
            {
                var attachmentTargets = EquipmentTargetMemo.AttachmentTargets(snap, purpose, opposition, targets);
                // The "before" side reads the deployed host alone (its stats, abilities, army,
                // purpose and the snapshot), never the equipment: priced once per recipient.
                bool memoBefore = hostUnit != null && deployment == null;
                if (!memoBefore || !EquipmentTargetMemo.TryGetValueBefore(snap, hostUnit, includeStealthTrait,
                        out float valueBefore))
                {
                    valueBefore = StrategicEffectRegistry.AttachmentValue(before, unpricedBefore, hostTags, opposition, army,
                        isHero, snap, includeStealthTrait, deployment, targets, hpSpent, attachmentTargets, hostUnit);
                    if (memoBefore)
                        EquipmentTargetMemo.StoreValueBefore(snap, hostUnit, includeStealthTrait, valueBefore);
                }
                float registry = StrategicEffectRegistry.AttachmentValue(predicted.Stats, unpricedAfter, hostTags, opposition, army,
                        isHero, snap, includeStealthTrait, deployment, targets, hpSpent, attachmentTargets, hostUnit)
                    - valueBefore;
                if (beforeConsumption != null || afterConsumption != null)
                {
                    var baseState = new PredictedEquipmentState(before, hostAbilities);
                    var laterBefore = beforeConsumption?.Apply(baseState) ?? baseState;
                    var laterAfter = afterConsumption?.Apply(predicted) ?? predicted;
                    float Value(PredictedEquipmentState state) => StrategicEffectRegistry.AttachmentValue(state.Stats,
                        state.Abilities.Where(x => !EquipmentEfficiency.IsPriced(x)).ToList(), hostTags, opposition, army,
                        isHero, snap, includeStealthTrait, deployment, targets, hpSpent, attachmentTargets, hostUnit);
                    float later = Value(laterAfter) - Value(laterBefore);
                    registry = later + (registry - later) / Mathf.Max(1f, ctx.ExpectedActivations);
                }
                tactical += registry;
            }

            if (beforeConsumption != null || afterConsumption != null)
                breakdown += " single-use: first attack only";

            // The text is formatted only when something reads it. `before`, the predicted state and
            // the ability lists are built fresh per call and never mutated afterwards, so the
            // deferred string equals the eager one.
            return new EquipmentDelta(combat, tactical, () => "stats=" + string.Join(",", before.OrderBy(k => k.Key)
                    .Where(k => After(k.Key) != k.Value).Select(k => $"{k.Key}:{k.Value}->{After(k.Key)}"))
                + " abilities+= " + string.Join(",", predicted.Abilities.Except(hostAbilities).OrderBy(x => x, System.StringComparer.Ordinal))
                + " abilities-= " + string.Join(",", hostAbilities.Except(predicted.Abilities).OrderBy(x => x, System.StringComparer.Ordinal))
                + " " + breakdown + $" combat={combat:0.###} tactical={tactical:0.###}");
        }

        // A bounded read-only portfolio on THIS operator's existing staffed sources. It reuses
        // the ordinary recipient utility and legality checks. Fate-bearing outputs are omitted
        // to prevent recursively valuing a genome through another genome's future production.
        internal static IReadOnlyList<(float Utility, float CostIfSuccess, float PBefore, float PAfter)>
            OperatorOutputForecast(WorldSnapshot snap, CapabilityInventory inv, UnitData hero,
                int fateBefore, int fateAfter)
        {
            var result = new List<(float Utility, float CostIfSuccess, float PBefore, float PAfter)>();
            if (hero == null || snap?.Self == null || snap.Observer == null
                || fateBefore == fateAfter || snap.Development == null || s_operatorForecast)
                return result;
            s_operatorForecast = true;
            try
            {
                var sources = snap.Development.ForecastSources
                    .Where(g => g?.CardDef?.cardType == CardType.Equipment
                        && ReferenceEquals(g.Hero, hero)
                        && !(g.CardDef.equipment?.statChanges?.Any(c => c.stat == EquipmentStat.Fate) ?? false))
                    .GroupBy(g => g.CardDef.authoredKey, System.StringComparer.Ordinal)
                    .Select(g => g.First()).OrderBy(g => g.CardDef.authoredKey, System.StringComparer.Ordinal);
                var player = snap.Observer;
                PlayerRoot root = PlayerRootRegistry.FindFor(player);
                var options = new List<(DevelopmentOpportunity Op, float Utility, float Before, float After)>();
                foreach (GenerationStep source in sources)
                {
                    CardDefinition card = source.CardDef;
                    float alternative = snap.Development.ForecastSources
                        .Where(g => g != null && !ReferenceEquals(g.Hero, hero)
                            && ReferenceEquals(g.CardDef, card))
                        .Select(g => ResearchProductionSystem.EstimateSuccessChance(g.Hero, card))
                        .DefaultIfEmpty(0f).Max();
                    float beforeChance = Mathf.Max(alternative,
                        ResearchProductionSystem.EstimateSuccessChanceForFate(fateBefore, card));
                    float afterChance = Mathf.Max(alternative,
                        ResearchProductionSystem.EstimateSuccessChanceForFate(fateAfter, card));
                    if (Mathf.Approximately(beforeChance, afterChance)) continue;
                    foreach (DevelopmentOpportunity op in DevelopmentOpportunityEvaluator.EquipmentOpportunities(
                        source.Mode, source.FacilityHex, card, 1f, null, snap, inv, player, root,
                        null, out _, futureAttachment: true, forecastHand: snap.Self.Hand))
                    {
                        float utility = EquipmentUpgradeValue(op);
                        if (utility <= 0f) continue;
                        options.Add((op, utility, beforeChance, afterChance));
                    }
                }
                // The same finite horizon already used by Development. Net out pending ordinary
                // card demand; use the snapshot's bank-aware spendable stock, never raw reserved funds.
                ResourceBundle budget = snap.Economy != null ? snap.Economy.SpendableStockpile : default;
                foreach (ResourceType type in ResourceBundle.All)
                {
                    float available = Mathf.Max(0f, budget.Get(type)
                        + snap.Self.PerTurnIncome.Get(type) * AiConfigV2.devChainFundingHorizonTurns
                        - ActionPrice.PendingCardResourceDemand(snap, type));
                    budget.Add(type, available - budget.Get(type));
                }
                float ap = Mathf.Max(0f, snap.Development.ForecastActionPoints ?? snap.Self.ActionPoints);
                if (snap.Self.ApEconomy != null)
                    ap += Mathf.Max(0f, snap.Self.ApEconomy.BaseActionPoints
                        - (snap.Self.ApEconomy.WitnessedApDemand ??
                            snap.Self.ApEconomy.EstimatedArmyApDemand + snap.Self.ApEconomy.EstimatedCardApDemand))
                        * AiConfigV2.devChainFundingHorizonTurns;
                float apScale = Mathf.Lerp(AiConfigV2.apMarginalUtilFloor, 1f,
                    EffectEvaluationContext.ResolveMarginalApUtility(snap, null));
                var remaining = new Dictionary<string, float>(System.StringComparer.Ordinal);
                var attempts = new Dictionary<CardDefinition, int>();
                for (int use = 0; use < AiConfigV2.devFacilityExpectedUses; use++)
                {
                    int best = -1;
                    float bestValue = 0f, bestUtility = 0f, bestAttach = 0f, bestWeight = 0f;
                    string bestSlot = null;
                    for (int i = 0; i < options.Count; i++)
                    {
                        var o = options[i];
                        CardDefinition card = o.Op.Card;
                        float chance = Mathf.Max(o.Before, o.After);
                        if (chance <= 0f || card.resourceCost != null && ResourceBundle.All.Any(
                                t => card.resourceCost.Get(t) > budget.Get(t))) continue;
                        string slot = DevelopmentOpportunityEvaluator.RecipientKey(o.Op) + ":" + card.attachmentSlot;
                        float weight = remaining.TryGetValue(slot, out float w) ? w : 1f;
                        float attachAp = Mathf.Max(0, card.activationApCost);
                        float attemptAp = ResearchProductionSystem.AttemptApCost(card);
                        if (ap < attemptAp + chance * weight * attachAp) continue;
                        attempts.TryGetValue(card, out int made);
                        float repeat = DevelopmentDiversity.RepeatFactor(
                            DevelopmentDiversity.RecentAttempts(player, snap.TurnNumber, card) + made);
                        float u = o.Utility * repeat * weight;
                        float attach = ActionPrice.ToCardScore(attachAp * apScale) * weight;
                        float price = StrategicCardEvaluator.StrategicResourceCostValue(card.resourceCost,
                            snap, t => budget.Get(t), player) + ActionPrice.ToCardScore(attemptAp * apScale)
                            + AiConfigV2.stratChainAttachStepPenalty + AiConfigV2.stratChainGenerationStepPenalty;
                        float value = chance * (u - attach) - price;
                        if (value <= AiConfigV2.devEvMargin || value <= bestValue) continue;
                        best = i; bestValue = value; bestUtility = u; bestAttach = attach;
                        bestSlot = slot; bestWeight = weight;
                    }
                    if (best < 0) break;
                    var selected = options[best];
                    CardDefinition output = selected.Op.Card;
                    result.Add((bestUtility, bestAttach, selected.Before, selected.After));
                    float p = Mathf.Max(selected.Before, selected.After);
                    remaining[bestSlot] = bestWeight * (1f - p);
                    foreach (ResourceType type in ResourceBundle.All)
                        budget.Add(type, -(output.resourceCost?.Get(type) ?? 0));
                    ap -= ResearchProductionSystem.AttemptApCost(output)
                        + p * bestWeight * Mathf.Max(0, output.activationApCost);
                    attempts.TryGetValue(output, out int count);
                    attempts[output] = count + 1;
                }
                return result;
            }
            finally { s_operatorForecast = false; }
        }

        // The host as an AA carrier against the air armies honestly known to its owner: distance from its
        // army, owner vision of that hex, the carrier hidden state, an already used reaction, and the AA
        // members of the same army that fire before it (AntiAirRules order: distance, army, slot). The
        // rules owner stays AntiAirRules / AntiAirState; this only reads their inputs.
        private static List<AirContact> AirContactsFor(WorldSnapshot snap, ArmyData army, UnitData host,
            EfficiencyContext ctx)
        {
            var contacts = new List<AirContact>();
            ctx.AntiAirRadius = host.AntiAirRadius;
            ctx.HostHidden = host.IsHidden;
            if (snap?.Known?.EnemySightings == null || snap.Observer == null || army.Members == null)
                return contacts;
            int hostSlot = army.Members.IndexOf(host);
            foreach (AiMapMemory.KnownEnemySighting sighting in snap.Known.EnemySightings)
            {
                if (!sighting.IsAir || sighting.Defenders == null)
                    continue;
                var air = sighting.Defenders.Where(d => d.HitPoints > 0
                    && (d.TypeTags?.Contains(UnitTypeTag.Aircraft) ?? false)).ToList();
                if (air.Count == 0)
                    continue;
                var mean = new WorthIt.DefenderProfile(air.Average(d => d.Defense), false, air[0].TypeTags,
                    air.Average(d => d.Attack), air.Average(d => d.HitPoints), air[0].Initiative, air[0].Abilities);
                int distance = HexGridMath.Distance(army.Hex, sighting.Hex);
                int earlier = 0;
                for (int i = 0; i < army.Members.Count && i < hostSlot; i++)
                {
                    UnitData other = army.Members[i];
                    if (other != null && !other.IsHidden
                        && Game.Aviation.AntiAirRules.TryGetRadius(other, out int r) && distance <= r
                        && Game.Aviation.AntiAirState.CanReact(other, sighting.ArmyId))
                        earlier++;
                }
                contacts.Add(new AirContact(mean, distance, snap.Own.IsVisible(sighting.Hex),
                    !Game.Aviation.AntiAirState.CanReact(host, sighting.ArmyId), earlier));
            }
            return contacts;
        }

        // Known facts only: the mission's target hex gives the route the host's army will really walk
        // (AiV2Util.TravelCost — the same route the mission planners price), the army's activation AP is
        // the sum of its members, and the map-knowledge owner supplies the unexplored fraction. Anything
        // not known stays at the context default (speed proxy, 0.5 dark, no detection relevance).
        internal static void ApplyKnownRouteAndCoverage(EfficiencyContext ctx, WorldSnapshot snap, ArmyData army,
            UnitData hostUnit, HexCoord? targetHex, MissionIntent purpose = null)
        {
            // The facts depend on the recipient and its purpose only (a route search, a scout
            // cost estimate): one Enumerate computes them once per recipient, not per equipment.
            if (!EquipmentTargetMemo.TryGetRoute(snap, army, hostUnit, targetHex, purpose, out RouteFacts facts))
            {
                facts = KnownRouteAndCoverage(snap, army, hostUnit, targetHex, purpose);
                EquipmentTargetMemo.StoreRoute(snap, army, hostUnit, targetHex, purpose, facts);
            }
            if (facts.DarkFraction.HasValue) ctx.UsefulDarkFraction = facts.DarkFraction.Value;
            if (facts.StealthRisk.HasValue) ctx.StealthRisk = facts.StealthRisk.Value;
            if (facts.RouteLength.HasValue) ctx.RouteLength = facts.RouteLength.Value;
            if (facts.OtherArmyActivationAp.HasValue) ctx.OtherArmyActivationAp = facts.OtherArmyActivationAp.Value;
        }

        // The context fields ApplyKnownRouteAndCoverage sets; null = left at the context default.
        internal readonly struct RouteFacts
        {
            internal readonly float? DarkFraction;
            internal readonly float? StealthRisk;
            internal readonly int? RouteLength;
            internal readonly float? OtherArmyActivationAp;

            internal RouteFacts(float? darkFraction, float? stealthRisk = null, int? routeLength = null,
                float? otherArmyActivationAp = null)
            {
                DarkFraction = darkFraction;
                StealthRisk = stealthRisk;
                RouteLength = routeLength;
                OtherArmyActivationAp = otherArmyActivationAp;
            }
        }

        private static RouteFacts KnownRouteAndCoverage(WorldSnapshot snap, ArmyData army,
            UnitData hostUnit, HexCoord? targetHex, MissionIntent purpose)
        {
            float? dark = snap?.MapKnowledge != null
                ? Mathf.Clamp01(snap.MapKnowledge.ExplorableUnknownFrac) : (float?)null;
            // No snapshot = the reference context (Self equipment-reserve estimate): no known route.
            if (snap == null || army?.Members == null || hostUnit == null)
                return new RouteFacts(dark);
            ArmySnapshot actor = snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == army.Id);
            if (purpose?.Kind == MissionKind.Scout && purpose.Scout != null
                && !ReconScoutKinds.IsAirSweep(purpose.Scout.Kind)
                && !ReconScoutKinds.IsCapture(purpose.Scout.Kind))
            {
                var target = new ScoutMissionTarget { Kind = purpose.Scout.Kind,
                    FocusHex = purpose.Scout.FocusHex,
                    Stealth = purpose.Scout.RequiresStealth ? StealthRequirement.Required : StealthRequirement.None };
                ScoutCostEstimate route = ScoutCostModel.Estimate(snap, target,
                    preferredMoverArmyId: army.Id, requirePreferredMover: true);
                if (!route.MoverKnown || !route.ExecutionHex.HasValue) return new RouteFacts(dark);
                // The same honest risk owner used by objective/vantage scoring. A detector's
                // presence does not invent a hidden TARGET: DetectionRelevance stays unchanged.
                return new RouteFacts(dark, ScoutRiskModel.DetectorRisk(snap, route.ExecutionHex.Value),
                    Mathf.Max(0, Mathf.RoundToInt(route.EstimatedDistance)),
                    ArmyData.ComputeActivationApCost(
                        army.Members.Where(m => m != null && !ReferenceEquals(m, hostUnit))));
            }
            if (!targetHex.HasValue || army.Hex.Equals(targetHex.Value)) return new RouteFacts(dark);
            int cost = actor != null ? AiV2Util.TravelCost(snap, actor, targetHex.Value, terrainOnly: true)
                : HexGridMath.Distance(army.Hex, targetHex.Value);
            if (cost <= 0 || cost == int.MaxValue)
                return new RouteFacts(dark);
            return new RouteFacts(dark, routeLength: cost,
                otherArmyActivationAp: ArmyData.ComputeActivationApCost(
                    army.Members.Where(m => m != null && !ReferenceEquals(m, hostUnit))));
        }

        // Read existing assignments only. An upgrade does not create a role or move an actor.
        internal static MissionIntent EquipmentPurpose(WorldSnapshot snap, CardData card, UnitData unit)
        {
            object host = unit != null ? unit : (object)card;
            if (host != null && EquipmentTargetMemo.TryGetPurpose(snap, host, out MissionIntent cached))
                return cached;
            MissionIntent found = FindEquipmentPurpose(snap, card, unit);
            if (host != null) EquipmentTargetMemo.StorePurpose(snap, host, found);
            return found;
        }

        private static MissionIntent FindEquipmentPurpose(WorldSnapshot snap, CardData card, UnitData unit)
        {
            ArmyData army = unit != null ? OwnWorld.Of(snap?.Observer).ArmyContaining(unit) : null;
            string key = unit != null ? StrikeRoster.UnitKey(unit) : StrikeRoster.CardKey(card?.Definition);
            var intents = snap?.Intents?.All;
            return intents?.Where(i => i != null && i.Status == IntentStatus.Active && !i.IsLifecycleLeg
                && (unit != null && object.ReferenceEquals(i.Development?.Hero, unit)
                    || i.PreferredMoverArmyId == army?.Id && army != null
                    || army != null && GroundCombatLegs.HeldGroundSupportArmyIds(i).Contains(army.Id)
                    || i.Attack?.Preparation == true && key != null
                        && i.Attack.TargetRoster?.Any(r => r.Key == key) == true))
                .OrderBy(i => unit != null && object.ReferenceEquals(i.Development?.Hero, unit) ? 0
                    : i.Kind == MissionKind.Attack ? 1 : i.Kind == MissionKind.Scout ? 2 : 3)
                .ThenBy(i => i.IntentKey).FirstOrDefault();
        }

        internal static string EquipmentPurposeLabel(WorldSnapshot snap, CardData card, UnitData unit)
        {
            MissionIntent intent = EquipmentPurpose(snap, card, unit);
            if (intent != null && intent.Kind != MissionKind.Economy)
                return intent.Kind.ToString();
            string key = unit != null ? StrikeRoster.UnitKey(unit) : StrikeRoster.CardKey(card?.Definition);
            return AttackForceReadiness.MobilizationOpen(snap?.Self)
                && snap.Self.StrikeRoster.Any(r => r.Key == key) ? "AttackPreparation" : "in-advance";
        }

        private static List<WorthIt.DefendingArmy> EquipmentOpposition(WorldSnapshot snap, MissionIntent purpose)
        {
            if (EquipmentTargetMemo.TryGetOpposition(snap, purpose, out var cached))
                return cached;
            List<WorthIt.DefendingArmy> built = BuildEquipmentOpposition(snap, purpose);
            EquipmentTargetMemo.StoreOpposition(snap, purpose, built);
            return built;
        }

        private static List<WorthIt.DefendingArmy> BuildEquipmentOpposition(WorldSnapshot snap, MissionIntent purpose)
        {
            var scoped = new List<WorthIt.DefendingArmy>();
            // A target's location comes only from Known, never hidden TrueWorld coordinates.
            HexCoord? at = purpose?.Attack?.Target.HasValue == true ? purpose.Attack.Target.Hex
                : purpose?.Raid?.Target.HasValue == true ? purpose.Raid.TargetHex : null;
            int? enemyId = purpose?.ActiveDefence?.EnemyArmyId
                ?? (purpose?.Raid?.Target.HasValue == true && purpose.Raid.Target.Kind == RaidTargetKind.NeutralArmy
                    ? (int?)purpose.Raid.TargetArmyId : null);
            foreach (var sighting in (snap?.Known?.EnemySightings ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap?.Known?.NeutralSightings ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>()))
                if ((at.HasValue && sighting.Hex.Equals(at.Value)
                    || enemyId.HasValue && sighting.ArmyId == enemyId.Value) && sighting.Defenders?.Count > 0)
                    scoped.Add(new WorthIt.DefendingArmy(sighting.Defenders, sighting.Commander));
            if (at.HasValue)
                foreach (var guard in snap?.Known?.EventGuards ?? System.Array.Empty<KnownEventGuardSnapshot>())
                    if (guard.Hex.Equals(at.Value) && guard.Defenders?.Count > 0)
                        scoped.Add(new WorthIt.DefendingArmy(guard.Defenders, guard.Commander));
            return scoped.Count > 0 ? scoped : EquipmentValuationThreats(snap);
        }

        // One settled Development Enumerate prices many (equipment, recipient) pairs against the
        // same snapshot; the target set (and its sort) depends only on snapshot + purpose.
        // Memoised for that scope only; outside it nothing is cached.
        internal sealed class EquipmentTargetMemo : System.IDisposable
        {
            [System.ThreadStatic] private static EquipmentTargetMemo s_current;
            private readonly EquipmentTargetMemo _outer;
            private readonly Dictionary<(WorldSnapshot, MissionIntent), List<WorthIt.DefenderProfile>> _sets = new();

            // Opposition and the owning purpose depend only on snapshot + mission / recipient inside
            // one Enumerate. Consumers treat the cached list as read-only.
            private readonly Dictionary<(WorldSnapshot, MissionIntent), List<WorthIt.DefendingArmy>> _opposition = new();
            private readonly Dictionary<(WorldSnapshot, object), MissionIntent> _purposes = new();

            internal static bool TryGetOpposition(WorldSnapshot snap, MissionIntent purpose,
                out List<WorthIt.DefendingArmy> opposition)
            {
                opposition = null;
                return s_current != null && s_current._opposition.TryGetValue((snap, purpose), out opposition);
            }

            internal static void StoreOpposition(WorldSnapshot snap, MissionIntent purpose,
                List<WorthIt.DefendingArmy> opposition)
            {
                if (s_current != null) s_current._opposition[(snap, purpose)] = opposition;
            }

            // Sorted target / battle selections of one opposition: shared by the before and after
            // pricing and, inside a scope, by every pair against the same snapshot + purpose.
            private readonly Dictionary<(WorldSnapshot, MissionIntent), StrategicEffectRegistry.AttachmentTargetSet>
                _attachmentSets = new();

            internal static StrategicEffectRegistry.AttachmentTargetSet AttachmentTargets(WorldSnapshot snap,
                MissionIntent purpose, List<WorthIt.DefendingArmy> opposition,
                IReadOnlyList<WorthIt.DefenderProfile> referenceTargets)
            {
                if (s_current == null)
                    return new StrategicEffectRegistry.AttachmentTargetSet(opposition, referenceTargets);
                if (!s_current._attachmentSets.TryGetValue((snap, purpose), out var set))
                    s_current._attachmentSets[(snap, purpose)] = set =
                        new StrategicEffectRegistry.AttachmentTargetSet(opposition, referenceTargets);
                return set;
            }

            internal static bool TryGetPurpose(WorldSnapshot snap, object host, out MissionIntent purpose)
            {
                purpose = null;
                return s_current != null && s_current._purposes.TryGetValue((snap, host), out purpose);
            }

            internal static void StorePurpose(WorldSnapshot snap, object host, MissionIntent purpose)
            {
                if (s_current != null) s_current._purposes[(snap, host)] = purpose;
            }

            // Recipient-only context (no equipment in the key): the host's AA contacts, its route and
            // coverage facts and the registry value of its abilities before any attachment.
            private readonly Dictionary<(WorldSnapshot, ArmyData, UnitData), List<AirContact>> _airContacts = new();
            private readonly Dictionary<(WorldSnapshot, ArmyData, UnitData, HexCoord?, MissionIntent), RouteFacts> _routes = new();
            private readonly Dictionary<(WorldSnapshot, UnitData, bool), float> _valuesBefore = new();

            internal static bool TryGetAirContacts(WorldSnapshot snap, ArmyData army, UnitData host,
                out List<AirContact> contacts)
            {
                contacts = null;
                return s_current != null && s_current._airContacts.TryGetValue((snap, army, host), out contacts);
            }

            internal static void StoreAirContacts(WorldSnapshot snap, ArmyData army, UnitData host,
                List<AirContact> contacts)
            {
                if (s_current != null) s_current._airContacts[(snap, army, host)] = contacts;
            }

            internal static bool TryGetRoute(WorldSnapshot snap, ArmyData army, UnitData host,
                HexCoord? targetHex, MissionIntent purpose, out RouteFacts facts)
            {
                facts = default;
                return s_current != null
                    && s_current._routes.TryGetValue((snap, army, host, targetHex, purpose), out facts);
            }

            internal static void StoreRoute(WorldSnapshot snap, ArmyData army, UnitData host,
                HexCoord? targetHex, MissionIntent purpose, RouteFacts facts)
            {
                if (s_current != null) s_current._routes[(snap, army, host, targetHex, purpose)] = facts;
            }

            internal static bool TryGetValueBefore(WorldSnapshot snap, UnitData host, bool includeStealthTrait,
                out float value)
            {
                value = 0f;
                return s_current != null
                    && s_current._valuesBefore.TryGetValue((snap, host, includeStealthTrait), out value);
            }

            internal static void StoreValueBefore(WorldSnapshot snap, UnitData host, bool includeStealthTrait,
                float value)
            {
                if (s_current != null) s_current._valuesBefore[(snap, host, includeStealthTrait)] = value;
            }

            public EquipmentTargetMemo() { _outer = s_current; s_current = this; }
            public void Dispose() => s_current = _outer;

            // A memo filled in several pieces (Development's paced verdict warm-up yields between
            // equipment cards): created inactive, then entered around each piece, so the scope is
            // never left active across a frame. Valid because nothing mutates the world between AI
            // frames (FramePacer); the keys still include the snapshot.
            private EquipmentTargetMemo(bool activate)
            {
                if (activate) { _outer = s_current; s_current = this; }
            }

            internal static EquipmentTargetMemo Detached() => new EquipmentTargetMemo(false);

            internal System.IDisposable Enter() => new Entry(this);

            private sealed class Entry : System.IDisposable
            {
                private readonly EquipmentTargetMemo _previous;
                internal Entry(EquipmentTargetMemo memo) { _previous = s_current; s_current = memo; }
                public void Dispose() => s_current = _previous;
            }

            internal static bool TryGet(WorldSnapshot snap, MissionIntent purpose,
                out List<WorthIt.DefenderProfile> targets)
            {
                targets = null;
                return s_current != null && s_current._sets.TryGetValue((snap, purpose), out targets);
            }

            internal static void Store(WorldSnapshot snap, MissionIntent purpose,
                List<WorthIt.DefenderProfile> targets)
            {
                if (s_current != null) s_current._sets[(snap, purpose)] = targets;
            }

            // Armoured / bio / air / mechanical shares of one target list: the list is shared by every pair priced
            // against the same snapshot + purpose, so it is scanned once (reference-keyed).
            private readonly Dictionary<List<WorthIt.DefenderProfile>, (float Armored, float Bio, float Air, float Mechanical)> _shares
                = new();

            internal static (float Armored, float Bio, float Air, float Mechanical) Shares(List<WorthIt.DefenderProfile> targets)
            {
                if (s_current != null && s_current._shares.TryGetValue(targets, out var hit))
                    return hit;
                float n = targets.Count;
                var shares = (
                    targets.Count(t => t.TypeTags != null && (t.TypeTags.Contains(UnitTypeTag.Armored)
                        || t.TypeTags.Contains(UnitTypeTag.Vehicle) || t.TypeTags.Contains(UnitTypeTag.Mecha))) / n,
                    targets.Count(t => t.TypeTags != null && (t.TypeTags.Contains(UnitTypeTag.Bio)
                        || t.TypeTags.Contains(UnitTypeTag.Infantry))) / n,
                    targets.Count(t => t.TypeTags != null && t.TypeTags.Contains(UnitTypeTag.Aircraft)) / n,
                    targets.Count(t => t.TypeTags != null && t.TypeTags.Contains(UnitTypeTag.Mechanical)) / n);
                if (s_current != null) s_current._shares[targets] = shares;
                return shares;
            }
        }

        private static List<WorthIt.DefenderProfile> EquipmentTargetsFor(WorldSnapshot snap,
            MissionIntent purpose)
        {
            if (EquipmentTargetMemo.TryGet(snap, purpose, out var cached))
                return cached;
            List<WorthIt.DefenderProfile> targets = SortTargets(
                EquipmentTargets(snap, EquipmentOpposition(snap, purpose)));
            EquipmentTargetMemo.Store(snap, purpose, targets);
            return targets;
        }

        private static List<WorthIt.DefenderProfile> SortTargets(List<WorthIt.DefenderProfile> targets) =>
            targets.OrderBy(p => p.Defense).ThenBy(p => p.Attack)
                .ThenBy(p => p.HitPoints)
                .ThenBy(p => string.Join(",", p.TypeTags.OrderBy(t => t)))
                .ThenBy(p => string.Join(",", p.Abilities.OrderBy(a => a, System.StringComparer.Ordinal))).ToList();

        private static List<WorthIt.DefenderProfile> EquipmentTargets(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition)
        {
            var scoped = opposition.SelectMany(a => a.Units).Where(p => !p.IsHero && p.HitPoints > 0).ToList();
            if (scoped.Count == 0)
            {
                // The owner's available composition gives absolute stat reference points. These
                // are stat-only benchmarks, not invented enemy Bio/Armored/Air targets.
                var deck = (snap?.Self?.Deck ?? System.Array.Empty<CardDefinition>())
                    .Concat(snap?.Self?.Hand?.Where(c => c?.Definition != null).Select(c => c.Definition)
                        ?? System.Array.Empty<CardDefinition>())
                    .Where(d => d != null && d.cardType == CardType.Unit
                        && !(d.unitTypeTags?.Contains(UnitTypeTag.Aircraft) ?? false))
                    .Select(d => AiPower.EffectiveLine(d, null)).ToList();
                var reference = deck.Select(d => new WorthIt.DefenderProfile(d.Defense, false, null,
                        d.Attack, d.HitPoints))
                    .Concat((snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                        .Where(a => a?.Members != null).SelectMany(a => a.Members)
                        .Where(p => !p.IsHero && p.IsGroundCombatant
                            && !p.TypeTags.Contains(UnitTypeTag.Aircraft))).ToList();
                if (reference.Count > 0)
                {
                    scoped.Add(new WorthIt.DefenderProfile(reference.Average(d => d.Defense), false, null,
                        reference.Average(d => d.Attack), reference.Average(d => d.HitPoints)));
                    scoped.Add(new WorthIt.DefenderProfile(reference.Max(d => d.Defense), false, null,
                        reference.Max(d => d.Attack), reference.Max(d => d.HitPoints)));
                }
            }
            return scoped.Where(p => !p.IsHero && p.HitPoints > 0).ToList();
        }


        private sealed class LazyText
        {
            private System.Func<string> _make;
            private string _value;
            public LazyText(System.Func<string> make) { _make = make; }
            public string Value
            {
                get
                {
                    if (_make != null)
                    {
                        using var __detail = new Game.Core.ProfileScope("AI/Equip.EffDetail");
                        _value = _make();
                        _make = null;
                    }
                    return _value;
                }
            }
        }

        internal readonly struct EquipmentDelta
        {
            public readonly float Combat;
            public readonly float Tactical;
            private readonly string _detail;
            private readonly LazyText _lazy;
            public EquipmentDelta(float combat, float tactical, string detail = null)
            { Combat = combat; Tactical = tactical; _detail = detail; _lazy = null; }
            public EquipmentDelta(float combat, float tactical, System.Func<string> detail)
            { Combat = combat; Tactical = tactical; _detail = null; _lazy = new LazyText(detail); }
            public string Detail => _lazy != null ? _lazy.Value : _detail;
            public float Total => Combat + Tactical;
        }
    }
}
