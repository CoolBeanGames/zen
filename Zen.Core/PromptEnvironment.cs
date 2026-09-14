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

    public static string OperatorPointerPath => Path.Combine(Directory, "operator-path.txt");

    // The operator ships as zen-operator.exe alongside Zen.exe in the same publish folder,
    // which is a new, versioned location every release. Keep PATH pointing at wherever this
    // running instance actually lives, correcting it whenever it's missing or stale. Windows
    // never refreshes PATH in shells that were already open, so also drop a pointer file next
    // to prompt.txt (a location agents already know to fetch from) with the exe's current
    // absolute path, so an agent whose PATH is stale can still find and invoke it directly.
    public static void SyncOperatorPath(SettingsStore store, ZenSettings settings)
    {
        var operatorDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var operatorExePath = Path.Combine(operatorDirectory, "zen-operator.exe");
        if (!File.Exists(operatorExePath)) return;

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

        if (changed)
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, entries), target);

        if (!string.Equals(settings.OperatorPathEntry, operatorDirectory, StringComparison.OrdinalIgnoreCase))
        {
            settings.OperatorPathEntry = operatorDirectory;
            store.Save(settings);
        }

        System.IO.Directory.CreateDirectory(Directory);
        if (!File.Exists(OperatorPointerPath) || File.ReadAllText(OperatorPointerPath) != operatorExePath)
        {
            var temporaryPath = OperatorPointerPath + ".tmp";
            File.WriteAllText(temporaryPath, operatorExePath);
            File.Move(temporaryPath, OperatorPointerPath, true);
        }
    }

    private static string EnsureOperatorPathFallbackInstructions(string prompt)
    {
        const string marker = "operator-path.txt";
        if (prompt.Contains(marker, StringComparison.Ordinal)) return prompt;

        const string instructions =
            "- If the `zen-operator` command is not found on PATH, it usually means your shell session started before Zen last updated PATH — Windows does not refresh a shell that is already open. Read `operator-path.txt`, in the same folder as this prompt.txt, for the absolute path to the current `zen-operator.exe`, and invoke it directly by that path instead of giving up.\r\n\r\n";
        const string nextHeading = "DATA SHAPE AND OWNERSHIP";
        var insertionPoint = prompt.IndexOf(nextHeading, StringComparison.Ordinal);
        return insertionPoint >= 0
            ? prompt.Insert(insertionPoint, instructions)
            : prompt.TrimEnd() + "\r\n\r\n" + instructions;
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
