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
            case "editTask":
                EditTask(document, operation);
                break;
            case "setTaskLocked":
                WithCard(document, operation, card => card.IsLocked = operation.Payload["isLocked"]!.GetValue<bool>());
                break;
            case "deleteTask":
                DeleteTask(document, operation);
                break;
            case "moveTask":
                MoveTask(document, operation);
                break;
            case "createBranch":
                CreateBranch(document, operation);
                break;
            case "setBranchLocked":
                WithBranch(document, operation, branch => branch.IsLocked = operation.Payload["isLocked"]!.GetValue<bool>());
                break;
            case "renameBranch":
                WithBranch(document, operation, branch => branch.Title = Require(operation, "title"));
                break;
            case "archiveBranch":
                ArchiveBranch(document, operation);
                break;
            default:
                throw new InvalidOperationException($"Unknown queued operation kind '{operation.Kind}'.");
        }
    }

    private static void EditTask(ProjectDocument document, QueuedWrite operation) => WithCard(document, operation, card =>
    {
        var payload = operation.Payload;
        if (payload["title"] is JsonNode titleNode) card.Title = titleNode.GetValue<string>();
        if (payload["task"] is JsonNode taskNode) card.Task = taskNode.GetValue<string>();
        if (payload["dueDate"] is JsonNode dueNode) card.DueDate = ParseDateOrClear(dueNode);
        if (payload["startedDate"] is JsonNode startedNode) card.StartedDate = ParseDateOrClear(startedNode);
        if (payload["priority"] is JsonNode priorityNode) card.Priority = ParsePriorityOrClear(priorityNode);
        if (payload["commit"] is JsonNode commitNode) card.Flags.Commit = commitNode.GetValue<bool>();
        if (payload["build"] is JsonNode buildNode) card.Flags.Build = buildNode.GetValue<bool>();
        if (payload["release"] is JsonNode releaseNode) card.Flags.Release = releaseNode.GetValue<bool>();
        if (payload["merge"] is JsonNode mergeNode) card.Flags.Merge = mergeNode.GetValue<bool>();
    });

    private static DateTime? ParseDateOrClear(JsonNode node)
    {
        var text = node.GetValue<string>();
        return text.Equals("clear", StringComparison.OrdinalIgnoreCase) ? null : DateTime.Parse(text);
    }

    private static CardPriority? ParsePriorityOrClear(JsonNode node)
    {
        var text = node.GetValue<string>();
        return text.Equals("clear", StringComparison.OrdinalIgnoreCase) ? null : Enum.Parse<CardPriority>(text, ignoreCase: true);
    }

    private static void DeleteTask(ProjectDocument document, QueuedWrite operation)
    {
        var taskId = Require(operation, "taskId");
        foreach (var branch in document.Branches)
        {
            var card = branch.Tasks.FirstOrDefault(t => t.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase) ||
                                                          (int.TryParse(taskId, out var index) && t.Index == index));
            if (card is null) continue;
            branch.Tasks.Remove(card);
            return;
        }
        throw new InvalidOperationException($"Task '{taskId}' was not found.");
    }

    private static void MoveTask(ProjectDocument document, QueuedWrite operation)
    {
        var taskId = Require(operation, "taskId");
        var destinationId = Require(operation, "branchId");
        var destination = document.Branches.FirstOrDefault(b => b.Id.Equals(destinationId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Branch '{destinationId}' was not found.");
        foreach (var branch in document.Branches)
        {
            var card = branch.Tasks.FirstOrDefault(t => t.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase) ||
                                                          (int.TryParse(taskId, out var index) && t.Index == index));
            if (card is null) continue;
            branch.Tasks.Remove(card);
            card.IsDone = destination.IsArchive;
            destination.Tasks.Add(card);
            return;
        }
        throw new InvalidOperationException($"Task '{taskId}' was not found.");
    }

    private static void WithBranch(ProjectDocument document, QueuedWrite operation, Action<BoardColumn> mutate)
    {
        var branchId = Require(operation, "branchId");
        var branch = document.Branches.FirstOrDefault(b => b.Id.Equals(branchId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Branch '{branchId}' was not found.");
        mutate(branch);
    }

    private static void CreateBranch(ProjectDocument document, QueuedWrite operation)
    {
        var title = Require(operation, "title");
        var id = Guid.NewGuid().ToString("N");
        var branchName = operation.Payload["branch"]?.GetValue<string>() ?? ProjectStore.CreateBranchName(title, id);
        document.Branches.Insert(Math.Max(0, document.Branches.Count - 1), new BoardColumn
        {
            Id = id,
            Title = title,
            Branch = branchName
        });
    }

    private static void ArchiveBranch(ProjectDocument document, QueuedWrite operation)
    {
        var branchId = Require(operation, "branchId");
        var branch = document.Branches.FirstOrDefault(b => b.Id.Equals(branchId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Branch '{branchId}' was not found.");
        if (branch.IsPermanent) throw new InvalidOperationException($"Branch '{branchId}' is a permanent branch and cannot be archived.");
        var archive = document.Branches.First(b => b.IsArchive);
        foreach (var task in branch.Tasks.ToList())
        {
            task.IsDone = true;
            archive.Tasks.Add(task);
        }
        document.Branches.Remove(branch);
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
