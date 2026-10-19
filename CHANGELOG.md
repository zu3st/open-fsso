# Changelog

## Unreleased

- Receivers re-send all active logins every hour by default. The Collector
  Agent purges syslog logons after its dead entry timeout even while the user
  stays logged on, and a re-sent login resets that timer.
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
