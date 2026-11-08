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
    var errors = new List<string>();
    void StorageFailed(string name, Exception error) { lock (errors) errors.Add(name); }
    ProfileStorageDiagnostics.Failed += StorageFailed;
    try
    {
        var bookmarks = new BookmarksService(Path.Combine(root, "bookmarks.json"));
        bookmarks.Add("Upper", "https://EXAMPLE.test/Page?q=A#Part");
        bookmarks.Add("Lower", "https://example.test/page?q=A#Part");
        bookmarks.Add("Same", "https://example.test:443/Page?q=A#Part");
        Assert(bookmarks.Bookmarks.Count == 2, "bookmark hosts normalize while path case remains distinct");
        Assert(!bookmarks.Contains("https://example.test/Page?q=a#Part"), "query values preserve case");
        Assert(!bookmarks.Contains("https://example.test/Page?q=A#part"), "fragments preserve case");
        bookmarks.Remove("https://example.test/page?q=A#Part");
        Assert(bookmarks.Bookmarks.Single().Title == "Upper", "removing a bookmark preserves a different case-sensitive path");
        Assert(new BookmarksService(Path.Combine(root, "bookmarks.json")).Bookmarks.Count == 1, "bookmarks survive restart");
        using (var locked = new FileStream(Path.Combine(root, "bookmarks.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bookmarks.Remove("https://example.test/Page?q=A#Part");
            Assert(bookmarks.Bookmarks.Count == 1, "a failed removal does not pretend the bookmark was deleted");
        }

        string blocked = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocked, "occupied");
        var unsavable = new BookmarksService(Path.Combine(blocked, "bookmarks.json"));
        int previousErrors = errors.Count;
        unsavable.Add("Cannot save", "https://example.test/");
        Assert(unsavable.Bookmarks.Count == 0 && errors.Count > previousErrors, "failed bookmark saves are reported without falsely adding a favorite");

        string corrupt = Path.Combine(root, "corrupt.json");
        File.WriteAllText(corrupt, "invalid");
        File.WriteAllText(corrupt + ".bak", "invalid");
        previousErrors = errors.Count;
        _ = new BookmarksService(corrupt);
        Assert(errors.Count > previousErrors, "unrecoverable profile corruption is reported");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var written = new List<int>();
        int caller = Environment.CurrentManagedThreadId;
        int workerThread = caller;
        var writer = new BackgroundProfileWriter<int>("test.json", (_, value) =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            if (value == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            written.Add(value);
        });
        try
        {
            writer.Schedule(1);
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "background disk writer starts");
            writer.Schedule(2);
            writer.Schedule(3);
        }
        finally { release.Set(); }
        Assert(writer.Flush() && workerThread != caller && written.SequenceEqual(new[] { 1, 3 }),
            "disk writes run off the caller thread, coalesce and flush the latest snapshot");
        var failingWriter = new BackgroundProfileWriter<int>("failed.json", (_, value) =>
        {
            if (value == 1) throw new IOException("Simulated storage failure");
        });
        failingWriter.Schedule(1);
        Assert(!failingWriter.Flush(), "background storage failures remain observable at shutdown");
        failingWriter.Schedule(2);
        Assert(failingWriter.Flush(), "a subsequent successful save clears the failure state");

        string historyPath = Path.Combine(root, "history.json");
        var savedHistory = new HistoryService(historyPath);
        for (int i = 0; i < 80; i++) savedHistory.AddEntry("Page " + i, "https://example.test/" + i);
        Assert(savedHistory.FlushPendingWrites(), "pending history is saved before exit");
        Assert(new HistoryService(historyPath).Entries[0].Url.EndsWith("/79"), "latest history survives restart");
        Assert(savedHistory.Clear(), "history clearing completes pending disk writes");
        Assert(new HistoryService(historyPath).Entries.Count == 0 && AtomicFileStore.Read<HistoryEntry[]>(historyPath + ".bak")!.Length == 0,
            "cleared history cannot return from its recovery backup");

        string downloadsPath = Path.Combine(root, "downloads.json");
        var downloads = new DownloadsService(downloadsPath);
        string downloadedFile = Path.Combine(root, "download.txt");
        File.WriteAllText(downloadedFile, "download test");
        downloads.Update("one", downloadedFile, "https://example.test/file", 100, 100, DownloadStatus.Completed);
        downloads.Update("two", Path.Combine(root, "partial.txt"), "https://example.test/partial", 100, 20, DownloadStatus.InProgress);
        downloads.Update("private", Path.Combine(root, "secret.txt"), "https://private.test/file", 100, 100, DownloadStatus.Completed, isPrivate: true);
        Assert(downloads.FlushPendingWrites(), "download history flushes on exit");
        var restored = new DownloadsService(downloadsPath);
        Assert(restored.Downloads.Count == 2 && restored.Downloads.Any(item => item.IsCompleted), "completed downloads survive restart without retaining private downloads");
        Assert(restored.ActiveCount == 0 && restored.Downloads.Any(item => item.Status == DownloadStatus.Failed), "interrupted downloads do not remain permanently active after restart");
        Assert(!File.ReadAllText(downloadsPath).Contains("private.test"), "incognito download URLs are excluded from disk snapshots");
        restored.ClearCompleted();
        Assert(restored.FlushPendingWrites() && new DownloadsService(downloadsPath).Downloads.Count == 0 && File.Exists(downloadedFile),
            "clearing download history persists without deleting downloaded files");
        for (int i = 0; i < 220; i++) restored.Update("bounded-" + i, Path.Combine(root, i + ".bin"),
            "https://example.test/" + i, 100, 100, DownloadStatus.Completed);
        Assert(restored.FlushPendingWrites() && restored.Downloads.Count == 200 && new DownloadsService(downloadsPath).Downloads.Count == 200,
            "download history stays bounded in memory and on disk");
        Console.WriteLine("PASS: Bookmark identity, storage failure reporting, background history and persistent downloads");
    }
    finally { ProfileStorageDiagnostics.Failed -= StorageFailed; }
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
    Assert(!ShieldRequestPolicy.ShouldBlockUrl("https://www.youtube.com/youtubei/v1/log_event?alt=json"), "YouTube playback reporting is not an advertising endpoint");
    Assert(!ShieldRequestPolicy.ShouldBlockUrl("https://www.youtube.com/api/stats/ads?event=playback&ad_type=video&adslot=1"), "YouTube ad playback reporting does not break the player");
    Assert(!ShieldRequestPolicy.ShouldBlockUrl("https://rr1.googlevideo.com/videoplayback?ad_type=video&adslot=1&sig=fixture"), "signed YouTube media transport remains playable");
    Assert(ShieldRequestPolicy.ShouldBlockUrl("https://googlevideo.com.evil.test/videoplayback?ad_type=video"), "media exception requires an actual Googlevideo host");
    Assert(PopupNavigationPolicy.ShouldBlock("https://popads.net/open", true), "advertising popups are blocked even with user activation");
    Assert(PopupNavigationPolicy.ShouldBlock("https://example.test/", false), "automatic popups are blocked");
    Assert(!PopupNavigationPolicy.ShouldBlock("https://example.test/article", true), "explicit links proceed to intent verification");
    Assert(!PopupNavigationPolicy.ShouldBlock("https://accounts.google.com/ServiceLogin", true), "Google sign-in popup remains available");
    Assert(!PopupNavigationPolicy.IsAuthenticationTarget("https://accounts.google.com.evil.test/"), "authentication popup exceptions require the exact host");
    Console.WriteLine("PASS: Popup navigation policy");
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
