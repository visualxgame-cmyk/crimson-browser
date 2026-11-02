using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Services;

internal sealed record SharedPreferences(string Theme, string? Accent, string HomePage, string SearchUrl, bool BlockAds,
    int SuspendAfterMinutes, int DiscardAfterMinutes, int MaxLiveTabs, bool SavePasswords, bool AutofillEnabled,
    string StartupMode, string TranslationLanguage, DateTimeOffset ModifiedAt)
{
    public bool EconomyMode { get; init; }
    public static SharedPreferences From(Settings value) => new(value.Theme, value.CustomAccent, value.HomePage, value.SearchUrl,
        value.BlockAds, value.SuspendAfterMinutes, value.DiscardAfterMinutes, value.MaxLiveTabs, value.SavePasswords,
        value.AutofillEnabled, value.EffectiveStartupMode, value.TranslationLanguage, value.PreferencesUpdatedAt) { EconomyMode = value.EconomyMode };
    public Settings ApplyTo(Settings current)
    {
        if (!Uri.TryCreate(HomePage, UriKind.Absolute, out var home) || home.Scheme is not ("https" or "http") ||
            !Uri.TryCreate(SearchUrl, UriKind.Absolute, out var search) || search.Scheme is not ("https" or "http") ||
            SuspendAfterMinutes < 0 || DiscardAfterMinutes < 0 || MaxLiveTabs is < 3 or > 100 ||
            StartupMode is not ("restore" or "home" or "newtab")) throw new InvalidDataException("Setările din arhivă nu sunt valide.");
        var result = current.Clone();
        result.Theme = Theme; result.CustomAccent = Accent; result.HomePage = HomePage; result.SearchUrl = SearchUrl;
        result.BlockAds = BlockAds; result.SuspendAfterMinutes = SuspendAfterMinutes; result.DiscardAfterMinutes = DiscardAfterMinutes;
        result.MaxLiveTabs = MaxLiveTabs; result.SavePasswords = SavePasswords; result.AutofillEnabled = AutofillEnabled;
        result.EconomyMode = EconomyMode;
        result.StartupMode = StartupMode; result.RestoreSession = StartupMode == "restore";
        result.TranslationLanguage = TranslationLanguage; result.PreferencesUpdatedAt = ModifiedAt;
        return result;
    }
}
internal sealed record BackupState(Settings Settings, List<Shortcut> Shortcuts, Dictionary<string, SitePreference> Sites, byte[]? Background)
{
    internal void Validate()
    {
        if (Settings is null || Shortcuts is null || Sites is null) throw new InvalidDataException("Backup incomplet.");
        SharedPreferences.From(Settings).ApplyTo(new Settings());
        ShortcutStore.ValidateSnapshot(Shortcuts); SitePreferencesStore.ValidateSnapshot(Sites);
        if (Settings.NewTabBackgroundMode is not ("theme" or "color" or "image") ||
            !ThemeCatalog.TryParse(Settings.NewTabBackgroundColor, out _) ||
            (Settings.CustomAccent is not null && !ThemeCatalog.TryParse(Settings.CustomAccent, out _)) ||
            Background?.Length > 12 * 1024 * 1024) throw new InvalidDataException("Aspectul din backup nu este valid.");
        if (Settings.NewTabBackgroundImage is { Length: > 0 } name &&
            (Path.GetFileName(name) != name || Path.GetExtension(name).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")))
            throw new InvalidDataException("Numele imaginii din backup nu este valid.");
    }
}
internal sealed record LibraryTransfer(int FormatVersion, string Purpose, DateTimeOffset CreatedAt, List<Bookmark> Bookmarks, SharedPreferences Preferences)
{
    public BackupState? LocalBackup { get; init; }
    public List<Bookmark>? ReadingList { get; init; }
}

internal static class EncryptedLibrary
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VELASY01");
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const int Iterations = 310000;
    public static byte[] Encrypt(LibraryTransfer payload, string password)
    {
        ValidatePassword(password);
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (plain.Length > MaximumBytes) throw new InvalidDataException("Arhiva depășește 32 MB.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var encrypted = new byte[plain.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, encrypted, tag, Magic);
            using var output = new MemoryStream(52 + encrypted.Length);
            output.Write(Magic); output.Write(salt); output.Write(nonce); output.Write(tag); output.Write(encrypted);
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public static LibraryTransfer Decrypt(byte[] data, string password)
    {
        ValidatePassword(password);
        if (data.Length is < 54 or > MaximumBytes + 52 || !data.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Format de arhivă Vela necunoscut.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, data.AsSpan(8, 16), Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[data.Length - 52];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(24, 12), data.AsSpan(52), data.AsSpan(36, 16), plain, Magic);
            var payload = JsonSerializer.Deserialize<LibraryTransfer>(plain) ?? throw new InvalidDataException("Arhivă goală.");
            if (payload.FormatVersion != 1 || payload.Purpose is not ("backup" or "sync") || payload.Bookmarks is null ||
                payload.Bookmarks.Count > BookmarkHtml.MaxEntries || payload.ReadingList?.Count > BookmarkHtml.MaxEntries || payload.Preferences is null) throw new InvalidDataException("Arhiva nu este compatibilă.");
            return payload;
        }
        catch (CryptographicException ex) { throw new InvalidDataException("Parola este greșită sau fișierul a fost modificat.", ex); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    private static void ValidatePassword(string password)
    { if (password.Length is < 12 or > 1024) throw new ArgumentException("Folosește o parolă de cel puțin 12 caractere (maximum 1.024)."); }
    public static void Write(string path, LibraryTransfer payload, string password)
    {
        var bytes = Encrypt(payload, password);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static LibraryTransfer Read(string path, string password)
    {
        if (new FileInfo(path).Length > MaximumBytes + 52) throw new InvalidDataException("Arhiva este prea mare.");
        return Decrypt(File.ReadAllBytes(path), password);
    }
    public static string DeviceId
    {
        get
        {
            var path = Path.Combine(AppPaths.Data, "sync-device-id.txt");
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path), out var saved)) return saved.ToString("N");
            var id = Guid.NewGuid().ToString("N"); AtomicFile.Write(path, id); return id;
        }
    }
    public static IReadOnlyList<LibraryTransfer> ReadSyncFolder(string folder, string password)
    {
        Directory.CreateDirectory(folder);
        var paths = Directory.EnumerateFiles(folder, "vela-*.sync", SearchOption.TopDirectoryOnly).Take(33).ToArray();
        if (paths.Length > 32) throw new InvalidDataException("Folderul conține prea multe dispozitive sau fișiere de sincronizare.");
        if (paths.Sum(p => new FileInfo(p).Length) > 64 * 1024 * 1024) throw new InvalidDataException("Fișierele de sincronizare depășesc împreună 64 MB.");
        var result = new List<LibraryTransfer>();
        foreach (var path in paths)
        {
            var item = Read(path, password);
            if (item.Purpose != "sync") throw new InvalidDataException("Un fișier din folder nu este o arhivă de sincronizare.");
            BookmarksStore.ValidateSnapshot(item.Bookmarks);
            if (item.ReadingList is not null) BookmarksStore.ValidateSnapshot(item.ReadingList);
            result.Add(item);
        }
        return result;
    }
}
