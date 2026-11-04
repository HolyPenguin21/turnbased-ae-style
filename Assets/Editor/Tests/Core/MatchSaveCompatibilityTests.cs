#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Saves;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Saves survive balance and art patches but not structure changes (project owner,
    // 2026-11-04): the format hash covers DTO fields and enum numbers, and a loaded body
    // re-derives its authored part from the current card through EquipmentSystem.
    public sealed class MatchSaveCompatibilityTests
    {
        [Test]
        public void FormatHashDescribesFieldsAndEnumNumbersOfTheWholeFile()
        {
            string text = MatchSaveSchema.Describe(typeof(MatchSaveEnvelope), typeof(MatchSaveData));
            Assert.That(text, Does.Contain("type Game.Saves.MatchUnitData{"));
            Assert.That(text, Does.Contain("source:System.String;"));
            Assert.That(text, Does.Contain("typeTags:list<" + typeof(UnitTypeTag).FullName + ">;"));
            Assert.That(text, Does.Contain("enum " + typeof(BattleRow).FullName + ":Int32;"));
            foreach (string name in System.Enum.GetNames(typeof(BattleRow)))
                Assert.That(text, Does.Contain(";" + name + "=" + (int)System.Enum.Parse(typeof(BattleRow), name)));
            Assert.That(MatchSaveSchema.Hash, Is.EqualTo(MatchSaveSchema.Compute(typeof(MatchSaveEnvelope), typeof(MatchSaveData))));
            Assert.That(MatchSaveSchema.Hash, Has.Length.EqualTo(64));
            Assert.That(MatchSaveSchema.Compute(typeof(MatchSaveEnvelope)), Is.Not.EqualTo(MatchSaveSchema.Hash));
        }

        // Mirrors UnitData.Materialize for a hand-built body (Materialize itself rolls a
        // repair cost through the native random generator).
        private static UnitData Spawned(CardDefinition host)
        {
            var body = AttachmentSlotTests.Body(host);
            if (body.Abilities.Contains(UnitAbilities.RapidReaction)) body.ActivationApCost = 0;
            body.CatalogSource = host;
            EquipmentSystem.InitializeBaseline(body);
            return body;
        }

        private static object[] State(UnitData u) => new object[]
        {
            u.Attack, u.Defense, u.Range, u.Initiative, u.ActivationApCost, u.CommandRating,
            u.HitPointsMax, u.HitPointsCurrent, u.FateMax, u.Fate, u.PermanentMoveMax, u.MovementSpent,
            u.BerserkDefenseLost, string.Join(",", u.Abilities.OrderBy(a => a, System.StringComparer.Ordinal)),
            Describe(u.AttachmentBase), Describe(u.AttachmentApplied),
            u.AttachmentResources.HasValue ? u.AttachmentResources.Value.HpSpent + "/" + u.AttachmentResources.Value.FateSpent
                + "/" + u.AttachmentResources.Value.MoveSpent : "none",
        };

        private static string Describe(PredictedEquipmentState? state) => !state.HasValue ? "none"
            : string.Join(",", state.Value.Stats.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))
              + "|" + string.Join(",", state.Value.Abilities.OrderBy(a => a, System.StringComparer.Ordinal));

        [Test]
        public void UnchangedContentRebaseReproducesEveryPublishedBodyExactly()
        {
            var items = AttachmentContentTestData.Blocks(AttachmentContentTestData.NeutralPath)
                .Select(AttachmentContentTestData.Read).Where(c => c.cardType == CardType.Equipment
                    && c.equipment.hostKinds.Any(k => k == EquipmentHostKind.Unit || k == EquipmentHostKind.Hero)).ToArray();
            var hosts = new[] { "IronConcord", "TheAshen", "TheVessels" }
                .SelectMany(f => AttachmentContentTestData.Blocks($"Assets/Cards/{f}/CardCatalog_{f}.asset"))
                .Select(AttachmentContentTestData.Read)
                .Where(c => c.cardType == CardType.Unit || c.cardType == CardType.Hero).ToArray();
            int cases = 0;
            foreach (var host in hosts)
            {
                var equipment = new CardDefinition[] { null }.Concat(items.Where(i =>
                    i.attachmentSlot == AttachmentSlot.Equipment && EquipmentSystem.FitsHost(i, host, out _)));
                var mutators = new CardDefinition[] { null }.Concat(items.Where(i =>
                    i.attachmentSlot == AttachmentSlot.Mutator && EquipmentSystem.FitsHost(i, host, out _))).Take(3);
                foreach (var gear in equipment)
                foreach (var mutator in mutators)
                foreach (bool consume in new[] { false, true })
                {
                    if (consume && !EquipmentSystem.IsSingleUse(gear)) continue;
                    var body = Spawned(host);
                    if (host.hitPoints > 1) body.HitPointsCurrent -= 1;
                    if (host.fate > 0) body.Fate -= 1;
                    if (host.moveMax > 0) body.RestoreMovementSpent(1);
                    EquipmentSystem.ApplyAttachments(body, gear, mutator);
                    if (consume) EquipmentSystem.ConsumeAfterAttack(body);
                    var before = State(body);
                    body.RebaseOnCatalog();
                    Assert.That(State(body), Is.EqualTo(before), $"{host.authoredKey}/{gear?.authoredKey}/{mutator?.authoredKey}/{consume}");
                    cases++;
                }
            }
            Assert.That(cases, Is.GreaterThan(hosts.Length));
        }

        [Test]
        public void PatchedCardReachesTheLiveBodyThroughItsAttachmentsAndKeepsMatchState()
        {
            var host = AttachmentSlotTests.Host();
            var gear = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Attack, 2);
            var body = Spawned(host);
            body.HitPointsCurrent = 6; body.Fate = 1; body.RestoreMovementSpent(2);
            EquipmentSystem.ApplyAttachments(body, gear, null);
            body.Attack += 1; body.DrillAttackBonus = 1; // a temporary battle delta

            host.attack = 8; host.hitPoints = 14; host.fate = 2; host.moveMax = 9;
            host.grantedAbilities.Add(UnitAbilities.Counterattack);
            host.displayName = "Patched name";
            body.RebaseOnCatalog();

            Assert.That(body.Attack, Is.EqualTo(8 + 2 + 1), "card + equipment + temporary delta");
            Assert.That(body.HitPointsMax, Is.EqualTo(14));
            Assert.That(body.HitPointsCurrent, Is.EqualTo(6), "current HP keeps its saved value");
            Assert.That(body.FateMax, Is.EqualTo(2));
            Assert.That(body.Fate, Is.EqualTo(1));
            Assert.That(body.PermanentMoveMax, Is.EqualTo(9));
            Assert.That(body.MovementSpent, Is.EqualTo(2));
            Assert.That(body.Abilities, Has.Member(UnitAbilities.Counterattack));
            Assert.That(body.Name, Is.EqualTo("Patched name"));

            host.hitPoints = 4; host.fate = 0;
            body.RebaseOnCatalog();
            Assert.That(body.HitPointsCurrent, Is.EqualTo(4), "a lowered maximum caps the kept value");
            Assert.That(body.Fate, Is.EqualTo(0));
        }

        [Test]
        public void RaisedMaximumDoesNotHealTheBodyAtItsNextAttachmentChange()
        {
            var host = AttachmentSlotTests.Host();
            var gear = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.HitPoints, 5, replace: true);
            gear.grantedAbilities.Add(UnitAbilities.SingleUse);
            var body = Spawned(host);
            body.HitPointsCurrent = 7; // 3 damage taken before the override clamped current to 5
            EquipmentSystem.ApplyAttachments(body, gear, null);
            var twin = Spawned(host);
            twin.HitPointsCurrent = 7;
            EquipmentSystem.ApplyAttachments(twin, gear, null);

            body.RebaseOnCatalog(); // unchanged content
            EquipmentSystem.ConsumeAfterAttack(body); EquipmentSystem.ConsumeAfterAttack(twin);
            Assert.That(body.HitPointsCurrent, Is.EqualTo(twin.HitPointsCurrent), "the clamp history survives a rebase");

            host.hitPoints = 12; // patch: +2 maximum while current is kept
            body.RebaseOnCatalog();
            int current = body.HitPointsCurrent;
            EquipmentSystem.ApplyAttachments(body, null, AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.Attack, 1));
            Assert.That(body.HitPointsCurrent, Is.EqualTo(current), "the raise is consumption, not a pending heal");
        }

        [Test]
        public void UncataloguedBodyKeepsItsSnapshot()
        {
            var host = AttachmentSlotTests.Host();
            var body = Spawned(host); body.CatalogSource = null;
            var before = State(body);
            host.attack = 99;
            body.RebaseOnCatalog();
            Assert.That(State(body), Is.EqualTo(before));
        }
    }
}
#endif
