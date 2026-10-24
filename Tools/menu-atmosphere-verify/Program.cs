using System;
using System.Reflection;

// Engine-independent checks of the real motion sampler. Unity graphics/lifecycle tests
// live in Assets/Editor/Tests/UI/MainMenuAtmosphereTests.cs and require the editor.
internal static class Program
{
    private static Type Motion => typeof(Program).Assembly.GetType("Game.UI.MainMenuWindMotion")
        ?? throw new Exception("MainMenuWindMotion is missing.");
    private static object Sample(double time, int layer, int puff, bool trail = false) =>
        Motion.GetMethod("SampleDust").Invoke(null, new object[] { time, layer, puff, trail });
    private static double Value(object sample, string name) => Convert.ToDouble(sample.GetType().GetField(name).GetValue(sample));
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    private static int Main()
    {
        int failures = 0;
        void Test(string name, Action body)
        {
            try { body(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
        }
        Test("Dust advects to the right, with faster foreground motion", () =>
        {
            double Delta(int layer) => Value(Sample(2.25, layer, 0), "X") - Value(Sample(2, layer, 0), "X");
            Check(Delta(0) > .01, "Far dust must move, not oscillate on the spot.");
            Check(Math.Abs(Delta(2) - .02375) < .00001, "Middle dust must retain the approved preview's speed.");
            Check(Delta(4) > Delta(2) * 2, "Foreground dust must be faster than the middle plane.");
        });
        Test("Loop boundaries fade instead of popping", () =>
        {
            Check(Value(Sample(0, 0, 0), "Alpha") == 0, "New cloud must enter invisibly.");
            Check(Value(Sample(39.99999, 0, 0), "Alpha") < .001, "Cloud must disappear before wrapping.");
        });
        Test("Trailing wisps retain the approved independent ground trajectory", () =>
        {
            object trail = Sample(0, 1, 0, true);
            Check(Math.Abs(Value(trail, "X") - .2438) < .00001, "Trail must follow the base width, not the swollen cloud.");
            Check(Math.Abs(Value(trail, "Y") - .6352) < .00001, "Trail must hug the base plane without the main cloud's lift.");
            Check(Math.Abs(Value(trail, "Height") - .0357) < .00001, "Trail height must remain independent of the main cloud's swelling.");
        });
        Test("Long sessions keep samples finite and bounded", () =>
        {
            foreach (double time in new[] { 0d, .01, 2, 7, 40, 3600, 86400, 1e9 })
                for (int layer = 0; layer < 6; layer++)
                    for (int puff = 0; puff < (layer < 4 ? 3 : 2); puff++)
                        foreach (bool trail in new[] { false, true })
                        {
                            object cloud = Sample(time, layer, puff, trail);
                            foreach (string field in new[] { "X", "Y", "Width", "Height", "Alpha", "Angle" })
                                Check(double.IsFinite(Value(cloud, field)), field + " is not finite.");
                            Check(Value(cloud, "Alpha") >= 0 && Value(cloud, "Alpha") <= 1, "Opacity must be bounded.");
                            Check(Value(cloud, "Width") > 0 && Value(cloud, "Height") > 0, "Cloud dimensions must be positive.");
                        }
        });
        Test("Sampling is independent of frame rate and call history", () =>
        {
            object expected = Sample(10, 3, 1);
            for (int i = 0; i < 600; i++) Sample(i / 60d, 3, 1);
            object actual = Sample(10, 3, 1);
            foreach (string field in new[] { "X", "Y", "Width", "Height", "Alpha" })
                Check(Value(expected, field) == Value(actual, field), field + " depends on previous frames.");
        });
        Test("Near debris is intermittent and has clean loop boundaries", () =>
        {
            object Debris(double t) => Motion.GetMethod("SampleDebris").Invoke(null, new object[] { t });
            Check(Value(Debris(6), "Alpha") == 0, "Debris must fade at entry.");
            Check(Value(Debris(10), "Alpha") == 0, "Debris must leave pauses between passes.");
            Check(Value(Debris(7), "Alpha") > .5, "Debris must be visible during a pass.");
        });
        Console.WriteLine(failures == 0 ? "All motion checks passed." : failures + " checks failed.");
        return failures == 0 ? 0 : 1;
    }
}
