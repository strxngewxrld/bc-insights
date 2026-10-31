# GitLab Observability

What the proxy delivers to GitLab Observability and how to use it.

## What arrives (verified in the GitLab UI)

| Observe view       | Fed by                                                                                   | Status |
| ------------------ | ---------------------------------------------------------------------------------------- | ------ |
| Logs               | every BC telemetry event (`event.name` = RT0004, RT0030, LC00xx, AL0000E2x …), BC Event Log, `sqlserver.blocking` / `sqlserver.deadlock`, `bc.error.first_seen` | ✔ |
| Traces             | one trace per BC operation (client action): a span per event with a duration or error (sign-in, SQL, AL, web service, report, job queue, task) | ✔ log ↔ trace linked |
| Services           | the spans above: latency p50/p95/p99, ops/s, error rate, Apdex, key operations           | ✔ |
| Exceptions         | error events (RT0030 error dialogs, `ExceptionData`, RT0012 lock timeout, RT0028 deadlock) and SQL Server deadlocks: failed span + `exception` event | ✔ type = `failureReason` / `DatabaseLockTimeout` / `DatabaseDeadlock` / `SqlDeadlock` |
| Infrastructure     | `system.*` in hostmetrics shape: CPU, memory, load average, network, disk, file system    | ✔ all host charts |
| Metrics, Dashboards| `bc.server.*`, `process.*`, `system.*`, `sqlserver.*`, `bcproxy.*`                        | ✔ |
| Alerts             | any of the above                                                                          | rules to be created |
| Service map        | parent/child spans *between* services                                                     | `business-central → sql-server` from database client spans (RT0005/12/28) once they occur |
| External APIs      | client spans of outgoing HTTP calls (RT0019)                                              | appears once BC calls web services |
| Messaging queues   | Kafka/RabbitMQ                                                                            | – not applicable |
| Infrastructure → Processes | (GitLab feature not yet available)                                                | – |

Resources: `service.name` = `business-central`, `service.instance.id` = `<host>/<instance>`
(host-level metrics: `<host>`), `host.name`, `bc.server_instance`,
`service.version` (BC build, from `componentVersion`), `deployment.environment(.name)`, `os.type`.

Notes:

- Span names contain no ids (`Job queue entry finished: {id}`), so operations group.
- Windows has no load average; `system.cpu.load_average.*` is processor queue plus busy
  cores, damped like Linux — good for trends, not comparable to Linux values.
- BC's "Average server operation time (ms)" counter always reads 0: BC never updates its
  base counter. Use the operation latency from Services instead.
- Usage metrics (`system.memory.usage`, `system.filesystem.usage`, `process.memory.usage`, …)
  are non-monotonic cumulative sums as in the semantic conventions; the host table
  only accepts that shape.

Logs link to their span (trace id), so a slow sign-in or SQL statement opens
the matching log entry with all BC dimensions.

## Following one user action: web client → BC server → database

BC on-premises sends no spans of its own; the proxy builds the chain from what it gets:

| Tier | What you see | Where |
| ---- | ------------ | ----- |
| Web client / web server | the action's `ai.operation.id` (= trace id), `bc.session.id`, `bc.client_type` | Traces: one trace per action |
| BC server instance | sign-in, AL, reports, web services, job queue, error dialogs as spans of that trace; `bc.server.*` counters | Traces, Services, dashboard |
| Database | RT0005/RT0012/RT0028 as client spans to `sql-server` (`server.address`, `db.namespace`, `sqlStatement`, `sqlServerSessionId`) | Traces, Exceptions |
| SQL Server | `sqlserver.*` metrics, blocking and deadlocks with `sqlServerSessionId`, both statements | Logs / Exceptions of `sql-server`, dashboard |

`sqlServerSessionId` is the same key in BC events and SQL Server logs, so a lock
timeout in BC leads to the blocking session in SQL Server. A session's whole
history: filter logs or traces by `bc.session.id`. The BC web server itself (IIS)
has no telemetry of its own; its host shows in *Infrastructure*.

## Dashboard

[`business-central-dashboard.json`](business-central-dashboard.json) is a
ready-made dashboard in the format of the
[GitLab Observability templates](https://gitlab.com/gitlab-org/embody-team/experimental-observability/o11y-templates):

- **Business Central server instances:** active sessions, server operations/s,
  average operation time, SQL connections, cache hit rates, background tasks,
  BC server process memory and CPU.
- **Telemetry events and operations:** events by id, errors and warnings,
  operation latency p95, failed operations, error log, new error kinds, database
  waits seen by BC.
- **Servers:** CPU, memory, disk space, disk throughput, network, disk queue.
- **Database server (SQL Server):** reachable, batch requests, waits by category,
  locks / timeouts / deadlocks, connections and blocked processes, page life
  expectancy, I/O wait per database, transaction log, host CPU and memory,
  blocking and deadlock log.
- **Telemetry proxy:** export queue, items per outcome, export degraded.

Variables: `deployment.environment`, `instance` (`service.instance.id`), `host`.

Import: GitLab → *Observe* → *Dashboards* → *New dashboard* → *Import JSON*
(verified on GitLab.com: all panels render, including the log and trace panels).

## Alerting ideas

Build alerts in GitLab Observability on:

| Condition                                         | Signal                                              |
| ------------------------------------------------- | --------------------------------------------------- |
| Proxy cannot export                               | `bcproxy.export.degraded` = 1 for 5 min             |
| Telemetry lost                                    | rate of `bcproxy.items{bcproxy.outcome=failed|dropped}` > 0 |
| BC instance down / no heartbeat                   | `bc.server.heartbeat_time` absent for 5 min         |
| SQL throttling                                    | `bc.server.hard_throttled_connections` > 0          |
| Error dialogs spike                               | logs `event.name = RT0030` count                    |
| Slow sign-ins                                     | spans `bc.event.id = RT0004` p95 > 2 s              |
| Disk almost full                                  | `system.filesystem.utilization` > 0.9               |
| New error kind (notify the partner / CSP)         | logs `event.name = bc.error.first_seen`             |
| Database deadlock                                 | logs `event.name IN (sqlserver.deadlock, RT0028)`   |
| Long blocking                                     | logs `event.name = sqlserver.blocking`              |
| Database server unreachable                       | `sqlserver.up` = 0 or absent for 5 min              |
| Memory pressure on SQL Server                     | `sqlserver.page.life_expectancy` < 300              |

## Link to a GitLab project

Set `Resource:Attributes:gitlab.project.id` (env `Resource__Attributes__gitlab.project.id`)
and `gitlab.project.name` to the project that owns the BC extensions; GitLab
recommends both to connect telemetry with the project (GitLab Duo, issues from exceptions).
Per source: `Resource:Sources:<host/instance>:Attributes`.
