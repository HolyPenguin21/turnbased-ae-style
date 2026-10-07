using System;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // A typed owner over the existing identity and bank token, never a new operation id.
    public sealed class ReservationOwner : IEquatable<ReservationOwner>
    {
        public MissionIntentKey? Operation { get; }
        public string Token { get; }
        private ReservationOwner(MissionIntentKey? operation, string token)
        { Operation = operation; Token = token; }

        public static ReservationOwner ForOperation(MissionIntentKey key) =>
            new ReservationOwner(key, key.Kind == MissionKind.Economy
                ? EconomyMissionPlanner.OwnerKey(StableMissionKey.ForEconomy(
                    (EconomyTaskKind)key.SubKind, key.ObjectiveId, new HexCoord(key.Q, key.R)))
                : key.Kind == MissionKind.Scout || key.Kind == MissionKind.Raid
                    || key.Kind == MissionKind.Attack || key.Kind == MissionKind.ActiveDefence
                    || key.Kind == MissionKind.Development
                    ? key.ToString() // Preserve existing gameplay bank tokens exactly.
                    : FormattableString.Invariant($"Operation:{(int)key.Kind}:{key.SubKind}:{key.ObjectiveId}:{key.Q}:{key.R}:{(int)key.TargetKind}"));
        public static ReservationOwner ForPass(string token) =>
            string.IsNullOrEmpty(token) ? null : new ReservationOwner(null, token);

        public static implicit operator string(ReservationOwner owner) => owner?.Token;
        // Writers must pick ForOperation or ForPass explicitly; only the read-only token
        // projection is implicit, so no raw string can become an owner by accident.
        public bool Equals(ReservationOwner other) => other != null
            && StringComparer.Ordinal.Equals(Token, other.Token);
        public override bool Equals(object obj) => obj is ReservationOwner owner ? Equals(owner)
            : obj is string token && StringComparer.Ordinal.Equals(Token, token);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Token);
        public override string ToString() => Token;
    }
}
