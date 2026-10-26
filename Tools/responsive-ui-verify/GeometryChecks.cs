using System;
using Game.UI;

static class GeometryChecks
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    public static void Main()
    {
        foreach (var size in new[] { (842.4f, 176.35f), (421.2f, 88.175f), (730f, 203f),
                                    (973f, 153f), (320f, 70f) })
        {
            var row = CardRowGeometry.Fit(7, 109f, 842.4f, 176.35f, size.Item1, size.Item2);
            Check(row.Scale > 0, "Visible row must have positive scale");
            for (int i = 0; i < 7; i++)
            {
                Check(Math.Abs(row.SlotX(i)) + 106f * 1.3f * row.Scale / 2 <= size.Item1 / 2 + .01f,
                    "Hovered outer cards must fit horizontally");
                Check(Math.Abs(row.SlotX(i) + row.SlotX(6-i)) < .001f, "Slots must stay symmetric");
                Check(Math.Abs(row.IndexAt(row.SlotX(i)) - i) < .0001f, "Visual slots and reorder hit coordinates disagree");
            }
        }
        var normal = CardRowGeometry.Fit(7, 109, 842.4f, 176.35f, 842.4f, 176.35f);
        Check(Math.Abs(normal.SlotX(0) + 327) < .001, "Reference hand must keep its authored position");
        Check(Math.Abs(normal.Scale - 1) < .001, "Reference hand must keep its authored scale");
        var resized = CardRowGeometry.Fit(7, 109, 842.4f, 176.35f, 421.2f, 88.175f);
        Check(Math.Abs(resized.SlotX(0) + 163.5f) < .001, "Positions must follow resized container");
        Check(Math.Abs(resized.Scale - .5f) < .001, "Size must follow resized container");
        // Pins sit 67.075 units below card centres in the reference hand. A tall/narrow
        // container must preserve that scaled offset, rather than use its own height ratio.
        Check(Math.Abs(resized.DecorationOffsetY(21.1f) + 33.5375f) < .001f,
            "Separator offset must follow the uniformly scaled card");
        var tall = CardRowGeometry.Fit(7, 109, 842.4f, 176.35f, 730f, 203f);
        Check(Math.Abs(tall.DecorationOffsetY(21.1f) - tall.CenterOffsetY + 58.1253f) < .001f,
            "Tall container must not detach separators from card corners");
        // A narrow/tall Free Aspect view must not lift the hand away from its bottom trim.
        // At 730x203 logical units the authored pin is 18.284663 above the bottom,
        // hence -83.215337 relative to the container centre (203 / 2).
        Check(Math.Abs(tall.DecorationOffsetY(21.1f) + 83.215337f) < .001f,
            "Narrow Free Aspect lifts the cards/pins away from the bottom edge");
        Check(Math.Abs(tall.CenterOffsetY + 25.09004f) < .001f,
            "Card centre must follow its bottom reference, not the tall container midpoint");
        foreach (var pixels in new[] { (1280f, 720f), (1920f, 1080f), (2560f, 1440f),
            (1920f, 1200f), (2560f, 1080f), (1024f, 768f), (457f, 656f), (360f, 800f), (1240f, 360f), (3440f, 1440f) })
        {
            float canvasScale = (float)Math.Sqrt(pixels.Item1 / 1280f * pixels.Item2 / 720f);
            float width = pixels.Item1 / canvasScale * .658125f;
            float height = pixels.Item2 / canvasScale * .31183338f * .785454f;
            var g = CardRowGeometry.Fit(7, 109f, 842.4f, 176.35f, width, height);
            float normalizedGap = (g.SlotX(1) - g.SlotX(0) - 106f * g.Scale) / g.Scale;
            Check(Math.Abs(normalizedGap - 3f) < .001f,
                "Card gap must remain proportional at every aspect ratio");
            Check(Math.Abs(g.SlotX(.5f) - (g.SlotX(0) + g.SlotX(1)) / 2f) < .001f,
                "Separator must stay halfway between adjacent slots");
            float bottom = height / 2f + g.CenterOffsetY - 155f * g.Scale / 2f;
            Check(Math.Abs(bottom / g.Scale - 10.675f) < .001f,
                "Bottom hand clearance changes with Free Aspect");
            Check(Math.Abs((height / 2f + g.DecorationOffsetY(21.1f)) / g.Scale - 21.1f) < .001f,
                "Pin must follow the same bottom reference as the card");
        }
        // On a wide/short Free Aspect view, shrinking a card must also shrink its slot step.
        // The authored gap is 109 - 106 = 3, regardless of which axis limits the row.
        var wide = CardRowGeometry.Fit(7, 109f, 842.4f, 176.35f, 973f, 153f);
        float gap = (wide.SlotX(1) - wide.SlotX(0) - 106f * wide.Scale) / wide.Scale;
        Check(Math.Abs(gap - 3f) < .001f,
            "Wide Free Aspect stretches card spacing independently from card size");
        var hidden = CardRowGeometry.Fit(7, 109, 842.4f, 176.35f, 0, 0);
        Check(hidden.Scale == 0 && !float.IsNaN(hidden.Step), "Zero-size layout must be finite");
        Console.WriteLine("Hand geometry: reference, resize, aspect ratios, hover bounds, reorder and separator alignment PASS");
    }
}
