using UnityEngine;

namespace Game.Map
{
    // Lightweight frame animation local to the flagged-citadel prefab. SpriteRenderer colour
    // is deliberately untouched, so MapObjectVisual.SetColor can keep the cloth in its owner's
    // colour while only the grayscale fold frame changes.
    public sealed class MapFlagAnimator : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer targetRenderer;
        [SerializeField] private Sprite[] frames;
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
        }
    }
}
