using Game.Players;
using UnityEngine;

namespace Game.Map
{
    // Lightweight paired-frame animation local to the flagged-citadel prefab. Renderer colours
    // stay untouched: MapObjectVisual can tint the cloth for its owner while the neutral emblem
    // follows the same fold phase without inheriting the faction colour.
    public sealed class MapFlagAnimator : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer targetRenderer;
        [SerializeField] private Sprite[] frames;
        [SerializeField] private SpriteRenderer logoRenderer;
        // Emblem frames per player-selectable faction, each matching `frames` one-to-one.
        // Each emblem set follows the same seven cloth frames. A missing faction set
        // leaves the emblem blank rather than showing another faction's mark.
        [SerializeField] private Sprite[] logoFrames;
        [SerializeField] private Sprite[] ashenLogoFrames;
        [SerializeField] private Sprite[] vesselsLogoFrames;
        [SerializeField, Min(0.1f)] private float framesPerSecond = 3.5f;
        [SerializeField] private bool randomizePhase = true;

        private float elapsed;
        private Sprite[] activeLogoFrames;

        // Every building-marker spawn/capture path calls this with the owner's resolved faction
        // (Random is already resolved by then — see GameSetupController.ResolveRandomFactions).
        public static void ApplyFaction(Component marker, Faction faction)
        {
            if (marker == null)
                return;
            foreach (MapFlagAnimator flag in marker.GetComponentsInChildren<MapFlagAnimator>(true))
                flag.SetFaction(faction);
        }

        public void SetFaction(Faction faction)
        {
            int frameCount = frames != null ? frames.Length : 0;
            Sprite[] selected = faction switch
            {
                Faction.Ashen => ashenLogoFrames,
                Faction.Vessels => vesselsLogoFrames,
                _ => logoFrames
            };
            activeLogoFrames = selected != null && selected.Length == frameCount ? selected : null;
            ApplyFrame(frameCount);
        }

        private void OnEnable()
        {
            int frameCount = frames != null ? frames.Length : 0;
            elapsed = randomizePhase && frameCount > 0
                ? Random.value * frameCount / Mathf.Max(0.1f, framesPerSecond)
                : 0f;
            ApplyFrame(frameCount);
        }

        private void Update()
        {
            int frameCount = frames != null ? frames.Length : 0;
            if (targetRenderer == null || frameCount == 0)
                return;

            elapsed += Time.deltaTime;
            ApplyFrame(frameCount);
        }

        private void ApplyFrame(int frameCount)
        {
            if (targetRenderer == null || frameCount == 0)
                return;
            int frameIndex = Mathf.FloorToInt(elapsed * Mathf.Max(0.1f, framesPerSecond)) % frameCount;
            targetRenderer.sprite = frames[frameIndex];

            // The neutral emblem follows the exact same phase as the tinted cloth. A second
            // animator would randomize independently and make the printed mark slide over it.
            if (logoRenderer != null)
                logoRenderer.sprite = activeLogoFrames != null ? activeLogoFrames[frameIndex] : null;
        }
    }
}
