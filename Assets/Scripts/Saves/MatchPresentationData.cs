using System;
using System.Collections.Generic;
using Game.Cards;
using UnityEngine;

namespace Game.Saves
{
    // Detached JsonUtility wire fields. HexCoord is readonly and is intentionally never a
    // wire field; all optional values use explicit presence flags instead of Nullable<T>.
    [Serializable] internal sealed class MatchPresentationData
    {
        public string CurrentViewerId;
        public bool ObserverMatch;
        public List<MatchVisionData> Vision = new List<MatchVisionData>();
        public List<MatchHumanMemoryData> Memory = new List<MatchHumanMemoryData>();
        public List<MatchFrozenVisualData> Visuals = new List<MatchFrozenVisualData>();
    }
    [Serializable] internal sealed class MatchHexData { public int Q, R; }
    [Serializable] internal sealed class MatchVisionData
    {
        public string PlayerId;
        public List<MatchHexData> Visible = new List<MatchHexData>();
        public List<MatchHexData> Visited = new List<MatchHexData>();
        public List<MatchHexData> EverSeen = new List<MatchHexData>();
    }
    [Serializable] internal sealed class MatchHumanMemoryData
    {
        public string PlayerId;
        public List<MatchHexData> Buildings = new List<MatchHexData>();
        public List<MatchRememberedArmyData> Armies = new List<MatchRememberedArmyData>();
    }
    [Serializable] internal sealed class MatchRememberedArmyData
    {
        public int SourceArmyId;
        public int Q, R;
        public string OwnerId, Name;
        public bool IsGarrison, IsPrison, IsAirfield, IsAirArmy, Activated;
        public bool HasDefenseBonus, HasConstructionDefense, HasCurrentMovement, HasMaxMovement;
        public int DefenseBonus, ConstructionDefense, CurrentMovement, MaxMovement;
        public List<MatchRememberedUnitData> Members = new List<MatchRememberedUnitData>();
    }
    [Serializable] internal sealed class MatchRememberedUnitData
    {
        public int RuntimeId;
        public string Name, OwnerId, CapturedFromId, ArtKey, DetailArtKey, EquipmentKey, MutatorKey;
        public int BerserkStacks, BerserkDefenseLost, DrillAttackBonus, MoveMax, MoveCurrent, ActivationApCost, ApCost;
        public bool HasOriginalCost, HasRepairCost;
        public ResourceCost OriginalCost = new ResourceCost(), RepairCost = new ResourceCost();
        public bool IsHero, IsPrisoner, IsAviation;
        public int CommandRating, Fate, FateMax, Attack, Defense, Range, HitPointsMax, HitPointsCurrent, Row, Initiative, AirfieldCapacity;
        public List<string> Abilities = new List<string>();
        public List<int> TypeTags = new List<int>();
    }
    [Serializable] internal sealed class MatchFrozenVisualData
    {
        public string ViewerId;
        public bool IsArmy;
        public int SourceArmyId, Q, R;
        public bool IsFrozen;
        public float ClickRadius;
        public int HitRenderer = -1;
        public int InnerCircle = -1, ObjectImage = -1, FactionAccent = -1;
        public List<int> AuxiliaryRenderers = new List<int>();
        public List<MatchVisualNodeData> Nodes = new List<MatchVisualNodeData>();
        public List<MatchSpriteRendererData> Renderers = new List<MatchSpriteRendererData>();
    }
    [Serializable] internal sealed class MatchVisualNodeData
    {
        public int Parent;
        public string Name;
        public bool Active;
        public int Layer;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }
    [Serializable] internal sealed class MatchSpriteRendererData
    {
        public int Node;
        public string SpriteKey, MaterialKey;
        public Color Tint;
        public int SortingLayer, SortingOrder;
        public bool Enabled, ControlsVisibility, FlipX, FlipY;
        public int DrawMode, TileMode, MaskInteraction, SortPoint;
        public Vector2 Size;
        public float AdaptiveModeThreshold;
    }
}
