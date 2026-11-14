using System.Reflection;

namespace RotrTrainer.Tests;

/// <summary>Runs every public static void Test*() in this assembly; exit code = failure count.</summary>
public static class TestRunner
{
    public static int Main()
    {
        int failed = 0, passed = 0;
        var tests = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.Name.StartsWith("Test") && method.GetParameters().Length == 0)
            .OrderBy(method => method.DeclaringType!.Name + "." + method.Name);
        foreach (var test in tests)
        {
            string name = $"{test.DeclaringType!.Name}.{test.Name}";
            try
            {
                test.Invoke(null, null);
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (TargetInvocationException exception)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {exception.InnerException!.Message}");
            }
        }
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed;
    }
}

public static class Check
{
    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what} expected {expected} but got {actual}");
    }

    public static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
