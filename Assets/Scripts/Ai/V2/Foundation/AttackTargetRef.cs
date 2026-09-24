using System;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ATK §21 — the two kinds of hostile strategic structure an Attack may be mounted against.
    // Metadata, not identity: see AttackTargetRef.Equals below.
    // Facility — a hostile non-Base structure (extractor, lab, factory). Walking onto it
    // undefended DESTROYS it (BuildingRegistry.CaptureOrDestroy), denying the owner its output;
    // it is an objective whether defended or not (AttackObjectiveEvaluator.Enumerate).
    public enum AttackTargetKind { Base, Citadel, Facility }

    // ===========================================================================================
    //  ATK §21 — THE canonical identity of one Attack objective. One object, never a hex field and
    //  an owner field kept in sync by convention on three different structs.
    //
    //  Identity is (hex, expected owner). Capturing "Base@5,3 held by Red" and "Base@5,3 held by
    //  Blue" are genuinely different strategic operations against different opponents, so an
    //  ownership change on the same hex must retire the old objective and admit a fresh one
    //  (§25) rather than silently re-point a running intent at a new enemy.
    //
    //  Kind is deliberately OUTSIDE identity. It describes what memory last saw standing there and
    //  can legitimately be refined by a better observation without the operation becoming a
    //  different operation.
    //
    //  Owner identity is PlayerSetupData.ColorIndex, not GetHashCode() and not Nickname: it is an
    //  int assigned once at setup (GameSetupModel.PickColorIndexForNewPlayer picks an index no
    //  other player holds, and PlayerColorPalette.NeutralColorIndex is reserved), never mutated
    //  afterwards, and already used as the stable player tag by AiV2Trace. A reference hash is not
    //  stable across sessions and a nickname is not guaranteed unique.
    // ===========================================================================================
    public readonly struct AttackTargetRef : IEquatable<AttackTargetRef>
    {
        public bool HasValue { get; }
        public HexCoord Hex { get; }
        public PlayerSetupData ExpectedOwner { get; }
        public AttackTargetKind Kind { get; }

        private AttackTargetRef(bool hasValue, HexCoord hex, PlayerSetupData owner,
            AttackTargetKind kind)
        {
            HasValue = hasValue;
            Hex = hex;
            ExpectedOwner = owner;
            Kind = kind;
        }

        public static AttackTargetRef None => default;

        public static AttackTargetRef For(HexCoord hex, PlayerSetupData expectedOwner,
            AttackTargetKind kind) =>
            expectedOwner == null
                ? None
                : new AttackTargetRef(true, hex, expectedOwner, kind);

        // The stable numeric player identity this target's ownership is pinned to. -1 only for a
        // valueless ref, which never reaches an intent key.
        public int ExpectedOwnerId => HasValue && ExpectedOwner != null ? ExpectedOwner.ColorIndex : -1;

        public bool Equals(AttackTargetRef other)
        {
            if (HasValue != other.HasValue) return false;
            if (!HasValue) return true;
            return Hex.Equals(other.Hex) && ExpectedOwnerId == other.ExpectedOwnerId;
        }

        public override bool Equals(object obj) => obj is AttackTargetRef o && Equals(o);

        public override int GetHashCode() => HasValue ? (Hex, ExpectedOwnerId).GetHashCode() : 0;

        public static bool operator ==(AttackTargetRef a, AttackTargetRef b) => a.Equals(b);
        public static bool operator !=(AttackTargetRef a, AttackTargetRef b) => !a.Equals(b);

        public string DiagnosticLabel => !HasValue
            ? "None"
            : $"{Kind}@{Hex.Q},{Hex.R}#P{ExpectedOwnerId}";

        public override string ToString() => DiagnosticLabel;
    }
}
