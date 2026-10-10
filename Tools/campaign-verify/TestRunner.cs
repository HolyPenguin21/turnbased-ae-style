using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
class Program
{
 public static int Main(string[] args)
 {
  int passed=0,failed=0;
  foreach(var method in typeof(CampaignSystemTests).GetMethods().OrderBy(m=>m.Name))
  {
   var cases=method.GetCustomAttributes<TestCaseAttribute>().Select(c=>c.Arguments).ToList();
   if(method.GetCustomAttribute<TestAttribute>()!=null) cases.Add(new object[0]);
   foreach(var arguments in cases)
   {
    var test=new CampaignSystemTests();test.Setup();
    try {method.Invoke(test,arguments);Console.WriteLine("PASS "+method.Name+" "+string.Join(",",arguments));passed++;}
    catch(Exception ex){Console.WriteLine("FAIL "+method.Name+": "+(ex.InnerException??ex));failed++;}
    finally {test.Cleanup();}
   }
  }
  if(args.Length>0) Experiments.Run(args[0]);
  Console.WriteLine($"Passed: {passed}; Failed: {failed}");return failed==0?0:1;
 }
}
