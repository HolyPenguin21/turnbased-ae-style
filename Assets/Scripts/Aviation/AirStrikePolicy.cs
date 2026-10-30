using UnityEngine;

namespace Game.Aviation
{
    public enum AirStrikePolicyKind { Standard, RaidSupport, DefenceSupport, Transit }

    // A transient policy supplied by the caller that owns the mission. Standard preserves the
    // ordinary endpoint strike exactly; RaidSupport pins one physical target and a survivor floor.
    public readonly struct AirStrikePolicy
    {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal AirStrikePolicy(bool __matchSave, global::Game.Aviation.AirStrikePolicyKind __Kind, int? __ExactTargetArmyId, int __MinimumSurvivors)
        {
            Kind = __Kind;
            ExactTargetArmyId = __ExactTargetArmyId;
            MinimumSurvivors = __MinimumSurvivors;
        }

        public readonly AirStrikePolicyKind Kind;
        public readonly int? ExactTargetArmyId;
        public readonly int MinimumSurvivors;

        public AirStrikePolicy(AirStrikePolicyKind kind, int? exactTargetArmyId = null,
            int minimumSurvivors = 0)
        {
            Kind = kind;
            ExactTargetArmyId = exactTargetArmyId;
            MinimumSurvivors = Mathf.Max(0, minimumSurvivors);
        }

        public bool AllowsStrike => Kind != AirStrikePolicyKind.Transit;
        public static AirStrikePolicy Transit => new AirStrikePolicy(AirStrikePolicyKind.Transit);
        public static AirStrikePolicy Standard => new AirStrikePolicy(AirStrikePolicyKind.Standard);
        public static AirStrikePolicy RaidSupport(int targetArmyId) =>
            new AirStrikePolicy(AirStrikePolicyKind.RaidSupport, targetArmyId, 1);
        // ActiveDefence: the one threatening army the defence answers, struck to destruction.
        public static AirStrikePolicy DefenceSupport(int targetArmyId) =>
            new AirStrikePolicy(AirStrikePolicyKind.DefenceSupport, targetArmyId, 0);
    }

}
