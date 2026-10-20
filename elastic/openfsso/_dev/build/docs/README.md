# OpenFSSO Integration

## Overview

[OpenFSSO](https://github.com/zu3st/open-fsso) maps Windows users to IP
addresses for FortiGate. A client service on each workstation proves the
signed-in user with Kerberos to receiver services on the Fortinet Collector
Agent hosts, which pass the mappings to the Collector Agent.

This integration collects the logs of both services, so you can follow which
users are mapped, spot workstations that fail to deliver, and see receivers
that reject requests or lose their collector.

### Compatibility

The integration reads the logs of OpenFSSO 0.1.36 or later. It needs Kibana
9.5.0 or later.

### How it works

Both services write one ECS JSON document per line to
`C:\ProgramData\OpenFsso\<service>\Logs\events-<date>.jsonl`. Elastic Agent
reads these files with the filestream input, and the ingest pipeline expands
the documents. Fields without an ECS equivalent are under `openfsso.*`.

## What data does this integration collect?

- `receiver`: mappings sent to the collector, rejected requests, accounts the
  domain revoked, collector availability and receiver starts.
- `client`: delivery failures and recoveries per receiver, the selected user,
  fabric detection, tray starts and client starts.

Every event has `event.action`, and most have `event.category`, `event.type`
and `event.outcome`. `service.node.name` is the Windows service name, which
tells several receivers on one host apart.

### Supported use cases

- Follow logins, updates and logouts per user and address.
- Find workstations whose deliveries fail, by receiver and HTTP status.
- Watch receivers for rejected identities, address claims and revoked accounts.
- Track the versions of the deployed clients and receivers.

## What do I need to use this integration?

Elastic Agent on the receiver hosts and on the workstations, enrolled in Fleet.

## How do I deploy this integration?

1. Add the integration to two agent policies, one for the Collector Agent
   hosts and one for the workstations.
2. In the receiver policy, turn on **OpenFSSO receiver logs** and turn off
   **OpenFSSO client logs**. In the workstation policy, do the reverse.
3. Adjust **Paths** when the services' `LogDirectory` differs from the default.

The default receiver path matches every service whose name starts with
`OpenFssoReceiver`.

### Dashboards

**[Logs OpenFSSO] Receivers** shows mapped users, rejected requests, syslog
failures, collector outages and the state of each receiver.

![Receivers dashboard](../img/openfsso-receivers.png)

**[Logs OpenFSSO] Clients** shows which clients fail to deliver to which
receiver and why, their versions and fabric state, and the selected users.

![Clients dashboard](../img/openfsso-clients.png)

### Validation

Open **[Logs OpenFSSO] Receivers** or **[Logs OpenFSSO] Clients** after a user
signs in to a workstation. A receiver logs a login within a minute.

## Troubleshooting

- Documents with `event.kind: pipeline_error` hold lines the pipeline could not
  parse, with the reason in `error.message`. Turn on **Preserve original event**
  to keep the raw line in `event.original`.
- The services keep their logs for 14 days. Agent skips files not modified
  within the **Ignore events older than** setting, 72 hours by default.

## Performance and scaling

A client writes a few events per sign-in and per network change. A receiver
writes a few events per signed-in user and hour, plus one per rejected request. Rejection warnings are limited to ten per minute.

## Reference

### receiver

{{event "receiver"}}

{{fields "receiver"}}

### client

{{event "client"}}

{{fields "client"}}
