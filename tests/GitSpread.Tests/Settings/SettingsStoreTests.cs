using GitSpread.Core.Settings;

namespace GitSpread.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("GitSpread settings ").FullName;
    private string FilePath => Path.Combine(_dir, "sous dossier", "settings.json");

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var settings = new SettingsStore(FilePath).Load();

        Assert.Empty(settings.RootFolders);
        Assert.Contains("node_modules", settings.ExcludedFolders);
    }

    [Fact]
    public void Round_trips_settings()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { RootFolders = [@"C:\xampp\htdocs", @"D:\Mes projets"], ScanDepth = 2 });

        var loaded = store.Load();

        Assert.Equal([@"C:\xampp\htdocs", @"D:\Mes projets"], loaded.RootFolders);
        Assert.Equal(2, loaded.ScanDepth);
        Assert.Equal(2, loaded.ToScanOptions().MaxDepth);
    }

    [Fact]
    public void Corrupted_file_is_backed_up_and_defaults_are_used()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ pas du json");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Empty(settings.RootFolders);
        Assert.Equal("{ pas du json", File.ReadAllText(FilePath + ".bak"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public class KnownEmailsTests
{
    [Fact]
    public void Remembers_confirmed_emails_per_account_and_survives_a_round_trip()
    {
        var settings = new AppSettings();
        settings.AddKnownEmail("github.com", "SmartDev-Web", "smart@gmail.com");
        settings.AddKnownEmail("github.com", "smartdev-web", "SMART@gmail.com"); // doublon ignoré

        var path = Path.Combine(Directory.CreateTempSubdirectory("GitSpread emails ").FullName, "settings.json");
        new SettingsStore(path).Save(settings);
        var loaded = new SettingsStore(path).Load();

        Assert.True(loaded.IsKnownEmail("github.com", "SmartDev-Web", "smart@GMAIL.com"));
        Assert.False(loaded.IsKnownEmail("github.com", "SamuelnitraM", "smart@gmail.com"));
        Assert.Single(loaded.AccountEmails.Single().Value);
    }
}
