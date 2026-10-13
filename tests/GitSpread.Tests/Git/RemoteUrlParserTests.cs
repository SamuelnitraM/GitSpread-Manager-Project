using GitSpread.Core.Git;
using GitSpread.Core.Ssh;

namespace GitSpread.Tests.Git;

public class RemoteUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/SamuelnitraM/SmartStock.git", RemoteProtocol.Https)]
    [InlineData("https://github.com/SamuelnitraM/SmartStock", RemoteProtocol.Https)]
    [InlineData("https://github.com/SamuelnitraM/SmartStock/", RemoteProtocol.Https)]
    [InlineData("https://SamuelnitraM@github.com/SamuelnitraM/SmartStock.git", RemoteProtocol.Https)]
    [InlineData("https://user:pass@www.github.com/SamuelnitraM/SmartStock.git", RemoteProtocol.Https)]
    [InlineData("http://github.com/SamuelnitraM/SmartStock.git", RemoteProtocol.Https)]
    [InlineData("git@github.com:SamuelnitraM/SmartStock.git", RemoteProtocol.Ssh)]
    [InlineData("github.com:SamuelnitraM/SmartStock", RemoteProtocol.Ssh)]
    [InlineData("ssh://git@github.com/SamuelnitraM/SmartStock.git", RemoteProtocol.Ssh)]
    [InlineData("ssh://git@ssh.github.com:443/SamuelnitraM/SmartStock.git", RemoteProtocol.Ssh)]
    [InlineData("git://github.com/SamuelnitraM/SmartStock.git", RemoteProtocol.Git)]
    [InlineData("  https://GitHub.com/SamuelnitraM/SmartStock.git  ", RemoteProtocol.Https)]
    public void Understands_every_github_url_form(string url, RemoteProtocol protocol)
    {
        var remote = RemoteUrlParser.Parse(url);

        Assert.Equal(protocol, remote.Protocol);
        Assert.Equal("github.com", remote.Host);
        Assert.True(remote.IsGitHub);
        Assert.Equal("SamuelnitraM/SmartStock", remote.FullName);
    }

    [Fact]
    public void Resolves_ssh_aliases_through_ssh_config()
    {
        var ssh = SshConfig.Parse(["Host github-perso", "  HostName github.com", "  IdentityFile ~/.ssh/id_perso"]);

        var remote = RemoteUrlParser.Parse("git@github-perso:SamuelnitraM/SmartStock.git", ssh);

        Assert.Equal("github.com", remote.Host);
        Assert.Equal("github-perso", remote.SshAlias);
        Assert.Equal("SamuelnitraM", remote.Owner);
    }

    [Fact]
    public void Unknown_alias_stays_an_unknown_host()
    {
        var remote = RemoteUrlParser.Parse("git@github-perso:SamuelnitraM/SmartStock.git");

        Assert.Equal("github-perso", remote.Host);
        Assert.False(remote.IsGitHub);
    }

    [Theory]
    [InlineData(@"C:\depots\projet.git")]
    [InlineData("C:/depots/projet.git")]
    [InlineData(@"\\serveur\partage\projet.git")]
    [InlineData("/srv/git/projet.git")]
    [InlineData("../autre-projet")]
    [InlineData("file:///C:/depots/projet.git")]
    public void Local_paths_are_not_mistaken_for_ssh(string url)
    {
        var remote = RemoteUrlParser.Parse(url);

        Assert.Equal(RemoteProtocol.File, remote.Protocol);
        Assert.False(remote.IsGitHub);
    }

    [Fact]
    public void Other_hosts_keep_their_full_owner_path()
    {
        var remote = RemoteUrlParser.Parse("https://gitlab.com/groupe/sous-groupe/projet.git");

        Assert.Equal("gitlab.com", remote.Host);
        Assert.Equal("groupe/sous-groupe", remote.Owner);
        Assert.Equal("projet", remote.Repository);
    }
}
