using System;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // The two kinds of neutral combat opportunity a Raid can pursue. A physical neutral army has a
    // stable ArmyId (which may legitimately be 0 — see ArmyData identity sequencing); an event guard
    // has no ArmyId until it is actually spawned at Explore-time, so it is identified by its stable
    // map hex instead. See Docs/ai-v2-raid-target-unification (RaidTargetRef) for the full rationale.
    public enum RaidTargetKind { NeutralArmy, EventGuard }

    // Single typed identity for a Raid target, replacing the old TargetArmyId/TargetHex pair that
    // required callers to keep two fields in sync by convention. default(RaidTargetRef) means "no
    // target" via HasValue == false — no numeric sentinel (0, -1, ...) is needed or should be
    // introduced for absence.
    public readonly struct RaidTargetRef : IEquatable<RaidTargetRef>
    {
        public bool HasValue { get; }
        public RaidTargetKind Kind { get; }
        public int ArmyId { get; }     // meaningful iff Kind == NeutralArmy; 0 is a legitimate id
        public HexCoord Hex { get; }   // meaningful iff Kind == EventGuard

        private RaidTargetRef(bool hasValue, RaidTargetKind kind, int armyId, HexCoord hex)
        {
            HasValue = hasValue;
            Kind = kind;
            ArmyId = armyId;
            Hex = hex;
        }

        public static RaidTargetRef None => default;

        public static RaidTargetRef ForNeutralArmy(int armyId) =>
            new RaidTargetRef(true, RaidTargetKind.NeutralArmy, armyId, default);

        public static RaidTargetRef ForEventGuard(HexCoord hex) =>
            new RaidTargetRef(true, RaidTargetKind.EventGuard, 0, hex);

        public bool Equals(RaidTargetRef other)
        {
            if (HasValue != other.HasValue) return false;
            if (!HasValue) return true;
            if (Kind != other.Kind) return false;
            return Kind == RaidTargetKind.NeutralArmy ? ArmyId == other.ArmyId : Hex.Equals(other.Hex);
        }

        public override bool Equals(object obj) => obj is RaidTargetRef o && Equals(o);

        public override int GetHashCode()
        {
            if (!HasValue) return 0;
            return Kind == RaidTargetKind.NeutralArmy
                ? ((int)Kind, ArmyId).GetHashCode()
                : ((int)Kind, Hex).GetHashCode();
        }

        public static bool operator ==(RaidTargetRef a, RaidTargetRef b) => a.Equals(b);
        public static bool operator !=(RaidTargetRef a, RaidTargetRef b) => !a.Equals(b);

        public string DiagnosticLabel =>
            !HasValue ? "None" :
            Kind == RaidTargetKind.NeutralArmy ? $"Army#{ArmyId}" : $"Guard@{Hex.Q},{Hex.R}";

        public override string ToString() => DiagnosticLabel;
    }
}
