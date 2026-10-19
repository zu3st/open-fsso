# OpenFSSO Kerberos agent

OpenFSSO maps the interactive user of each domain-joined Windows workstation to
its IP address for Fortinet FSSO. A client service on the workstation proves the
user's identity with Kerberos to a receiver service on each FSSO Collector Agent
host. The receiver passes login and logout events to its local collector over
syslog.

## Security model and prerequisites

- **Identity** comes only from the receiver's Kerberos authentication of the
  selected user. TLS uses strict channel binding. Each client keeps one session
  per receiver, which authenticates once and is renewed about every hour.
- **IP address** comes from the TCP connection. The client reports the local
  address of the socket carrying the request, and the receiver accepts it only if
  it equals the connection's peer address. Paths to the receivers must therefore
  be direct, and every receiver must see the address FortiGate sees.
- **Owner selection:** the most recent console logon or unlock wins. Lock keeps
  the owner, logoff stops renewal, and RDP sessions count only if `AllowRdp` is set.
  A service restart keeps the saved owner while its session is attached, locked
  or not. Otherwise, including right after installation, a single attached
  session becomes the owner. Only several attached sessions without a matching
  record wait for the next logon or unlock.
- **Leases** live in receiver memory for 180 seconds after the last presence. A
  restarted receiver starts empty and is repopulated within seconds, as clients
  reconnect immediately.
- **Logoff** sends a release authorized by the lease reference the receiver
  returned with the last presence, since the user's Kerberos credentials end with
  the session. Lease expiry covers lost releases.
- **Bootstrap:** connections to the receivers and to AD, KDC, time and PKI
  services must work without FSSO authorization.

## Components

- `src/OpenFsso.Client`: LocalSystem service. It selects the owner from session
  events, renews presence with each receiver every 60 seconds and backs off for
  1–30 seconds after failures. Network, route and resume changes trigger an
  earlier renewal over a fresh connection.
- `src/OpenFsso.Tray`: per-user notification icon. A shield with a colored dot
  shows the state, and the hover card shows user, socket IP and the time of the
  latest acknowledgement.
- `src/OpenFsso.Receiver`: ASP.NET Core on HTTP.sys with native Kerberos and
  strict channel binding. A serialized pipeline applies presence, release, expiry
  and reassertion, and sends syslog to the local collector.
- `src/OpenFsso.Windows`, `src/OpenFsso.Core`, `src/OpenFsso.Configuration`,
  `src/OpenFsso.Logging`: Windows interop, leases, YAML loading with JSON Schema
  validation, and ECS JSON logging.

## Build and test

```bash
python3 tools/format.py --check
docker build --target artifacts --output type=local,dest=artifacts .
python3 tools/package.py
```

The build checks formatting, runs the MSTest suite (TRX report in
`artifacts/test-results/`) and publishes self-contained, uncompressed single-file
`win-x64` executables to `artifacts/client/` and `artifacts/receiver/`.
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

## Deployment

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
paths and the syslog templates, which end with a space. Configuration is
read at service start. Check a file before deployment with:

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

## Status and health

![The OpenFSSO tray icon with its status tooltip](docs/tray-status.png)

The client takes one to eight receivers. The tray dot is green when every
configured receiver acknowledged recently, amber while connecting or with only
some receivers healthy, red when all fail, gray while waiting for
a logon or unlock, and blue off-fabric. The tray reads the service's local
status pipe. `OpenFsso.Tray.exe --status-json <file>` writes the same snapshot.

Receivers serve `GET /health` with `Authorization: Bearer <HealthApiKey>`. It
returns readiness, the active lease count, collector service state and local UDP
delivery state, with 200 when healthy and 503 otherwise:

```bash
curl --header "Authorization: Bearer $HEALTH_API_KEY" https://receiver-a.example.test:8443/health
```

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
`memberOf`). The collector has one syslog listener and picks the rule by sender
address. If another feed, such as VPN logons, already sends from `127.0.0.1`,
set `SyslogSourceAddress` to another loopback address such as `127.0.0.2` and
add the source for that address. The collector resolves the user's
groups at every login and update. The group field in the message is ignored.

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

## Repository layout

- `src/`: services, tray and shared libraries
- `tests/`: application tests
- `config/`: example configurations and their JSON Schemas
- `deploy/msi/`: WiX installer sources
- `tools/`: formatting and packaging helpers
