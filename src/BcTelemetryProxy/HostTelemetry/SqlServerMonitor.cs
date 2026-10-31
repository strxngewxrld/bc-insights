using System.Diagnostics;
using System.Xml.Linq;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using BcTelemetryProxy.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace BcTelemetryProxy.HostTelemetry;

/// <summary>
/// Agentless monitoring of the BC database servers (also remote ones in a 2-tier
/// setup) through dynamic management views, every <c>IntervalSeconds</c>:
/// <list type="bullet">
/// <item><c>sqlserver.*</c> metrics: availability, batches, connections, page life
/// expectancy, cache hit ratio, locks, deadlocks, memory, waits by category, file
/// I/O and latency per database, transaction log, CPU and memory of the SQL host,</item>
/// <item>a <c>sqlserver.blocking</c> log per request blocked longer than the threshold
/// (blocked and blocking session, wait, resource, both statements),</item>
/// <item>a failed span with <c>exception</c> event plus log per deadlock from the
/// <c>system_health</c> session, also counted for support reports.</item>
/// </list>
/// Resource: <c>service.name</c> = <see cref="MappingOptions.DatabaseServiceName"/>,
/// <c>service.instance.id</c> = <c>host/sqlInstance</c>, <c>db.system</c> = mssql.
/// </summary>
public sealed class SqlServerMonitor : BackgroundService
{
    private const int MaxGraphLength = 16_000;
    private const int MaxStatementLength = 4_000;

    private readonly SqlMonitoringOptions _options;
    private readonly string _serviceName;
    private readonly IOtelEmitter _emitter;
    private readonly SourceResolver _sources;
    private readonly AttributeMapper _attributes;
    private readonly ErrorRegistry? _errors;
    private readonly BcInstanceCatalog _catalog;
    private readonly ILogger<SqlServerMonitor> _logger;

    public SqlServerMonitor(
        IOptions<SqlMonitoringOptions> options,
        IOptions<MappingOptions> mapping,
        IOtelEmitter emitter,
        SourceResolver sources,
        AttributeMapper attributes,
        BcInstanceCatalog catalog,
        ILogger<SqlServerMonitor> logger,
        ErrorRegistry? errors = null)
    {
        _options = options.Value;
        _serviceName = mapping.Value.DatabaseServiceName;
        _emitter = emitter;
        _sources = sources;
        _attributes = attributes;
        _catalog = catalog;
        _logger = logger;
        _errors = errors;
    }

    /// <summary>Servers to monitor: discovered from the BC instances plus configured ones.</summary>
    public static IReadOnlyList<(string Name, string ConnectionString)> Targets(SqlMonitoringOptions options, BcInstanceCatalog catalog)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.AutoDiscover)
        {
            foreach (var db in catalog.Databases.Values.DistinctBy(d => d.Server, StringComparer.OrdinalIgnoreCase))
            {
                targets[db.DisplayName] = options.ConnectionStringTemplate.Replace("{server}", db.Server, StringComparison.Ordinal);
            }
        }

        foreach (var (name, connectionString) in options.Servers)
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                targets[name] = connectionString;
            }
        }

        return targets.Select(t => (t.Key, t.Value)).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var servers = Targets(_options, _catalog).Select(t => new Server(t.Name, t.ConnectionString)).ToList();
        if (servers.Count == 0)
        {
            _logger.LogInformation("SQL monitoring: no database server found or configured");
            return;
        }

        _logger.LogInformation("SQL monitoring: {Servers}", string.Join(", ", servers.Select(s => s.Name)));
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _options.IntervalSeconds)));
        do
        {
            foreach (var server in servers)
            {
                await CollectAsync(server, stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(Server server, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        try
        {
            await using var connection = new SqlConnection(server.ConnectionString);
            await connection.OpenAsync(ct);
            server.Source ??= await IdentifyAsync(server, connection, ct);

            var points = new List<MetricItem>();
            Gauge(points, server, now, "sqlserver.up", 1, "1");
            await CountersAsync(server, connection, now, points, ct);
            await WaitsAsync(server, connection, now, points, ct);
            await FileIoAsync(server, connection, now, points, ct);
            await HostAsync(server, connection, now, points, ct);
            foreach (var point in points)
            {
                _emitter.EmitMetric(point);
            }

            if (_options.BlockingThresholdSeconds > 0)
            {
                await BlockingAsync(server, connection, ct);
            }

            if (_options.Deadlocks)
            {
                await DeadlocksAsync(server, connection, ct);
            }

            if (server.Failing)
            {
                server.Failing = false;
                _logger.LogInformation("SQL monitoring: {Server} reachable again", server.Name);
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException or IOException)
        {
            if (!server.Failing)
            {
                server.Failing = true;
                _logger.LogWarning(ex, "SQL monitoring: {Server} failed (needs VIEW SERVER STATE for the proxy's identity)", server.Name);
            }

            if (server.Source is not null)
            {
                var points = new List<MetricItem>();
                Gauge(points, server, now, "sqlserver.up", 0, "1");
                _emitter.EmitMetric(points[0]);
            }
        }
    }

    private async Task<TelemetrySource> IdentifyAsync(Server server, SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)),
                   CAST(ISNULL(SERVERPROPERTY('InstanceName'), 'MSSQLSERVER') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)),
                   DATEADD(second, DATEDIFF(second, GETDATE(), GETUTCDATE()), sqlserver_start_time)
            FROM sys.dm_os_sys_info
            """;
        await using var reader = await Command(connection, sql).ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        server.StartUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
        server.DeadlockWatermark = DateTime.UtcNow;
        return _sources.Database(_serviceName, reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    // -- Metrics ----------------------------------------------------------------

    private enum Kind
    {
        Gauge,
        Sum,
        UpDown,
    }

    private static readonly Dictionary<string, (string Metric, Kind Kind, string Unit, double Scale)> Counters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User Connections"] = ("sqlserver.user.connection.count", Kind.Gauge, "{connection}", 1),
        ["Processes blocked"] = ("sqlserver.processes.blocked", Kind.Gauge, "{process}", 1),
        ["Batch Requests/sec"] = ("sqlserver.batch.requests", Kind.Sum, "{request}", 1),
        ["SQL Compilations/sec"] = ("sqlserver.batch.sql_compilations", Kind.Sum, "{compilation}", 1),
        ["SQL Re-Compilations/sec"] = ("sqlserver.batch.sql_recompilations", Kind.Sum, "{compilation}", 1),
        ["Page life expectancy"] = ("sqlserver.page.life_expectancy", Kind.Gauge, "s", 1),
        ["Lock Waits/sec"] = ("sqlserver.lock.waits", Kind.Sum, "{wait}", 1),
        ["Lock Timeouts/sec"] = ("sqlserver.lock.timeouts", Kind.Sum, "{timeout}", 1),
        ["Number of Deadlocks/sec"] = ("sqlserver.deadlocks", Kind.Sum, "{deadlock}", 1),
        ["Lock Wait Time (ms)"] = ("sqlserver.lock.wait_time", Kind.Sum, "s", 0.001),
        ["Memory Grants Pending"] = ("sqlserver.memory.grants_pending", Kind.Gauge, "{grant}", 1),
        ["Total Server Memory (KB)"] = ("sqlserver.memory.usage", Kind.UpDown, "By", 1024),
        ["Target Server Memory (KB)"] = ("sqlserver.memory.target", Kind.UpDown, "By", 1024),
        ["Transactions/sec"] = ("sqlserver.database.transactions", Kind.Sum, "{transaction}", 1),
        ["Log File(s) Size (KB)"] = ("sqlserver.database.log.size", Kind.UpDown, "By", 1024),
        ["Log File(s) Used Size (KB)"] = ("sqlserver.database.log.used", Kind.UpDown, "By", 1024),
        ["Data File(s) Size (KB)"] = ("sqlserver.database.data.size", Kind.UpDown, "By", 1024),
        ["Log Flush Waits/sec"] = ("sqlserver.database.log.flush_waits", Kind.Sum, "{wait}", 1),
    };

    private async Task CountersAsync(Server server, SqlConnection connection, DateTime now, List<MetricItem> points, CancellationToken ct)
    {
        const string sql = """
            SELECT RTRIM(object_name), RTRIM(counter_name), RTRIM(instance_name), cntr_value
            FROM sys.dm_os_performance_counters
            WHERE (object_name LIKE '%:General Statistics%' AND counter_name IN ('User Connections', 'Processes blocked'))
               OR (object_name LIKE '%:SQL Statistics%' AND counter_name IN ('Batch Requests/sec', 'SQL Compilations/sec', 'SQL Re-Compilations/sec'))
               OR (object_name LIKE '%:Buffer Manager%' AND counter_name IN ('Page life expectancy', 'Buffer cache hit ratio', 'Buffer cache hit ratio base'))
               OR (object_name LIKE '%:Locks%' AND instance_name = '_Total'
                   AND counter_name IN ('Lock Waits/sec', 'Lock Timeouts/sec', 'Number of Deadlocks/sec', 'Lock Wait Time (ms)'))
               OR (object_name LIKE '%:Memory Manager%' AND counter_name IN ('Memory Grants Pending', 'Total Server Memory (KB)', 'Target Server Memory (KB)'))
               OR (object_name LIKE '%:Databases%' AND instance_name NOT IN ('_Total', 'mssqlsystemresource', 'model', 'msdb')
                   AND counter_name IN ('Transactions/sec', 'Log File(s) Size (KB)', 'Log File(s) Used Size (KB)', 'Data File(s) Size (KB)', 'Log Flush Waits/sec'))
            """;
        double? hit = null, hitBase = null;
        await using var reader = await Command(connection, sql).ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (obj, counter, instance, value) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), (double)reader.GetInt64(3));
            if (counter.Equals("Buffer cache hit ratio", StringComparison.OrdinalIgnoreCase))
            {
                hit = value;
                continue;
            }

            if (counter.Equals("Buffer cache hit ratio base", StringComparison.OrdinalIgnoreCase))
            {
                hitBase = value;
                continue;
            }

            if (!Counters.TryGetValue(counter, out var m))
            {
                continue;
            }

            var tags = obj.EndsWith(":Databases", StringComparison.OrdinalIgnoreCase) ? new[] { ("db.namespace", (object)instance) } : Array.Empty<(string, object)>();
            Add(points, server, now, m.Metric, m.Kind, value * m.Scale, m.Unit, tags);
        }

        if (hit is { } h && hitBase is > 0)
        {
            Gauge(points, server, now, "sqlserver.page.buffer_cache.hit_ratio", h / hitBase.Value * 100, "%");
        }
    }

    private async Task WaitsAsync(Server server, SqlConnection connection, DateTime now, List<MetricItem> points, CancellationToken ct)
    {
        // Waits grouped into the categories that matter for BC (cumulative since start).
        const string sql = """
            SELECT category, SUM(wait_time_ms), SUM(waiting_tasks_count)
            FROM (SELECT CASE
                    WHEN wait_type LIKE 'LCK[_]M[_]%' THEN 'lock'
                    WHEN wait_type LIKE 'PAGEIOLATCH[_]%' THEN 'disk_io'
                    WHEN wait_type IN ('WRITELOG', 'LOGBUFFER', 'LOG_RATE_GOVERNOR') THEN 'transaction_log'
                    WHEN wait_type LIKE 'PAGELATCH[_]%' OR wait_type LIKE 'LATCH[_]%' THEN 'latch'
                    WHEN wait_type = 'ASYNC_NETWORK_IO' THEN 'network_client'
                    WHEN wait_type IN ('SOS_SCHEDULER_YIELD', 'THREADPOOL') THEN 'cpu'
                    WHEN wait_type LIKE 'CX%' THEN 'parallelism'
                    WHEN wait_type LIKE 'RESOURCE[_]SEMAPHORE%' THEN 'memory'
                  END AS category, wait_time_ms, waiting_tasks_count
                  FROM sys.dm_os_wait_stats) w
            WHERE category IS NOT NULL
            GROUP BY category
            """;
        await using var reader = await Command(connection, sql).ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var category = ("sqlserver.wait.category", (object)reader.GetString(0));
            Add(points, server, now, "sqlserver.os.wait.duration", Kind.Sum, reader.GetInt64(1) / 1000d, "s", category);
            Add(points, server, now, "sqlserver.os.wait.count", Kind.Sum, reader.GetInt64(2), "{wait}", category);
        }
    }

    private async Task FileIoAsync(Server server, SqlConnection connection, DateTime now, List<MetricItem> points, CancellationToken ct)
    {
        // Latency = rate(io.stall) / rate(operations), per database and file type (data/log).
        const string sql = """
            SELECT DB_NAME(f.database_id), m.type_desc,
                   SUM(f.num_of_reads), SUM(f.num_of_writes), SUM(f.io_stall_read_ms), SUM(f.io_stall_write_ms),
                   SUM(f.num_of_bytes_read), SUM(f.num_of_bytes_written)
            FROM sys.dm_io_virtual_file_stats(NULL, NULL) f
            JOIN sys.master_files m ON m.database_id = f.database_id AND m.file_id = f.file_id
            WHERE f.database_id NOT IN (3, 4, 32767)
            GROUP BY f.database_id, m.type_desc
            """;
        await using var reader = await Command(connection, sql).ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var db = ("db.namespace", (object)reader.GetString(0));
            var file = ("sqlserver.file.type", (object)(reader.GetString(1) == "LOG" ? "log" : "data"));
            var read = ("direction", (object)"read");
            var write = ("direction", (object)"write");
            Add(points, server, now, "sqlserver.database.operations", Kind.Sum, reader.GetInt64(2), "{operation}", db, file, read);
            Add(points, server, now, "sqlserver.database.operations", Kind.Sum, reader.GetInt64(3), "{operation}", db, file, write);
            Add(points, server, now, "sqlserver.database.io.stall", Kind.Sum, reader.GetInt64(4) / 1000d, "s", db, file, read);
            Add(points, server, now, "sqlserver.database.io.stall", Kind.Sum, reader.GetInt64(5) / 1000d, "s", db, file, write);
            Add(points, server, now, "sqlserver.database.io", Kind.Sum, reader.GetInt64(6), "By", db, file, read);
            Add(points, server, now, "sqlserver.database.io", Kind.Sum, reader.GetInt64(7), "By", db, file, write);
        }
    }

    private async Task HostAsync(Server server, SqlConnection connection, DateTime now, List<MetricItem> points, CancellationToken ct)
    {
        // CPU of the database host from the scheduler monitor (one record per minute), memory from the OS view.
        const string sql = """
            SELECT TOP (1)
                   rec.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int'),
                   rec.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int')
            FROM (SELECT CONVERT(xml, record) AS rec, [timestamp]
                  FROM sys.dm_os_ring_buffers
                  WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' AND record LIKE N'%<SystemHealth>%') r
            ORDER BY [timestamp] DESC;
            SELECT total_physical_memory_kb, available_physical_memory_kb FROM sys.dm_os_sys_memory;
            """;
        await using var reader = await Command(connection, sql).ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0) && !reader.IsDBNull(1))
        {
            var sqlCpu = reader.GetInt32(0) / 100d;
            var other = Math.Max(0, 100 - reader.GetInt32(1) - reader.GetInt32(0)) / 100d;
            Gauge(points, server, now, "sqlserver.cpu.utilization", sqlCpu, "1", ("cpu.owner", (object)"sqlserver"));
            Gauge(points, server, now, "sqlserver.cpu.utilization", other, "1", ("cpu.owner", (object)"other"));
        }

        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
        {
            var total = reader.GetInt64(0) * 1024d;
            var available = reader.GetInt64(1) * 1024d;
            Add(points, server, now, "sqlserver.host.memory.usage", Kind.UpDown, total - available, "By", ("state", (object)"used"));
            Add(points, server, now, "sqlserver.host.memory.usage", Kind.UpDown, available, "By", ("state", (object)"free"));
            Gauge(points, server, now, "sqlserver.host.memory.utilization", total > 0 ? (total - available) / total : 0, "1");
        }
    }

    // -- Blocking -----------------------------------------------------------------

    private async Task BlockingAsync(Server server, SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, r.wait_resource, DB_NAME(r.database_id),
                   s.program_name, s.host_name, SUBSTRING(t.text, 1, 4000),
                   bs.program_name, bs.host_name, SUBSTRING(bt.text, 1, 4000), r.start_time
            FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            LEFT JOIN sys.dm_exec_sessions bs ON bs.session_id = r.blocking_session_id
            LEFT JOIN sys.dm_exec_connections bc ON bc.session_id = r.blocking_session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            OUTER APPLY sys.dm_exec_sql_text(bc.most_recent_sql_handle) bt
            WHERE r.blocking_session_id > 0 AND r.wait_time >= @threshold
            """;
        var command = Command(connection, sql);
        command.Parameters.AddWithValue("@threshold", _options.BlockingThresholdSeconds * 1000);
        var current = new HashSet<(int, DateTime)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var session = reader.GetInt16(0);
            var key = ((int)session, reader.GetDateTime(12));
            current.Add(key);
            if (server.ReportedBlocks.Contains(key))
            {
                continue;
            }

            var blocker = reader.GetInt16(1);
            var waitType = Text(reader, 2);
            var seconds = reader.GetInt32(3) / 1000d;
            var resource = Text(reader, 4);
            var attributes = new List<KeyValuePair<string, object?>>
            {
                new("sqlServerSessionId", (long)session),
                new("sqlServerBlockingSessionId", (long)blocker),
                new("sqlserver.wait.type", waitType),
                new("sqlserver.wait.time_ms", (long)reader.GetInt32(3)),
                new("sqlserver.wait.resource", resource),
                new("db.namespace", Text(reader, 5)),
                new("sqlserver.client.app", Text(reader, 6)),
                new("sqlserver.client.host", Text(reader, 7)),
                new("db.query.text", Text(reader, 8)),
                new("sqlserver.blocking.client.app", Text(reader, 9)),
                new("sqlserver.blocking.client.host", Text(reader, 10)),
                new("sqlserver.blocking.query.text", Text(reader, 11)),
            };
            _emitter.EmitLog(new LogItem(server.Source!, DateTime.UtcNow, LogLevel.Warning,
                $"Session {session} blocked for {seconds:0} s by session {blocker} ({waitType}, {resource})",
                "sqlserver.blocking", null, null, _attributes.Filter(attributes.Where(a => a.Value is not null).ToList())));
        }

        server.ReportedBlocks = current;
    }

    // -- Deadlocks ----------------------------------------------------------------

    private async Task DeadlocksAsync(Server server, SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT x.e.value('@timestamp', 'datetime2'), CAST(x.e.query('(data/value/deadlock)[1]') AS nvarchar(max))
            FROM (SELECT CAST(t.target_data AS xml) AS data
                  FROM sys.dm_xe_session_targets t
                  JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
                  WHERE s.name = N'system_health' AND t.target_name = N'ring_buffer') d
            CROSS APPLY d.data.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS x(e)
            WHERE x.e.value('@timestamp', 'datetime2') > @since
            ORDER BY 1
            """;
        var command = Command(connection, sql);
        command.Parameters.AddWithValue("@since", server.DeadlockWatermark);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var time = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
            server.DeadlockWatermark = time > server.DeadlockWatermark ? time : server.DeadlockWatermark;
            if (Deadlock.Parse(reader.GetString(1)) is { } deadlock)
            {
                EmitDeadlock(server, time, deadlock);
            }
        }
    }

    private void EmitDeadlock(Server server, DateTime time, Deadlock deadlock)
    {
        var victim = deadlock.Victim;
        var message = $"Deadlock on {deadlock.Objects}: session {victim?.Spid} ({victim?.App}) was chosen as victim, {deadlock.Processes.Count} sessions involved";
        var attributes = new List<KeyValuePair<string, object?>>
        {
            new("sqlserver.deadlock.process_count", (long)deadlock.Processes.Count),
            new("sqlserver.deadlock.objects", deadlock.Objects),
            new("sqlserver.deadlock.graph", deadlock.Graph.Length > MaxGraphLength ? deadlock.Graph[..MaxGraphLength] : deadlock.Graph),
        };
        if (victim is not null)
        {
            attributes.Add(new("sqlServerSessionId", victim.Spid));
            attributes.Add(new("db.namespace", victim.Database));
            attributes.Add(new("sqlserver.client.app", victim.App));
            attributes.Add(new("sqlserver.client.host", victim.Host));
            attributes.Add(new("sqlserver.wait.resource", victim.WaitResource));
            attributes.Add(new("db.query.text", victim.Statement));
        }

        foreach (var (other, i) in deadlock.Processes.Where(p => p != victim).Select((p, i) => (p, i)))
        {
            attributes.Add(new($"sqlserver.deadlock.other.{i}.session_id", other.Spid));
            attributes.Add(new($"sqlserver.deadlock.other.{i}.client.app", other.App));
            attributes.Add(new($"sqlserver.deadlock.other.{i}.query.text", other.Statement));
        }

        var fingerprint = ErrorRegistry.Fingerprint("SqlDeadlock", deadlock.Objects, AiToOtelMapper.SpanName(victim?.Statement, null));
        attributes.Add(new("bc.error.fingerprint", fingerprint));
        var filtered = _attributes.Filter(attributes.Where(a => a.Value is not null).ToList());

        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var exception = new List<KeyValuePair<string, object?>>
        {
            new("exception.type", "SqlDeadlock"),
            new("exception.message", message),
        };
        _emitter.EmitSpan(new SpanItem(server.Source!, "Deadlock", ActivityKind.Internal, time, TimeSpan.Zero,
            traceId, spanId, null, Success: false, "SqlDeadlock", filtered,
            new[] { new SpanEvent("exception", time, exception) }));
        _emitter.EmitLog(new LogItem(server.Source!, time, LogLevel.Error, message, "sqlserver.deadlock", traceId, spanId, filtered));
        _errors?.Record(server.Source!, fingerprint, "SqlDeadlock", message, "sqlserver.deadlock", time, traceId, filtered);
    }

    /// <summary>The parts of an <c>xml_deadlock_report</c> needed for reports.</summary>
    public sealed record Deadlock(IReadOnlyList<DeadlockProcess> Processes, DeadlockProcess? Victim, string Objects, string Graph)
    {
        public static Deadlock? Parse(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }

            var root = XElement.Parse(xml);
            var victimId = root.Element("victim-list")?.Elements("victimProcess").FirstOrDefault()?.Attribute("id")?.Value;
            var processes = root.Element("process-list")?.Elements("process").Select(p => new DeadlockProcess(
                    (string?)p.Attribute("id"),
                    long.TryParse((string?)p.Attribute("spid"), out var spid) ? spid : 0,
                    (string?)p.Attribute("clientapp"),
                    (string?)p.Attribute("hostname"),
                    (string?)p.Attribute("currentdbname"),
                    (string?)p.Attribute("waitresource"),
                    Shorten(p.Element("inputbuf")?.Value.Trim())))
                .ToList() ?? new List<DeadlockProcess>();
            var objects = root.Element("resource-list")?.Elements()
                .Select(r => (string?)r.Attribute("objectname"))
                .Where(o => !string.IsNullOrEmpty(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string?>();

            return new Deadlock(processes, processes.FirstOrDefault(p => p.Id == victimId),
                objects.Count == 0 ? "unknown objects" : string.Join(", ", objects), xml);
        }

        private static string? Shorten(string? text) => text is { Length: > MaxStatementLength } ? text[..MaxStatementLength] : text;
    }

    public sealed record DeadlockProcess(string? Id, long Spid, string? App, string? Host, string? Database, string? WaitResource, string? Statement);

    // -- Helpers ------------------------------------------------------------------

    private SqlCommand Command(SqlConnection connection, string sql)
        => new(sql, connection) { CommandTimeout = Math.Max(5, _options.QueryTimeoutSeconds) };

    private static string? Text(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();

    private static void Gauge(List<MetricItem> points, Server server, DateTime now, string name, double value, string unit, params (string Key, object Value)[] tags)
        => Add(points, server, now, name, Kind.Gauge, value, unit, tags);

    private static void Add(List<MetricItem> points, Server server, DateTime now, string name, Kind kind, double value, string unit, params (string Key, object Value)[] tags)
    {
        var attributes = tags.Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray();
        var point = new AiMetricPoint(name, value, null, null, null, null);
        points.Add(kind switch
        {
            Kind.Sum => new MetricItem(server.Source!, now, point, MetricKind.Counter, attributes, unit, server.StartUtc),
            Kind.UpDown => new MetricItem(server.Source!, now, point, MetricKind.UpDownCounter, attributes, unit, server.StartUtc),
            _ => new MetricItem(server.Source!, now, point, MetricKind.Gauge, attributes, unit),
        });
    }

    private sealed class Server(string name, string connectionString)
    {
        public string Name { get; } = name;

        public string ConnectionString { get; } = connectionString;

        public TelemetrySource? Source { get; set; }

        public DateTime StartUtc { get; set; }

        public DateTime DeadlockWatermark { get; set; }

        public HashSet<(int, DateTime)> ReportedBlocks { get; set; } = new();

        public bool Failing { get; set; }
    }
}
