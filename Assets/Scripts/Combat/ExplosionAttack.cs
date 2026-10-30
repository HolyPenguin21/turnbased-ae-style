using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // One launched attack, not N attacks. Its immutable offensive snapshot survives the death
    // of the launcher; equipment and an innate SingleUse unit are spent only at completion.
    public sealed class ExplosionAttack
    {
        public const int AttackTokens = 10;
        public static float AttackPower(float ordinaryAttack, IEnumerable<string> abilities)
            => TargetLimit(abilities) > 0 ? AttackTokens : ordinaryAttack;
        public static int TargetLimit(IEnumerable<string> abilities)
        {
            int limit = 0;
            foreach (string ability in abilities ?? Array.Empty<string>())
                if (ability == UnitAbilities.Explosion10) limit = 10;
                else if (ability == UnitAbilities.Explosion5) limit = Math.Max(limit, 5);
                else if (ability == UnitAbilities.Explosion3) limit = Math.Max(limit, 3);
            return limit;
        }

        public static bool IsSingleUseUnit(CardDefinition card) => card != null && card.cardType == CardType.Unit
            && card.grantedAbilities?.Contains(UnitAbilities.SingleUse) == true;

        public static bool IsSingleUseUnit(UnitData unit) => unit != null && !unit.IsHero
            && (unit.OriginatingCard != null
                ? IsSingleUseUnit(unit.OriginatingCard)
                : unit.AttachmentBase.HasValue
                    ? unit.AttachmentBase.Value.Abilities.Contains(UnitAbilities.SingleUse)
                    : unit.Equipment == null && unit.Mutator == null && unit.HasAbility(UnitAbilities.SingleUse));

        // Equal inclusion probability gives the exact expected sum over a uniform subset.
        // Only supplied/known combatants are read: planning never queries hidden live rosters.
        public static float ExpectedNetValue(UnitData actor, IEnumerable<UnitData> targets,
            Func<UnitData, float> hitPoints = null, Func<UnitData, int> defenseBonus = null)
        {
            bool expendUnit = IsSingleUseUnit(actor);
            var pool = targets.Where(t => t != null && (!expendUnit || t != actor)
                && (hitPoints?.Invoke(t) ?? t.HitPointsCurrent) > 0).Distinct().ToList();
            if (pool.Count == 0) return 0;
            float value = 0;
            foreach (UnitData victim in pool)
            {
                int hp = Mathf.Max(1, Mathf.CeilToInt(hitPoints?.Invoke(victim) ?? victim.HitPointsCurrent));
                int defense = (victim.IsHero ? victim.FateMax : victim.Defense) + (defenseBonus?.Invoke(victim) ?? 0);
                float bodyValue = WorthIt.CombatValue(victim.Attack, victim.Defense,
                    victim.HitPointsMax, victim.Initiative);
                value += (victim.Owner == actor.Owner ? -1f : 1f) * ExpectedDamageValue(actor.Abilities,
                    defense, victim.TypeTags, victim.Abilities, hp, victim.HitPointsMax, bodyValue);
            }
            return NetValue(value, pool.Count, TargetLimit(actor.Abilities), expendUnit
                ? WorthIt.CombatValue(actor.Attack, actor.Defense, actor.HitPointsCurrent, actor.Initiative) : 0f);
        }

        // The caller supplies an eligible observed pool, never reconstructed foreign live units.
        internal static float ExpectedNetValue(WorthIt.DefenderProfile actor,
            IEnumerable<AviationCombatEstimator.ExplosionCollateral> targets)
        {
            float value = 0f;
            int count = 0;
            foreach (var target in targets)
            {
                var victim = target.Profile;
                if (victim.HitPoints <= 0) continue;
                if (target.OwnCitadel && ChallengeResult.ApplyAbilityModifiers(AttackTokens,
                    actor.Abilities, victim.TypeTags, victim.Abilities, AbilityMagnitudes.Default) >= victim.HitPoints)
                    return float.NegativeInfinity;
                count++;
                value += (target.Friendly ? -1f : 1f) * ExpectedDamageValue(actor.Abilities,
                    victim.IsHero ? victim.FateMax : Mathf.RoundToInt(victim.Defense), victim.TypeTags,
                    victim.Abilities, victim.HitPoints, victim.MaxHitPoints,
                    WorthIt.CombatValue(victim.Attack, victim.Defense, victim.MaxHitPoints, victim.Initiative));
            }
            return NetValue(value, count, TargetLimit(actor.Abilities), actor.SingleUseUnit
                ? WorthIt.CombatValue(actor.Attack, actor.Defense, actor.HitPoints, actor.Initiative) : 0f);
        }

        internal static float NetValue(float signedDamageValue, int count, int limit, float sacrifice) =>
            count == 0 ? 0f : signedDamageValue * Mathf.Min(limit, count) / count - sacrifice;

        internal static float ExpectedDamageValue(IEnumerable<string> attackerAbilities, int defense,
            IReadOnlyCollection<UnitTypeTag> tags, IEnumerable<string> defenderAbilities,
            float hp, float maxHp, float bodyValue)
        {
            float damage = BattleSimulationKernel.ExpectedExchangeDamage(AttackTokens, defense,
                attackerAbilities, tags, defenderAbilities, Mathf.Max(1, Mathf.CeilToInt(hp)), out _);
            return damage / Mathf.Max(1, maxHp) * bodyValue;
        }

        public static bool RisksOwnCitadel(UnitData actor, IEnumerable<Target> targets) => targets.Any(t =>
            t.Facility == null && t.Building != null && t.Building.IsStartingCitadel && t.Building.Owner == actor.Owner
            && ChallengeResult.ApplyAbilityModifiers(AttackTokens, actor, t.Combatant(), AbilityMagnitudes.Default)
                >= t.Building.StructurePointsCurrent);

        public static float ExpectedNetValue(UnitData actor, IEnumerable<Target> targets)
        {
            var pool = targets.Where(t => !actor.IsAviation || t.Unit == null
                || AviationRules.CanTargetFromAir(t.Unit, t.Army)).ToArray();
            return RisksOwnCitadel(actor, pool) ? float.NegativeInfinity
                : ExpectedNetValue(actor, pool.Select(t => t.Combatant()));
        }

        public sealed class Target
        {
            public UnitData Unit { get; }
            public ArmyData Army { get; }
            public BuildingData Building { get; }
            public FacilityData Facility { get; }
            public int Slot { get; }
            public bool IsStructure => Building != null;
            public bool IsAlive => Unit != null ? Unit.HitPointsCurrent > 0 && Army.Members.Contains(Unit)
                : ReferenceEquals(BuildingRegistry.FindAt(Building.Hex), Building)
                    && (Facility == null ? Building.StructurePointsCurrent > 0
                        : ReferenceEquals(Building.FacilitySlots[Slot], Facility) && Facility.StructurePointsCurrent > 0);
            internal Target(UnitData unit, ArmyData army) { Unit = unit; Army = army; }
            internal Target(BuildingData building, FacilityData facility = null, int slot = -1)
            { Building = building; Facility = facility; Slot = slot; }

            // The existing challenge shell takes a combat profile. A structure adapter owns no
            // live unit identity and writes only structure HP/Fate back after its challenge.
            public UnitData Combatant()
            {
                if (Unit != null) return Unit;
                var profile = UnitData.CreateProjection();
                profile.Name = Facility?.Name ?? Building.Name;
                profile.Owner = Building.Owner;
                profile.Art = Facility?.Art ?? Building.Art;
                profile.DetailArt = Facility?.DetailArt ?? Building.DetailArt;
                profile.Defense = Facility?.Defense ?? Building.Defense;
                profile.HitPointsCurrent = Facility?.StructurePointsCurrent ?? Building.StructurePointsCurrent;
                profile.HitPointsMax = Facility?.StructurePointsMax ?? Building.StructurePointsMax;
                profile.Fate = profile.FateMax = Facility?.Fate ?? Building.Fate;
                profile.Abilities.UnionWith(Facility != null ? Facility.Abilities : Building.Abilities);
                return profile;
            }
            public UnitData Hero => Unit != null
                ? (Unit.IsHero ? Unit : Army.Members.Find(u => u.IsHero && u.HitPointsCurrent > 0)) : null;
        }

        public UnitData Launcher { get; }
        public ArmyData LauncherArmy => _launcherArmy;
        public UnitData Actor { get; }
        public HexCoord Hex { get; }
        public IReadOnlyList<Target> Targets { get; }
        public int ResolvedCount { get; private set; }
        private bool _completed;
        private readonly BattleEngine _battle;
        internal bool IsCompleted => _completed;
        private readonly ArmyData _launcherArmy;

        internal ExplosionAttack(BattleEngine battle, UnitData launcher, HexCoord hex, System.Random rng,
            params ArmyData[] participatingArmies) : this(launcher, hex, rng, participatingArmies)
        { _battle = battle; }

        private BattleEngine OwningBattle(BattleEngine supplied)
        {
            if (_battle != null && supplied != null && !ReferenceEquals(_battle, supplied))
                throw new InvalidOperationException("The explosion belongs to another battle.");
            return _battle ?? supplied;
        }

        public ExplosionAttack(UnitData launcher, HexCoord hex, System.Random rng,
            params ArmyData[] participatingArmies)
        {
            Launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            Hex = hex;
            Actor = launcher.CloneForCombat();
            Actor.Attack = AttackTokens;
            _launcherArmy = (participatingArmies ?? Array.Empty<ArmyData>())
                .FirstOrDefault(a => a != null && a.Members.Contains(launcher)) ?? ArmyRegistry.FindArmyContaining(launcher);
            var pool = CollectTargets(hex, participatingArmies);
            if (launcher.IsAviation) pool.RemoveAll(t => t.Unit != null
                && !AviationRules.CanTargetFromAir(t.Unit, t.Army));
            if (IsSingleUseUnit(launcher)) pool.RemoveAll(t => t.Unit == launcher);
            int count = Math.Min(TargetLimit(launcher.Abilities), pool.Count);
            // Partial Fisher-Yates: every distinct occupant has the same inclusion chance.
            for (int i = 0; i < count; i++)
            {
                int j = rng.Next(i, pool.Count);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            Targets = pool.Take(count).ToArray();
        }

        public static List<Target> CollectTargets(HexCoord hex, params ArmyData[] participatingArmies)
        {
            var result = new List<Target>();
            var seen = new HashSet<UnitData>();
            var stationed = ArmyRegistry.AllAt(hex)
                .Where(a => a.Controller == null || !a.Controller.IsMoving || a.Controller.CurrentHex.Equals(hex));
            foreach (ArmyData army in stationed.Concat(participatingArmies ?? Array.Empty<ArmyData>()).Distinct())
                if (army != null)
                    foreach (UnitData unit in army.Members)
                        if (unit != null && unit.HitPointsCurrent > 0 && seen.Add(unit))
                            result.Add(new Target(unit, army));
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null)
            {
                if (building.StructurePointsCurrent > 0) result.Add(new Target(building));
                for (int i = 0; i < building.FacilitySlots.Length; i++)
                    if (building.FacilitySlots[i]?.StructurePointsCurrent > 0)
                        result.Add(new Target(building, building.FacilitySlots[i], i));
            }
            return result;
        }

        public static IEnumerable<Target> KnownTargets(HexCoord hex, PlayerSetupData observer,
            params ArmyData[] participatingArmies) => CollectTargets(hex, participatingArmies)
            .Where(t => (t.Unit == null || !StealthSystem.IsHiddenFrom(t.Unit, observer))
                && (t.Facility == null || t.Building.Owner == observer));

        public static IReadOnlyList<WorthIt.DefenderProfile> StructureProfiles(BuildingData building,
            bool includeFacilities = true)
        {
            var profiles = new List<WorthIt.DefenderProfile>();
            void Add(Target target)
            {
                UnitData u = target.Combatant();
                if (u.HitPointsCurrent > 0) profiles.Add(new WorthIt.DefenderProfile(u.Defense,
                    u.HasAbility(UnitAbilities.CeramicArmor), null, hitPoints: u.HitPointsCurrent, initiative: u.Initiative,
                    abilities: u.Abilities.ToArray(), maxHitPoints: u.HitPointsMax, isGroundCombatant: false,
                    fateMax: u.Fate));
            }
            Add(new Target(building));
            if (includeFacilities)
                for (int i = 0; i < building.FacilitySlots.Length; i++)
                    if (building.FacilitySlots[i] != null) Add(new Target(building, building.FacilitySlots[i], i));
            return profiles.AsReadOnly();
        }

        public BattleAttackApplication Resolve(Target target, UnitData defender, BattleChallengeRollResult roll,
            AbilityMagnitudes magnitudes, UnitData attackerHero = null, UnitData defenderHero = null,
            BattleEngine battle = null)
        {
            battle = OwningBattle(battle);
            using var mutation = VisionSystem.BeginMutation();
            if (_completed || (battle != null && !battle.OwnsExplosion(this))
                || !Targets.Contains(target) || !target.IsAlive)
                throw new InvalidOperationException("Explosion target is no longer available.");
            if (battle != null) magnitudes = battle.Magnitudes;
            if (!_resolved.Add(target)) throw new InvalidOperationException("An explosion cannot hit the same target twice.");
            ResolvedCount++;
            var application = BattleEngine.ResolveStandaloneAttack(Actor, defender, roll, magnitudes,
                attackerHero, defenderHero, completeAttack: false);
            if (target.Unit != null)
            {
                if (application.DefenderDied)
                {
                    if (battle != null && battle.State.OwningArmy(target.Unit) != null)
                        application.Removals.Add(battle.RemoveUnit(target.Unit, announceKiller: false, deferCarrierCleanup: true));
                    else
                    {
                        ArmyActions.RemoveMember(target.Army, target.Unit, cleanup: false, publish: false);
                        StealthSystem.OnUnitRemoved(target.Unit, deferCarrierCleanup: true, deferNotifications: true);
                    }
                    target.Army.RefreshTransport();
                }
                else if (BattleSimulationKernel.SuppressesTurn(application.Damage, Actor.Abilities))
                {
                    application.Shocked = battle?.SkipPendingTurn(target.Unit) ?? false;
                    application.ShockTriggered = application.Shocked;
                }
            }
            else if (target.Facility != null)
            {
                target.Facility.StructurePointsCurrent = defender.HitPointsCurrent;
                target.Facility.Fate = defender.Fate;
            }
            else
            {
                target.Building.StructurePointsCurrent = defender.HitPointsCurrent;
                target.Building.Fate = defender.Fate;
            }
            return application;
        }
        private readonly HashSet<Target> _resolved = new HashSet<Target>();

        public void Complete(BattleEngine battle = null, HexSelectionController selection = null)
        {
            battle = OwningBattle(battle);
            using var mutation = VisionSystem.BeginMutation();
            if (_completed) return;
            if (battle != null && !battle.OwnsExplosion(this))
                throw new InvalidOperationException("The explosion does not own this battle action.");
            _completed = true;
            if (ResolvedCount == 0) { battle?.CompleteExplosionAction(Launcher); return; }
            foreach (var target in Targets)
                if (target.Unit != null && target.Unit.HitPointsCurrent <= 0)
                    AviationActions.RemoveCarrierContainers(target.Unit, selection);
            CompleteLauncher(Launcher, battle, selection, _launcherArmy);
            // Publish structural destruction only after all selected challenges. Destroying a
            // starting citadel can finish the match; it must not cut a launched blast short.
            BuildingData building = BuildingRegistry.FindAt(Hex);
            if (building != null)
            {
                for (int i = 0; i < building.FacilitySlots.Length; i++)
                    if (building.FacilitySlots[i]?.StructurePointsCurrent <= 0)
                        building.FacilitySlots[i] = null;
                if (building.StructurePointsCurrent <= 0)
                {
                    BuildingRegistry.Unregister(Hex);
                    BuildingRegistry.RaiseStructureLost(Hex, building.Owner);
                    if (building.Visual != null) UnityEngine.Object.Destroy(building.Visual.gameObject);
                }
            }
            foreach (ArmyData army in Targets.Where(t => t.Army != null).Select(t => t.Army).Distinct())
            { ArmyActions.DeleteArmyIfEmptied(army); VisionSystem.RecomputeFor(army.Owner); }
            selection?.RestackArmiesOn(Hex, null);
            VisionSystem.NotifyContentChanged(Hex);
            battle?.CompleteExplosionAction(Launcher);
        }

        public static void CompleteLauncher(UnitData launcher, BattleEngine battle = null,
            HexSelectionController selection = null, ArmyData launcherArmy = null)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (launcher == null) return;
            bool expendUnit = IsSingleUseUnit(launcher);
            EquipmentSystem.ConsumeAfterAttack(launcher);
            if (expendUnit && launcher.HitPointsCurrent > 0)
            {
                launcher.HitPointsCurrent = 0;
                ArmyData army = launcherArmy ?? ArmyRegistry.FindArmyContaining(launcher) ?? battle?.State.OwningArmy(launcher);
                if (battle != null && battle.State.OwningArmy(launcher) != null)
                    battle.RemoveUnit(launcher, announceKiller: false, deferCarrierCleanup: true);
                else { ArmyActions.RemoveMember(army, launcher, cleanup: false); StealthSystem.OnUnitRemoved(launcher, deferCarrierCleanup: true); }
                AviationActions.RemoveCarrierContainers(launcher, selection);
                army?.RefreshTransport();
                ArmyActions.DeleteArmyIfEmptied(army);
            }
        }
    }
}
