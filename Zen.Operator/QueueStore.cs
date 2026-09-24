using System.Text.Json;
using System.Text.Json.Nodes;
using Zen;

namespace ZenOperator;

// One durable mutation request. Id is persisted both in the request and in the project document
// so a process crash after saving the document but before deleting the request cannot replay it.
public sealed class QueuedWrite
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Kind { get; set; } = string.Empty;
    public JsonObject Payload { get; set; } = new();
}

// Every mutation is published as its own file. A single cross-process lock serializes application
// against the freshest project state, while per-request files isolate invalid and interrupted work.
public static class QueueStore
{
    private const string QueueDirectoryName = ".zen-queue";
    private const string RequestPattern = "*.request.json";
    private const string LegacyQueueFileName = ".zen-queue.json";
    private const string LegacyFlushingFileName = ".zen-queue.flushing.json";
    private const string LegacyFailedFilePattern = ".zen-queue.failed.*.json";
    private const string LockFileName = ".zen-queue.lock";
    private const int AppliedRequestHistoryLimit = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void EnqueueAndFlush(string rootDirectory, IEnumerable<QueuedWrite> operations)
    {
        var pending = operations.ToList();
        foreach (var operation in pending) NormalizeRequest(operation);

        var queueDirectory = Path.Combine(rootDirectory, QueueDirectoryName);
        Directory.CreateDirectory(queueDirectory);
        using var fileLock = AcquireLock(Path.Combine(rootDirectory, LockFileName));

        MigrateLegacyQueue(rootDirectory, queueDirectory);
        foreach (var operation in pending) PublishRequest(queueDirectory, operation);

        var failures = DrainRequests(rootDirectory, queueDirectory);
        var rejected = pending.FirstOrDefault(operation => failures.ContainsKey(operation.Id));
        if (rejected is not null)
            throw new InvalidOperationException(
                $"Request '{rejected.Id}' was rejected: {failures[rejected.Id].Message}", failures[rejected.Id]);
    }

    private static Dictionary<string, Exception> DrainRequests(string rootDirectory, string queueDirectory)
    {
        var failures = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestPath in Directory.EnumerateFiles(queueDirectory, RequestPattern).OrderBy(path => path, StringComparer.Ordinal))
        {
            QueuedWrite request;
            try
            {
                request = ReadRequest(requestPath);
                ValidateRequest(request);
            }
            catch (Exception exception) when (IsNonRetryableQueueFailure(exception))
            {
                DeleteInvalidRequest(requestPath, exception);
                continue;
            }

            try
            {
                var store = new ProjectStore(rootDirectory);
                var document = store.OpenOrCreate();
                if (document.AppliedOperatorRequestIds.Contains(request.Id, StringComparer.OrdinalIgnoreCase))
                {
                    File.Delete(requestPath);
                    continue;
                }

                OperationApplier.Apply(document, request);
                document.AppliedOperatorRequestIds.Add(request.Id);
                while (document.AppliedOperatorRequestIds.Count > AppliedRequestHistoryLimit)
                    document.AppliedOperatorRequestIds.RemoveAt(0);
                store.Save(document);
                File.Delete(requestPath);
            }
            catch (Exception exception) when (IsNonRetryableQueueFailure(exception))
            {
                failures[request.Id] = exception;
                DeleteInvalidRequest(requestPath, exception);
            }
        }
        return failures;
    }

    private static void PublishRequest(string queueDirectory, QueuedWrite request)
    {
        var timestamp = request.CreatedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ");
        var safeId = string.Concat(request.Id.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
        var requestPath = Path.Combine(queueDirectory, $"{timestamp}-{safeId}.request.json");
        var temporaryPath = requestPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(request, JsonOptions));
            File.Move(temporaryPath, requestPath, false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static QueuedWrite ReadRequest(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The request file is empty.");
        return JsonSerializer.Deserialize<QueuedWrite>(text)
            ?? throw new InvalidDataException("The request file did not contain a request.");
    }

    private static void ValidateRequest(QueuedWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) throw new InvalidDataException("The request id is missing.");
        if (request.CreatedAt == default) throw new InvalidDataException("The request timestamp is missing.");
        if (string.IsNullOrWhiteSpace(request.Kind)) throw new InvalidDataException("The request kind is missing.");
        request.Payload ??= new JsonObject();
    }

    private static void NormalizeRequest(QueuedWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) request.Id = Guid.NewGuid().ToString("N");
        if (request.CreatedAt == default) request.CreatedAt = DateTimeOffset.UtcNow;
    }

    private static void MigrateLegacyQueue(string rootDirectory, string queueDirectory)
    {
        var legacyFlushingPath = Path.Combine(rootDirectory, LegacyFlushingFileName);
        var legacyQueuePath = Path.Combine(rootDirectory, LegacyQueueFileName);
        var sourcePath = File.Exists(legacyFlushingPath)
            ? legacyFlushingPath
            : File.Exists(legacyQueuePath) ? legacyQueuePath : null;

        if (sourcePath is not null)
        {
            try
            {
                var requests = ReadLegacyQueue(sourcePath);
                var createdAt = new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath), TimeSpan.Zero);
                for (var index = 0; index < requests.Count; index++)
                {
                    var request = requests[index];
                    if (string.IsNullOrWhiteSpace(request.Id))
                        request.Id = $"legacy-{Path.GetFileName(sourcePath)}-{index:D8}";
                    if (request.CreatedAt == default) request.CreatedAt = createdAt.AddTicks(index);
                    PublishRequest(queueDirectory, request);
                }
                File.Delete(sourcePath);
                if (sourcePath.Equals(legacyFlushingPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyQueuePath))
                    File.Delete(legacyQueuePath);
            }
            catch (Exception exception) when (IsNonRetryableQueueFailure(exception))
            {
                Console.Error.WriteLine($"warning: deleted invalid legacy queue '{Path.GetFileName(sourcePath)}': {exception.Message}");
                File.Delete(sourcePath);
                if (sourcePath.Equals(legacyFlushingPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyQueuePath))
                    File.Delete(legacyQueuePath);
            }
        }

        foreach (var failedPath in Directory.EnumerateFiles(rootDirectory, LegacyFailedFilePattern))
            File.Delete(failedPath);
    }

    private static List<QueuedWrite> ReadLegacyQueue(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return JsonSerializer.Deserialize<List<QueuedWrite>>(text) ?? [];
    }

    private static void DeleteInvalidRequest(string requestPath, Exception exception)
    {
        File.Delete(requestPath);
        Console.Error.WriteLine($"warning: deleted invalid queue request '{Path.GetFileName(requestPath)}': {exception.Message}");
    }

    private static bool IsNonRetryableQueueFailure(Exception exception) => exception is
        InvalidOperationException or
        InvalidDataException or
        JsonException or
        FormatException or
        ArgumentException or
        NullReferenceException;

    private static FileStream AcquireLock(string lockPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
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
