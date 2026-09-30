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
        foreach (var method in typeof(TerrainComplexTests).GetMethods().Where(m=>m.GetCustomAttribute<TestAttribute>()!=null))
        {
            var fixture=new TerrainComplexTests();
            try { fixture.SetUp();method.Invoke(fixture,null);passed++;Console.WriteLine("PASS "+method.Name); }
            catch(Exception e) {failed++;Console.WriteLine("FAIL "+method.Name+": "+(e.InnerException??e).Message);}
            finally { fixture.TearDown(); }
        }
        Console.WriteLine($"Terrain algorithm checks: {passed} passed, {failed} failed (managed stubs; Unity engine checks still required).");
        return failed==0?0:1;
    }
}
