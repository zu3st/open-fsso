# Changelog

## 0.1.32

- The client selects the signed-in user when its service starts, including right
  after installation and while the session is locked. A restart or a fresh
  install no longer waits for a lock and unlock.
- The receiver contacts the service manager first and requests extra start time,
  so a slow first start no longer fails the installation. A failed start is
  written to the Application log with its cause.
- The executables are trimmed: receiver 26 MB (was 108), client 21 MB (was 81),
  tray 14 MB (was 74). All JSON uses compile-time metadata and YAML is read
  without reflection.
- The tray names timeouts and failed connections for unreachable receivers.

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
