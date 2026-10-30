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
    // Estimate inputs: one combatant (DefenderProfile), a side's commander (SideCommander) and one
    // defending army of a contested hex (DefendingArmy).
    public static partial class WorthIt
    {
        // A side's battle commander (ArmyData.Commander — the army's first hero). Heroes never
        // fight, but the commander's Initiative is added to every combatant of its side
        // (BattleTurnOrder) and its Fate buys rerolls in every exchange (BattleAttackPopupUI's
        // duel). Live reads retain current Fate: a preceding standalone air strike can spend it.
        // Completed ground encounters replenish Fate; card-only projections start full.
        public readonly struct SideCommander : System.IEquatable<SideCommander>
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal SideCommander(bool __matchSave, bool __Present, int __Initiative, int __Fate, global::System.Collections.Generic.IReadOnlyList<global::Game.Combat.WorthIt.DefenderProfile> __SupportHeroes)
        {
            Present = __Present;
            Initiative = __Initiative;
            Fate = __Fate;
            SupportHeroes = __SupportHeroes;
        }

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
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal DefenderProfile(bool __matchSave, bool __SingleUseUnit, bool __CanDisruptBuilding, global::Game.Cards.EquipmentConsumption __Consumption, float __Defense, bool __HasCeramicArmor, global::System.Collections.Generic.IReadOnlyList<global::Game.Cards.UnitTypeTag> __TypeTags, float __Attack, float __HitPoints, float __MaxHitPoints, int __Initiative, global::System.Collections.Generic.IReadOnlyList<string> __Abilities, bool __IsGroundCombatant, bool __IsHero, int __FateMax, bool __IsSummoned, int __Range)
        {
            SingleUseUnit = __SingleUseUnit;
            CanDisruptBuilding = __CanDisruptBuilding;
            Consumption = __Consumption;
            Defense = __Defense;
            HasCeramicArmor = __HasCeramicArmor;
            TypeTags = __TypeTags;
            Attack = __Attack;
            HitPoints = __HitPoints;
            MaxHitPoints = __MaxHitPoints;
            Initiative = __Initiative;
            Abilities = __Abilities;
            IsGroundCombatant = __IsGroundCombatant;
            IsHero = __IsHero;
            FateMax = __FateMax;
            IsSummoned = __IsSummoned;
            Range = __Range;
        }

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
    }
}
