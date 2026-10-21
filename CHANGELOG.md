# Changelog

## 0.1.38

- Elastic integration 0.1.2. The receivers dashboard selects clients by
  address instead of the receiver's own host.

## 0.1.37

- Receiver logs name the receiver by its host name in `service.node.name`
  instead of its Windows service name. Client events about a receiver carry its
  host name, address and port in `server.address`, `server.ip` and
  `server.port`.
- Elastic integration 0.1.1. Its dashboard controls use data views of their own
  data stream, so they list only OpenFSSO values, and both dashboards select
  receivers by host name.

## 0.1.36

- An Elastic integration in `elastic/openfsso` collects the receiver and client
  logs with Elastic Agent and adds dashboards for receivers and clients. Releases
  carry it as `openfsso-<version>.zip`.
- Every log event carries `event.action`, and most carry `event.category`,
  `event.type` and `event.outcome`. Logs add `event.kind`, `related.user`,
  `related.ip` and `service.node.name`, the Windows service name. The `host`
  block is gone, since log shippers add it.
- Receiver log lines of a request name the client address in `source.ip`,
  including rejected identities and failed health key checks.

## 0.1.35

- The receiver asks the domain about the account at each authentication, so
  about every hour. Disabled, locked-out, expired and deleted accounts are
  logged out and refused for an hour. Before, a disabled account kept its
  mapping for the life of its cached Kerberos ticket.

## 0.1.34

- The client uses one address for FSSO: the local address routed to the first
  receiver. Every receiver connection is bound to it, so all receivers map the
  same address. Fabric changes and address changes are logged with `source.ip`.
- The first attempt fails after 1 second instead of up to 15, and the timeout
  grows with the back-off (2, 4, 8 and 15 seconds).
- Logs no longer carry the `process` block.

## 0.1.33

- `OpenFsso.Client.exe --status` prints the client status as JSON and exits with
  1 when the service is not running. It replaces `OpenFsso.Tray.exe
  --status-json`, which wrote to a file. Status values such as the tray
  indicator are written as names.
- Clearer README sections on configuration and the Collector Agent.

## 0.1.32

- The client selects the signed-in user when its service starts, including right
  after installation and while the session is locked. A restart or a fresh
  install no longer waits for a lock and unlock.
- The receiver contacts the service manager first and requests extra start
  time. A failed start is written to the Application log with its cause.
- The tray uses the standard Windows notification tooltip, so it follows the
  Windows theme. It shows the address in use while a receiver cannot be reached,
  and names timeouts and failed connections.
- Log messages no longer repeat their values. User, address, action and error
  details go to ECS fields, and OpenFSSO's own values under `openfsso.*`.
- The executables are trimmed: receiver 26 MB (was 108), client 21 MB (was 81),
  tray 14 MB (was 74).
- The README starts with what OpenFSSO does and lists the Collector Agent
  settings plainly.

## 0.1.31

- New receiver setting `SyslogSourceAddress` (default `127.0.0.1`) selects the
  loopback address the receiver sends syslog from. The Collector Agent picks its
  matching rule by sender address, so a receiver can share the listener with
  another local feed by sending from, for example, `127.0.0.2`.

## 0.1.30

- Receivers send an hourly syslog update for every active mapping by default
  (`ReassertSeconds: 3600`, new `UpdateTemplate`). The Collector Agent purges
  syslog logons after its dead entry timeout even while the user stays logged
  on, and an update resets that timer and refreshes the user's groups.
- The README lists the Collector Agent syslog rule, source and timer settings.

## 0.1.29

- Presence and release take constant time per request, so a receiver keeps up
  with large client counts and anonymous release requests cannot stall it.
- Rejection warnings are limited to 10 per minute, and a claimed IP is logged
  only as a parsed address.
- The receiver ends each presence session after about an hour, so the client
  authenticates again with Kerberos.
- The same user returning with a new logon generation keeps the mapping instead
  of a brief logout.
- The client keeps sending presence when its status pipe cannot be created.
- Receiver names are limited to 64 characters.

## 0.1.28

- The client accepts one to eight receivers instead of exactly two. The tray is
  green when every configured receiver is healthy, so a single receiver shows
  green when connected.
- Tagged releases are built, tested and attested by GitHub Actions.

## 0.1.27

- First public release.
