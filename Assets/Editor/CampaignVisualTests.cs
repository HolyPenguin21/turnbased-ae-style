#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Campaign;
using Game.Players;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public sealed class CampaignVisualTests
{
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
    [Test]
    public void SurfaceResourcesAndFrameImportAreReadyForAPlayerBuild()
    {
        Assert.NotNull(Resources.Load<Texture2D>("Campaign/WastelandSurface"));
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
            CampaignGeometry.Validate(state.Regions, true);
        }
        finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); }
    }
}
#endif
