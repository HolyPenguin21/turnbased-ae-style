using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // The one shared "is this ground fight worth it" estimate — every army-vs-army comparison on
    // the map goes through here, never a private formula at a call site.
    //
    //  · DefenderProfile — one combatant as the estimator sees it: our own live units
    //    (FromLiveUnit) or an enemy's fog-honest remembered roster (AiMapMemory).
    //  · Estimate / WinChance — MonteCarloTrials complete battles (SimulateOneBattle): each round a
    //    shuffle-then-Initiative turn order, every living actor rolls real tokens (50% per die)
    //    against a random living enemy, the canonical ability-modifier chain, and the Fate duel of
    //    every exchange (ResolveExchange, spending decided by FateDuelAi). Each side may have a
    //    commander (SideCommander — ArmyData.Commander, the army's first hero): its Initiative is
    //    added to every combatant of its side and its Fate buys rerolls. No grid positions.
    //  · EstimateSequential — a hex held by several armies: one battle per defending army,
    //    strongest first, wounds carry over, Fate refills.
    //  · HexDefenseBonus — terrain + Base-building defence, added to the DEFENDER's tokens only,
    //    exactly as BattleScreenUI.Combat's BeginAttack does in a real fight.
    //  · CanDamageAll — the coverage gate: every known defender must have at least one attacker
    //    wounding it in at least CoverageMinHitProbability of exchanges (the exact tokens odds
    //    BattleCombatOdds gives live combat), because a win chance alone can overstate a fight
    //    nothing in the roster can actually hurt.
    //  · CombatValue — the quick body value every strongest-first roster pick uses.
    public static partial class WorthIt
    {
        // Attack-sum of `army`'s own non-hero members — the same side of the comparison every
        // caller here always uses for the ATTACKING army (heroes never counted, matching every
        // other flat Attack/Defense sum already in this codebase).
        public static float AttackSum(ArmyData army) => AttackSum(army?.Members);

        // Own non-hero Defense sum, no hex bonus — used on its own as the ATTACKER's side of Score's
        // return-fire read (an attacking army isn't standing on a defensible hex it gets credit for, it's marching onto the defender's).
        public static float DefenseSum(ArmyData army) => DefenseSum(army?.Members);

        // Roster-scoped overloads — same non-hero flat sum against an explicit member set rather
        // than a whole ArmyData. Needed where the caller must rank/measure only the members it is
        // actually allowed to know about: a defender's members that are HIDDEN from the observer
        // must not feed the "which army do I contact" power read (BattleInitiator.FindEnemyAt),
        // or an invisible heavy unit inside a mixed army would still steer the enemy's target
        // pick (project owner's own P1).
        public static float AttackSum(IEnumerable<UnitData> members) => members == null ? 0f : CombatantsOf(members).Sum(m => ExplosionAttack.AttackPower(m.Attack, m.Abilities));
        public static float DefenseSum(IEnumerable<UnitData> members) => members == null ? 0f : CombatantsOf(members).Sum(m => m.Defense);

        // The ONE ground-combatant filter every estimator entry point below applies to BOTH sides
        // (UnitData.IsGroundCombatant / DefenderProfile.IsGroundCombatant). Callers pass whole
        // rosters; no caller filters heroes itself, so no caller can disagree with the battle.
        private static List<UnitData> CombatantsOf(IEnumerable<UnitData> units) =>
            units == null ? new List<UnitData>() : units.Where(u => u != null && u.IsGroundCombatant).ToList();

        private static List<DefenderProfile> CombatantsOf(IEnumerable<DefenderProfile> profiles) =>
            profiles == null ? new List<DefenderProfile>() : profiles.Where(p => p.IsGroundCombatant).ToList();

        // Tactical battle has two different concepts now: actors and targets. Heroes never act,
        // but while combatants are still fighting they are ordinary attackable board targets.
        private static List<UnitData> TacticalTargetsOf(IEnumerable<UnitData> units) =>
            units == null ? new List<UnitData>() : units.Where(u => u != null && (u.IsGroundCombatant || u.IsHero)).ToList();

        private static List<DefenderProfile> TacticalTargetsOf(IEnumerable<DefenderProfile> profiles) =>
            profiles == null ? new List<DefenderProfile>() : profiles.Where(p => p.IsGroundCombatant || p.IsHero).ToList();

        private static List<DefenderProfile> WithBattleSummons(IEnumerable<DefenderProfile> source)
        {
            var roster = TacticalTargetsOf(source);
            if (roster.Any(p => p.IsSummoned))
                return roster;

            UnitAbilityCatalog catalog = UnitAbilityCatalog.Active;
            CardDefinition template = catalog != null ? catalog.ResolveRaiseTheRotsCard() : null;
            if (template == null || catalog.raiseTheRotsUnitsPerSummoner <= 0)
                return roster;

            int summoners = roster.Count(p => p.Abilities != null
                && p.Abilities.Contains(UnitAbilities.RaiseTheRots));
            int freeDeploymentCells = Mathf.Max(0, BattleGrid.Columns * 2 - roster.Count);
            int count = Mathf.Min(freeDeploymentCells,
                summoners * Mathf.Max(0, catalog.raiseTheRotsUnitsPerSummoner));
            for (int i = 0; i < count; i++)
                roster.Add(ProfileFromCard(template));
            return roster;
        }

        private static DefenderProfile ProfileFromCard(CardDefinition card) =>
            new DefenderProfile(card.defenseRating,
                card.grantedAbilities != null && card.grantedAbilities.Contains(UnitAbilities.CeramicArmor),
                card.unitTypeTags, card.attack, card.hitPoints, card.initiative,
                card.grantedAbilities, card.hitPoints, isGroundCombatant: true, isSummoned: true,
                range: card.range);

        // Number of simulated exchanges Score/WinChance each run per call — capped at 100 per the
        // project owner's own explicit call (2026-08-22: "ограничиваемся сотней вызовов"),
        // lowered to 25 (2026-09-19) once profiling showed CombatOpportunityAnalyzer actually
        // calls this twice per known target, per settled step, many settled steps per AI turn —
        // not "once or twice per turn" as assumed when the 100-trial limit was first set. 25
        // trials still gives a usable win-chance estimate; it's a named constant purely so nobody
        // quietly cranks it back up on a hot path without weighing that cost again.
        private const int MonteCarloTrials = 25;

        // Deterministic per-matchup seed — built ONLY from the raw numbers that describe the
        // matchup (rosters, commanders, hex bonus), never from GetHashCode() of an object. The
        // SAME matchup always replays the SAME trials; any real change rolls fresh ones. Its own
        // System.Random per call, never UnityEngine.Random (read-only strategic bookkeeping must
        // not consume the game's RNG stream). A side without a commander adds nothing to the
        // hash, so commander-less matchups keep exactly the seeds they always had.
        private static int BuildRosterSeed(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(hexDefenseBonus);
                if (attackerCommander.Present)
                    hash = (hash * 31 + 101 + attackerCommander.Initiative) * 31 + attackerCommander.Fate;
                if (defenderCommander.Present)
                    hash = (hash * 31 + 211 + defenderCommander.Initiative) * 31 + defenderCommander.Fate;
                if (attackerCommander.SupportHeroes != null)
                    foreach (DefenderProfile hero in attackerCommander.SupportHeroes) hash = AccumulateProfileHash(hash, hero);
                if (defenderCommander.SupportHeroes != null)
                    foreach (DefenderProfile hero in defenderCommander.SupportHeroes) hash = AccumulateProfileHash(hash, hero);
                foreach (DefenderProfile p in attackerUnits)
                    hash = AccumulateProfileHash(hash, p);
                hash = hash * 31 + 12345; // separates the two rosters — an empty attacker side must
                                           // never hash the same as an empty enemy side
                foreach (DefenderProfile p in enemyUnits)
                    hash = AccumulateProfileHash(hash, p);
                return hash;
            }
        }

        private static int AccumulateProfileHash(int hash, DefenderProfile p)
        {
            unchecked
            {
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.Attack);
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.Defense);
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.HitPoints);
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.MaxHitPoints);
                hash = hash * 31 + p.Initiative;
                hash = hash * 31 + (p.IsGroundCombatant ? 1 : 0);
                hash = hash * 31 + (p.IsHero ? 1 : 0);
                hash = hash * 31 + p.FateMax;
                hash = hash * 31 + (p.IsSummoned ? 1 : 0);
                // Empty/singleton collections are already sorted. Avoid allocating LINQ sort
                // iterators for the common case without changing the seed's ordering rule.
                if (p.TypeTags.Count == 1)
                    hash = hash * 31 + (int)p.TypeTags[0];
                else if (p.TypeTags.Count > 1)
                    foreach (UnitTypeTag tag in p.TypeTags.OrderBy(t => (int)t))
                        hash = hash * 31 + (int)tag;
                if (p.Abilities.Count == 1)
                    hash = AccumulateAbilityHash(hash, p.Abilities[0]);
                else if (p.Abilities.Count > 1)
                    foreach (string ability in p.Abilities.OrderBy(a => a, System.StringComparer.Ordinal))
                        hash = AccumulateAbilityHash(hash, ability);
                if (p.Consumption != null) hash = AccumulateAbilityHash(hash, p.Consumption.Key);
                if (p.SingleUseUnit) hash = hash * 31 + 19019;
                if (!p.CanDisruptBuilding) hash = hash * 31 + 19021;
                return hash;
            }
        }

        private static int AccumulateAbilityHash(int hash, string ability)
        {
            if (ability == null) return hash;
            unchecked
            {
                foreach (char ch in ability)
                    hash = hash * 31 + ch;
                return hash * 31 + 7;
            }
        }

        // A side's battle commander (ArmyData.Commander — the army's first hero). Heroes never
        // fight, but the commander's Initiative is added to every combatant of its side
        // (BattleTurnOrder) and its Fate buys rerolls in every exchange (BattleAttackPopupUI's
        // duel). Live reads retain current Fate: a preceding standalone air strike can spend it.
        // Completed ground encounters replenish Fate; card-only projections start full.
        public readonly struct SideCommander : System.IEquatable<SideCommander>
        {
            public readonly bool Present;
            public readonly int Initiative;
            public readonly int Fate;
            // Body-only army snapshots still need the heroes whose passive battle skills act —
            // Heal (a wounded Bio ally) and Drill (an ally's Attack) — including secondary heroes.
            // Their HP/tags/abilities are frozen knowledge, not live reads.
            public readonly IReadOnlyList<DefenderProfile> SupportHeroes;

            public SideCommander(int initiative, int fate, IReadOnlyList<DefenderProfile> supportHeroes = null)
            {
                Present = true;
                Initiative = initiative;
                Fate = Mathf.Max(0, fate);
                SupportHeroes = supportHeroes;
            }

            public static SideCommander Of(UnitData hero) =>
                hero == null ? default : new SideCommander(hero.Initiative, hero.Fate,
                    hero.HitPointsCurrent > 0 && !hero.IsPrisoner && IsSupportHero(hero.Abilities)
                    ? new[] { FromLiveUnit(hero) } : null);
            // A hero that is still a card (an event guard, a hero about to be played).
            public static SideCommander Of(CardDefinition hero) =>
                hero == null ? default : new SideCommander(hero.initiative, hero.fate,
                    IsSupportHero(hero.grantedAbilities)
                    ? new[] { new DefenderProfile(hero.fate, false, hero.unitTypeTags, hero.attack,
                        hero.hitPoints, hero.initiative, hero.grantedAbilities, hero.hitPoints,
                        isGroundCombatant: false, isHero: true, fateMax: hero.fate, range: hero.range) } : null);
            public static SideCommander Of(IEnumerable<UnitData> members)
            {
                var roster = members?.ToList();
                UnitData commander = ArmyData.CommanderOf(roster);
                if (commander == null) return default;
                var support = roster.Where(u => u != null && u.IsHero && !u.IsPrisoner
                    && u.HitPointsCurrent > 0 && IsSupportHero(u.Abilities)).Select(FromLiveUnit).ToList();
                return new SideCommander(commander.Initiative, commander.Fate, support.Count > 0 ? support : null);
            }

            // This commander leading a formation whose other heroes stay in it: their Heal/Drill
            // act in its battles too (the profile form of Of(members) for a roster led by it).
            public SideCommander WithSupport(IEnumerable<DefenderProfile> otherHeroes)
            {
                if (!Present || otherHeroes == null) return this;
                var support = (SupportHeroes ?? System.Array.Empty<DefenderProfile>())
                    .Concat(otherHeroes.Where(p => p.IsHero && p.HitPoints > 0 && IsSupportHero(p.Abilities)))
                    .ToList();
                return support.Count == (SupportHeroes?.Count ?? 0) ? this
                    : new SideCommander(Initiative, Fate, support);
            }

            public bool Equals(SideCommander other)
            {
                if (Present != other.Present || Initiative != other.Initiative || Fate != other.Fate
                    || (SupportHeroes?.Count ?? 0) != (other.SupportHeroes?.Count ?? 0)) return false;
                for (int i = 0; i < (SupportHeroes?.Count ?? 0); i++)
                {
                    DefenderProfile a = SupportHeroes[i], b = other.SupportHeroes[i];
                    if (a.HitPoints != b.HitPoints || a.MaxHitPoints != b.MaxHitPoints
                        || a.Defense != b.Defense || a.FateMax != b.FateMax || a.Initiative != b.Initiative
                        || a.CanDisruptBuilding != b.CanDisruptBuilding
                        || !a.Abilities.SequenceEqual(b.Abilities) || !a.TypeTags.SequenceEqual(b.TypeTags)) return false;
                }
                return true;
            }
            public override bool Equals(object obj) => obj is SideCommander other && Equals(other);
            public override int GetHashCode() => unchecked((Present ? 1 : 0) * 397 ^ Initiative * 31 ^ Fate);
        }

        // Passive heroes (Heal, Drill, Disruption) stay in the simulated roster even though
        // heroes never take a combat turn (SideCommander.SupportHeroes).
        private static bool IsSupportHero(IEnumerable<string> abilities) =>
            abilities != null && abilities.Any(a => a == UnitAbilities.Heal || a == UnitAbilities.Drill
                || a == UnitAbilities.Disruption);

        // One armed exchange as the real battle resolves it: both pools rolled, then the Fate duel
        // (FateDuelOrder — defender first, sides alternate while anyone spends; each
        // spend rerolls the first miss with an ordinary die; only attackers stop after a failed
        // reroll), every spend decided by the ONE policy FateDuelAi owns. Returns the damage after
        // the canonical ability modifier chain.
        private static BattleSimExchangeOutcome ResolveExchange(BattleUnit actor, BattleUnit target,
            ref int actorFate, ref int targetFate, System.Random rng, AbilityMagnitudes magnitudes)
        {
            return BattleSimulationKernel.ResolveExchange(
                Mathf.RoundToInt(actor.Attack), Mathf.RoundToInt(target.Defense),
                actor.Abilities, target.TypeTags, target.Abilities,
                ref actorFate, ref targetFate,
                Mathf.Max(1, Mathf.CeilToInt(target.Hp)),
                magnitudes, rng);
        }

        // ---- Full round-by-round Monte Carlo (2026-08-22, project owner's own call) ----
        //
        // Everything above this point still only ever plays out ONE simultaneous exchange per
        // trial, because that used to be all the map genuinely knew about the other side — an
        // aggregate Attack/Defense sum, no composition, no HP. That's no longer true: once an
        // enemy army has actually been SEEN, AiMapMemory now remembers its full per-unit roster
        // (Attack/Defense/HitPoints/Initiative, not just Defense — see DefenderProfile's own
        // comment), and a "cheat" read (AiDefencePlanner.CheatEstimateRaiderThreat) already has the
        // real live roster directly. Whenever a real per-unit roster is available on BOTH sides,
        // WinChance below plays MonteCarloTrials complete battles to actual HP-zero instead of one
        // flat exchange — same round structure BattleTurnOrder uses (shuffle for a random
        // Initiative tie-break, then sort descending), same tokens, just with a
        // RANDOM target each attack rather than BattleTargetSelector's scored pick — this class
        // still never has a live grid/position/range to be smart about (see this file's own top
        // comment), only a remembered/cheat-read roster.
        private struct BattleUnit
        {
            public bool SingleUseUnit;
            public bool CanDisruptBuilding;
            public EquipmentConsumption Consumption;
            public float Attack;
            public float Defense;
            public IReadOnlyList<string> Abilities;
            public IReadOnlyList<UnitTypeTag> TypeTags;
            public int Initiative;
            public float Hp;
            public bool IsHero;
            public int HeroFate;
            public bool IsSummoned;
            // Attack range (-1: unknown). Positions do not exist here, so a Range-1 (or unknown)
            // attacker is the one that stands within reach of a Counterattack answer.
            public int Range;

            public bool HasAbility(string ability) => Abilities != null && Abilities.Contains(ability);

            // True max HP the unit entered this simulated battle with — separate from Hp (which
            // Estimate() below mutates round by round) because CriticalAfterBattleChance needs to
            // compare a SURVIVOR's final Hp against its own real max, not the (possibly already
            // wounded) starting Hp of this one simulated fight. For the DefenderProfile-only
            // conversion below (ToBattleUnits), nothing here knows a real MaxHp distinct from the
            // remembered/assumed starting HitPoints, so it defaults to the same value as Hp — see
            // ToAttackerBattleUnits for the one path (our own live roster) that fills in the real one.
            public float MaxHp;
        }

        private static void ConsumeRosterEquipment(List<BattleUnit> side, int index)
        {
            BattleUnit unit = side[index];
            if (unit.SingleUseUnit) { unit.Hp = 0f; side[index] = unit; return; }
            EquipmentConsumption change = unit.Consumption;
            if (change == null) return;
            unit.Attack += change.Delta(EquipmentStat.Attack);
            unit.Defense += change.Delta(EquipmentStat.Defense);
            unit.Initiative += change.Delta(EquipmentStat.Initiative);
            unit.Range += change.Delta(EquipmentStat.Range);
            unit.HeroFate = Mathf.Max(0, unit.HeroFate + change.Delta(EquipmentStat.Fate));
            unit.Hp = change.RemainingHitPoints(unit.Hp, unit.MaxHp, out unit.MaxHp);
            unit.Abilities = change.ApplyAbilities(unit.Abilities);
            unit.Consumption = null;
            side[index] = unit;
        }

        // Longest a single simulated battle plays before being scored a draw — map-sized rosters
        // (a handful of units a side) resolve in a handful of rounds almost always; this is
        // generous headroom for a Monte Carlo trial to terminate, not a tuned balance number.
        private const int MaxSimulatedRounds = 50;

        private static List<BattleUnit> ToBattleUnits(IReadOnlyCollection<DefenderProfile> profiles,
            float extraDefense = 0f, int initiativeBonus = 0, SideCommander commander = default)
        {
            var list = new List<BattleUnit>();
            if (profiles == null)
                return list;
            foreach (DefenderProfile p in profiles)
            {
                list.Add(ToBattleUnit(p, extraDefense, initiativeBonus));
            }
            int represented = list.Count(u => u.IsHero && IsSupportHero(u.Abilities));
            for (int i = represented; i < (commander.SupportHeroes?.Count ?? 0); i++)
                list.Add(ToBattleUnit(commander.SupportHeroes[i], extraDefense, 0));
            return list;
        }

        // Both key construction and miss computation use this exact conversion. A cache hit
        // needs its values, not a temporary list containing them.
        private static BattleUnit ToBattleUnit(DefenderProfile p, float extraDefense, int initiativeBonus)
        {
            float hp = Mathf.Max(1f, p.HitPoints);
            return new BattleUnit
            {
                Attack = p.Attack,
                Defense = p.Defense + extraDefense,
                Abilities = p.Abilities,
                TypeTags = p.TypeTags,
                Initiative = p.IsHero ? p.Initiative : p.Initiative + initiativeBonus,
                Hp = hp,
                MaxHp = Mathf.Max(hp, p.MaxHitPoints),
                IsHero = p.IsHero,
                HeroFate = p.FateMax,
                IsSummoned = p.IsSummoned,
                Range = p.Range,
                Consumption = p.Consumption,
                SingleUseUnit = p.SingleUseUnit,
                CanDisruptBuilding = p.CanDisruptBuilding && p.HitPoints > 0f,
            };
        }

        // Attacker BattleUnits built straight off the real ArmyData roster rather than through
        // DefenderProfile — FromLiveUnit's own comment already explains DefenderProfile.HitPoints
        // means CURRENT hp for our own side, so it can't also carry the unit's true MaxHp that
        // Estimate()'s CriticalAfterBattleChance needs. Non-hero only, same convention every other
        // attacker-side read in this file uses.
        private static List<BattleUnit> ToAttackerBattleUnits(ArmyData attacker, int initiativeBonus)
        {
            var list = new List<BattleUnit>();
            if (attacker == null)
                return list;
            foreach (UnitData m in TacticalTargetsOf(attacker.Members))
                list.Add(new BattleUnit
                {
                    Attack = m.Attack,
                    Defense = m.IsHero ? Mathf.Max(0, m.FateMax) : m.Defense,
                    Abilities = m.Abilities.ToList(),
                    TypeTags = m.TypeTags.ToList(),
                    Initiative = m.IsHero ? m.Initiative : m.Initiative + initiativeBonus,
                    Hp = Mathf.Max(1f, m.HitPointsCurrent),
                    MaxHp = Mathf.Max(1f, m.HitPointsMax),
                    IsHero = m.IsHero,
                    HeroFate = Mathf.Max(0, m.FateMax),
                    IsSummoned = m.IsSummoned,
                    Range = m.Range,
                    Consumption = EquipmentSystem.Consumption(m),
                    SingleUseUnit = ExplosionAttack.IsSingleUseUnit(m),
                    CanDisruptBuilding = !m.IsAviation && !m.IsPrisoner,
                });
            return list;
        }

        private static void AppendFreshBattleSummons(List<BattleUnit> side, int initiativeBonus)
        {
            if (side == null || side.Any(u => u.IsSummoned))
                return;
            UnitAbilityCatalog catalog = UnitAbilityCatalog.Active;
            CardDefinition template = catalog != null ? catalog.ResolveRaiseTheRotsCard() : null;
            if (template == null)
                return;

            int summoners = side.Count(u => u.Hp > 0f && !u.IsSummoned
                && u.Abilities != null && u.Abilities.Contains(UnitAbilities.RaiseTheRots));
            int count = Mathf.Min(Mathf.Max(0, BattleGrid.Columns * 2 - side.Count),
                summoners * Mathf.Max(0, catalog.raiseTheRotsUnitsPerSummoner));
            if (count <= 0)
                return;

            DefenderProfile profile = ProfileFromCard(template);
            for (int i = 0; i < count; i++)
                side.AddRange(ToBattleUnits(new[] { profile }, 0f, initiativeBonus));
        }

        // Aggregate-roster mirror of BattleScreenUI.Combat.cs's ResolveSplashSkills for
        // SimulateOneBattle. Half (floored) of the primary damage minus the victim's own
        // CeramicArmor (Option A — the attacker's offensive bonuses are already in `primaryDamage`),
        // to up to two random OTHER living enemies (Splash) and/or one random living Bio enemy
        // (Scorcher). Positions don't exist in this model, so "adjacent" is approximated as
        // "random other body". Only runs for a Splash/Scorcher actor.
        private static void ApplyRosterSplash(List<BattleUnit> enemyList, int primaryTargetIndex,
            BattleUnit actor, int primaryDamage, System.Random rng, AbilityMagnitudes magnitudes,
            List<int> others)
        {
            bool splash = actor.HasAbility(UnitAbilities.Splash);
            bool scorcher = actor.HasAbility(UnitAbilities.Scorcher);
            if ((!splash && !scorcher) || primaryDamage <= 0)
                return;
            int half = primaryDamage / 2; // floor
            if (half <= 0)
                return;

            others.Clear();
            for (int i = 0; i < enemyList.Count; i++)
                if (i != primaryTargetIndex && enemyList[i].Hp > 0f)
                    others.Add(i);
            if (others.Count == 0)
                return;

            List<BattleSimSecondaryTarget> selectedTargets =
                BattleSimulationKernel.SelectSecondaryTargets(
                    others.Count,
                    i =>
                    {
                        int unitIndex = others[i];
                        return enemyList[unitIndex].TypeTags != null
                            && enemyList[unitIndex].TypeTags.Contains(UnitTypeTag.Bio);
                    },
                    splash, scorcher, rng);
            foreach (BattleSimSecondaryTarget selected in selectedTargets)
                RosterSideHit(enemyList, others[selected.Index], primaryDamage, magnitudes);
        }

        private static void RosterSideHit(List<BattleUnit> list, int idx, int primaryDamage,
            AbilityMagnitudes magnitudes)
        {
            BattleUnit u = list[idx];
            int damage = BattleSimulationKernel.SecondaryDamage(
                primaryDamage, u.Abilities, magnitudes);
            if (damage <= 0)
                return;
            u.Hp -= damage;

            int attack = Mathf.RoundToInt(u.Attack);
            int defense = Mathf.RoundToInt(u.Defense);
            BattleSimulationKernel.ApplyBerserkIfHit(
                true, u.Abilities, ref attack, ref defense, magnitudes);
            u.Attack = attack;
            u.Defense = defense;
            list[idx] = u;
        }

        private static bool AnyCombatantAlive(List<BattleUnit> units)
        {
            foreach (BattleUnit u in units)
                if (!u.IsHero && u.Hp > 0f)
                    return true;
            return false;
        }

        private static int FirstLivingHeroIndex(List<BattleUnit> units)
        {
            for (int i = 0; i < units.Count; i++)
                if (units[i].IsHero && units[i].Hp > 0f)
                    return i;
            return -1;
        }

        private static void TransferCommanderAfterHeroDeath(List<BattleUnit> side, int deadHeroIndex,
            ref int sideFate)
        {
            if (deadHeroIndex < 0 || deadHeroIndex >= side.Count || !side[deadHeroIndex].IsHero)
                return;

            // Only the first living hero is Commander. If another hero was targeted first, command
            // does not change. If Commander died, remove its Initiative bonus from every actor and
            // immediately transfer command/Fate to the next living hero.
            int firstAlive = FirstLivingHeroIndex(side);
            int oldBonus = side[deadHeroIndex].Initiative;
            if (firstAlive >= 0 && firstAlive < deadHeroIndex)
                return;

            int newBonus = firstAlive >= 0 ? side[firstAlive].Initiative : 0;
            sideFate = firstAlive >= 0 ? side[firstAlive].HeroFate : 0;
            int delta = newBonus - oldBonus;
            for (int i = 0; i < side.Count; i++)
            {
                BattleUnit u = side[i];
                if (!u.IsHero)
                {
                    u.Initiative += delta;
                    side[i] = u;
                }
            }
        }

        // One full battle: rounds of shuffle-then-sort-by-Initiative turn order, every living
        // actor rolling real tokens against a random living enemy, until one side has nobody left or
        // MaxSimulatedRounds runs out. Returns +1 (attackers wiped the defenders), -1 (defenders
        // wiped the attackers), or 0 (mutual wipe, or neither side finished the other off in time —
        // a draw, which every readout counts as half a win).
        // Estimate-local workspace: never shared across estimates or exposed through their cache.
        // Every battle/round clears it; it changes allocations, not simulated inputs or RNG order.
        private sealed class SimulationScratch
        {
            internal readonly List<(bool isAttacker, int index)> Order;
            internal readonly HashSet<(bool isAttacker, int index)> Acted = new HashSet<(bool, int)>();
            internal readonly HashSet<(bool isAttacker, int index)> Suppressed = new HashSet<(bool, int)>();
            // Per round: Bodyguard reactions spent, Drill bonuses handed out.
            internal readonly HashSet<(bool isAttacker, int index)> BodyguardsUsed = new HashSet<(bool, int)>();
            internal readonly List<(bool isAttacker, int index)> Drilled = new List<(bool, int)>();
            internal readonly List<int> LivingTargets;
            internal readonly List<int> SecondaryTargets;
            internal readonly System.Comparison<(bool isAttacker, int index)> ByInitiativeDescending;
            private List<BattleUnit> _attackers, _defenders;

            internal SimulationScratch(int attackers, int defenders)
            {
                Order = new List<(bool, int)>(attackers + defenders);
                LivingTargets = new List<int>(System.Math.Max(attackers, defenders));
                SecondaryTargets = new List<int>(System.Math.Max(attackers, defenders));
                ByInitiativeDescending = (a, b) =>
                {
                    int ai = a.isAttacker ? _attackers[a.index].Initiative : _defenders[a.index].Initiative;
                    int bi = b.isAttacker ? _attackers[b.index].Initiative : _defenders[b.index].Initiative;
                    return bi.CompareTo(ai);
                };
            }

            internal void Reset(List<BattleUnit> attackers, List<BattleUnit> defenders)
            {
                _attackers = attackers; _defenders = defenders;
                Order.Clear(); Acted.Clear(); Suppressed.Clear();
                BodyguardsUsed.Clear(); Drilled.Clear();
                LivingTargets.Clear(); SecondaryTargets.Clear();
            }
        }

        private static void ApplyHealing(List<BattleUnit> side, int carrierIndex, System.Random rng)
        {
            BattleUnit carrier = side[carrierIndex];
            if (carrier.Hp <= 0f || !carrier.HasAbility(UnitAbilities.Heal)) return;
            int index = BattleSimulationKernel.SelectHealingTarget(side.Count, i =>
                side[i].Hp > 0f && side[i].Hp < side[i].MaxHp
                && side[i].TypeTags != null && side[i].TypeTags.Contains(UnitTypeTag.Bio), rng);
            if (index < 0) return;
            BattleUnit target = side[index];
            target.Hp = BattleSimulationKernel.HealedHitPoints(target.Hp, target.MaxHp);
            target.Consumption = target.Consumption?.AfterHpRefill();
            side[index] = target;
        }

        // UnitAbilities.Drill at round start — the live rule (BattleSimulationKernel.
        // SelectDrillTargets) on this roster; RevertRosterDrill takes the bonus back off.
        private static void ApplyRosterDrill(List<BattleUnit> side, bool isAttacker,
            List<(bool isAttacker, int index)> drilled, AbilityMagnitudes magnitudes)
        {
            int carriers = 0;
            for (int i = 0; i < side.Count; i++)
                if (side[i].Hp > 0f && side[i].HasAbility(UnitAbilities.Drill))
                    carriers++;
            if (carriers == 0 || magnitudes.DrillAttackBonus == 0)
                return;
            foreach (int index in BattleSimulationKernel.SelectDrillTargets(side.Count, carriers,
                         i => !side[i].IsHero && side[i].Hp > 0f, i => Mathf.RoundToInt(side[i].Attack)))
            {
                BattleUnit u = side[index];
                u.Attack += magnitudes.DrillAttackBonus;
                side[index] = u;
                drilled.Add((isAttacker, index));
            }
        }

        private static void RevertRosterDrill(List<BattleUnit> attackers, List<BattleUnit> defenders,
            List<(bool isAttacker, int index)> drilled, AbilityMagnitudes magnitudes)
        {
            foreach ((bool isAttacker, int index) in drilled)
            {
                List<BattleUnit> side = isAttacker ? attackers : defenders;
                BattleUnit u = side[index];
                u.Attack -= magnitudes.DrillAttackBonus;
                side[index] = u;
            }
            drilled.Clear();
        }

        // UnitAbilities.Bodyguard on a roster: positions do not exist, so — the same
        // approximation Splash makes for its neighbours — any living unspent guard of the hit side
        // counts as adjacent. The most HP wins, the lowest index on a tie (BattleRules).
        private static int FindRosterBodyguard(List<BattleUnit> side, int targetIndex, bool sideIsAttacker,
            HashSet<(bool isAttacker, int index)> used)
        {
            int best = -1;
            float bestHp = 0f;
            for (int i = 0; i < side.Count; i++)
            {
                BattleUnit u = side[i];
                if (i == targetIndex || u.IsHero || u.Hp <= bestHp || !u.HasAbility(UnitAbilities.Bodyguard)
                    || used.Contains((sideIsAttacker, i)))
                    continue;
                best = i;
                bestHp = u.Hp;
            }
            return best;
        }

        // One resolved exchange landing on list[index] (the hit side is attackers when
        // hitSideIsAttacker): HP, the victim's Berserk, Commander hand-over, then Splash/Scorcher
        // around it — this roster model has no positions, so "neighbours" are random OTHER living
        // units (a no-op for an actor with neither skill). Returns whether the hit suppresses the
        // victim's pending turn (ShockAttack).
        private static bool ApplyRosterHit(List<BattleUnit> list, int index, BattleUnit actor,
            BattleSimExchangeOutcome exchange, bool hitSideIsAttacker, System.Random rng,
            AbilityMagnitudes magnitudes, SimulationScratch scratch, ref int attackerFate, ref int defenderFate,
            List<BattleUnit> attackers, List<BattleUnit> defenders)
        {
            BattleUnit target = list[index];
            float targetHp = target.Hp;
            int transientAttack = Mathf.RoundToInt(target.Attack);
            int transientDefense = Mathf.RoundToInt(target.Defense);
            BattleSimulationKernel.ApplyPrimaryOutcome(
                exchange, actor.Abilities, target.Abilities,
                ref targetHp, ref transientAttack, ref transientDefense,
                magnitudes, out bool suppressesTurn);
            target.Hp = targetHp;
            target.Attack = transientAttack;
            target.Defense = transientDefense;
            list[index] = target;

            if (target.IsHero && target.Hp <= 0f)
            {
                if (hitSideIsAttacker)
                    TransferCommanderAfterHeroDeath(attackers, index, ref attackerFate);
                else
                    TransferCommanderAfterHeroDeath(defenders, index, ref defenderFate);
            }

            if (exchange.Damage > 0)
            {
                int commanderBefore = FirstLivingHeroIndex(list);
                ApplyRosterSplash(list, index, actor, exchange.Damage, rng, magnitudes,
                    scratch.SecondaryTargets);
                // A Commander killed by a side-hit hands over command like a primary kill.
                if (commanderBefore >= 0 && list[commanderBefore].Hp <= 0f)
                {
                    if (hitSideIsAttacker)
                        TransferCommanderAfterHeroDeath(attackers, commanderBefore, ref attackerFate);
                    else
                        TransferCommanderAfterHeroDeath(defenders, commanderBefore, ref defenderFate);
                }
            }
            return suppressesTurn;
        }

        private static float RosterExplosionValue(BattleUnit actor, List<BattleUnit> own,
            List<BattleUnit> enemy, int actorIndex)
        {
            int count = 0;
            float value = 0;
            void Add(BattleUnit target, bool friendly, bool self)
            {
                if (target.Hp <= 0 || self && actor.SingleUseUnit) return;
                count++;
                value += (friendly ? -1 : 1) * ExplosionAttack.ExpectedDamageValue(actor.Abilities,
                    Mathf.RoundToInt(target.Defense), target.TypeTags, target.Abilities, target.Hp, target.MaxHp,
                    CombatValue(target.Attack, target.Defense, target.MaxHp, target.Initiative));
            }
            for (int i = 0; i < own.Count; i++) Add(own[i], true, i == actorIndex);
            foreach (var target in enemy) Add(target, false, false);
            value *= Mathf.Min(ExplosionAttack.TargetLimit(actor.Abilities), count) / (float)Mathf.Max(1, count);
            return value - (actor.SingleUseUnit ? CombatValue(actor.Attack, actor.Defense, actor.Hp, actor.Initiative) : 0);
        }

        private static bool AnyCarries(List<BattleUnit> side, string ability)
        {
            for (int i = 0; i < side.Count; i++)
                if (side[i].HasAbility(ability) || side[i].Consumption?.After.Abilities.Contains(ability) == true)
                    return true;
            return false;
        }

        private static void ApplyRosterExplosion(BattleUnit actor, List<BattleUnit> attackers,
            List<BattleUnit> defenders, bool actorIsAttacker, int actorIndex, System.Random rng, AbilityMagnitudes magnitudes,
            SimulationScratch scratch, ref int attackerFate, ref int defenderFate)
        {
            var pool = new List<(bool attacking, int index)>();
            for (int i = 0; i < attackers.Count; i++)
                if (attackers[i].Hp > 0 && !(actor.SingleUseUnit && actorIsAttacker && i == actorIndex)) pool.Add((true, i));
            for (int i = 0; i < defenders.Count; i++)
                if (defenders[i].Hp > 0 && !(actor.SingleUseUnit && !actorIsAttacker && i == actorIndex)) pool.Add((false, i));
            int count = System.Math.Min(ExplosionAttack.TargetLimit(actor.Abilities), pool.Count);
            // Area hits neither intercept nor create Splash/Counterattack chains.
            actor.Attack = ExplosionAttack.AttackTokens;
            for (int i = 0; i < count; i++)
            {
                int j = rng.Next(i, pool.Count); (pool[i], pool[j]) = (pool[j], pool[i]);
                var victim = pool[i];
                var side = victim.attacking ? attackers : defenders;
                BattleUnit target = side[victim.index];
                int offensiveFate = actorIsAttacker == victim.attacking ? 0
                    : actorIsAttacker ? attackerFate : defenderFate;
                int targetFate = victim.attacking ? attackerFate : defenderFate;
                var exchange = ResolveExchange(actor, target, ref offensiveFate, ref targetFate, rng, magnitudes);
                if (actorIsAttacker != victim.attacking)
                { if (actorIsAttacker) attackerFate = offensiveFate; else defenderFate = offensiveFate; }
                if (victim.attacking) attackerFate = targetFate; else defenderFate = targetFate;
                float hp = target.Hp;
                int attack = Mathf.RoundToInt(target.Attack), defense = Mathf.RoundToInt(target.Defense);
                BattleSimulationKernel.ApplyPrimaryOutcome(exchange, actor.Abilities, target.Abilities,
                    ref hp, ref attack, ref defense, magnitudes, out bool suppressed);
                target.Hp = hp; target.Attack = attack; target.Defense = defense;
                side[victim.index] = target;
                if (target.IsHero && hp <= 0)
                {
                    if (victim.attacking) TransferCommanderAfterHeroDeath(side, victim.index, ref attackerFate);
                    else TransferCommanderAfterHeroDeath(side, victim.index, ref defenderFate);
                }
                if (suppressed && !scratch.Acted.Contains(victim)) scratch.Suppressed.Add(victim);
            }
        }

        private static int SimulateOneBattle(List<BattleUnit> attackers, List<BattleUnit> defenders,
            System.Random rng, int attackerFate = 0, int defenderFate = 0,
            AbilityMagnitudes? magnitudesOverride = null, SimulationScratch scratch = null)
        {
            AbilityMagnitudes magnitudes = magnitudesOverride ?? AbilityMagnitudes.Default;
            scratch = scratch ?? new SimulationScratch(attackers.Count, defenders.Count);
            scratch.Reset(attackers, defenders);
            var order = scratch.Order;
            var acted = scratch.Acted;
            var suppressed = scratch.Suppressed;
            var livingTargets = scratch.LivingTargets;
            // Battle-constant skill presence: rosters never gain abilities mid-battle, so sides
            // without Bodyguard/Counterattack/Drill skip those checks on the hot path entirely.
            bool attackersGuard = AnyCarries(attackers, UnitAbilities.Bodyguard);
            bool defendersGuard = AnyCarries(defenders, UnitAbilities.Bodyguard);
            bool anyCounter = AnyCarries(attackers, UnitAbilities.Counterattack)
                || AnyCarries(defenders, UnitAbilities.Counterattack);
            bool attackersDrill = AnyCarries(attackers, UnitAbilities.Drill);
            bool defendersDrill = AnyCarries(defenders, UnitAbilities.Drill);
            for (int round = 0; round < MaxSimulatedRounds && AnyCombatantAlive(attackers) && AnyCombatantAlive(defenders); round++)
            {
                for (int i = 0; i < attackers.Count; i++)
                    if (attackers[i].IsHero) ApplyHealing(attackers, i, rng);
                for (int i = 0; i < defenders.Count; i++)
                    if (defenders[i].IsHero) ApplyHealing(defenders, i, rng);
                scratch.BodyguardsUsed.Clear();
                if (attackersDrill) ApplyRosterDrill(attackers, true, scratch.Drilled, magnitudes);
                if (defendersDrill) ApplyRosterDrill(defenders, false, scratch.Drilled, magnitudes);
                order.Clear();
                for (int i = 0; i < attackers.Count; i++) if (!attackers[i].IsHero) order.Add((true, i));
                for (int i = 0; i < defenders.Count; i++) if (!defenders[i].IsHero) order.Add((false, i));

                // Shuffle first (random Initiative tie-break, same reasoning BattleTurnOrder's own
                // class comment gives for why equal Initiative shouldn't always resolve the same
                // way), then a stable sort descending — matches the real turn-order rule exactly.
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                order.Sort(scratch.ByInitiativeDescending);

                acted.Clear();
                suppressed.Clear();
                foreach ((bool isAttacker, int index) turn in order)
                {
                    List<BattleUnit> ownList = turn.isAttacker ? attackers : defenders;
                    List<BattleUnit> enemyList = turn.isAttacker ? defenders : attackers;
                    BattleUnit actor = ownList[turn.index];
                    if (actor.Hp <= 0f)
                    {
                        acted.Add(turn);
                        continue; // killed earlier this same round — skips its turn, same as a real one would
                    }
                    if (suppressed.Contains(turn))
                    {
                        acted.Add(turn);
                        continue; // ShockAttack removed this not-yet-taken action from the round
                    }

                    ApplyHealing(ownList, turn.index, rng);
                    actor = ownList[turn.index];

                    // A hero stands in the back row and the fight ends when the non-hero bodies
                    // are gone, so an attack is only spent on a hero once no body of that side
                    // stands (playtest 2026-10-02: a lone Flamer + hero was rated 0.88 against two
                    // neutral units and lost four of four — random picks wasted half the enemy
                    // attacks on the hero).
                    livingTargets.Clear();
                    for (int i = 0; i < enemyList.Count; i++)
                        if (enemyList[i].Hp > 0f && !enemyList[i].IsHero)
                            livingTargets.Add(i);
                    if (livingTargets.Count == 0)
                        for (int i = 0; i < enemyList.Count; i++)
                            if (enemyList[i].Hp > 0f)
                                livingTargets.Add(i);
                    if (livingTargets.Count == 0)
                        break; // this side just ran out of targets mid-round — battle's over

                    if (ExplosionAttack.TargetLimit(actor.Abilities) > 0)
                    {
                        if (RosterExplosionValue(actor, ownList, enemyList, turn.index) > 0)
                        {
                            ApplyRosterExplosion(actor, attackers, defenders, turn.isAttacker, turn.index, rng, magnitudes,
                                scratch, ref attackerFate, ref defenderFate);
                            ConsumeRosterEquipment(ownList, turn.index);
                        }
                        acted.Add(turn);
                        continue;
                    }

                    int targetIndex = livingTargets[rng.Next(livingTargets.Count)];
                    BattleUnit target = enemyList[targetIndex];

                    BattleSimExchangeOutcome exchange = turn.isAttacker
                        ? ResolveExchange(actor, target, ref attackerFate, ref defenderFate, rng, magnitudes)
                        : ResolveExchange(actor, target, ref defenderFate, ref attackerFate, rng, magnitudes);

                    // UnitAbilities.Bodyguard — a landed hit goes to the hit side's guard, re-run
                    // against its own tags/armor; the guard is the victim from here on.
                    if (exchange.Damage > 0 && (turn.isAttacker ? defendersGuard : attackersGuard))
                    {
                        int guard = FindRosterBodyguard(enemyList, targetIndex, !turn.isAttacker,
                            scratch.BodyguardsUsed);
                        if (guard >= 0)
                        {
                            scratch.BodyguardsUsed.Add((!turn.isAttacker, guard));
                            int intercepted = BattleSimulationKernel.InterceptedDamage(
                                exchange.AttackerSuccesses - exchange.DefenderSuccesses, actor.Abilities,
                                enemyList[guard].TypeTags, enemyList[guard].Abilities, magnitudes);
                            exchange = new BattleSimExchangeOutcome(intercepted, intercepted > 0,
                                exchange.AttackerSuccesses, exchange.DefenderSuccesses);
                            targetIndex = guard;
                        }
                    }

                    bool suppressesTurn = ApplyRosterHit(enemyList, targetIndex, actor, exchange,
                        !turn.isAttacker, rng, magnitudes, scratch, ref attackerFate, ref defenderFate,
                        attackers, defenders);
                    if (suppressesTurn)
                    {
                        var targetTurn = (!turn.isAttacker, targetIndex);
                        if (!acted.Contains(targetTurn))
                            suppressed.Add(targetTurn);
                    }

                    ConsumeRosterEquipment(ownList, turn.index);

                    // UnitAbilities.Counterattack — a surviving victim answers a Range-1 (or
                    // unknown-range) attacker at once: never intercepted, never answered itself.
                    if (anyCounter && ownList[turn.index].Hp > 0f)
                    {
                        BattleUnit victim = enemyList[targetIndex];
                        if (BattleSimulationKernel.TriggersCounterattack(victim.Abilities, victim.IsHero,
                                victim.Hp, actor.Range <= 1,
                                exchange.Damage, actor.Abilities))
                        {
                            BattleUnit answered = ownList[turn.index];
                            BattleSimExchangeOutcome answer = turn.isAttacker
                                ? ResolveExchange(victim, answered, ref defenderFate, ref attackerFate, rng, magnitudes)
                                : ResolveExchange(victim, answered, ref attackerFate, ref defenderFate, rng, magnitudes);
                            ApplyRosterHit(ownList, turn.index, victim, answer, turn.isAttacker, rng, magnitudes,
                                scratch, ref attackerFate, ref defenderFate, attackers, defenders);
                            ConsumeRosterEquipment(enemyList, targetIndex);
                        }
                    }

                    acted.Add(turn);
                }
                RevertRosterDrill(attackers, defenders, scratch.Drilled, magnitudes);
            }

            bool attackersAlive = AnyCombatantAlive(attackers);
            bool defendersAlive = AnyCombatantAlive(defenders);
            if (attackersAlive && !defendersAlive) return 1;
            if (defendersAlive && !attackersAlive) return -1;
            return 0;
        }

        // Full-roster win fraction — MonteCarloTrials complete battles (SimulateOneBattle above),
        // fresh HP for every trial. `hexDefenseBonus` is folded into every defending unit's own
        // Defense once here (same as CanDamage's own `extraDefense`), never pre-baked by the
        // caller. Empty/null `enemyUnits` is a trivial
        // win (nothing known to fight — matches CanDamageAll's own "vacuously coverable" reading);
        // empty/null `attackerUnits` against a real enemy roster is a trivial loss.
        public static float WinChance(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus = 0f,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null) =>
            Estimate(attackerUnits, enemyUnits, hexDefenseBonus, attackerCommander, defenderCommander,
                magnitudes).WinChance;

        // Converts a real UnitData into the same per-combatant snapshot DefenderProfile carries for
        // a remembered/cheat-read enemy — used both for our own army (never behind fog of war) and
        // for a REAL, ground-truth-visible enemy roster no memory lookup is needed for (e.g. an
        // AirStrike loiter/repeat target the army is physically sitting on top of right now — see
        // AiAggressionPlanner.TryEnterLoiterAtTarget). Either way this always uses the unit's REAL
        // current HP (never HitPointsMax the way a remembered/fogged enemy has to). Public since
        // 2026-08-26 (air-strike scoring rework) for that second use — same single conversion, no
        // second copy of it anywhere else.
        public static DefenderProfile FromLiveUnit(UnitData unit) =>
            new DefenderProfile(unit.Defense, unit.HasAbility(UnitAbilities.CeramicArmor), unit.TypeTags.ToList(),
                unit.Attack, unit.HitPointsCurrent, unit.Initiative, unit.Abilities.ToList(),
                unit.HitPointsMax, unit.IsGroundCombatant, unit.IsHero, unit.FateMax, unit.IsSummoned,
                unit.Range, EquipmentSystem.Consumption(unit), ExplosionAttack.IsSingleUseUnit(unit),
                canDisruptBuilding: !unit.IsAviation && !unit.IsPrisoner);

        // Asset-free counterpart of EquipmentSystem.ConsumeAfterAttack for forecasts.
        public static DefenderProfile AfterAttack(DefenderProfile p)
        {
            if (p.SingleUseUnit)
                return new DefenderProfile(p.Defense, p.HasCeramicArmor, p.TypeTags, p.Attack, 0f,
                    p.Initiative, p.Abilities, p.MaxHitPoints, p.IsGroundCombatant, p.IsHero, p.FateMax,
                    p.IsSummoned, p.Range, p.Consumption, true, p.CanDisruptBuilding);
            EquipmentConsumption c = p.Consumption;
            if (c == null) return p;
            var abilities = c.ApplyAbilities(p.Abilities);
            float hp = c.RemainingHitPoints(p.HitPoints, p.MaxHitPoints, out float maxHp);
            return new DefenderProfile(p.Defense + c.Delta(EquipmentStat.Defense),
                abilities.Contains(UnitAbilities.CeramicArmor), p.TypeTags,
                p.Attack + c.Delta(EquipmentStat.Attack), hp,
                p.Initiative + c.Delta(EquipmentStat.Initiative), abilities, maxHp,
                p.IsGroundCombatant, p.IsHero, p.FateMax + c.Delta(EquipmentStat.Fate),
                p.IsSummoned, p.Range + c.Delta(EquipmentStat.Range), canDisruptBuilding: p.CanDisruptBuilding);
        }

        // Richer Monte Carlo readout added 2026-08-24 (project owner's own P1 plan, "WorthIt не
        // оценивает цену победы") alongside the bare win/lose verdict WinChance always returned —
        // a 95% WinChance can still mean the survivors limp home critically wounded, which the old
        // float couldn't say anything about. WinChance(ArmyData, ...) below is now a thin wrapper
        // over Estimate() so every existing pass/fail caller is unaffected.
        public readonly struct BattleEstimate
        {
            public readonly float WinChance;

            // Mean fraction of the attacking army's starting HP (sum across all non-hero members)
            // still standing at the end, averaged only over the trials the attacker actually won —
            // a losing trial says nothing about "surviving" a win that didn't happen.
            public readonly float ExpectedSurvivingHpRatioOnWin;

            // Fraction of WON trials where at least one surviving non-hero member ends the battle
            // at or below half its OWN real MaxHp — the exact predicate
            // RaidWeakerArmyTask.IsCriticallyWounded already applies to a real post-battle army, so
            // this number answers "how often does even a WIN immediately trigger that same
            // return-to-base-to-repair verdict".
            public readonly float CriticalAfterBattleChance;

            public BattleEstimate(float winChance, float expectedSurvivingHpRatioOnWin, float criticalAfterBattleChance)
            {
                WinChance = winChance;
                ExpectedSurvivingHpRatioOnWin = expectedSurvivingHpRatioOnWin;
                CriticalAfterBattleChance = criticalAfterBattleChance;
            }
        }

        // Same full-roster Monte Carlo WinChance(ArmyData, ...) always ran, just also tracking
        // what happens to OUR OWN side across the trials it wins (see BattleEstimate's own
        // comment). Observability only for now (2026-08-24 plan's own explicit scope) — nothing
        // gates on this yet, callers just log it. Seeded identically to the pre-existing
        // WinChance(ArmyData, ...) call (same FromLiveUnit-derived profile list feeds
        // BuildRosterSeed) so this change doesn't shift which battles a given call used to roll.
        // The attacker's commander is read off the live army itself (ArmyData.Commander).
        public static BattleEstimate Estimate(ArmyData attacker, IReadOnlyCollection<DefenderProfile> enemyUnits,
            float hexDefenseBonus = 0f, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null)
        {
            enemyUnits = WithBattleSummons(enemyUnits);
            if (!enemyUnits.Any(p => p.IsGroundCombatant))
                return new BattleEstimate(1f, 1f, 0f);

            SideCommander attackerCommander = SideCommander.Of(attacker?.Commander);
            List<BattleUnit> baseline = ToAttackerBattleUnits(attacker, attackerCommander.Initiative);
            var liveProfiles = TacticalTargetsOf(attacker.Members).Select(FromLiveUnit).ToList();
            List<DefenderProfile> liveWithSummons = WithBattleSummons(liveProfiles);
            if (liveWithSummons.Count > liveProfiles.Count)
                baseline.AddRange(ToBattleUnits(
                    liveWithSummons.Skip(liveProfiles.Count).ToList(), 0f, attackerCommander.Initiative));
            if (!baseline.Any(u => !u.IsHero))
                return new BattleEstimate(0f, 0f, 0f);

            // Seeded off the same FromLiveUnit-derived profile the live roster represents — real
            // MaxHp still comes from ToAttackerBattleUnits above (a wounded attacker's true max is
            // not recoverable from DefenderProfile.HitPoints, which only ever carries CURRENT hp).
            var seedProfiles = liveWithSummons;
            int seed = BuildRosterSeed(seedProfiles, enemyUnits, hexDefenseBonus,
                attackerCommander, defenderCommander);
            return EstimateCore(baseline, enemyUnits, hexDefenseBonus, seed,
                attackerCommander, defenderCommander, magnitudes);
        }

        // Roster-vs-roster overload (2026-09-14, Housekeeping contact-selection sync) — the same
        // full round-by-round Monte Carlo as the ArmyData overload above, for a virtual/projected
        // attacker that has no live ArmyData (e.g. an enemy DefenderProfile roster read off
        // AiMapMemory, or a Housekeeping virtual defender roster). Both overloads now share the one
        // EstimateCore loop below — no second Monte Carlo copy.
        public static BattleEstimate Estimate(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenderUnits, float hexDefenseBonus,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null)
        {
            attackerUnits = WithBattleSummons(attackerUnits);
            defenderUnits = WithBattleSummons(defenderUnits);
            if (!defenderUnits.Any(p => p.IsGroundCombatant))
                return new BattleEstimate(1f, 1f, 0f);

            if (!attackerUnits.Any(p => !p.IsHero))
                return new BattleEstimate(0f, 0f, 0f);

            int seed = BuildRosterSeed(attackerUnits, defenderUnits, hexDefenseBonus,
                attackerCommander, defenderCommander);
            return EstimateCore(attackerUnits, defenderUnits, hexDefenseBonus, seed,
                attackerCommander, defenderCommander, magnitudes);
        }

        // THE quick "how much fight is in this body" stat sum (Attack + Defense + HitPoints +
        // 0.25 × Initiative). Roster picks (strongest-first ordering, swap candidates, the
        // Monte-Carlo pre-filter) read it through AiPower.SkilledCombatValue, which adds the
        // skill bump. One formula, never re-typed at a call site.
        public static float CombatValue(float attack, float defense, float hitPoints, int initiative) =>
            attack + defense + hitPoints + 0.25f * initiative;

        // Every defending body of an opposition, in army order — the flat roster callers use for
        // counts, power and coverage. Built from the per-army list, never kept as a second source.
        public static List<DefenderProfile> UnitsOf(IEnumerable<DefendingArmy> armies)
        {
            var units = new List<DefenderProfile>();
            if (armies != null)
                foreach (DefendingArmy a in armies)
                    if (a.Units != null)
                        units.AddRange(a.Units);
            return units;
        }

        // One defending army of a multi-army hex: its fighting roster and its own commander.
        public readonly struct DefendingArmy
        {
            public readonly IReadOnlyCollection<DefenderProfile> Units;
            public readonly SideCommander Commander;
            // Exact terrain+structure bonus for THIS defending army when known. Null preserves the
            // legacy caller contract and falls back to EstimateSequential's shared hex bonus.
            public readonly float? DefenseBonusOverride;
            public readonly int? ArmyId;
            // The structural component of the total bonus, still present after known resident
            // Disruption. Kept separate so a projected incoming carrier removes only this part.
            public readonly float BuildingDefenseBonus;

            public DefendingArmy(IReadOnlyCollection<DefenderProfile> units, SideCommander commander,
                float? defenseBonusOverride = null, int? armyId = null, float buildingDefenseBonus = 0f)
            {
                Units = units ?? System.Array.Empty<DefenderProfile>();
                Commander = commander;
                DefenseBonusOverride = defenseBonusOverride;
                ArmyId = armyId;
                BuildingDefenseBonus = buildingDefenseBonus;
            }

            public float DefenseBonus(float fallback) => DefenseBonusOverride ?? fallback;
            public float DefenseBonus(float fallback, IReadOnlyCollection<DefenderProfile> attackers,
                SideCommander attackerCommander = default) =>
                DefenseBonus(fallback) - (DisruptionRules.HasCarrier(attackers)
                    || DisruptionRules.HasCarrier(attackerCommander.SupportHeroes)
                    ? Mathf.Max(0f, BuildingDefenseBonus) : 0f);
        }

        // Hero-only contacts have no tactical actors, but their Capture/Kill challenge is not a
        // guaranteed success. Use the canonical pools, Fate policy/order and outcome resolver.
        // Pure observed inputs, deterministic RNG and a separate exact cache mode; no live lookup.
        public static BattleEstimate EstimateCaptureKill(IReadOnlyCollection<DefenderProfile> attackers,
            SideCommander attackerCommander, IReadOnlyList<DefendingArmy> heroArmies)
        {
            int bodyCount = attackers?.Count(p => !p.IsHero && p.IsGroundCombatant) ?? 0;
            if (bodyCount == 0)
                return new BattleEstimate(0f, 0f, 0f);
            var targets = new List<(int pool, int fate)>();
            foreach (DefendingArmy army in heroArmies ?? System.Array.Empty<DefendingArmy>())
            {
                bool firstHero = true;
                foreach (DefenderProfile hero in army.Units.Where(p => p.IsHero))
                {
                    targets.Add((hero.FateMax, firstHero && army.Commander.Present
                        ? army.Commander.Fate : hero.FateMax));
                    firstHero = false;
                }
            }
            if (targets.Count == 0)
                return new BattleEstimate(1f, 1f, 0f);
            int hunterPool = BattleRules.CaptureKillHunterPool(bodyCount);
            int hunterFate = attackerCommander.Present ? attackerCommander.Fate : 0;
            int seed = unchecked(hunterPool * 397 + hunterFate);
            foreach (var target in targets)
                seed = unchecked((seed * 31 + target.pool) * 31 + target.fate);
            return CachedEstimate(3, seed, AbilityMagnitudes.Default,
                key =>
                {
                    key.Add(hunterPool); key.Add(hunterFate); key.Add(targets.Count);
                    foreach (var target in targets) { key.Add(target.pool); key.Add(target.fate); }
                },
                () =>
                {
                    var rng = new System.Random(seed);
                    int wins = 0;
                    for (int trial = 0; trial < MonteCarloTrials; trial++)
                    {
                        int fate = hunterFate;
                        bool won = true;
                        foreach (var target in targets)
                        {
                            int heroFate = target.fate;
                            if (BattleSimulationKernel.ResolveCaptureKill(hunterPool, target.pool,
                                    ref fate, ref heroFate, rng) == CaptureKillOutcome.Escaped)
                                won = false;
                        }
                        if (won) wins++;
                    }
                    return new BattleEstimate((float)wins / MonteCarloTrials, 1f, 0f);
                });
        }

        // A hex held by several armies is taken the way the real rules take it: one battle per
        // defending army, strongest defender first (BattleInitiator.FindEnemyAt — the army the
        // attacker is least likely to beat), the surviving attacker carrying its wounds into the
        // next battle (ResolveHexAfterVictory) while both sides' Fate refills for every battle
        // (ReplenishFateForNewBattle). The attack wins only by winning every battle; the hex
        // defence bonus applies to every defending army. A single army is exactly Estimate().
        public static BattleEstimate EstimateSequential(IReadOnlyCollection<DefenderProfile> attackerUnits,
            SideCommander attackerCommander, IReadOnlyList<DefendingArmy> defendingArmies,
            float hexDefenseBonus, AbilityMagnitudes? magnitudes = null)
        {
            BattleEstimate fights = EstimateSequentialFights(attackerUnits, attackerCommander,
                defendingArmies, hexDefenseBonus, magnitudes);
            // Hero-only armies are no battle, they are Capture/Kill challenges taken after the fights.
            // The opposition-based AI estimate only: the flat Estimate overloads stay as they are, since
            // BattleInitiator.FindEnemyAt ranks real defenders through them.
            // Judged after summons exactly like the fights filter, so a summoning hero is never both.
            List<DefendingArmy> heroOnly = (defendingArmies ?? System.Array.Empty<DefendingArmy>())
                .Where(a => a.Units != null && a.Units.Any(p => p.IsHero)
                    && !WithBattleSummons(a.Units).Any(p => p.IsGroundCombatant)).ToList();
            if (heroOnly.Count == 0)
                return fights;
            BattleEstimate capture = EstimateCaptureKill(attackerUnits, attackerCommander, heroOnly);
            return new BattleEstimate(fights.WinChance * capture.WinChance,
                fights.ExpectedSurvivingHpRatioOnWin, fights.CriticalAfterBattleChance);
        }

        private static BattleEstimate EstimateSequentialFights(IReadOnlyCollection<DefenderProfile> attackerUnits,
            SideCommander attackerCommander, IReadOnlyList<DefendingArmy> defendingArmies,
            float hexDefenseBonus, AbilityMagnitudes? magnitudes)
        {
            var armies = (defendingArmies ?? System.Array.Empty<DefendingArmy>())
                .Select(a => new DefendingArmy(WithBattleSummons(a.Units), a.Commander,
                    a.DefenseBonusOverride, a.ArmyId, a.BuildingDefenseBonus))
                .Where(a => a.Units.Any(p => p.IsGroundCombatant))
                .ToList();
            if (armies.Count <= 1)
                return armies.Count == 0
                    ? new BattleEstimate(1f, 1f, 0f)
                    : Estimate(attackerUnits, armies[0].Units,
                        armies[0].DefenseBonus(hexDefenseBonus, attackerUnits, attackerCommander), attackerCommander,
                        armies[0].Commander, magnitudes);

            attackerUnits = WithBattleSummons(attackerUnits);
            List<BattleUnit> baseline = ToBattleUnits(attackerUnits, 0f, attackerCommander.Initiative, attackerCommander);
            if (!baseline.Any(u => !u.IsHero))
                return new BattleEstimate(0f, 0f, 0f);

            // Strongest defender first, judged against the fresh attacker (stable for equal odds).
            List<DefendingArmy> order = armies
                .Select((a, i) => (a, i, win: Estimate(attackerUnits, a.Units,
                    a.DefenseBonus(hexDefenseBonus, attackerUnits, attackerCommander),
                    attackerCommander, a.Commander, magnitudes).WinChance))
                .OrderBy(x => x.win).ThenBy(x => x.i)
                .Select(x => x.a)
                .ToList();

            int seed = BuildRosterSeed(attackerUnits,
                order.SelectMany(a => a.Units).ToList(), hexDefenseBonus, attackerCommander, default);
            foreach (DefendingArmy a in order)
                if (a.Commander.Present)
                    seed = unchecked((seed * 31 + a.Commander.Initiative) * 31 + a.Commander.Fate);
            AbilityMagnitudes m = magnitudes ?? AbilityMagnitudes.Default;
            // Each defending army converted once; every trial fights a fresh copy (same values the
            // per-trial conversion produced).
            var defenderTemplates = order
                .Select(a => ToBattleUnits(a.Units, a.DefenseBonus(hexDefenseBonus), a.Commander.Initiative, a.Commander))
                .ToList();
            return CachedEstimate(2, seed, m,
                buf =>
                {
                    AppendKey(buf, attackerCommander);
                    AppendKey(buf, baseline);
                    buf.Add(order.Count);
                    for (int i = 0; i < order.Count; i++)
                    {
                        AppendKey(buf, order[i].Commander);
                        AppendKey(buf, defenderTemplates[i]);
                        buf.Add(System.BitConverter.SingleToInt32Bits(order[i].BuildingDefenseBonus));
                    }
                },
                () => SimulateSequential(baseline, order, defenderTemplates, seed, attackerCommander, m));
        }

        private static BattleEstimate SimulateSequential(List<BattleUnit> baseline,
            List<DefendingArmy> order, List<List<BattleUnit>> defenderTemplates, int seed,
            SideCommander attackerCommander, AbilityMagnitudes magnitudes)
        {
            var rng = new System.Random(seed);
            float startHp = baseline.Where(u => !u.IsHero).Sum(u => u.Hp);
            int wins = 0, draws = 0, criticalOnWin = 0;
            float survivingRatioSum = 0f;
            var attackers = new List<BattleUnit>(baseline.Count);
            var defenders = new List<BattleUnit>();
            var entryStats = new List<BattleUnit>(baseline.Count);
            var survivors = new List<BattleUnit>(baseline.Count);
            var survivorStats = new List<BattleUnit>(baseline.Count);
            var scratch = new SimulationScratch(baseline.Count,
                defenderTemplates.Count > 0 ? defenderTemplates.Max(d => d.Count) : 0);
            for (int t = 0; t < MonteCarloTrials; t++)
            {
                attackers.Clear(); attackers.AddRange(baseline);
                entryStats.Clear(); entryStats.AddRange(baseline);
                int result = 1;
                for (int ai = 0; ai < order.Count; ai++)
                {
                    DefendingArmy a = order[ai];
                    int commanderIndex = FirstLivingHeroIndex(attackers);
                    int refreshedAttackerFate = ai == 0 ? attackerCommander.Fate
                        : commanderIndex >= 0 ? attackers[commanderIndex].HeroFate : 0;
                    defenders.Clear(); defenders.AddRange(defenderTemplates[ai]);
                    // Recheck surviving sources between encounters, then freeze this fight's
                    // bonus even if its carrier dies in the first exchange.
                    if (a.BuildingDefenseBonus > 0f && attackers.Any(u => u.Hp > 0f
                        && !u.IsSummoned && u.CanDisruptBuilding && u.HasAbility(UnitAbilities.Disruption)))
                        for (int di = 0; di < defenders.Count; di++)
                        {
                            BattleUnit d = defenders[di];
                            d.Defense -= a.BuildingDefenseBonus;
                            defenders[di] = d;
                        }
                    result = SimulateOneBattle(attackers, defenders, rng,
                        refreshedAttackerFate, a.Commander.Fate, magnitudes, scratch);
                    if (result <= 0)
                        break;
                    // Wounds carry into the next battle; in-battle stat changes (Berserk) do not —
                    // the game reverts them when a battle ends (BattleScreenUI.RevertBerserkStacks).
                    survivors.Clear(); survivorStats.Clear();
                    for (int i = 0; i < attackers.Count; i++)
                    {
                        if (attackers[i].Hp <= 0f || attackers[i].IsSummoned)
                            continue;
                        BattleUnit u = attackers[i];
                        u.Attack = entryStats[i].Attack;
                        u.Defense = entryStats[i].Defense;
                        survivors.Add(u);
                        survivorStats.Add(entryStats[i]);
                    }
                    // Swap buffers, keeping the old lists for the next battle/trial.
                    var oldAttackers = attackers; attackers = survivors; survivors = oldAttackers;
                    var oldStats = entryStats; entryStats = survivorStats; survivorStats = oldStats;
                    if (ai + 1 < order.Count)
                    {
                        int nextCommanderIndex = FirstLivingHeroIndex(attackers);
                        int nextCommanderInitiative = nextCommanderIndex >= 0
                            ? attackers[nextCommanderIndex].Initiative : 0;
                        AppendFreshBattleSummons(attackers, nextCommanderInitiative);
                        entryStats.Clear(); entryStats.AddRange(attackers);
                    }
                }
                if (result > 0)
                {
                    wins++;
                    survivingRatioSum += startHp > 0f ? attackers.Where(u => !u.IsHero).Sum(u => Mathf.Max(0f, u.Hp)) / startHp : 0f;
                    if (attackers.Any(u => !u.IsHero && u.Hp > 0f && u.Hp <= u.MaxHp / 2f))
                        criticalOnWin++;
                }
                else if (result == 0) draws++;
            }
            return new BattleEstimate((wins + draws * 0.5f) / MonteCarloTrials,
                wins > 0 ? survivingRatioSum / wins : 0f,
                wins > 0 ? (float)criticalOnWin / wins : 0f);
        }

        // Shared Monte Carlo readout loop — `baseline` is the attacker's own BattleUnit snapshot
        // (already carrying whatever MaxHp fidelity its caller could offer), copied fresh every
        // trial; `defenderUnits`/`hexDefenseBonus` are rebuilt into BattleUnits per trial the same
        // way every existing caller here already expected.
        private static BattleEstimate EstimateCore(List<BattleUnit> baseline,
            IReadOnlyCollection<DefenderProfile> defenderUnits, float hexDefenseBonus, int seed,
            SideCommander attackerCommander, SideCommander defenderCommander,
            AbilityMagnitudes? magnitudes)
        {
            AbilityMagnitudes m = magnitudes ?? AbilityMagnitudes.Default;
            return CachedEstimate(1, seed, m,
                buf =>
                {
                    AppendKey(buf, attackerCommander);
                    AppendKey(buf, baseline);
                    AppendKey(buf, defenderCommander);
                    AppendKey(buf, defenderUnits, hexDefenseBonus, defenderCommander.Initiative);
                    // The simulated commander healing heroes also receive the hex bonus.
                    buf.Add(System.BitConverter.SingleToInt32Bits(hexDefenseBonus));
                },
                () => SimulateSingle(baseline,
                    ToBattleUnits(defenderUnits, hexDefenseBonus, defenderCommander.Initiative, defenderCommander), seed, attackerCommander,
                    defenderCommander, m));
        }

        private static BattleEstimate EstimateCore(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenderUnits, float hexDefenseBonus, int seed,
            SideCommander attackerCommander, SideCommander defenderCommander,
            AbilityMagnitudes? magnitudes)
        {
            AbilityMagnitudes m = magnitudes ?? AbilityMagnitudes.Default;
            return CachedEstimate(1, seed, m,
                buf =>
                {
                    AppendKey(buf, attackerCommander);
                    AppendKey(buf, attackerUnits, 0f, attackerCommander.Initiative);
                    AppendKey(buf, defenderCommander);
                    AppendKey(buf, defenderUnits, hexDefenseBonus, defenderCommander.Initiative);
                    // The simulated commander healing heroes also receive the hex bonus.
                    buf.Add(System.BitConverter.SingleToInt32Bits(hexDefenseBonus));
                },
                () => SimulateSingle(ToBattleUnits(attackerUnits, 0f, attackerCommander.Initiative, attackerCommander),
                    ToBattleUnits(defenderUnits, hexDefenseBonus, defenderCommander.Initiative, defenderCommander), seed,
                    attackerCommander, defenderCommander, m));
        }

        private static BattleEstimate SimulateSingle(List<BattleUnit> baseline,
            List<BattleUnit> defenderTemplate, int seed, SideCommander attackerCommander,
            SideCommander defenderCommander, AbilityMagnitudes magnitudes)
        {
            var rng = new System.Random(seed);
            float startHp = baseline.Sum(u => u.Hp);

            int wins = 0, draws = 0, criticalOnWin = 0;
            float survivingRatioSum = 0f;
            var attackers = new List<BattleUnit>(baseline.Count);
            var defenders = new List<BattleUnit>(defenderTemplate.Count);
            var scratch = new SimulationScratch(baseline.Count, defenderTemplate.Count);
            for (int i = 0; i < MonteCarloTrials; i++)
            {
                attackers.Clear(); attackers.AddRange(baseline);
                defenders.Clear(); defenders.AddRange(defenderTemplate);
                int result = SimulateOneBattle(attackers, defenders, rng,
                    attackerCommander.Fate, defenderCommander.Fate, magnitudes, scratch);
                if (result > 0)
                {
                    wins++;
                    survivingRatioSum += startHp > 0f ? attackers.Sum(u => Mathf.Max(0f, u.Hp)) / startHp : 0f;
                    if (attackers.Any(u => u.Hp > 0f && u.Hp <= u.MaxHp / 2f))
                        criticalOnWin++;
                }
                else if (result == 0) draws++;
            }

            float winChance = (wins + draws * 0.5f) / MonteCarloTrials;
            float survivingRatio = wins > 0 ? survivingRatioSum / wins : 0f;
            float criticalChance = wins > 0 ? (float)criticalOnWin / wins : 0f;
            return new BattleEstimate(winChance, survivingRatio, criticalChance);
        }

        // `attacker`'s own live non-hero roster as the same DefenderProfile snapshot shape —
        // ArmyData convenience overload of the full-roster WinChance above. Thin wrapper over
        // Estimate() (2026-08-24) — every pass/fail caller here keeps working unchanged.
        public static float WinChance(ArmyData attacker, IReadOnlyCollection<DefenderProfile> enemyUnits,
            float hexDefenseBonus = 0f, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null) =>
            Estimate(attacker, enemyUnits, hexDefenseBonus, defenderCommander, magnitudes).WinChance;

        // Per-combatant read for the coverage check below (CanDamage → HitProbability: the exact
        // tokens odds BattleCombatOdds gives a real attack pick, through the canonical ability
        // modifier chain on Attack, Abilities, Defense and TypeTags).
        //
        // TypeTags (added 2026-08-19, project owner's own call) — also lets
        // AiMapMemory remember a scouted enemy's own Bio/Armored/Mechanical classification
        // alongside its Defense, honestly (only ever set from an actually-observed sighting or
        // guard — see AiMapMemory's own "Видимость с памятью" principle) — AiManagementPlanner's
        // own counter-tech card scoring (Hyperkinetic vs known Armored, Pyrokinetic vs known Bio)
        // reads the tags off the sighting. Never null — empty when the source had no tags (or wasn't a real UnitData/
        // CardDefinition read at all).
        //
        // Attack/HitPoints/Initiative (added 2026-08-22, project owner's own call: "если мы
        // когда-то видели армию значит мы видели её состав и знаем всё о ней кроме текущего
        // состояния") — the full per-combatant snapshot SimulateOneBattle needs (see WinChance's
        // own DefenderProfile-list overload above) to actually play a real round-by-round fight
        // instead of one flat aggregate exchange. HitPoints is the unit's CURRENT HP as of its own
        // last observation (AiMapMemory.OnVisibilityChanged, 2026-08-26 air-strike-memory fix —
        // was MAX HP, "assume it's healed up since we last saw it"; repair and regeneration
        // may change HP after observation, but must not grant live reads through fog. A remembered
        // sighting now freezes damage the same honest way it already freezes composition,
        // corrected only by a later re-observation). A live/cheat source
        // (AiDefencePlanner.CheatEstimateRaiderThreat) already reads the real ArmyData's own
        // current HP the same way. This struct is now
        // used symmetrically for BOTH sides of a fight (see WinChance's own DefenderProfile-list
        // overload below) — "Defender" is a legacy name from when it only ever described the
        // other side.
        public readonly struct DefenderProfile : System.IEquatable<DefenderProfile>
        {
            public readonly bool SingleUseUnit;
            // Abstract/card profiles default to ground. Live snapshots explicitly exclude
            // aircraft and prisoners, preserving that eligibility across forecasts and caches.
            public readonly bool CanDisruptBuilding;
            public readonly EquipmentConsumption Consumption;
            public readonly float Defense;
            public readonly bool HasCeramicArmor;
            public readonly IReadOnlyList<UnitTypeTag> TypeTags;
            public readonly float Attack;
            public readonly float HitPoints;
            // True maximum HP when the source is live. Older/remembered profile producers that
            // only know current HP may omit it; the constructor then conservatively falls back to
            // HitPoints. The full BattleEstimate tie-break needs this distinction for wounded
            // attackers, otherwise CriticalAfterBattleChance diverges from the live ArmyData path.
            public readonly float MaxHitPoints;
            public readonly int Initiative;
            public readonly IReadOnlyList<string> Abilities;
            // UnitData.IsGroundCombatant carried into the profile (FromLiveUnit), so a profile
            // roster keeps the one "fights in a ground battle" fact after conversion. Producers that
            // only ever describe fighting bodies (remembered enemies, cards, projections) leave the
            // default true. Every WorthIt estimator filters on it — see CombatantsOf.
            public readonly bool IsGroundCombatant;
            public readonly bool IsHero;
            public readonly int FateMax;
            public readonly bool IsSummoned;
            // Attack range in hexes when the producer knows it; -1 = unknown. The combat simulation never
            // reads it (WorthIt fights on a fixed geometry); equipment valuation reads it to tell whether the
            // enemy can ANSWER at a contact distance. Unknown is treated as "answers at any distance".
            public readonly int Range;

            public DefenderProfile(float defense, bool hasCeramicArmor, IReadOnlyList<UnitTypeTag> typeTags = null,
                float attack = 0f, float hitPoints = 0f, int initiative = 0,
                IReadOnlyList<string> abilities = null, float maxHitPoints = 0f,
                bool isGroundCombatant = true, bool isHero = false, int fateMax = 0, bool isSummoned = false,
                int range = -1, EquipmentConsumption consumption = null, bool singleUseUnit = false,
                bool canDisruptBuilding = true)
            {
                SingleUseUnit = singleUseUnit;
                CanDisruptBuilding = canDisruptBuilding;
                Consumption = consumption;
                Range = range;
                IsGroundCombatant = isGroundCombatant;
                IsSummoned = isSummoned;
                IsHero = isHero;
                FateMax = Mathf.Max(0, fateMax);
                Defense = isHero ? Mathf.Max(0, fateMax) : defense;
                HasCeramicArmor = hasCeramicArmor;
                // Profiles cross observation and estimate-cache boundaries. Own the nested lists
                // so a readonly profile cannot retain mutable live/caller state.
                TypeTags = System.Array.AsReadOnly((typeTags ?? System.Array.Empty<UnitTypeTag>()).ToArray());
                Attack = attack;
                HitPoints = hitPoints;
                MaxHitPoints = maxHitPoints > 0f ? maxHitPoints : hitPoints;
                Initiative = initiative;
                Abilities = System.Array.AsReadOnly((abilities ?? (hasCeramicArmor
                    ? (IReadOnlyList<string>)new[] { UnitAbilities.CeramicArmor }
                    : System.Array.Empty<string>())).ToArray());
            }

            // The same body in another combat state: exactly the constructor called with this
            // profile's own fields and new HP / Attack / Defense, but sharing the lists this
            // profile already owns (they are frozen) - forecasts derive thousands of such states.
            public DefenderProfile WithState(float hitPoints, float attack, float defense) =>
                new DefenderProfile(this, hitPoints, attack, defense);

            private DefenderProfile(DefenderProfile p, float hitPoints, float attack, float defense)
            {
                SingleUseUnit = p.SingleUseUnit;
                CanDisruptBuilding = p.CanDisruptBuilding;
                Consumption = p.Consumption;
                Range = p.Range;
                IsGroundCombatant = p.IsGroundCombatant;
                IsSummoned = p.IsSummoned;
                IsHero = p.IsHero;
                FateMax = p.FateMax;
                Defense = p.IsHero ? p.FateMax : defense;
                HasCeramicArmor = p.HasCeramicArmor;
                TypeTags = p.TypeTags ?? System.Array.AsReadOnly(System.Array.Empty<UnitTypeTag>());
                Attack = attack;
                HitPoints = hitPoints;
                MaxHitPoints = p.MaxHitPoints > 0f ? p.MaxHitPoints : hitPoints;
                Initiative = p.Initiative;
                Abilities = p.Abilities ?? System.Array.AsReadOnly(System.Array.Empty<string>());
            }

            // Exactly the default struct equality (every field by its own Equals; the owned lists
            // therefore by identity), without ValueType.Equals' reflection and boxing - equipment
            // valuation looks a profile up in its battle on every Splash/Scorcher evaluation.
            public bool Equals(DefenderProfile other) =>
                Defense.Equals(other.Defense) && HasCeramicArmor == other.HasCeramicArmor
                && Equals(TypeTags, other.TypeTags) && Attack.Equals(other.Attack)
                && HitPoints.Equals(other.HitPoints) && MaxHitPoints.Equals(other.MaxHitPoints)
                && Initiative == other.Initiative && Equals(Abilities, other.Abilities)
                && IsGroundCombatant == other.IsGroundCombatant && IsHero == other.IsHero
                && FateMax == other.FateMax && IsSummoned == other.IsSummoned && Range == other.Range
                && Consumption?.Key == other.Consumption?.Key && SingleUseUnit == other.SingleUseUnit
                && CanDisruptBuilding == other.CanDisruptBuilding;

            // The same observed body, lists compared by content: the rule of every AI change
            // detector (remembered sightings, snapshot actors). A profile rebuilt from an unchanged
            // unit compares equal; any field a battle or a valuation reads tells it apart. Keep it in
            // step with the field list above.
            public bool SameContent(DefenderProfile o) =>
                Defense.Equals(o.Defense) && HasCeramicArmor == o.HasCeramicArmor
                && Attack.Equals(o.Attack) && HitPoints.Equals(o.HitPoints)
                && MaxHitPoints.Equals(o.MaxHitPoints) && Initiative == o.Initiative
                && IsGroundCombatant == o.IsGroundCombatant && IsHero == o.IsHero
                && FateMax == o.FateMax && IsSummoned == o.IsSummoned && Range == o.Range
                && Consumption?.Key == o.Consumption?.Key && SingleUseUnit == o.SingleUseUnit
                && CanDisruptBuilding == o.CanDisruptBuilding
                && (TypeTags ?? System.Array.Empty<UnitTypeTag>())
                    .SequenceEqual(o.TypeTags ?? System.Array.Empty<UnitTypeTag>())
                && (Abilities ?? System.Array.Empty<string>())
                    .SequenceEqual(o.Abilities ?? System.Array.Empty<string>());

            public static bool SameContent(IReadOnlyList<DefenderProfile> a, IReadOnlyList<DefenderProfile> b)
            {
                int count = a?.Count ?? 0;
                if (count != (b?.Count ?? 0)) return false;
                for (int i = 0; i < count; i++)
                    if (!a[i].SameContent(b[i])) return false;
                return true;
            }

            public override bool Equals(object obj) => obj is DefenderProfile other && Equals(other);

            public override int GetHashCode() =>
                (Defense, Attack, HitPoints, Initiative, Range, FateMax).GetHashCode();
        }

        // 2026-10-19 (project owner's decision) — "can damage" is the share of one exchange's tokens
        // outcomes that wound the defender (HitProbability), at least CoverageMinHitProbability. It
        // replaced the rounded mean `attack*0.5 - defense*0.5`, which live combat had already left
        // (BattleCombatOdds): a Medium Tank (5) against Null Vessel (3) on a Citadel (+2) read as
        // "cannot scratch", the threat model priced the Citadel's PotentialDamage at 0 while the
        // same estimate gave the attack a win of 1.00, and the tank column took it next turn.
        //
        // `extraDefense` — the hex's own terrain/Base-building bonus (HexDefenseBonus), added
        // 2026-08-20 (project owner's own report). DefenderProfile.Defense is always the
        // defender's own raw stat as memorized, never including where the fight would actually
        // happen — every OTHER read in this file that compares against a real hex (Score's own enemyDefense
        // parameter) already folds this in; this coverage check used to
        // be the one exception, which could read a defender as damageable off its raw stat alone
        // while the hex bonus on top would actually make it un-killable. Defaults to 0f so a
        // caller with no hex to check against (a pure stat comparison) is unaffected.
        //
        // Public since 2026-08-23 (project owner's own report) — AiManagementPlanner's own
        // UnitCompositionFitBonus needs this exact per-unit check to ask a hypothetical "would
        // THIS candidate card cover a defender none of a StillAssembling raid's current roster
        // can already damage", the same single-source-of-truth rule every other coverage read in
        // this codebase already follows (see CanDamageAll's own comment) — no second copy of this
        // formula anywhere else.
        public static bool CanDamage(float attack, DefenderProfile defender, float extraDefense = 0f,
            AbilityMagnitudes? magnitudesOverride = null) =>
            CanDamage(new DefenderProfile(0f, false, attack: attack, hitPoints: 1f), defender,
                extraDefense, magnitudesOverride);

        // A defender wounded in fewer than this share of exchanges is a brick the roster cannot
        // wear down within a battle (the coverage gate's meaning). Per exchange, pre-Fate:
        // 5 tokens vs 5 = 0.38, 4 vs 6 = 0.17, 3 vs 6 = 0.09, 3 vs 9 = 0.02.
        public const float CoverageMinHitProbability = 0.10f;

        // Composition-aware penetration check for immutable rosters.
        public static bool CanDamage(DefenderProfile attacker, DefenderProfile defender,
            float extraDefense = 0f, AbilityMagnitudes? magnitudesOverride = null) =>
            HitProbability(attacker, defender, extraDefense, magnitudesOverride)
                >= CoverageMinHitProbability;

        // The chance one exchange wounds `defender`: attack tokens vs defence tokens (+ hex bonus),
        // 50% per die, success-count subtraction, then the canonical ability modifier chain — the
        // same exact pre-Fate distribution BattleCombatOdds gives live target selection.
        public static float HitProbability(DefenderProfile attacker, DefenderProfile defender,
            float extraDefense = 0f, AbilityMagnitudes? magnitudesOverride = null)
        {
            AbilityMagnitudes magnitudes = magnitudesOverride ?? AbilityMagnitudes.Default;
            int attackTokens = Mathf.Max(0, Mathf.RoundToInt(
                ExplosionAttack.AttackPower(attacker.Attack, attacker.Abilities)));
            int defenseTokens = Mathf.Max(0, Mathf.RoundToInt(defender.Defense + extraDefense));
            double[] pa = BinomialHalf(attackTokens), pd = BinomialHalf(defenseTokens);
            double hit = 0d, positive = 0d;
            for (int raw = 1; raw <= attackTokens; raw++)
            {
                double p = 0d;
                for (int d = 0; d <= defenseTokens && d + raw <= attackTokens; d++)
                    p += pd[d] * pa[d + raw];
                positive += p;
                if (ChallengeResult.ApplyAbilityModifiers(raw, attacker.Abilities,
                        defender.TypeTags, defender.Abilities, magnitudes) > 0)
                    hit += p;
            }
            // Every other outcome is a raw 0; a modifier may still turn it into a wound.
            if (ChallengeResult.ApplyAbilityModifiers(0, attacker.Abilities,
                    defender.TypeTags, defender.Abilities, magnitudes) > 0)
                hit += 1d - positive;
            return (float)hit;
        }

        private static readonly Dictionary<int, double[]> BinomialHalfCache = new Dictionary<int, double[]>();

        // P(k successes of n tokens), each die 50%.
        private static double[] BinomialHalf(int n)
        {
            if (BinomialHalfCache.TryGetValue(n, out double[] cached))
                return cached;
            var p = new double[n + 1];
            double c = 1d, scale = System.Math.Pow(0.5d, n);
            for (int k = 0; k <= n; k++)
            {
                p[k] = c * scale;
                c = c * (n - k) / (k + 1);
            }
            if (n <= 256)
                BinomialHalfCache[n] = p;
            return p;
        }

        public static bool CanDamageAll(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenders, float extraDefense = 0f,
            AbilityMagnitudes? magnitudes = null)
        {
            defenders = CombatantsOf(defenders);
            if (defenders.Count == 0)
                return true;
            attackerUnits = CombatantsOf(attackerUnits);
            if (attackerUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!attackerUnits.Any(attacker => CanDamage(attacker, defender, extraDefense, magnitudes)))
                    return false;
            return true;
        }

        // Coverage gate on top of Score's overall power read: every known enemy unit needs at
        // least ONE counter somewhere in `attacker`'s own roster. A lopsided net edge doesn't mean
        // much if one specific enemy unit is a brick wall none of our units can actually dent — it
        // just tanks forever while the rest of the fight plays out around it, dragging the
        // exchange out far longer than Score's own single-number read suggests. Null/empty
        // `defenders` (no guard, or a data source that has no per-unit read at all) is vacuously
        // coverable — nothing to fail to cover.
        public static bool CanDamageAll(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyList<DefendingArmy> opposition, float fallbackDefenseBonus = 0f,
            AbilityMagnitudes? magnitudes = null, SideCommander attackerCommander = default) =>
            UncoveredDefenderCount(attackerUnits, opposition, fallbackDefenseBonus, magnitudes, attackerCommander) == 0;

        // How many known defending combatants no unit of `attackerUnits` can damage — the same
        // coverage rule as CanDamageAll, counted, so a planner can tell whether a candidate
        // reinforcement closes any of the gap (2026-10-17: a host past the power bar was fed
        // power-only supports for six turns while one defender stayed uncovered).
        public static int UncoveredDefenderCount(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyList<DefendingArmy> opposition, float fallbackDefenseBonus = 0f,
            AbilityMagnitudes? magnitudes = null, SideCommander attackerCommander = default)
        {
            if (opposition == null || opposition.Count == 0)
                return 0;
            List<DefenderProfile> attackers = CombatantsOf(attackerUnits);
            int uncovered = 0;
            foreach (DefendingArmy army in opposition)
            {
                float extra = army.DefenseBonus(fallbackDefenseBonus, attackerUnits, attackerCommander);
                foreach (DefenderProfile defender in CombatantsOf(army.Units))
                    if (!attackers.Any(attacker => CanDamage(attacker, defender, extra, magnitudes)))
                        uncovered++;
            }
            return uncovered;
        }

        public static bool CanDamageAll(ArmyData attacker, IReadOnlyCollection<DefenderProfile> defenders,
            float extraDefense = 0f, AbilityMagnitudes? magnitudes = null) =>
            CanDamageAll(attacker?.Members, defenders, extraDefense, magnitudes);

        // Same coverage gate, against a raw unit set instead of a real ArmyData — the shared
        // building block the ArmyData overload above delegates to.
        public static bool CanDamageAll(IEnumerable<UnitData> attackerUnits, IReadOnlyCollection<DefenderProfile> defenders,
            float extraDefense = 0f, AbilityMagnitudes? magnitudes = null)
        {
            defenders = CombatantsOf(defenders);
            if (defenders.Count == 0)
                return true;
            List<UnitData> ourUnits = CombatantsOf(attackerUnits);
            if (ourUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!ourUnits.Any(u => CanDamage(ExplosionAttack.AttackPower(u.Attack, u.Abilities), defender, extraDefense, magnitudes)))
                    return false;
            return true;
        }

        // The hex's own contribution alone, no army — terrain.defenseModifier (see
        // TerrainTypeEntry's own comment: added to the defender's tokens pool only, in every real
        // fight) plus a Base building's own Defense stat if one sits here (see
        // BuildingData.IsBase — only Base buildings carry this, per BattingScreenUI.Combat.cs's
        // own gate). Needed on its own for a Hex Event's card-stat guard (AiMapMemory.
        // KnownEventGuardStrengthAt) — that guard is never a live ArmyData sitting on the hex until
        // Explore is chosen, so there's no army to hand over, only the hex's own bonus to add
        // on top of the guard's own card total.
        public static float HexDefenseBonus(HexCoord hex, HexMap map)
        {
            float bonus = 0f;
            if (map != null && map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain) && terrain != null)
                bonus += terrain.defenseModifier;

            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null && building.IsBase)
                bonus += building.Defense;

            return bonus;
        }
    }
}
