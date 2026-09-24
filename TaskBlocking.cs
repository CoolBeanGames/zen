namespace Zen;

public enum TaskBlockerStatus
{
    Valid,
    Missing,
    Archived,
    WrongBranch,
    WrongCluster,
    Self,
    NotTask,
    Circular
}

public sealed record TaskBlockerResult(int Index, TaskCard? Task, TaskBlockerStatus Status, string Message)
{
    public bool IsValid => Status == TaskBlockerStatus.Valid;
}

public static class TaskBlockingRules
{
    public static IReadOnlyList<int> ParseIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase)) return [];

        var result = new List<int>();
        foreach (var token in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (token.Length == 0 || !int.TryParse(token, out var index) || index <= 0)
                throw new FormatException($"'{token}' is not a positive numeric task ID.");
            if (!result.Contains(index)) result.Add(index);
        }
        return result;
    }

    public static IReadOnlyList<TaskBlockerResult> Evaluate(
        ProjectDocument document,
        TaskCard target,
        IEnumerable<int> indexes,
        string? targetClusterId = null,
        bool useClusterOverride = false)
    {
        var targetBranch = FindBranch(document, target);
        var clusterId = NormalizeCluster(useClusterOverride ? targetClusterId : target.ClusterId);
        var cards = document.Branches.SelectMany(branch => branch.Tasks.Select(card => (Branch: branch, Card: card))).ToList();
        var results = new List<TaskBlockerResult>();

        foreach (var index in indexes.Distinct())
        {
            var matches = cards.Where(item => item.Card.Index == index).ToList();
            if (matches.Count == 0)
            {
                results.Add(new(index, null, TaskBlockerStatus.Missing, $"Task #{index} does not exist."));
                continue;
            }

            var match = matches[0];
            if (ReferenceEquals(match.Card, target) || match.Card.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(index, match.Card, TaskBlockerStatus.Self, "A task cannot block itself."));
                continue;
            }
            if (match.Branch.IsArchive || match.Card.IsDone)
            {
                results.Add(new(index, match.Card, TaskBlockerStatus.Archived, $"Task #{index} is archived."));
                continue;
            }
            if (match.Card.Kind != CardKind.Task)
            {
                results.Add(new(index, match.Card, TaskBlockerStatus.NotTask, $"#{index} is not a task card."));
                continue;
            }
            if (targetBranch is null || !ReferenceEquals(match.Branch, targetBranch))
            {
                results.Add(new(index, match.Card, TaskBlockerStatus.WrongBranch, $"Task #{index} is in another branch."));
                continue;
            }
            if (!string.Equals(NormalizeCluster(match.Card.ClusterId), clusterId, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(index, match.Card, TaskBlockerStatus.WrongCluster, $"Task #{index} is not in the same cluster."));
                continue;
            }
            if (WouldCreateCycle(document, target, match.Card))
            {
                results.Add(new(index, match.Card, TaskBlockerStatus.Circular, $"Task #{index} would create a circular dependency."));
                continue;
            }

            results.Add(new(index, match.Card, TaskBlockerStatus.Valid, $"Task #{index} is a valid blocker."));
        }
        return results;
    }

    public static void SetBlockers(ProjectDocument document, TaskCard target, IEnumerable<int> indexes, string? targetClusterId = null, bool useClusterOverride = false)
    {
        if (target.Kind != CardKind.Task) throw new InvalidOperationException("Only task cards can have blocking dependencies.");
        var requested = indexes.Distinct().ToList();
        var results = Evaluate(document, target, requested, targetClusterId, useClusterOverride);
        var invalid = results.FirstOrDefault(result => !result.IsValid);
        if (invalid is not null) throw new InvalidOperationException(invalid.Message);

        target.BlockedByTaskIds.Clear();
        foreach (var index in requested) target.BlockedByTaskIds.Add(index);
        if (requested.Count > 0)
            for (var index = target.Tags.Count - 1; index >= 0; index--)
                if (target.Tags[index].Equals("in progress", StringComparison.OrdinalIgnoreCase)) target.Tags.RemoveAt(index);
        target.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static bool IsBlocked(ProjectDocument document, TaskCard target)
    {
        if (target.BlockedByTaskIds.Count == 0) return false;
        var targetBranch = FindBranch(document, target);
        if (targetBranch is null || targetBranch.IsArchive) return false;

        foreach (var index in target.BlockedByTaskIds.Distinct())
        {
            var blocker = targetBranch.Tasks.FirstOrDefault(card => card.Index == index);
            if (blocker is null || blocker.Kind != CardKind.Task ||
                !string.Equals(NormalizeCluster(blocker.ClusterId), NormalizeCluster(target.ClusterId), StringComparison.OrdinalIgnoreCase)) return true;
            if (blocker.IsDone) continue;
            return true;
        }
        return false;
    }

    public static void PruneInvalidDependencies(ProjectDocument document)
    {
        foreach (var branch in document.Branches)
        {
            foreach (var card in branch.Tasks)
            {
                card.BlockedByTaskIds ??= [];
                if (card.Kind != CardKind.Task || branch.IsArchive || card.IsDone)
                {
                    card.BlockedByTaskIds.Clear();
                    continue;
                }

                var valid = card.BlockedByTaskIds
                    .Distinct()
                    .Where(index => branch.Tasks.Any(blocker => blocker.Index == index && blocker.Kind == CardKind.Task &&
                        !blocker.IsDone && !ReferenceEquals(blocker, card) &&
                        string.Equals(NormalizeCluster(blocker.ClusterId), NormalizeCluster(card.ClusterId), StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                card.BlockedByTaskIds.Clear();
                foreach (var index in valid) card.BlockedByTaskIds.Add(index);
            }
        }
    }

    public static BoardColumn? FindBranch(ProjectDocument document, TaskCard card) =>
        document.Branches.FirstOrDefault(branch => branch.Tasks.Any(candidate => ReferenceEquals(candidate, card) || candidate.Id.Equals(card.Id, StringComparison.OrdinalIgnoreCase)));

    private static bool WouldCreateCycle(ProjectDocument document, TaskCard target, TaskCard candidate)
    {
        var byIndex = document.Branches.SelectMany(branch => branch.Tasks).ToDictionary(card => card.Index);
        var pending = new Stack<TaskCard>();
        var visited = new HashSet<int>();
        pending.Push(candidate);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current.Index)) continue;
            if (current.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var index in current.BlockedByTaskIds)
                if (byIndex.TryGetValue(index, out var next)) pending.Push(next);
        }
        return false;
    }

    private static string? NormalizeCluster(string? clusterId) => string.IsNullOrWhiteSpace(clusterId) ? null : clusterId;
}
