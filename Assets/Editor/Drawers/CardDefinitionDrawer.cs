using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Labels each CardDefinition row in a FactionCardCatalog's `cards` list with its own
    // displayName instead of Unity's default "Element N" — same "pick a better label, keep
    // everything else default" idea as ArmyDefinitionDrawer/EventDefinitionDrawer. Draws the
    // foldout itself (not via a recursive PropertyField, which would re-enter this same
    // drawer) then walks the visible children so every field/header still renders normally.
    [CustomPropertyDrawer(typeof(Game.Cards.CardDefinition))]
    public class CardDefinitionDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty nameProp = property.FindPropertyRelative("displayName");
            GUIContent foldoutLabel = new GUIContent(
                !string.IsNullOrEmpty(nameProp?.stringValue) ? nameProp.stringValue : label.text);

            EditorGUI.BeginProperty(position, foldoutLabel, property);

            Rect foldoutRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, foldoutLabel, true);

            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                float y = foldoutRect.yMax + EditorGUIUtility.standardVerticalSpacing;
                SerializedProperty end = property.GetEndProperty();
                SerializedProperty child = property.Copy();
                child.NextVisible(true);
                while (!SerializedProperty.EqualContents(child, end))
                {
                    float height = EditorGUI.GetPropertyHeight(child, true);
                    EditorGUI.PropertyField(new Rect(position.x, y, position.width, height), child, true);
                    y += height + EditorGUIUtility.standardVerticalSpacing;
                    if (!child.NextVisible(false))
                        break;
                }
                EditorGUI.indentLevel--;
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded)
            {
                SerializedProperty end = property.GetEndProperty();
                SerializedProperty child = property.Copy();
                child.NextVisible(true);
                while (!SerializedProperty.EqualContents(child, end))
                {
                    height += EditorGUI.GetPropertyHeight(child, true) + EditorGUIUtility.standardVerticalSpacing;
                    if (!child.NextVisible(false))
                        break;
                }
            }
            return height;
        }
    }
}
