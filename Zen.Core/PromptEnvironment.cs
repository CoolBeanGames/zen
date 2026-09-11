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

    private static readonly string[] OperatorFiles =
    [
        "zen-operator.exe", "zen-operator.dll", "zen-operator.deps.json", "zen-operator.runtimeconfig.json"
    ];

    // zen-operator ships alongside Zen.exe in a versioned publish folder, so its location
    // changes every release. Rather than repeatedly reshuffling PATH (which already-open
    // agent shells never pick up), copy it into the stable Directory, which Sync() already
    // keeps on PATH permanently.
    public static void SyncOperatorPath(SettingsStore store, ZenSettings settings)
    {
        var operatorDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourceExe = Path.Combine(operatorDirectory, "zen-operator.exe");
        if (!File.Exists(sourceExe) || SamePath(operatorDirectory, Directory)) return;

        System.IO.Directory.CreateDirectory(Directory);
        foreach (var fileName in OperatorFiles)
        {
            var source = Path.Combine(operatorDirectory, fileName);
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(Directory, fileName);
            if (File.Exists(destination) && FilesAreIdentical(source, destination)) continue;
            var temporaryPath = destination + ".tmp";
            File.Copy(source, temporaryPath, true);
            File.Move(temporaryPath, destination, true);
        }

        // Clean up a stale versioned PATH entry left behind by older Zen builds.
        if (!string.IsNullOrEmpty(settings.OperatorPathEntry))
        {
            const EnvironmentVariableTarget target = EnvironmentVariableTarget.User;
            var entries = (Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (entries.RemoveAll(entry => SamePath(entry, settings.OperatorPathEntry)) > 0)
                Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, entries), target);

            settings.OperatorPathEntry = string.Empty;
            store.Save(settings);
        }
    }

    private static bool FilesAreIdentical(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length) return false;
        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        int byteA, byteB;
        do
        {
            byteA = streamA.ReadByte();
            byteB = streamB.ReadByte();
            if (byteA != byteB) return false;
        } while (byteA != -1);
        return true;
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
