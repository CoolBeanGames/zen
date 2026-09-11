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
            Console.WriteLine(ReadCanonicalPrompt());
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
            PrintJson(tasks);
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
            PrintJson(card);
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
                PrintJson(EligibilityEngine.GetEligible(branch));
            }
            else
            {
                PrintJson(EligibilityEngine.GetEligibleProject(document)
                    .Select(entry => new { Branch = entry.Branch.Id, Tasks = entry.Tasks }));
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

string ReadCanonicalPrompt()
{
    var settings = new SettingsStore().Load();
    return string.IsNullOrWhiteSpace(settings.GlobalPrompt) ? ProjectStore.ReadEmbeddedPrompt() : settings.GlobalPrompt;
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
      tag add|remove <id-or-index> <tag>        add or remove a single tag
      tags set <id-or-index> <t1,t2,...>        replace a task's whole tag list
      requirement <id-or-index> <reqId> done|undone
      progress <id-or-index> start|stop         shortcut for the 'in progress' tag
      archive <id-or-index>                     mark a task done (moves it into Archived)
      archive <id-or-index> --undo [--to <branchId>]
                                                 move a task out of Archived and mark it not done (defaults to 'main')
      bug --branch <id> --title <t> --task <t> [--tag <t>]* [--requirement <r>]*
          [--commit] [--build] [--release] [--merge]
                                                 file a new bug task with correct id/index/ordering
      paths --exe <relative-path> [--release]   update latestExePath (and latestReleasePath)
    """);
}
