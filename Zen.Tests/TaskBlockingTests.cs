using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Text.Json;
using Zen;
using ZenOperator;
using Xunit;

namespace Zen.Tests;

public sealed class TaskBlockingTests
{
    [Fact]
    public void ParseIds_accepts_commas_and_removes_duplicates()
    {
        Assert.Equal([12, 7], TaskBlockingRules.ParseIds("12, 7, 12"));
        Assert.Empty(TaskBlockingRules.ParseIds("clear"));
        Assert.Throws<FormatException>(() => TaskBlockingRules.ParseIds("12, nope"));
    }

    [Fact]
    public void Evaluate_enforces_identity_branch_cluster_archive_and_cycle_rules()
    {
        var (document, main, target, blocker) = CreateDocument();
        Assert.Equal(TaskBlockerStatus.Valid, TaskBlockingRules.Evaluate(document, target, [blocker.Index]).Single().Status);
        Assert.Equal(TaskBlockerStatus.Self, TaskBlockingRules.Evaluate(document, target, [target.Index]).Single().Status);
        Assert.Equal(TaskBlockerStatus.Missing, TaskBlockingRules.Evaluate(document, target, [999]).Single().Status);

        blocker.ClusterId = "cluster-a";
        Assert.Equal(TaskBlockerStatus.WrongCluster, TaskBlockingRules.Evaluate(document, target, [blocker.Index]).Single().Status);
        blocker.ClusterId = null;

        main.Tasks.Remove(blocker);
        document.Branches[1].Tasks.Add(blocker);
        Assert.Equal(TaskBlockerStatus.WrongBranch, TaskBlockingRules.Evaluate(document, target, [blocker.Index]).Single().Status);
        document.Branches[1].Tasks.Remove(blocker);
        main.Tasks.Add(blocker);

        blocker.BlockedByTaskIds.Add(target.Index);
        Assert.Equal(TaskBlockerStatus.Circular, TaskBlockingRules.Evaluate(document, target, [blocker.Index]).Single().Status);
        blocker.BlockedByTaskIds.Clear();

        main.Tasks.Remove(blocker);
        var archive = document.Branches.Single(branch => branch.IsArchive);
        blocker.IsDone = true;
        archive.Tasks.Add(blocker);
        Assert.Equal(TaskBlockerStatus.Archived, TaskBlockingRules.Evaluate(document, target, [blocker.Index]).Single().Status);
    }

    [Fact]
    public void Eligibility_excludes_a_blocked_task_until_its_blocker_finishes()
    {
        var (document, main, target, blocker) = CreateDocument();
        target.BlockedByTaskIds.Add(blocker.Index);

        var eligible = EligibilityEngine.GetEligible(document, main);
        Assert.Contains(blocker, eligible);
        Assert.DoesNotContain(target, eligible);

        blocker.IsDone = true;
        eligible = EligibilityEngine.GetEligible(document, main);
        Assert.Contains(target, eligible);
    }

    [Fact]
    public void Operator_updates_built_in_and_custom_blocking_fields()
    {
        var (document, _, target, blocker) = CreateDocument();
        target.Tags.Add("in progress");
        OperationApplier.Apply(document, new QueuedWrite
        {
            Kind = "setTaskBlockers",
            Payload = new JsonObject
            {
                ["taskId"] = target.Id,
                ["blockedByTaskIds"] = new JsonArray(blocker.Index)
            }
        });
        Assert.Equal([blocker.Index], target.BlockedByTaskIds);
        Assert.DoesNotContain("in progress", target.Tags);

        var definition = new CustomCardDefinition { Name = "Custom" };
        var field = new CustomFieldDefinition { Name = "Blocked by", Type = "blocking" };
        definition.Fields.Add(field);
        document.CustomCardTypes.Add(definition);
        target.CustomTypeId = definition.Id;
        OperationApplier.Apply(document, new QueuedWrite
        {
            Kind = "setCustomField",
            Payload = new JsonObject { ["taskId"] = target.Id, ["fieldId"] = field.Id, ["value"] = "clear" }
        });
        Assert.Empty(target.BlockedByTaskIds);
    }

    [Fact]
    public void Persistence_round_trip_preserves_valid_dependencies_and_prunes_archived_ones()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zen-blocking-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var (document, _, target, blocker) = CreateDocument();
            target.BlockedByTaskIds.Add(blocker.Index);
            var store = new ProjectStore(directory);
            store.Save(document);

            var loaded = store.OpenOrCreate();
            Assert.Equal([blocker.Index], loaded.Branches.Single(branch => branch.Id == "main").Tasks.Single(task => task.Id == target.Id).BlockedByTaskIds);

            var loadedMain = loaded.Branches.Single(branch => branch.Id == "main");
            var loadedBlocker = loadedMain.Tasks.Single(task => task.Id == blocker.Id);
            loadedMain.Tasks.Remove(loadedBlocker);
            loadedBlocker.IsDone = true;
            loaded.Branches.Single(branch => branch.IsArchive).Tasks.Add(loadedBlocker);
            ProjectStore.Normalize(loaded);
            Assert.Empty(loadedMain.Tasks.Single(task => task.Id == target.Id).BlockedByTaskIds);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Operator_cli_reports_and_enforces_blockers_in_the_eligible_queue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zen-blocking-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            new ProjectStore(directory).Save(new ProjectDocument
            {
                Branches =
                [
                    new BoardColumn { Id = "main", Title = "main", Branch = "main", IsPermanent = true },
                    new BoardColumn { Id = "archive", Title = "Archived", IsArchive = true, IsPermanent = true }
                ]
            });
            Assert.Equal(0, RunOperator(directory, out _, "task", "create", "--branch", "main", "--title", "Foundation", "--task", "First").ExitCode);
            Assert.Equal(0, RunOperator(directory, out _, "task", "create", "--branch", "main", "--title", "Dependent", "--task", "Second").ExitCode);
            Assert.Equal(0, RunOperator(directory, out _, "task", "blockers", "set", "2", "--ids", "1").ExitCode);
            Assert.NotEqual(0, RunOperator(directory, out _, "progress", "2", "start").ExitCode);

            var taskResult = RunOperator(directory, out var taskJson, "task", "2");
            Assert.Equal(0, taskResult.ExitCode);
            using var task = JsonDocument.Parse(taskJson);
            Assert.Equal(1, task.RootElement.GetProperty("blockedByTaskIds")[0].GetInt32());
            Assert.Equal("Foundation", task.RootElement.GetProperty("blockingTasks")[0].GetProperty("title").GetString());

            var eligibleResult = RunOperator(directory, out var eligibleJson, "eligible", "--branch", "main");
            Assert.Equal(0, eligibleResult.ExitCode);
            using var eligible = JsonDocument.Parse(eligibleJson);
            Assert.Single(eligible.RootElement.EnumerateArray());
            Assert.Equal(1, eligible.RootElement[0].GetProperty("index").GetInt32());

            Assert.NotEqual(0, RunOperator(directory, out _, "task", "blockers", "set", "2", "--ids", "999").ExitCode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static (ProjectDocument Document, BoardColumn Main, TaskCard Target, TaskCard Blocker) CreateDocument()
    {
        var main = new BoardColumn { Id = "main", Title = "main", Branch = "main", IsPermanent = true };
        var archive = new BoardColumn { Id = "archive", Title = "Archived", IsArchive = true, IsPermanent = true };
        var other = new BoardColumn { Id = "other", Title = "other", Branch = "other" };
        var blocker = new TaskCard { Index = 1, Title = "Foundation" };
        var target = new TaskCard { Index = 2, Title = "Dependent" };
        main.Tasks.Add(blocker);
        main.Tasks.Add(target);
        var document = new ProjectDocument { NextCardIndex = 3, Branches = [main, other, archive] };
        return (document, main, target, blocker);
    }

    private static Process RunOperator(string directory, out string standardOutput, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(typeof(OperationApplier).Assembly.Location);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch zen-operator for integration testing.");
        standardOutput = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process;
    }
}
