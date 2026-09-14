using Zen;

namespace ZenOperator;

// Reimplements the "eligible queue" selection rules from the agent instructions so every agent
// gets the same answer instead of re-deriving locks/breaks/bug-priority by hand each time.
public static class EligibilityEngine
{
    public static List<TaskCard> GetEligible(BoardColumn branch)
    {
        if (branch.IsLocked || branch.IsArchive) return [];
        var eligible = new List<TaskCard>();
        foreach (var card in branch.Tasks)
        {
            if (card.Kind == CardKind.Break) break;
            if (card.IsLocked || card.IsDone) continue;
            if (card.Kind == CardKind.Note) continue;
            eligible.Add(card);
        }
        return eligible.OrderByDescending(card => card.Tags.Contains("bug", StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static List<(BoardColumn Branch, List<TaskCard> Tasks)> GetEligibleProject(ProjectDocument document) =>
        document.Branches
            .Where(branch => !branch.IsLocked && !branch.IsArchive)
            .Select(branch => (branch, GetEligible(branch)))
            .Where(entry => entry.Item2.Count > 0)
            .ToList();
}
