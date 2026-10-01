#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiAttackPreparationPriorityTests
    {
        private static MissionProposal Prep(float value, bool durable = false) => new MissionProposal
        {
            Kind = MissionKind.Attack, EffectiveValue = value, FromDurableIntent = durable,
            Target = new AttackMissionTarget { Preparation = true, PreparationStep = AttackPreparationStep.MoveHost },
        };

        private static MissionProposal Raid(float value, bool durable = false) => new MissionProposal
        {
            Kind = MissionKind.Raid, EffectiveValue = value, FromDurableIntent = durable,
        };

        [Test]
        public void FreshRaids_RankJustBelowTheFirstPreparationStep_StartedRaidsKeepTheirRank()
        {
            MissionProposal prep = Prep(15f), fresh = Raid(27f), weak = Raid(10f), started = Raid(30f, durable: true);
            var scout = new MissionProposal { Kind = MissionKind.Scout, EffectiveValue = 40f };
            int n = AttackPreparationPriority.Apply(new List<MissionProposal> { prep, fresh, weak, started, scout });

            Assert.That(n, Is.EqualTo(1));
            Assert.That(fresh.EffectiveValue, Is.LessThan(prep.EffectiveValue));
            Assert.That(fresh.EffectiveValue, Is.EqualTo(prep.EffectiveValue - AiConfigV2.allocatorSliceEpsilon).Within(1e-4f));
            Assert.That(weak.EffectiveValue, Is.EqualTo(10f));
            Assert.That(started.EffectiveValue, Is.EqualTo(30f));
            Assert.That(scout.EffectiveValue, Is.EqualTo(40f));
        }

        [Test]
        public void ALivePreparationStep_DoesNotSinkTheRaids()
        {
            MissionProposal liveStep = Prep(0f, durable: true), fresh = Raid(27f);
            Assert.That(AttackPreparationPriority.Apply(new List<MissionProposal> { liveStep, fresh }), Is.Zero);
            Assert.That(fresh.EffectiveValue, Is.EqualTo(27f));
        }
    }
}
#endif
