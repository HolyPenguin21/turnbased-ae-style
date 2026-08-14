using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Draws an [ArmyTag]-marked string field as a dropdown of every NeutralArmyCatalog.armies
    // name found in the project — same "pick from a list, fall back to free text" pattern as
    // AbilityTagDrawer, just sourced from NeutralArmyCatalog instead of UnitAbilityCatalog.
    [CustomPropertyDrawer(typeof(ArmyTagAttribute))]
    public class ArmyTagDrawer : PropertyDrawer
    {
        private static NeutralArmyCatalog cachedCatalog;

        private static NeutralArmyCatalog FindCatalog()
        {
            if (cachedCatalog != null)
                return cachedCatalog;

            string[] guids = AssetDatabase.FindAssets("t:NeutralArmyCatalog");
            if (guids.Length > 0)
                cachedCatalog = AssetDatabase.LoadAssetAtPath<NeutralArmyCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));

            return cachedCatalog;
        }

        // Sentinel entry mapping to an empty guardArmyName — an event is allowed to have no
        // defending force at all, not just a choice among existing armies.
        private const string NoGuardLabel = "(No Guard)";

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            NeutralArmyCatalog catalog = FindCatalog();
            if (catalog == null)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            List<string> names = catalog.armies?
                .Where(entry => entry != null && !string.IsNullOrEmpty(entry.name))
                .Select(entry => entry.name)
                .ToList() ?? new List<string>();

            // Keeps an unrecognized/legacy value (typo, or an army since removed from the
            // catalog) selectable and visible instead of silently overwriting it.
            if (!string.IsNullOrEmpty(property.stringValue) && !names.Contains(property.stringValue))
                names.Insert(0, property.stringValue);

            names.Insert(0, NoGuardLabel);

            int currentIndex = string.IsNullOrEmpty(property.stringValue) ? 0 : names.IndexOf(property.stringValue);
            EditorGUI.BeginProperty(position, label, property);
            int newIndex = EditorGUI.Popup(position, label.text, currentIndex, names.ToArray());
            if (newIndex >= 0)
                property.stringValue = newIndex == 0 ? string.Empty : names[newIndex];
            EditorGUI.EndProperty();
        }
    }
}
