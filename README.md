# OpenFSSO

FortiGate applies user-based firewall policies through Fortinet Single Sign-On
(FSSO), which needs to know which user is behind each IP address. The FSSO
Collector Agent normally infers this indirectly, from domain controller logon
events and by polling workstations.

OpenFSSO has each Windows workstation report its signed-in user directly:

- A client service on the workstation proves the user's identity with Kerberos
  to a receiver service on each Collector Agent host.
- The receiver takes the IP address from that connection, so a workstation can
  only report the address it actually uses.
- The receiver passes the mapping to the local Collector Agent over syslog: a
  login when the user signs in, a logout when they sign off, and an hourly update
  in between.

## Requirements

- Domain-joined 64-bit Windows 10 2004, Windows Server 2022 or newer.
- Fortinet Collector Agent 5.0.0291 or later in Advanced Mode on each receiver
  host.
- Direct paths from workstations to the receivers, without NAT or proxies, so
  every receiver sees the address FortiGate sees.
- The receivers and the AD, KDC, time and PKI services must be reachable
  without FSSO authorization.

## Installation

Register each receiver's HTTP SPN on its computer account and bind a trusted certificate to its HTTP.sys listener:

```powershell
setspn -S HTTP/receiver-a.example.test EXAMPLE\COLLECTOR-A$
netsh http add sslcert ipport=0.0.0.0:8443 certhash=<thumbprint> appid={<guid>}
```

Adapt `config/client.example.yaml` and `config/receiver.example.yaml`. Give each
receiver its own random `HealthApiKey`, for example from
`python3 -c 'import secrets; print(secrets.token_urlsafe(32))'`. Keep certificate
revocation checking enabled.

Install from an elevated terminal or a SYSTEM deployment agent:

```powershell
msiexec /i OpenFSSO.Client-<version>.msi CONFIGFILE="C:\Release\client.yaml" /qn /norestart
msiexec /i OpenFSSO.Server-<version>.msi CONFIGFILE="C:\Release\server.yaml" /qn /norestart
```

The installer copies `CONFIGFILE` to `Config\config.yaml` below the application's
Program Files directory, readable only by Administrators and SYSTEM. Logs and
state are in `C:\ProgramData\OpenFsso\<service>\`. Upgrades, repairs and
uninstall keep the configuration unless `CONFIGFILE` supplies a new one. To
replace it in a same-version reinstall, add `REINSTALL=ALL REINSTALLMODE=vomus`.
A configuration that fails validation stops the service from starting, and the
installation rolls back. Return codes 0 and 3010 mean success.

The client MSI registers the tray for every sign-in and adds a Start menu entry.
The client service also starts the tray in sessions that are already signed in.

## Configuration

Both services read YAML with case-sensitive property names. Single-quote Windows
paths and the syslog templates, which end with a space. A client lists one to
eight receivers under `Receivers`. Configuration is read at service start. Check
a file before deployment with:

```powershell
OpenFsso.Client.exe --validate-config C:\Release\client.yaml
```

The embedded schemas are [config/client.schema.json](config/client.schema.json)
and [config/receiver.schema.json](config/receiver.schema.json).

### Fabric detection

The optional client `onFabric` rule limits sending to the configured networks:

```yaml
onFabric:
  all:
    - clientIp: [10.12.0.0/16, 10.13.13.0/24]
    - any:
        - dhcpServer: {ip: 10.14.14.1, mac: '00:11:22:33:44:55'}
        - not:
            clientIp: [10.12.99.0/24]
```

Each node holds exactly one of `clientIp` (CIDR alternatives, IPv4 or IPv6),
`dhcpServer` (IPv4 address and optional MAC), `any`, `all` or `not`. `any` and
`all` take two or more rules, `not` takes one, and groups nest. The complete
rule is evaluated per active adapter and at least one adapter must pass. A MAC
matches only a DHCP server on the adapter's local link.

Detection runs before every request cycle. Off-fabric clients pause presence and
release, their receiver leases expire, and returning on-fabric sends fresh state.
The tray shows a blue dot while off-fabric.

## Fortinet collector

The receiver sends syslog to the Collector Agent on the same host: a login when
a mapping starts, a logout when it ends, and an hourly update for each active
mapping. Use Collector Agent 5.0.0291 or later in Advanced Mode, with its
syslog server enabled on UDP 514 (the receiver's `SyslogPort`).

The default templates produce lines such as:

```text
<134>OpenFsso action=login user="EXAMPLE\alice" group="ignored-by-remote-LDAP" ip=192.0.2.31 
```

Add this matching rule under **Advanced Settings > Syslog Source List**.
Replace `EXAMPLE` with the users' NetBIOS domain. Users of other domains do not
match and are not mapped. All three events are required.

| Field | Value |
| --- | --- |
| Trigger | `OpenFsso` |
| Logon | `action=login` |
| Logoff | `action=logout` |
| Update | `action=update` |
| Username | `user="EXAMPLE\{{:user}}"` |
| Client IPv4 | `ip={{:client_ip}} ` (with the trailing space) |
| Group | `group="{{:group}}"`, separator `,` |

Add a syslog source for the receiver's `SyslogSourceAddress` (default
`127.0.0.1`) with this rule and user type **Remote User / LDAP**, bound to an
LDAP server for the domain (user attribute `sAMAccountName`, groups from
`memberOf`). If another syslog source already uses `127.0.0.1`, set
`SyslogSourceAddress` to another loopback address, such as `127.0.0.2`. The
collector resolves the user's groups at every login and update. The group field
in the message is ignored.

Collector timers:

- **Workstation verify interval** `0`. The collector cannot verify these
  workstations, so its checks only cost time.
- **Dead entry timeout** stays enabled, 480 minutes by default. It purges an
  entry that receives no login or update for that long, even while the user
  stays logged on. The receiver's update every `ReassertSeconds` (default 3600)
  prevents that, so keep it well below the timeout.
- **IP address change verify interval** `0`, since the receiver reports address
  changes itself.

After a collector restart the receiver re-sends all active mappings as logins.
Disable competing DC polling and workstation DNS identity checks, and block
direct client syslog (UDP 514) and DC-agent input (UDP 8002) from workstations.
An unhealthy receiver node may need withdrawal through existing FSSO controls.

## Status and health

![The OpenFSSO tray icon with its status tooltip](docs/tray-status.png)

| Dot | Tooltip | Meaning |
| --- | --- | --- |
| Green | Connected | Every configured receiver acknowledged within the last two minutes |
| Amber | Connecting, Partially connected | First attempt running, or only some receivers healthy |
| Red | Delivery failed | All receivers failing |
| Red | Service unavailable, Network check unavailable | Client service or fabric check not answering |
| Gray | Waiting for logon or unlock | No selected user |
| Blue | Off fabric | Outside the configured networks, updates paused |

Scripts can query the status with `OpenFsso.Tray.exe --status-json <file>`,
which writes it to the file as JSON.

Receivers serve `GET /health` with `Authorization: Bearer <HealthApiKey>`. It
reports whether the receiver is ready, how many users are mapped, whether the
collector service runs and whether syslog messages to it could be sent. It
answers 200 when all is well and 503 otherwise:

```bash
curl --header "Authorization: Bearer $HEALTH_API_KEY" https://receiver-a.example.test:8443/health
```

## How it works in detail

- **Identity** comes only from the receiver's Kerberos authentication of the
  signed-in user, over TLS with strict channel binding. Each client keeps one
  session per receiver and authenticates it again about every hour.
- **IP address:** the client reports the local address of its connection, and
  the receiver accepts it only if it equals the connection's peer address.
- **Selected user:** the most recent console logon or unlock wins. Lock keeps the
  user, and RDP sessions count only with `AllowRdp`. After a service restart or
  installation, the saved user stays while their session is attached, and
  otherwise a single attached session is selected. Only several attached
  sessions without a matching record wait for the next logon or unlock.
- **Renewal:** the client renews every 60 seconds and backs off for 1–30 seconds
  after failures. Network, route and resume changes renew at once over a new
  connection.
- **Leases** live in receiver memory for 180 seconds after the last renewal. A
  restarted receiver starts empty and is repopulated within seconds, because
  clients reconnect immediately.
- **Logoff** sends a release, authorized by the lease reference from the last
  renewal, since the user's Kerberos credentials end with the session. Lease
  expiry covers lost releases.

## Building

```bash
python3 tools/format.py --check
docker build --target artifacts --output type=local,dest=artifacts .
python3 tools/package.py
```

The build checks formatting, runs the MSTest suite (TRX report in
`artifacts/test-results/`) and publishes trimmed, self-contained single-file `win-x64`
executables to `artifacts/client/` and `artifacts/receiver/`.
`tools/package.py` collects them in `artifacts/release-unsigned.zip` for signing.
`python3 tools/format.py` applies formatting. Set `OPENFSSO_ARTIFACTS` to use
another output folder with the tools.

Sign the executables, create a SHA-256 catalog over each payload and sign the
catalog. Put each signed `payload/` folder and its `OpenFsso.cat` into
`artifacts/signed/client-signed.zip` and `receiver-signed.zip`, then build the
MSIs into `artifacts/msi/`:

```bash
python3 tools/build_msi.py
```

Sign the resulting MSIs with your code-signing identity and a timestamp. The
version comes from `Directory.Build.props`. `--role Receiver --service <name>`
builds a separately installable receiver instance. See
[deploy/msi/README.md](deploy/msi/README.md) for the WiX sources.

### Releases

Add a `CHANGELOG.md` entry for the version, then push a tag `v<version>` that
matches `Directory.Build.props`. This runs
[the release workflow](.github/workflows/release.yml). It builds and tests in
Docker as above, packages the unsigned payloads with
`tools/build_msi.py --unsigned`, and creates a draft GitHub release with the
changelog entry, both MSIs, the payload zip and `SHA256SUMS`. The files carry a
build provenance attestation, which
`gh attestation verify <file> --repo <owner>/<repo>` checks.

## Repository layout

- `src/OpenFsso.Client`: workstation service
- `src/OpenFsso.Tray`: notification icon
- `src/OpenFsso.Receiver`: receiver service for the Collector Agent host
- `src/OpenFsso.Core`, `src/OpenFsso.Windows`, `src/OpenFsso.Configuration`,
  `src/OpenFsso.Logging`: shared code. Logs are ECS JSON, with OpenFSSO fields
  under `openfsso.*`.
- `tests/`: application tests
- `config/`: example configurations and their JSON Schemas
- `deploy/msi/`: WiX installer sources
- `tools/`: formatting and packaging helpers
