#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Campaign;
using Game.Players;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public sealed class CampaignVisualTests
{
    [Test]
    public void ShaderKeepsBothSidesAfterCanvasBatchTranslation()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("A graphics device is required for the batched UI shader regression.");
        var material = new Material(Resources.Load<Shader>("Campaign/CampaignSurface"));
        var mesh = new Mesh();
        var target = RenderTexture.GetTemporary(512, 256, 0, RenderTextureFormat.ARGB32);
        var readback = new Texture2D(512, 256, TextureFormat.RGBA32, false);
        var previous = RenderTexture.active;
        try
        {
            // The actual 1360x740 campaign viewport after translation into the 1920x1080 Canvas.
            mesh.vertices = new[] { new Vector3(-914, -332), new Vector3(446, -332), new Vector3(446, 408), new Vector3(-914, 408) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.SetUVs(1, new System.Collections.Generic.List<Vector4> { new Vector4(0, 1, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 1, 0, 0) });
            mesh.SetUVs(2, new System.Collections.Generic.List<Vector4> { new Vector4(0, 0, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 0, 1, 1), new Vector4(0, 0, 0, 1) });
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            material.SetVector("_SurfaceSize", new Vector4(1360, 740, 0, 0));
            material.SetTexture("_MainTex", Texture2D.whiteTexture);
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            RenderTexture.active = target;
            GL.Clear(true, true, Color.clear);
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(-960, 960, -540, 540);
                Assert.True(material.SetPass(0));
                Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            }
            finally { GL.PopMatrix(); }
            readback.ReadPixels(new Rect(0, 0, 512, 256), 0, 0); readback.Apply();
            // x=-800 was incorrectly below the old local xMin=-680, despite being inside the map.
            Assert.That(readback.GetPixel(43, 128).a, Is.GreaterThan(.95f), "Left side clipped by mixed coordinate spaces");
            Assert.That(readback.GetPixel(336, 128).a, Is.GreaterThan(.95f), "Right side must stay visible too");
        }
        finally
        {
            RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(readback); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material);
        }
    }
    [TestCase(1920, 1080)]
    [TestCase(1280, 720)]
    [TestCase(2560, 1440)]
    public void ProjectedInputUsesTheSamePixelSpace(int width, int height)
    {
        var rect = new Rect(-width / 2f, -height / 2f, width, height);
        foreach (var point in new[] { Vector2.zero, new Vector2(.8f, -.2f), new Vector2(-.3f, .7f) })
        {
            var projected = CampaignMapView.Project(point);
            var result = CampaignMapView.FromPixel(rect, CampaignMapView.ToPixel(rect, projected));
            Assert.That((result - projected).sqrMagnitude, Is.LessThan(1e-10f));
        }
    }
    [TestCase(1360f, 740f, 1254f, 1254f)]
    [TestCase(740f, 1360f, 1254f, 1254f)]
    [TestCase(1360f, 740f, 1920f, 1080f)]
    public void TerrainPixelsHaveTheSameScaleOnBothAxes(float width, float height, float textureWidth, float textureHeight)
    {
        var rect = new Rect(-width / 2, -height / 2, width, height);
        float aspect = width / height, textureAspect = textureWidth / textureHeight;
        var origin = CampaignMapView.ProjectedSurfaceUV(Vector2.zero, aspect, textureAspect);
        var dx = CampaignMapView.ProjectedSurfaceUV(CampaignMapView.FromPixel(rect, new Vector2(10, 0)), aspect, textureAspect) - origin;
        var dy = CampaignMapView.ProjectedSurfaceUV(CampaignMapView.FromPixel(rect, new Vector2(0, 10)), aspect, textureAspect) - origin;
        Assert.That(dx.x * textureWidth, Is.EqualTo(dy.y * textureHeight).Within(.0001f));
        foreach (var point in new[] { new Vector2(-1, -1), new Vector2(1, 1) })
        {
            var uv = CampaignMapView.ProjectedSurfaceUV(point, aspect, textureAspect);
            Assert.That(uv.x, Is.InRange(0f, 1f)); Assert.That(uv.y, Is.InRange(0f, 1f));
        }
    }
    [Test]
    public void SurfaceResourcesAndFrameImportAreReadyForAPlayerBuild()
    {
        Assert.NotNull(Resources.Load<Texture2D>("Campaign/WastelandSurface"));
        for (int id = 1; id <= CampaignMapGenerator.BackgroundCount; id++)
            Assert.NotNull(Resources.Load<Texture2D>(CampaignMapView.BackgroundResourcePath(id)), "Missing campaign background " + id);
        Assert.NotNull(Resources.Load<Shader>("Campaign/CampaignSurface"));
        Assert.NotNull(Resources.Load<Sprite>("Campaign/Parchment"));
        Assert.NotNull(Resources.Load<Sprite>("Campaign/ConsoleMetal"));
        var frame = Resources.Load<Sprite>("Campaign/MetalFrame");
        Assert.NotNull(frame); Assert.That(frame.border.x, Is.GreaterThan(0));
    }
    [TestCase(1)]
    [TestCase(37)]
    [TestCase(94)]
    [TestCase(20261011)]
    public void GeneratedRegionsKeepTheirTopologyAndHaveInteriorMarkers(int seed)
    {
        var state = new CampaignMapGenerator().Generate(seed, Faction.IronConcord);
        var before = state.Regions.Select(r => r.PolygonVertices.ToArray()).ToArray();
        foreach (var region in state.Regions)
        {
            Assert.True(CampaignGeometry.Contains(region.PolygonVertices, CampaignRegionGraphic.InteriorCenter(region)));
            var visible = CampaignRegionGraphic.VisibleCenter(region);
            var projected = CampaignMapView.Project(visible);
            Assert.True(CampaignGeometry.Contains(region.PolygonVertices, visible));
            var displayed = region.PolygonVertices.ConvertAll(CampaignMapView.Project);
            Assert.True(CampaignGeometry.Contains(displayed, projected));
            var triangles = CampaignGeometry.Triangulate(displayed); double triangleArea = 0;
            for (int t = 0; t < triangles.Count; t += 3)
                triangleArea += CampaignGeometry.Cross(displayed[triangles[t]], displayed[triangles[t + 1]], displayed[triangles[t + 2]]) / 2;
            Assert.That(triangleArea, Is.EqualTo(CampaignGeometry.Area(displayed)).Within(1e-7));
            Assert.That(Mathf.Abs(projected.x), Is.LessThan(.995f));
            Assert.That(Mathf.Abs(projected.y), Is.LessThan(.995f));
            foreach (var vertex in region.PolygonVertices)
            {
                var uv = CampaignMapView.SurfaceUV(vertex);
                Assert.That(uv.x, Is.InRange(0f, 1f)); Assert.That(uv.y, Is.InRange(0f, 1f));
            }
        }
        for (int i = 0; i < state.Regions.Count; i++) CollectionAssert.AreEqual(before[i], state.Regions[i].PolygonVertices);
        CampaignGeometry.Validate(state.Regions, true);
    }
    [Test]
    public void ConcaveRegionMarkerDoesNotUseAnOutsideVertexAverage()
    {
        var region = new RegionState { PolygonVertices = new System.Collections.Generic.List<Vector2>
            { new Vector2(0, 0), new Vector2(3, 0), new Vector2(3, 1),
              new Vector2(1, 1), new Vector2(1, 3), new Vector2(0, 3) } };
        Assert.False(CampaignGeometry.Contains(region.PolygonVertices, CampaignGeometry.Center(region)));
        Assert.True(CampaignGeometry.Contains(region.PolygonVertices, CampaignRegionGraphic.InteriorCenter(region)));
    }
    [Test]
    public void SelectionGlowFadesFromContourTowardTheInterior()
    {
        var boundary = new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100) };
        Assert.That(CampaignRegionGraphic.SelectionGlow(new Vector2(0, 50), boundary, 24), Is.EqualTo(1f).Within(1e-6));
        Assert.That(CampaignRegionGraphic.SelectionGlow(new Vector2(12, 50), boundary, 24), Is.EqualTo(.5f).Within(1e-6));
        Assert.That(CampaignRegionGraphic.SelectionGlow(new Vector2(50, 50), boundary, 24), Is.EqualTo(0f).Within(1e-6));
        var concave = new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 40),
            new Vector2(40, 40), new Vector2(40, 100), new Vector2(0, 100) };
        Assert.That(CampaignRegionGraphic.SelectionGlow(new Vector2(39, 60), concave, 12), Is.GreaterThan(.9f));
    }
    [Test]
    public void RegionMeshSeparatesTexturedTerrainFromSolidBordersAndMarkers()
    {
        var state = new CampaignMapGenerator().Generate(73, Faction.IronConcord);
        var root = new GameObject("Campaign visual test", typeof(RectTransform), typeof(CanvasRenderer));
        var mesh = new Mesh();
        var material = new Material(Resources.Load<Shader>("Campaign/CampaignSurface"));
        try
        {
            var rect = root.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(1000, 600);
            var graphic = root.AddComponent<CampaignRegionGraphic>();
            graphic.SetSurface(Resources.Load<Texture2D>("Campaign/WastelandSurface"), material);
            graphic.Configure(state.Regions[0], () => { }, _ => { });
            graphic.Refresh(CampaignMapView.ColorFor(state.Regions[0].OwnerFaction), true, false, false, false, false, true);
            using (var vh = new VertexHelper())
            {
                typeof(CampaignRegionGraphic).GetMethod("OnPopulateMesh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(graphic, new object[] { vh });
                vh.FillMesh(mesh);
            }
            Assert.That(mesh.vertexCount, Is.GreaterThan(100));
            Assert.True(mesh.uv2.Any(v => v.x == 1)); Assert.True(mesh.uv2.Any(v => v.x == 0));
            Assert.True(mesh.uv.All(v => v.x >= 0 && v.x <= 1 && v.y >= 0 && v.y <= 1));
            var surfaceData = new System.Collections.Generic.List<Vector4>(); mesh.GetUVs(1, surfaceData);
            Assert.True(surfaceData.Any(v => Mathf.Abs(v.z) + Mathf.Abs(v.w) > .1f));
            var positions = mesh.vertices; var textureUVs = mesh.uv;
            for (int i = 0; i < positions.Length; i++)
            {
                if (surfaceData[i].x != 1) continue;
                // Texture coordinates must stay linear in screen space through subdivision.
                var expected = new Vector2(.5f + positions[i].x / rect.rect.width * .96f,
                    .5f + positions[i].y / rect.rect.width * .96f);
                Assert.That((textureUVs[i] - expected).sqrMagnitude, Is.LessThan(1e-10f));
            }
            // These coordinates, unlike POSITION, survive translation/scaling during UI batching.
            // The old mesh left zw at zero and clipped a large strip off the left of the map.
            var effects = new System.Collections.Generic.List<Vector4>(); mesh.GetUVs(2, effects);
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.That(effects[i].z, Is.EqualTo((positions[i].x - rect.rect.xMin) / rect.rect.width).Within(1e-6));
                Assert.That(effects[i].w, Is.EqualTo((positions[i].y - rect.rect.yMin) / rect.rect.height).Within(1e-6));
            }
            Assert.True(mesh.uv3.Any(v => v.x > .9f));
            Assert.True(mesh.uv3.Any(v => v.x > .01f && v.x < .99f));
            graphic.Refresh(CampaignMapView.ColorFor(state.Regions[0].OwnerFaction), false, false, false, false, false, true);
            using (var vh = new VertexHelper())
            {
                typeof(CampaignRegionGraphic).GetMethod("OnPopulateMesh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(graphic, new object[] { vh });
                vh.FillMesh(mesh);
            }
            Assert.True(mesh.uv3.All(v => v.x == 0));
            CampaignGeometry.Validate(state.Regions, true);
        }
        finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); }
    }
}
#endif
