using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Self — BuildSelf, army/ability projection (ToArmySnapshot + its helpers, BuildApActionEconomy), and ArmyVisionRadius (also used by BuildTrueWorld in WorldAnalysis.Knowledge.cs).
    // File-split (mechanical, no behaviour change) from WorldAnalysis.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 5. Still exactly the WorldAnalysis
    // class; only this snapshot family's slice moved to its own file.
    public static partial class WorldAnalysis
    {
        private const int NoHeroStackCapacity = 2;

        private static SelfSnapshot BuildSelf(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var self = new SelfSnapshot();

            List<ArmyData> ownArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !a.IsPrison)
                .ToList();

            var baseHexes = ownArmies
                .Where(a => a.IsGarrison)
                .Select(a => a.Hex)
                .Distinct()
                .ToList();

            HexCoord citadel = player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue
                ? new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value)
                : (baseHexes.Count > 0 ? baseHexes[0] : default);
            if (baseHexes.Count == 0)
                baseHexes.Add(citadel);

            self.Citadel = citadel;
            self.BaseHexes = baseHexes;
            self.Armies = ownArmies.Select(a => ToArmySnapshot(a, player, isOwn: true, ArmyVisionRadius(ctx))).ToList();

            self.FieldPower = self.Armies.Where(a => !a.IsGarrison).Sum(a => a.EffectiveArmyPower);
            self.GarrisonPower = self.Armies.Where(a => a.IsGarrison).Sum(a => a.EffectiveArmyPower);
            self.TotalPower = self.FieldPower + self.GarrisonPower;

            foreach (ResourceType t in ResourceBundle.All)
            {
                self.Stockpile.Add(t, root != null ? root.GetResource(t) : 0);
                self.PerTurnIncome.Add(t, IncomeProjection.IncomeFor(player, t, ctx.Map));
            }
            self.ActionPoints = root != null ? root.ActionPoints : 0;

            self.Hand = hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>();
            self.Deck = hand?.RemainingDeck ?? (IReadOnlyList<CardDefinition>)System.Array.Empty<CardDefinition>();
            self.HandCapacity = hand?.Capacity ?? 0;
            self.HasFreeHandSlot = hand?.HasFreeSlot ?? false;

            ReconAirObservationCapacity airObs = ReconAirCapacityPolicy.Evaluate(player, root);
            self.AirborneReconWings = airObs.AirborneReconWings;
            self.SpareAirObservationSorties = airObs.SpareSorties;

            self.HasDevFacility = BuildingRegistry.AllBuildings().Any(b => b != null && b.Owner == player
                && (b.HasFacilityWithAbility(UnitAbilities.Research) || b.HasFacilityWithAbility(UnitAbilities.Production)));
            self.HasDevOperator = ownArmies
                .SelectMany(a => a.Members)
                .Any(m => m != null && m.IsHero
                    && (m.HasAbility(UnitAbilities.Researcher) || m.HasAbility(UnitAbilities.Assembler)));

            BuildApActionEconomy(self, player, ownArmies);

            var nowPool = new List<AiPower.PowerUnit>();
            int nowCap = NoHeroStackCapacity;
            foreach (ArmyData a in ownArmies)
                foreach (UnitData m in a.Members)
                {
                    nowPool.Add(AiPower.ToPowerUnit(m));
                    if (m.IsHero && m.CommandRating > nowCap) nowCap = m.CommandRating;
                }
            foreach (CardData c in self.Hand)
                if (c?.Definition != null && IsMilitaryCard(c.Definition))
                {
                    nowPool.Add(AiPower.ToPowerUnit(c.Definition));
                    if (c.Definition.cardType == CardType.Hero && c.Definition.commandRating > nowCap)
                        nowCap = c.Definition.commandRating;
                }

            var ceilingPool = new List<AiPower.PowerUnit>(nowPool);
            int ceilingCap = nowCap;
            foreach (CardDefinition d in self.Deck)
                if (d != null && IsMilitaryCard(d))
                {
                    ceilingPool.Add(AiPower.ToPowerUnit(d));
                    if (d.cardType == CardType.Hero && d.commandRating > ceilingCap)
                        ceilingCap = d.commandRating;
                }

            self.BestStackPotential = AiPower.BestStackPotential(nowPool, nowCap);
            self.TotalMilitaryPotential = AiPower.TotalMilitaryPotential(ceilingPool, ceilingCap);

            return self;
        }

        private static bool IsMilitaryCard(CardDefinition d) =>
            d.cardType == CardType.Unit || d.cardType == CardType.Hero;

        // AI-MGR — Dynamic Strategic Effect Utility. Snapshot-pure AP action-economy read: how many
        // recurring-AP sources are in play, how much AP the AI could still usefully spend this turn,
        // and from those the marginal value of one more AP/turn. ACTION economy only — never a
        // H/E/M/T security read. Also counts the non-hero bodies a hero's Command could realistically
        // put to use.
        private static void BuildApActionEconomy(SelfSnapshot self, PlayerSetupData player,
            List<ArmyData> ownArmies)
        {
            int recurringApSources = 0;
            foreach (ArmyData a in ownArmies)
            {
                if (a.IsPrison) continue;
                foreach (UnitData m in a.Members)
                    if (m != null && m.HasAbility(UnitAbilities.ApBonus))
                        recurringApSources++;
            }
            foreach (BuildingData b in BuildingRegistry.AllBuildings())
            {
                if (b == null || b.Owner != player) continue;
                if (b.HasAbility(UnitAbilities.ApBonus))
                    recurringApSources++;
                if (b.FacilitySlots != null)
                    foreach (FacilityData f in b.FacilitySlots)
                        if (f != null && f.HasAbility(UnitAbilities.ApBonus))
                            recurringApSources++;
            }

            int unactivatedArmies = 0;
            float armyApDemand = 0f;
            int nonHeroBodies = 0;
            foreach (ArmyData a in ownArmies)
            {
                foreach (UnitData m in a.Members)
                    if (m != null && !m.IsHero) nonHeroBodies++;
                if (a.IsGarrison || a.IsPrison || a.IsAirArmy || a.Members.Count == 0) continue;
                if (a.HasActivatedThisTurn) continue;
                unactivatedArmies++;
                armyApDemand += Mathf.Max(1, a.ActivationApCost);
            }

            int apCards = 0;
            float cardApDemand = 0f;
            foreach (CardData c in self.Hand)
            {
                float ap = c != null ? c.EffectivePlayApCost : 0f;
                if (ap > 0f) { apCards++; cardApDemand += ap; }
                if (c?.Definition != null && c.Definition.cardType == CardType.Unit) nonHeroBodies++;
            }

            float devDemand = self.HasDevFacility && self.HasDevOperator ? AiConfigV2.apDevActionApProxy : 0f;
            float airDemand = (self.AirborneReconWings + self.SpareAirObservationSorties) * AiConfigV2.apAirSortieApProxy;

            // STRUCTURAL FACTS ONLY — an upper bound. WorldAnalysis does not decide which of these
            // are useful/legal this turn; the owner-witnessed AP workload is assembled at evaluation
            // time in StrategicManager Phase A/B. These feed only the discounted structural fallback
            // (StrategicEffectRegistry.StructuralFallbackApDemand). No ramp here.
            self.ApEconomy = new ApActionEconomySnapshot
            {
                BaseActionPoints = self.ActionPoints,
                RecurringApSources = recurringApSources,
                RecurringApPerTurn = recurringApSources * UnitAbilities.ApBonusActionPointsPerSource,
                UnactivatedActionableArmies = unactivatedArmies,
                ApCostingHandActions = apCards,
                EstimatedArmyApDemand = armyApDemand,
                EstimatedCardApDemand = cardApDemand,
                EstimatedDevelopmentApDemand = devDemand,
                EstimatedAirApDemand = airDemand,
            };
            self.DeployableCombatBodies = nonHeroBodies;
        }

        private static ArmySnapshot ToArmySnapshot(ArmyData a, PlayerSetupData viewer, bool isOwn, int armyVisionRadius)
        {
            var nonHero = a.Members.Where(m => !m.IsHero).ToList();
            bool allHidden = !isOwn && a.Members.Count > 0
                && a.Members.All(m => StealthSystem.IsHiddenFrom(m, viewer));

            return new ArmySnapshot
            {
                ArmyId = a.Id,
                Owner = a.Owner,
                Hex = a.Hex,
                IsGarrison = a.IsGarrison,
                IsPrison = a.IsPrison,
                IsAir = a.IsAirArmy,
                IsAirfield = a.IsAirfield,
                MemberCount = a.Members.Count,
                HasHero = a.Members.Any(m => m.IsHero),
                HeroCommandRating = a.Members.Where(m => m.IsHero).Select(m => m.CommandRating).DefaultIfEmpty(0).Max(),
                HasAntiAir = a.Members.Any(m => m.HasAbility(UnitAbilities.AntiAir)),
                // review-r4 P1 ARCH — the coverage roles come from StrategicEffectRegistry, so a new
                // counter/support/mobility mechanic flows in without editing this file.
                StrategicCoverage = StrategicCoverageOf(a),
                // final closure §3.3 — own-army ally auras, so the effect context can price the
                // marginal buff a standing aura gives an incoming candidate. Empty until an aura row
                // exists in the registry.
                AllyAuraEffects = isOwn ? AllyAuraEffectsOf(a) : System.Array.Empty<StrategicEffect>(),
                IsHiddenFromUs = allHidden,
                AttackSum = WorthIt.AttackSum(a),
                DefenseSum = WorthIt.DefenseSum(a),
                EffectiveArmyPower = AiPower.EffectiveArmyPower(a.Members),
                CompositionQuality = AiPower.CompositionQualityOf(a.Members),
                MaxMovement = a.MaxMovement,
                Capacity = a.Capacity,
                OccupiedBattleSlots = a.Members.Count,
                Members = nonHero.Select(WorthIt.FromLiveUnit).ToList(),
                MembersWithHeroes = a.Members.Select(WorthIt.FromLiveUnit).ToList(),
                NonHeroActivationApCosts = nonHero.Select(u => u.ActivationApCost).ToList(),
                NonHeroMoveMax = nonHero.Select(u => u.MoveMax).ToList(),
                NonHeroIsAviation = nonHero.Select(u => u.IsAviation).ToList(),
                HeroActivationApCost = a.Members.Where(u => u.IsHero)
                    .Sum(u => u.ActivationApCost),
                HeroMoveMax = a.Members.Where(u => u.IsHero)
                    .Select(u => u.MoveMax).DefaultIfEmpty(a.MaxMovement).Min(),
                HeroIsHomeVocation = a.Members.Where(u => u.IsHero)
                    .Any(HeroRoleEvaluator.HasSupportVocation),
                ActivationApCost = a.ActivationApCost,
                ActivationEnergyCost = a.ActivationEnergyCost,
                HasActivatedThisTurn = a.HasActivatedThisTurn,
                CurrentMovement = a.CurrentMovement,
                IsSoloRecce = isOwn && AiArmyRoles.IsSoloRecce(a),
                IsMobileEconomyBuilder = isOwn && AiArmyRoles.IsHeroLed(a),
                IsStructuralRaidActor = isOwn
                    && !a.IsPrison && !a.IsGarrison && !a.IsAirArmy && !a.IsAirfield
                    && !AiArmyRoles.IsSoloRecce(a) && !AiArmyRoles.IsSoloHeroAwaitingEscort(a)
                    && a.Members.Count > 0,
                IsHidden = isOwn && a.Members.Count > 0 && a.Members.All(m => m.IsHidden),
                CanEnterStealth = isOwn && a.Members.Any(StealthSystem.CanEnterStealth),
                StealthLevel = isOwn
                    ? a.Members.Select(AbilityParams.GetStealthLevel).DefaultIfEmpty(0).Max()
                    : 0,
                EffectiveVisionRadius = armyVisionRadius + AbilityParams.GetBestRecceRadius(a),
                CollectionCapacity = isOwn ? CollectionCapacityOf(a) : default(ResourceBundle),
            };
        }

        private static ResourceBundle CollectionCapacityOf(ArmyData army)
        {
            var result = new ResourceBundle();
            if (army?.Members == null)
                return result;
            foreach (ResourceType type in ResourceBundle.All)
                result.Add(type, army.Members.Count(member => member != null
                    && member.HasAbility(UnitAbilities.CollectAbilityFor(type))));
            return result;
        }

        // review-r4 P1 ARCH — union of every member's registry-resolved coverage roles (abilities +
        // effective moveMax). One place, no per-role branch.
        private static RoleCoverage StrategicCoverageOf(ArmyData a)
        {
            RoleCoverage c = RoleCoverage.None;
            if (a?.Members != null)
                foreach (UnitData m in a.Members)
                    if (m != null)
                        c = c.Union(StrategicEffectRegistry.CoverageOf(m.Abilities, m.MoveMax));
            return c;
        }

        // final closure §3.3 — the ally-aura effects standing in `a` (members' registry-resolved
        // effects whose context is EligibleAllies). One place, no per-ability branch. Empty until an
        // aura row is added to StrategicEffectRegistry.ByAbility.
        private static IReadOnlyList<StrategicEffect> AllyAuraEffectsOf(ArmyData a)
        {
            List<StrategicEffect> list = null;
            if (a?.Members != null)
                foreach (UnitData m in a.Members)
                {
                    if (m == null) continue;
                    foreach (StrategicEffect e in StrategicEffectRegistry.Resolve(m.Abilities, m.MoveMax))
                        if (e.Context == StrategicEffectContext.EligibleAllies)
                            (list ??= new List<StrategicEffect>()).Add(e);
                }
            return list ?? (IReadOnlyList<StrategicEffect>)System.Array.Empty<StrategicEffect>();
        }

        private static int ArmyVisionRadius(AiTurnContext ctx) =>
            ctx != null && ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0;

    }
}
