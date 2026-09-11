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
                TaskCount = b.Tasks.Count
            }));
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

        case "task":
        {
            if (arguments.Length < 2) return Fail("usage: task <id-or-index>");
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
            if (arguments.Length < 2) return Fail("usage: archive <id-or-index> [--undo]");
            var id = arguments[1];
            var isDone = !arguments.Contains("--undo");
            Enqueue(root, "setDone", new JsonObject { ["taskId"] = id, ["isDone"] = isDone });
            Console.WriteLine($"ok: {id} marked {(isDone ? "done" : "not done")}");
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
    Never edit zen.tasks.json by hand; use these commands instead.

      prompt                                   print the canonical agent instructions
      branches                                  list branches (id, title, git branch, locked, task count)
      tasks [--branch <id>] [--eligible]        list tasks, optionally filtered
      task <id-or-index>                        print one task
      eligible [--branch <id>]                  list eligible tasks (locks/breaks/bug-priority applied)
      notes [--branch <id>]                     list note cards
      tag add|remove <id-or-index> <tag>        add or remove a single tag
      tags set <id-or-index> <t1,t2,...>        replace a task's whole tag list
      requirement <id-or-index> <reqId> done|undone
      progress <id-or-index> start|stop         shortcut for the 'in progress' tag
      archive <id-or-index> [--undo]            mark a task done (or, with --undo, not done)
      bug --branch <id> --title <t> --task <t> [--tag <t>]* [--requirement <r>]*
          [--commit] [--build] [--release] [--merge]
                                                 file a new bug task with correct id/index/ordering
      paths --exe <relative-path> [--release]   update latestExePath (and latestReleasePath)
    """);
}
