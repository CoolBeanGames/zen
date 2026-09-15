using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = ServerOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(ServerOptions.HelpText);
    return;
}

var tailnet = await NetworkHelpers.GetTailnetIdentityAsync();
var listenAddress = IPAddress.Loopback;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Listen(listenAddress, options.Port));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<OperatorClient>();

var app = builder.Build();
app.MapGet("/", () => Results.Content(DashboardPageV2.Html, "text/html; charset=utf-8"));
app.MapGet("/project/{projectId}", () => Results.Content(DashboardPageV2.Html, "text/html; charset=utf-8"));
app.MapGet("/project/{projectId}/task/{taskId}", () => Results.Content(DashboardPageV2.Html, "text/html; charset=utf-8"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", mode = "tailscale-funnel", tailnetUrl = tailnet.Url }));
app.MapGet("/api/projects", async (OperatorClient client, ProjectRegistry registry, CancellationToken cancellationToken) =>
{
    var projects = registry.Discover();
    var results = new JsonArray();
    foreach (var projectPath in projects)
        results.Add(await client.ReadProjectAsync(projectPath, cancellationToken));

    return Results.Json(new JsonObject
    {
        ["generatedAt"] = DateTimeOffset.UtcNow,
        ["operatorPath"] = options.OperatorPath,
        ["projects"] = results
    });
});

await app.StartAsync();
try
{
    await NetworkHelpers.ConfigureFunnelAsync(options.Port);
    Console.WriteLine($"Zen Remote dashboard: {tailnet.Url}");
    Console.WriteLine($"Loopback backend: http://127.0.0.1:{options.Port}");
    Console.WriteLine("The dashboard is exposed through Tailscale Funnel for phone access without the Tailscale app.");
    Console.WriteLine("Anyone who knows this URL can reach the read-only dashboard.");
    Console.WriteLine($"Operator: {options.OperatorPath}");
    await app.WaitForShutdownAsync();
}
finally
{
    await app.StopAsync();
}

internal sealed record ServerOptions(string OperatorPath, int Port, bool ShowHelp, IReadOnlyList<string> ProjectPaths)
{
    public const string HelpText = """
        zen-remote - read-only Zen task dashboard

        Usage:
          zen-remote [--port 4777] [--operator PATH] [--project PATH]...

        Options:
          --port PORT       HTTP port (default: 4777).
          --operator PATH  zen-operator.exe path. Defaults to %LOCALAPPDATA%\Zen\zen-operator.exe.
          --project PATH   Add a project folder in addition to Zen's known-projects list. Repeatable.
          --help            Show this text.

        Examples:
          zen-remote.exe
          zen-remote.exe --port 4777
          zen-remote.exe --project C:\work\project-a --project D:\projects\project-b
        """;

    public static ServerOptions Parse(string[] args)
    {
        var operatorPath = Environment.GetEnvironmentVariable("ZEN_OPERATOR_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zen", "zen-operator.exe");
        var port = 4777;
        var help = false;
        var projects = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h": help = true; break;
                case "--port" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], out port) || port is < 1 or > 65535)
                        throw new ArgumentException("--port must be between 1 and 65535.");
                    break;
                case "--operator" when index + 1 < args.Length: operatorPath = args[++index]; break;
                case "--project" when index + 1 < args.Length: projects.Add(args[++index]); break;
                default: throw new ArgumentException($"Unknown or incomplete option: {args[index]}");
            }
        }

        operatorPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(operatorPath));
        if (!help && !File.Exists(operatorPath))
            throw new FileNotFoundException("zen-operator.exe was not found. Pass its path with --operator.", operatorPath);
        return new ServerOptions(operatorPath, port, help, projects);
    }
}

internal sealed class ProjectRegistry(ServerOptions options)
{
    public IReadOnlyList<string> Discover()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredPath in options.ProjectPaths)
            AddIfPresent(paths, configuredPath);

        var registryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zen", "known-projects.json");
        try
        {
            if (File.Exists(registryPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
                foreach (var entry in document.RootElement.EnumerateArray())
                    if (entry.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } value)
                        AddIfPresent(paths, value);
            }
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Could not read {registryPath}: {exception.Message}");
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddIfPresent(ISet<string> paths, string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (Directory.Exists(fullPath)) paths.Add(fullPath);
    }
}

internal sealed class OperatorClient(ServerOptions options)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<JsonObject> ReadProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var project = await RunJsonAsync(projectPath, cancellationToken, "project");
            var branchPayload = await RunJsonAsync(projectPath, cancellationToken, "branches");
            var branches = new JsonArray();
            foreach (var branchNode in branchPayload.AsArray())
            {
                var branch = branchNode?.AsObject();
                if (branch?["id"]?.GetValue<string>() is not { Length: > 0 } branchId) continue;
                var tasks = await RunJsonAsync(projectPath, cancellationToken, "tasks", "--branch", branchId);
                branches.Add(new JsonObject
                {
                    ["id"] = branchId,
                    ["title"] = branch["title"]?.GetValue<string>() ?? branchId,
                    ["gitBranch"] = branch["branch"]?.DeepClone(),
                    ["isArchive"] = branch["isArchive"]?.GetValue<bool>() ?? false,
                    ["isLocked"] = branch["isLocked"]?.GetValue<bool>() ?? false,
                    ["tasks"] = tasks
                });
            }

            return new JsonObject
            {
                ["path"] = projectPath,
                ["project"] = project,
                ["branches"] = branches
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new JsonObject { ["path"] = projectPath, ["error"] = exception.Message };
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<JsonNode> RunJsonAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.OperatorPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start zen-operator.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"zen-operator timed out in {workingDirectory}.");
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"zen-operator exited with code {process.ExitCode}." : error.Trim());
        return JsonNode.Parse(output) ?? throw new InvalidOperationException("zen-operator returned empty JSON.");
    }
}

internal sealed record TailnetIdentity(string DnsName)
{
    public string Url => $"https://{DnsName}/";
}

internal static class NetworkHelpers
{
    public static async Task<TailnetIdentity> GetTailnetIdentityAsync()
    {
        var output = await RunTailscaleAsync("status", "--json");
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (!root.TryGetProperty("Self", out var self) ||
            !self.TryGetProperty("Online", out var online) || !online.GetBoolean())
            throw new InvalidOperationException("This machine is not online in Tailscale.");
        if (!self.TryGetProperty("DNSName", out var dnsProperty) || string.IsNullOrWhiteSpace(dnsProperty.GetString()))
            throw new InvalidOperationException("Tailscale did not report a MagicDNS hostname for this machine.");
        return new TailnetIdentity(dnsProperty.GetString()!.TrimEnd('.'));
    }

    public static async Task ConfigureFunnelAsync(int port)
    {
        await RunTailscaleAsync("funnel", "--bg", "--yes", $"http://127.0.0.1:{port}");
    }

    private static async Task<string> RunTailscaleAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tailscale",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start tailscale.");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) return output;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"tailscale exited with code {process.ExitCode}." : error.Trim());
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("Tailscale CLI was not found. Tailscale is required to run Zen Remote.", exception);
        }
    }
}

internal static class LegacyDashboardPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Zen Remote</title>
  <style>
    :root{color-scheme:dark;--bg:#0b0d12;--panel:#12151c;--card:#1d222c;--line:#262c38;--muted:#8992a5;--text:#f4f6fa;--accent:#8b7cff}
    *{box-sizing:border-box} body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI Variable","Segoe UI",sans-serif}
    header{position:sticky;top:0;z-index:10;display:flex;gap:18px;align-items:center;padding:18px 24px;background:#0b0d12eF;border-bottom:1px solid var(--line);backdrop-filter:blur(14px)}
    h1{font-size:20px;margin:0;white-space:nowrap}.mark{color:var(--accent)} .controls{display:flex;gap:9px;flex:1;justify-content:flex-end}
    input,select,button{background:#161a22;color:var(--text);border:1px solid #394151;border-radius:8px;padding:9px 11px;font:inherit}input{width:min(360px,45vw)}button{cursor:pointer}button:hover{border-color:var(--accent)}
    main{padding:22px}.project{margin:0 0 28px}.project-head{display:flex;align-items:end;justify-content:space-between;gap:15px;margin:0 0 12px}.project h2{font-size:18px;margin:0}.path{color:var(--muted);font-size:11px;word-break:break-all}
    .branches{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:13px}.branch{background:var(--panel);border:1px solid var(--line);border-radius:13px;padding:12px;min-width:0}.branch-head{display:flex;justify-content:space-between;align-items:center;margin-bottom:10px}.branch h3{font-size:12px;text-transform:uppercase;letter-spacing:.06em;margin:0}.count{color:var(--muted);font-size:11px}
    .cards{display:grid;gap:8px}.card{background:var(--card);border:1px solid #2a303d;border-radius:10px;padding:11px;overflow:hidden}.card.bug{border-color:#7a2632;background:#24171c}.card.done{opacity:.58}.card.waiting{box-shadow:0 0 0 1px #54c989,0 0 15px #54c98942}.top{display:flex;gap:8px;align-items:start}.id{color:var(--muted);font-size:10px;font-weight:700}.title{font-size:13px;font-weight:650;flex:1}.kind{font-size:9px;color:var(--muted);text-transform:uppercase}.body{color:#bcc3d0;font-size:12px;margin-top:7px;white-space:pre-wrap;overflow-wrap:anywhere;display:-webkit-box;-webkit-line-clamp:4;-webkit-box-orient:vertical;overflow:hidden}.tags,.meta{display:flex;flex-wrap:wrap;gap:5px;margin-top:8px}.tag,.pill{font-size:9px;font-weight:650;border-radius:5px;padding:3px 6px}.pill{background:#292f3a;color:#cbd1dc}.empty,.error{padding:22px;border:1px dashed #394151;border-radius:12px;color:var(--muted)}.error{color:#ff8997}.status{font-size:11px;color:var(--muted);margin-left:auto}.hidden{display:none!important}
    @media(max-width:680px){header{align-items:stretch;flex-direction:column;padding:14px}.controls{justify-content:stretch}.controls input{width:100%;flex:1}main{padding:14px}.branches{grid-template-columns:1fr}.project-head{align-items:start;flex-direction:column}}
  </style>
</head>
<body>
  <header><h1><span class="mark">ZEN</span> REMOTE</h1><div class="controls"><input id="search" placeholder="Search tasks, tags, IDs…"><select id="filter"><option value="open">Open tasks</option><option value="all">All tasks</option><option value="done">Completed</option></select><button id="refresh">Refresh</button></div><span class="status" id="status">Loading…</span></header>
  <main id="app"><div class="empty">Asking zen-operator for projects…</div></main>
  <script>
    const app=document.querySelector('#app'),search=document.querySelector('#search'),filter=document.querySelector('#filter'),status=document.querySelector('#status');let model=null;
    const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const textOf=t=>t.task||t.customFields?.find(f=>f.name?.toLowerCase()==='description')?.value||'';
    function render(){if(!model)return;const q=search.value.trim().toLowerCase(),mode=filter.value;let shown=0;
      app.innerHTML=model.projects.map(p=>{if(p.error)return `<section class="project"><div class="error"><b>${esc(p.path)}</b><br>${esc(p.error)}</div></section>`;const colors=Object.fromEntries((p.project.tagCatalog||[]).map(t=>[t.name.toLowerCase(),t.color]));
        const branches=p.branches.map(b=>{const tasks=(b.tasks||[]).filter(t=>{if(mode==='open'&&t.isDone)return false;if(mode==='done'&&!t.isDone)return false;const hay=[t.index,t.id,t.title,t.task,t.kind,...(t.tags||[]),...(t.customFields||[]).map(f=>`${f.name} ${Array.isArray(f.value)?f.value.join(' '):f.value}`)].join(' ').toLowerCase();return !q||hay.includes(q)});shown+=tasks.length;
          const cards=tasks.map(t=>{const title=t.title||t.customCardType?.name||t.kind||'Untitled';const tags=(t.tags||[]).map(tag=>`<span class="tag" style="background:${esc(colors[tag.toLowerCase()]||'#4b5261')}">${esc(tag)}</span>`).join('');const flags=Object.entries(t.flags||{}).filter(([,v])=>v).map(([k])=>`<span class="pill">${esc(k)}</span>`).join('');const meta=[t.isLocked?'locked':'',t.isAwaitingFeedback?'awaiting feedback':'',t.requirements?.length?`${t.requirements.filter(r=>r.isDone).length}/${t.requirements.length} requirements`:''].filter(Boolean).map(v=>`<span class="pill">${esc(v)}</span>`).join('');return `<article class="card ${t.tags?.some(x=>x.toLowerCase()==='bug')?'bug':''} ${t.isDone?'done':''} ${t.isAwaitingFeedback?'waiting':''}"><div class="top"><span class="id">#${esc(t.index)}</span><span class="title">${esc(title)}</span><span class="kind">${esc(t.kind)}</span></div>${textOf(t)?`<div class="body">${esc(textOf(t))}</div>`:''}${tags?`<div class="tags">${tags}</div>`:''}${flags||meta?`<div class="meta">${flags}${meta}</div>`:''}</article>`}).join('');return `<section class="branch"><div class="branch-head"><h3>${esc(b.title)}${b.isArchive?' · archive':''}${b.isLocked?' · locked':''}</h3><span class="count">${tasks.length}</span></div><div class="cards">${cards||'<div class="empty">No matching tasks</div>'}</div></section>`}).join('');return `<section class="project"><div class="project-head"><div><h2>${esc(p.project.name)}</h2><div class="path">${esc(p.path)}</div></div><div class="count">${esc(p.project.projectId)}</div></div><div class="branches">${branches}</div></section>`}).join('')||'<div class="empty">No Zen projects were discovered.</div>';status.textContent=`${shown} task${shown===1?'':'s'} · ${new Date(model.generatedAt).toLocaleTimeString()}`}
    async function load(){status.textContent='Refreshing…';try{const response=await fetch('/api/projects',{cache:'no-store'});if(!response.ok)throw new Error(`${response.status} ${response.statusText}`);model=await response.json();render()}catch(error){app.innerHTML=`<div class="error">${esc(error.message)}</div>`;status.textContent='Refresh failed'}}
    search.addEventListener('input',render);filter.addEventListener('change',render);document.querySelector('#refresh').addEventListener('click',load);load();setInterval(load,15000);
  </script>
</body>
</html>
""";
}
