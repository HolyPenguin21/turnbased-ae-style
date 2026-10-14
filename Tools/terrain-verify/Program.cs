using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Game.EditorTests;
public static class Program
{
    public static int Main()
    {
        int passed=0,failed=0;
        foreach (var type in new[] { typeof(TerrainComplexTests), typeof(MatchResourcePlacementTests) })
        foreach (var method in type.GetMethods().Where(m=>m.GetCustomAttribute<TestAttribute>()!=null))
        {
            var fixture=Activator.CreateInstance(type);
            try { type.GetMethod("SetUp").Invoke(fixture,null);method.Invoke(fixture,null);passed++;Console.WriteLine("PASS "+method.Name); }
            catch(Exception e) {failed++;Console.WriteLine("FAIL "+method.Name+": "+(e.InnerException??e).Message);}
            finally { type.GetMethod("TearDown").Invoke(fixture,null); }
        }
        Console.WriteLine($"Terrain algorithm checks: {passed} passed, {failed} failed (managed stubs; Unity engine checks still required).");
        return failed==0?0:1;
    }
}
