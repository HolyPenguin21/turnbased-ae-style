using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Draws one RewardEntry as two rows: a `type` popup (Resources/Card), then whichever
    // field actually matters for that type — the `resources` block (Unity's own default
    // multi-field drawer, same as CardDefinition.resourceCost/resourceYield use) for
    // Resources, or a card dropdown (built from whichever EventCatalog asset owns this
    // property — its own `cardCatalogs` list, same "<catalog.displayName>: <card.
    // displayName>" labeling as ArmyUnitEntryDrawer/DeckCardEntryDrawer) for Card.
    [CustomPropertyDrawer(typeof(RewardEntry))]
    public class RewardEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty typeProp = property.FindPropertyRelative("type");
            SerializedProperty resourcesProp = property.FindPropertyRelative("resources");
            SerializedProperty cardKeyProp = property.FindPropertyRelative("cardKey");

            EditorGUI.BeginProperty(position, label, property);

            Rect typeRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.PropertyField(typeRect, typeProp, label);

            Rect valueRect = new Rect(position.x, typeRect.yMax + EditorGUIUtility.standardVerticalSpacing,
                position.width, position.height - typeRect.height - EditorGUIUtility.standardVerticalSpacing);

            var type = (RewardType)typeProp.enumValueIndex;
            if (type == RewardType.Resources)
            {
                EditorGUI.PropertyField(valueRect, resourcesProp, new GUIContent("Resources"), true);
            }
            else
            {
                valueRect.height = EditorGUIUtility.singleLineHeight;
                var catalog = property.serializedObject.targetObject as EventCatalog;
                List<string> keys = BuildCardKeys(catalog);

                if (keys.Count == 0)
                {
                    EditorGUI.PropertyField(valueRect, cardKeyProp, new GUIContent("Card"));
                }
                else
                {
                    if (!string.IsNullOrEmpty(cardKeyProp.stringValue) && !keys.Contains(cardKeyProp.stringValue))
                        keys.Insert(0, cardKeyProp.stringValue);

                    int currentIndex = keys.IndexOf(cardKeyProp.stringValue);
                    int newIndex = EditorGUI.Popup(valueRect, "Card", currentIndex, keys.ToArray());
                    if (newIndex >= 0)
                        cardKeyProp.stringValue = keys[newIndex];
                }
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            SerializedProperty typeProp = property.FindPropertyRelative("type");
            var type = (RewardType)typeProp.enumValueIndex;

            float height = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            if (type == RewardType.Resources)
                height += EditorGUI.GetPropertyHeight(property.FindPropertyRelative("resources"), true);
            else
                height += EditorGUIUtility.singleLineHeight;
            return height;
        }

        private static List<string> BuildCardKeys(EventCatalog catalog)
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
