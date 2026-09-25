// Compile-check stubs only (never part of the Unity project).
namespace TMPro
{
    public enum TextAlignmentOptions { Left, Center, Right, TopLeft, Top, TopRight, MidlineLeft, Midline, MidlineRight, BottomLeft, Bottom, BottomRight, Justified, Flush, CenterGeoAligned, Capline, Baseline, Converted, Midline_Left }
    public enum TextWrappingModes { NoWrap, Normal, PreserveWhitespace, PreserveWhitespaceNoWrap }
    public enum TextOverflowModes { Overflow, Ellipsis, Masking, Truncate, ScrollRect, Page, Linked }
    public enum FontStyles { Normal, Bold, Italic, Underline }
    public class TMP_FontAsset : UnityEngine.ScriptableObject { }
    public class TMP_CharacterInfo { public bool isVisible; public int index; public UnityEngine.Vector3 bottomLeft, topRight, topLeft, bottomRight; }
    public class TMP_TextInfo { public int characterCount; public TMP_CharacterInfo[] characterInfo = new TMP_CharacterInfo[0]; public int lineCount; }
    public class TMP_Text : UnityEngine.UI.MaskableGraphic
    {
        public virtual string text { get; set; }
        public float fontSize { get; set; }
        public TMP_FontAsset font { get; set; }
        public TextAlignmentOptions alignment { get; set; }
        public TextWrappingModes textWrappingMode { get; set; }
        public bool enableWordWrapping { get; set; }
        public TextOverflowModes overflowMode { get; set; }
        public FontStyles fontStyle { get; set; }
        public bool richText { get; set; }
        public int maxVisibleCharacters { get; set; }
        public TMP_TextInfo textInfo { get; }
        public float preferredHeight { get; }
        public float preferredWidth { get; }
        public bool enableAutoSizing { get; set; }
        public float fontSizeMin { get; set; }
        public float fontSizeMax { get; set; }
        public UnityEngine.Vector4 margin { get; set; }
        public void ForceMeshUpdate(bool a = false, bool b = false) { }
        public void SetText(string s) { }
        public UnityEngine.Vector2 GetPreferredValues(string s) => default;
        public UnityEngine.Vector2 GetPreferredValues(string s, float w, float h) => default;
    }
    public class TextMeshProUGUI : TMP_Text { }
    public class TextMeshPro : TMP_Text { public UnityEngine.Renderer renderer { get; } }
    public class TMP_Settings { public static TMP_FontAsset defaultFontAsset; }
    public class TMP_Dropdown : UnityEngine.UI.Selectable
    {
        public class OptionData { public string text; public OptionData() { } public OptionData(string t) { text = t; } }
        public class DropdownEvent : UnityEngine.Events.UnityEvent<int> { }
        public System.Collections.Generic.List<OptionData> options { get; set; } = new System.Collections.Generic.List<OptionData>();
        public int value { get; set; }
        public DropdownEvent onValueChanged { get; set; } = new DropdownEvent();
        public TMP_Text captionText { get; set; }
        public void ClearOptions() { }
        public void AddOptions(System.Collections.Generic.List<string> o) { }
        public void AddOptions(System.Collections.Generic.List<OptionData> o) { }
        public void RefreshShownValue() { }
        public void SetValueWithoutNotify(int v) { }
    }
    public class TMP_InputField : UnityEngine.UI.Selectable
    {
        public class SubmitEvent : UnityEngine.Events.UnityEvent<string> { }
        public class OnChangeEvent : UnityEngine.Events.UnityEvent<string> { }
        public string text { get; set; }
        public int characterLimit { get; set; }
        public SubmitEvent onEndEdit { get; set; } = new SubmitEvent();
        public SubmitEvent onSubmit { get; set; } = new SubmitEvent();
        public OnChangeEvent onValueChanged { get; set; } = new OnChangeEvent();
        public TMP_Text textComponent { get; set; }
        public UnityEngine.UI.Graphic placeholder { get; set; }
        public void SetTextWithoutNotify(string s) { }
        public void ActivateInputField() { }
    }
}
namespace UnityEngine.InputSystem
{
    public class ButtonControl { public bool wasPressedThisFrame; public bool isPressed; public bool wasReleasedThisFrame; }
    public class Vector2Control { public UnityEngine.Vector2 ReadValue() => default; }
    public class Keyboard { public static Keyboard current; public ButtonControl wKey, aKey, sKey, dKey, upArrowKey, downArrowKey, leftArrowKey, rightArrowKey, enterKey, escapeKey, numpadEnterKey, spaceKey, tabKey, leftShiftKey, leftCtrlKey;
        public ButtonControl this[Key k] => null; }
    public enum Key { None, Space, Enter, Escape, Tab, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Digit0, LeftShift, RightShift, LeftCtrl, UpArrow, DownArrow, LeftArrow, RightArrow }
    public class Mouse { public static Mouse current; public ButtonControl leftButton, rightButton, middleButton; public Vector2Control position; public Vector2Control scroll; }
}
namespace UnityEditor
{
    public static class EditorApplication { public static bool isPlaying; public static void ExitPlaymode() { } }
    public class SerializedProperty { public string propertyPath, name, displayName; public int intValue; public float floatValue; public bool boolValue; public string stringValue; public int enumValueIndex; public string[] enumNames; public string[] enumDisplayNames; public UnityEngine.Object objectReferenceValue; public SerializedPropertyType propertyType;
        public SerializedProperty FindPropertyRelative(string n) => null; public bool isExpanded; public int arraySize; public SerializedProperty GetArrayElementAtIndex(int i) => null; }
    public enum SerializedPropertyType { Integer, Boolean, Float, String, Enum, ObjectReference }
    public class PropertyDrawer { public virtual void OnGUI(UnityEngine.Rect r, SerializedProperty p, UnityEngine.GUIContent l) { } public virtual float GetPropertyHeight(SerializedProperty p, UnityEngine.GUIContent l) => 0; }
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    public class CustomPropertyDrawer : System.Attribute { public CustomPropertyDrawer(System.Type t, bool b = false) { } }
    public static class EditorGUI { public static void PropertyField(UnityEngine.Rect r, SerializedProperty p, UnityEngine.GUIContent l, bool c = false) { } public static float GetPropertyHeight(SerializedProperty p, UnityEngine.GUIContent l = null, bool c = false) => 0;
        public static void BeginProperty(UnityEngine.Rect r, UnityEngine.GUIContent l, SerializedProperty p) { } public static void EndProperty() { } public static int Popup(UnityEngine.Rect r, string l, int i, string[] o) => 0; public static int Popup(UnityEngine.Rect r, int i, string[] o) => 0; public static void LabelField(UnityEngine.Rect r, string s) { } public static int IntField(UnityEngine.Rect r, string l, int v) => v; public static int IntField(UnityEngine.Rect r, int v) => v; public static int indentLevel; }
    public static class EditorGUIUtility { public static float singleLineHeight; public static float standardVerticalSpacing; public static float labelWidth; }
}
