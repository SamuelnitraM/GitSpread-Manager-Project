using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;

namespace GitSpread.Core.Projects;

/// <summary>Ce qu'il faut faire pour publier un projet local sur un compte GitHub.</summary>
public sealed record PublishPlan(string Directory, GitHubAccount Account, string RepositoryName, RepositoryVisibility Visibility)
{
    public string? Description { get; init; }

    /// <summary>Le dossier n'est pas encore un dépôt git : git init d'abord.</summary>
    public bool InitializeRepository { get; init; }

    /// <summary>Modèle GitHub à écrire dans .gitignore (seulement s'il n'en existe pas déjà un).</summary>
    public string? GitignoreTemplate { get; init; }

    /// <summary>Identité de commit à fixer pour ce dépôt avant le premier commit.</summary>
    public GitIdentity? Identity { get; init; }

    /// <summary>Commiter les fichiers non commités avant de pousser.</summary>
    public bool CommitPendingChanges { get; init; } = true;
    public string CommitMessage { get; init; } = "Premier commit";

    public string RemoteName { get; init; } = "origin";
    public string InitialBranch { get; init; } = "main";
}

/// <summary>Enchaîne init → .gitignore → commit → création du repo → remote → push, avec le compte choisi.</summary>
public sealed class ProjectPublisher(GitService git, GitHubService gitHub, GitCredentialResolver credentials)
{
    /// <summary>Commandes qui seront exécutées, pour les montrer avant de lancer.</summary>
    public static IReadOnlyList<string> Preview(PublishPlan plan)
    {
        var steps = new List<string>();
        if (plan.InitializeRepository) steps.Add($"git init -b {plan.InitialBranch}");
        if (plan.GitignoreTemplate is { } t) steps.Add($"(écriture de .gitignore depuis le modèle GitHub « {t} »)");
        if (plan.Identity is { } id) steps.Add($"git config --local user.name \"{id.Name}\"   ·   user.email {id.Email}");
        if (plan.CommitPendingChanges) steps.AddRange(["git add --all", $"git commit -m \"{plan.CommitMessage}\""]);
        steps.Add($"gh repo create {plan.Account.Login}/{plan.RepositoryName} --{(plan.Visibility == RepositoryVisibility.Public ? "public" : "private")}   (avec le compte {plan.Account.Login})");
        steps.Add($"git remote add {plan.RemoteName} https://{plan.Account.Host}/{plan.Account.Login}/{plan.RepositoryName}.git");
        steps.Add($"git push --set-upstream {plan.RemoteName} <branche>   (avec le compte {plan.Account.Login})");
        return steps;
    }

    /// <summary>Publie et renvoie l'URL du nouveau repo. <paramref name="progress"/> reçoit chaque étape en clair.</summary>
    public async Task<string> PublishAsync(PublishPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!RepositoryName.IsValid(plan.RepositoryName))
            throw new ArgumentException($"« {plan.RepositoryName} » n'est pas un nom de repo GitHub valide.");

        if (plan.InitializeRepository)
        {
            progress?.Report("Initialisation de git dans le dossier…");
            (await git.InitAsync(plan.Directory, plan.InitialBranch, ct)).EnsureSuccess();
        }

        var gitignore = Path.Combine(plan.Directory, ".gitignore");
        if (plan.GitignoreTemplate is { } template && !File.Exists(gitignore))
        {
            progress?.Report($"Ajout d'un .gitignore « {template} »…");
            await File.WriteAllTextAsync(gitignore, await gitHub.GetGitignoreTemplateAsync(plan.Account, template, ct), ct);
        }

        if (plan.Identity is { Name: { } name, Email: { } email })
        {
            progress?.Report($"Identité des commits : {name} <{email}>");
            foreach (var r in await git.SetLocalIdentityAsync(plan.Directory, name, email, ct)) r.EnsureSuccess();
        }

        var status = await git.GetStatusAsync(plan.Directory, ct);
        if (plan.CommitPendingChanges && (!status.IsClean || !status.HasCommits))
        {
            progress?.Report("Commit des fichiers du projet…");
            foreach (var r in await git.CommitAsync(plan.Directory, plan.CommitMessage, allowEmpty: !status.HasCommits, cancellationToken: ct))
                r.EnsureSuccess();
            status = await git.GetStatusAsync(plan.Directory, ct);
        }
        if (!status.HasCommits)
            throw new InvalidOperationException("Le projet n'a encore aucun commit : il n'y a rien à publier.");
        if (status.Branch is not { } branch)
            throw new InvalidOperationException("Aucune branche n'est active (HEAD détachée) : impossible de choisir quoi pousser.");
        if ((await git.GetRemotesAsync(plan.Directory, ct)).Any(r => r.Name == plan.RemoteName))
            throw new InvalidOperationException($"Ce projet a déjà un remote « {plan.RemoteName} ».");

        var fullName = $"{plan.Account.Login}/{plan.RepositoryName}";
        if (await gitHub.RepositoryExistsAsync(plan.Account, fullName, ct))
            throw new InvalidOperationException($"Le repo {fullName} existe déjà sur GitHub. Choisis un autre nom.");

        progress?.Report($"Création du repo {fullName} sur GitHub…");
        var url = await gitHub.CreateRepositoryAsync(plan.Account, plan.RepositoryName, plan.Visibility, plan.Description, ct);

        progress?.Report($"Liaison du projet au repo ({plan.RemoteName})…");
        (await git.AddRemoteAsync(plan.Directory, plan.RemoteName, url, ct)).EnsureSuccess();

        progress?.Report($"Envoi de la branche {branch} avec le compte {plan.Account.Login}…");
        var auth = await credentials.EnvironmentForAsync(plan.Account, RemoteUrlParser.Parse(url), ct);
        var push = await git.PushAsync(plan.Directory, plan.RemoteName, branch, setUpstream: true, auth, ct);
        if (!push.Succeeded)
            throw new GitCommandException(
                $"Le repo a bien été créé et relié, mais l'envoi a échoué : {push.Explanation ?? push.Output}. Tu peux réessayer avec « Pousser ».",
                push.Process);

        progress?.Report("Publication terminée.");
        return url;
    }
}
