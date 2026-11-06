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
    var history = new[]
    {
        new HistoryEntry("ChatGPT conversation", "https://chatgpt.com/c/example", DateTime.Now),
        new HistoryEntry("GitHub project", "https://github.com/example/project", DateTime.Now),
        new HistoryEntry("ChatGPT second conversation", "https://chatgpt.com/c/second", DateTime.Now),
        new HistoryEntry("Not a website", "file:///C:/private.txt", DateTime.Now)
    };
    var suggestions = OmniboxSuggestions.Search(history, "chat");
    Assert(suggestions[0].Completion == "chatgpt.com" && suggestions[0].Url == "https://chatgpt.com/",
        "domain completion opens the site rather than a previous conversation");
    Assert(suggestions.Count(s => s.Completion == "chatgpt.com") == 1, "domain suggestions are deduplicated");
    Assert(OmniboxSuggestions.Search(history, "GIT")[0].Completion == "github.com", "other sites and mixed case work");
    Assert(OmniboxSuggestions.Search(history, "https://chat")[0].Completion == "https://chatgpt.com", "typed scheme is preserved");
    Assert(OmniboxSuggestions.Search(history, "project").Any(s => s.Url.EndsWith("/example/project")), "page titles match");
    Assert(OmniboxSuggestions.Search(history, "private").Count == 0, "local file history is not suggested");
    Assert(OmniboxSuggestions.Search(history, "").Count == 0, "empty input exposes no history");
    Assert(OmniboxSuggestions.Search(history, "chat", 1).Count == 1, "suggestion count is bounded");
    var www = OmniboxSuggestions.Search(new[] { new HistoryEntry("Site", "https://www.example.test/page", DateTime.Now) }, "exam")[0];
    Assert(www.Completion == "example.test" && www.Url == "https://www.example.test/", "display completion retains the actual www destination");
    Console.WriteLine("PASS: Local address suggestions");
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
