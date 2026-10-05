using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using UnityEngine;
using Game.Combat;

namespace Game.Ai.V2
{
    // Shared snapshot-tier estimator for known army and guarded-event combat opportunities.
    // The same target facts are consumed by radar, objective admission and Raid provisioning.
    public readonly struct CombatOpportunity
    {
        public readonly bool HasTarget;
        public readonly HexCoord TargetHex;
        public readonly RaidTargetRef Target;
        public int TargetArmyId => Target.Kind == RaidTargetKind.NeutralArmy ? Target.ArmyId : 0;
        public readonly PlayerSetupData TargetOwner;
        public readonly bool TargetIsNeutral;
        public readonly int DefenderCount;
        public readonly float ReadyWinChance;
        public readonly float AssemblableWinChance;
        public readonly bool CanCoverAllDefenders;
        public readonly float BattleCostProxy;
        public readonly int Eta;
        public readonly float TargetValue;
        public readonly float Confidence;
        public readonly bool GatePassed;
        public readonly float OpportunityScore;

        public CombatOpportunity(bool hasTarget, HexCoord targetHex, RaidTargetRef target, PlayerSetupData targetOwner, bool targetIsNeutral,
            int defenderCount, float readyWinChance, float assemblableWinChance, bool canCoverAll, float battleCostProxy,
            int eta, float targetValue, float confidence, bool gatePassed, float opportunityScore)
        {
            HasTarget = hasTarget;
            TargetHex = targetHex;
            Target = target;
            TargetOwner = targetOwner;
            TargetIsNeutral = targetIsNeutral;
            DefenderCount = defenderCount;
            ReadyWinChance = readyWinChance;
            AssemblableWinChance = assemblableWinChance;
            CanCoverAllDefenders = canCoverAll;
            BattleCostProxy = battleCostProxy;
            Eta = eta;
            TargetValue = targetValue;
            Confidence = confidence;
            GatePassed = gatePassed;
            OpportunityScore = opportunityScore;
        }

        // The ONE "can we take this fight now" rule: the shared gate passed, or the ready or
        // assemblable force covers every defender at the Raid floor. Objective admission and
        // ForceNeedModel both read it.
        public bool IsViable => GatePassed
            || (CanCoverAllDefenders && (ReadyWinChance >= AiConfigV2.raidMinViableWinChance
                || AssemblableWinChance >= AiConfigV2.raidMinViableWinChance));

        public static CombatOpportunity None =>
            new CombatOpportunity(false, default, RaidTargetRef.None, null, false, 0, 0f, 0f, false, 0f, 0, 0f, 0f, false, 0f);
    }

    public sealed class CombatOpportunityReport
    {
        public IReadOnlyList<CombatOpportunity> All = System.Array.Empty<CombatOpportunity>();
        public CombatOpportunity Best = CombatOpportunity.None;
        public IReadOnlyList<CombatOpportunity> NeutralOpportunities = System.Array.Empty<CombatOpportunity>();
        public CombatOpportunity BestNeutralOpportunity = CombatOpportunity.None;
        public bool HeroAvailable;
        public int AssemblableCap;
    }

    public static class CombatOpportunityAnalyzer
    {
        private const int NoHeroStackCapacity = 2;

        // ---------------------------------------------------------------------------------------
        //  T06 — "can the KNOWN pool ever field an army that clears this fight's coverage gate?"
        // ---------------------------------------------------------------------------------------
        //  Coverage (WorthIt.CanDamageAll: every defender has at least one attacker able to damage
        //  it) is a hard part of the one ground-combat gate (GroundCombatFeasibility.Clears), and
        //  it is monotone in the attacker set: a roster drawn from a pool can only cover what the
        //  WHOLE pool covers. So "the whole optimistic pool cannot cover" is a strict proof that no
        //  army built from it ever clears — unlike RequiredPower (a margin) or a Monte Carlo 0.
        //  The pool ignores claims, MP, AP, resources, capacity, investment windows and where a
        //  facility stands (those are timing, never impossibility):
        //    · every own ground unit on the map (garrisons included), as it stands now;
        //    · every Unit card in hand (with its attached equipment) and in the remaining deck;
        //    · every Unit the faction's Research/Production can mint (DevelopmentReadiness.CatalogOutputs);
        //    · each of those again under every known equipment grant (hand, deck, attached,
        //      catalog), applied on top — an over-estimate, which only makes a proof rarer;
        //    · the RaiseTheRots summon when a pool unit can raise it.
        //  Future random rewards are not assumed. No catalog, or a summoner whose summon template
        //  is unknown, means no upper bound: nothing is proven. Cached per snapshot (a new world
        //  state is a new WorldSnapshot).
        private sealed class PoolBox
        {
            public List<WorthIt.DefenderProfile> Attackers;
            public bool Bounded;
            public string Summary;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot, PoolBox>
            PoolCache = new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot, PoolBox>();

        internal static bool ProvenUncoverableWithinKnownPool(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, out string reason)
        {
            reason = null;
            if (snap?.Self == null || opposition == null || opposition.Count == 0)
                return false;
            PoolBox pool = PoolCache.GetValue(snap, BuildKnownPool);
            if (!pool.Bounded)
                return false;
            if (WorthIt.CanDamageAll(pool.Attackers, opposition, hexBonus))
                return false;
            foreach (WorthIt.DefendingArmy army in opposition)
                foreach (WorthIt.DefenderProfile d in army.Units ?? System.Array.Empty<WorthIt.DefenderProfile>())
                {
                    if (!d.IsGroundCombatant)
                        continue;
                    float extra = army.DefenseBonus(hexBonus);
                    if (pool.Attackers.Any(a => a.IsGroundCombatant && WorthIt.CanDamage(a, d, extra)))
                        continue;
                    reason = $"no_known_pool_unit_damages_defender(A{d.Attack:0.#}/D{d.Defense:0.#}"
                        + $"+{extra:0.#}{(d.HasCeramicArmor ? "/ceramic" : "")}"
                        + $"{(d.TypeTags != null && d.TypeTags.Count > 0 ? "/" + string.Join("+", d.TypeTags) : "")}) "
                        + $"pool={pool.Summary}";
                    return true;
                }
            reason = $"known_pool_misses_coverage pool={pool.Summary}";
            return true;
        }

        private static PoolBox BuildKnownPool(WorldSnapshot snap)
        {
            var attackers = new List<WorthIt.DefenderProfile>();
            var grants = new List<CardDefinition>();
            var occupiedMutators = new HashSet<int>();
            int map = 0, hand = 0, deck = 0, outputs = 0;

            void Grant(CardDefinition d)
            {
                if (d != null && d.cardType == CardType.Equipment && d.equipment != null)
                    grants.Add(d);
            }

            foreach (ArmySnapshot a in snap.Self.Armies ?? (IReadOnlyList<ArmySnapshot>)System.Array.Empty<ArmySnapshot>())
            {
                if (a == null || a.IsPrison || a.Members == null) continue;
                for (int memberIndex = 0; memberIndex < a.Members.Count; memberIndex++)
                {
                    WorthIt.DefenderProfile m = a.Members[memberIndex];
                    if (!m.IsGroundCombatant) continue;
                    if (a.NonHeroMutatorOccupied != null && memberIndex < a.NonHeroMutatorOccupied.Count
                        && a.NonHeroMutatorOccupied[memberIndex]) occupiedMutators.Add(attackers.Count);
                    attackers.Add(m); map++;
                }
            }
            // The frozen card view (SelfSnapshot.PoolCards), coherent with the frozen Armies:
            // the live Hand/Deck lists may already miss a card whose unit Armies does not show yet.
            IEnumerable<(CardDefinition Card, CardDefinition Equipment, CardDefinition Mutator, bool InHand)> cards =
                snap.Self.PoolCards
                ?? (snap.Self.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
                    .Where(c => c?.Definition != null)
                    .Select(c => (c.Definition, c.Equipment, c.Mutator, true))
                    .Concat((snap.Self.Deck ?? (IReadOnlyList<CardDefinition>)System.Array.Empty<CardDefinition>())
                        .Where(d => d != null).Select(d => (d, (CardDefinition)null, (CardDefinition)null, false)));
            foreach ((CardDefinition d, CardDefinition equipment, CardDefinition mutator, bool inHand) in cards)
            {
                Grant(d);
                if (equipment != null) Grant(equipment);
                if (mutator != null) Grant(mutator);
                if (d.cardType != CardType.Unit) continue;
                var profile = AiPower.ToDefenderProfile(d);
                if (equipment?.equipment != null) profile = Equipped(profile, equipment.equipment);
                if (mutator?.equipment != null) profile = Equipped(profile, mutator.equipment);
                if (mutator != null) occupiedMutators.Add(attackers.Count);
                attackers.Add(profile);
                if (inHand) hand++; else deck++;
            }
            DevelopmentReadiness dev = snap.Development;
            bool bounded = dev != null && dev.CatalogKnown;
            foreach (CardDefinition d in dev?.CatalogOutputs ?? (IReadOnlyList<CardDefinition>)System.Array.Empty<CardDefinition>())
            {
                if (d == null) continue;
                Grant(d);
                if (d.cardType != CardType.Unit) continue;
                attackers.Add(AiPower.ToDefenderProfile(d));
                outputs++;
            }

            int baseCount = attackers.Count;
            for (int i = 0; i < baseCount; i++)
                foreach (CardDefinition attachment in grants)
                {
                    var host = attackers[i];
                    if (attachment.attachmentSlot == AttachmentSlot.Mutator
                        && (occupiedMutators.Contains(i) || !EquipmentSystem.FitsHostCore(attachment,
                            host.IsHero ? EquipmentHostKind.Hero : EquipmentHostKind.Unit,
                            host.TypeTags != null ? new List<UnitTypeTag>(host.TypeTags) : null, out _))) continue;
                    attackers.Add(Equipped(host, attachment.equipment));
                }

            if (attackers.Any(a => a.Abilities != null && a.Abilities.Contains(UnitAbilities.RaiseTheRots)))
            {
                CardDefinition summon = UnitAbilityCatalog.Active?.ResolveRaiseTheRotsCard();
                if (summon == null)
                    bounded = false;
                else
                    attackers.Add(AiPower.ToDefenderProfile(summon));
            }

            return new PoolBox
            {
                Attackers = attackers,
                Bounded = bounded,
                Summary = $"map{map}/hand{hand}/deck{deck}/outputs{outputs}/equipment{grants.Count}"
                    + (bounded ? "" : "/unbounded"),
            };
        }

        // `p` with one equipment grant applied on top (attack and abilities are what coverage reads).
        private static WorthIt.DefenderProfile Equipped(WorthIt.DefenderProfile p, EquipmentGrant g)
        {
            var stats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = Mathf.RoundToInt(p.Attack),
                [EquipmentStat.Defense] = Mathf.RoundToInt(p.Defense),
                [EquipmentStat.HitPoints] = Mathf.RoundToInt(p.HitPoints),
                [EquipmentStat.Initiative] = p.Initiative,
            };
            PredictedEquipmentState pred = EquipmentSystem.Predict(g, stats, p.Abilities);
            int S(EquipmentStat s) => pred.Stats != null && pred.Stats.TryGetValue(s, out int v) ? v : stats[s];
            IReadOnlyList<string> abilities = pred.Abilities ?? EquipmentSystem.EffectiveAbilities(p.Abilities, g);
            return new WorthIt.DefenderProfile(S(EquipmentStat.Defense),
                abilities.Contains(UnitAbilities.CeramicArmor), p.TypeTags, S(EquipmentStat.Attack),
                S(EquipmentStat.HitPoints), S(EquipmentStat.Initiative), abilities, p.MaxHitPoints,
                p.IsGroundCombatant, p.IsHero, p.FateMax, p.IsSummoned);
        }

        public static CombatOpportunityReport Analyze(WorldSnapshot snap)
        {
            using var __profile = new Game.Core.ProfileScope("AI/CombatOpportunity.Analyze");
            var report = new CombatOpportunityReport();
            if (snap?.Self == null || snap.Known == null)
                return report;

            List<WorthIt.DefenderProfile> assemblableBodies = AssemblableBodies(snap);
            List<HeroRoleEvaluator.HeroProfile> commanders = Commanders(snap);
            report.HeroAvailable = commanders.Count > 0;
            report.AssemblableCap = commanders.Count > 0
                ? commanders.Max(h => h.CommandRating) : NoHeroStackCapacity;

            ArmySnapshot bestReadyArmy = BestReadyArmy(snap);
            List<WorthIt.DefenderProfile> readyRoster = bestReadyArmy?.Members?.ToList()
                ?? new List<WorthIt.DefenderProfile>();
            // The ready army fights under its own commander.
            WorthIt.SideCommander readyCommander = bestReadyArmy?.Commander ?? default;

            var fromHexes = new List<HexCoord>();
            int moverBudget = AiConfigV2.etaFallbackMoveBudget;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (!a.IsStructuralRaidActor) continue;
                fromHexes.Add(a.Hex);
                if (a.MaxMovement > moverBudget) moverBudget = a.MaxMovement;
            }
            if (snap.Self.BaseHexes != null) fromHexes.AddRange(snap.Self.BaseHexes);

            var candidates = new List<AiMapMemory.KnownEnemySighting>();
            if (snap.Known.EnemySightings != null) candidates.AddRange(snap.Known.EnemySightings);
            if (snap.Known.NeutralSightings != null) candidates.AddRange(snap.Known.NeutralSightings);

            var all = new List<CombatOpportunity>(candidates.Count);
            foreach (AiMapMemory.KnownEnemySighting t in candidates)
            {
                IReadOnlyList<WorthIt.DefenderProfile> defenders = t.Defenders
                    ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
                // The target defends on its own hex: terrain (and any known structure) counts.
                float hexBonus = AiMapMemory.KnownHexDefenseBonusFor(
                    snap.Observer, t.Hex, t.Owner);
                float readyWin = WorthIt.WinChance(readyRoster, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, hexBonus,
                    readyCommander, t.Commander);
                HeroRoleEvaluator.CommandProjection assembly = BestAssembly(commanders, assemblableBodies,
                    new[] { new WorthIt.DefendingArmy(defenders, t.Commander, hexBonus) }, hexBonus);
                float asmWin = assembly.WinChance;
                bool cover = WorthIt.CanDamageAll(assembly.Roster, defenders, hexBonus);
                int minDist = fromHexes.Count > 0 ? fromHexes.Min(h => HexGridMath.Distance(h, t.Hex)) : 99;
                int eta = CeilDiv(minDist, moverBudget);
                float targetValue = Mathf.Min(AiConfigV2.assetValueArmyCap,
                    AiPower.EffectiveArmyPowerFromProfiles(defenders) / AiConfigV2.assetValueArmyPowerDivisor);
                float confidence = ConfidenceForSighting(snap, t);
                bool gate = cover && asmWin >= AiConfigV2.opportunityMinViableWinChance;
                float score = 0f;
                if (gate)
                {
                    float effValue = Mathf.Max(targetValue, AiConfigV2.opportunityBeatableValueFloor);
                    float valueTerm = Mathf.Clamp01(effValue / Mathf.Max(0.0001f, AiConfigV2.opportunityValueNorm));
                    float etaTerm = 1f / (1f + AiConfigV2.opportunityEtaWeight * Mathf.Max(0, eta));
                    float costTerm = Mathf.Clamp01(1f - AiConfigV2.opportunityCostWeight * (1f - asmWin));
                    float raw = asmWin * valueTerm * etaTerm * costTerm * confidence;
                    score = Mathf.Clamp01(raw / Mathf.Max(0.0001f, AiConfigV2.opportunityScoreNorm));
                }

                all.Add(new CombatOpportunity(
                    hasTarget: true,
                    targetHex: t.Hex,
                    target: RaidTargetRef.ForNeutralArmy(t.ArmyId),
                    targetOwner: t.Owner,
                    targetIsNeutral: RaidObjectiveEvaluator.IsNeutralRaidTarget(t.Owner),
                    defenderCount: defenders.Count,
                    readyWinChance: readyWin,
                    assemblableWinChance: asmWin,
                    canCoverAll: cover,
                    battleCostProxy: 1f - asmWin,
                    eta: eta,
                    targetValue: targetValue,
                    confidence: confidence,
                    gatePassed: gate,
                    opportunityScore: score));
            }

            if (snap.Known.EventGuards != null)
            {
                foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                {
                    IReadOnlyList<WorthIt.DefenderProfile> defenders = g.Defenders
                        ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
                    float hexBonus = AiMapMemory.KnownHexDefenseBonusFor(
                        snap.Observer, g.Hex, defendingOwner: null);
                    float readyWin = WorthIt.WinChance(readyRoster, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, hexBonus,
                        readyCommander, g.Commander);
                    HeroRoleEvaluator.CommandProjection assembly = BestAssembly(commanders, assemblableBodies,
                        new[] { new WorthIt.DefendingArmy(defenders, g.Commander, hexBonus) }, hexBonus);
                    float asmWin = assembly.WinChance;
                    bool cover = WorthIt.CanDamageAll(assembly.Roster, defenders, hexBonus);
                    int minDist = fromHexes.Count > 0 ? fromHexes.Min(h => HexGridMath.Distance(h, g.Hex)) : 99;
                    int eta = CeilDiv(minDist, moverBudget);
                    float targetValue = Mathf.Min(AiConfigV2.assetValueArmyCap,
                        AiPower.EffectiveArmyPowerFromProfiles(defenders) / AiConfigV2.assetValueArmyPowerDivisor);
                    // A remembered event guard is a fixed, card-defined encounter, not a moving
                    // enemy-army contact. Its observed defender profile cannot become less reliable
                    // merely because ThreatModel (correctly) contains no enemy army at this hex.
                    // Event existence is revalidated by the existing Raid lifecycle/execution owners.
                    float confidence = AiConfigV2.threatConfidenceExact;
                    bool gate = cover && asmWin >= AiConfigV2.opportunityMinViableWinChance;
                    float score = 0f;
                    if (gate)
                    {
                        float effValue = Mathf.Max(targetValue, AiConfigV2.opportunityBeatableValueFloor);
                        float valueTerm = Mathf.Clamp01(effValue / Mathf.Max(0.0001f, AiConfigV2.opportunityValueNorm));
                        float etaTerm = 1f / (1f + AiConfigV2.opportunityEtaWeight * Mathf.Max(0, eta));
                        float costTerm = Mathf.Clamp01(1f - AiConfigV2.opportunityCostWeight * (1f - asmWin));
                        float raw = asmWin * valueTerm * etaTerm * costTerm * confidence;
                        score = Mathf.Clamp01(raw / Mathf.Max(0.0001f, AiConfigV2.opportunityScoreNorm));
                    }

                    all.Add(new CombatOpportunity(
                        hasTarget: true,
                        targetHex: g.Hex,
                        target: RaidTargetRef.ForEventGuard(g.Hex),
                        targetOwner: null,
                        targetIsNeutral: true,
                        defenderCount: defenders.Count,
                        readyWinChance: readyWin,
                        assemblableWinChance: asmWin,
                        canCoverAll: cover,
                        battleCostProxy: 1f - asmWin,
                        eta: eta,
                        targetValue: targetValue,
                        confidence: confidence,
                        gatePassed: gate,
                        opportunityScore: score));
                }
            }

            report.All = all;
            report.Best = all.Count > 0
                ? all.OrderByDescending(o => o.OpportunityScore).ThenByDescending(o => o.AssemblableWinChance).First()
                : CombatOpportunity.None;
            List<CombatOpportunity> neutrals = all.Where(o => o.TargetIsNeutral).ToList();
            report.NeutralOpportunities = neutrals;
            report.BestNeutralOpportunity = neutrals.Count > 0
                ? neutrals.OrderByDescending(o => o.OpportunityScore)
                    .ThenByDescending(o => o.AssemblableWinChance).First()
                : CombatOpportunity.None;
            return report;
        }

        // Everything that could form the assemblable roster: bodies on the map and in hand.
        private static List<WorthIt.DefenderProfile> AssemblableBodies(WorldSnapshot snap)
        {
            var bodies = new List<WorthIt.DefenderProfile>();
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || a.IsPrison) continue;
                if (a.Members != null) bodies.AddRange(a.Members);
            }
            foreach (CardData card in snap.Self.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
            {
                CardDefinition d = card?.Definition;
                if (d != null && d.cardType == CardType.Unit)
                    bodies.Add(AiPower.ToDefenderProfile(d));
            }
            return bodies;
        }

        // Its commander options: heroes on the map and in hand. Which one leads is decided per
        // fight by HeroRoleEvaluator (BestAssembly).
        private static List<HeroRoleEvaluator.HeroProfile> Commanders(WorldSnapshot snap) =>
            (snap.Self.CommandHeroes ?? (IReadOnlyList<OwnCommandHero>)System.Array.Empty<OwnCommandHero>())
                .Where(h => h.Source != ForceSource.Deck)
                .Select(h => h.Profile)
                .ToList();

        private static ArmySnapshot BestReadyArmy(WorldSnapshot snap) =>
            snap.Self.Armies
                .Where(a => a.IsStructuralRaidActor && a.Members != null && a.Members.Count > 0)
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .FirstOrDefault();

        // Every input CombatOpportunity.IsViable depends on, exactly and in order: the ready
        // roster and its commander, the assemblable bodies, the commander options, and per target
        // (enemy sightings, neutral sightings, event guards - Analyze's order) its defenders,
        // commander and hex defence. Equal fingerprint => every IsViable verdict is equal, with no
        // Monte Carlo run. Distance, confidence and value are left out on purpose: IsViable never
        // reads them. If Analyze's viability ever reads a new input, add it here.
        internal static string ViabilityInputsFingerprint(WorldSnapshot snap)
        {
            if (snap?.Self == null || snap.Known == null)
                return "none";
            var sb = new System.Text.StringBuilder(512);
            ArmySnapshot ready = BestReadyArmy(snap);
            sb.Append("ready=");
            AppendCommander(sb, ready?.Commander ?? default);
            AppendProfiles(sb, ready?.Members);
            sb.Append("|bodies=");
            AppendProfiles(sb, AssemblableBodies(snap));
            sb.Append("|cmd=");
            foreach (HeroRoleEvaluator.HeroProfile h in Commanders(snap))
            {
                sb.Append(h.CommandRating).Append(',').Append(h.RolePreference).Append(',')
                    .Append(Exact(h.Leadership)).Append(',').Append(h.StableKey).Append(',');
                AppendCommander(sb, h.Commander);
                sb.Append(';');
            }
            AppendSightings(sb, snap, snap.Known.EnemySightings);
            AppendSightings(sb, snap, snap.Known.NeutralSightings);
            if (snap.Known.EventGuards != null)
                foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                {
                    sb.Append("|g=").Append(g.Hex.Q).Append(',').Append(g.Hex.R).Append(',')
                        .Append(Exact(AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, g.Hex, defendingOwner: null)));
                    AppendCommander(sb, g.Commander);
                    AppendProfiles(sb, g.Defenders);
                }
            return sb.ToString();
        }

        private static void AppendSightings(System.Text.StringBuilder sb, WorldSnapshot snap,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> sightings)
        {
            if (sightings == null)
                return;
            foreach (AiMapMemory.KnownEnemySighting t in sightings)
            {
                sb.Append("|t=").Append(t.Hex.Q).Append(',').Append(t.Hex.R).Append(',')
                    .Append(Exact(AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, t.Hex, t.Owner)));
                AppendCommander(sb, t.Commander);
                AppendProfiles(sb, t.Defenders);
            }
        }

        private static string Exact(float value) =>
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static void AppendCommander(System.Text.StringBuilder sb, WorthIt.SideCommander c) =>
            sb.Append('<').Append(c.Present ? 1 : 0).Append(',').Append(c.Initiative).Append(',')
                .Append(c.Fate).Append('>');

        // Order-preserving and full precision: the simulation (and its seed) depends on both.
        private static void AppendProfiles(System.Text.StringBuilder sb,
            IReadOnlyCollection<WorthIt.DefenderProfile> profiles)
        {
            sb.Append('[');
            if (profiles != null)
                foreach (WorthIt.DefenderProfile p in profiles)
                {
                    sb.Append(Exact(p.Attack)).Append('/').Append(Exact(p.Defense)).Append('/')
                        .Append(Exact(p.HitPoints)).Append('/').Append(Exact(p.MaxHitPoints)).Append('/')
                        .Append(p.Initiative).Append('/').Append(p.HasCeramicArmor ? 1 : 0)
                        .Append(p.IsGroundCombatant ? 1 : 0).Append('/');
                    if (p.TypeTags != null)
                        foreach (UnitTypeTag tag in p.TypeTags)
                            sb.Append((int)tag).Append(',');
                    sb.Append('/');
                    if (p.Abilities != null)
                        foreach (string ability in p.Abilities)
                            sb.Append(ability).Append(',');
                    sb.Append(';');
                }
            sb.Append(']');
        }

        // The assemblable roster under the commander HeroRoleEvaluator picks for THIS fight: the
        // best bodies that fit under each candidate, judged against the opposition. With no hero
        // anywhere the stack holds NoHeroStackCapacity bodies and has no commander; that is the
        // same projection with no commander slot to take (rating = capacity + 1).
        private static HeroRoleEvaluator.CommandProjection BestAssembly(
            List<HeroRoleEvaluator.HeroProfile> commanders, List<WorthIt.DefenderProfile> bodies,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus)
        {
            if (commanders.Count == 0)
                return HeroRoleEvaluator.ProjectCommand(NoHeroStackCapacity + 1, 0, default,
                    bodies, opposition, hexBonus);
            return commanders
                .Select(h => HeroRoleEvaluator.Candidate(h, HeroRoleEvaluator.ProjectCommand(
                    h.CommandRating, 0, h.Commander, bodies, opposition, hexBonus)))
                .OrderBy(c => c, Comparer<HeroRoleEvaluator.CommandCandidate>.Create(
                    HeroRoleEvaluator.CompareCandidates))
                .First().Projection;
        }

        // A neutral sighting has its own honest last-observed turn. It is not part of the enemy
        // threat-contact model; using that model's missing-contact fallback used to mark even a
        // just-discovered neutral as stale and silently reject viable Raid objectives.
        // Keep the established threat-contact confidence for ordinary enemy armies unchanged.
        private static float ConfidenceForSighting(WorldSnapshot snap, AiMapMemory.KnownEnemySighting sighting)
        {
            if (RaidObjectiveEvaluator.IsNeutralRaidTarget(sighting.Owner))
                return sighting.SeenTurn == snap.TurnNumber
                    ? AiConfigV2.threatConfidenceExact : AiConfigV2.threatConfidenceLastKnown;
            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts != null)
                foreach (EnemyContactSnapshot c in contacts)
                    if (c.Position.HasValue && c.Position.Value.Equals(sighting.Hex))
                        return c.Confidence;
            return AiConfigV2.threatConfidenceLastKnown;
        }

        private static int CeilDiv(int a, int b) => AiV2Util.CeilDiv(a, b);
    }
}
