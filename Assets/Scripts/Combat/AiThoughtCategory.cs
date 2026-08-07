namespace Game.Combat
{
    // Every trigger point BattleAi/BattleScreenUI/BattleAttackPopupUI can fire an AiЕhoughts_Text
    // line from — see BattleAiPhraseBank for the actual phrases per category.
    public enum AiThoughtCategory
    {
        FightDecision,
        RetreatDecision,
        CitadelDefense,
        FinishingBlow,
        PriorityTarget,
        UselessTargetSkip,
        AdvanceMove,
        CautiousWait,
        ForcedAdvance,
        FateSpendWorthIt,
        FateSpendSkip,
        DamageTakenMinor,
        DamageTakenMajor,
        GoodRoll,
        BadRoll,
        UnitDied,
        EnemyKilled,
        BattleWon,
        BattleLost,
        PlayerIdle,
    }
}
