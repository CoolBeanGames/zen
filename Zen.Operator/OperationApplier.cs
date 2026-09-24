using System.Globalization;
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
                    if (tag.Equals("in progress", StringComparison.OrdinalIgnoreCase) && TaskBlockingRules.IsBlocked(document, card))
                        throw new InvalidOperationException($"Task #{card.Index} is blocked and cannot be started until every blocker is complete.");
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
            case "addRequirement":
                WithCard(document, operation, card =>
                {
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A requirement cannot be empty.");
                    card.Requirements.Add(new TaskRequirement { Index = card.Requirements.Count + 1, Text = text });
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "editRequirement":
                WithCard(document, operation, card =>
                {
                    var requirement = RequireRequirement(card, Require(operation, "requirementId"));
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A requirement cannot be empty.");
                    requirement.Text = text;
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "removeRequirement":
                WithCard(document, operation, card =>
                {
                    var requirement = RequireRequirement(card, Require(operation, "requirementId"));
                    card.Requirements.Remove(requirement);
                    for (var index = 0; index < card.Requirements.Count; index++) card.Requirements[index].Index = index + 1;
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "setCustomField":
                WithCard(document, operation, card =>
                {
                    var field = RequireCustomField(document, card, Require(operation, "fieldId"));
                    var value = Require(operation, "value");
                    if (field.Type.Equals("files", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("File fields must be changed with a file operation.");
                    if (field.Type.Equals("label", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Label fields are informational and read-only.");
                    if (field.Type.Equals("dropdown", StringComparison.OrdinalIgnoreCase) &&
                        field.Options.Count > 0 && !field.Options.Contains(value, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"'{value}' is not an option for custom field '{field.Name}'.");
                    if (field.Type.Equals("tags", StringComparison.OrdinalIgnoreCase))
                    {
                        var systemTags = card.Tags.Where(IsSystemTag).ToList();
                        var requestedTags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(tag => !IsSystemTag(tag)).Distinct(StringComparer.OrdinalIgnoreCase);
                        card.Tags.Clear();
                        foreach (var tag in systemTags.Concat(requestedTags)) card.Tags.Add(tag);
                    }
                    else if (field.Type.Equals("cluster", StringComparison.OrdinalIgnoreCase))
                    {
                        SetCardCluster(document, card, value);
                    }
                    else if (field.Type.Equals("blocking", StringComparison.OrdinalIgnoreCase))
                    {
                        TaskBlockingRules.SetBlockers(document, card, TaskBlockingRules.ParseIds(value));
                    }
                    else if (field.Type.Equals("list", StringComparison.OrdinalIgnoreCase))
                    {
                        card.CustomValues[field.Id] = CustomListCodec.Serialize(CustomListCodec.Parse(value));
                    }
                    else if (field.Type.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!bool.TryParse(value, out var checkedValue))
                            throw new InvalidOperationException($"Custom checkbox field '{field.Name}' requires true or false.");
                        card.CustomValues[field.Id] = checkedValue ? "true" : "false";
                    }
                    else if (field.Type.Equals("number", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                            throw new InvalidOperationException($"Custom number field '{field.Name}' requires an invariant numeric value.");
                        card.CustomValues[field.Id] = value;
                    }
                    else
                    {
                        card.CustomValues[field.Id] = value;
                    }
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "addCustomListItem":
                WithCustomList(document, operation, items => items.Add(Require(operation, "item")));
                break;
            case "removeCustomListItem":
                WithCustomList(document, operation, items =>
                {
                    var index = operation.Payload["index"]?.GetValue<int>() ?? -1;
                    if (index < 0 || index >= items.Count) throw new InvalidOperationException($"List index {index + 1} is out of range.");
                    items.RemoveAt(index);
                });
                break;
            case "clearCustomList":
                WithCustomList(document, operation, items => items.Clear());
                break;
            case "addCardFile":
                WithCard(document, operation, card =>
                {
                    if (operation.Payload["fieldId"] is JsonNode fieldNode)
                        RequireFileField(document, card, fieldNode.GetValue<string>());
                    card.Files.Add(new CardFile
                    {
                        Id = Require(operation, "id"),
                        Name = Require(operation, "name"),
                        RelativePath = Require(operation, "relativePath"),
                        Size = operation.Payload["size"]?.GetValue<long>() ?? 0
                    });
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "removeCardFile":
                WithCard(document, operation, card =>
                {
                    if (operation.Payload["fieldId"] is JsonNode fieldNode)
                        RequireFileField(document, card, fieldNode.GetValue<string>());
                    var fileId = Require(operation, "fileId");
                    var file = card.Files.FirstOrDefault(item => item.Id.Equals(fileId, StringComparison.OrdinalIgnoreCase) ||
                                                                  item.Name.Equals(fileId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Attachment '{fileId}' was not found on task '{card.Id}'.");
                    card.Files.Remove(file);
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "addCardNote":
                WithCard(document, operation, card =>
                {
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A note cannot be empty.");
                    card.Notes.Add(new TaskNote { Text = text });
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "removeCardNote":
                WithCard(document, operation, card =>
                {
                    var noteId = Require(operation, "noteId");
                    var note = card.Notes.FirstOrDefault(item => item.Id.Equals(noteId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Note '{noteId}' was not found on task '{card.Id}'.");
                    card.Notes.Remove(note);
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "editCardNote":
                WithCard(document, operation, card =>
                {
                    var noteId = Require(operation, "noteId");
                    var note = card.Notes.FirstOrDefault(item => item.Id.Equals(noteId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Note '{noteId}' was not found on task '{card.Id}'.");
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A note cannot be empty.");
                    note.Text = text;
                    card.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "setDone":
                WithCard(document, operation, card => card.IsDone = operation.Payload["isDone"]!.GetValue<bool>());
                break;
            case "setAwaitingFeedback":
                WithCard(document, operation, card => card.IsAwaitingFeedback = operation.Payload["isAwaitingFeedback"]!.GetValue<bool>());
                break;
            case "setPaths":
                if (operation.Payload["latestExePath"] is JsonNode exeNode) document.LatestExePath = exeNode.GetValue<string>();
                if (operation.Payload["latestReleasePath"] is JsonNode releaseNode) document.LatestReleasePath = releaseNode.GetValue<string>();
                break;
            case "addSignature":
                AddSignature(document, operation);
                break;
            case "createCluster":
                CreateCluster(document, operation);
                break;
            case "editCluster":
                EditCluster(document, operation);
                break;
            case "setClusterLocked":
                WithCluster(document, operation, cluster => cluster.IsLocked = operation.Payload["isLocked"]!.GetValue<bool>());
                break;
            case "setClusterCollapsed":
                WithCluster(document, operation, cluster => cluster.IsCollapsed = operation.Payload["isCollapsed"]!.GetValue<bool>());
                break;
            case "deleteCluster":
                DeleteCluster(document, operation);
                break;
            case "archiveCluster":
                ArchiveCluster(document, operation);
                break;
            case "moveCluster":
                MoveCluster(document, operation);
                break;
            case "setTaskCluster":
                WithCard(document, operation, card => SetCardCluster(document, card, Require(operation, "clusterId")));
                break;
            case "addClusterRequirement":
                WithCluster(document, operation, cluster =>
                {
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A cluster requirement cannot be empty.");
                    cluster.Requirements.Add(new TaskRequirement { Index = cluster.Requirements.Count + 1, Text = text });
                    cluster.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "editClusterRequirement":
                WithCluster(document, operation, cluster =>
                {
                    var requirement = RequireClusterRequirement(cluster, Require(operation, "requirementId"));
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A cluster requirement cannot be empty.");
                    requirement.Text = text;
                    cluster.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "removeClusterRequirement":
                WithCluster(document, operation, cluster =>
                {
                    cluster.Requirements.Remove(RequireClusterRequirement(cluster, Require(operation, "requirementId")));
                    for (var index = 0; index < cluster.Requirements.Count; index++) cluster.Requirements[index].Index = index + 1;
                    cluster.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "setClusterRequirement":
                WithCluster(document, operation, cluster =>
                {
                    RequireClusterRequirement(cluster, Require(operation, "requirementId")).IsDone = operation.Payload["isDone"]!.GetValue<bool>();
                    cluster.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "addClusterNote":
                WithCluster(document, operation, cluster =>
                {
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A cluster note cannot be empty.");
                    cluster.Notes.Add(new TaskNote { Text = text });
                    cluster.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "editClusterNote":
                WithCluster(document, operation, cluster =>
                {
                    var note = RequireClusterNote(cluster, Require(operation, "noteId"));
                    var text = Require(operation, "text").Trim();
                    if (text.Length == 0) throw new InvalidOperationException("A cluster note cannot be empty.");
                    note.Text = text;
                    cluster.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "removeClusterNote":
                WithCluster(document, operation, cluster =>
                {
                    cluster.Notes.Remove(RequireClusterNote(cluster, Require(operation, "noteId")));
                    cluster.UpdatedAt = DateTimeOffset.UtcNow;
                });
                break;
            case "addTask":
                AddTask(document, operation);
                break;
            case "editTask":
                EditTask(document, operation);
                break;
            case "setTaskBlockers":
                WithCard(document, operation, card =>
                    TaskBlockingRules.SetBlockers(document, card, ReadTaskIndexes(operation.Payload["blockedByTaskIds"])));
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

    private static void AddSignature(ProjectDocument document, QueuedWrite operation)
    {
        var message = Require(operation, "message").Trim();
        var agent = Require(operation, "agent").Trim();
        var taskToken = Require(operation, "taskId").Trim();
        var signedAtText = Require(operation, "signedAt").Trim();
        if (message.Length == 0) throw new InvalidOperationException("A signature message cannot be empty.");
        if (agent.Length == 0) throw new InvalidOperationException("A signature agent cannot be empty.");
        var card = FindCard(document, taskToken) ?? throw new InvalidOperationException($"Task '{taskToken}' was not found.");
        if (!DateTimeOffset.TryParse(signedAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var signedAt))
            throw new InvalidOperationException("The signature date and time is invalid.");
        document.Signatures.Add(new ProjectSignature
        {
            Message = message,
            Agent = agent,
            TaskId = card.Id,
            SignedAt = signedAt
        });
    }

    private static void EditTask(ProjectDocument document, QueuedWrite operation) => WithCard(document, operation, card =>
    {
        var payload = operation.Payload;
        if (payload["title"] is JsonNode titleNode) card.Title = titleNode.GetValue<string>();
        if (payload["task"] is JsonNode taskNode) card.Task = taskNode.GetValue<string>();
        if (payload["dueDate"] is JsonNode dueNode) card.DueDate = ParseDateOrClear(dueNode);
        if (payload["startedDate"] is JsonNode startedNode) card.StartedDate = ParseDateOrClear(startedNode);
        if (payload["priority"] is JsonNode priorityNode) card.Priority = ParsePriorityOrClear(priorityNode);
        if (payload["cluster"] is JsonNode clusterNode) SetCardCluster(document, card, clusterNode.GetValue<string>());
        if (payload["blockedByTaskIds"] is JsonNode blockedByNode)
            TaskBlockingRules.SetBlockers(document, card, ReadTaskIndexes(blockedByNode));
        if (payload["commit"] is JsonNode commitNode) card.Flags.Commit = commitNode.GetValue<bool>();
        if (payload["build"] is JsonNode buildNode) card.Flags.Build = buildNode.GetValue<bool>();
        if (payload["release"] is JsonNode releaseNode) card.Flags.Release = releaseNode.GetValue<bool>();
        if (payload["merge"] is JsonNode mergeNode) card.Flags.Merge = mergeNode.GetValue<bool>();
    });

    private static void CreateCluster(ProjectDocument document, QueuedWrite operation)
    {
        var name = Require(operation, "name").Trim();
        if (name.Length == 0) throw new InvalidOperationException("A cluster name cannot be empty.");
        if (document.Clusters.Any(cluster => cluster.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Cluster '{name}' already exists.");
        var color = operation.Payload["color"]?.GetValue<string>() ?? "#514890";
        if (!IsHexColor(color)) throw new InvalidOperationException($"'{color}' is not a valid cluster color.");
        var cluster = new ClusterDefinition
        {
            Name = name,
            Description = operation.Payload["description"]?.GetValue<string>() ?? string.Empty,
            Color = color,
            DoNotArchive = operation.Payload["doNotArchive"]?.GetValue<bool>() ?? false
        };
        if (operation.Payload["tags"] is JsonNode tagsNode)
            foreach (var tag in tagsNode.AsArray().Select(node => node!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase)) cluster.Tags.Add(tag);
        if (operation.Payload["requirements"] is JsonNode requirementsNode)
            foreach (var text in requirementsNode.AsArray().Select(node => node!.GetValue<string>()))
                cluster.Requirements.Add(new TaskRequirement { Index = cluster.Requirements.Count + 1, Text = text });
        cluster.Flags.Commit = operation.Payload["commit"]?.GetValue<bool>() ?? false;
        cluster.Flags.Merge = operation.Payload["merge"]?.GetValue<bool>() ?? false;
        cluster.Flags.Build = operation.Payload["build"]?.GetValue<bool>() ?? false;
        cluster.Flags.Release = operation.Payload["release"]?.GetValue<bool>() ?? false;
        document.Clusters.Add(cluster);
    }

    private static void EditCluster(ProjectDocument document, QueuedWrite operation) => WithCluster(document, operation, cluster =>
    {
        var payload = operation.Payload;
        if (payload["name"] is JsonNode nameNode)
        {
            var name = nameNode.GetValue<string>().Trim();
            if (name.Length == 0) throw new InvalidOperationException("A cluster name cannot be empty.");
            if (document.Clusters.Any(other => other != cluster && other.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Cluster '{name}' already exists.");
            cluster.Name = name;
        }
        if (payload["description"] is JsonNode descriptionNode) cluster.Description = descriptionNode.GetValue<string>();
        if (payload["color"] is JsonNode colorNode)
        {
            var color = colorNode.GetValue<string>();
            if (!IsHexColor(color)) throw new InvalidOperationException($"'{color}' is not a valid cluster color.");
            cluster.Color = color;
        }
        if (payload["tags"] is JsonNode tagsNode)
        {
            cluster.Tags.Clear();
            foreach (var tag in tagsNode.AsArray().Select(node => node!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase)) cluster.Tags.Add(tag);
        }
        if (payload["commit"] is JsonNode commitNode) cluster.Flags.Commit = commitNode.GetValue<bool>();
        if (payload["merge"] is JsonNode mergeNode) cluster.Flags.Merge = mergeNode.GetValue<bool>();
        if (payload["build"] is JsonNode buildNode) cluster.Flags.Build = buildNode.GetValue<bool>();
        if (payload["release"] is JsonNode releaseNode) cluster.Flags.Release = releaseNode.GetValue<bool>();
        if (payload["isAwaitingFeedback"] is JsonNode feedbackNode) cluster.IsAwaitingFeedback = feedbackNode.GetValue<bool>();
        if (payload["doNotArchive"] is JsonNode archiveNode) cluster.DoNotArchive = archiveNode.GetValue<bool>();
        cluster.UpdatedAt = DateTimeOffset.UtcNow;
    });

    private static void DeleteCluster(ProjectDocument document, QueuedWrite operation)
    {
        var cluster = RequireCluster(document, Require(operation, "clusterId"));
        foreach (var card in document.Branches.SelectMany(branch => branch.Tasks).Where(card => card.ClusterId == cluster.Id)) card.ClusterId = null;
        document.Clusters.Remove(cluster);
    }

    private static void ArchiveCluster(ProjectDocument document, QueuedWrite operation)
    {
        var cluster = RequireCluster(document, Require(operation, "clusterId"));
        if (cluster.IsLocked) throw new InvalidOperationException($"Cluster '{cluster.Name}' is locked.");
        if (cluster.DoNotArchive) throw new InvalidOperationException($"Cluster '{cluster.Name}' is marked do not archive.");
        MoveClusterCards(document, cluster, document.Branches.First(branch => branch.IsArchive));
    }

    private static void MoveCluster(ProjectDocument document, QueuedWrite operation)
    {
        var cluster = RequireCluster(document, Require(operation, "clusterId"));
        if (cluster.IsLocked) throw new InvalidOperationException($"Cluster '{cluster.Name}' is locked.");
        var destinationId = Require(operation, "branchId");
        var destination = document.Branches.FirstOrDefault(branch => branch.Id.Equals(destinationId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Branch '{destinationId}' was not found.");
        if (destination.IsLocked) throw new InvalidOperationException($"Branch '{destinationId}' is locked.");
        if (destination.IsArchive && cluster.DoNotArchive) throw new InvalidOperationException($"Cluster '{cluster.Name}' is marked do not archive.");
        MoveClusterCards(document, cluster, destination);
    }

    private static void MoveClusterCards(ProjectDocument document, ClusterDefinition cluster, BoardColumn destination)
    {
        var cards = document.Branches.SelectMany(branch => branch.Tasks).Where(card => card.ClusterId == cluster.Id).ToList();
        foreach (var card in cards)
            foreach (var branch in document.Branches) branch.Tasks.Remove(card);
        foreach (var card in cards)
        {
            card.IsDone = destination.IsArchive;
            destination.Tasks.Add(card);
        }
        cluster.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void SetCardCluster(ProjectDocument document, TaskCard card, string idOrName)
    {
        if (idOrName.Equals("clear", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(idOrName))
        {
            card.ClusterId = null;
            return;
        }
        card.ClusterId = RequireCluster(document, idOrName).Id;
        card.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void WithCluster(ProjectDocument document, QueuedWrite operation, Action<ClusterDefinition> mutate)
    {
        mutate(RequireCluster(document, Require(operation, "clusterId")));
    }

    private static ClusterDefinition RequireCluster(ProjectDocument document, string idOrName)
    {
        var byId = document.Clusters.FirstOrDefault(cluster => cluster.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var byName = document.Clusters.Where(cluster => cluster.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            > 1 => throw new InvalidOperationException($"Cluster name '{idOrName}' is ambiguous; use its immutable id."),
            _ => throw new InvalidOperationException($"Cluster '{idOrName}' was not found.")
        };
    }

    private static TaskRequirement RequireClusterRequirement(ClusterDefinition cluster, string idOrIndex) =>
        cluster.Requirements.FirstOrDefault(requirement => requirement.Id.Equals(idOrIndex, StringComparison.OrdinalIgnoreCase) ||
                                                           (int.TryParse(idOrIndex, out var index) && requirement.Index == index))
        ?? throw new InvalidOperationException($"Requirement '{idOrIndex}' was not found on cluster '{cluster.Name}'.");

    private static TaskNote RequireClusterNote(ClusterDefinition cluster, string id) =>
        cluster.Notes.FirstOrDefault(note => note.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Note '{id}' was not found on cluster '{cluster.Name}'.");

    private static bool IsHexColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] == '#' && value.Length is 4 or 5 or 7 or 9 && value[1..].All(Uri.IsHexDigit);

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

    private static CustomFieldDefinition RequireCustomField(ProjectDocument document, TaskCard card, string fieldId)
    {
        if (string.IsNullOrWhiteSpace(card.CustomTypeId))
            throw new InvalidOperationException($"Task '{card.Id}' is not a custom card.");
        var definition = document.CustomCardTypes.FirstOrDefault(item => item.Id.Equals(card.CustomTypeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Custom card definition '{card.CustomTypeId}' was not found.");
        return definition.Fields.FirstOrDefault(field => field.Id.Equals(fieldId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Custom field '{fieldId}' was not found on task '{card.Id}'.");
    }

    private static void WithCustomList(ProjectDocument document, QueuedWrite operation, Action<List<string>> mutate)
    {
        WithCard(document, operation, card =>
        {
            var field = RequireCustomField(document, card, Require(operation, "fieldId"));
            if (!field.Type.Equals("list", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Custom field '{field.Name}' is not a list.");
            var items = CustomListCodec.Parse(card.CustomValues.GetValueOrDefault(field.Id, field.DefaultValue));
            mutate(items);
            card.CustomValues[field.Id] = CustomListCodec.Serialize(items);
            card.UpdatedAt = DateTimeOffset.UtcNow;
        });
    }

    private static TaskRequirement RequireRequirement(TaskCard card, string idOrIndex) =>
        card.Requirements.FirstOrDefault(requirement => requirement.Id.Equals(idOrIndex, StringComparison.OrdinalIgnoreCase) ||
                                                        (int.TryParse(idOrIndex, out var index) && requirement.Index == index))
        ?? throw new InvalidOperationException($"Requirement '{idOrIndex}' was not found on task '{card.Id}'.");

    private static void RequireFileField(ProjectDocument document, TaskCard card, string fieldId)
    {
        var field = RequireCustomField(document, card, fieldId);
        if (!field.Type.Equals("files", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Custom field '{field.Name}' is type '{field.Type}', not 'files'.");
    }

    private static bool IsSystemTag(string tag) => tag.Equals("bug", StringComparison.OrdinalIgnoreCase) ||
                                                    tag.Equals("in progress", StringComparison.OrdinalIgnoreCase);

    private static void AddTask(ProjectDocument document, QueuedWrite operation)
    {
        var branchId = Require(operation, "branchId");
        var branch = document.Branches.FirstOrDefault(b => b.Id.Equals(branchId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Branch '{branchId}' was not found.");
        if (branch.IsLocked) throw new InvalidOperationException($"Branch '{branchId}' is locked.");

        var payload = operation.Payload;
        var kind = payload["kind"] is JsonNode kindNode && Enum.TryParse<CardKind>(kindNode.GetValue<string>(), true, out var parsedKind)
            ? parsedKind
            : CardKind.Task;
        var card = new TaskCard
        {
            Id = Guid.NewGuid().ToString("N"),
            Index = document.NextCardIndex,
            Kind = kind,
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
        if (payload["cluster"] is JsonNode clusterNode) SetCardCluster(document, card, clusterNode.GetValue<string>());

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
        if (payload["blockedByTaskIds"] is JsonNode blockedByNode)
            TaskBlockingRules.SetBlockers(document, card, ReadTaskIndexes(blockedByNode));
    }

    private static IReadOnlyList<int> ReadTaskIndexes(JsonNode? node) =>
        node?.AsArray().Select(item => item?.GetValue<int>() ?? 0).ToList() ?? [];

    private static string Require(QueuedWrite operation, string key) =>
        operation.Payload[key]?.GetValue<string>() ?? throw new InvalidOperationException($"Missing required field '{key}'.");
}
