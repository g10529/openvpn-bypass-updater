# OpenVPN Bypass Updater

`OpenVpnBypassUpdater.exe` is a small Windows utility for OpenVPN GUI users.
It reads `domains.txt`, finds the currently connected OpenVPN profile, writes
host routes into `bypass-routes-auto.conf`, and reconnects the active profile
so the new direct-route rules take effect.

## What To Keep In GitHub

For the GitHub repository, keep these files:

- `OpenVpnBypassUpdater.cs`
- `build-openvpn-updater.ps1`
- `domains.txt`
- `README.md`

Optional:

- `OpenVpnBypassUpdater.exe`

The `.exe` is useful if you want people to download and run it directly, but it
is usually cleaner to attach the compiled binary to a GitHub Release instead of
tracking it as source in the repository.

## What To Distribute

For end users, the minimum distribution set is:

- `OpenVpnBypassUpdater.exe`
- `domains.txt`

Those two files should stay in the same folder.

## How It Works

1. Detect the currently active OpenVPN connection.
2. Resolve the domains in `domains.txt` to IPv4 addresses.
3. Generate `bypass-routes-auto.conf` beside the active `.ovpn` file.
4. Ensure the active `.ovpn` contains `config bypass-routes-auto.conf`.
5. Ask OpenVPN GUI to reconnect the active profile.
6. Print simple connection diagnostics for the configured domains.

## Notes

- This tool is designed for OpenVPN GUI on Windows.
- It uses IPv4 host routes in the form:

```ovpn
route 203.0.113.10 255.255.255.255 net_gateway
```

- `bypass-routes-auto.conf` is generated automatically next to the active
  profile's `.ovpn` file. Users do not need to create it manually.
- `domains.txt` is the only file that most users should edit.

## Rebuild

From PowerShell:

```powershell
& ".\build-openvpn-updater.ps1"
```
