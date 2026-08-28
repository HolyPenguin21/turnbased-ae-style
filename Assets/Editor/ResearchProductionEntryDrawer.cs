using System.Collections.Generic;
using Game.Cards;
using Game.Players;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Draws one ResearchProductionEntry as a single inspector row: a card dropdown on the left
    // (built from whichever ResearchProductionCatalog asset owns this property — its own
    // `cardCatalogs` list, labeled "<catalog.displayName>/<card.displayName>") plus a faction
    // popup on the right. Same "pick from a list instead of typing a key" idea as
    // DeckCardEntryDrawer, just with a faction enum in place of the copy-count field.
    [CustomPropertyDrawer(typeof(ResearchProductionEntry))]
    public class ResearchProductionEntryDrawer : PropertyDrawer
    {
        private const float FactionFieldWidth = 110f;
        private const float Spacing = 4f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty cardKeyProp = property.FindPropertyRelative("cardKey");
            SerializedProperty factionProp = property.FindPropertyRelative("factionRestriction");

            var catalog = property.serializedObject.targetObject as ResearchProductionCatalog;
            List<string> keys = BuildCardKeys(catalog);

            EditorGUI.BeginProperty(position, label, property);

            Rect cardRect = new Rect(position.x, position.y,
                position.width - FactionFieldWidth - Spacing, position.height);
            Rect factionRect = new Rect(cardRect.xMax + Spacing, position.y, FactionFieldWidth, position.height);

            if (keys.Count == 0)
            {
                EditorGUI.PropertyField(cardRect, cardKeyProp, GUIContent.none);
            }
            else
            {
                // Keeps a stale key (renamed/removed card) selectable and visible instead of
                // silently overwriting it — same fallback DeckCardEntryDrawer uses.
                if (!string.IsNullOrEmpty(cardKeyProp.stringValue) && !keys.Contains(cardKeyProp.stringValue))
                    keys.Insert(0, cardKeyProp.stringValue);

                int currentIndex = keys.IndexOf(cardKeyProp.stringValue);
                int newIndex = EditorGUI.Popup(cardRect, currentIndex, keys.ToArray());
                if (newIndex >= 0)
                    cardKeyProp.stringValue = keys[newIndex];
            }

            EditorGUI.PropertyField(factionRect, factionProp, GUIContent.none);

            EditorGUI.EndProperty();
        }

        private static List<string> BuildCardKeys(ResearchProductionCatalog catalog)
        {
            var keys = new List<string>();
            if (catalog?.cardCatalogs == null)
                return keys;

            foreach (FactionCardCatalog fc in catalog.cardCatalogs)
            {
                if (fc == null || fc.cards == null)
                    continue;
                foreach (CardDefinition card in fc.cards)
                {
                    if (card == null || string.IsNullOrEmpty(card.displayName))
                        continue;
                    keys.Add($"{fc.displayName}/{card.displayName}");
                }
            }
            return keys;
        }
    }
}
