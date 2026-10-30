using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Internal;

internal static class WorldStateChecks
{
    private static int Main()
    {
        int count = 0, failed = 0;
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.Namespace == "Game.EditorTests").OrderBy(t => t.Name))
            foreach (var method in type.GetMethods().Where(m => m.IsDefined(typeof(TestAttribute), true)).OrderBy(m => m.Name))
            {
                using (new TestExecutionContext.IsolatedContext())
                {
                    object fixture = Activator.CreateInstance(type);
                    try
                    {
                        InvokeAttributed<SetUpAttribute>(fixture);
                        method.Invoke(fixture, null);
                        Console.WriteLine("PASS " + type.Name + "." + method.Name);
                    }
                    catch (Exception error)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + type.Name + "." + method.Name + ": " + (error.InnerException ?? error));
                    }
                    finally
                    {
                        try { InvokeAttributed<TearDownAttribute>(fixture); }
                        catch (Exception error) { failed++; Console.WriteLine("FAIL teardown: " + (error.InnerException ?? error)); }
                    }
                    count++;
                }
            }
        failed += UnityEngine.Debug.NotificationFailures;
        Console.WriteLine(count + " checks, " + failed + " failures");
        return failed == 0 && count > 0 ? 0 : 1;
    }
    private static void InvokeAttributed<T>(object fixture) where T : Attribute
    {
        foreach (var method in fixture.GetType().GetMethods().Where(m => m.IsDefined(typeof(T), true))) method.Invoke(fixture, null);
    }
}
