using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
class Program
{
    public static int Main()
    {
        int passed = 0, failed = 0;
        foreach (var method in typeof(Game.EditorTests.LibraryRewardReceiptTests).GetMethods().OrderBy(m => m.Name))
        {
            var cases = method.GetCustomAttributes<TestCaseAttribute>().Select(c => c.Arguments).ToList();
            if (method.GetCustomAttribute<TestAttribute>() != null) cases.Add(new object[0]);
            foreach (var arguments in cases)
            {
                var test = new Game.EditorTests.LibraryRewardReceiptTests(); test.Setup();
                try { method.Invoke(test, arguments); Console.WriteLine("PASS " + method.Name); passed++; }
                catch (Exception ex) { Console.WriteLine("FAIL " + method.Name + ": " + (ex.InnerException ?? ex)); failed++; }
                finally { test.Cleanup(); }
            }
        }
        Console.WriteLine($"Passed: {passed}; Failed: {failed}"); return failed == 0 ? 0 : 1;
    }
}
