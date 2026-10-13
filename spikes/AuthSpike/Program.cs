// Étape 0 — Spike d'authentification multi-comptes.
//
// Question : peut-on faire un push HTTPS avec le compte B alors que le compte A est actif dans gh,
// sans gh auth switch (état global) et sans toucher à la config git de l'utilisateur ?
//
// Tout est non destructif : les push sont en --dry-run (GitHub exige quand même
// l'authentification en écriture pour annoncer git-receive-pack), aucun repo n'est créé.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitSpread.AuthSpike;

const string Host = "github.com";
const string SpikeRef = "refs/heads/gitspread-auth-spike";
var gitTimeout = TimeSpan.FromSeconds(45);

// ---------------------------------------------------------------- environnement
Section("Environnement");
Line((await Proc.Run("git", ["--version"])).StdOut.Trim());
Line((await Proc.Run("gh", ["--version"])).StdOut.Split('\n')[0].Trim());
var helpers = await Proc.Run("git", ["config", "--show-origin", "--get-all", "credential.helper"]);
Line($"credential.helper : {(helpers.StdOut.Trim() is { Length: > 0 } h ? h.Replace('\n', ' ') : "(aucun)")}");

// ---------------------------------------------------------------- comptes gh
Section("Comptes connectés (gh auth status --json hosts)");
var accounts = await ReadAccounts();
foreach (var a in accounts) Line($"{a.Login,-20} actif={a.Active}");
if (accounts.Count < 2) { Line("⚠ Il faut au moins deux comptes connectés pour ce spike."); return 1; }
var activeBefore = accounts.Single(a => a.Active).Login;

// ---------------------------------------------------------------- tokens par compte
Section("Token par compte (gh auth token --user X) vérifié via l'API /user");
var tokens = new Dictionary<string, string>();
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("GitSpread-AuthSpike");
foreach (var a in accounts)
{
    var t = await Proc.Run("gh", ["auth", "token", "--hostname", Host, "--user", a.Login]);
    if (!t.Ok) { Line($"{a.Login,-20} ✗ {Redact(t.StdErr.Trim())}"); continue; }
    tokens[a.Login] = t.StdOut.Trim();

    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens[a.Login]);
    using var resp = await http.SendAsync(req);
    var login = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
        .TryGetProperty("login", out var l) ? l.GetString() : $"HTTP {(int)resp.StatusCode}";
    Line($"{a.Login,-20} → API répond login={login} {(login == a.Login ? "✓" : "✗")}");
}

// ---------------------------------------------------------------- helper gh et username
Section("gh auth git-credential respecte-t-il le username demandé ?");
var ghHelperHonoursUser = true;
foreach (var a in accounts.Where(a => tokens.ContainsKey(a.Login)))
{
    var r = await Proc.Run("gh", ["auth", "git-credential", "get"],
        stdin: $"protocol=https\nhost={Host}\nusername={a.Login}\n\n");
    var pwd = r.StdOut.Split('\n').FirstOrDefault(x => x.StartsWith("password="))?["password=".Length..].Trim();
    var same = pwd == tokens[a.Login];
    ghHelperHonoursUser &= same;
    Line($"username={a.Login,-20} → {(same ? "token de ce compte ✓" : pwd is null ? "aucun mot de passe ✗" : "token d'un AUTRE compte ✗")}");
}

// ---------------------------------------------------------------- repos cibles
Section("Repo cible par compte (premier repo non archivé dont il est propriétaire)");
var targets = new Dictionary<string, string>();
foreach (var a in accounts)
{
    var r = await Proc.Run("gh", ["repo", "list", a.Login, "--limit", "1", "--no-archived", "--json", "nameWithOwner"]);
    var name = r.Ok ? JsonDocument.Parse(r.StdOut).RootElement.EnumerateArray().FirstOrDefault() : default;
    if (name.ValueKind == JsonValueKind.Object)
    {
        targets[a.Login] = name.GetProperty("nameWithOwner").GetString()!;
        Line($"{a.Login,-20} → {targets[a.Login]}");
    }
    else Line($"{a.Login,-20} → aucun repo trouvé");
}

// ---------------------------------------------------------------- dépôt local temporaire
// Chemin volontairement piégeux : espaces + accents.
var workDir = Path.Combine(Path.GetTempPath(), $"GitSpread spike éà {Guid.NewGuid():N}"[..^24]);
Directory.CreateDirectory(workDir);
await Git(workDir, null, "init", "-q");
await Git(workDir, null, "-c", "user.name=GitSpread Spike", "-c", "user.email=spike@gitspread.local",
    "commit", "--allow-empty", "-q", "-m", "spike");
Section("Dépôt local temporaire");
Line(workDir);

var ghExe = (await Proc.Run("where", ["gh"])).StdOut.Split('\n')[0].Trim().Replace('\\', '/');

// ---------------------------------------------------------------- matrice de push --dry-run
var strategies = new (string Name, Func<string, string, (string Url, Dictionary<string, string> Env)> Build)[]
{
    ("Référence : config actuelle (GCM)", (_, repo) =>
        ($"https://{Host}/{repo}.git", NonInteractive())),

    ("A : token injecté (http.extraHeader via GIT_CONFIG_*)", (account, repo) =>
        ($"https://{Host}/{repo}.git", NonInteractive(
            ("credential.helper", ""),
            ($"http.https://{Host}/.extraHeader", $"Authorization: Basic {B64($"{account}:{tokens[account]}")}")))),

    ("B : helper gh + username dans l'URL", (account, repo) =>
        ($"https://{account}@{Host}/{repo}.git", NonInteractive(
            ("credential.helper", ""),
            ("credential.helper", $"!\"{ghExe}\" auth git-credential")))),
};

var verdicts = new List<(string Strategy, bool Controlled)>();
foreach (var (name, build) in strategies)
{
    Section($"push --dry-run — {name}");
    var controlled = true;
    var isBaseline = name.StartsWith("Référence");
    List<string> asAccounts = isBaseline ? [activeBefore] : tokens.Keys.ToList();
    foreach (var account in asAccounts)
    foreach (var (owner, repo) in targets)
    {
        var (url, env) = build(account, repo);
        var r = await Git(workDir, env, "push", "--dry-run", "--porcelain", url, $"HEAD:{SpikeRef}");
        var deniedTo = Regex.Match(r.StdErr, @"denied to ([^\s.]+)").Groups[1].Value;
        string outcome = r switch
        {
            { TimedOut: true } => "⏱ délai dépassé (prompt bloqué ?)",
            { Ok: true } => "✓ écriture autorisée",
            _ when deniedTo != "" => $"✗ refusé, authentifié comme {deniedTo}",
            _ => $"✗ {FirstUsefulLine(r.StdErr)}",
        };
        var label = isBaseline ? "(identité choisie par GCM)" : $"en tant que {account}";
        Line($"{label,-30} → {repo,-40} {outcome}");

        if (!isBaseline)
        {
            // L'identité est maîtrisée si : on écrit sur ses propres repos, et un refus éventuel
            // désigne bien le compte demandé (jamais un autre).
            if (owner == account && !r.Ok) controlled = false;
            if (deniedTo != "" && deniedTo != account) controlled = false;
            if (r.TimedOut) controlled = false;
        }
    }
    if (!isBaseline) verdicts.Add((name, controlled));
}

// ---------------------------------------------------------------- effets de bord
Section("Effets de bord");
var activeAfter = (await ReadAccounts()).Single(a => a.Active).Login;
Line($"Compte actif gh avant={activeBefore} après={activeAfter} {(activeBefore == activeAfter ? "✓ inchangé" : "✗ MODIFIÉ")}");
var helpersAfter = await Proc.Run("git", ["config", "--show-origin", "--get-all", "credential.helper"]);
Line($"Config credential git {(helpersAfter.StdOut == helpers.StdOut ? "✓ inchangée" : "✗ MODIFIÉE")}");

// ---------------------------------------------------------------- SSH (informatif)
Section("SSH (informatif)");
var sshCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
List<string> aliases = File.Exists(sshCfg) ? ParseSshAliases(File.ReadAllLines(sshCfg)) : [];
Line(aliases.Count == 0 ? "Aucun alias Host → github.com dans ~/.ssh/config" : $"Alias github.com : {string.Join(", ", aliases)}");
foreach (var hostAlias in aliases.Prepend(Host).Distinct())
{
    var r = await Proc.Run("ssh", ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
        "-o", "StrictHostKeyChecking=accept-new", $"git@{hostAlias}"], timeout: TimeSpan.FromSeconds(20));
    var m = Regex.Match(r.StdErr, @"Hi ([^!]+)!");
    Line($"ssh git@{hostAlias,-25} → {(m.Success ? $"✓ authentifié comme {m.Groups[1].Value}" : FirstUsefulLine(r.StdErr))}");
}

// ---------------------------------------------------------------- verdict
Section("Verdict");
foreach (var (s, ok) in verdicts) Line($"{(ok ? "✓" : "✗")} {s}");
Line($"{(ghHelperHonoursUser ? "✓" : "✗")} gh auth git-credential renvoie le token du username demandé");

TryDelete(workDir);
return 0;

// ================================================================= helpers

async Task<List<(string Login, bool Active)>> ReadAccounts()
{
    var r = await Proc.Run("gh", ["auth", "status", "--json", "hosts"]);
    var list = new List<(string, bool)>();
    if (JsonDocument.Parse(r.StdOut).RootElement.GetProperty("hosts").TryGetProperty(Host, out var arr))
        foreach (var e in arr.EnumerateArray().Where(e => e.GetProperty("state").GetString() == "success"))
            list.Add((e.GetProperty("login").GetString()!, e.GetProperty("active").GetBoolean()));
    return list;
}

Task<ProcResult> Git(string cwd, Dictionary<string, string>? env, params string[] args) =>
    Proc.Run("git", args, cwd, env, timeout: gitTimeout);

// Jamais de prompt (terminal ou fenêtre GCM) : l'app doit échouer proprement, pas bloquer.
static Dictionary<string, string> NonInteractive(params (string Key, string Value)[] config)
{
    var env = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_CONFIG_COUNT"] = config.Length.ToString(),
    };
    for (var i = 0; i < config.Length; i++)
    {
        env[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
        env[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
    }
    return env;
}

static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

static List<string> ParseSshAliases(string[] lines)
{
    var result = new List<string>();
    string[] current = [];
    foreach (var raw in lines.Select(l => l.Trim()))
    {
        var parts = raw.Split((char[])[' ', '\t', '='], 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) continue;
        if (parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase)) current = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        else if (parts[0].Equals("HostName", StringComparison.OrdinalIgnoreCase) && parts[1].Trim() == Host) result.AddRange(current);
    }
    return result;
}

static string FirstUsefulLine(string s) => Redact(
    s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("fatal") || l.StartsWith("remote:") || l.Contains("denied"))
    ?? s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(aucune sortie)");

static string Redact(string s) => Regex.Replace(s, @"gh[opusr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}", "<token masqué>");

static void Section(string title) => Console.WriteLine($"\n== {title} ==");
static void Line(string s) => Console.WriteLine("   " + Redact(s));

static void TryDelete(string dir)
{
    try
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
    catch (Exception e) { Line($"(nettoyage incomplet de {dir} : {e.Message})"); }
}
