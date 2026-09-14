using System.Text.Json;
using System.Text.Json.Nodes;
using Zen;

namespace ZenOperator;

// A pending mutation, described generically so it can round-trip through the on-disk queue file.
public sealed class QueuedWrite
{
    public string Kind { get; set; } = "";
    public JsonObject Payload { get; set; } = new();
}

// Implements the queue contract requested for the operator: a write is appended to a queue file,
// then the whole queue is copied to a ".flushing" file, the original is cleared, the copy is
// applied to zen.tasks.json, and only then is the copy deleted. If a process is killed mid-flush,
// the next invocation finds the leftover ".flushing" file and resumes from it before doing anything else.
public static class QueueStore
{
    private const string QueueFileName = ".zen-queue.json";
    private const string FlushingFileName = ".zen-queue.flushing.json";
    private const string LockFileName = ".zen-queue.lock";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void EnqueueAndFlush(string rootDirectory, IEnumerable<QueuedWrite> operations)
    {
        var queuePath = Path.Combine(rootDirectory, QueueFileName);
        var flushingPath = Path.Combine(rootDirectory, FlushingFileName);
        var lockPath = Path.Combine(rootDirectory, LockFileName);

        using var fileLock = AcquireLock(lockPath);

        ResumeInterruptedFlush(rootDirectory, flushingPath);

        var pending = ReadQueue(queuePath);
        pending.AddRange(operations);
        File.WriteAllText(queuePath, JsonSerializer.Serialize(pending, JsonOptions));

        File.Copy(queuePath, flushingPath, true);
        File.WriteAllText(queuePath, "[]");
        ApplyFlushFile(rootDirectory, flushingPath);
    }

    private static void ResumeInterruptedFlush(string rootDirectory, string flushingPath)
    {
        if (File.Exists(flushingPath)) ApplyFlushFile(rootDirectory, flushingPath);
    }

    private static void ApplyFlushFile(string rootDirectory, string flushingPath)
    {
        var operations = ReadQueue(flushingPath);
        if (operations.Count > 0)
        {
            var store = new ProjectStore(rootDirectory);
            var document = store.OpenOrCreate();
            foreach (var operation in operations)
                OperationApplier.Apply(document, operation);
            store.Save(document);
        }
        File.Delete(flushingPath);
    }

    private static List<QueuedWrite> ReadQueue(string path)
    {
        if (!File.Exists(path)) return [];
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return JsonSerializer.Deserialize<List<QueuedWrite>>(text) ?? [];
    }

    private static FileStream AcquireLock(string lockPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }
}
