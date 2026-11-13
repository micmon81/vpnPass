# vpnPass

A small Windows tray app that sends selected public destinations through a physical network connection. Built for the home-network routing approach tested with GlobalProtect on this PC.

## Run

The updated switch UI is in **`dist\vpnPass-v1.1.2\vpnPass.exe`**. Exit the older version from its tray menu before opening this one, then accept the Windows administrator prompt. Your existing saved rules are retained. The Release executable includes its .NET runtime; no separate .NET installation is required. This is an unsigned local build, not an installer.

1. Connect the VPN.
2. Open vpnPass. The home connection is selected automatically. To choose Ethernet/Wi-Fi explicitly, open **Connection details**.
3. Turn on the switch beside the **ChatGPT** routing rule.
4. Leave vpnPass running and try the desktop app. Restart the desktop app if an existing connection still uses the VPN.
5. Use **Hide to tray** to keep vpnPass running. Right-click the tray icon to toggle rules.

Turning a switch off removes that rule's routes. **Turn all on** enables every configured rule. **Turn all off** disables every rule and removes all routes managed by this instance. Both actions are available in the window and tray menu. **Exit** removes routes and closes vpnPass. The window's X and minimize buttons hide it to the tray; they do not exit. Rules start off on every launch.

If a previous diagnostic script is still running, stop it or wait for its routes to expire first. vpnPass deliberately leaves existing host routes untouched. **Refresh now** retries immediately.

## Configure routing rules

Use **Add rule** or the **Edit** button beside a rule. **Remove rule** is inside the editor. A rule needs a display name (such as ChatGPT) and one or more destination domains or public IPv4/IPv6 addresses, one per line. For example:

```text
chatgpt.com
```

The ChatGPT rule is included. Create additional rules for other destinations and add their required domains. Spotify, WhatsApp and similar services can use separate login, media, CDN and call endpoints; a single domain may not cover everything. Those services have not been validated with this app.

Rules match destination IP addresses, including those resolved from configured domains. They do not select a process or executable and cannot distinguish URL paths. The display name is only a label. An enabled switch requests home routing; the status below the rules reports errors or incomplete updates. Destinations shared by multiple programs or websites use the same route.

Do not enter `https://`, paths, wildcard names, ports or subnet ranges. Private/local addresses are rejected, including private addresses returned by DNS. The app supports up to 32 rules, 32 entries per rule, 64 distinct entries overall, and 256 resolved addresses per update.

Settings are stored at **`%LOCALAPPDATA%\vpnPass\profiles.json`**. Under **Connection details**, **Open settings folder** opens that folder. The filename and JSON `Profiles` field retain their original names for compatibility. The JSON can also be edited while the app is closed. Invalid settings are reported and left intact. Logs are stored alongside settings in `activity.log`, with one rotated previous log. Manual updates record total duration, queue wait, DNS time, Windows route-update time, route count and warning count. Routine successful renewals stay quiet; errors are still reported.

Example JSON:

```json
{
  "AdapterId": "",
  "Profiles": [
    {
      "Id": "45a2b0bd8c134c32b5147679d8493790",
      "Name": "ChatGPT",
      "Targets": ["chatgpt.com"]
    }
  ]
}
```

`AdapterId` is empty for automatic selection, or the physical adapter GUID selected in the UI. Each rule needs a unique GUID. On/off states are intentionally not saved.

## Behaviour and limits

- Resolves names using Windows DNS and refreshes every 60 seconds while routes are requested or still need cleanup. Routine refreshes leave the controls usable and do not flash an updating message. User actions that arrive during a refresh are queued and applied afterward. The resolver bounds individual DNS waits to three seconds and runs at most four lookups concurrently. Cached DNS responses are governed by Windows DNS caching.
- Adds only individual `/32` IPv4 or `/128` IPv6 routes in Windows' `ActiveStore`. It does not change the default route, DNS settings, hosts file, proxy settings, firewall or VPN configuration.
- Routes expire five minutes after their last successful creation/renewal. If the app crashes or the PC sleeps through the renewal interval, the routes cease to be valid. Normal **Exit** removes them immediately; shutdown/forced termination rely on the expiry fallback if cleanup cannot run.
- Tracks its own routes for cleanup. Existing host routes are skipped, even if they point to the same gateway. Shared addresses between enabled rules are routed only once and retained until no enabled rule requests them.
- Reconciles route changes when rules, DNS answers or the selected physical network change. A DNS failure removes that target's previous addresses unless another enabled target still resolves to them; it is reported in activity and retried on refresh.
- **Automatic** uses an available physical adapter, preferring the lowest IPv4 default-route plus interface metric. Select the desired adapter explicitly if multiple physical networks are connected.
- “Routes active” means Windows accepted the routes. It does not prove that every endpoint of a service is reachable or that an existing app connection has switched over.
- Routing applies to destination IPs across the computer, not to individual processes. Other services sharing a destination IP also use that route. It cannot implement wildcard domain routing, per-app routing, or discover every service endpoint automatically.
- GlobalProtect enforcement or corporate network policy changes can prevent this approach from working. Windows administrative rights are required to change routes; no service, scheduled task or startup entry is installed.

## Build and checks

Requires the .NET 10 SDK and Windows. No third-party NuGet packages are used.

```powershell
.\build.ps1 -OutputDirectory dist\vpnPass-v1.1.2 -PackageName vpnPass-v1.1.2-win-x64.zip
```

This runs the C# checks and mocked Windows route lifecycle tests, then publishes a self-contained Windows x64 executable in `dist\vpnPass-v1.1.2` and creates `dist\vpnPass-v1.1.2-win-x64.zip`. A separate folder lets you keep an older version running while building the update.

Manual commands:

```powershell
dotnet build VpnPass\VpnPass.csproj -c Release
dotnet run --project VpnPass.Tests\VpnPass.Tests.csproj -c Release -- --inspect-network --render
powershell.exe -NoProfile -ExecutionPolicy Bypass -File VpnPass.Tests\Test-Routes.ps1
dotnet publish VpnPass\VpnPass.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\vpnPass-v1.1.2
```

The tests validate profile parsing/persistence, DNS failure/timeout handling, address deduplication and route lifecycle rules. `--inspect-network` runs a read-only discovery through the actual Windows helper. `--render` writes preview images in `artifacts` without changing routes. Route mutation tests use mocked Windows cmdlets; the packaged app still needs its first elevated end-to-end activation on this PC.

The app uses a bundled PowerShell helper for Windows network commands. Requests are encoded JSON data passed through structured process arguments; domain names are never interpolated into shell code. The helper is extracted to a temporary file while the app runs and removed on normal exit.
