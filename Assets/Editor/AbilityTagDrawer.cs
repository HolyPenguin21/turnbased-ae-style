using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Draws each CardDefinition.grantedAbilities entry as a dropdown of every
    // UnitAbilityCatalog.knownAbilities tag, the same "pick from a list" experience
    // UnitTypeTag's real enum gives unitTypeTags — except the choices come from whichever
    // UnitAbilityCatalog asset is in the project, since that list is tuned as data, not code.
    // Falls back to a plain text field if no catalog asset can be found, so grantedAbilities
    // stays editable either way.
    [CustomPropertyDrawer(typeof(AbilityTagAttribute))]
    public class AbilityTagDrawer : PropertyDrawer
    {
        private static UnitAbilityCatalog cachedCatalog;

        private static UnitAbilityCatalog FindCatalog()
        {
            if (cachedCatalog != null)
                return cachedCatalog;

            string[] guids = AssetDatabase.FindAssets("t:UnitAbilityCatalog");
            if (guids.Length > 0)
                cachedCatalog = AssetDatabase.LoadAssetAtPath<UnitAbilityCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));

            return cachedCatalog;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            UnitAbilityCatalog catalog = FindCatalog();
            List<string> tags = catalog?.knownAbilities?
                .Where(entry => entry != null && !string.IsNullOrEmpty(entry.tag))
                .Select(entry => entry.tag)
                .ToList();

            if (tags == null || tags.Count == 0)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            // Keeps an unrecognized/legacy value (typo, or a tag since removed from the
            // catalog) selectable and visible instead of silently overwriting it.
            if (!string.IsNullOrEmpty(property.stringValue) && !tags.Contains(property.stringValue))
                tags.Insert(0, property.stringValue);

            int currentIndex = tags.IndexOf(property.stringValue);
            EditorGUI.BeginProperty(position, label, property);
            int newIndex = EditorGUI.Popup(position, label.text, currentIndex, tags.ToArray());
            if (newIndex >= 0)
                property.stringValue = tags[newIndex];
            EditorGUI.EndProperty();
        }
    }
}
