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
        Check(Math.Abs(tall.DecorationOffsetY(21.1f) + 58.1253f) < .001f,
            "Tall container must not detach separators from card corners");
        var hidden = CardRowGeometry.Fit(7, 109, 842.4f, 176.35f, 0, 0);
        Check(hidden.Scale == 0 && !float.IsNaN(hidden.Step), "Zero-size layout must be finite");
        Console.WriteLine("Hand geometry: reference, resize, aspect ratios, hover bounds, reorder and separator alignment PASS");
    }
}
