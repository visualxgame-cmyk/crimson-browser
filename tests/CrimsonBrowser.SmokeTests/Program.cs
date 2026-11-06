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
    Assert(ShieldRequestPolicy.ShouldBlockUrl("https://pagead2.googlesyndication.com/ads"), "advertising hosts remain blocked");
    Assert(ShieldRequestPolicy.ShouldBlockUrl("https://www.youtube.com/pagead/example"), "YouTube advertising endpoints remain blocked");
    Assert(!ShieldRequestPolicy.ShouldBlockUrl("https://www.youtube.com/youtubei/v1/att/get?prettyPrint=false"), "YouTube attestation is not classified as advertising");
    Assert(!ShieldRequestPolicy.ShouldBlockUrl("https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fdoubleclick.net%2F&state=adslot%3D123"),
        "authentication redirect parameters are not mistaken for a tracker request");
    Assert(!ShieldRequestPolicy.ShouldBlockUrl("https://example.test/article?next=https://youtube.com/pagead/"), "nested URL values do not cause unrelated requests to be blocked");
    Assert(!ShieldRequestPolicy.ShouldBlockUrl("https://notdoubleclick.net/article"), "domain matching respects hostname boundaries");
    Assert(ShieldRequestPolicy.IsAuthenticationPage("https://accounts.youtube.com/accounts/SetSID"), "YouTube account transfer is protected from script injection");
    Assert(ShieldRequestPolicy.IsAuthenticationPage("https://www.youtube.com/signin?action_handle_signin=true"), "same-tab sign-in handoff is protected from script injection");
    Assert(!ShieldRequestPolicy.IsAuthenticationPage("https://www.youtube.com/watch?v=example"), "normal YouTube content keeps ad filtering");
    Assert(!ShieldRequestPolicy.IsAuthenticationPage("https://accounts.google.com.evil.test/"), "authentication origins are exact");
    Console.WriteLine("PASS: Authentication-safe Shields request matching");
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
