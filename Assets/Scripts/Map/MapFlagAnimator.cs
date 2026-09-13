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
        [SerializeField] private Sprite[] logoFrames;
        [SerializeField, Min(0.1f)] private float framesPerSecond = 3.5f;
        [SerializeField] private bool randomizePhase = true;

        private float elapsed;

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
            if (logoRenderer != null && logoFrames != null && logoFrames.Length == frameCount)
                logoRenderer.sprite = logoFrames[frameIndex];
        }
    }
}
