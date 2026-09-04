using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  NON-COMBAT SURPLUS CARD PLAY  (Strategy V2 — Strategic Manager Phase B, spec §5/§13)
    // ===========================================================================================
    //  MaterializationCandidateBuilder.BestSurplus only ever bodies a Unit / Hero / solo-Recce
    //  card (and its Equipment attachment). Aviation, Base, Facility and standalone Equipment
    //  cards never produced a surplus candidate, so — even with Phase B running in every mode —
    //  they sat in hand forever whenever no matching non-Recon demand reached Phase A. That made
    //  "card type" the de-facto reason a legal card went unplayed, which §5/§13 forbids.
    //
    //  This is the missing lane. It enumerates every hand card the materialization chain cannot
    //  body, checks it against the SAME canonical gameplay APIs the human UI / V1 AI use
    //  (BuildingPlayExecutor -> InfrastructureActions, AviationActions.TryDeployFromCard,
    //  EquipmentSystem), and hands StrategicManager.UseSurplus a fully-preflighted best play.
    //  Every rejection carries a real gameplay reason (no AP, no resources, no legal destination,
    //  no capacity, no host) — never "wrong card type" and never "ReconOnly".
    // ===========================================================================================
    internal static class NonCombatCardPlayer
    {
        internal enum PlayKind { Base, Facility, Aviation, Equipment }

        internal sealed class NonCombatPlay
        {
            public CardData Card;
            public PlayKind Kind;
            public HexCoord TargetHex;
            public UnitData EquipHost;   // Equipment only
            public float Score;
            public string Explain;
            // AI-MGR-01 review-r4 finding 9b — set when this play must first MINT its card through a
            // Research/Production Challenge (Card is then a throwaway pre-mint stand-in; Execute
            // re-resolves the placement against the real minted instance). null => Card is a real
            // hand card.
            public GenerationStep Generation;
        }

        // Base founding is scanned within this radius of each owned base/citadel — an AI surplus
        // Base card expands adjacent to held territory, never across the map.
        private const int BaseFoundScanRadius = 3;

        // AI-MGR-01 P0.1 — every non-combat card is scored through the shared StrategicCardEvaluator
        // (same breakdown / NetScore band as a Unit/Hero chain), so Phase B can compare the two
        // lanes directly instead of the old incomparable 55/45/40/24 fixed scale.
        private static NonCombatRole RoleOf(PlayKind k) => k switch
        {
            PlayKind.Base => NonCombatRole.Base,
            PlayKind.Facility => NonCombatRole.Facility,
            PlayKind.Aviation => NonCombatRole.Aviation,
            _ => NonCombatRole.Equipment,
        };

        private static float Score(WorldSnapshot snap, PlayerSetupData player, PlayKind k, CardData card,
            AiHandData hand, float bestEquipmentUpgrade, GenerationStep generation = null)
        {
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            return StrategicCardEvaluator.ScoreNonCombat(
                RoleOf(k), card, snap, inv, hand, bestEquipmentUpgrade, generation).NetScore;
        }

        // Pure card-type router: which Phase-B lane owns this card. null => the Unit/Hero/Recce
        // materialization chain (MaterializationCandidateBuilder) owns it. Exhaustive over
        // CardType — no card falls through to "no lane", so card type is never on its own a reason
        // a legal card is left unplayed.
        internal static PlayKind? LaneFor(CardDefinition def)
        {
            if (def == null)
                return null;
            if (def.isAviation)
                return PlayKind.Aviation;
            bool isRecce = AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities);
            if (def.cardType == CardType.Unit || def.cardType == CardType.Hero || isRecce)
                return null; // combat body -> materialization chain
            switch (def.cardType)
            {
                case CardType.Base: return PlayKind.Base;
                case CardType.Facility: return PlayKind.Facility;
                case CardType.Equipment: return PlayKind.Equipment;
                default: return null;
            }
        }

        public static NonCombatPlay BestPlay(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, out List<string> blocked, PlayKind? onlyKind = null,
            MaterializationReservation reservation = null)
        {
            blocked = new List<string>();
            NonCombatPlay best = null;
            foreach (NonCombatPlay p in EnumeratePlays(snap, player, root, hand, ctx, blocked, reservation))
                if ((onlyKind == null || p.Kind == onlyKind.Value) && (best == null || p.Score > best.Score))
                    best = p;
            return best;
        }

        // AI-MGR-02 round 6 — every LEGAL non-combat play for the current hand (each already
        // resolved to a real placement / host / airfield slot / base slot by BuildPlayFor).
        // BestPlay picks the highest-Score one; the reaction feasibility probe needs the whole set
        // so it can find the genuinely CHEAPEST feasible reaction, not the best-scored card.
        internal static IEnumerable<NonCombatPlay> EnumeratePlays(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, List<string> blocked,
            MaterializationReservation reservation = null)
        {
            if (player == null || root == null || hand?.Hand == null || ctx == null)
                yield break;
            if (blocked == null) blocked = new List<string>();

            var ownBaseHexes = OwnedBaseHexes(snap, player);

            foreach (CardData card in hand.Hand.ToList())
            {
                CardDefinition def = card?.Definition;
                if (def == null)
                    continue;
                // A non-aviation Unit / Hero / solo-Recce card is the materialization chain's job.
                if (!def.isAviation
                    && (def.cardType == CardType.Unit || def.cardType == CardType.Hero
                        || AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities)))
                    continue;
                NonCombatPlay p = BuildPlayFor(card, generation: null, snap, player, root, hand, ctx,
                    ownBaseHexes, blocked);
                if (p != null)
                    yield return p;
            }

            // AI-MGR-01 review-r4 finding 9b — generated non-combat cards. A Research/Production
            // Challenge whose minted card is an Aviation / Base / Facility is scored on the SAME
            // NetScore band (throwaway pre-mint stand-in), discounted by the Challenge success
            // chance + the generation step penalty; Execute mints then deploys via the canonical
            // API. Generated Equipment stays with the materialization GenerateAttachDeploy chain.
            if (reservation != null && reservation.CanGenerateMore && hand.HasFreeSlot)
            {
                foreach (GenerationStep g in GenerationSource.Enumerate(player, root, ctx, hand,
                    reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards))
                {
                    CardDefinition gd = g?.CardDef;
                    if (gd == null || g.ProducesEquipment)
                        continue;
                    if (!(gd.isAviation || gd.cardType == CardType.Base || gd.cardType == CardType.Facility))
                        continue;
                    var stand = new CardData(gd) { ResearchProductionCreated = true };
                    NonCombatPlay p = BuildPlayFor(stand, g, snap, player, root, hand, ctx,
                        ownBaseHexes, blocked);
                    if (p == null)
                        continue;
                    p.Explain = $"generate:{gd.displayName} -> " + p.Explain;
                    yield return p;
                }
            }
        }

        // AI-MGR-01 review-r4 finding 9b — one non-combat play for one card (real hand card, or a
        // pre-mint stand-in when `generation` is set). Extracted from BestPlay's per-card loop so
        // the generated path reuses the exact same placement resolution + scoring.
        private static NonCombatPlay BuildPlayFor(CardData card, GenerationStep generation,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, List<HexCoord> ownBaseHexes, List<string> blocked)
        {
            CardDefinition def = card?.Definition;
            if (def == null)
                return null;

            if (def.isAviation)
            {
                // §1 final closure — V2-owned feasibility query, no V1 AiManagementPlanner.
                if (!PlacementRules.TryFindAviationPlacement(snap, player, root, card,
                        out HexCoord hx, out string why))
                {
                    blocked.Add($"{def.displayName}:aviation({why ?? "noAirfieldSlot"})");
                    return null;
                }
                return new NonCombatPlay
                {
                    Card = card, Kind = PlayKind.Aviation, TargetHex = hx, Generation = generation,
                    Score = Score(snap, player, PlayKind.Aviation, card, hand, 0f, generation),
                    Explain = $"{def.displayName} -> airfield ({hx.Q},{hx.R})",
                };
            }

            if (def.cardType == CardType.Facility)
            {
                HexCoord? at = null;
                string why = "noOwnedBase";
                foreach (HexCoord h in ownBaseHexes)
                {
                    if (BuildingPlayExecutor.CanPlaceFacilityAt(player, hand, ctx, card, h, out string r))
                    { at = h; break; }
                    if (r != null) why = r;
                }
                if (at == null)
                {
                    blocked.Add($"{def.displayName}:facility({why})");
                    return null;
                }
                return new NonCombatPlay
                {
                    Card = card, Kind = PlayKind.Facility, TargetHex = at.Value, Generation = generation,
                    Score = Score(snap, player, PlayKind.Facility, card, hand, 0f, generation),
                    Explain = $"{def.displayName} -> Base ({at.Value.Q},{at.Value.R})",
                };
            }

            if (def.cardType == CardType.Base)
            {
                HexCoord? at = null;
                string why = "noLegalFoundHex";
                foreach (HexCoord h in BaseFoundCandidates(snap, player, ownBaseHexes))
                {
                    if (BuildingPlayExecutor.CanFoundBaseAt(player, hand, ctx, card, h, out string r))
                    { at = h; break; }
                    if (r != null) why = r;
                }
                if (at == null)
                {
                    blocked.Add($"{def.displayName}:base({why})");
                    return null;
                }
                return new NonCombatPlay
                {
                    Card = card, Kind = PlayKind.Base, TargetHex = at.Value, Generation = generation,
                    Score = Score(snap, player, PlayKind.Base, card, hand, 0f, generation),
                    Explain = $"{def.displayName} -> found Base ({at.Value.Q},{at.Value.R})",
                };
            }

            if (def.cardType == CardType.Equipment && def.equipment != null)
            {
                (UnitData unit, HexCoord hex, float upgrade)? host = BestEquipmentHost(player, root, card);
                if (host == null)
                {
                    blocked.Add($"{def.displayName}:equipment(noLegalDeployedHost)");
                    return null;
                }
                // review-r2 — host is chosen by the REAL predicted before/after stat delta on that
                // carrier (StrategicCardEvaluator.EquipmentUpgradeUtilityFor), not by raw host
                // power, and that same delta is the RoleFit.
                return new NonCombatPlay
                {
                    Card = card, Kind = PlayKind.Equipment, EquipHost = host.Value.unit,
                    TargetHex = host.Value.hex, Generation = generation,
                    Score = Score(snap, player, PlayKind.Equipment, card, hand, host.Value.upgrade, generation),
                    Explain = $"{def.displayName} -> {host.Value.unit.Name} (Δ{host.Value.upgrade:0.00})",
                };
            }

            blocked.Add($"{def.displayName}:{def.cardType}(noNonCombatPlayPath)");
            return null;
        }

        // AI-MGR-01 review-r4 P1 — a structured result. A generated non-combat play is NOT atomic
        // (mint then deploy), so a partial failure — Challenge lost after resources were spent /
        // the Researcher was revealed, OR a mint that then can't be deployed — really changes state
        // and consumes the turn's generation attempt. The caller must see that, not just `false`.
        internal struct NonCombatExecuteResult
        {
            public bool Played;               // final deploy/attach/build succeeded
            public bool StateChanged;         // ANY real world mutation happened (mint, reveal, resource spend, deploy)
            public bool GenerationAttempted;  // a Challenge was rolled (attempt is spent either way)
            public bool Generated;            // the Challenge won and a card is now in hand
            public float ApSpent;
            public string FailReason;
        }

        public static NonCombatExecuteResult Execute(NonCombatPlay play, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var res = new NonCombatExecuteResult();
            if (play?.Card?.Definition == null || player == null || root == null || hand?.Hand == null || ctx == null)
            {
                res.FailReason = "missing args";
                return res;
            }

            int apBefore = root.ActionPoints;

            // finding 9b — a generated non-combat play mints its card first (ResourceCost only, no
            // AP; probabilistic), then deploys the REAL minted instance. finding P1 — every real
            // mutation of this non-atomic chain is reported even when a later step fails.
            if (play.Generation != null)
            {
                res.GenerationAttempted = true;
                MaterializationExecutor.GenerationOutcome go =
                    MaterializationExecutor.TryGenerate(play.Generation, player, root, hand);
                if (go.StateChanged) res.StateChanged = true;
                if (!go.Success)
                {
                    res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                    res.FailReason = go.FailReason ?? "generation failed";
                    return res;
                }
                res.Generated = true;
                res.StateChanged = true;   // a card was minted into the hand

                // Re-resolve the placement against the real card + live world (the pre-mint
                // stand-in's target may be stale). The minted card stays in hand if this fails —
                // a real asset, exactly like CardPlayExecutor keeping a created empty army.
                NonCombatPlay fresh = BuildPlayFor(go.Minted, generation: null, snap, player, root,
                    hand, ctx, OwnedBaseHexes(snap, player), new List<string>());
                if (fresh == null || fresh.Kind != play.Kind)
                {
                    res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                    res.FailReason = "no legal placement for the generated non-combat card";
                    return res;
                }
                play = fresh;
            }

            if (!hand.Hand.Contains(play.Card))
            {
                res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                res.FailReason = "card no longer in hand";
                return res;
            }

            bool ok;
            string failReason = null;
            switch (play.Kind)
            {
                case PlayKind.Aviation:
                {
                    ok = AviationActions.TryDeployFromCard(play.Card.Definition, player, root,
                        ctx.HexSelection, play.TargetHex, out failReason, null, play.Card);
                    if (ok)
                        hand.Hand.Remove(play.Card);
                    break;
                }
                case PlayKind.Base:
                {
                    BuildingPlayResult r = BuildingPlayExecutor.PlayBaseCard(player, root, hand, ctx, play.Card, play.TargetHex);
                    ok = r.Built;
                    failReason = r.FailReason;
                    break;
                }
                case PlayKind.Facility:
                {
                    BuildingPlayResult r = BuildingPlayExecutor.PlayFacilityCard(player, root, hand, ctx, play.Card, play.TargetHex);
                    ok = r.Built;
                    failReason = r.FailReason;
                    break;
                }
                case PlayKind.Equipment:
                {
                    if (play.EquipHost == null)
                    {
                        failReason = "equipment host gone";
                        ok = false;
                        break;
                    }
                    ok = EquipmentSystem.TryAttach(play.Card, play.EquipHost, root, out failReason);
                    if (ok)
                        hand.Hand.Remove(play.Card);
                    break;
                }
                default:
                    failReason = "unknown non-combat play kind";
                    ok = false;
                    break;
            }

            res.Played = ok;
            if (ok) res.StateChanged = true;
            res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
            if (!ok) res.FailReason = failReason ?? "non-combat play failed";
            return res;
        }

        // ------------------------------------------------------------------ helpers ----

        private static List<HexCoord> OwnedBaseHexes(WorldSnapshot snap, PlayerSetupData player)
        {
            var set = new HashSet<HexCoord>();
            foreach (BuildingData b in BuildingRegistry.AllBuildings())
                if (b != null && b.Owner == player && b.IsBase)
                    set.Add(b.Hex);
            if (snap?.Self?.BaseHexes != null)
                foreach (HexCoord h in snap.Self.BaseHexes)
                    set.Add(h);
            if (snap?.Self != null)
                set.Add(snap.Self.Citadel);
            return set.OrderBy(h => h.Q).ThenBy(h => h.R).ToList();
        }

        private static IEnumerable<HexCoord> BaseFoundCandidates(WorldSnapshot snap, PlayerSetupData player,
            List<HexCoord> ownBaseHexes)
        {
            var seen = new HashSet<HexCoord>();
            foreach (HexCoord anchor in ownBaseHexes)
                foreach (HexCoord h in HexGridMath.HexesInRange(anchor, BaseFoundScanRadius)
                    .OrderBy(x => HexGridMath.Distance(anchor, x)).ThenBy(x => x.Q).ThenBy(x => x.R))
                    if (seen.Add(h) && !ownBaseHexes.Contains(h))
                        yield return h;
        }

        // review-r2 — the legal (host) that maximises the REAL predicted equipment delta on that
        // carrier (StrategicCardEvaluator.EquipmentUpgradeUtilityFor via EquipmentSystem.Predict),
        // name only as the final deterministic tie-break.
        private static (UnitData unit, HexCoord hex, float upgrade)? BestEquipmentHost(
            PlayerSetupData player, PlayerRoot root, CardData equipCard)
        {
            (UnitData unit, HexCoord hex, float upgrade)? best = null;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army?.Members == null)
                    continue;
                foreach (UnitData u in army.Members)
                {
                    if (u == null || u.IsAviation || u.Equipment != null)
                        continue;
                    if (!EquipmentSystem.CanAttach(equipCard, u, root, out _))
                        continue;
                    float delta = StrategicCardEvaluator.EquipmentUpgradeUtilityFor(equipCard.Definition, u);
                    if (best == null || delta > best.Value.upgrade + 0.0001f
                        || (System.Math.Abs(delta - best.Value.upgrade) <= 0.0001f
                            && string.CompareOrdinal(u.Name ?? "", best.Value.unit.Name ?? "") < 0))
                        best = (u, army.Hex, delta);
                }
            }
            return best;
        }
    }
}
