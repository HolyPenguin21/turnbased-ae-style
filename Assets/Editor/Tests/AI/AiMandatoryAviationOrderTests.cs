#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Level 2 of the pipeline simplification: the shared choice of the next mandatory aviation
    // work item. The expected values are what the two inline branches in RunTypedAdmissions did.
    public class AiMandatoryAviationOrderTests
    {
        private static List<ArmyData> Wings(params ArmyData[] w) => new List<ArmyData>(w);

        [Test]
        public void NothingPendingSelectsNothing()
        {
            var next = MandatoryAviation.Next(Wings(), Wings());
            Assert.AreEqual(MandatoryAviationKind.None, next.Kind);
            Assert.IsNull(next.Actor);
            Assert.AreEqual(MandatoryAviationKind.None, MandatoryAviation.Next(null, null).Kind);
        }

        [Test]
        public void OneKindAloneIsSelected()
        {
            ArmyData wing = new ArmyData();
            var rebase = MandatoryAviation.Next(Wings(wing), Wings());
            Assert.AreEqual(MandatoryAviationKind.Rebase, rebase.Kind);
            Assert.AreSame(wing, rebase.Actor);
            var recovery = MandatoryAviation.Next(Wings(), Wings(wing));
            Assert.AreEqual(MandatoryAviationKind.Recovery, recovery.Kind);
            Assert.AreSame(wing, recovery.Actor);
        }

        [Test]
        public void TheLowerArmyIdWinsAndARebaseWinsATie()
        {
            ArmyData low = new ArmyData();
            ArmyData high = new ArmyData();
            Assert.AreEqual(MandatoryAviationKind.Rebase,
                MandatoryAviation.Next(Wings(low), Wings(high)).Kind, "rebase has the lower id");
            var recoveryFirst = MandatoryAviation.Next(Wings(high), Wings(low));
            Assert.AreEqual(MandatoryAviationKind.Recovery, recoveryFirst.Kind, "recovery has the lower id");
            Assert.AreSame(low, recoveryFirst.Actor);
            Assert.AreEqual(MandatoryAviationKind.Rebase,
                MandatoryAviation.Next(Wings(low), Wings(low)).Kind, "a tie resumes the rebase");
        }

        [Test]
        public void OnlyTheHeadOfEachListCompetes()
        {
            ArmyData a = new ArmyData();
            ArmyData b = new ArmyData();
            ArmyData c = new ArmyData();
            // rebase head = a, recovery head = b; c (later in the rebase list) never competes.
            var next = MandatoryAviation.Next(Wings(a, c), Wings(b));
            Assert.AreEqual(MandatoryAviationKind.Rebase, next.Kind);
            Assert.AreSame(a, next.Actor);
        }

        [Test]
        public void ASelectionAgreesWithRebaseFirstForEveryHeadPair()
        {
            ArmyData[] pool = { new ArmyData(), new ArmyData(), new ArmyData() };
            foreach (ArmyData r in pool)
                foreach (ArmyData v in pool)
                {
                    bool rebaseFirst = MandatoryAviation.RebaseFirst(r.Id, v.Id);
                    var next = MandatoryAviation.Next(Wings(r), Wings(v));
                    Assert.AreEqual(rebaseFirst ? MandatoryAviationKind.Rebase : MandatoryAviationKind.Recovery,
                        next.Kind);
                    Assert.AreSame(rebaseFirst ? r : v, next.Actor);
                }
        }

        [Test]
        public void TheKindKeepsItsBaselineTriggerPairsAndLabels()
        {
            Assert.AreEqual(1, MandatoryAviation.TriggerPairs(MandatoryAviationKind.Rebase));
            Assert.AreEqual(2, MandatoryAviation.TriggerPairs(MandatoryAviationKind.Recovery));
            Assert.AreEqual("aviation-rebase", MandatoryAviation.Label(MandatoryAviationKind.Rebase));
            Assert.AreEqual("recovery", MandatoryAviation.Label(MandatoryAviationKind.Recovery));
            Assert.AreEqual("aviation rebase #4 could not take a safe step - deferred to next turn, missions continue",
                MandatoryAviation.StallMessage(MandatoryAviationKind.Rebase, 4));
            Assert.AreEqual("recovery #4 made no progress - deferred to next turn, missions continue",
                MandatoryAviation.StallMessage(MandatoryAviationKind.Recovery, 4));
        }
    }
}
#endif
