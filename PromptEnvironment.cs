using System.IO;

namespace Zen;

// Keeps a single canonical prompt.txt on disk and on the user PATH so agent
// launchers can point at it regardless of which project folder they run in.
public static class PromptEnvironment
{
    public static string Directory => SettingsStore.DataDirectory;
    public static string PromptPath => Path.Combine(Directory, ProjectStore.PromptFileName);

    public static string Materialize(string globalPrompt)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var content = string.IsNullOrWhiteSpace(globalPrompt) ? ProjectStore.ReadEmbeddedPrompt() : globalPrompt;
        if (!File.Exists(PromptPath) || File.ReadAllText(PromptPath) != content)
        {
            var temporaryPath = PromptPath + ".tmp";
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, PromptPath, true);
        }
        return PromptPath;
    }

    public static void Sync(SettingsStore store, ZenSettings settings)
    {
        var updatedPrompt = EnsureBuildPathInstructions(settings.GlobalPrompt);
        updatedPrompt = EnsureOperatorPathFallbackInstructions(updatedPrompt);
        updatedPrompt = EnsureBuiltInCardOperatorInstructions(updatedPrompt);
        updatedPrompt = EnsureMergeControlInstructions(updatedPrompt);
        updatedPrompt = EnsureCustomCardOperatorInstructions(updatedPrompt);
        updatedPrompt = EnsureClusterInstructions(updatedPrompt);
        updatedPrompt = EnsureSignatureInstructions(updatedPrompt);
        updatedPrompt = EnsureAwaitingFeedbackInstructions(updatedPrompt);
        var promptChanged = !string.Equals(settings.GlobalPrompt, updatedPrompt, StringComparison.Ordinal);
        settings.GlobalPrompt = updatedPrompt;
        Materialize(settings.GlobalPrompt);

        const EnvironmentVariableTarget target = EnvironmentVariableTarget.User;
        var entries = (Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var changed = false;

        if (!string.IsNullOrEmpty(settings.PromptPathEntry) && !SamePath(settings.PromptPathEntry, Directory))
            changed |= entries.RemoveAll(entry => SamePath(entry, settings.PromptPathEntry)) > 0;

        if (!entries.Any(entry => SamePath(entry, Directory)))
        {
            entries.Add(Directory);
            changed = true;
        }

        if (changed)
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, entries), target);

        if (promptChanged || !string.Equals(settings.PromptPathEntry, Directory, StringComparison.Ordinal))
        {
            settings.PromptPathEntry = Directory;
            store.Save(settings);
        }
    }

    private static string EnsureBuildPathInstructions(string prompt)
    {
        const string heading = "BUILD AND RELEASE PATHS";
        if (prompt.Contains(heading, StringComparison.Ordinal)) return prompt;

        const string instructions = "BUILD AND RELEASE PATHS\r\n" +
            "- After every successful build, set the root `latestExePath` in `zen.tasks.json` to the executable produced by that build. This is the newest executable regardless of build configuration.\r\n" +
            "- After every successful release build, set the root `latestReleasePath` to that release executable and also set `latestExePath` to it because it is now the newest executable.\r\n" +
            "- Prefer project-relative paths with `/` separators. Record a path only after confirming that the executable exists, and preserve all concurrent database changes when saving it.\r\n" +
            "- Never set `latestReleasePath` to a debug or other non-release executable.\r\n\r\n";
        const string nextHeading = "REQUIREMENTS AND COMPLETION";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        return insertionPoint >= 0
            ? prompt.Insert(insertionPoint, instructions)
            : prompt.TrimEnd() + "\r\n\r\n" + instructions;
    }

    private static string EnsureCustomCardOperatorInstructions(string prompt)
    {
        const string heading = "CUSTOM CARDS AND OPERATOR FIELD ACCESS";
        if (prompt.Contains(heading, StringComparison.Ordinal)) return prompt;
        const string instructions = "CUSTOM CARDS AND OPERATOR FIELD ACCESS\r\n" +
            "- `task`, `tasks`, and `eligible` explicitly return `isCustomCard`, the custom card type id/name, its `agentInstructions`, and every resolved `customFields` entry with immutable id, name, type, and current value. A custom field may contain the real work description even when the ordinary `task` property is empty.\r\n" +
            "- Discover schemas with `zen-operator custom types`. Read a custom card with `zen-operator custom pull <id-or-index>` (or `custom fields`); read one field with `zen-operator custom get <id-or-index> <fieldId|name>`. Follow the returned `agentInstructions` when using that card type.\r\n" +
            "- Write a non-file field with `zen-operator custom set <id-or-index> <fieldId|name> --value <value>`. Field ids are the stable automation contract; names are convenience aliases and must be unambiguous. List values use the card's serialized list text. Tag-field writes synchronize user tags while preserving the system `bug` and `in progress` tags.\r\n" +
            "- Mutate list fields safely with `zen-operator custom list add <task> <field> --item <text>`, `custom list remove <task> <field> --index <one-based-index>`, and `custom list clear <task> <field>`. Prefer these atomic commands to rewriting a whole list.\r\n" +
            "- Write a file field with `zen-operator custom file add <id-or-index> <fieldId|name> --path <sourcePath>` or detach one with `custom file remove <id-or-index> <fieldId|name> --file <fileId|name>`. Detaching never deletes the managed file from disk.\r\n" +
            "- Add an opened-card note with `zen-operator note add <id-or-index> --text <text>` and remove one with `zen-operator note remove <id-or-index> <noteId>`.\r\n\r\n";
        const string nextHeading = "DATA SHAPE AND OWNERSHIP";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        return insertionPoint >= 0 ? prompt.Insert(insertionPoint, instructions) : prompt.TrimEnd() + "\r\n\r\n" + instructions;
    }

    private static string EnsureMergeControlInstructions(string prompt)
    {
        const string heading = "MERGE CONTROL CARDS";
        if (prompt.Contains(heading, StringComparison.Ordinal)) return prompt;

        var newline = prompt.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var legacyIndex = lines.FindIndex(line => line.Contains("add menu", StringComparison.OrdinalIgnoreCase) && line.Contains("Merge", StringComparison.Ordinal));
        if (legacyIndex >= 0) lines.RemoveAt(legacyIndex);
        var section = new[]
        {
            heading,
            "- Zen's add menu and `zen-operator merge --branch <id>` insert a nameless built-in `kind: \"merge\"` card immediately, like Cleanup. It has no title, task text, tags, requirements, or action flags.",
            "- When reached on a non-main Git-backed branch, verify earlier work is complete, merge that branch safely into `main`, and push `origin/main`. When reached on `main`, verify and push `main` without trying to merge it into itself. Never discard conflicts or unrelated work.",
            "- Archive the merge card only after the remote update succeeds. If the merge or push fails, leave it open and report the blocker.",
            string.Empty
        };
        var releaseIndex = lines.FindIndex(line => line.Equals("RELEASE TASKS", StringComparison.Ordinal));
        if (releaseIndex < 0) lines.AddRange(section);
        else lines.InsertRange(releaseIndex, section);
        return string.Join(newline, lines);
    }

    private static string EnsureBuiltInCardOperatorInstructions(string prompt)
    {
        const string heading = "BUILT-IN CARD OPERATOR ACCESS";
        if (prompt.Contains(heading, StringComparison.Ordinal)) return prompt;
        const string instructions = "BUILT-IN CARD OPERATOR ACCESS\r\n" +
            "- `task`, `tasks`, and `eligible` return every built-in field: title/task text, tags, requirements, notes, files, action flags, schedule/priority, lock/done state, and timestamps. Use `task edit` for title, task text, dates, priority, and action flags; use the focused commands below for collections.\r\n" +
            "- Tags: `tag add|remove <task> <tag>` changes one tag; `tags set <task> <comma-separated-tags>` replaces user and system tags exactly. Use `progress <task> start|stop` for the managed `in progress` tag.\r\n" +
            "- Requirements: `requirement add <task> --text <text>`, `requirement edit <task> <requirementId> --text <text>`, `requirement remove <task> <requirementId>`, and `requirement <task> <requirementId> done|undone`.\r\n" +
            "- Opened-card notes: `note add <task> --text <text>`, `note edit <task> <noteId> --text <text>`, and `note remove <task> <noteId>`. The plural `notes [--branch <id>]` command lists note-type cards; it is different from notes attached to a task.\r\n" +
            "- Attachments: `file add <task> --path <sourcePath>` imports a managed copy; `file remove <task> <fileId|name>` detaches it but intentionally leaves the managed file on disk.\r\n\r\n";
        const string nextHeading = "CUSTOM CARDS AND OPERATOR FIELD ACCESS";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        if (insertionPoint < 0) insertionPoint = prompt.IndexOf("DATA SHAPE AND OWNERSHIP", StringComparison.Ordinal);
        return insertionPoint >= 0 ? prompt.Insert(insertionPoint, instructions) : prompt.TrimEnd() + "\r\n\r\n" + instructions;
    }

    private static string EnsureAwaitingFeedbackInstructions(string prompt)
    {
        const string heading = "AWAITING FEEDBACK";
        if (prompt.Contains(heading, StringComparison.Ordinal)) return prompt;
        const string instructions = "AWAITING FEEDBACK\r\n" +
            "- Every card has a global `isAwaitingFeedback` state. Set it with `zen-operator feedback <task> waiting` only after adding a clear note that explains what user input is needed. Then run `progress <task> stop` and do not continue that card until the user responds and the state is cleared with `feedback <task> clear`.\r\n" +
            "- Awaiting-feedback cards glow green in Zen and are excluded from the eligible queue. When processing a branch or project, continue with other eligible cards if their work does not depend on the pending answer; otherwise stop and report the dependency.\r\n\r\n";
        const string nextHeading = "LOCKS AND ELIGIBILITY";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        return insertionPoint >= 0 ? prompt.Insert(insertionPoint, instructions) : prompt.TrimEnd() + "\r\n\r\n" + instructions;
    }

    public static string OperatorPointerPath => Path.Combine(Directory, "operator-path.txt");

    public static void SyncOperatorPath(SettingsStore store, ZenSettings settings)
    {
        var operatorDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var operatorExePath = Path.Combine(operatorDirectory, "zen-operator.exe");
        if (!File.Exists(operatorExePath)) return;

        WriteOperatorPointer(operatorExePath);

        const EnvironmentVariableTarget target = EnvironmentVariableTarget.User;
        var entries = (Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var changed = false;
        if (!string.IsNullOrEmpty(settings.OperatorPathEntry) && !SamePath(settings.OperatorPathEntry, operatorDirectory))
            changed |= entries.RemoveAll(entry => SamePath(entry, settings.OperatorPathEntry)) > 0;
        if (!entries.Any(entry => SamePath(entry, operatorDirectory)))
        {
            entries.Add(operatorDirectory);
            changed = true;
        }
        if (changed) Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, entries), target);
        if (!string.Equals(settings.OperatorPathEntry, operatorDirectory, StringComparison.OrdinalIgnoreCase))
        {
            settings.OperatorPathEntry = operatorDirectory;
            store.Save(settings);
        }
    }

    private static void WriteOperatorPointer(string operatorExePath)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (!File.Exists(OperatorPointerPath) || File.ReadAllText(OperatorPointerPath) != operatorExePath)
        {
            var temporaryPath = $"{OperatorPointerPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, operatorExePath);
                if (File.Exists(operatorExePath)) File.Move(temporaryPath, OperatorPointerPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static string EnsureOperatorPathFallbackInstructions(string prompt)
    {
        const string marker = "operator-path.txt";
        if (prompt.Contains(marker, StringComparison.Ordinal)) return prompt;
        const string instructions =
            "- If the `zen-operator` command is not found on PATH, read `operator-path.txt` next to this canonical prompt for the absolute path to the current `zen-operator.exe`, and invoke it directly.\r\n\r\n";
        const string nextHeading = "DATA SHAPE AND OWNERSHIP";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        return insertionPoint >= 0 ? prompt.Insert(insertionPoint, instructions) : prompt.TrimEnd() + "\r\n\r\n" + instructions;
    }

    private static string EnsureClusterInstructions(string prompt)
    {
        const string heading = "CLUSTERS";
        if (prompt.Contains(heading, StringComparison.Ordinal)) return prompt;
        const string instructions = "CLUSTERS\r\n" +
            "- Clusters are project-wide work groups stored in `clusters[]`; tasks join one with `clusterId`. `project`, `task`, `tasks`, and `eligible` expose cluster data. Use `clusters` or `cluster <id-or-name>` for full metadata and member tasks.\r\n" +
            "- Create and edit clusters with `cluster create` and `cluster edit`. Use `cluster task add <cluster> <task>` or `cluster task remove <task>` for membership. A custom card field of type `cluster` reads and writes the same built-in membership.\r\n" +
            "- Cluster requirements and notes use `cluster requirement ...` and `cluster note ...`. Mark each cluster requirement done as soon as it is satisfied, just like a task requirement.\r\n" +
            "- A locked or awaiting-feedback cluster makes every member task ineligible. When cluster feedback is needed, add a cluster note first, set `--awaiting`, and stop work in that cluster until it is cleared.\r\n" +
            "- `cluster move` and `cluster archive` act on the entire group. Never archive a cluster whose `doNotArchive` value is true. Deleting a cluster keeps its tasks and only removes their grouping.\r\n" +
            "- A launch scoped to a cluster authorizes only that cluster's member tasks. Respect its order, requirements, notes, locks, feedback state, and commit/build/release flags.\r\n\r\n";
        const string nextHeading = "DATA SHAPE AND OWNERSHIP";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        return insertionPoint >= 0 ? prompt.Insert(insertionPoint, instructions) : prompt.TrimEnd() + "\r\n\r\n" + instructions;
    }

    private static string EnsureSignatureInstructions(string prompt)
    {
        const string heading = "SIGNATURES";
        if (prompt.Contains(heading, StringComparison.Ordinal)) return prompt;
        const string instructions = "SIGNATURES\r\n" +
            "- After a task's work, requirements, and enabled commit/build/release/merge flags have succeeded, add one short project signature before archiving it: `zen-operator signature add --message <summary> --agent <agent-name> --task <id-or-index> --at <ISO-8601-date-time>`. Identify yourself accurately (for example Codex, Claude, or Gemini), provide the current time with offset, and keep the message concise.\r\n" +
            "- `zen-operator signatures` lists the project's signatures in chronological order. Signatures are project-level history and do not replace task notes, requirement updates, or completion state. User-entered signatures are created by Zen with task `N/A`, agent `User`, and an automatic timestamp.\r\n\r\n";
        const string nextHeading = "DATA SHAPE AND OWNERSHIP";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        return insertionPoint >= 0 ? prompt.Insert(insertionPoint, instructions) : prompt.TrimEnd() + "\r\n\r\n" + instructions;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
