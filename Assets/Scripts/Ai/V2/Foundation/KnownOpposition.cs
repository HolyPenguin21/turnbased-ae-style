using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // THE known opposition of a ground fight, read only from honest memory (the snapshot's Known
    // section, or AiMapMemory for an execution-time audit). A mover that lands on a hex fights
    // everything there — the battle opens against the strongest body (BattleInitiator.FindEnemyAt)
    // and the winner fights the next (WorthIt.EstimateSequential models the same order) — so every
    // lane prices a fight through these readers: Raid (ForRaid), ActiveDefence (OfEnemyArmy),
    // Attack's site and local contacts and CombatOpportunityAnalyzer (OnHex / AddCoOccupants).
    internal static class KnownOpposition
    {
        // Every known body a ground mover landing on `hex` fights: an event guard and each enemy /
        // neutral sighting standing there. Empty when nothing is known.
        internal static List<WorthIt.DefendingArmy> OnHex(WorldSnapshot snap, HexCoord hex)
        {
            var result = new List<WorthIt.DefendingArmy>();
            AddCoOccupants(snap, hex, -1, result);
            return result;
        }

        // The same as one flat roster, for counts / power / coverage.
        internal static List<WorthIt.DefenderProfile> DefendersOnHex(WorldSnapshot snap, HexCoord hex) =>
            WorthIt.UnitsOf(OnHex(snap, hex));

        // The fight an intercept of this enemy is: its honestly-known roster and commander FIRST
        // (the fist ActiveDefence's HoldChanceAtAsset reads as opposition[0]), then every other
        // known body on its hex. Null when the contact carries no position.
        internal static IReadOnlyList<WorthIt.DefendingArmy> OfEnemyArmy(WorldSnapshot snap, int enemyArmyId)
        {
            EnemyContactSnapshot contact = snap?.Threat?.Contacts?.FirstOrDefault(c =>
                c?.Army != null && c.Army.ArmyId == enemyArmyId && c.Position.HasValue);
            if (contact == null)
                return null;
            var result = new List<WorthIt.DefendingArmy>
            {
                new WorthIt.DefendingArmy(contact.Army.Members, contact.Army.Commander,
                    Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, contact.Position.Value,
                        contact.Army.Owner), enemyArmyId,
                    Game.Ai.AiMapMemory.KnownBuildingDefenseBonusFor(snap.Observer, contact.Position.Value, contact.Army.Owner)),
            };
            AddCoOccupants(snap, contact.Position.Value, enemyArmyId, result);
            return result;
        }

        // THE single resolver every Raid consumer (objective, demand, admission, assembly,
        // provisioning, reinforcement projection, continuity WorthIt checks) must use to find a
        // Raid target's defenders — no event/army switch duplicated elsewhere. Note armyId 0 is a
        // legitimate army id (see ArmyData identity sequencing), so absence is expressed only by
        // RaidTargetRef.HasValue == false or "not found in the sighting/guard list", never by a
        // numeric sentinel.
        internal static IReadOnlyList<WorthIt.DefenderProfile> RaidDefenders(WorldSnapshot snap, RaidTargetRef target) =>
            WorthIt.UnitsOf(ForRaid(snap, target));

        // The same target as the fight it is: the defending army (or event guard) with its
        // observed commander, PLUS every other known body standing on that hex. A mover that
        // lands on a hex fights everything there (an event guard and a roaming neutral army can
        // share one; the 2026-10-02 playtest sent a lone Scav Carrier at "Guard@-2,-2" priced
        // against its 2 defenders alone while neutral Army#6 stood on the same hex — four
        // retreats in a row at an estimated win of 1.00). Empty when nothing is known.
        internal static IReadOnlyList<WorthIt.DefendingArmy> ForRaid(WorldSnapshot snap, RaidTargetRef target) =>
            BuildRaid(snap, target, includeSite: true);

        // Execution historically revalidates only the bound primary encounter. Keep that scope
        // explicit: do not silently turn a mechanical audit into a different fight projection.
        internal static IReadOnlyList<WorthIt.DefendingArmy> RaidPrimaryLive(
            Game.Players.PlayerSetupData player, RaidTargetRef target)
        {
            var guards = new List<KnownEventGuardSnapshot>();
            if (target.Kind == RaidTargetKind.EventGuard)
            {
                Game.Ai.AiMapMemory.GuardStrength? guard = Game.Ai.AiMapMemory.KnownEventGuardStrengthAt(player, target.Hex);
                if (guard.HasValue)
                    guards.Add(new KnownEventGuardSnapshot(target.Hex, guard.Value, null,
                        guard.Value.Defenders?.Count ?? 0));
            }
            return BuildRaid(new WorldSnapshot
            {
                Observer = player,
                Known = new KnownSnapshot
                {
                    EnemySightings = Game.Ai.AiMapMemory.AllKnownEnemySightings(player).ToList(),
                    NeutralSightings = Game.Ai.AiMapMemory.AllKnownNeutralSightings(player).ToList(),
                    EventGuards = guards,
                },
            }, target, includeSite: false);
        }

        // THE hex defence a Raid target fights with — the companion of ForRaid, resolved the same
        // way (event guard: its stable hex; neutral army: its last observed hex) and read through
        // the one fog-honest owner. A field battle is still a battle on a hex: terrain defends the
        // target exactly as WorthIt folds it into the live fight.
        internal static float RaidDefenceBonus(WorldSnapshot snap, RaidTargetRef target)
        {
            if (snap?.Known == null || !target.HasValue)
                return 0f;
            if (target.Kind == RaidTargetKind.EventGuard)
                return Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(
                    snap.Observer, target.Hex, defendingOwner: null);
            Game.Ai.AiMapMemory.KnownEnemySighting? sighting = snap.Known.SightingOf(target.ArmyId);
            return sighting.HasValue ? Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(
                snap.Observer, sighting.Value.Hex, sighting.Value.Owner) : 0f;
        }

        // Every OTHER known body a ground mover landing on `hex` also fights besides the army it
        // aims at: an event guard and each enemy/neutral sighting standing there (2026-10-19
        // playtest: an intercept of a lone Watcher Drone estimated at win 1.00 walked into the
        // 5-unit army sharing its hex, two turns in a row).
        internal static void AddCoOccupants(WorldSnapshot snap, HexCoord hex, int excludeArmyId,
            List<WorthIt.DefendingArmy> into)
        {
            if (snap?.Known == null || into == null)
                return;
            AddEventGuard(snap, hex, into);
            AddSightingsAt(snap, hex, excludeArmyId, into);
        }

        // The sightings part alone (a caller whose target IS the hex's event guard).
        // Air armies and airfields never join a ground battle (BattleInitiator.IsEngageable).
        internal static void AddSightingsAt(WorldSnapshot snap, HexCoord hex, int excludeArmyId,
            List<WorthIt.DefendingArmy> into)
        {
            if (snap?.Known == null || into == null)
                return;
            IEnumerable<Game.Ai.AiMapMemory.KnownEnemySighting> all =
                (snap.Known.EnemySightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>());
            foreach (Game.Ai.AiMapMemory.KnownEnemySighting s in all.OrderBy(x => x.ArmyId))
                if (s.Hex.Equals(hex) && s.ArmyId != excludeArmyId && s.Defenders != null
                    && s.Defenders.Count > 0 && !s.IsAir && s.Owner != snap.Observer)
                    into.Add(new WorthIt.DefendingArmy(s.Defenders, s.Commander,
                        Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, hex, s.Owner), s.ArmyId,
                        Game.Ai.AiMapMemory.KnownBuildingDefenseBonusFor(snap.Observer, hex, s.Owner)));
            AddRememberedSiteResidents(snap, hex, excludeArmyId, into);
        }

        // An enemy field army last observed standing on its OWN Base/Citadel is still that site's
        // defender until a look proves it gone — the Recon history (AiReconMemory, the snapshot's
        // LastKnown contacts) drops it on re-observation. Its short tactical sighting expiring
        // (AiConfig.enemySightingMemoryTurns) is no evidence it left. Playtest 2026-10-22: Ironfang
        // and Dune Wolves expired at Ysolde's T19 while standing on Kryll's Citadel; her assault
        // priced the site at 46 instead of ~100, marched three turns and withdrew at win 0.04.
        // Only a structure-bound staging site: a field position seen once is not kept as occupied.
        private static void AddRememberedSiteResidents(WorldSnapshot snap, HexCoord hex,
            int excludeArmyId, List<WorthIt.DefendingArmy> into)
        {
            IReadOnlyList<EnemyContactSnapshot> contacts = snap?.Threat?.Contacts;
            IReadOnlyList<Game.Ai.AiMapMemory.KnownBuilding> buildings = snap?.Known?.Buildings;
            if (contacts == null || buildings == null)
                return;
            foreach (EnemyContactSnapshot c in contacts
                .Where(c => c?.Army != null && c.Knowledge == ContactKnowledge.LastKnown
                    && c.Position.HasValue && c.Position.Value.Equals(hex))
                .OrderBy(c => c.Army.ArmyId))
            {
                ArmySnapshot army = c.Army;
                if (army.ArmyId == excludeArmyId || army.IsAir || army.Owner == null
                    || army.Owner == snap.Observer || army.Owner.IsNeutral
                    || army.Members == null || army.Members.Count == 0
                    || into.Any(d => d.ArmyId == army.ArmyId)
                    // The site was not looked at since the army was seen on it: a later look that
                    // found it absent (the history keeps an army that only LEFT into the fog) wins.
                    || !buildings.Any(b => b.Hex.Equals(hex) && b.Owner == army.Owner
                        && (b.IsBase || b.IsStartingCitadel) && b.SeenTurn <= c.LastObservedTurn))
                    continue;
                into.Add(new WorthIt.DefendingArmy(army.Members, army.Commander,
                    Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, hex, army.Owner), army.ArmyId,
                    Game.Ai.AiMapMemory.KnownBuildingDefenseBonusFor(snap.Observer, hex, army.Owner)));
            }
        }

        private static IReadOnlyList<WorthIt.DefendingArmy> BuildRaid(WorldSnapshot snap, RaidTargetRef target, bool includeSite)
        {
            if (snap?.Known == null || !target.HasValue)
                return System.Array.Empty<WorthIt.DefendingArmy>();

            var result = new List<WorthIt.DefendingArmy>();
            HexCoord hex;
            int excludeArmyId = -1;
            if (target.Kind == RaidTargetKind.EventGuard)
            {
                hex = target.Hex;
                AddEventGuard(snap, hex, result, includeSite);
                if (result.Count == 0)
                    return System.Array.Empty<WorthIt.DefendingArmy>();
            }
            else
            {
                Game.Ai.AiMapMemory.KnownEnemySighting? own = snap.Known.SightingOf(target.ArmyId);
                if (!own.HasValue)
                    return System.Array.Empty<WorthIt.DefendingArmy>();
                hex = own.Value.Hex;
                excludeArmyId = target.ArmyId;
                result.Add(new WorthIt.DefendingArmy(own.Value.Defenders, own.Value.Commander,
                    includeSite ? Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, hex, own.Value.Owner) : 0f, own.Value.ArmyId,
                    includeSite ? Game.Ai.AiMapMemory.KnownBuildingDefenseBonusFor(snap.Observer, hex, own.Value.Owner) : 0f));
                if (includeSite) AddEventGuard(snap, hex, result);
            }

            if (!includeSite) return result;

            AddSightingsAt(snap, hex, excludeArmyId, result);
            return result;
        }

        private static void AddEventGuard(WorldSnapshot snap, HexCoord hex, List<WorthIt.DefendingArmy> into, bool includeSite = true)
        {
            if (snap.Known.EventGuards == null)
                return;
            foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                if (g.Hex.Equals(hex))
                {
                    into.Add(new WorthIt.DefendingArmy(g.Defenders, g.Commander,
                        includeSite ? Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, g.Hex, defendingOwner: null) : 0f,
                        buildingDefenseBonus: includeSite ? Game.Ai.AiMapMemory.KnownBuildingDefenseBonusFor(
                            snap.Observer, g.Hex, defendingOwner: null) : 0f));
                    return;
                }
        }
    }
}
