using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One atlas mesh per resolved neighbour, with proper adjacent-frame alpha blending.
    public sealed class BattleDamageFlipbook : MaskableGraphic
    {
        private const string ShaderPath = "Shaders/BattleSkillFlipbook";
        private static Texture2D _fireAtlas, _splashAtlas;
        private static Material _blendMaterial;
        private static bool _warned;
        private RectTransform _sourceCard, _targetCard;
        private Texture2D _atlas;
        private bool _fire, _sourcePulse, _ready;
        private float _started;
        public override Texture mainTexture => _atlas != null ? _atlas : Texture2D.whiteTexture;

        public static BattleDamageFlipbook Show(RectTransform parent, RectTransform source,
            RectTransform target, bool fire, bool sourcePulse = true)
        {
            if (parent == null || source == null || target == null) return null;
            var go = new GameObject(fire ? "ScorcherFlipbook" : "SplashFlipbook", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(LayoutElement), typeof(BattleDamageFlipbook));
            Stretch((RectTransform)go.transform, parent);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var effect = go.GetComponent<BattleDamageFlipbook>();
            effect._sourceCard = source; effect._targetCard = target;
            effect._fire = fire; effect._sourcePulse = sourcePulse;
            effect.raycastTarget = false; effect.maskable = false;
            effect._started = Time.unscaledTime;
            var canvas = parent.GetComponentInParent<Canvas>();
            if (canvas != null) canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
            var atlas = fire ? _fireAtlas : _splashAtlas;
            if (atlas == null) atlas = GameAssetCatalog.Load<Texture2D>(fire ? BattleSkillFlipbook.FirePath : BattleSkillFlipbook.SplashPath);
            if (fire) _fireAtlas = atlas; else _splashAtlas = atlas;
            if (_blendMaterial == null)
            {
                var shader = GameAssetCatalog.Load<Shader>(ShaderPath);
                if (shader != null) _blendMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            int rows = BattleSkillFlipbook.Rows(fire);
            effect._atlas = atlas;
            effect._ready = atlas != null && _blendMaterial != null && atlas.width % 4 == 0
                && atlas.height % rows == 0 && atlas.width / 4 == atlas.height / rows;
            if (effect._ready) effect.material = _blendMaterial;
            else if (!_warned)
            {
                _warned = true;
                Debug.LogWarning("Battle flipbook assets are missing or have the wrong grid. Import the approved v2 PNGs into Assets/Textures/UI/Effects/Flipbooks/ and let the editor register them.");
            }
            effect.SetMaterialDirty(); effect.SetVerticesDirty();
            return effect;
        }

        internal static void Stretch(RectTransform rect, RectTransform parent)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (!_ready || _sourceCard == null || _targetCard == null) return;
            float elapsed = Time.unscaledTime - _started;
            var sample = BattleSkillFlipbook.Sample(elapsed, _fire);
            if (sample.Frame < 0) return;
            // Canvas layout may change after Update: measure endpoints at mesh rebuild time.
            Vector2 source = rectTransform.InverseTransformPoint(_sourceCard.TransformPoint(_sourceCard.rect.center));
            Vector2 target = rectTransform.InverseTransformPoint(_targetCard.TransformPoint(_targetCard.rect.center));
            Vector2 top = rectTransform.InverseTransformPoint(_sourceCard.TransformPoint(
                _sourceCard.rect.center + Vector2.up * _sourceCard.rect.height));
            Vector2 right = rectTransform.InverseTransformPoint(_sourceCard.TransformPoint(
                _sourceCard.rect.center + Vector2.right * _sourceCard.rect.width));
            float height = Mathf.Max(1f, (top-source).magnitude), width = Mathf.Max(1f, (right-source).magnitude);
            Vector2 delta = target-source;
            if (_fire && Mathf.Abs(delta.y) > Mathf.Abs(delta.x))
            {
                // Vertical neighbours use an upright source flare, then target engulf from below.
                // Crop the existing crown; never turn the horizontal ground wave on its side.
                if (elapsed < BattleSkillFlipbook.Arrival)
                {
                    if (!_sourcePulse) return;
                    float p = elapsed / BattleSkillFlipbook.Arrival;
                    int frame = 9 + Mathf.Min(2, Mathf.FloorToInt(p * 3f));
                    var flare = new BattleSkillFlipbook.FrameSample(frame, frame, 0f);
                    DrawUpright(vh, source, height, height * .65f, flare, Mathf.Sin(p * Mathf.PI) * .65f);
                }
                else
                {
                    float size = height * Mathf.Lerp(.7f, 1.25f,
                        Mathf.Clamp01((elapsed - BattleSkillFlipbook.Arrival) / .2f));
                    DrawUpright(vh, target, height, size, sample, 1f);
                }
                return;
            }
            if (_fire)
            {
                // The ground is sheared with the endpoints, while flame tongues stay upward.
                float size = Mathf.Abs(delta.x) > .1f ? delta.x / .66f : width;
                float h = Mathf.Abs(size);
                Vector2 xAxis = new Vector2(size, delta.y / .66f);
                Vector2 origin = source - Vector2.up * (height * .4f) - xAxis * .17f - Vector2.up * (h * .1f);
                Quad(vh, origin, xAxis, Vector2.up * h, 0f, 1f, sample, 1f);
            }
            else
            {
                float distance = delta.magnitude;
                Vector2 axis = distance > .1f ? delta / distance : Vector2.right;
                float size = Mathf.Max(width, distance / .6f) * .85f;
                Vector2 up = new Vector2(-axis.y, axis.x);
                Vector2 origin = source - axis * (size * .25f) - up * (size * .44f);
                // Only the first neighbour owns the original blast. Further meshes carry its fan.
                Quad(vh, origin, axis * size, up * size, _sourcePulse ? 0f : .42f, 1f, sample, 1f);
            }
        }

        private void DrawUpright(VertexHelper vh, Vector2 target, float cardHeight, float size,
            BattleSkillFlipbook.FrameSample sample, float alpha)
        {
            Vector2 origin = target - Vector2.right * (size * .8f)
                - Vector2.up * (cardHeight * .4f + size * .1f);
            Quad(vh, origin, Vector2.right * size, Vector2.up * size, .5f, 1f, sample, alpha);
        }

        private Vector2 UV(int frame, float x, float y)
        {
            int rows = BattleSkillFlipbook.Rows(_fire);
            float insetX = .5f / _atlas.width, insetY = .5f / _atlas.height;
            return new Vector2((frame % 4) * .25f + Mathf.Lerp(insetX, .25f - insetX, x),
                1f - (frame / 4 + 1f) / rows + Mathf.Lerp(insetY, 1f / rows - insetY, y));
        }
        private void Quad(VertexHelper vh, Vector2 origin, Vector2 axis, Vector2 up, float left, float right,
            BattleSkillFlipbook.FrameSample sample, float alpha)
        {
            int start = vh.currentVertCount;
            Vertex(vh, origin + axis * left, left, 0f, sample, alpha);
            Vertex(vh, origin + axis * left + up, left, 1f, sample, alpha);
            Vertex(vh, origin + axis * right + up, right, 1f, sample, alpha);
            Vertex(vh, origin + axis * right, right, 0f, sample, alpha);
            vh.AddTriangle(start, start+1, start+2); vh.AddTriangle(start, start+2, start+3);
        }
        private void Vertex(VertexHelper vh, Vector2 position, float x, float y,
            BattleSkillFlipbook.FrameSample sample, float alpha)
        {
            var v = UIVertex.simpleVert;
            v.position = position; v.color = new Color(1f,1f,1f,alpha);
            v.uv0 = UV(sample.Frame, x, y);
            Vector2 next = sample.Next >= 0 ? UV(sample.Next, x, y) : Vector2.zero;
            v.uv1 = new Vector4(next.x, next.y, sample.Blend, sample.Next >= 0 ? 1f : 0f);
            vh.AddVert(v);
        }
        private void Update()
        {
            if (_sourceCard == null || _targetCard == null || Time.unscaledTime - _started >= BattleSkillFlipbook.Duration(_fire))
            { gameObject.SetActive(false); Destroy(gameObject); return; }
            SetVerticesDirty();
        }
    }
}
