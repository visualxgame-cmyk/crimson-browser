using CrimsonBrowser.Services;

var root = Path.Combine(Path.GetTempPath(), "CrimsonBrowser.SmokeTests", Guid.NewGuid().ToString("N"));
var file = Path.Combine(root, "profile.json");

try
{
    AtomicFileStore.Write(file, new TestData("first"));
    AtomicFileStore.Write(file, new TestData("second"));
    Assert(AtomicFileStore.Read<TestData>(file)?.Value == "second", "latest profile data is readable");

    File.WriteAllText(file, "{ invalid json");
    Assert(AtomicFileStore.Read<TestData>(file)?.Value == "first", "backup recovers a corrupt primary profile file");

    Console.WriteLine("PASS: Atomic profile storage");
    return 0;
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Smoke test failed: " + message);
}

internal sealed record TestData(string Value);
