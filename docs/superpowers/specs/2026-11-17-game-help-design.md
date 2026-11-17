# In-game help

The existing Button_Help opens a separate GameHelpBlockingRoot, a sibling of
GameMenuBlockingRoot under GameMenuUI. Both use the existing menu owner for input,
CanvasGroup snapshots, AI observer hold and cleanup. They cannot be open together.
Help has a 30/70 layout with independently masked, vertically scrolling topic and
article columns, a persistent close button and Escape. Layout/templates are authored
in the scene; runtime code only instantiates content and selects topics.

A separate ScriptableObject catalog owns ordered sections/topics with stable IDs,
localization keys and English fallback strings (the current UI language). Text/image
blocks preserve article order. A text resolver can later bridge the chosen localization
package without changing IDs or controller behavior. Russian titles and coverage
sources are submitted in Docs/GameHelp_Topics_Validation.md. Articles remain explicitly
pending until the owner validates the headings; no invented gameplay rules are shipped.

Implementation choice: authored scene + catalog, rather than generating the whole UI
at runtime (harder to restyle) or hardcoding article strings in a controller (translation
and content changes would require code changes). Existing packages stay unchanged.
