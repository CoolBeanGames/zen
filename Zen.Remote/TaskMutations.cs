using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class CreateTaskRequest
{
    public string BranchId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<string> Requirements { get; set; } = [];
    public string? BlockedBy { get; set; }
    public TaskFlagsRequest Flags { get; set; } = new();
}

internal sealed class EditTaskRequest
{
    public string Title { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public string? StartedDate { get; set; }
    public string? DueDate { get; set; }
    public string? Priority { get; set; }
    public List<string> Tags { get; set; } = [];
    public TaskFlagsRequest Flags { get; set; } = new();
    public bool IsAwaitingFeedback { get; set; }
    public bool IsInProgress { get; set; }
    public bool IsDone { get; set; }
    public string? RestoreBranchId { get; set; }
    public List<RequirementEditRequest> Requirements { get; set; } = [];
    public Dictionary<string, object?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? BlockedBy { get; set; }
}

internal sealed class TaskFlagsRequest
{
    public bool Commit { get; set; }
    public bool Build { get; set; }
    public bool Release { get; set; }
    public bool Merge { get; set; }
}

internal sealed class RequirementEditRequest
{
    public string? Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsDone { get; set; }
}

internal sealed class MutationException(string message) : Exception(message);

internal sealed class TaskMutationService(OperatorClient client, ProjectRegistry registry)
{
    private static readonly HashSet<string> Priorities = new(StringComparer.OrdinalIgnoreCase) { "low", "normal", "high", "critical" };

    public async Task CreateTaskAsync(string projectId, CreateTaskRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await FindProjectAsync(projectId, cancellationToken);
        var branch = FindBranch(snapshot, request.BranchId);
        if (branch["isArchive"]?.GetValue<bool>() == true) throw new MutationException("Tasks cannot be created in the archive.");
        if (branch["isLocked"]?.GetValue<bool>() == true) throw new MutationException("That branch is locked.");
        var title = Required(request.Title, "Title", 300);
        var task = Limited(request.Task, "Task", 100_000);
        var arguments = new List<string> { "task", "create", "--branch", request.BranchId, "--title", title, "--task", task };
        if (!string.IsNullOrWhiteSpace(request.BlockedBy)) arguments.AddRange(["--blocked-by", request.BlockedBy]);
        foreach (var tag in NormalizeTags(request.Tags)) { arguments.Add("--tag"); arguments.Add(tag); }
        foreach (var requirement in request.Requirements.Select(value => value.Trim()).Where(value => value.Length > 0).Take(100))
        { arguments.Add("--requirement"); arguments.Add(Limited(requirement, "Requirement", 10_000)); }
        AddEnabledFlags(arguments, request.Flags);
        await client.RunAsync(snapshot.Path, cancellationToken, arguments.ToArray());
    }

    public async Task EditTaskAsync(string projectId, string taskId, EditTaskRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await FindProjectAsync(projectId, cancellationToken);
        var located = FindTask(snapshot, taskId);
        var current = located.Task;
        if (current["isLocked"]?.GetValue<bool>() == true) throw new MutationException("That task is locked.");
        if (!string.Equals(current["kind"]?.GetValue<string>(), "task", StringComparison.OrdinalIgnoreCase))
            throw new MutationException("Only task cards can be edited from Zen Remote.");

        var arguments = new List<string> { "task", "edit", taskId };
        if (current["isCustomCard"]?.GetValue<bool>() != true)
        {
            arguments.AddRange(["--title", Required(request.Title, "Title", 300), "--task", Limited(request.Task, "Task", 100_000)]);
        }
        arguments.AddRange(["--started", NormalizeDate(request.StartedDate), "--due", NormalizeDate(request.DueDate), "--priority", NormalizePriority(request.Priority)]);
        if (request.BlockedBy is not null)
            arguments.AddRange(["--blocked-by", string.IsNullOrWhiteSpace(request.BlockedBy) ? "clear" : request.BlockedBy]);
        AddBooleanFlag(arguments, "commit", request.Flags.Commit);
        AddBooleanFlag(arguments, "build", request.Flags.Build);
        AddBooleanFlag(arguments, "release", request.Flags.Release);
        AddBooleanFlag(arguments, "merge", request.Flags.Merge);
        await client.RunAsync(snapshot.Path, cancellationToken, arguments.ToArray());

        await client.RunAsync(snapshot.Path, cancellationToken, "tags", "set", taskId, string.Join(',', NormalizeTags(request.Tags)));
        await client.RunAsync(snapshot.Path, cancellationToken, "feedback", taskId, request.IsAwaitingFeedback ? "waiting" : "clear");
        await client.RunAsync(snapshot.Path, cancellationToken, "progress", taskId, request.IsInProgress ? "start" : "stop");
        await ReconcileRequirementsAsync(snapshot.Path, taskId, current, request.Requirements, cancellationToken);
        await UpdateCustomFieldsAsync(snapshot.Path, taskId, current, request.CustomFields, cancellationToken);

        var wasDone = current["isDone"]?.GetValue<bool>() == true;
        if (!wasDone && request.IsDone)
            await client.RunAsync(snapshot.Path, cancellationToken, "archive", taskId);
        else if (wasDone && !request.IsDone)
        {
            var destination = request.RestoreBranchId ?? snapshot.Branches.FirstOrDefault(branch => branch["isArchive"]?.GetValue<bool>() != true)?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(destination)) throw new MutationException("A destination branch is required to restore this task.");
            var branch = FindBranch(snapshot, destination);
            if (branch["isArchive"]?.GetValue<bool>() == true || branch["isLocked"]?.GetValue<bool>() == true) throw new MutationException("The restore branch is unavailable.");
            await client.RunAsync(snapshot.Path, cancellationToken, "archive", taskId, "--undo", "--to", destination);
        }
    }

    private async Task ReconcileRequirementsAsync(string path, string taskId, JsonObject current, IReadOnlyList<RequirementEditRequest> requested, CancellationToken cancellationToken)
    {
        var currentRequirements = current["requirements"]?.AsArray().OfType<JsonObject>().ToDictionary(item => item["id"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase) ?? [];
        var requestedExisting = requested.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToDictionary(item => item.Id!, StringComparer.OrdinalIgnoreCase);
        foreach (var existing in currentRequirements)
        {
            if (!requestedExisting.TryGetValue(existing.Key, out var edit))
            { await client.RunAsync(path, cancellationToken, "requirement", "remove", taskId, existing.Key); continue; }
            var text = Required(edit.Text, "Requirement", 10_000);
            if (!string.Equals(existing.Value["text"]?.GetValue<string>(), text, StringComparison.Ordinal))
                await client.RunAsync(path, cancellationToken, "requirement", "edit", taskId, existing.Key, "--text", text);
            if ((existing.Value["isDone"]?.GetValue<bool>() == true) != edit.IsDone)
                await client.RunAsync(path, cancellationToken, "requirement", taskId, existing.Key, edit.IsDone ? "done" : "undone");
        }
        foreach (var added in requested.Where(item => string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Text)))
            await client.RunAsync(path, cancellationToken, "requirement", "add", taskId, "--text", Required(added.Text, "Requirement", 10_000));
    }

    private async Task UpdateCustomFieldsAsync(string path, string taskId, JsonObject current, IReadOnlyDictionary<string, object?> requested, CancellationToken cancellationToken)
    {
        if (current["customFields"] is not JsonArray fields) return;
        foreach (var field in fields.OfType<JsonObject>())
        {
            var id = field["id"]?.GetValue<string>();
            var type = field["type"]?.GetValue<string>() ?? "text";
            if (id is null || !requested.TryGetValue(id, out var raw) || type is "label" or "files") continue;
            if (type == "list")
            {
                var values = raw switch
                {
                    JsonElement element when element.ValueKind == JsonValueKind.Array => element.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList(),
                    _ => raw?.ToString()?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? []
                };
                await client.RunAsync(path, cancellationToken, "custom", "list", "clear", taskId, id);
                foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
                    await client.RunAsync(path, cancellationToken, "custom", "list", "add", taskId, id, "--item", Limited(value, "List item", 10_000));
            }
            else
            {
                var value = raw is JsonElement element ? element.ToString() : raw?.ToString() ?? string.Empty;
                await client.RunAsync(path, cancellationToken, "custom", "set", taskId, id, "--value", Limited(value, "Custom field", 100_000));
            }
        }
    }

    private async Task<ProjectSnapshot> FindProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        foreach (var path in registry.Discover())
        {
            var project = await client.ReadProjectAsync(path, cancellationToken);
            if (project["error"] is not null) continue;
            if (string.Equals(project["project"]?["projectId"]?.GetValue<string>(), projectId, StringComparison.OrdinalIgnoreCase))
                return new ProjectSnapshot(path, project["branches"]!.AsArray().OfType<JsonObject>().ToList());
        }
        throw new MutationException("Project was not found in Zen's known-project registry.");
    }

    private static JsonObject FindBranch(ProjectSnapshot snapshot, string branchId) => snapshot.Branches.FirstOrDefault(branch => string.Equals(branch["id"]?.GetValue<string>(), branchId, StringComparison.OrdinalIgnoreCase)) ?? throw new MutationException("Branch was not found.");
    private static (JsonObject Task, JsonObject Branch) FindTask(ProjectSnapshot snapshot, string taskId)
    {
        foreach (var branch in snapshot.Branches)
            if (branch["tasks"] is JsonArray tasks)
                foreach (var task in tasks.OfType<JsonObject>())
                    if (string.Equals(task["id"]?.GetValue<string>(), taskId, StringComparison.OrdinalIgnoreCase)) return (task, branch);
        throw new MutationException("Task was not found.");
    }

    private static string Required(string? value, string name, int maxLength)
    { var result = value?.Trim() ?? string.Empty; if (result.Length == 0) throw new MutationException($"{name} is required."); return Limited(result, name, maxLength); }
    private static string Limited(string? value, string name, int maxLength)
    { var result = value ?? string.Empty; if (result.Length > maxLength) throw new MutationException($"{name} is too long."); return result; }
    private static string NormalizeDate(string? value) => string.IsNullOrWhiteSpace(value) ? "clear" : DateOnly.TryParse(value, out var date) ? date.ToString("yyyy-MM-dd") : throw new MutationException("Dates must use YYYY-MM-DD.");
    private static string NormalizePriority(string? value) => string.IsNullOrWhiteSpace(value) ? "clear" : Priorities.Contains(value) ? value.ToLowerInvariant() : throw new MutationException("Priority is invalid.");
    private static List<string> NormalizeTags(IEnumerable<string>? tags) => (tags ?? []).Select(tag => tag.Trim()).Where(tag => tag.Length > 0 && !tag.Contains(',')).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).Select(tag => Limited(tag, "Tag", 100)).ToList();
    private static void AddEnabledFlags(ICollection<string> arguments, TaskFlagsRequest flags) { if (flags.Commit) arguments.Add("--commit"); if (flags.Build) arguments.Add("--build"); if (flags.Release) arguments.Add("--release"); if (flags.Merge) arguments.Add("--merge"); }
    private static void AddBooleanFlag(ICollection<string> arguments, string name, bool enabled) => arguments.Add($"--{(enabled ? string.Empty : "no-")}{name}");
    private sealed record ProjectSnapshot(string Path, List<JsonObject> Branches);
}
