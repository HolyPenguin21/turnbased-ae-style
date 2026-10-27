using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

public static class Program
{
    public static int Main()
    {
        int passed = 0, failed = 0;
        foreach (var type in new[]
        {
            typeof(Game.EditorTests.PlayerHandStateTests),
            typeof(Game.EditorTests.AiHandCapacityParityTests)
        })
        foreach (var method in type.GetMethods().Where(m => m.IsDefined(typeof(TestAttribute)))
                     .OrderBy(m => m.MetadataToken))
        {
            try
            {
                method.Invoke(Activator.CreateInstance(type), null);
                passed++;
                Console.WriteLine("PASS " + method.Name);
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine("FAIL " + method.Name + ": " + (exception.InnerException ?? exception));
            }
        }
        Console.WriteLine($"Hand model checks: {passed} passed, {failed} failed (managed adapters; Unity checks still required).");
        return passed > 0 && failed == 0 ? 0 : 1;
    }
}
