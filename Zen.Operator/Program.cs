using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zen;
using ZenOperator;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

try
{
    return Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Run(string[] arguments)
{
    if (arguments.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    var root = ProjectLocator.FindRoot(Directory.GetCurrentDirectory());
    var store = new ProjectStore(root);

    switch (arguments[0])
    {
        case "prompt":
            Console.WriteLine(ReadCanonicalPrompt(store));
            return 0;

        case "project":
        {
            var document = store.OpenOrCreate();
            PrintJson(new
            {
                document.ProjectId,
                document.Name,
                document.NextCardIndex,
                document.LatestExePath,
                document.LatestReleasePath,
                document.TagCatalog
            });
            return 0;
        }

        case "branches":
        {
            var document = store.OpenOrCreate();
            PrintJson(document.Branches.Select(b => new
            {
                b.Id,
                b.Title,
                b.Branch,
                b.IsArchive,
                b.IsLocked,
                b.IsPermanent,
                TaskCount = b.Tasks.Count
            }));
            return 0;
        }

        case "branch" when arguments.Length >= 2 && arguments[1] == "create":
        {
            var title = OptionValue(arguments, "--title") ?? throw new InvalidOperationException("--title is required");
            var payload = new JsonObject { ["title"] = title };
            if (OptionValue(arguments, "--branch") is { } branchName) payload["branch"] = branchName;
            Enqueue(root, "createBranch", payload);
            Console.WriteLine($"ok: created branch '{title}'");
            return 0;
        }

        case "branch" when arguments.Length >= 3 && arguments[1] is "lock" or "unlock":
        {
            Enqueue(root, "setBranchLocked", new JsonObject { ["branchId"] = arguments[2], ["isLocked"] = arguments[1] == "lock" });
            Console.WriteLine($"ok: branch {arguments[2]} {(arguments[1] == "lock" ? "locked" : "unlocked")}");
            return 0;
        }

        case "branch" when arguments.Length >= 3 && arguments[1] == "rename":
        {
            var title = OptionValue(arguments, "--title") ?? throw new InvalidOperationException("--title is required");
            Enqueue(root, "renameBranch", new JsonObject { ["branchId"] = arguments[2], ["title"] = title });
            Console.WriteLine($"ok: branch {arguments[2]} renamed to '{title}'");
            return 0;
        }

        case "branch" when arguments.Length >= 3 && arguments[1] == "archive":
        {
            Enqueue(root, "archiveBranch", new JsonObject { ["branchId"] = arguments[2] });
            Console.WriteLine($"ok: branch {arguments[2]} archived (its tasks moved to Archived)");
            return 0;
        }

        case "tasks":
        {
            var document = store.OpenOrCreate();
            var branchFilter = OptionValue(arguments, "--branch");
            var eligibleOnly = arguments.Contains("--eligible");
            var branches = document.Branches.Where(b => branchFilter is null || b.Id.Equals(branchFilter, StringComparison.OrdinalIgnoreCase));
            var tasks = eligibleOnly
                ? branches.SelectMany(EligibilityEngine.GetEligible)
                : branches.SelectMany(b => b.Tasks);
            PrintJson(tasks.Select(card => DescribeTask(document, card)));
            return 0;
        }

        case "task" when arguments.Length >= 2 && arguments[1] == "create":
        {
            var branchId = OptionValue(arguments, "--branch") ?? throw new InvalidOperationException("--branch is required");
            var title = OptionValue(arguments, "--title") ?? throw new InvalidOperationException("--title is required");
            var task = OptionValue(arguments, "--task") ?? throw new InvalidOperationException("--task is required");
            var tags = new JsonArray(OptionValues(arguments, "--tag").Select(t => (JsonNode)t).ToArray());
            var requirements = new JsonArray(OptionValues(arguments, "--requirement").Select(r => (JsonNode)r).ToArray());
            var payload = new JsonObject
            {
                ["branchId"] = branchId,
                ["title"] = title,
                ["task"] = task,
                ["tags"] = tags,
                ["requirements"] = requirements,
                ["commit"] = arguments.Contains("--commit"),
                ["build"] = arguments.Contains("--build"),
                ["release"] = arguments.Contains("--release"),
                ["merge"] = arguments.Contains("--merge")
            };
            Enqueue(root, "addTask", payload);
            Console.WriteLine($"ok: created task '{title}' on branch {branchId}");
            return 0;
        }

        case "task" when arguments.Length >= 3 && arguments[1] == "edit":
        {
            var id = arguments[2];
            var payload = new JsonObject { ["taskId"] = id };
            if (OptionValue(arguments, "--title") is { } title) payload["title"] = title;
            if (OptionValue(arguments, "--task") is { } task) payload["task"] = task;
            if (OptionValue(arguments, "--due") is { } due) payload["dueDate"] = due;
            if (OptionValue(arguments, "--started") is { } started) payload["startedDate"] = started;
            if (OptionValue(arguments, "--priority") is { } priority) payload["priority"] = priority;
            if (arguments.Contains("--commit")) payload["commit"] = true;
            if (arguments.Contains("--no-commit")) payload["commit"] = false;
            if (arguments.Contains("--build")) payload["build"] = true;
            if (arguments.Contains("--no-build")) payload["build"] = false;
            if (arguments.Contains("--release")) payload["release"] = true;
            if (arguments.Contains("--no-release")) payload["release"] = false;
            if (arguments.Contains("--merge")) payload["merge"] = true;
            if (arguments.Contains("--no-merge")) payload["merge"] = false;
            Enqueue(root, "editTask", payload);
            Console.WriteLine($"ok: edited task {id}");
            return 0;
        }

        case "task" when arguments.Length >= 3 && arguments[1] is "lock" or "unlock":
        {
            Enqueue(root, "setTaskLocked", new JsonObject { ["taskId"] = arguments[2], ["isLocked"] = arguments[1] == "lock" });
            Console.WriteLine($"ok: task {arguments[2]} {(arguments[1] == "lock" ? "locked" : "unlocked")}");
            return 0;
        }

        case "task" when arguments.Length >= 3 && arguments[1] == "delete":
        {
            Enqueue(root, "deleteTask", new JsonObject { ["taskId"] = arguments[2] });
            Console.WriteLine($"ok: deleted task {arguments[2]}");
            return 0;
        }

        case "task" when arguments.Length >= 3 && arguments[1] == "move":
        {
            var branchId = OptionValue(arguments, "--to") ?? throw new InvalidOperationException("--to is required");
            Enqueue(root, "moveTask", new JsonObject { ["taskId"] = arguments[2], ["branchId"] = branchId });
            Console.WriteLine($"ok: moved task {arguments[2]} to branch {branchId}");
            return 0;
        }

        case "task":
        {
            if (arguments.Length < 2) return Fail("usage: task <id-or-index> | task create|edit|lock|unlock|delete|move ...");
            var document = store.OpenOrCreate();
            var card = OperationApplier.FindCard(document, arguments[1]);
            if (card is null) return Fail($"task '{arguments[1]}' not found");
            PrintJson(DescribeTask(document, card));
            return 0;
        }

        case "custom" when arguments.Length >= 3 && arguments[1] is "pull" or "fields":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[2]);
            PrintJson(DescribeTask(document, card));
            return 0;
        }

        case "custom" when arguments.Length >= 2 && arguments[1] == "types":
        {
            var document = store.OpenOrCreate();
            PrintJson(document.CustomCardTypes.Select(definition => new
            {
                definition.Id,
                definition.Name,
                AgentInstructions = definition.Instructions,
                definition.Fields
            }));
            return 0;
        }

        case "custom" when arguments.Length >= 4 && arguments[1] == "get":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[2]);
            var field = RequireCustomField(document, card, arguments[3]);
            PrintJson(DescribeCustomField(card, field));
            return 0;
        }

        case "custom" when arguments.Length >= 4 && arguments[1] == "set":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[2]);
            var field = RequireCustomField(document, card, arguments[3]);
            if (field.Type.Equals("files", StringComparison.OrdinalIgnoreCase))
                return Fail("file fields use 'custom file add|remove', not 'custom set'");
            if (field.Type.Equals("label", StringComparison.OrdinalIgnoreCase))
                return Fail("label fields are informational and read-only");
            var value = OptionValue(arguments, "--value") ?? throw new InvalidOperationException("--value is required");
            Enqueue(root, "setCustomField", new JsonObject { ["taskId"] = card.Id, ["fieldId"] = field.Id, ["value"] = value });
            Console.WriteLine($"ok: set custom field '{field.Name}' on {card.Id}");
            return 0;
        }

        case "custom" when arguments.Length >= 5 && arguments[1] == "list" && arguments[2] == "add":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[3]);
            var field = RequireCustomField(document, card, arguments[4]);
            if (!field.Type.Equals("list", StringComparison.OrdinalIgnoreCase)) return Fail($"custom field '{field.Name}' is not a list");
            var item = OptionValue(arguments, "--item") ?? throw new InvalidOperationException("--item is required");
            Enqueue(root, "addCustomListItem", new JsonObject { ["taskId"] = card.Id, ["fieldId"] = field.Id, ["item"] = item });
            Console.WriteLine($"ok: added item to custom list '{field.Name}' on {card.Id}");
            return 0;
        }

        case "custom" when arguments.Length >= 5 && arguments[1] == "list" && arguments[2] == "remove":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[3]);
            var field = RequireCustomField(document, card, arguments[4]);
            if (!field.Type.Equals("list", StringComparison.OrdinalIgnoreCase)) return Fail($"custom field '{field.Name}' is not a list");
            var indexText = OptionValue(arguments, "--index") ?? throw new InvalidOperationException("--index is required");
            if (!int.TryParse(indexText, out var index) || index < 1) return Fail("--index must be a one-based positive integer");
            Enqueue(root, "removeCustomListItem", new JsonObject { ["taskId"] = card.Id, ["fieldId"] = field.Id, ["index"] = index - 1 });
            Console.WriteLine($"ok: removed item {index} from custom list '{field.Name}' on {card.Id}");
            return 0;
        }

        case "custom" when arguments.Length >= 5 && arguments[1] == "list" && arguments[2] == "clear":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[3]);
            var field = RequireCustomField(document, card, arguments[4]);
            if (!field.Type.Equals("list", StringComparison.OrdinalIgnoreCase)) return Fail($"custom field '{field.Name}' is not a list");
            Enqueue(root, "clearCustomList", new JsonObject { ["taskId"] = card.Id, ["fieldId"] = field.Id });
            Console.WriteLine($"ok: cleared custom list '{field.Name}' on {card.Id}");
            return 0;
        }

        case "custom" when arguments.Length >= 5 && arguments[1] == "file" && arguments[2] == "add":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[3]);
            var field = RequireCustomField(document, card, arguments[4]);
            if (!field.Type.Equals("files", StringComparison.OrdinalIgnoreCase))
                return Fail($"custom field '{field.Name}' is type '{field.Type}', not 'files'");
            var sourcePath = OptionValue(arguments, "--path") ?? throw new InvalidOperationException("--path is required");
            if (!File.Exists(sourcePath)) return Fail($"source file '{sourcePath}' does not exist");
            var imported = store.ImportFile(sourcePath);
            Enqueue(root, "addCardFile", new JsonObject
            {
                ["taskId"] = card.Id,
                ["fieldId"] = field.Id,
                ["id"] = imported.Id,
                ["name"] = imported.Name,
                ["relativePath"] = imported.RelativePath,
                ["size"] = imported.Size
            });
            Console.WriteLine($"ok: attached '{imported.Name}' to custom file field '{field.Name}' on {card.Id}");
            return 0;
        }

        case "custom" when arguments.Length >= 5 && arguments[1] == "file" && arguments[2] == "remove":
        {
            var document = store.OpenOrCreate();
            var card = RequireCustomCard(document, arguments[3]);
            var field = RequireCustomField(document, card, arguments[4]);
            if (!field.Type.Equals("files", StringComparison.OrdinalIgnoreCase))
                return Fail($"custom field '{field.Name}' is type '{field.Type}', not 'files'");
            var fileId = OptionValue(arguments, "--file") ?? throw new InvalidOperationException("--file is required");
            Enqueue(root, "removeCardFile", new JsonObject { ["taskId"] = card.Id, ["fieldId"] = field.Id, ["fileId"] = fileId });
            Console.WriteLine($"ok: removed attachment '{fileId}' from {card.Id} (managed file retained on disk)");
            return 0;
        }

        case "eligible":
        {
            var document = store.OpenOrCreate();
            var branchFilter = OptionValue(arguments, "--branch");
            if (branchFilter is not null)
            {
                var branch = document.Branches.FirstOrDefault(b => b.Id.Equals(branchFilter, StringComparison.OrdinalIgnoreCase));
                if (branch is null) return Fail($"branch '{branchFilter}' not found");
                PrintJson(EligibilityEngine.GetEligible(branch).Select(card => DescribeTask(document, card)));
            }
            else
            {
                PrintJson(EligibilityEngine.GetEligibleProject(document)
                    .Select(entry => new { Branch = entry.Branch.Id, Tasks = entry.Tasks.Select(card => DescribeTask(document, card)) }));
            }
            return 0;
        }

        case "notes":
        {
            var document = store.OpenOrCreate();
            var branchFilter = OptionValue(arguments, "--branch");
            var branches = document.Branches.Where(b => branchFilter is null || b.Id.Equals(branchFilter, StringComparison.OrdinalIgnoreCase));
            PrintJson(branches.SelectMany(b => b.Tasks).Where(card => card.Kind == CardKind.Note));
            return 0;
        }

        case "note" when arguments.Length >= 3 && arguments[1] == "add":
        {
            var text = OptionValue(arguments, "--text") ?? throw new InvalidOperationException("--text is required");
            Enqueue(root, "addCardNote", new JsonObject { ["taskId"] = arguments[2], ["text"] = text });
            Console.WriteLine($"ok: added note to {arguments[2]}");
            return 0;
        }

        case "note" when arguments.Length >= 4 && arguments[1] == "remove":
        {
            Enqueue(root, "removeCardNote", new JsonObject { ["taskId"] = arguments[2], ["noteId"] = arguments[3] });
            Console.WriteLine($"ok: removed note {arguments[3]} from {arguments[2]}");
            return 0;
        }

        case "note" when arguments.Length >= 4 && arguments[1] == "edit":
        {
            var text = OptionValue(arguments, "--text") ?? throw new InvalidOperationException("--text is required");
            Enqueue(root, "editCardNote", new JsonObject { ["taskId"] = arguments[2], ["noteId"] = arguments[3], ["text"] = text });
            Console.WriteLine($"ok: edited note {arguments[3]} on {arguments[2]}");
            return 0;
        }

        case "file" when arguments.Length >= 3 && arguments[1] == "add":
        {
            var sourcePath = OptionValue(arguments, "--path") ?? throw new InvalidOperationException("--path is required");
            if (!File.Exists(sourcePath)) return Fail($"source file '{sourcePath}' does not exist");
            var document = store.OpenOrCreate();
            var card = OperationApplier.FindCard(document, arguments[2]) ?? throw new InvalidOperationException($"task '{arguments[2]}' not found");
            var imported = store.ImportFile(sourcePath);
            Enqueue(root, "addCardFile", new JsonObject
            {
                ["taskId"] = card.Id,
                ["id"] = imported.Id,
                ["name"] = imported.Name,
                ["relativePath"] = imported.RelativePath,
                ["size"] = imported.Size
            });
            Console.WriteLine($"ok: attached '{imported.Name}' to {card.Id}");
            return 0;
        }

        case "file" when arguments.Length >= 4 && arguments[1] == "remove":
        {
            Enqueue(root, "removeCardFile", new JsonObject { ["taskId"] = arguments[2], ["fileId"] = arguments[3] });
            Console.WriteLine($"ok: detached attachment '{arguments[3]}' from {arguments[2]} (managed file retained on disk)");
            return 0;
        }

        case "tag":
        {
            if (arguments.Length < 4) return Fail("usage: tag <add|remove> <id-or-index> <tag>");
            var mode = arguments[1];
            var id = arguments[2];
            var tag = arguments[3];
            var kind = mode switch { "add" => "addTag", "remove" => "removeTag", _ => throw new InvalidOperationException("mode must be 'add' or 'remove'") };
            Enqueue(root, kind, new JsonObject { ["taskId"] = id, ["tag"] = tag });
            Console.WriteLine($"ok: {mode} tag '{tag}' on {id}");
            return 0;
        }

        case "tags":
        {
            if (arguments.Length < 4 || arguments[1] != "set") return Fail("usage: tags set <id-or-index> <tag1,tag2,...>");
            var id = arguments[2];
            var tags = arguments[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var payload = new JsonObject { ["taskId"] = id, ["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()) };
            Enqueue(root, "setTags", payload);
            Console.WriteLine($"ok: set tags on {id} to [{string.Join(", ", tags)}]");
            return 0;
        }

        case "requirement" when arguments.Length >= 3 && arguments[1] == "add":
        {
            var text = OptionValue(arguments, "--text") ?? throw new InvalidOperationException("--text is required");
            Enqueue(root, "addRequirement", new JsonObject { ["taskId"] = arguments[2], ["text"] = text });
            Console.WriteLine($"ok: added requirement to {arguments[2]}");
            return 0;
        }

        case "requirement" when arguments.Length >= 4 && arguments[1] == "edit":
        {
            var text = OptionValue(arguments, "--text") ?? throw new InvalidOperationException("--text is required");
            Enqueue(root, "editRequirement", new JsonObject { ["taskId"] = arguments[2], ["requirementId"] = arguments[3], ["text"] = text });
            Console.WriteLine($"ok: edited requirement {arguments[3]} on {arguments[2]}");
            return 0;
        }

        case "requirement" when arguments.Length >= 4 && arguments[1] == "remove":
        {
            Enqueue(root, "removeRequirement", new JsonObject { ["taskId"] = arguments[2], ["requirementId"] = arguments[3] });
            Console.WriteLine($"ok: removed requirement {arguments[3]} from {arguments[2]}");
            return 0;
        }

        case "requirement":
        {
            if (arguments.Length < 4) return Fail("usage: requirement <id-or-index> <requirement-id> <done|undone>");
            var id = arguments[1];
            var requirementId = arguments[2];
            var isDone = arguments[3] switch { "done" => true, "undone" => false, _ => throw new InvalidOperationException("state must be 'done' or 'undone'") };
            Enqueue(root, "setRequirement", new JsonObject { ["taskId"] = id, ["requirementId"] = requirementId, ["isDone"] = isDone });
            Console.WriteLine($"ok: requirement {requirementId} on {id} marked {arguments[3]}");
            return 0;
        }

        case "progress":
        {
            if (arguments.Length < 3) return Fail("usage: progress <id-or-index> <start|stop>");
            var id = arguments[1];
            var kind = arguments[2] switch { "start" => "addTag", "stop" => "removeTag", _ => throw new InvalidOperationException("state must be 'start' or 'stop'") };
            Enqueue(root, kind, new JsonObject { ["taskId"] = id, ["tag"] = "in progress" });
            Console.WriteLine($"ok: {(arguments[2] == "start" ? "marked" : "cleared")} in-progress on {id}");
            return 0;
        }

        case "feedback":
        {
            if (arguments.Length < 3) return Fail("usage: feedback <id-or-index> <waiting|clear>");
            var isAwaitingFeedback = arguments[2] switch
            {
                "waiting" => true,
                "clear" => false,
                _ => throw new InvalidOperationException("state must be 'waiting' or 'clear'")
            };
            Enqueue(root, "setAwaitingFeedback", new JsonObject { ["taskId"] = arguments[1], ["isAwaitingFeedback"] = isAwaitingFeedback });
            Console.WriteLine($"ok: awaiting feedback {(isAwaitingFeedback ? "set" : "cleared")} on {arguments[1]}");
            return 0;
        }

        case "archive":
        case "complete":
        {
            if (arguments.Length < 2) return Fail("usage: archive <id-or-index> | archive <id-or-index> --undo [--to <branchId>]");
            var id = arguments[1];
            if (arguments.Contains("--undo"))
            {
                // A card sitting in Archived is forced back to isDone:true by normalization on every
                // save, so undoing archive has to physically relocate it, not just flip the flag.
                var destination = OptionValue(arguments, "--to") ?? "main";
                Enqueue(root, "moveTask", new JsonObject { ["taskId"] = id, ["branchId"] = destination });
                Console.WriteLine($"ok: {id} marked not done and moved to branch {destination}");
            }
            else
            {
                Enqueue(root, "setDone", new JsonObject { ["taskId"] = id, ["isDone"] = true });
                Console.WriteLine($"ok: {id} marked done");
            }
            return 0;
        }

        case "bug":
        {
            var branchId = OptionValue(arguments, "--branch") ?? throw new InvalidOperationException("--branch is required");
            var title = OptionValue(arguments, "--title") ?? throw new InvalidOperationException("--title is required");
            var task = OptionValue(arguments, "--task") ?? throw new InvalidOperationException("--task is required");
            var tags = new JsonArray(new JsonNode[] { "bug" }.Concat(OptionValues(arguments, "--tag").Select(t => (JsonNode)t)).ToArray());
            var requirements = new JsonArray(OptionValues(arguments, "--requirement").Select(r => (JsonNode)r).ToArray());
            var payload = new JsonObject
            {
                ["branchId"] = branchId,
                ["title"] = title,
                ["task"] = task,
                ["tags"] = tags,
                ["requirements"] = requirements,
                ["commit"] = arguments.Contains("--commit"),
                ["build"] = arguments.Contains("--build"),
                ["release"] = arguments.Contains("--release"),
                ["merge"] = arguments.Contains("--merge")
            };
            Enqueue(root, "addTask", payload);
            Console.WriteLine($"ok: filed bug '{title}' on branch {branchId}");
            return 0;
        }

        case "paths":
        {
            var exePath = OptionValue(arguments, "--exe") ?? throw new InvalidOperationException("--exe is required");
            var fullExePath = Path.Combine(root, exePath);
            if (!File.Exists(fullExePath)) return Fail($"'{exePath}' does not exist under the project root");
            var isRelease = arguments.Contains("--release");
            var payload = new JsonObject { ["latestExePath"] = exePath };
            if (isRelease) payload["latestReleasePath"] = exePath;
            Enqueue(root, "setPaths", payload);
            Console.WriteLine($"ok: latestExePath set to {exePath}" + (isRelease ? " (also latestReleasePath)" : ""));
            return 0;
        }

        default:
            PrintUsage();
            return 1;
    }
}

void Enqueue(string root, string kind, JsonObject payload) =>
    QueueStore.EnqueueAndFlush(root, [new QueuedWrite { Kind = kind, Payload = payload }]);

void PrintJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));

int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}

string? OptionValue(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

IEnumerable<string> OptionValues(string[] arguments, string name)
{
    for (var i = 0; i < arguments.Length - 1; i++)
        if (arguments[i] == name) yield return arguments[i + 1];
}

TaskCard RequireCustomCard(ProjectDocument document, string idOrIndex)
{
    var card = OperationApplier.FindCard(document, idOrIndex)
        ?? throw new InvalidOperationException($"task '{idOrIndex}' not found");
    if (string.IsNullOrWhiteSpace(card.CustomTypeId))
        throw new InvalidOperationException($"task '{idOrIndex}' is not a custom card");
    if (!document.CustomCardTypes.Any(definition => definition.Id.Equals(card.CustomTypeId, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException($"custom card definition '{card.CustomTypeId}' was not found");
    return card;
}

CustomFieldDefinition RequireCustomField(ProjectDocument document, TaskCard card, string idOrName)
{
    var definition = document.CustomCardTypes.First(item => item.Id.Equals(card.CustomTypeId, StringComparison.OrdinalIgnoreCase));
    var byId = definition.Fields.FirstOrDefault(field => field.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    if (byId is not null) return byId;
    var byName = definition.Fields.Where(field => field.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
    return byName.Count switch
    {
        1 => byName[0],
        > 1 => throw new InvalidOperationException($"custom field name '{idOrName}' is ambiguous; use its immutable field id"),
        _ => throw new InvalidOperationException($"custom field '{idOrName}' was not found on task '{card.Id}'")
    };
}

object DescribeTask(ProjectDocument document, TaskCard card)
{
    var definition = string.IsNullOrWhiteSpace(card.CustomTypeId)
        ? null
        : document.CustomCardTypes.FirstOrDefault(item => item.Id.Equals(card.CustomTypeId, StringComparison.OrdinalIgnoreCase));
    return new
    {
        card.Id,
        card.Index,
        card.Kind,
        card.Title,
        card.Task,
        card.Tags,
        card.Files,
        card.Requirements,
        card.Notes,
        card.Flags,
        card.IsDone,
        card.IsLocked,
        card.IsAwaitingFeedback,
        card.IsCollapsed,
        card.DueDate,
        card.StartedDate,
        card.Priority,
        card.CreatedAt,
        card.UpdatedAt,
        IsCustomCard = !string.IsNullOrWhiteSpace(card.CustomTypeId),
        CustomCardType = string.IsNullOrWhiteSpace(card.CustomTypeId) ? null : new
        {
            Id = card.CustomTypeId,
            Name = definition?.Name,
            AgentInstructions = definition?.Instructions
        },
        CustomFields = definition?.Fields.Select(field => DescribeCustomField(card, field)).ToList()
    };
}

object DescribeCustomField(TaskCard card, CustomFieldDefinition field)
{
    object value = field.Type.Equals("files", StringComparison.OrdinalIgnoreCase)
        ? card.Files.Select(file => new { file.Id, file.Name, file.RelativePath, file.Size }).ToList()
        : field.Type.Equals("tags", StringComparison.OrdinalIgnoreCase)
            ? card.Tags.Where(tag => !IsSystemTag(tag)).ToList()
            : field.Type.Equals("list", StringComparison.OrdinalIgnoreCase)
                ? CustomListCodec.Parse(card.CustomValues.GetValueOrDefault(field.Id, field.DefaultValue))
                : field.Type.Equals("label", StringComparison.OrdinalIgnoreCase)
                    ? field.DefaultValue
                : card.CustomValues.GetValueOrDefault(field.Id, field.DefaultValue);
    return new
    {
        field.Id,
        field.Name,
        field.Type,
        Value = value,
        field.DefaultValue,
        field.Options,
        field.ShowOnCollapsed
    };
}

bool IsSystemTag(string tag) => tag.Equals("bug", StringComparison.OrdinalIgnoreCase) ||
                                tag.Equals("in progress", StringComparison.OrdinalIgnoreCase);

string ReadCanonicalPrompt(ProjectStore store)
{
    var settings = new SettingsStore().Load();
    var prompt = File.Exists(PromptEnvironment.PromptPath)
        ? File.ReadAllText(PromptEnvironment.PromptPath)
        : string.IsNullOrWhiteSpace(settings.GlobalPrompt) ? ProjectStore.ReadEmbeddedPrompt() : settings.GlobalPrompt;
    var customCards = store.OpenOrCreate().CustomCardTypes.Where(card => !string.IsNullOrWhiteSpace(card.Instructions)).ToList();
    if (customCards.Count == 0) return prompt;
    var instructions = string.Join(Environment.NewLine + Environment.NewLine, customCards.Select(card => $"CUSTOM CARD: {card.Name}{Environment.NewLine}{card.Instructions.Trim()}"));
    return $"{prompt.TrimEnd()}{Environment.NewLine}{Environment.NewLine}PROJECT CUSTOM CARD INSTRUCTIONS{Environment.NewLine}{instructions}";
}

void PrintUsage()
{
    Console.WriteLine("""
    zen-operator - single entry point for agents working a Zen project.
    Never edit zen.tasks.json by hand; use these commands instead. This tool gives full
    read/write control over the project data — it exists to serialize concurrent writers
    (you and the GUI, or multiple agents) through one queue, not to restrict what you can do.

      prompt                                   print the canonical agent instructions
      project                                   print project id/name/nextCardIndex/paths/tagCatalog
      branches                                  list branches (id, title, git branch, locked, task count)
      branch create --title <t> [--branch <gitBranchName>]
                                                 create a new branch/column
      branch lock|unlock <id>                   lock or unlock a branch
      branch rename <id> --title <t>            rename a branch
      branch archive <id>                       archive a non-permanent branch (its tasks move to Archived)
      tasks [--branch <id>] [--eligible]        list tasks, optionally filtered
      task <id-or-index>                        print one task
      task create --branch <id> --title <t> --task <t> [--tag <t>]* [--requirement <r>]*
                  [--commit] [--build] [--release] [--merge]
                                                 create a plain task
      task edit <id-or-index> [--title <t>] [--task <t>] [--due <date|clear>]
                [--started <date|clear>] [--priority low|normal|high|critical|clear]
                [--commit|--no-commit] [--build|--no-build] [--release|--no-release] [--merge|--no-merge]
                                                 edit any field on an existing task
      task lock|unlock <id-or-index>            lock or unlock a task
      task delete <id-or-index>                 permanently delete a task
      task move <id-or-index> --to <branchId>   move a task to another branch
      eligible [--branch <id>]                  list eligible tasks (locks/breaks/bug-priority applied)
      notes [--branch <id>]                     list note cards
      note add <id-or-index> --text <text>      add a note to an opened task card
      note edit <id-or-index> <noteId> --text <text>
                                                 edit an opened-card note
      note remove <id-or-index> <noteId>        remove a task-card note
      file add <id-or-index> --path <sourcePath>
                                                 import and attach a managed file
      file remove <id-or-index> <fileId|name>   detach it without deleting the managed file
      custom pull|fields <id-or-index>          print custom type, agent instructions, and all fields
      custom types                              list custom card definitions and field schemas
      custom get <id-or-index> <fieldId|name>   print one resolved custom field
      custom set <id-or-index> <fieldId|name> --value <value>
                                                 set a writable custom field (file commands are separate; labels are read-only)
      custom list add <id-or-index> <fieldId|name> --item <text>
      custom list remove <id-or-index> <fieldId|name> --index <one-based-index>
      custom list clear <id-or-index> <fieldId|name>
                                                 mutate list fields atomically without rewriting the list
      custom file add <id-or-index> <fieldId|name> --path <sourcePath>
                                                 import and attach a managed file
      custom file remove <id-or-index> <fieldId|name> --file <fileId|name>
                                                 detach a managed file without deleting it from disk
      tag add|remove <id-or-index> <tag>        add or remove a single tag
      tags set <id-or-index> <t1,t2,...>        replace a task's whole tag list
      requirement <id-or-index> <reqId> done|undone
      requirement add <id-or-index> --text <text>
      requirement edit <id-or-index> <reqId> --text <text>
      requirement remove <id-or-index> <reqId> add, edit, or remove checklist items
      progress <id-or-index> start|stop         shortcut for the 'in progress' tag
      feedback <id-or-index> waiting|clear      set or clear the global awaiting-feedback state
      archive <id-or-index>                     mark a task done (moves it into Archived)
      archive <id-or-index> --undo [--to <branchId>]
                                                 move a task out of Archived and mark it not done (defaults to 'main')
      bug --branch <id> --title <t> --task <t> [--tag <t>]* [--requirement <r>]*
          [--commit] [--build] [--release] [--merge]
                                                 file a new bug task with correct id/index/ordering
      paths --exe <relative-path> [--release]   update latestExePath (and latestReleasePath)
    """);
}
