using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Draws each CardDefinition.grantedAbilities entry as a dropdown of every
    // Game.Cards.UnitAbilities.All tag — the single real source of every ability tag in the
    // game (see its own comment) — the same "pick from a list" experience UnitTypeTag's real
    // enum gives unitTypeTags. Reading straight from the code constants themselves, rather than
    // from a data asset's own copy of them, is what makes a stored tag unable to ever drift
    // from what the effect-checking code actually reads (see UnitAbilityCatalog's own comment
    // on the incident — a hand-typed tag with a stray space — this replaced).
    [CustomPropertyDrawer(typeof(AbilityTagAttribute))]
    public class AbilityTagDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            List<string> tags = UnitAbilities.All?.ToList();

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
