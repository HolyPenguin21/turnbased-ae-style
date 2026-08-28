using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Level-1 planner for the Development category (spec P0 §6 — Research AND Production under one
    // planner, never two independent directions). It:
    //   • gathers every card the ResearchProductionCatalog offers this player's faction, in both
    //     modes, scores each as a concrete investment (spec §8 — ScoreCard below), and picks the
    //     best affordable one;
    //   • if a qualifying Researcher/Assembler Hero is already standing on the matching Lab/
    //     Factory, emits a RunResearchProduction decision straight away;
    //   • otherwise assigns a free (non-garrison) Researcher/Assembler Hero to the nearest such
    //     Facility and walks its army there as an ordinary scored MoveArmy (spec §7 — the move
    //     still competes with Defence / retreat / everything else), via a Develop AiTask;
    //   • runs the Challenge headlessly (ResearchProductionSystem.RollChallenge) and, on success,
    //     mints the produced card into AiHandData.
    //
    // Guards, all enforced here and again in the routine right before anything is spent:
    //   §9  resource surplus — a card is eligible only if, per resource type, the free surplus
    //       (AiResourceReservation.Available, after BuildFacility/BuildBase/air reservations)
    //       minus the card's cost stays at/above AiConfig.developmentMinResourceKeep;
    //   §10 hand capacity — AiHandData.HasFreeSlot (the shared CardHandUI.MaxHandSize);
    //   §11 no repeat Challenge — ctx.HasTriedDevelopment(hero, mode, card) skips a combination
    //       already attempted this turn (win or lose);
    //   §12 Stealth — a Research candidate whose Hero is hidden takes a danger-scaled penalty
    //       (ScoreCard), and the routine actually reveals the Hero (ApplyResearchReveal).
    //
    // The whole category is deliberately OUTSIDE AiTurnBudget's AP split (see AiTurnBudget) — the
    // Challenge itself costs the player no AP (spec §6); only the positioning move does, and that
    // rides the ordinary AP accounting like any other MoveArmy. The global desire to develop at
    // all is AiStrategyAssessment.Development, applied on top by AiStrategyLayer.Adjust.
    public static class AiDevelopmentPlanner
    {
        private static readonly ResearchProductionMode[] Modes =
        {
            ResearchProductionMode.Research, ResearchProductionMode.Production,
        };

        // One scored, affordable Research/Production candidate for a specific (hero, mode) at a
        // specific Facility hex.
        private readonly struct DevelopPick
        {
            public readonly CardDefinition Card;
            public readonly float Score;
            public readonly string Reason;

            public DevelopPick(CardDefinition card, float score, string reason)
            {
                Card = card;
                Score = score;
                Reason = reason;
            }
        }

        // ---- candidate gathering (called from AiTurnController.Decide) ----------------------

        public static List<AiDecision> TryStartDevelopmentCandidates(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AiHandData hand, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            if (player == null || root == null || ctx == null || ctx.ResearchProductionCatalog == null || hand == null)
                return results;
            if (AiTaskRegistry.CountActive(player, AiTaskKind.Develop) >= AiConfig.maxConcurrentDevelop)
                return results;
            if (!hand.HasFreeSlot) // spec §10 — a won Challenge would have nowhere to go
                return results;

            List<BuildingData> ownBuildings = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player)
                .ToList();

            // 1) Act where a qualifying Hero already stands — no positioning needed.
            foreach (BuildingData building in ownBuildings)
            {
                foreach (ResearchProductionMode mode in Modes)
                {
                    if (!building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode)))
                        continue;
                    HexCoord hex = building.Hex;
                    if (!ResearchProductionSystem.IsEligible(player, hex, mode, out _))
                        continue;
                    UnitData hero = ResearchProductionSystem.FindActor(player, hex, mode);
                    if (hero == null)
                        continue;
                    DevelopPick? pick = ScoreBestCard(player, root, ctx, hero, mode, hex);
                    if (pick.HasValue)
                        results.Add(AiDecision.RunResearchProduction(hero, hex, mode, pick.Value.Card, null,
                            pick.Value.Score, pick.Value.Reason));
                }
            }
            if (results.Count > 0)
                return results; // prefer acting on the spot over marching a hero somewhere

            // 2) Position a free (non-garrison) Researcher/Assembler Hero toward the nearest
            //    matching Facility (spec §7). The garrison is never dragged across the map — only
            //    a hero already out in a field/reserve army is a positioning candidate.
            foreach (ArmyData army in pool.AvailableArmies().ToList())
            {
                if (army == null || army.IsGarrison || army.IsPrison || army.Members.Count == 0 || army.Controller == null)
                    continue;

                foreach (ResearchProductionMode mode in Modes)
                {
                    string role = ResearchProductionSystem.RoleAbility(mode);
                    UnitData hero = army.Members.FirstOrDefault(m => m != null && m.IsHero && !m.IsPrisoner && m.HasAbility(role));
                    if (hero == null)
                        continue;

                    BuildingData target = ownBuildings
                        .Where(b => !b.Hex.Equals(army.Hex)
                            && b.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode)))
                        .OrderBy(b => HexGridMath.Distance(army.Hex, b.Hex))
                        .FirstOrDefault(b => HexGridMath.Distance(army.Hex, b.Hex) <= AiConfig.developmentPositioningMaxDistance);
                    if (target == null)
                        continue;

                    // Don't march for nothing — only if something is actually worth producing there.
                    if (!ScoreBestCard(player, root, ctx, hero, mode, target.Hex).HasValue)
                        continue;
                    if (!AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, target.Hex))
                        continue;

                    var task = new AiTask
                    {
                        Kind = AiTaskKind.Develop,
                        Army = army,
                        TargetHex = target.Hex,
                        DevelopMode = mode,
                        Reason = $"{hero.Name} → {mode} at ({target.Hex.Q},{target.Hex.R})",
                    };
                    results.Add(AiDecision.Move(army, target.Hex,
                        $"moves {hero.Name} toward the {FacilityLabel(mode)} at ({target.Hex.Q},{target.Hex.R}) to {mode}",
                        task, AiConfig.developmentPositioningMoveScore, AiTaskCategory.Development));
                }
            }
            return results;
        }

        // In-flight Develop task (called from AiTurnController.Decide's continue-task loop).
        public static AiDecision TryContinueDevelopTask(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AiHandData hand, AiTask task)
        {
            if (task?.Army == null || task.DevelopMode == null || task.Army.Controller == null
                || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            ResearchProductionMode mode = task.DevelopMode.Value;
            string role = ResearchProductionSystem.RoleAbility(mode);
            UnitData hero = task.Army.Members.FirstOrDefault(m => m != null && m.IsHero && !m.IsPrisoner && m.HasAbility(role));
            if (hero == null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: Development — \"{task.Army.Name}\" lost its {role}, task ended.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            BuildingData facility = BuildingRegistry.FindAt(task.TargetHex);
            if (facility == null || facility.Owner != player
                || !facility.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode)))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: Development — the {FacilityLabel(mode)} at "
                    + $"({task.TargetHex.Q},{task.TargetHex.R}) is gone, task ended.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Arrived — hand off to a Challenge if there's still something worth producing.
            if (task.Army.Hex.Equals(task.TargetHex))
            {
                if (!ResearchProductionSystem.IsEligible(player, task.TargetHex, mode, out string why))
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: Development — arrived at ({task.TargetHex.Q},"
                        + $"{task.TargetHex.R}) but {why}, task ended.");
                    AiTaskRegistry.Remove(player, task);
                    return null;
                }
                DevelopPick? pick = ScoreBestCard(player, root, ctx, hero, mode, task.TargetHex);
                if (!pick.HasValue)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: Development — arrived at ({task.TargetHex.Q},"
                        + $"{task.TargetHex.R}) but nothing worth {mode} right now, task ended.");
                    AiTaskRegistry.Remove(player, task);
                    return null;
                }
                return AiDecision.RunResearchProduction(hero, task.TargetHex, mode, pick.Value.Card, task,
                    pick.Value.Score, pick.Value.Reason);
            }

            // Still travelling.
            if (!AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, task.TargetHex))
                return null;
            return AiDecision.Move(task.Army, task.TargetHex,
                $"moves {hero.Name} toward the {FacilityLabel(mode)} at ({task.TargetHex.Q},{task.TargetHex.R}) to {mode}",
                task, AiConfig.developmentPositioningMoveScore, AiTaskCategory.Development);
        }

        // ---- card scoring (spec §8) -------------------------------------------------------

        // The single best (card, score) for this (hero, mode) at `facilityHex` right now, or null
        // if nothing is affordable / likely enough / worth it. Never returns a combination already
        // attempted this turn (spec §11).
        private static DevelopPick? ScoreBestCard(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            UnitData hero, ResearchProductionMode mode, HexCoord facilityHex)
        {
            List<CardDefinition> offered = ResearchProductionSystem.OfferedCards(
                ctx.ResearchProductionCatalog, mode, player.Faction);
            if (offered.Count == 0)
                return null;

            bool hidden = mode == ResearchProductionMode.Research && hero.IsHidden;
            float stealthPenalty = 0f;
            if (hidden)
            {
                bool danger = AiMapMemory.HasKnownEnemyWithin(player, facilityHex, AiConfig.developmentStealthDangerRadius);
                stealthPenalty = AiConfig.developmentStealthLossPenaltyBase
                    * (danger ? 1f : AiConfig.developmentStealthCalmFactor);
            }

            CardDefinition bestCard = null;
            float bestScore = float.NegativeInfinity;
            string bestReason = null;

            foreach (CardDefinition card in offered)
            {
                if (card == null || ctx.HasTriedDevelopment(hero, mode, card))
                    continue;
                if (!FitsResourceSurplus(root, player, card))
                    continue;

                float chance = ResearchProductionSystem.EstimateSuccessChance(hero, card);
                if (chance < AiConfig.developmentMinSuccessChance)
                    continue;

                bool isUnitOrHero = card.cardType == CardType.Unit || card.cardType == CardType.Hero;
                float statSum = isUnitOrHero
                    ? card.attack + card.hitPoints + card.defenseRating + card.commandRating
                    : 0f;
                float rawValue = AiConfig.developmentCardBaseValue
                    + (isUnitOrHero ? statSum * AiConfig.developmentCardStatWeight : AiConfig.developmentNonUnitCardValue);

                ResourceCost cost = card.resourceCost ?? new ResourceCost();
                int resourceTotal = cost.human + cost.energy + cost.materials + cost.tech;
                float utility = rawValue - resourceTotal * AiConfig.developmentResourceCostWeight - stealthPenalty;
                float score = AiConfig.developmentBaseWeight + utility * chance;
                // Never let a routine investment climb into the tactical/emergency band.
                score = Mathf.Min(score, AiConfig.strategyExemptScore - 1f);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCard = card;
                    bestReason = $"{mode} {card.displayName} — util {utility.ToString("0", CultureInfo.InvariantCulture)}, "
                        + $"~{(chance * 100f).ToString("0", CultureInfo.InvariantCulture)}% success"
                        + (stealthPenalty > 0f ? ", −stealth" : string.Empty);
                }
            }

            return bestCard != null ? new DevelopPick(bestCard, bestScore, bestReason) : (DevelopPick?)null;
        }

        // spec §9 — every resource type must keep at least developmentMinResourceKeep free after
        // paying, measured against the reservation-aware free surplus (not the raw stockpile).
        private static bool FitsResourceSurplus(PlayerRoot root, PlayerSetupData player, CardDefinition card)
        {
            ResourceCost cost = card.resourceCost;
            if (cost == null)
                return true;
            foreach (ResourceType type in System.Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>())
            {
                int need = cost.Get(type);
                if (need <= 0)
                    continue;
                if (AiResourceReservation.Available(root, player, type) - need < AiConfig.developmentMinResourceKeep)
                    return false;
            }
            return true;
        }

        private static string FacilityLabel(ResearchProductionMode mode) =>
            mode == ResearchProductionMode.Research ? "Lab" : "Factory";

        // ---- execution -----------------------------------------------------------------------

        public static IEnumerator RunResearchProductionRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            UnitData hero = decision.DevelopHero;
            CardDefinition card = decision.DevelopCard;
            ResearchProductionMode mode = decision.DevelopMode;
            HexCoord hex = decision.TargetHex;

            yield return AiTurnController.PanTo(ctx, hex);

            // The positioning task's job ends the moment we reach here — one attempt, win/lose/skip,
            // then a fresh evaluation next turn decides whether to develop again (spec: Develop is
            // removed as soon as one Challenge has been attempted or the hero/facility stops
            // qualifying).
            if (decision.Task != null)
                AiTaskRegistry.Remove(player, decision.Task);

            if (root == null || hero == null || card == null)
                yield break;

            // Re-validate against the live world (same "trust nothing stale" rule the human
            // transaction follows) before spending anything.
            if (!ResearchProductionSystem.IsEligible(player, hex, mode, out string why)
                || !ResearchProductionSystem.ActorStillQualifies(player, hero, hex, mode))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: Development — {mode} at ({hex.Q},{hex.R}) no longer valid "
                    + $"({why ?? "hero moved"}), skipped.");
                yield return AiTurnController.WaitStep(ctx);
                yield break;
            }

            AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
            if (hand == null || !hand.HasFreeSlot) // spec §10
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: Development — hand full, {mode} of {card.displayName} skipped.");
                yield return AiTurnController.WaitStep(ctx);
                yield break;
            }

            if (!ResearchProductionSystem.CanAffordCard(root, card)) // spec §9 — final check
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: Development — not enough resources for {card.displayName}, skipped.");
                yield return AiTurnController.WaitStep(ctx);
                yield break;
            }

            int ap0 = root.ActionPoints;
            int human0 = root.GetResource(ResourceType.Human);
            int energy0 = root.GetResource(ResourceType.Energy);
            int materials0 = root.GetResource(ResourceType.Materials);
            int tech0 = root.GetResource(ResourceType.Tech);

            // Research reveals the Researcher — a consequence of choosing to Research, applied
            // whether or not the roll wins (spec §12). Production never reveals.
            ResearchProductionSystem.ApplyResearchReveal(mode, hero);

            // Only ResourceCost — the attempt costs the player no AP (spec §6). Never refunded.
            ResearchProductionSystem.PayCardCost(root, card);

            // spec §11 — this combination is spent for the rest of the turn regardless of outcome.
            ctx.RecordDevelopmentAttempt(hero, mode, card);

            ResearchProductionSystem.ChallengeOutcome outcome = ResearchProductionSystem.RollChallenge(hero, card);
            string delta = AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0);

            if (outcome.Success)
            {
                hand.Hand.Add(ResearchProductionSystem.MintCard(card));
                AiDebugLog.Write($"[AI] {player.Nickname}: {mode} SUCCESS — {card.displayName} added to hand "
                    + $"({outcome.Successes}/{outcome.Required} successes, Fate spent {outcome.FateSpent}).{delta}");
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {mode} FAILED — {card.displayName} not produced "
                    + $"({outcome.Successes}/{outcome.Required} successes), resources spent.{delta}");
            }

            yield return AiTurnController.WaitStep(ctx);
        }
    }
}

