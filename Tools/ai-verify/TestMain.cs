// Entry point of the ai-verify test exe (copied as Program.cs by run_tests.sh; never part of Assets/).
// AI_VERIFY_EXCLUDE=<file>: full test names, one per line ('#' comments), are excluded by turning them
// into `test != '...'` clauses ANDed with every --where given. Built in-process: a ~1000-name filter
// does not fit the Windows command line, NUnitLite's @argfile splits on spaces inside names, and
// --testlist does not match cases renamed with TestCase(TestName = ...).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class TestMain
{
    public static int Main(string[] args)
    {
        var rest = new List<string>();
        var where = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--where" && i + 1 < args.Length) where.Add(args[++i]);
            else if (args[i].StartsWith("--where=")) where.Add(args[i].Substring("--where=".Length));
            else rest.Add(args[i]);
        }
        string exclude = Environment.GetEnvironmentVariable("AI_VERIFY_EXCLUDE");
        if (!string.IsNullOrEmpty(exclude))
            foreach (string line in File.ReadAllLines(exclude))
            {
                string name = line.Trim().TrimStart('﻿');
                if (name.Length == 0 || name[0] == '#') continue;
                where.Add("test != '" + name.Replace("\\", "\\\\").Replace("'", "\\'") + "'");
            }
        if (where.Count > 0) rest.Add("--where=" + string.Join(" && ", where.Select(w => "(" + w + ")")));
        return new NUnitLite.AutoRun(typeof(TestMain).Assembly).Execute(rest.ToArray());
    }
}
