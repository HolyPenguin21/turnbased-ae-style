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
        }

        // Base founding is scanned within this radius of each owned base/citadel — an AI surplus
        // Base card expands adjacent to held territory, never across the map.
        private const int BaseFoundScanRadius = 3;

        // Rough surplus values — these only order the non-combat lane against itself and against a
        // combat surplus body; the canonical preflight is the real authority on whether a play
        // happens at all.
        private static float BaseScore(PlayKind k) => k switch
        {
            PlayKind.Base => 55f,
            PlayKind.Facility => 45f,
            PlayKind.Aviation => 40f,   // an aircraft in an airfield is what makes AirRecon possible
            PlayKind.Equipment => 24f,
            _ => 0f,
        };

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
            AiHandData hand, AiTurnContext ctx, out List<string> blocked, PlayKind? onlyKind = null)
        {
            blocked = new List<string>();
            if (player == null || root == null || hand?.Hand == null || ctx == null)
                return null;

            NonCombatPlay best = null;
            void Consider(NonCombatPlay p)
            {
                if (p != null && (onlyKind == null || p.Kind == onlyKind.Value)
                    && (best == null || p.Score > best.Score))
                    best = p;
            }

            var ownBaseHexes = OwnedBaseHexes(snap, player);

            foreach (CardData card in hand.Hand.ToList())
            {
                CardDefinition def = card?.Definition;
                if (def == null)
                    continue;

                // A non-aviation Unit / Hero / solo-Recce card is the materialization chain's job.
                bool isRecce = AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities);
                if (!def.isAviation
                    && (def.cardType == CardType.Unit || def.cardType == CardType.Hero || isRecce))
                    continue;

                if (def.isAviation)
                {
                    HexCoord? hx = AiManagementPlanner.FindAviationPlacement(player, root, card);
                    if (hx == null)
                    {
                        blocked.Add($"{def.displayName}:aviation(noAirfieldSlotOrUnaffordable)");
                        continue;
                    }
                    Consider(new NonCombatPlay
                    {
                        Card = card, Kind = PlayKind.Aviation, TargetHex = hx.Value,
                        Score = BaseScore(PlayKind.Aviation),
                        Explain = $"{def.displayName} -> airfield ({hx.Value.Q},{hx.Value.R})",
                    });
                    continue;
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
                        continue;
                    }
                    Consider(new NonCombatPlay
                    {
                        Card = card, Kind = PlayKind.Facility, TargetHex = at.Value,
                        Score = BaseScore(PlayKind.Facility),
                        Explain = $"{def.displayName} -> Base ({at.Value.Q},{at.Value.R})",
                    });
                    continue;
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
                        continue;
                    }
                    Consider(new NonCombatPlay
                    {
                        Card = card, Kind = PlayKind.Base, TargetHex = at.Value,
                        Score = BaseScore(PlayKind.Base),
                        Explain = $"{def.displayName} -> found Base ({at.Value.Q},{at.Value.R})",
                    });
                    continue;
                }

                if (def.cardType == CardType.Equipment && def.equipment != null)
                {
                    (UnitData unit, HexCoord hex)? host = BestEquipmentHost(player, root, card);
                    if (host == null)
                    {
                        blocked.Add($"{def.displayName}:equipment(noLegalDeployedHost)");
                        continue;
                    }
                    Consider(new NonCombatPlay
                    {
                        Card = card, Kind = PlayKind.Equipment, EquipHost = host.Value.unit,
                        TargetHex = host.Value.hex,
                        Score = BaseScore(PlayKind.Equipment),
                        Explain = $"{def.displayName} -> {host.Value.unit.Name}",
                    });
                    continue;
                }

                blocked.Add($"{def.displayName}:{def.cardType}(noNonCombatPlayPath)");
            }

            return best;
        }

        public static bool Execute(NonCombatPlay play, WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, out float apSpent, out string failReason)
        {
            apSpent = 0f;
            failReason = null;
            if (play?.Card?.Definition == null || player == null || root == null || hand?.Hand == null || ctx == null)
            {
                failReason = "missing args";
                return false;
            }
            if (!hand.Hand.Contains(play.Card))
            {
                failReason = "card no longer in hand";
                return false;
            }

            int apBefore = root.ActionPoints;
            bool ok;
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

            apSpent = System.Math.Max(0, apBefore - root.ActionPoints);
            return ok;
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

        private static (UnitData unit, HexCoord hex)? BestEquipmentHost(PlayerSetupData player,
            PlayerRoot root, CardData equipCard)
        {
            (UnitData unit, HexCoord hex)? best = null;
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
                    // Deterministic: first legal host by unit name.
                    if (best == null
                        || string.CompareOrdinal(u.Name ?? "", best.Value.unit.Name ?? "") < 0)
                        best = (u, army.Hex);
                }
            }
            return best;
        }
    }
}
