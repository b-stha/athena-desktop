using Athena.Desktop.RegressionTests;

if (await McpTests.RunFixtureAsync(args) is { } fixtureExitCode) return fixtureExitCode;

var tests = new List<(string Name, Func<Task> Run)>();
if (!args.Contains("--mcp-only")) tests.AddRange(BackgroundTests.Cases);
tests.AddRange(McpTests.Cases);

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
return failures == 0 ? 0 : 1;
