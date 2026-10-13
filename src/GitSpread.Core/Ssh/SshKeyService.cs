using GitSpread.Core.Processes;

namespace GitSpread.Core.Ssh;

public sealed record SshPublicKey(string PublicKeyPath, string Type, string Comment)
{
    public string FileName => Path.GetFileName(PublicKeyPath);
    public string PrivateKeyPath => PublicKeyPath[..^".pub".Length];
}

/// <summary>Clés SSH locales : liste des clés publiques et génération (ssh-keygen).</summary>
public sealed class SshKeyService(IProcessRunner runner, string? sshDirectory = null)
{
    public string SshDirectory { get; } = sshDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public IReadOnlyList<SshPublicKey> ListPublicKeys()
    {
        if (!Directory.Exists(SshDirectory)) return [];
        return Directory.EnumerateFiles(SshDirectory, "*.pub")
            .Select(TryRead)
            .OfType<SshPublicKey>()
            .OrderBy(k => k.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Génère une clé ed25519. Refuse d'écraser une clé existante.</summary>
    public async Task<SshPublicKey> GenerateAsync(string fileName, string comment, string passphrase,
        CancellationToken cancellationToken = default)
    {
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Length == 0)
            throw new ArgumentException($"Nom de fichier de clé invalide : « {fileName} ».", nameof(fileName));

        var privatePath = Path.Combine(SshDirectory, fileName);
        if (File.Exists(privatePath) || File.Exists(privatePath + ".pub"))
            throw new InvalidOperationException($"Une clé existe déjà : {privatePath}. Choisis un autre nom.");

        Directory.CreateDirectory(SshDirectory);
        var result = await runner.RunAsync(new ProcessRequest("ssh-keygen",
            ["-t", "ed25519", "-C", comment, "-f", privatePath, "-N", passphrase, "-q"]), cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"ssh-keygen a échoué : {result.StandardError.Trim()}");

        return TryRead(privatePath + ".pub")
            ?? throw new InvalidOperationException($"Clé générée mais illisible : {privatePath}.pub");
    }

    /// <summary>Nom de fichier libre et parlant pour la clé d'un compte (ex. id_ed25519_samuelnitram).</summary>
    public string SuggestFileName(string login)
    {
        var baseName = $"id_ed25519_{login.ToLowerInvariant()}";
        var name = baseName;
        for (var i = 2; File.Exists(Path.Combine(SshDirectory, name)) || File.Exists(Path.Combine(SshDirectory, name + ".pub")); i++)
            name = $"{baseName}_{i}";
        return name;
    }

    private static SshPublicKey? TryRead(string path)
    {
        try
        {
            var parts = File.ReadAllText(path).Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && (parts[0].StartsWith("ssh-") || parts[0].StartsWith("ecdsa-") || parts[0].StartsWith("sk-"))
                ? new SshPublicKey(path, parts[0], parts.Length == 3 ? parts[2] : "")
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
