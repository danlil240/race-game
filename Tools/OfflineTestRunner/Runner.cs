using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace FamilyRacing.OfflineTests
{
    // Minimal NUnit runner used while NuGet restore is unavailable (no network
    // and an empty global package cache). It compiles and executes the shared
    // test sources unchanged against the real nunit.framework bundled with the
    // installed Unity editor. Supported features: [Test], [TestCase] with
    // inline arguments, [SetUp], [TearDown]. Anything else (TestCaseSource,
    // categories, fixture-level attributes) is not implemented. Exits nonzero
    // when zero tests are discovered.
    internal static class Runner
    {
        private static int Main()
        {
            var passed = 0;
            var failed = 0;
            var skipped = 0;
            var failures = new List<string>();

            foreach (var type in typeof(Runner).Assembly.GetTypes())
            {
                var cases = CollectCases(type);
                if (cases.Count == 0) continue;

                object fixture;
                try
                {
                    fixture = Activator.CreateInstance(type);
                }
                catch (Exception e)
                {
                    failed += cases.Count;
                    failures.Add(type.FullName + ": fixture construction failed: " + e);
                    Console.WriteLine("FAIL " + type.Name + " (fixture construction)");
                    continue;
                }

                var setUp = Lifecycle(type, typeof(SetUpAttribute));
                var tearDown = Lifecycle(type, typeof(TearDownAttribute));

                foreach (var test in cases)
                {
                    var name = type.Name + "." + test.Method.Name + FormatArgs(test.Args);
                    try
                    {
                        InvokeAll(setUp, fixture);
                        try
                        {
                            test.Method.Invoke(fixture, test.Args);
                        }
                        finally
                        {
                            InvokeAll(tearDown, fixture);
                        }
                        passed++;
                        Console.WriteLine("PASS " + name);
                    }
                    catch (Exception e)
                    {
                        var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                        if (inner.GetType().Name == "SuccessException")
                        {
                            passed++;
                            Console.WriteLine("PASS " + name);
                        }
                        else if (inner.GetType().Name.IndexOf("Ignore", StringComparison.Ordinal) >= 0
                                 || inner.GetType().Name.IndexOf("Inconclusive", StringComparison.Ordinal) >= 0)
                        {
                            skipped++;
                            Console.WriteLine("SKIP " + name + " " + inner.Message);
                        }
                        else
                        {
                            failed++;
                            failures.Add(name + Environment.NewLine + "    " + inner.GetType().Name + ": " + inner.Message
                                         + Environment.NewLine + inner.StackTrace);
                            Console.WriteLine("FAIL " + name + "  " + inner.GetType().Name + ": " + FirstLine(inner.Message));
                        }
                    }
                }
            }

            Console.WriteLine();
            foreach (var failure in failures)
            {
                Console.WriteLine(failure);
                Console.WriteLine();
            }
            if (passed + failed + skipped == 0)
            {
                Console.WriteLine("ERROR: no [Test]/[TestCase] methods were discovered; this is a runner failure, not a green suite.");
                return 1;
            }
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed, " + skipped + " skipped.");
            return failed == 0 ? 0 : 1;
        }

        private sealed class TestCase
        {
            public MethodInfo Method;
            public object[] Args;
        }

        private static List<TestCase> CollectCases(Type type)
        {
            var cases = new List<TestCase>();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.GetCustomAttributes(typeof(TestAttribute), true).Length > 0)
                    cases.Add(new TestCase { Method = method, Args = null });
                foreach (var attribute in method.GetCustomAttributes(typeof(TestCaseAttribute), true))
                    cases.Add(new TestCase { Method = method, Args = ((TestCaseAttribute)attribute).Arguments });
            }
            return cases;
        }

        private static List<MethodInfo> Lifecycle(Type type, Type attribute)
        {
            var methods = new List<MethodInfo>();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (method.GetCustomAttributes(attribute, true).Length > 0)
                    methods.Add(method);
            return methods;
        }

        private static void InvokeAll(List<MethodInfo> methods, object fixture)
        {
            foreach (var method in methods) method.Invoke(fixture, null);
        }

        private static string FormatArgs(object[] args)
        {
            if (args == null || args.Length == 0) return "";
            var builder = new StringBuilder("(");
            for (var i = 0; i < args.Length; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(args[i] == null ? "null" : args[i] is string ? "\"" + args[i] + "\"" : args[i].ToString());
            }
            return builder.Append(")").ToString();
        }

        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            var newline = message.IndexOf('\n');
            return newline < 0 ? message : message.Substring(0, newline);
        }
    }
}
