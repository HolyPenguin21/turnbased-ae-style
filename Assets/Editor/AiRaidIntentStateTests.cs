using Game.Ai.V2;
using NUnit.Framework;

public sealed class AiRaidIntentStateTests
{
    [Test]
    public void PhaseTransition_ClearsReinforcementRequestAge()
    {
        var raid = new RaidIntent();
        raid.Phase = RaidMissionPhase.Reinforcement;
        raid.ReinforcementRequestedTurn = 7;

        raid.Phase = RaidMissionPhase.Assault;

        Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
    }

    [Test]
    public void RepeatedReinforcement_WithoutSupport_StartsFreshRequestCycle()
    {
        var raid = new RaidIntent();
        raid.Phase = RaidMissionPhase.Reinforcement;
        raid.ReinforcementRequestedTurn = 7;

        raid.Phase = RaidMissionPhase.Reinforcement;

        Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
    }

    [Test]
    public void RepeatedReinforcement_WithBoundSupport_PreservesCurrentRequestAge()
    {
        var raid = new RaidIntent();
        raid.Phase = RaidMissionPhase.Reinforcement;
        raid.SupportArmyId = 42;
        raid.ReinforcementRequestedTurn = 7;

        raid.Phase = RaidMissionPhase.Reinforcement;

        Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(7));
    }

    [Test]
    public void SupportReturnTransition_ClearsReinforcementRequestAge()
    {
        var raid = new RaidIntent();
        raid.Phase = RaidMissionPhase.Reinforcement;
        raid.SupportArmyId = 42;
        raid.ReinforcementRequestedTurn = 7;

        raid.Phase = RaidMissionPhase.SupportReturn;

        Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
    }

    [Test]
    public void SupportReturnToReinforcement_CannotReviveOldRequestAge()
    {
        var raid = new RaidIntent();
        raid.Phase = RaidMissionPhase.Reinforcement;
        raid.SupportArmyId = 42;
        raid.ReinforcementRequestedTurn = 7;
        raid.Phase = RaidMissionPhase.SupportReturn;
        raid.SupportArmyId = null;

        raid.Phase = RaidMissionPhase.Reinforcement;

        Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
    }
}
