# Zen Remote

`zen-remote.exe` is a standalone, read-only web dashboard for Zen projects. It does not reference or modify the Zen desktop application, Zen.Core, or zen-operator. Project and task payloads are obtained by launching the installed `zen-operator.exe` in each discovered project directory.

## Run

```powershell
.\zen-remote.exe
```

Tailscale is required. The server asks `tailscale ip -4` for this machine's tailnet address and binds only to that address; it never listens on localhost or public interfaces. The console prints the exact phone-accessible URL. Traffic remains limited by the machine's Tailscale connectivity and tailnet ACLs/grants. HTTP content is transported inside Tailscale's encrypted tunnel.

Use `--port 4777` to select another port. Windows may ask for firewall permission the first time the server listens on the Tailscale interface.

## Project discovery

The server reads `%LOCALAPPDATA%\Zen\known-projects.json` to discover project directories, then asks `zen-operator` for the project metadata, branches, and tasks. Additional directories can be supplied without modifying Zen:

```powershell
.\zen-remote.exe --project C:\work\one --project D:\work\two
```

Use `--operator PATH` or the `ZEN_OPERATOR_PATH` environment variable if the operator is installed elsewhere.
