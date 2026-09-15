# Zen Remote

`zen-remote.exe` is a standalone web dashboard for Zen projects. It does not reference or modify the Zen desktop application, Zen.Core, or zen-operator. Project/task payloads and all task mutations are handled by launching the installed `zen-operator.exe` in each discovered project directory.

## Run

```powershell
.\zen-remote.exe
```

Tailscale is required. The server validates that this machine is online, starts its backend only on `127.0.0.1`, and automatically configures Tailscale Funnel to provide the machine's word-based HTTPS URL. This matches the original Zen remote-server setup and works from a phone without installing the Tailscale app. The console prints the exact phone-accessible URL.

Tailscale Funnel is internet-facing. There is deliberately no authentication: anyone who knows the URL can view, create, and edit tasks. The backend itself remains loopback-only, validates project, branch, task, lock, and field data, and routes every mutation through `zen-operator`.

Use `--port 4777` to select another port. Windows may ask for firewall permission the first time the server listens on the Tailscale interface.

## Project discovery

The server reads `%LOCALAPPDATA%\Zen\known-projects.json` to discover project directories, then asks `zen-operator` for the project metadata, branches, and tasks. Additional directories can be supplied without modifying Zen:

```powershell
.\zen-remote.exe --project C:\work\one --project D:\work\two
```

Use `--operator PATH` or the `ZEN_OPERATOR_PATH` environment variable if the operator is installed elsewhere.
