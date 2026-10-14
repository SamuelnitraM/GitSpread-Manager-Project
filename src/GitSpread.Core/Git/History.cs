using System.Text.RegularExpressions;

namespace GitSpread.Core.Git;

/// <summary>Un commit de l'historique, tel que l'affiche l'explorateur.</summary>
/// <param name="Refs">Branches (locales ou distantes, ex. « origin/main »), tags et HEAD qui pointent sur ce commit.</param>
public sealed record HistoryCommit(string Sha, IReadOnlyList<string> Parents, string Author, string AuthorEmail, DateTimeOffset Date,
    string Subject, IReadOnlyList<string> Refs)
{
    public string ShortSha => Sha[..Math.Min(7, Sha.Length)];
    public bool IsMerge => Parents.Count > 1;
    public bool IsHead => Refs.Contains("HEAD");
}

/// <summary>Nature d'une branche, pour sa couleur : la principale, une branche « classique », ou une branche de maintenance.</summary>
public enum BranchKind { Default, Classic, Maintenance }

/// <summary>Couleur d'un trait ou d'un point du graphe : une famille et une nuance dans cette famille.</summary>
public readonly record struct BranchColor(BranchKind Kind, int Shade);

/// <summary>Classe les branches par leur nom, sans jamais le modifier.</summary>
public static partial class BranchKinds
{
    /// <summary>Termes de maintenance reconnus au début d'un nom de branche (ex. fix/…, hotfix-…, debug_…, bugfix/…).</summary>
    public static readonly IReadOnlyList<string> DefaultMaintenanceTerms =
        ["fix", "hotfix", "bugfix", "bug", "debug", "patch", "maintenance", "maint", "repair", "correctif", "correction", "support"];

    public static BranchKind Of(string branch, string? defaultBranch, IEnumerable<string>? maintenanceTerms = null)
    {
        if (defaultBranch is not null && branch.Equals(defaultBranch, StringComparison.OrdinalIgnoreCase)) return BranchKind.Default;
        // Le premier mot du nom (avant / - _ .) décide : « fix/login », « hotfix-2.1 », « debugs », « bugfix_x ».
        var first = Separators().Split(branch.ToLowerInvariant()).FirstOrDefault(w => w.Length > 0) ?? "";
        return (maintenanceTerms ?? DefaultMaintenanceTerms).Any(t => t.Length > 0 && first.StartsWith(t.ToLowerInvariant()))
            ? BranchKind.Maintenance
            : BranchKind.Classic;
    }

    /// <summary>Nom de branche sans le préfixe du distant (« origin/feature/x » → « feature/x »).</summary>
    public static string Local(string reference, IEnumerable<string> remotes) =>
        remotes.FirstOrDefault(r => reference.StartsWith(r + "/", StringComparison.Ordinal)) is { } remote ? reference[(remote.Length + 1)..] : reference;

    /// <summary>Branche fusionnée, d'après le message du commit de fusion (« Merge branch 'x' », « Merge pull request #1 from moi/x »…).</summary>
    public static string? MergedBranch(string subject)
    {
        var m = MergeSubject().Match(subject);
        return m.Success ? m.Groups["b"].Value : null;
    }

    [GeneratedRegex(@"[/\-_.]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^Merge branch '(?<b>[^']+)'|^Merge remote-tracking branch '[^/']+/(?<b>[^']+)'|^Merge pull request #\d+ from [^/\s]+/(?<b>\S+)")]
    private static partial Regex MergeSubject();
}

/// <summary>Un morceau de trait dans une ligne du graphe, entre deux couloirs (colonnes).</summary>
/// <param name="FromLane">Couloir en haut du morceau.</param>
/// <param name="ToLane">Couloir en bas du morceau.</param>
public readonly record struct GraphSegment(int FromLane, int ToLane, BranchColor Color);

/// <summary>Dessin d'une ligne du graphe : le point du commit et les traits de la moitié haute et de la moitié basse.</summary>
public sealed record GraphRow(int NodeLane, BranchColor NodeColor, IReadOnlyList<GraphSegment> Top, IReadOnlyList<GraphSegment> Bottom, int LaneCount);

/// <summary>Résultat de la mise en page : une ligne par commit, la branche de chaque commit et la couleur de chaque branche.</summary>
public sealed record HistoryLayout(IReadOnlyList<GraphRow> Rows, IReadOnlyDictionary<string, string> CommitBranch, IReadOnlyDictionary<string, BranchColor> BranchColors)
{
    public int MaxLanes => Rows.Count == 0 ? 0 : Rows.Max(r => r.LaneCount);
}

/// <summary>
/// Place les commits dans des couloirs, comme les outils git graphiques : chaque branche garde sa colonne,
/// les fusions et les départs de branche relient les colonnes. Chaque commit appartient à la branche qui
/// l'a créé (chaîne des premiers parents depuis le bout de la branche), ce qui donne sa couleur.
/// </summary>
public static class HistoryGraph
{
    /// <param name="commits">Commits du plus récent au plus ancien, enfants avant parents (git log --date-order).</param>
    /// <param name="branchTips">Branches (nom sans préfixe de distant) et leur dernier commit, par ordre de priorité : la principale d'abord.</param>
    public static HistoryLayout Layout(IReadOnlyList<HistoryCommit> commits, IReadOnlyList<(string Branch, string Sha)> branchTips,
        string? defaultBranch, IEnumerable<string>? maintenanceTerms = null)
    {
        var terms = maintenanceTerms?.ToList();
        var byId = commits.ToDictionary(c => c.Sha);

        // 1. Branche de chaque commit : on suit les premiers parents depuis le bout de chaque branche (la principale d'abord).
        var owner = new Dictionary<string, string>();
        void Claim(string branch, string? sha)
        {
            while (sha is not null && byId.TryGetValue(sha, out var c) && owner.TryAdd(sha, branch))
                sha = c.Parents.Count > 0 ? c.Parents[0] : null;
        }
        foreach (var (branch, sha) in branchTips) Claim(branch, sha);
        // Branches supprimées depuis : retrouvées par le message de leur fusion (« Merge branch 'x' »).
        foreach (var c in commits.Where(c => c.IsMerge))
            if (BranchKinds.MergedBranch(c.Subject) is { } merged)
                Claim(merged, c.Parents[1]);

        // 2. Couleur de chaque branche : sa famille, puis une nuance différente par branche dans cette famille.
        var colors = new Dictionary<string, BranchColor>();
        var shades = new Dictionary<BranchKind, int>();
        BranchColor ColorOf(string? branch)
        {
            if (branch is null) return new BranchColor(BranchKind.Classic, -1); // branche inconnue : trait neutre
            if (colors.TryGetValue(branch, out var known)) return known;
            var kind = BranchKinds.Of(branch, defaultBranch, terms);
            var shade = shades.GetValueOrDefault(kind);
            shades[kind] = shade + 1;
            return colors[branch] = new BranchColor(kind, shade);
        }
        foreach (var (branch, _) in branchTips) ColorOf(branch);
        BranchColor CommitColor(string sha) => ColorOf(owner.GetValueOrDefault(sha));

        // 3. Couloirs : chaque couloir attend un commit (le prochain parent à dessiner) avec la couleur de son trait.
        var lanes = new List<(string Sha, BranchColor Color)?>();
        var rows = new List<GraphRow>(commits.Count);
        foreach (var commit in commits)
        {
            var nodeColor = CommitColor(commit.Sha);
            var node = lanes.FindIndex(l => l?.Sha == commit.Sha);
            if (node < 0) node = FreeLane(lanes); // bout de branche : rien n'arrive d'en haut

            // Moitié haute : les traits qui attendaient ce commit le rejoignent, les autres continuent tout droit.
            var top = new List<GraphSegment>();
            var passing = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is not { } lane) continue;
                if (lane.Sha == commit.Sha)
                {
                    top.Add(new GraphSegment(i, node, lane.Color));
                    lanes[i] = null;
                }
                else
                {
                    top.Add(new GraphSegment(i, i, lane.Color));
                    passing.Add(i);
                }
            }

            // Moitié basse : le commit part vers ses parents. Le trait vers le premier parent garde la couleur
            // de la branche du commit ; celui vers un parent fusionné prend la couleur de la branche de ce parent.
            var bottom = new List<GraphSegment>();
            for (var p = 0; p < commit.Parents.Count; p++)
            {
                var parent = commit.Parents[p];
                var color = p == 0 ? nodeColor : CommitColor(parent);
                // La branche garde son couloir jusqu'au parent (les couloirs se rejoignent sur la ligne du parent) ;
                // un parent fusionné déjà attendu ailleurs est rejoint directement.
                var existing = p == 0 ? -1 : lanes.FindIndex(l => l?.Sha == parent);
                if (existing >= 0)
                {
                    bottom.Add(new GraphSegment(node, existing, color));
                    continue;
                }
                var lane = p == 0 ? node : FreeLane(lanes);
                lanes[lane] = (parent, color);
                bottom.Add(new GraphSegment(node, lane, color));
            }
            foreach (var i in passing) bottom.Add(new GraphSegment(i, i, lanes[i]!.Value.Color));

            while (lanes.Count > 0 && lanes[^1] is null) lanes.RemoveAt(lanes.Count - 1);
            var width = top.Concat(bottom).Select(s => Math.Max(s.FromLane, s.ToLane) + 1).Append(node + 1).Max();
            rows.Add(new GraphRow(node, nodeColor, top, bottom, width));
        }
        return new HistoryLayout(rows, owner, colors);
    }

    private static int FreeLane(List<(string Sha, BranchColor Color)?> lanes)
    {
        var free = lanes.FindIndex(l => l is null);
        if (free >= 0) return free;
        lanes.Add(null);
        return lanes.Count - 1;
    }
}
