using System;

namespace Game.Units
{
    // Unity skips Nullable<int> fields during serialization; this keeps the same int? surface for state snapshots.
    [Serializable]
    public struct SerializableNullableInt : IEquatable<SerializableNullableInt>
    {
        public bool HasValue;
        public int Value;

        public static implicit operator SerializableNullableInt(int? v) => new SerializableNullableInt { HasValue = v.HasValue, Value = v ?? 0 };
        public static implicit operator int?(SerializableNullableInt v) => v.HasValue ? v.Value : (int?)null;

        public bool Equals(SerializableNullableInt other) => HasValue == other.HasValue && (!HasValue || Value == other.Value);
        public override bool Equals(object obj) => obj is SerializableNullableInt other && Equals(other);
        public override int GetHashCode() => HasValue ? Value : -1;
        public static bool operator ==(SerializableNullableInt a, SerializableNullableInt b) => a.Equals(b);
        public static bool operator !=(SerializableNullableInt a, SerializableNullableInt b) => !a.Equals(b);
    }
}
