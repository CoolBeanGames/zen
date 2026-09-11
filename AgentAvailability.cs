using System.Diagnostics;

namespace Zen;

public sealed record AgentDefinition(string Key, string Label, string Command, string InstallCommand);

// Detects whether each supported agent CLI is on PATH, so the launch menu only offers
// agents that are actually installed and Settings can offer to install the rest.
public static class AgentAvailability
{
    public static readonly AgentDefinition[] Agents =
    [
        new("codex", "Codex", "codex", "npm install -g @openai/codex"),
        new("claude", "Claude", "claude", "npm install -g @anthropic-ai/claude-code"),
        new("gemini", "Gemini", "agy", "curl -fsSL https://antigravity.google/cli/install.cmd -o install.cmd && install.cmd && del install.cmd")
    ];

    public static bool IsInstalled(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo("where", command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Dictionary<string, bool> DetectAll() =>
        Agents.ToDictionary(agent => agent.Key, agent => IsInstalled(agent.Command));
}
