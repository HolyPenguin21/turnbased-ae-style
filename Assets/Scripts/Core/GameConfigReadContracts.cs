using System.Collections.Generic;
using Game.Cards;
using Game.Styles;
using UnityEngine;

namespace Game.Core
{
    public interface IStartingDeckContent
    {
        StartingDeckCatalog CollectionDeckCatalog { get; }
    }

    public interface IProgressionContent : IStartingDeckContent
    {
        ResearchProductionCatalog CollectionResearchCatalog { get; }
    }

    internal interface IMatchCardContent : IStartingDeckContent
    {
        IReadOnlyList<CardDefinition> ExtractionFacilityCards { get; }
    }

    public interface IVisionRules
    {
        int ArmyVisionRadius { get; }
        int BuildingVisionRadius { get; }
    }

    public interface IExtractionSiteRules
    {
        int ResourceSiteStructurePoints { get; }
        int ResourceSiteDefense { get; }
        int ResourceSiteFate { get; }
    }

    public interface ICitadelPlacementRules
    {
        int MaxEdgeDistance { get; }
        int MinCityRuinsDistance { get; }
        int CornerExclusionRadius { get; }
    }

    public interface IMoveArrowStyle
    {
        MoveArrowStyle MoveArrowStyle { get; }
    }

    public interface IAbilityText
    {
        string FormatAbilities(IEnumerable<string> abilities);
        string FormatAbilitiesDetailed(IEnumerable<string> abilities);
    }

    internal interface IUnitDetailStyle : IAbilityText
    {
        Color StatBonusColor { get; }
        Color StatPenaltyColor { get; }
    }
}
