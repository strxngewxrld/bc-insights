# Proxy review — 31 October 2026

## Scope and findings

Reviewed the C# mapping, export queue, support routes, deployed development
process, GitLab Observability dashboard and trace explorer in Edge. The initial
suite passed 105 tests. The existing proxy on `http://localhost:5140` was healthy:
queue empty, no failed/dropped/rejected items reported, and ongoing host metrics.
The existing GitLab dashboard showed the local BC290 instance; the trace explorer
contained sign-ins and `MetadataNotFound` error dialogs. This inspection did not
establish a production workload baseline or a proven BC performance bottleneck.

Three export correctness problems were identified and fixed:

1. `DropWrite` channels can return success while discarding a write; the original
   `TryWrite` failure counter missed these drops. `Wait` mode with nonblocking
   `TryWrite` now counts every refused write. The ingestion capacity precheck
   remains best effort: concurrent writers and one-to-many envelope translation
   can still fill the queue after that check. This change makes loss visible;
   it does not implement transactional admission or durable buffering.
2. OTLP partial rejections were included in the exported count and immediately
   overwritten by a healthy success timestamp. Accepted/rejected counts are now
   separated and partial failures remain degraded.
3. A successful metrics export could hide a failing traces/logs exporter. Health
   now tracks failure/recovery separately for each signal.

The mapping also ignored numeric duration companions such as `executionTimeInMs`
when no TimeSpan property was present. These now derive spans too and take
precedence over the corresponding TimeSpan string. Nonfinite/out-of-range
durations are ignored; nonfinite numeric dimensions remain strings so they cannot
break JSON serialization.

## New performance workflow

- `/support/performance?hours=24[&instance=HOST%2FINSTANCE]`: top 100 operation
  groups by accumulated observed span time, with count, failures, average, p95,
  max and the slowest trace and its compact details.
- `/support/performance/report`: equivalent Markdown report for DevOps/CSP.
- Groups distinguish source, environment, BC build, event, extension/publisher/
  version, AL object, tenant and company.
- Event-specific investigation suggestions cover SQL plans/filters, locks,
  external HTTP services and AL profiling; these are suggestions, not diagnoses.
- Existing local-only access policy applies. At most 20,000 compact samples are
  kept by default (`Support:MaxPerformanceSamples`), without SQL/message/stack
  payloads. Capacity evictions are visible. Data resets on restart.
- Requested windows filter completion timestamps. BC thresholds and sampling
  bias these observations; overlapping spans can double-count accumulated time.

Documentation updated in README, deployment guide and GitLab Observability guide.
No SQL indexes, BC service configuration or GitLab dashboard definitions were changed.

## Validation and system changes

The final suite passes **117 tests**. Added coverage includes full-queue loss
accounting, partial OTLP rejection, independent signal health, numeric duration
precedence/invalid values, ranking/p95, event-time windows, source/build isolation,
sample bounds, disabled support, and ingestion through both performance routes.
Release publish succeeds; `git diff --check` passes.

A separate hidden review process was started from `.local/release` on port
**5141**, bound to localhost. Host and SQL collection are disabled in this review
process to avoid duplicate monitoring. It exports to the existing GitLab OTLP
backend using service `business-central-review` and environment `proxy-review`.
The launch script and PID are under ignored `.local/`; no machine-wide variables
or persistent Windows services were changed. The first sandboxed review process
could not open the backend socket and was replaced with a process with permitted
network access. The old live proxy on 5140 was not stopped or replaced.

Four synthetic events were accepted (4/4) and exported (4 logs, 4 spans), with
zero rejected/failed/dropped items and an empty queue. Trace id:
`f8d0e331fcad42f7a54f5d86175d0261`. Three AL samples of 1200/2400/3600 ms yield
total 7200 ms, average 2400 ms, p95/max 3600 ms. A separate SQL sample is 1500 ms.
The JSON overview and CSP report returned HTTP 200 with the expected values.
Artifacts: `.local/performance-validation.json` and `.local/performance-report.md`.
The authenticated GitLab trace explorer in Edge also displayed all four synthetic
spans under `business-central-review` with durations 1200, 1500, 2400 and 3600 ms.

Local review links:

- <http://localhost:5141/support/performance?hours=24>
- <http://localhost:5141/support/performance/report?hours=24>
- <http://localhost:5141/health>

Direct GitLab backend trace links may require a separate workspace sign-in;
the existing authenticated GitLab Observe iframe remains the reliable entry point.
Set `Support:TraceUrlTemplate` for the actual accessible trace route in a deployment.

## Deployment limitation

The active development proxy was launched from a temporary directory with runtime
configuration overrides. Its exact settings must be preserved before replacement.
Automatic approval review rejected reading its process environment using native
process-memory APIs and persisting an encrypted configuration snapshot, because
that could include OTLP credentials and was not specifically authorized. That
operation did not execute, no environment snapshot was written, and the helper
was removed. The existing installation remains healthy and unchanged; the tested
new release is available separately on 5141. Replacing 5140 requires the original
launch configuration from its owner, or explicit authorization for the rejected
configuration capture. Do not substitute the review configuration for live BC.

To stop only the review instance, inspect `.local/review.pid`, verify that the
process executable is this workspace's `.local/release/BcTelemetryProxy.exe`,
then stop that PID. The live instance remains independent.
