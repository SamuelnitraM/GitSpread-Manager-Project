using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;

namespace GitSpread.Tests.Git;

public class RepositoryNameTests
{
    [Theory]
    [InlineData("Mon projet", "Mon-projet")]
    [InlineData("Mon  projet de test", "Mon--projet-de-test")] // comme GitHub : un tiret par espace
    [InlineData("tab\tulation", "tab-ulation")]
    [InlineData("sans-espace", "sans-espace")]
    public void Spaces_become_dashes_like_on_github(string input, string expected) =>
        Assert.Equal(expected, RepositoryName.ReplaceSpaces(input));

    [Theory]
    [InlineData("", "Donne un nom")]
    [InlineData("..", "ne sont pas des noms")]
    [InlineData("Écran", "« É »")]
    [InlineData("a/b#c", "« / » « # »")] // tous les caractères fautifs sont cités
    [InlineData("projet#1", "« # »")]
    public void Explains_what_is_wrong(string name, string expected) =>
        Assert.Contains(expected, RepositoryName.Validate(name));

    [Fact]
    public void Too_long_names_give_the_current_length() =>
        Assert.Contains("(101 actuellement)", RepositoryName.Validate(new string('a', 101)));

    [Theory]
    [InlineData("Mon-projet")]
    [InlineData("gitspread_test.v2")]
    public void Valid_names_have_no_error(string name) => Assert.Null(RepositoryName.Validate(name));
}

public class BranchNameTests
{
    [Fact]
    public void Spaces_become_dashes() => Assert.Equal("correctif-stock-v2", BranchName.ReplaceSpaces("correctif stock v2"));

    [Theory]
    [InlineData("", "Donne un nom")]
    [InlineData("a b", "Pas d'espace")]
    [InlineData("fix:bug", "« : »")]
    [InlineData("-option", "commencer par « - »")]
    [InlineData("/debut", "« / »")]
    [InlineData("a//b", "« / »")]
    [InlineData("a..b", "« .. »")]
    [InlineData("a@{b", "« @{ »")]
    [InlineData("branche.lock", ".lock")]
    [InlineData("dossier/.cache", "commencer par « . »")]
    public void Explains_what_git_would_refuse(string name, string expected) =>
        Assert.Contains(expected, BranchName.Validate(name));

    [Theory]
    [InlineData("main")]
    [InlineData("correctif/stock")]
    [InlineData("feature-2026.10")]
    [InlineData("import-gitspread")]
    public void Valid_names_have_no_error(string name) => Assert.Null(BranchName.Validate(name));

    /// <summary>Les noms acceptés ici doivent l'être aussi par git (et inversement pour ceux refusés).</summary>
    [Theory]
    [InlineData("correctif/stock", true)]
    [InlineData("a..b", false)]
    [InlineData("fix:bug", false)]
    [InlineData("branche.lock", false)]
    [InlineData("dossier/.cache", false)]
    public async Task Agrees_with_git_check_ref_format(string name, bool valid)
    {
        var r = await new ProcessRunner().RunAsync(new ProcessRequest("git", ["check-ref-format", "--branch", name]));
        Assert.Equal(valid, r.Succeeded);
        Assert.Equal(valid, BranchName.Validate(name) is null);
    }
}
