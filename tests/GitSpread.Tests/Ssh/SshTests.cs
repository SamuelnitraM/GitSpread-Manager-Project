using GitSpread.Core.Processes;
using GitSpread.Core.Ssh;

namespace GitSpread.Tests.Ssh;

public class SshConfigTests
{
    [Fact]
    public void Reads_aliases_with_their_real_host_and_key()
    {
        var config = SshConfig.Parse(
        [
            "# clés par compte",
            "Host github-pro github-travail",
            "    HostName github.com",
            "    User git",
            "    IdentityFile ~/.ssh/id_pro",
            "",
            "Host *",
            "    AddKeysToAgent yes",
            "Host autre",
            "HostName=exemple.org",
        ]);

        Assert.Equal("github.com", config.ResolveHost("github-pro"));
        Assert.Equal("github.com", config.ResolveHost("GITHUB-TRAVAIL"));
        Assert.Equal("~/.ssh/id_pro", config.Find("github-pro")!.IdentityFile);
        Assert.Equal("exemple.org", config.ResolveHost("autre"));
        Assert.Equal("inconnu", config.ResolveHost("inconnu"));
        Assert.Null(config.Find("*"));
    }

    [Fact]
    public void First_value_wins_like_ssh()
    {
        var config = SshConfig.Parse(["Host gh", "HostName github.com", "HostName ailleurs.com"]);

        Assert.Equal("github.com", config.ResolveHost("gh"));
    }
}

public sealed class SshKeyServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("GitSpread ssh ").FullName;

    [Fact]
    public void Lists_only_valid_public_keys()
    {
        File.WriteAllText(Path.Combine(_dir, "id_ed25519.pub"), "ssh-ed25519 AAAAC3Nza moi@pc\n");
        File.WriteAllText(Path.Combine(_dir, "id_ecdsa.pub"), "ecdsa-sha2-nistp256 AAAAE2Vj\n");
        File.WriteAllText(Path.Combine(_dir, "notes.pub"), "pas une clé");
        File.WriteAllText(Path.Combine(_dir, "id_ed25519"), "privée");

        var keys = new SshKeyService(new ProcessRunner(), _dir).ListPublicKeys();

        Assert.Equal(["id_ecdsa.pub", "id_ed25519.pub"], keys.Select(k => k.FileName));
        Assert.Equal("moi@pc", keys[1].Comment);
        Assert.Equal(Path.Combine(_dir, "id_ed25519"), keys[1].PrivateKeyPath);
    }

    [Fact]
    public async Task Generates_an_ed25519_key_with_ssh_keygen()
    {
        var service = new SshKeyService(new ProcessRunner(), _dir);

        var key = await service.GenerateAsync("id_test avec espace", "SamuelnitraM@GitSpread", passphrase: "");

        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Equal("SamuelnitraM@GitSpread", key.Comment);
        Assert.True(File.Exists(key.PrivateKeyPath));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_key()
    {
        File.WriteAllText(Path.Combine(_dir, "id_existante"), "privée");
        var service = new SshKeyService(new ProcessRunner(), _dir);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("id_existante", "x", ""));
        Assert.Equal("privée", File.ReadAllText(Path.Combine(_dir, "id_existante")));
    }

    [Fact]
    public void Suggests_a_free_file_name_per_account()
    {
        File.WriteAllText(Path.Combine(_dir, "id_ed25519_samuelnitram.pub"), "ssh-ed25519 AAAA");
        var service = new SshKeyService(new ProcessRunner(), _dir);

        Assert.Equal("id_ed25519_samuelnitram_2", service.SuggestFileName("SamuelnitraM"));
        Assert.Equal("id_ed25519_smartdev-web", service.SuggestFileName("SmartDev-Web"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
