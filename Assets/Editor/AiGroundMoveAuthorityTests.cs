#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiGroundMoveAuthorityTests
    {
        private static bool Authorize(AiGroundMoveAuthority authority,
            bool knownContact, bool knownTakeover, out string reason)
        {
            AiDecision decision = AiDecision.Move(null, default(HexCoord), "test", 0f, authority);
            MethodInfo method = typeof(AiTurnController).GetMethod(
                "IsKnownGroundOutcomeAuthorized",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            object[] args = { decision, knownContact, knownTakeover, null };
            bool allowed = (bool)method.Invoke(null, args);
            reason = args[3] as string;
            return allowed;
        }

        [Test]
        public void MoveDefaultsToTransitAuthority()
        {
            AiDecision decision = AiDecision.Move(null, default(HexCoord), "default", 0f);
            Assert.That(decision.GroundMoveAuthority, Is.EqualTo(AiGroundMoveAuthority.Transit));
            Assert.That(decision.AllowsGroundCombat, Is.False);
            Assert.That(decision.AllowsStructureTakeover, Is.False);
        }

        [TestCase(AiGroundMoveAuthority.Transit, false, false, true)]
        [TestCase(AiGroundMoveAuthority.Transit, true, false, false)]
        [TestCase(AiGroundMoveAuthority.Transit, false, true, false)]
        [TestCase(AiGroundMoveAuthority.Combat, true, false, true)]
        [TestCase(AiGroundMoveAuthority.Combat, false, true, false)]
        [TestCase(AiGroundMoveAuthority.Combat, true, true, false)]
        [TestCase(AiGroundMoveAuthority.CombatAndCapture, true, false, true)]
        [TestCase(AiGroundMoveAuthority.CombatAndCapture, false, true, true)]
        [TestCase(AiGroundMoveAuthority.CombatAndCapture, true, true, true)]
        public void KnownOutcomeGateMatchesTypedAuthority(AiGroundMoveAuthority authority,
            bool knownContact, bool knownTakeover, bool expected)
        {
            bool allowed = Authorize(authority, knownContact, knownTakeover, out string reason);
            Assert.That(allowed, Is.EqualTo(expected));
            Assert.That(string.IsNullOrEmpty(reason), Is.EqualTo(expected),
                "Authorized outcomes have no rejection reason; rejected outcomes must explain why.");
        }

        // ATK §26/§27 — retargeted from the deleted StrategicPressureAdvance to the surviving
        // single policy owner. The rule itself is unchanged and is now the Attack lane's: every
        // approach step of a deliberate structure-capture operation is plain Transit, and only the
        // terminal step INTO the operation's own target may seek a takeover, so an assault can
        // never capture some other structure it happens to walk across.
        [Test]
        public void StructureAssaultOnlyAuthorizesTheTerminalStepIntoItsOwnTarget()
        {
            var target = new HexCoord(9, -4);
            var approach = new HexCoord(8, -4);

            Assert.That(GroundMoveAuthorityPolicy.ForStructureAssaultStep(approach, target),
                Is.EqualTo(AiGroundMoveAuthority.Transit),
                "Approach steps must remain safe Transit and must not gain incidental combat/capture permission.");

            AiGroundMoveAuthority terminal =
                GroundMoveAuthorityPolicy.ForStructureAssaultStep(target, target);
            Assert.That(terminal, Is.EqualTo(AiGroundMoveAuthority.CombatAndCapture),
                "The final step into the operation's own Base/Citadel must be allowed to complete it.");
            Assert.That(Authorize(terminal, knownContact: true, knownTakeover: true,
                out string reason), Is.True, reason);
        }

        [Test]
        public void CombatDoesNotImplicitlyAuthorizeEmptyStructureTakeover()
        {
            Assert.That(Authorize(AiGroundMoveAuthority.Combat,
                knownContact: true, knownTakeover: true, out string reason), Is.False);
            Assert.That(reason, Does.Contain("structure"));
        }
    }
}
#endif
