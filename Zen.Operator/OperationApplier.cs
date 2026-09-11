using System.Text.Json.Nodes;
using Zen;

namespace ZenOperator;

public static class OperationApplier
{
    public static void Apply(ProjectDocument document, QueuedWrite operation)
    {
        switch (operation.Kind)
        {
            case "addTag":
                WithCard(document, operation, card =>
                {
                    var tag = Require(operation, "tag");
                    if (!card.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) card.Tags.Add(tag);
                });
                break;
            case "removeTag":
                WithCard(document, operation, card =>
                {
                    var tag = Require(operation, "tag");
                    for (var i = card.Tags.Count - 1; i >= 0; i--)
                        if (card.Tags[i].Equals(tag, StringComparison.OrdinalIgnoreCase)) card.Tags.RemoveAt(i);
                });
                break;
            case "setTags":
                WithCard(document, operation, card =>
                {
                    var tags = operation.Payload["tags"]!.AsArray().Select(node => node!.GetValue<string>());
                    card.Tags.Clear();
                    foreach (var tag in tags) card.Tags.Add(tag);
                });
                break;
            case "setRequirement":
                WithCard(document, operation, card =>
                {
                    var requirementId = Require(operation, "requirementId");
                    var isDone = operation.Payload["isDone"]!.GetValue<bool>();
                    var requirement = card.Requirements.FirstOrDefault(r => r.Id.Equals(requirementId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Requirement '{requirementId}' not found on task '{card.Id}'.");
                    requirement.IsDone = isDone;
                });
                break;
            case "setDone":
                WithCard(document, operation, card => card.IsDone = operation.Payload["isDone"]!.GetValue<bool>());
                break;
            case "setPaths":
                if (operation.Payload["latestExePath"] is JsonNode exeNode) document.LatestExePath = exeNode.GetValue<string>();
                if (operation.Payload["latestReleasePath"] is JsonNode releaseNode) document.LatestReleasePath = releaseNode.GetValue<string>();
                break;
            case "addTask":
                AddTask(document, operation);
                break;
            default:
                throw new InvalidOperationException($"Unknown queued operation kind '{operation.Kind}'.");
        }
    }

    private static void WithCard(ProjectDocument document, QueuedWrite operation, Action<TaskCard> mutate)
    {
        var taskId = Require(operation, "taskId");
        var card = FindCard(document, taskId) ?? throw new InvalidOperationException($"Task '{taskId}' was not found.");
        mutate(card);
    }

    public static TaskCard? FindCard(ProjectDocument document, string idOrIndex)
    {
        var cards = document.Branches.SelectMany(branch => branch.Tasks);
        var byId = cards.FirstOrDefault(card => card.Id.Equals(idOrIndex, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        return int.TryParse(idOrIndex, out var index) ? cards.FirstOrDefault(card => card.Index == index) : null;
    }

    private static void AddTask(ProjectDocument document, QueuedWrite operation)
    {
        var branchId = Require(operation, "branchId");
        var branch = document.Branches.FirstOrDefault(b => b.Id.Equals(branchId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Branch '{branchId}' was not found.");
        if (branch.IsLocked) throw new InvalidOperationException($"Branch '{branchId}' is locked.");

        var payload = operation.Payload;
        var card = new TaskCard
        {
            Id = Guid.NewGuid().ToString("N"),
            Index = document.NextCardIndex,
            Kind = CardKind.Task,
            Title = payload["title"]?.GetValue<string>() ?? "",
            Task = payload["task"]?.GetValue<string>() ?? "",
            IsDone = false,
            IsLocked = false
        };
        document.NextCardIndex = card.Index + 1;

        if (payload["tags"] is JsonNode tagsNode)
            foreach (var tag in tagsNode.AsArray()) card.Tags.Add(tag!.GetValue<string>());

        if (payload["requirements"] is JsonNode requirementsNode)
            foreach (var text in requirementsNode.AsArray())
                card.Requirements.Add(new TaskRequirement { Text = text!.GetValue<string>() });

        card.Flags.Commit = payload["commit"]?.GetValue<bool>() ?? false;
        card.Flags.Build = payload["build"]?.GetValue<bool>() ?? false;
        card.Flags.Release = payload["release"]?.GetValue<bool>() ?? false;
        card.Flags.Merge = payload["merge"]?.GetValue<bool>() ?? false;

        var isBug = card.Tags.Contains("bug", StringComparer.OrdinalIgnoreCase);
        if (isBug)
        {
            var insertAt = branch.Tasks.ToList().FindIndex(existing => !existing.Tags.Contains("bug", StringComparer.OrdinalIgnoreCase));
            branch.Tasks.Insert(insertAt < 0 ? branch.Tasks.Count : insertAt, card);
        }
        else
        {
            branch.Tasks.Add(card);
        }
    }

    private static string Require(QueuedWrite operation, string key) =>
        operation.Payload[key]?.GetValue<string>() ?? throw new InvalidOperationException($"Missing required field '{key}'.");
}
