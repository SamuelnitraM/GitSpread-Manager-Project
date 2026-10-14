using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Git;

/// <summary>Corrections de la revue complète du code (sécurité et fiabilité).</summary>
public sealed class ReviewFixesTests : IDisposable
{
    private readonly TempRepo _repo = new("revue");
    private readonly GitService _git = new(new ProcessRunner());
    private string P => _repo.Path;

    [Fact]
    public async Task File_names_are_never_patterns()
    {
        await _repo.WithCommitAsync();
        _repo.Write("pages/[id].tsx", "page");
        _repo.Write("pages/i.tsx", "autre");

        // Commit du seul « [id].tsx » : « i.tsx » (que le motif [id] désignerait aussi) n'est pas inclus.
        Assert.All(await _git.CommitAsync(P, "Page", ["pages/[id].tsx"]), r => Assert.True(r.Succeeded, r.Output));
        Assert.Equal("pages/[id].tsx", (await _repo.Git("show", "--name-only", "--format=", "HEAD")).Trim());
        Assert.True(await _git.HasUncommittedChangesAsync(P, "pages/i.tsx"));

        // Restaurer « [id].tsx » ne touche pas aux modifications de « i.tsx ».
        await _repo.Git("add", "--all");
        await _repo.Git("commit", "-q", "-m", "i");
        var v1 = await _repo.Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(P, "pages", "[id].tsx"), "modifiée");
        await _repo.Git("commit", "-q", "-am", "v2");
        File.WriteAllText(Path.Combine(P, "pages", "i.tsx"), "travail en cours");
        var restore = await _git.RestoreFileAsync(P, v1, "pages/[id].tsx");
        Assert.True(restore.Succeeded, restore.Output);
        Assert.Equal($"git restore --source={v1} --worktree -- pages/[id].tsx", restore.Command); // affichée telle qu'on la taperait
        Assert.Equal("page", File.ReadAllText(Path.Combine(P, "pages", "[id].tsx")));
        Assert.Equal("travail en cours", File.ReadAllText(Path.Combine(P, "pages", "i.tsx")));
    }

    [Fact]
    public async Task History_of_an_empty_repository_or_one_with_a_file_named_HEAD()
    {
        await _repo.Git("init", "-q", "-b", "main");
        Assert.Empty(await _git.ReadHistoryAsync(P)); // repo vide : pas d'erreur

        await _repo.CommitFileAsync("HEAD", "un fichier qui s'appelle HEAD", "Premier");
        Assert.Equal("Premier", Assert.Single(await _git.ReadHistoryAsync(P)).Subject);
        Assert.Single(await _git.ListUnpushedCommitsAsync(P)); // aucun dépôt distant : le commit n'est pas encore envoyé
    }

    [Fact]
    public async Task Lost_commits_are_counted_on_the_branch_even_if_a_tag_or_folder_has_its_name()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("tag", "docs"); // tag sur main
        await _repo.Git("switch", "-q", "-c", "docs");
        await _repo.CommitFileAsync("docs/guide.md", "g", "Travail unique"); // dossier « docs » aussi
        await _repo.Git("switch", "-q", "main");

        Assert.Equal(1, await _git.CountCommitsOnlyInAsync(P, "refs/heads/docs", ["refs/heads/main"], excludeRemotes: true));
    }

    [Fact]
    public async Task A_vanished_project_folder_gives_a_clear_error()
    {
        var gone = Path.Combine(_repo.Root, "disparu");
        var e = await Assert.ThrowsAsync<GitCommandException>(() => _git.GetStatusAsync(gone));
        Assert.Contains("n'existe plus", e.Message);
    }

    [Fact]
    public void Duplicate_commits_never_break_the_graph()
    {
        var c = new HistoryCommit("a1", [], "Moi", "moi@x", DateTimeOffset.Now, "Un", ["HEAD"]);
        var layout = HistoryGraph.Layout([c, c], [("main", "a1")], "main");
        Assert.Equal(2, layout.Rows.Count);
    }

    [Fact]
    public void Github_api_paths_are_checked_and_encoded()
    {
        Assert.Equal("fix/%2312", GitHubService.BranchPath("fix/#12"));
        Assert.Equal("feature/caf%C3%A9", GitHubService.BranchPath("feature/café"));
        Assert.Equal("SmartDev-Web/stock.v2_x", GitHubService.RepoPath("SmartDev-Web/stock.v2_x"));
        foreach (var bad in new[] { "moi/../autre", "moi/stock?x=1", "a/b/c", "moi/stock#x", "moi /x", "moi.x/stock" })
            Assert.Throws<ArgumentException>(() => GitHubService.RepoPath(bad));
    }

    [Fact]
    public async Task Deleting_a_github_branch_with_a_hash_targets_that_exact_branch()
    {
        var runner = new FakeProcessRunner().When(["auth", "token"], "gho_x").When(["api"], "");
        var gh = new GhCli(runner);
        var account = new GitHubAccount("SmartDev-Web", "github.com", true, true);

        await new GitHubService(gh, new AccountService(gh)).DeleteBranchAsync(account, "SmartDev-Web/stock", "fix#12");

        Assert.Equal(["api", "--method", "DELETE", "repos/SmartDev-Web/stock/git/refs/heads/fix%2312"], runner.Single("api", "--method").Arguments);
    }

    [Fact]
    public async Task Unlinking_a_github_repo_never_touches_another_link()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://gitlab.com/moi/stock.git");
        await _repo.Git("remote", "add", "perso", "https://github.com/SamuelnitraM/stock.git");
        var project = ProjectScanner.ReadProject(P)!;
        var linked = new LinkedProjects(_git, new GitCredentialResolver(new AccountService(new GhCli(new FakeProcessRunner()))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => linked.UnlinkAsync(project, "SamuelnitraM/autre"));
        Assert.True((await linked.UnlinkAsync(project, "SamuelnitraM/stock")).Succeeded);

        Assert.Equal(["origin"], (await _repo.Git("remote")).Split('\n')); // le lien GitLab principal est intact
    }

    [Fact]
    public void Secret_arguments_never_appear_in_the_logged_command_line()
    {
        var request = new ProcessRequest("ssh-keygen", ["-N", "ma phrase secrète", "-C", "moi"]) { Secrets = ["ma phrase secrète"] };
        Assert.Equal("ssh-keygen -N *** -C moi", request.CommandLine);
    }

    [Fact]
    public void Token_is_never_sent_without_checking_the_certificate()
    {
        var env = GitCredentialEnvironment.For("moi", "gho_x", "github.com");
        var config = Enumerable.Range(0, int.Parse(env["GIT_CONFIG_COUNT"])).ToDictionary(i => env[$"GIT_CONFIG_KEY_{i}"], i => env[$"GIT_CONFIG_VALUE_{i}"]);
        Assert.Equal("true", config["http.sslVerify"]);
        Assert.Equal("true", config["http.https://github.com/.sslVerify"]);
    }

    [Fact]
    public async Task A_project_named_like_an_excluded_folder_is_still_found()
    {
        var root = _repo.Sub("racine");
        var build = Directory.CreateDirectory(Path.Combine(root, "build")).FullName;
        await _repo.GitIn(build, "init", "-q");
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep", ".git")); // dépendance : toujours ignorée

        var result = await new ProjectScanner().ScanAsync(new ScanOptions([root]) { MaxDepth = 3 });

        Assert.Equal([build], result.Projects.Select(p => p.Path));
    }

    [Fact]
    public async Task Saving_over_an_existing_file_keeps_it_when_reading_fails()
    {
        await _repo.WithCommitAsync();
        var target = Path.Combine(_repo.Sub("copies"), "garde.txt");
        File.WriteAllText(target, "précieux");

        await Assert.ThrowsAsync<GitCommandException>(() => _git.SaveFileAsync(P, "HEAD", "absent.txt", target));
        Assert.Equal("précieux", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!)); // pas de fichier temporaire oublié
    }

    [Fact]
    public async Task Programs_declared_by_the_repository_itself_are_reported()
    {
        await _repo.WithCommitAsync();
        Assert.Empty(await _git.ListRepositoryProgramsAsync(P)); // dépôt normal : rien

        await _repo.Git("config", "filter.x.clean", "evil.exe");
        await _repo.Git("config", "merge.m.driver", "evil.exe");
        await _repo.Git("config", "core.hooksPath", "C:/ailleurs");
        await _repo.Git("config", "diff.Mon Outil.textconv", "evil.exe"); // nom avec espace : complet
        await _repo.Git("config", "gpg.program", "evil.exe");
        Assert.Equal(["core.hookspath", "diff.Mon Outil.textconv", "filter.x.clean", "gpg.program", "merge.m.driver"],
            (await _git.ListRepositoryProgramsAsync(P)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_repository_cannot_switch_off_certificate_checks_for_the_token()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("config", "http.https://github.com/moi/.sslVerify", "false"); // config locale piégée
        var env = GitCredentialEnvironment.For("moi", "gho_x", "github.com", "https://github.com/moi/stock.git");

        var r = await new ProcessRunner().RunAsync(new ProcessRequest("git", ["config", "--get-urlmatch", "http.sslVerify", "https://github.com/moi/stock.git"])
            { WorkingDirectory = P, Environment = env });

        Assert.Equal("true", r.StandardOutput.Trim());

        // Même avec un nom d'utilisateur ou un port dans l'adresse du dépôt.
        foreach (var url in new[] { "https://moi@github.com/moi/stock.git", "https://github.com:443/moi/stock.git" })
        {
            await _repo.Git("config", $"http.{url}.sslVerify", "false");
            var check = await new ProcessRunner().RunAsync(new ProcessRequest("git", ["config", "--get-urlmatch", "http.sslVerify", url])
                { WorkingDirectory = P, Environment = GitCredentialEnvironment.For("moi", "gho_x", "github.com", url) });
            Assert.Equal("true", check.StandardOutput.Trim());
        }
        Assert.Contains("certificat", GitErrorExplainer.Explain("fatal: unable to access 'https://github.com/moi/stock.git/': SSL certificate problem: self-signed certificate"));
        Assert.DoesNotContain("certificat", GitErrorExplainer.Explain("fatal: unable to access 'https://github.com/x/': schannel: failed to receive handshake, SSL/TLS connection failed"));
    }

    [Fact]
    public void Github_names_with_underscores_are_valid_but_dots_alone_are_not()
    {
        Assert.Equal("octocat_acme/stock", GitHubService.RepoPath("octocat_acme/stock")); // comptes Enterprise Managed Users
        Assert.Equal("octocat_acme", GitHubService.LoginPath("octocat_acme"));
        Assert.Throws<ArgumentException>(() => GitHubService.RepoPath("moi/.."));
        Assert.Throws<ArgumentException>(() => GitHubService.RepoPath("moi/."));
    }

    [Fact]
    public async Task The_link_on_the_right_server_is_the_one_removed()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://ghe.entreprise.fr/moi/stock.git");
        await _repo.Git("remote", "add", "perso", "https://github.com/moi/stock.git");
        var project = ProjectScanner.ReadProject(P)!;
        var linked = new LinkedProjects(_git, new GitCredentialResolver(new AccountService(new GhCli(new FakeProcessRunner()))));

        Assert.True((await linked.UnlinkAsync(project, "moi/stock", host: "github.com")).Succeeded);

        Assert.Equal(["origin"], (await _repo.Git("remote")).Split('\n')); // le lien du serveur d'entreprise est intact
    }

    [Fact]
    public async Task The_token_is_never_sent_when_the_repository_reroutes_its_connection()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://github.com/moi/stock.git");
        await _repo.Git("config", "http.curloptResolve", "github.com:443:127.0.0.1"); // config locale piégée
        var auth = GitCredentialEnvironment.For("moi", "gho_x", "github.com", "https://github.com/moi/stock.git");

        var e = await Assert.ThrowsAsync<GitCommandException>(() => _git.FetchAsync(P, "origin", auth));
        Assert.Contains("n'y envoie pas ton jeton", e.Message);

        // Sans jeton (ex. dépôt d'un autre hôte), rien n'est refusé : git fait son travail normalement.
        await _repo.Git("config", "--unset", "http.curloptResolve");
        await _repo.Git("config", "remote.origin.uploadpack", "evil.exe");
        Assert.Contains("remote.origin.uploadpack", await _git.ListRepositoryProgramsAsync(P)); // signalé dans le panneau
    }

    [Fact]
    public async Task The_token_environment_keeps_https_working()
    {
        // Vrai appel HTTPS (serveur local injoignable) : l'erreur doit être une connexion refusée, jamais un réglage
        // injecté qui casse git (ex. « Unsupported SSL backend » avec http.curloptResolve vide).
        var env = GitCredentialEnvironment.For("moi", "gho_x", "127.0.0.1", "https://127.0.0.1:9/moi/stock.git");
        var r = await new ProcessRunner().RunAsync(new ProcessRequest("git", ["ls-remote", "https://127.0.0.1:9/moi/stock.git"])
            { Environment = env, Timeout = TimeSpan.FromSeconds(20) });

        Assert.False(r.Succeeded);
        Assert.DoesNotContain("SSL backend", r.StandardError);
        Assert.DoesNotContain("bad config", r.StandardError);
    }

    [Fact]
    public async Task Settings_hidden_in_an_included_file_are_seen_too()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://github.com/moi/stock.git");
        var auth = GitCredentialEnvironment.For("moi", "gho_x", "github.com", "https://github.com/moi/stock.git");

        // Vérification du certificat explicitement activée par le dépôt : rien à redire.
        await _repo.Git("config", "http.sslVerify", "true");
        var allowed = await _git.FetchAsync(P, "origin", auth); // échoue plus loin (pas de réseau ni de repo), mais pas sur ce contrôle
        Assert.DoesNotContain("n'y envoie pas ton jeton", allowed.Output);

        // Proxy caché dans un fichier inclus par .git/config : refusé.
        File.WriteAllText(Path.Combine(P, ".git", "cache.cfg"), "[http]\n\tproxy = http://piege:8080\n[filter \"x\"]\n\tclean = evil.exe\n");
        await _repo.Git("config", "include.path", "cache.cfg");
        var e = await Assert.ThrowsAsync<GitCommandException>(() => _git.FetchAsync(P, "origin", auth));
        Assert.Contains("http.proxy", e.Message);
        Assert.Contains("filter.x.clean", await _git.ListRepositoryProgramsAsync(P)); // signalé lui aussi
    }

    [Fact]
    public async Task Switching_off_certificate_checks_in_the_repository_blocks_the_token()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://github.com/moi/stock.git");
        await _repo.Git("config", "http.https://github.com/moi/stock.git/x.sslVerify", "false");
        var auth = GitCredentialEnvironment.For("moi", "gho_x", "github.com", "https://github.com/moi/stock.git");

        var e = await Assert.ThrowsAsync<GitCommandException>(() => _git.FetchAsync(P, "origin", auth));
        Assert.Contains("sslverify", e.Message);

        // Toutes les façons d'écrire « faux » pour git (ex. « 0k ») sont reconnues…
        await _repo.Git("config", "http.https://github.com/moi/stock.git/x.sslVerify", "0k");
        await Assert.ThrowsAsync<GitCommandException>(() => _git.FetchAsync(P, "origin", auth));

        // …et une clé seule (qui veut dire « vrai ») ne bloque rien.
        await _repo.Git("config", "--unset", "http.https://github.com/moi/stock.git/x.sslVerify");
        File.AppendAllText(Path.Combine(P, ".git", "config"), "[http]\n\tsslVerify\n");
        var ok = await _git.FetchAsync(P, "origin", auth); // échoue plus loin (pas de réseau ni de repo), mais pas sur ce contrôle
        Assert.DoesNotContain("n'y envoie pas ton jeton", ok.Output);
    }

    [Fact]
    public async Task Tricky_certificate_settings_cannot_slip_through()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://github.com/moi/stock.git");
        var auth = GitCredentialEnvironment.For("moi", "gho_x", "github.com", "https://github.com/moi/stock.git");

        // Adresse avec un espace : la clé est lue en entier.
        await _repo.Git("config", "http.https://github.com/moi/a b.sslVerify", "false");
        await Assert.ThrowsAsync<GitCommandException>(() => _git.FetchAsync(P, "origin", auth));
        await _repo.Git("config", "--unset", "http.https://github.com/moi/a b.sslVerify");

        // Valeur illisible pour une autre adresse, qui masquerait un « false » ailleurs : refusé plutôt que supposé sûr.
        await _repo.Git("config", "http.https://github.com/evil/.sslVerify", "false");
        await _repo.Git("config", "http.https://ailleurs.invalid/.sslVerify", "peut-etre");
        var e = await Assert.ThrowsAsync<GitCommandException>(() => _git.FetchAsync(P, "origin", auth));
        Assert.Contains("illisible", e.Message);
    }

    [Fact]
    public async Task Network_commands_with_the_token_never_go_into_submodules()
    {
        // La config d'un sous-module n'est pas contrôlée : git ne doit pas y descendre avec le jeton.
        var env = GitCredentialEnvironment.For("moi", "gho_x", "github.com", "https://github.com/moi/stock.git");
        await _repo.WithCommitAsync();
        await _repo.Git("config", "submodule.recurse", "true");
        await _repo.Git("config", "fetch.recurseSubmodules", "yes");

        foreach (var (key, expected) in new[] { ("submodule.recurse", "false"), ("fetch.recurseSubmodules", "false"), ("push.recurseSubmodules", "no") })
        {
            var r = await new ProcessRunner().RunAsync(new ProcessRequest("git", ["config", "--get", key]) { WorkingDirectory = P, Environment = env });
            Assert.Equal(expected, r.StandardOutput.Trim()); // la ligne de commande l'emporte sur la config du dépôt
        }
    }

    public void Dispose() => _repo.Dispose();
}
