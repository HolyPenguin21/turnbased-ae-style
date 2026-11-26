using System;
using Game.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Sprites;

namespace Game.Presentation
{
    // Owns the lifetime of a detached card visual on either the map or the battle canvas.
    public sealed class CardDissolveEffect : MonoBehaviour
    {
        public const string ShaderKey = "Effects/CardPlacementDissolve";
        public const float Duration = 0.75f;
        private static readonly int ArtUVRectId = Shader.PropertyToID("_ArtUVRect");
        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");

        private Material _material;
        private Func<bool> _canShow;
        private double _started;

        public static Material CreateMaterial(Sprite art, Color tint, bool overlay = false)
        {
            if (art == null) return null;
            var shader = GameAssetCatalog.Load<Shader>(ShaderKey);
            if (shader == null || !shader.isSupported) return null;
            Vector4 uv = DataUtility.GetOuterUV(art);
            if (uv.z <= uv.x || uv.w <= uv.y) return null;

            var material = new Material(shader);
            material.SetVector(ArtUVRectId, new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y));
            material.SetColor(TintId, tint);
            if (overlay) material.SetInt(ZTestId, (int)CompareFunction.Always);
            return material;
        }

        public static void Play(GameObject visual, Material material, Func<bool> canShow = null)
        {
            var effect = visual.AddComponent<CardDissolveEffect>();
            effect._material = material;
            effect._canShow = canShow;
            effect._started = Time.unscaledTimeAsDouble;
        }

        private void Update()
        {
            float progress = (float)((Time.unscaledTimeAsDouble - _started) / Duration);
            if (progress >= 1f || _material == null || _canShow?.Invoke() == false)
            {
                Destroy(gameObject);
                return;
            }

            _material.SetFloat(ProgressId, progress);
        }

        private void ReleaseMaterial()
        {
            var material = _material;
            _material = null;
            _canShow = null;
            UnityObjectLifetime.Destroy(material);
        }

        private void OnDisable()
        {
            if (_material == null) return;
            ReleaseMaterial();
            if (Application.isPlaying) Destroy(gameObject);
        }

        private void OnDestroy() => ReleaseMaterial();
    }
}
