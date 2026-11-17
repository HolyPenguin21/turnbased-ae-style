using System;
using UnityEngine;

namespace Game.UI
{
    [Serializable]
    public sealed class HelpText
    {
        public string key;
        [TextArea] public string fallback;

        // A missing translation always falls back to the authored text, never the internal key.
        public string Resolve(Func<string, string> resolver)
        {
            string translated = !string.IsNullOrEmpty(key) ? resolver?.Invoke(key) : null;
            return string.IsNullOrEmpty(translated) ? fallback ?? string.Empty : translated;
        }
    }

    public enum HelpBlockKind { Text = 0, Image = 1, Heading = 2 }

    [Serializable]
    public sealed class HelpArticleBlock
    {
        public string id;
        public HelpBlockKind kind;
        public HelpText text = new HelpText();
        public Sprite image;
    }

    [Serializable]
    public sealed class HelpTopic
    {
        public string id;
        public HelpText title = new HelpText();
        public HelpArticleBlock[] blocks = Array.Empty<HelpArticleBlock>();
    }

    [Serializable]
    public sealed class HelpSection
    {
        public string id;
        public HelpText title = new HelpText();
        public HelpTopic[] topics = Array.Empty<HelpTopic>();
    }

    // Stable topic/block IDs do not depend on the language, display order or title spelling.
    // Later localization integrates through GameHelpPanelUI.SetTextResolver; no package dependency.
    [CreateAssetMenu(fileName = "GameHelpCatalog", menuName = "Game/Help Catalog")]
    public sealed class GameHelpCatalog : ScriptableObject
    {
        public HelpText windowTitle = new HelpText();
        public HelpText closeLabel = new HelpText();
        public HelpText pendingArticle = new HelpText();
        public HelpSection[] sections = Array.Empty<HelpSection>();
    }
}
