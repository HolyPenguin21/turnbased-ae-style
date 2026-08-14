using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Draws one DeckCardEntry as a single inspector row: a card dropdown (left, built from
    // whichever StartingDeckCatalog asset owns this property — its own `catalogs` list, labeled
    // "<catalog.displayName>: <card.displayName>") plus a small copy-count field (right). Same
    // "pick from a list" idea as AbilityTagDrawer, just for a whole two-field row instead of one
    // string, since a deck row is "card + how many" rather than a single tag.
    [CustomPropertyDrawer(typeof(DeckCardEntry))]
    public class DeckCardEntryDrawer : PropertyDrawer
    {
        private const float CountFieldWidth = 40f;
        private const float Spacing = 4f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty cardKeyProp = property.FindPropertyRelative("cardKey");
            SerializedProperty countProp = property.FindPropertyRelative("count");

            var catalog = property.serializedObject.targetObject as StartingDeckCatalog;
            List<string> keys = BuildCardKeys(catalog);

            EditorGUI.BeginProperty(position, label, property);

            Rect cardRect = new Rect(position.x, position.y, position.width - CountFieldWidth - Spacing, position.height);
            Rect countRect = new Rect(cardRect.xMax + Spacing, position.y, CountFieldWidth, position.height);

            if (keys.Count == 0)
            {
                EditorGUI.PropertyField(cardRect, cardKeyProp, GUIContent.none);
            }
            else
            {
                // Keeps a stale key (renamed/removed card) selectable and visible instead of
                // silently overwriting it — same fallback AbilityTagDrawer uses.
                if (!string.IsNullOrEmpty(cardKeyProp.stringValue) && !keys.Contains(cardKeyProp.stringValue))
                    keys.Insert(0, cardKeyProp.stringValue);

                int currentIndex = keys.IndexOf(cardKeyProp.stringValue);
                int newIndex = EditorGUI.Popup(cardRect, currentIndex, keys.ToArray());
                if (newIndex >= 0)
                    cardKeyProp.stringValue = keys[newIndex];
            }

            EditorGUI.PropertyField(countRect, countProp, GUIContent.none);

            EditorGUI.EndProperty();
        }

        private static List<string> BuildCardKeys(StartingDeckCatalog catalog)
        {
            var keys = new List<string>();
            if (catalog?.catalogs == null)
                return keys;

            foreach (FactionCardCatalog fc in catalog.catalogs)
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
