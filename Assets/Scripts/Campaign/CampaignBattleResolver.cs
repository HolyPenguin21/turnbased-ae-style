using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Players;

namespace Game.Campaign
{
    // Ephemeral read-only inputs. Definitions remain authored data, never serialized into campaign state.
    public sealed class CampaignDeck
    {
        public readonly string Name;
        public readonly IReadOnlyList<CardDefinition> Main, Attachments;
        public CampaignDeck(string name, IEnumerable<CardDefinition> main, IEnumerable<CardDefinition> attachments = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Campaign deck needs a name.");
            Name = name;
            Main = Canonical(main);
            Attachments = Canonical(attachments ?? Enumerable.Empty<CardDefinition>());
        }
        private static IReadOnlyList<CardDefinition> Canonical(IEnumerable<CardDefinition> cards)
        {
            var list = cards?.ToList() ?? throw new ArgumentNullException(nameof(cards));
            if (list.Any(c => c == null || string.IsNullOrWhiteSpace(c.authoredKey))) throw new ArgumentException("Unresolved campaign card.");
            return list.OrderBy(c => c.authoredKey, StringComparer.Ordinal).ToArray();
        }
        public static CampaignDeck Standard(Faction faction, DeckRules rules, string activeDeckReference = "standard")
        {
            if (activeDeckReference != "standard") throw new InvalidOperationException("Unsupported active faction deck: " + activeDeckReference);
            var source = rules.Starting.GetDeck(faction) ?? throw new InvalidOperationException("Missing standard faction deck.");
            if (!rules.Starting.TryBuildDeckPool(source.cards, out var pool, out string error)) throw new InvalidOperationException(error);
            // AI's existing production access is catalog-based and not bounded by human collection.
            var blueprints = rules.Research.ResolveFor(ResearchProductionMode.Production, faction)
                .Concat(rules.Research.ResolveFor(ResearchProductionMode.Research, faction)).Where(c => c.cardType == CardType.Equipment).Distinct();
            return new CampaignDeck(source.deckName, pool, blueprints);
        }
        public static CampaignDeck Saved(Game.Progression.SavedDeck deck, Game.Progression.CollectionService collection)
        {
            var loadout = new MatchLoadout(deck, collection.Rules, collection.Owned);
            if (!loadout.TryBuildPool(collection.Rules, out var pool, out string error)) throw new InvalidOperationException(error);
            var attachments = deck.equipment.Concat(deck.mutators).SelectMany(e => Enumerable.Repeat(collection.Rules.Resolve(e.cardKey), e.count));
            return new CampaignDeck(deck.name, pool, attachments);
        }
    }
    public sealed class CampaignDeckScore
    {
        public double CombatPotential, CommanderPotential, StrategicPotential, AttachmentPotential;
        public double Total => CombatPotential + CommanderPotential + StrategicPotential + AttachmentPotential;
        public double AirPower, AntiAirCoverage;
    }
    public sealed class CampaignBattleEstimate
    {
        public CampaignDeckScore Attacker, Defender;
        public double WinChance, Roll;
        public CampaignOutcome Outcome;
    }
    // Heuristic v1: scores are ranking signals, not measured full-match win probabilities.
    // Weights are explicit and centralized so calibration does not touch campaign/UI rules.
    public sealed class CampaignBattleResolver
    {
        public const double SecondArmyWeight = .35, AviationWeight = .45, CommanderWeight = 1.5,
            StrategicWeight = 4, AttachmentReadiness = .5, ProbabilitySlope = 4, MatchupWeight = .08;
        private sealed class Host
        {
            public CardDefinition Card, Equipment, Mutator;
            public AiPower.ProjectedStrategicLine Line => AiPower.EffectiveLine(Card, Equipment?.equipment, Mutator?.equipment);
        }
        private static double StrategicValue(AiPower.ProjectedStrategicLine line)
        {
            double value = Math.Min(4, line.MoveMax) * .15;
            if (line.ActivationApCost == 0) value += .25;
            foreach (var ability in line.EffectiveAbilities)
                if (AbilityParams.TryGetRecce(ability, out int radius, out _)) value += Math.Min(3, radius) * .3;
            return value;
        }
        private static double HeroValue(AiPower.ProjectedStrategicLine line) => Math.Max(0, line.CommandRating - 2) + line.Fate * .35 + line.Initiative * .15;
        private static AiPower.PowerUnit Power(Host h)
        { var l = h.Line; return new AiPower.PowerUnit(l.BasePower, h.Card.unitTypeTags, l.Range, h.Card.cardType == CardType.Hero, l.CommandRating); }
        private static double Formations(List<Host> hosts)
        {
            var bodies = hosts.Where(h => h.Card.cardType == CardType.Unit && !h.Card.isAviation).ToList();
            var capacities = hosts.Where(h => h.Card.cardType == CardType.Hero).Select(h => Math.Max(2, h.Line.CommandRating - 1))
                .OrderByDescending(c => c).ToList();
            int first = capacities.Count > 0 ? capacities[0] : 2;
            int second = capacities.Count > 1 ? capacities[1] : 2;
            var powers = bodies.Select(h => (double)h.Line.BasePower).OrderByDescending(v => v).ToList();
            // Role quality describes the available formation options, not simultaneous deployment
            // of every card. Capacity-limited top bodies are a deliberately coarse upper estimate.
            // Fixed role facts + nonnegative weighted order statistics are monotone in body power.
            double quality = bodies.Count == 0 ? 0 : AiConfigV2.compoFloor + (1 - AiConfigV2.compoFloor)
                * AiPower.CompositionQuality(bodies.Select(Power).ToList());
            double ground = powers.Take(first).Sum() + powers.Skip(first).Take(second).Sum() * SecondArmyWeight;
            double air = hosts.Where(h => h.Card.isAviation).Select(h => (double)h.Line.BasePower).OrderByDescending(p => p).Take(2).Sum();
            return ground * quality + air * AviationWeight;
        }
        private static double CommanderValue(List<Host> hosts) => hosts.Where(h => h.Card.cardType == CardType.Hero)
            .Select(h => HeroValue(h.Line)).OrderByDescending(v => v).Take(3).Sum() * CommanderWeight;
        private static double MobilityValue(List<Host> hosts) => hosts.Select(h => StrategicValue(h.Line)).OrderByDescending(v => v).Take(4).Sum() * StrategicWeight;
        private readonly Dictionary<string, CampaignDeckScore> scores = new Dictionary<string, CampaignDeckScore>(StringComparer.Ordinal);
        private static CampaignDeckScore Copy(CampaignDeckScore s) => new CampaignDeckScore { CombatPotential = s.CombatPotential,
            CommanderPotential = s.CommanderPotential, StrategicPotential = s.StrategicPotential, AttachmentPotential = s.AttachmentPotential,
            AirPower = s.AirPower, AntiAirCoverage = s.AntiAirCoverage };
        private static string Fingerprint(CampaignDeck deck)
        {
            // Key actual immutable scoring facts, never MatchId, PlayerRoot or a world snapshot.
            // Re-reading facts also invalidates an edited authored definition during editor tests.
            var key = new System.Text.StringBuilder();
            void Append(CardDefinition c)
            {
                void Text(string v) { v = v ?? ""; key.Append(v.Length).Append(':').Append(v).Append('|'); }
                Text(c.authoredKey); Text(((int)c.cardType).ToString());
                foreach (int value in new[] { c.attack, c.defenseRating, c.resistanceRating, c.hitPoints, c.initiative, c.range,
                    c.commandRating, c.fate, c.moveMax, c.activationApCost, c.isAviation ? 1 : 0, (int)c.attachmentSlot }) key.Append(value).Append(',');
                Text(c.requiredBuildingAbility);
                foreach (var tag in c.unitTypeTags ?? new List<UnitTypeTag>()) key.Append((int)tag).Append(','); key.Append('|');
                foreach (var ability in c.grantedAbilities ?? new List<string>()) Text(ability); key.Append('|');
                if (c.equipment != null)
                {
                    foreach (var kind in c.equipment.hostKinds ?? new List<EquipmentHostKind>()) key.Append((int)kind).Append(','); key.Append('|');
                    foreach (var tag in c.equipment.hostTypeTags ?? new List<UnitTypeTag>()) key.Append((int)tag).Append(','); key.Append('|');
                    foreach (var family in c.equipment.clearAbilityFamilies ?? new List<AbilityFamily>()) key.Append((int)family).Append(','); key.Append('|');
                    foreach (var ability in c.equipment.removeAbilities ?? new List<string>()) Text(ability); key.Append('|');
                    foreach (var ability in c.equipment.addAbilities ?? new List<string>()) Text(ability); key.Append('|');
                    foreach (var stat in c.equipment.statChanges ?? new List<EquipmentStatChange>())
                        if (stat != null) key.Append((int)stat.stat).Append(',').Append(stat.amount).Append(',').Append(stat.isOverride ? 1 : 0).Append(';');
                }
                key.Append('#');
            }
            foreach (var c in deck.Main) Append(c); key.Append("/attachments/"); foreach (var c in deck.Attachments) Append(c);
            return key.ToString();
        }
        public CampaignDeckScore Evaluate(CampaignDeck deck)
        {
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            string signature = Fingerprint(deck);
            if (scores.TryGetValue(signature, out var cached)) return Copy(cached);
            var available = new HashSet<string>(deck.Main.SelectMany(c => c.grantedAbilities ?? new List<string>()));
            var hosts = deck.Main.Where(c => c.cardType == CardType.Unit || c.cardType == CardType.Hero)
                .Where(c => string.IsNullOrEmpty(c.requiredBuildingAbility) || available.Contains(c.requiredBuildingAbility) || c.requiredBuildingAbility == UnitAbilities.Barracks)
                .Select(c => new Host { Card = c }).ToList();
            int economicRoles = available.Count(a => UnitAbilities.CollectAbilities.Contains(a));
            int sites = deck.Main.Count(c => c.cardType == CardType.Base);
            bool production = available.Contains(UnitAbilities.Production) && hosts.Any(h => h.Card.cardType == CardType.Hero && h.Line.EffectiveAbilities.Contains(UnitAbilities.Assembler));
            bool research = available.Contains(UnitAbilities.Research) && hosts.Any(h => h.Card.cardType == CardType.Hero && h.Line.EffectiveAbilities.Contains(UnitAbilities.Researcher));
            double setupValue = StrategicWeight * (economicRoles * .5 + Math.Min(2, sites) * .5 + (production ? .5 : 0) + (research ? .5 : 0));
            var score = new CampaignDeckScore { CombatPotential = Formations(hosts), CommanderPotential = CommanderValue(hosts), StrategicPotential = setupValue + MobilityValue(hosts) };
            double best = score.Total;
            // Small, fixed set of feasible slot assignments, independent of host stat ranking.
            // Each policy uses each blueprint copy once and each host's independent slot once.
            // Taking their maximum retains every previous candidate when a stat is improved.
            for (int policy = 0; policy < 8; policy++) for (int mode = 0; mode < 3; mode++)
            {
                var projected = hosts.Select(h => new Host { Card = h.Card }).ToList();
                var order = Enumerable.Range(0, projected.Count).ToList();
                if (policy == 1) order.Reverse();
                else if (policy > 1)
                {
                    var random = new CampaignRandom(CampaignRandom.Derive(1193, policy));
                    for (int i = order.Count - 1; i > 0; i--) { int j = random.Range(i + 1); int old = order[i]; order[i] = order[j]; order[j] = old; }
                }
                foreach (var attachment in deck.Attachments)
                {
                    bool mutator = attachment.attachmentSlot == AttachmentSlot.Mutator;
                    if ((mutator ? !research : !production) || (mode == 0 && mutator) || (mode == 1 && !mutator)) continue;
                    foreach (int index in order)
                    {
                        var host = projected[index];
                        if ((mutator ? host.Mutator : host.Equipment) != null || !EquipmentSystem.FitsHost(attachment, host.Card, out _)) continue;
                        if (mutator) host.Mutator = attachment; else host.Equipment = attachment;
                        break;
                    }
                }
                best = Math.Max(best, Formations(projected) + CommanderValue(projected) + (setupValue + MobilityValue(projected)));
            }
            // Only the gain of a complete feasible option is counted; no summation of item bonuses.
            score.AttachmentPotential = (best - score.Total) * AttachmentReadiness;
            score.AirPower = hosts.Where(h => h.Card.isAviation).Select(h => (double)h.Line.BasePower).OrderByDescending(v => v).Take(2).Sum();
            score.AntiAirCoverage = Math.Min(1, hosts.Count(h => h.Line.EffectiveAbilities.Contains(UnitAbilities.AntiAir)) / 2.0);
            if (scores.Count >= 128) scores.Clear(); scores[signature] = Copy(score);
            return score;
        }
        public CampaignBattleEstimate Compare(CampaignDeck attacker, CampaignDeck defender)
        {
            var a = Evaluate(attacker); var d = Evaluate(defender);
            double difference = (a.Total - d.Total) / Math.Max(1, (a.Total + d.Total) / 2);
            // Only the existing AA tag is used. No counter-effect registry or combat RNG.
            double airMatch = (a.AirPower * (1 - d.AntiAirCoverage) - d.AirPower * (1 - a.AntiAirCoverage)) / Math.Max(1, a.Total + d.Total);
            double probability = 1 / (1 + Math.Exp(-ProbabilitySlope * (difference + MatchupWeight * airMatch)));
            return new CampaignBattleEstimate { Attacker = a, Defender = d, WinChance = Math.Max(.1, Math.Min(.9, probability)) };
        }
        public CampaignBattleEstimate Resolve(CampaignDeck attacker, CampaignDeck defender, int seed)
        {
            var result = Compare(attacker, defender); result.Roll = new CampaignRandom(seed).Value();
            result.Outcome = result.Roll < result.WinChance ? CampaignOutcome.AttackerVictory : CampaignOutcome.DefenderVictory;
            return result;
        }
    }
}
