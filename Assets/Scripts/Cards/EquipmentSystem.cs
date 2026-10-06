using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Cards
{
    // The one place a CardType.Equipment card's effect is validated and applied — the analogue
    // of ArmyActions.DeployUnitFromCard for gear. Two attach targets, per the project owner's
    // own spec: a live UnitData already in an army, or a not-yet-spawned Unit/Hero CardData
    // still in hand (the grant then rides along and is applied by ArmyActions.DeployUnitFromCard
    // when that card is finally played). Two independent permanent slots per host.
    // There is no un-attach (the manual: "Once placed an attachment card can never be removed").
    //
    // Cost to attach is the equipment card's own apCost + resourceCost (same fields every card
    // has) — spent once, here, whichever target kind it's attached to.
    public static class EquipmentSystem
    {
        public static CardDefinition GetAttachment(CardData host, CardDefinition attachment)
            => attachment?.attachmentSlot == AttachmentSlot.Mutator ? host?.Mutator : host?.Equipment;

        public static CardDefinition GetAttachment(UnitData host, CardDefinition attachment)
            => attachment?.attachmentSlot == AttachmentSlot.Mutator ? host?.Mutator : host?.Equipment;

        private static void SetAttachment(CardData host, CardDefinition attachment)
        {
            if (attachment.attachmentSlot == AttachmentSlot.Mutator) host.Mutator = attachment;
            else host.Equipment = attachment;
        }

        // Install from a hand card once, before the spawned unit is published. sourceCard owns
        // both references; the optional legacy Equipment argument is only a fallback.
        public static void ApplyAttachments(UnitData unit, CardDefinition equipment, CardDefinition mutator)
        {
            if (equipment != null) Install(equipment, unit);
            if (mutator != null) Install(mutator, unit);
        }

        private static void Install(CardDefinition attachment, UnitData unit)
        {
            if (!unit.AttachmentBase.HasValue)
                unit.AttachmentBase = new PredictedEquipmentState(ReadStats(unit), new List<string>(unit.Abilities));
            bool first = unit.Equipment == null && unit.Mutator == null;
            if (first)
                unit.AttachmentResources = new AttachmentResourceState(unit.HitPointsMax - unit.HitPointsCurrent,
                    unit.MoveMax - unit.MoveCurrent, unit.FateMax - unit.Fate, unit);
            if (attachment.attachmentSlot == AttachmentSlot.Mutator) unit.Mutator = attachment;
            else unit.Equipment = attachment;

            if (first)
            {
                // Preserve the existing one-Equipment gameplay, including current/max semantics.
                Apply(attachment.equipment, unit);
                unit.AttachmentApplied = new PredictedEquipmentState(ReadStats(unit), new List<string>(unit.Abilities));
                var usage = unit.AttachmentResources.Value;
                unit.AttachmentResources = new AttachmentResourceState(usage.HpSpent, usage.MoveSpent, usage.FateSpent, unit);
                return;
            }

            PredictedEquipmentState projected = PredictSlots(unit.AttachmentBase.Value,
                unit.Equipment, unit.Mutator);
            PredictedEquipmentState next = MergeRuntimeState(unit, projected);
            WriteStats(unit, next.Stats);
            unit.Abilities.Clear();
            unit.Abilities.UnionWith(next.Abilities);
            unit.AttachmentApplied = projected;
        }

        public static Dictionary<EquipmentStat, int> DefinitionStats(CardDefinition host)
            => new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = host.attack,
                [EquipmentStat.Defense] = host.defenseRating,
                [EquipmentStat.Resistance] = host.resistanceRating,
                [EquipmentStat.Range] = host.range,
                [EquipmentStat.HitPoints] = host.hitPoints,
                [EquipmentStat.MoveMax] = host.moveMax,
                [EquipmentStat.Initiative] = host.initiative,
                [EquipmentStat.ActivationApCost] = host.grantedAbilities != null
                    && host.grantedAbilities.Contains(UnitAbilities.RapidReaction) ? 0 : host.activationApCost,
                [EquipmentStat.CommandRating] = host.commandRating,
                [EquipmentStat.Fate] = host.fate,
            };

        private static Dictionary<EquipmentStat, int> ReadStats(UnitData host)
            => new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = host.Attack,
                [EquipmentStat.Defense] = host.Defense,
                [EquipmentStat.Resistance] = host.Resistance,
                [EquipmentStat.Range] = host.Range,
                [EquipmentStat.HitPoints] = host.HitPointsMax,
                [EquipmentStat.MoveMax] = host.MoveMax,
                [EquipmentStat.Initiative] = host.Initiative,
                [EquipmentStat.ActivationApCost] = host.ActivationApCost,
                [EquipmentStat.CommandRating] = host.CommandRating,
                [EquipmentStat.Fate] = host.FateMax,
            };

        private static void WriteStats(UnitData unit, IReadOnlyDictionary<EquipmentStat, int> stats)
        {
            int hpSpent = SpentResource(unit, EquipmentStat.HitPoints);
            int moveSpent = SpentResource(unit, EquipmentStat.MoveMax);
            int fateSpent = SpentResource(unit, EquipmentStat.Fate);
            unit.HitPointsCurrent = Mathf.Clamp(stats[EquipmentStat.HitPoints] - hpSpent,
                unit.HitPointsCurrent > 0 ? 1 : 0, stats[EquipmentStat.HitPoints]);
            unit.MoveCurrent = Mathf.Clamp(stats[EquipmentStat.MoveMax] - moveSpent, 0, stats[EquipmentStat.MoveMax]);
            unit.Fate = Mathf.Clamp(stats[EquipmentStat.Fate] - fateSpent, 0, stats[EquipmentStat.Fate]);
            unit.AttachmentResources = new AttachmentResourceState(hpSpent, moveSpent, fateSpent, unit);
            unit.Attack = stats[EquipmentStat.Attack]; unit.Defense = stats[EquipmentStat.Defense];
            unit.Resistance = stats[EquipmentStat.Resistance]; unit.Range = stats[EquipmentStat.Range];
            unit.HitPointsMax = stats[EquipmentStat.HitPoints]; unit.MoveMax = stats[EquipmentStat.MoveMax];
            unit.Initiative = stats[EquipmentStat.Initiative]; unit.ActivationApCost = stats[EquipmentStat.ActivationApCost];
            unit.CommandRating = stats[EquipmentStat.CommandRating]; unit.FateMax = stats[EquipmentStat.Fate];
        }

        // Preserve consumption even when an intermediate attachment lowered a maximum and
        // clamped current. A later canonical rebuild must not interpret that clamp as healing.
        private static int SpentResource(UnitData unit, EquipmentStat stat)
        {
            var usage = unit.AttachmentResources;
            if (stat == EquipmentStat.HitPoints)
                return Mathf.Max(0, usage.HasValue ? usage.Value.HpSpent + usage.Value.LastHp - unit.HitPointsCurrent
                    : unit.HitPointsMax - unit.HitPointsCurrent);
            if (stat == EquipmentStat.MoveMax)
                return Mathf.Max(0, usage.HasValue ? usage.Value.MoveSpent + usage.Value.LastMove - unit.MoveCurrent
                    : unit.MoveMax - unit.MoveCurrent);
            return Mathf.Max(0, usage.HasValue ? usage.Value.FateSpent + usage.Value.LastFate - unit.Fate
                : unit.FateMax - unit.Fate);
        }

        public static int CurrentAfterAttachment(UnitData unit, EquipmentStat stat, int nextMax)
        {
            if (unit.AttachmentResources.HasValue)
                return Mathf.Clamp(nextMax - SpentResource(unit, stat),
                    stat == EquipmentStat.HitPoints && unit.HitPointsCurrent > 0 ? 1 : 0, nextMax);
            int current = stat == EquipmentStat.HitPoints ? unit.HitPointsCurrent
                : stat == EquipmentStat.MoveMax ? unit.MoveCurrent : unit.Fate;
            int maximum = stat == EquipmentStat.HitPoints ? unit.HitPointsMax
                : stat == EquipmentStat.MoveMax ? unit.MoveMax : unit.FateMax;
            return Mathf.Clamp(current + Mathf.Max(0, nextMax - maximum), 0, nextMax);
        }

        // An explicit refill/repair starts a new consumption history for that resource.
        // Ordinary current-value deltas cannot distinguish a refill from a maximum clamp.
        internal static void ReconcileResourceRefill(UnitData unit, EquipmentStat stat)
        {
            if (!unit.AttachmentResources.HasValue) return;
            unit.AttachmentResources = new AttachmentResourceState(
                stat == EquipmentStat.HitPoints ? unit.HitPointsMax - unit.HitPointsCurrent : SpentResource(unit, EquipmentStat.HitPoints),
                stat == EquipmentStat.MoveMax ? unit.MoveMax - unit.MoveCurrent : SpentResource(unit, EquipmentStat.MoveMax),
                stat == EquipmentStat.Fate ? unit.FateMax - unit.Fate : SpentResource(unit, EquipmentStat.Fate), unit);
        }

        // Full projected state, shared by hand UI and AI. Canonical slot order is independent
        // of installation order. A candidate replaces only its corresponding projected slot.
        public static PredictedEquipmentState Project(CardDefinition host, CardDefinition equipment,
            CardDefinition mutator, CardDefinition candidate = null)
            => PredictSlots(new PredictedEquipmentState(DefinitionStats(host),
                host.grantedAbilities != null ? new List<string>(host.grantedAbilities) : new List<string>()),
                equipment, mutator, candidate);

        public static PredictedEquipmentState Project(CardData host, CardDefinition candidate = null)
            => Project(host.Definition, host.Equipment, host.Mutator, candidate);

        public static List<string> EffectiveAbilities(CardData host, CardDefinition candidate = null)
            => host?.Definition == null ? new List<string>() : new List<string>(Project(host, candidate).Abilities);

        public static PredictedEquipmentState PredictAttachment(CardDefinition candidate, UnitData unit)
        {
            if (!unit.AttachmentBase.HasValue || !unit.AttachmentApplied.HasValue)
                // Predict is a sparse grant delta; attachment consumers require the whole host.
                // Start from live stats and use the same full composition as hand projections.
                return PredictSlots(new PredictedEquipmentState(ReadStats(unit),
                    new List<string>(unit.Abilities)), null, null, candidate);
            PredictedEquipmentState next = PredictSlots(unit.AttachmentBase.Value, unit.Equipment, unit.Mutator, candidate);
            return MergeRuntimeState(unit, next);
        }

        // Replacing a permanent projection must not discard temporary combat changes.
        private static PredictedEquipmentState MergeRuntimeState(UnitData unit, PredictedEquipmentState next)
        {
            var stats = ReadStats(unit);
            PredictedEquipmentState previous = unit.AttachmentApplied.Value;
            foreach (EquipmentStat stat in new List<EquipmentStat>(stats.Keys))
                stats[stat] += next.Stats[stat] - previous.Stats[stat];
            var abilities = new HashSet<string>(next.Abilities);
            foreach (string ability in unit.Abilities)
                if (!previous.Abilities.Contains(ability)) abilities.Add(ability);
            foreach (string ability in previous.Abilities)
                if (!unit.Abilities.Contains(ability)) abilities.Remove(ability);
            return new PredictedEquipmentState(stats, new List<string>(abilities));
        }

        private static PredictedEquipmentState PredictSlots(PredictedEquipmentState baseline,
            CardDefinition equipment, CardDefinition mutator, CardDefinition candidate = null)
        {
            if (candidate != null)
            {
                if (candidate.attachmentSlot == AttachmentSlot.Mutator) mutator = candidate;
                else equipment = candidate;
            }
            var stats = new Dictionary<EquipmentStat, int>();
            foreach (var pair in baseline.Stats) stats[pair.Key] = pair.Value;
            IReadOnlyList<string> abilities = baseline.Abilities;
            foreach (CardDefinition attachment in new[] { equipment, mutator })
            {
                if (attachment?.equipment == null) continue;
                PredictedEquipmentState next = Predict(attachment.equipment, stats, abilities);
                foreach (var pair in next.Stats) stats[pair.Key] = pair.Value;
                abilities = next.Abilities;
            }
            return new PredictedEquipmentState(stats, abilities);
        }

        // --- family matching (for EquipmentGrant.clearAbilityFamilies) ----------------------
        // Delegates to AbilityParams so the parameterized-tag grammar (r<N>s<M>, Stealth<N>)
        // stays defined in exactly one file.
        public static bool IsFamilyMember(string ability, AbilityFamily family)
        {
            switch (family)
            {
                case AbilityFamily.Recce: return AbilityParams.TryGetRecce(ability, out _, out _);
                case AbilityFamily.Stealth: return AbilityParams.TryGetStealthLevel(ability, out _);
                default: return false;
            }
        }

        // The host's ability tag list AFTER this grant is applied, without mutating anything —
        // same order as EquipmentGrant's doc / Apply below: clear families, then remove exact,
        // then add. Used by the hand & catalog-preview UI to show what a card's abilities will
        // become once an in-hand equipment attach reaches the spawned unit. `grant` null just
        // returns a fresh copy of baseAbilities.
        public static List<string> EffectiveAbilities(IEnumerable<string> baseAbilities, EquipmentGrant grant)
        {
            var result = baseAbilities != null ? new List<string>(baseAbilities) : new List<string>();
            if (grant == null)
                return result;

            if (grant.clearAbilityFamilies != null)
                foreach (AbilityFamily family in grant.clearAbilityFamilies)
                {
                    if (family == AbilityFamily.None)
                        continue;
                    result.RemoveAll(a => IsFamilyMember(a, family));
                }

            if (grant.removeAbilities != null)
                foreach (string tag in grant.removeAbilities)
                    if (!string.IsNullOrEmpty(tag))
                        result.RemoveAll(a => a == tag);

            if (grant.addAbilities != null)
                foreach (string tag in grant.addAbilities)
                    if (!string.IsNullOrEmpty(tag) && !result.Contains(tag))
                        result.Add(tag);

            return result;
        }

        // --- validation --------------------------------------------------------------------

        public static bool CanAttach(CardDefinition equipment, UnitData target, PlayerRoot owner, out string reason)
            => CanAttach(equipment, equipment != null ? equipment.apCost : 0,
                equipment != null ? equipment.resourceCost : null, target, owner, out reason);

        // Research/Production-created equipment: the CardData instance already paid its
        // ResourceCost at Create, so it attaches for activationApCost and no resources. Ordinary
        // equipment cards resolve 1:1 to apCost / resourceCost (see the CardDefinition overload).
        public static bool CanAttach(CardData equipmentCard, UnitData target, PlayerRoot owner, out string reason)
            => CanAttach(equipmentCard?.Definition, equipmentCard != null ? equipmentCard.EffectivePlayApCost : 0,
                equipmentCard != null ? equipmentCard.EffectivePlayResourceCost : null, target, owner, out reason);

        private static bool CanAttach(CardDefinition equipment, int apCost, ResourceCost resourceCost,
            UnitData target, PlayerRoot owner, out string reason, bool checkBudget = true)
        {
            if (target == null)
            {
                reason = "No target.";
                return false;
            }
            EquipmentHostKind kind = target.IsHero ? EquipmentHostKind.Hero : EquipmentHostKind.Unit;
            return CanAttachCore(equipment, apCost, resourceCost, kind, target.TypeTags, GetAttachment(target, equipment) != null, owner, out reason, checkBudget);
        }

        // Same checks against a card still in hand — host kind/tags come from the card's own
        // design (CardDefinition), not a spawned UnitData.
        public static bool CanAttach(CardDefinition equipment, CardData targetCard, PlayerRoot owner, out string reason)
            => CanAttach(equipment, equipment != null ? equipment.apCost : 0,
                equipment != null ? equipment.resourceCost : null, targetCard, owner, out reason);

        public static bool CanAttach(CardData equipmentCard, CardData targetCard, PlayerRoot owner, out string reason)
            => CanAttach(equipmentCard?.Definition, equipmentCard != null ? equipmentCard.EffectivePlayApCost : 0,
                equipmentCard != null ? equipmentCard.EffectivePlayResourceCost : null, targetCard, owner, out reason);

        private static bool CanAttach(CardDefinition equipment, int apCost, ResourceCost resourceCost,
            CardData targetCard, PlayerRoot owner, out string reason, bool checkBudget = true)
        {
            if (!TryGetHostProfile(targetCard?.Definition, out EquipmentHostKind kind,
                    out ICollection<UnitTypeTag> hostTags, out reason))
                return false;
            return CanAttachCore(equipment, apCost, resourceCost, kind, hostTags,
                GetAttachment(targetCard, equipment) != null, owner, out reason, checkBudget);
        }

        // Future attachment: retain host/tag and occupied-slot checks, defer only payment.
        // Execution always uses CanAttach/TryAttach with the live budget.
        internal static bool CanAttachPreview(CardDefinition equipment, UnitData target, out string reason)
            => CanAttach(equipment, 0, null, target, null, out reason, checkBudget: false);

        internal static bool CanAttachPreview(CardDefinition equipment, CardData target, out string reason)
            => CanAttach(equipment, 0, null, target, null, out reason, checkBudget: false);

        // Pure definition-level compatibility for planners and previews. Slot occupancy, AP and
        // resources are deliberately excluded; the live CanAttach overloads layer those checks on
        // top of the same FitsHostCore rule below.
        public static bool FitsHost(CardDefinition equipment, CardDefinition host, out string reason)
        {
            if (!TryGetHostProfile(host, out EquipmentHostKind kind,
                    out ICollection<UnitTypeTag> hostTags, out reason))
                return false;
            return FitsHostCore(equipment, kind, hostTags, out reason);
        }

        private static bool TryGetHostProfile(CardDefinition host, out EquipmentHostKind kind,
            out ICollection<UnitTypeTag> hostTags, out string reason)
        {
            kind = EquipmentHostKind.Unit;
            hostTags = null;
            if (host == null)
            {
                reason = "No target.";
                return false;
            }
            if (host.cardType != CardType.Unit && host.cardType != CardType.Hero)
            {
                reason = "Equipment can only go on a unit or hero card.";
                return false;
            }
            kind = host.cardType == CardType.Hero ? EquipmentHostKind.Hero : EquipmentHostKind.Unit;
            hostTags = host.unitTypeTags;
            reason = null;
            return true;
        }

        internal static bool FitsHostCore(CardDefinition equipment, EquipmentHostKind kind,
            ICollection<UnitTypeTag> hostTags, out string reason)
        {
            reason = null;
            if (equipment == null || equipment.cardType != CardType.Equipment || equipment.equipment == null)
            {
                reason = "Not an equipment card.";
                return false;
            }
            EquipmentGrant grant = equipment.equipment;
            if (equipment.attachmentSlot != AttachmentSlot.Equipment && equipment.attachmentSlot != AttachmentSlot.Mutator)
            {
                reason = "Unknown attachment slot.";
                return false;
            }
            if (equipment.attachmentSlot == AttachmentSlot.Mutator
                && (hostTags == null || !hostTags.Contains(UnitTypeTag.Bio)))
            {
                reason = "Mutators require a Bio unit or hero.";
                return false;
            }

            if (grant.hostKinds == null || !grant.hostKinds.Contains(kind))
            {
                reason = $"{equipment.displayName} can't be attached to that.";
                return false;
            }

            // Empty hostTypeTags = fits any host of an allowed kind; otherwise ANY match.
            if (grant.hostTypeTags != null && grant.hostTypeTags.Count > 0)
            {
                bool match = false;
                if (hostTags != null)
                    foreach (UnitTypeTag needed in grant.hostTypeTags)
                        if (hostTags.Contains(needed)) { match = true; break; }
                if (!match)
                {
                    reason = $"{equipment.displayName} doesn't fit this unit.";
                    return false;
                }
            }
            return true;
        }

        private static bool CanAttachCore(CardDefinition equipment, int apCost, ResourceCost resourceCost,
            EquipmentHostKind kind, ICollection<UnitTypeTag> hostTags, bool slotTaken, PlayerRoot owner, out string reason, bool checkBudget = true)
        {
            if (!FitsHostCore(equipment, kind, hostTags, out reason))
                return false;

            if (slotTaken)
            {
                reason = $"This already has {equipment.attachmentSlot.ToString().ToLowerInvariant()} attached.";
                return false;
            }

            if (!checkBudget) return true;

            if (owner == null || !owner.CanSpendActionPoints(apCost))
            {
                reason = $"Not enough action points to attach {equipment.displayName}.";
                return false;
            }
            if (resourceCost != null && !resourceCost.CanAfford(owner))
            {
                reason = $"Not enough resources to attach {equipment.displayName}.";
                return false;
            }
            return true;
        }

        // --- attach ----------------------------------------------------------------------

        public static bool TryAttach(CardDefinition equipment, UnitData target, PlayerRoot owner, out string reason)
        {
            if (!CanAttach(equipment, target, owner, out reason))
                return false;
            PayCost(equipment, equipment != null ? equipment.apCost : 0,
                equipment != null ? equipment.resourceCost : null, owner);
            Install(equipment, target);
            RefreshLiveHostObservation(target);
            return true;
        }

        public static bool TryAttach(CardDefinition equipment, CardData targetCard, PlayerRoot owner, out string reason)
        {
            if (!CanAttach(equipment, targetCard, owner, out reason))
                return false;
            PayCost(equipment, equipment != null ? equipment.apCost : 0,
                equipment != null ? equipment.resourceCost : null, owner);
            // Not applied now — the grant is stashed on the card and applied to the spawned
            // UnitData by ArmyActions.DeployUnitFromCard when this card is finally played.
            SetAttachment(targetCard, equipment);
            return true;
        }

        // CardData variants — used by CardHandUI's hand attach flow so a Research/Production-
        // created equipment card is charged its effective (instance) cost: activationApCost and
        // no ResourceCost, since Create already paid it. An ordinary equipment card behaves
        // exactly as the CardDefinition overloads above.
        public static bool TryAttach(CardData equipmentCard, UnitData target, PlayerRoot owner, out string reason)
        {
            CardDefinition equipment = equipmentCard?.Definition;
            if (!CanAttach(equipmentCard, target, owner, out reason))
                return false;
            PayCost(equipment, equipmentCard != null ? equipmentCard.EffectivePlayApCost : 0,
                equipmentCard != null ? equipmentCard.EffectivePlayResourceCost : null, owner);
            Install(equipment, target);
            RefreshLiveHostObservation(target);
            return true;
        }

        public static bool TryAttach(CardData equipmentCard, CardData targetCard, PlayerRoot owner, out string reason)
        {
            CardDefinition equipment = equipmentCard?.Definition;
            if (!CanAttach(equipmentCard, targetCard, owner, out reason))
                return false;
            PayCost(equipment, equipmentCard != null ? equipmentCard.EffectivePlayApCost : 0,
                equipmentCard != null ? equipmentCard.EffectivePlayResourceCost : null, owner);
            SetAttachment(targetCard, equipment);
            return true;
        }

        private static void RefreshLiveHostObservation(UnitData target)
        {
            ArmyData army = ArmyRegistry.FindArmyContaining(target);
            if (army == null)
                return;

            // Equipment can change Recce radius and combat facts. The gameplay owner publishes
            // both consequences once: the owner's visibility footprint is recomputed, while every
            // player already watching the hex refreshes its honest content memory.
            VisionSystem.RecomputeFor(army.Owner);
            VisionSystem.NotifyContentChanged(army.Hex);
        }

        private static void PayCost(CardDefinition equipment, int apCost, ResourceCost resourceCost, PlayerRoot owner)
        {
            owner.SpendActionPoints(apCost);
            resourceCost?.PayFrom(owner);
        }

        // --- effect application --------------------------------------------------------------
        // Also called by ArmyActions.DeployUnitFromCard for equipment that was attached to the
        // card while it was still in hand. Application order matches EquipmentGrant's own doc:
        // clear families -> remove exact -> add -> additive stats -> override stats.
        public static void Apply(EquipmentGrant grant, UnitData unit)
        {
            if (grant == null || unit == null)
                return;

            if (grant.clearAbilityFamilies != null)
                foreach (AbilityFamily family in grant.clearAbilityFamilies)
                {
                    if (family == AbilityFamily.None)
                        continue;
                    unit.Abilities.RemoveWhere(a => IsFamilyMember(a, family));
                }

            if (grant.removeAbilities != null)
                foreach (string tag in grant.removeAbilities)
                    if (!string.IsNullOrEmpty(tag))
                        unit.Abilities.Remove(tag);

            if (grant.addAbilities != null)
                foreach (string tag in grant.addAbilities)
                    if (!string.IsNullOrEmpty(tag))
                        unit.Abilities.Add(tag);

            if (grant.statChanges != null)
            {
                foreach (EquipmentStatChange change in grant.statChanges)
                    if (change != null && !change.isOverride)
                        ApplyStat(unit, change);
                foreach (EquipmentStatChange change in grant.statChanges)
                    if (change != null && change.isOverride)
                        ApplyStat(unit, change);
            }

            // Same parity ArmyActions/HexSelectionController.SpawnUnit enforce: an added
            // RapidReaction zeroes the activation cost outright.
            if (unit.Abilities.Contains(UnitAbilities.RapidReaction))
                unit.ActivationApCost = 0;
        }

        private static void ApplyStat(UnitData unit, EquipmentStatChange change)
        {
            switch (change.stat)
            {
                case EquipmentStat.Attack:
                    unit.Attack = Combine(unit.Attack, change, FloorFor(EquipmentStat.Attack));
                    break;
                case EquipmentStat.Defense:
                    unit.Defense = Combine(unit.Defense, change, FloorFor(EquipmentStat.Defense));
                    break;
                case EquipmentStat.Resistance:
                    unit.Resistance = Combine(unit.Resistance, change, FloorFor(EquipmentStat.Resistance));
                    break;
                case EquipmentStat.Range:
                    unit.Range = Combine(unit.Range, change, FloorFor(EquipmentStat.Range));
                    break;
                case EquipmentStat.Initiative:
                    unit.Initiative = Combine(unit.Initiative, change, FloorFor(EquipmentStat.Initiative));
                    break;
                case EquipmentStat.ActivationApCost:
                    unit.ActivationApCost = Combine(unit.ActivationApCost, change, FloorFor(EquipmentStat.ActivationApCost));
                    break;
                case EquipmentStat.CommandRating:
                    unit.CommandRating = Combine(unit.CommandRating, change, FloorFor(EquipmentStat.CommandRating));
                    break;
                case EquipmentStat.HitPoints:
                {
                    int newMax = Combine(unit.HitPointsMax, change, FloorFor(EquipmentStat.HitPoints));
                    // A permanent buff raises current HP with max; an override that lowers max
                    // clamps current down to it, but never heals a wounded unit past what it had.
                    int delta = newMax - unit.HitPointsMax;
                    unit.HitPointsMax = newMax;
                    unit.HitPointsCurrent = Mathf.Clamp(unit.HitPointsCurrent + Mathf.Max(0, delta), 1, newMax);
                    break;
                }
                case EquipmentStat.MoveMax:
                {
                    int newMove = Combine(unit.MoveMax, change, FloorFor(EquipmentStat.MoveMax));
                    int delta = newMove - unit.MoveMax;
                    unit.MoveMax = newMove;
                    unit.MoveCurrent = Mathf.Clamp(unit.MoveCurrent + Mathf.Max(0, delta), 0, newMove);
                    break;
                }
                case EquipmentStat.Fate:
                {
                    int newFateMax = Combine(unit.FateMax, change, FloorFor(EquipmentStat.Fate));
                    int delta = newFateMax - unit.FateMax;
                    unit.FateMax = newFateMax;
                    unit.Fate = Mathf.Clamp(unit.Fate + Mathf.Max(0, delta), 0, newFateMax);
                    break;
                }
            }
        }

        // isOverride: set to `amount` (floored). Otherwise: add `amount` to `current` (floored).
        private static int Combine(int current, EquipmentStatChange change, int floor)
        {
            int result = change.isOverride ? change.amount : current + change.amount;
            return Mathf.Max(floor, result);
        }

        // The per-stat minimum Combine clamps to — the ONE table, read by ApplyStat above and by
        // Predict below. Extracted from ApplyStat's own former inline literals (2026-08-28 P1,
        // project owner's spec item 16): any evaluator that needs the post-attach value of a stat
        // must get the same floor gameplay applies, without keeping its own copy of this list.
        public static int FloorFor(EquipmentStat stat)
        {
            switch (stat)
            {
                case EquipmentStat.Defense:
                case EquipmentStat.Range:
                case EquipmentStat.Initiative:
                case EquipmentStat.HitPoints:
                case EquipmentStat.MoveMax:
                    return 1;
                default:
                    return 0;
            }
        }

        // The effective host state an EquipmentGrant would produce — the single gameplay-owned
        // "what does this attach actually do" helper (2026-08-28 P1, spec item 16), so callers
        // that must weigh an attach BEFORE committing it (AiManagementPlanner's host ranking, the
        // hand/catalog preview UI) never re-derive Apply's arithmetic themselves and can't drift
        // from it.
        //
        // Replays Apply's exact order without mutating anything: ability clear-families -> remove
        // -> add (via EffectiveAbilities), then additive stat changes, then override stat changes,
        // each Combine-floored by FloorFor, then the same RapidReaction activation-cost parity
        // Apply enforces at the end. `beforeStats` supplies the host's current value for every
        // stat the grant touches; a stat missing from it is treated as 0. `grant` null yields the
        // untouched inputs back.
        public static PredictedEquipmentState Predict(EquipmentGrant grant,
            IReadOnlyDictionary<EquipmentStat, int> beforeStats, IEnumerable<string> beforeAbilities)
        {
            var abilities = EffectiveAbilities(beforeAbilities, grant);
            var stats = new Dictionary<EquipmentStat, int>();

            if (grant?.statChanges != null)
            {
                foreach (EquipmentStatChange change in grant.statChanges)
                {
                    if (change == null || stats.ContainsKey(change.stat))
                        continue;
                    stats[change.stat] = beforeStats != null && beforeStats.TryGetValue(change.stat, out int b) ? b : 0;
                }
                foreach (EquipmentStatChange change in grant.statChanges)
                    if (change != null && !change.isOverride)
                        stats[change.stat] = Combine(stats[change.stat], change, FloorFor(change.stat));
                foreach (EquipmentStatChange change in grant.statChanges)
                    if (change != null && change.isOverride)
                        stats[change.stat] = Combine(stats[change.stat], change, FloorFor(change.stat));
            }

            if (abilities.Contains(UnitAbilities.RapidReaction))
                stats[EquipmentStat.ActivationApCost] = 0;

            return new PredictedEquipmentState(stats, abilities);
        }
    }

    internal readonly struct AttachmentResourceState
    {
        internal readonly int HpSpent, MoveSpent, FateSpent, LastHp, LastMove, LastFate;
        internal AttachmentResourceState(int hpSpent, int moveSpent, int fateSpent, UnitData unit)
        {
            HpSpent = hpSpent; MoveSpent = moveSpent; FateSpent = fateSpent;
            LastHp = unit.HitPointsCurrent; LastMove = unit.MoveCurrent; LastFate = unit.Fate;
        }
    }

    // Return value of EquipmentSystem.Predict — the post-attach snapshot an evaluator scores.
    // Stats holds an after-value only for the stats the grant actually changes; Abilities is the
    // host's full effective tag set once the grant's clear/remove/add have been replayed.
    public readonly struct PredictedEquipmentState
    {
        public readonly IReadOnlyDictionary<EquipmentStat, int> Stats;
        public readonly IReadOnlyList<string> Abilities;

        public PredictedEquipmentState(IReadOnlyDictionary<EquipmentStat, int> stats, IReadOnlyList<string> abilities)
        {
            Stats = stats;
            Abilities = abilities;
        }
    }
}
