using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BcTelemetryProxy.Configuration;
using BcTelemetryProxy.Export;
using BcTelemetryProxy.Ingestion;
using BcTelemetryProxy.Mapping;
using BcTelemetryProxy.Model;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace BcTelemetryProxy.HostTelemetry;

/// <summary>
/// Samples metrics on the proxy's own machine every <c>IntervalSeconds</c>:
/// <list type="bullet">
/// <item>server metrics under OpenTelemetry <c>system.*</c> names (host resource),</item>
/// <item>every Business Central performance counter as <c>bc.server.*</c>, one
/// resource per BC server instance (same <c>host/instance</c> as its telemetry),</item>
/// <item>BC server process metrics (<c>process.*</c>),</item>
/// <item>the proxy's own export counters (<c>bcproxy.*</c>) for alerting.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostMetricsCollector : BackgroundService
{
    private const string BcServicePrefixKey = @"SYSTEM\CurrentControlSet\Services";
    private const string BcServicePrefix = "MicrosoftDynamicsNavServer$";
    private const string BcProcessName = "Microsoft.Dynamics.Nav.Server";

    private readonly HostTelemetryOptions _options;
    private readonly IOtelEmitter _emitter;
    private readonly SourceResolver _sources;
    private readonly OtlpExporter _exporter;
    private readonly ILogger<HostMetricsCollector> _logger;
    private readonly DateTime _bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly Dictionary<string, BcInstanceCounters> _bcInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedErrors = new();
    private SystemCounters? _system;
    private LoadAverage? _load;

    public HostMetricsCollector(
        IOptions<HostTelemetryOptions> options,
        IOtelEmitter emitter,
        SourceResolver sources,
        OtlpExporter exporter,
        ILogger<HostMetricsCollector> logger)
    {
        _options = options.Value;
        _emitter = emitter;
        _sources = sources;
        _exporter = exporter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        // Do not block host startup; rate counters need a first sample as baseline.
        await Task.Yield();
        Collect(emit: false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.IntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Collect(emit: true);
        }
    }

    public override void Dispose()
    {
        _system?.Dispose();
        foreach (var instance in _bcInstances.Values)
        {
            instance.Dispose();
        }

        base.Dispose();
    }

    private void Collect(bool emit)
    {
        var now = DateTime.UtcNow;
        var host = _sources.Local(null);
        var points = new List<MetricItem>();

        Try("system", () => { if (_options.SystemMetrics) CollectSystem(host, now, points); });
        Try("bc-counters", () => { if (_options.BcCounters) CollectBcCounters(now, points); });
        Try("bc-process", () => CollectBcProcesses(host, now, points));
        CollectProxy(host, now, points);

        if (emit)
        {
            foreach (var point in points)
            {
                _emitter.EmitMetric(point);
            }
        }
    }

    // -- System ---------------------------------------------------------------

    private void CollectSystem(TelemetrySource host, DateTime now, List<MetricItem> points)
    {
        _system ??= new SystemCounters();

        Gauge(points, host, now, "system.cpu.utilization", _system.Cpu.NextValue() / 100d, "1");
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Cumulative CPU seconds summed over all logical processors (hostmetrics shape).
            var idleSeconds = idle.Seconds;
            Sum(points, host, now, _bootUtc, "system.cpu.time", user.Seconds, "s", ("cpu.mode", "user"), ("state", "user"));
            Sum(points, host, now, _bootUtc, "system.cpu.time", kernel.Seconds - idleSeconds, "s", ("cpu.mode", "system"), ("state", "system"));
            Sum(points, host, now, _bootUtc, "system.cpu.time", idleSeconds, "s", ("cpu.mode", "idle"), ("state", "idle"));
        }

        Gauge(points, host, now, "system.cpu.logical.count", Environment.ProcessorCount, "{cpu}");

        // Windows has no load average; approximate it like Linux does (EWMA of
        // runnable threads = processor queue + busy cores) so host views can show it.
        var runnable = _system.QueueLength.NextValue() + (_system.Cpu.NextValue() / 100d * Environment.ProcessorCount);
        _load ??= new LoadAverage(runnable);
        _load.Update(runnable, now);
        Gauge(points, host, now, "system.cpu.load_average.1m", _load.One, "{thread}");
        Gauge(points, host, now, "system.cpu.load_average.5m", _load.Five, "{thread}");
        Gauge(points, host, now, "system.cpu.load_average.15m", _load.Fifteen, "{thread}");

        var total = (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var available = (double)_system.AvailableBytes.NextValue();
        UpDown(points, host, now, _bootUtc, "system.memory.usage", total - available, "By", ("system.memory.state", "used"), ("state", "used"));
        UpDown(points, host, now, _bootUtc, "system.memory.usage", available, "By", ("system.memory.state", "free"), ("state", "free"));
        Gauge(points, host, now, "system.memory.utilization", total > 0 ? (total - available) / total : 0, "1", ("system.memory.state", "used"), ("state", "used"));
        Gauge(points, host, now, "system.memory.utilization", total > 0 ? available / total : 0, "1", ("system.memory.state", "free"), ("state", "free"));

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var used = (double)(drive.TotalSize - drive.TotalFreeSpace);
            var tags = new (string, object)[] { ("system.device", drive.Name), ("system.filesystem.mountpoint", drive.Name), ("system.filesystem.type", drive.DriveFormat) };
            UpDown(points, host, now, _bootUtc, "system.filesystem.usage", used, "By", tags.Append(("system.filesystem.state", "used")).Append(("state", "used")).ToArray());
            UpDown(points, host, now, _bootUtc, "system.filesystem.usage", drive.TotalFreeSpace, "By", tags.Append(("system.filesystem.state", "free")).Append(("state", "free")).ToArray());
            Gauge(points, host, now, "system.filesystem.utilization", drive.TotalSize > 0 ? used / drive.TotalSize : 0, "1", tags);
        }

        Gauge(points, host, now, "system.disk.utilization", 1 - (_system.DiskIdle.NextValue() / 100d), "1", ("system.device", "_Total"));
        UpDown(points, host, now, _bootUtc, "system.disk.pending_operations", _system.DiskQueue.NextValue(), "{operation}", ("system.device", "_Total"));
        Gauge(points, host, now, "system.disk.throughput", _system.DiskRead.NextValue(), "By/s", ("disk.io.direction", "read"));
        Gauge(points, host, now, "system.disk.throughput", _system.DiskWrite.NextValue(), "By/s", ("disk.io.direction", "write"));

        // Raw values of the Windows rate counters are cumulative totals since boot
        // (bytes, operations, 100 ns busy ticks) - exactly the hostmetrics sums.
        foreach (var (direction, bytes, ops, time) in new[]
                 {
                     ("read", _system.DiskRead, _system.DiskReads, _system.DiskReadTime),
                     ("write", _system.DiskWrite, _system.DiskWrites, _system.DiskWriteTime),
                 })
        {
            var tags = new (string, object)[] { ("device", "_Total"), ("system.device", "_Total"), ("direction", direction), ("disk.io.direction", direction) };
            Sum(points, host, now, _bootUtc, "system.disk.io", bytes.RawValue, "By", tags);
            Sum(points, host, now, _bootUtc, "system.disk.operations", ops.RawValue, "{operation}", tags);
            Sum(points, host, now, _bootUtc, "system.disk.operation_time", time.RawValue / 1e7, "s", tags);
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                     n.OperationalStatus == OperationalStatus.Up &&
                     n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        {
            var stats = nic.GetIPStatistics();
            foreach (var (direction, bytes, packets, errors, dropped) in new[]
                     {
                         ("receive", stats.BytesReceived, stats.UnicastPacketsReceived + stats.NonUnicastPacketsReceived, stats.IncomingPacketsWithErrors, stats.IncomingPacketsDiscarded),
                         ("transmit", stats.BytesSent, stats.UnicastPacketsSent + stats.NonUnicastPacketsSent, stats.OutgoingPacketsWithErrors, stats.OutgoingPacketsDiscarded),
                     })
            {
                // Current semantic-convention keys plus the hostmetrics keys host views group by.
                var tags = new (string, object)[] { ("network.interface.name", nic.Name), ("device", nic.Name), ("network.io.direction", direction), ("direction", direction) };
                Sum(points, host, now, _bootUtc, "system.network.io", bytes, "By", tags);
                Sum(points, host, now, _bootUtc, "system.network.packets", packets, "{packet}", tags);
                Sum(points, host, now, _bootUtc, "system.network.errors", errors, "{error}", tags);
                Sum(points, host, now, _bootUtc, "system.network.dropped", dropped, "{packet}", tags);
            }
        }

        var ip = IPGlobalProperties.GetIPGlobalProperties();
        var connections = ip.GetActiveTcpConnections()
            .GroupBy(c => TcpStateName(c.State))
            .Select(g => (State: g.Key, Count: g.Count()))
            .Append(("LISTEN", ip.GetActiveTcpListeners().Length));
        foreach (var (state, count) in connections)
        {
            UpDown(points, host, now, _bootUtc, "system.network.connections", count, "{connection}",
                ("protocol", "tcp"), ("network.transport", "tcp"), ("state", state), ("network.connection.state", state.ToLowerInvariant()));
        }

        Gauge(points, host, now, "system.uptime", Environment.TickCount64 / 1000d, "s");
    }

    // -- Business Central performance counters ---------------------------------

    private void CollectBcCounters(DateTime now, List<MetricItem> points)
    {
        if (!PerformanceCounterCategory.Exists(_options.BcCounterCategory))
        {
            return;
        }

        var category = new PerformanceCounterCategory(_options.BcCounterCategory);
        var live = category.GetInstanceNames().Where(i => i != "_Total").ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var gone in _bcInstances.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _bcInstances[gone].Dispose();
            _bcInstances.Remove(gone);
        }

        var names = BcInstanceNames();
        foreach (var instance in live)
        {
            if (!_bcInstances.TryGetValue(instance, out var counters))
            {
                // New instance: create and prime; it reports from the next tick on.
                var display = names.FirstOrDefault(n => n.Equals(instance, StringComparison.OrdinalIgnoreCase)) ?? instance;
                _bcInstances[instance] = new BcInstanceCounters(display, category.GetCounters(instance));
                continue;
            }

            var source = _sources.Local(counters.DisplayName);
            foreach (var c in counters.Counters)
            {
                points.Add(new MetricItem(source, now, new AiMetricPoint(c.Name, c.Counter.NextValue(), null, null, null, null),
                    MetricKind.Gauge, Array.Empty<KeyValuePair<string, object?>>(), c.Unit, Description: c.Description));
            }
        }
    }

    /// <summary>Correctly cased BC server instance names from the Windows services.</summary>
    private static List<string> BcInstanceNames()
    {
        using var services = Registry.LocalMachine.OpenSubKey(BcServicePrefixKey);
        return services?.GetSubKeyNames()
            .Where(n => n.StartsWith(BcServicePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n[BcServicePrefix.Length..])
            .ToList() ?? new List<string>();
    }

    // -- BC server process ----------------------------------------------------

    private void CollectBcProcesses(TelemetrySource host, DateTime now, List<MetricItem> points)
    {
        var processes = Process.GetProcessesByName(BcProcessName);
        try
        {
            // With a single BC instance the process belongs to it; otherwise the
            // processes stay on the host resource, told apart by process.pid.
            var single = processes.Length == 1 && _bcInstances.Count == 1
                ? _sources.Local(_bcInstances.Values.First().DisplayName)
                : null;

            foreach (var p in processes)
            {
                var source = single ?? host;
                var pid = ("process.pid", (object)(long)p.Id);
                var started = p.StartTime.ToUniversalTime();
                UpDown(points, source, now, started, "process.memory.usage", p.WorkingSet64, "By", pid);
                UpDown(points, source, now, started, "process.memory.virtual", p.PrivateMemorySize64, "By", pid);
                Sum(points, source, now, started, "process.cpu.time", p.TotalProcessorTime.TotalSeconds, "s", pid);
                Gauge(points, source, now, "process.thread.count", p.Threads.Count, "{thread}", pid);
                Gauge(points, source, now, "process.handle.count", p.HandleCount, "{handle}", pid);
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    // -- Proxy self-monitoring -------------------------------------------------

    private void CollectProxy(TelemetrySource host, DateTime now, List<MetricItem> points)
    {
        var s = _exporter.Statistics;
        Gauge(points, host, now, "bcproxy.queue.length", _exporter.QueueLength, "{item}");
        Gauge(points, host, now, "bcproxy.export.degraded", s.IsDegraded ? 1 : 0, "1");
        foreach (var (outcome, value) in new[]
                 {
                     ("exported_logs", Interlocked.Read(ref s.ExportedLogs)),
                     ("exported_spans", Interlocked.Read(ref s.ExportedSpans)),
                     ("exported_metrics", Interlocked.Read(ref s.ExportedMetrics)),
                     ("failed", Interlocked.Read(ref s.Failed)),
                     ("dropped", Interlocked.Read(ref s.Dropped)),
                     ("rejected", Interlocked.Read(ref s.Rejected)),
                 })
        {
            Sum(points, host, now, _startUtc, "bcproxy.items", value, "{item}", ("bcproxy.outcome", outcome));
        }
    }

    // -- Helpers ----------------------------------------------------------------

    private static void Gauge(List<MetricItem> points, TelemetrySource source, DateTime now, string name, double value, string? unit, params (string Key, object Value)[] tags)
        => points.Add(new MetricItem(source, now, new AiMetricPoint(name, value, null, null, null, null), MetricKind.Gauge, Tags(tags), unit));

    /// <summary>UpDownCounter per semantic conventions (memory, file system, connections, queue): non-monotonic cumulative sum.</summary>
    private static void UpDown(List<MetricItem> points, TelemetrySource source, DateTime now, DateTime start, string name, double value, string? unit, params (string Key, object Value)[] tags)
        => points.Add(new MetricItem(source, now, new AiMetricPoint(name, value, null, null, null, null), MetricKind.UpDownCounter, Tags(tags), unit, start));

    private static void Sum(List<MetricItem> points, TelemetrySource source, DateTime now, DateTime start, string name, double value, string? unit, params (string Key, object Value)[] tags)
        => points.Add(new MetricItem(source, now, new AiMetricPoint(name, value, null, null, null, null), MetricKind.Counter, Tags(tags), unit, start));

    private static KeyValuePair<string, object?>[] Tags((string Key, object Value)[] tags)
        => tags.Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray();

    private void Try(string area, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException)
        {
            // Report each failing area once; keep collecting the others.
            if (_reportedErrors.Add(area))
            {
                _logger.LogWarning(ex, "Host telemetry: collecting {Area} failed", area);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        /// <summary>FILETIME counts 100 ns ticks.</summary>
        public double Seconds => (((ulong)_high << 32) | _low) / 1e7;
    }

    private static string TcpStateName(TcpState state) => state switch
    {
        TcpState.Established => "ESTABLISHED",
        TcpState.SynSent => "SYN_SENT",
        TcpState.SynReceived => "SYN_RECV",
        TcpState.FinWait1 => "FIN_WAIT_1",
        TcpState.FinWait2 => "FIN_WAIT_2",
        TcpState.TimeWait => "TIME_WAIT",
        TcpState.CloseWait => "CLOSE_WAIT",
        TcpState.LastAck => "LAST_ACK",
        TcpState.Closing => "CLOSING",
        TcpState.Listen => "LISTEN",
        _ => "CLOSE",
    };

    private sealed class SystemCounters : IDisposable
    {
        public PerformanceCounter Cpu { get; } = new("Processor", "% Processor Time", "_Total");
        public PerformanceCounter QueueLength { get; } = new("System", "Processor Queue Length");
        public PerformanceCounter AvailableBytes { get; } = new("Memory", "Available Bytes");
        public PerformanceCounter DiskIdle { get; } = new("PhysicalDisk", "% Idle Time", "_Total");
        public PerformanceCounter DiskQueue { get; } = new("PhysicalDisk", "Current Disk Queue Length", "_Total");
        public PerformanceCounter DiskRead { get; } = new("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        public PerformanceCounter DiskWrite { get; } = new("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        public PerformanceCounter DiskReads { get; } = new("PhysicalDisk", "Disk Reads/sec", "_Total");
        public PerformanceCounter DiskWrites { get; } = new("PhysicalDisk", "Disk Writes/sec", "_Total");
        public PerformanceCounter DiskReadTime { get; } = new("PhysicalDisk", "% Disk Read Time", "_Total");
        public PerformanceCounter DiskWriteTime { get; } = new("PhysicalDisk", "% Disk Write Time", "_Total");

        public void Dispose()
        {
            foreach (var c in new[] { Cpu, QueueLength, AvailableBytes, DiskIdle, DiskQueue, DiskRead, DiskWrite, DiskReads, DiskWrites, DiskReadTime, DiskWriteTime })
            {
                c.Dispose();
            }
        }
    }

    /// <summary>Linux-style exponentially damped load averages over 1, 5 and 15 minutes.</summary>
    private sealed class LoadAverage
    {
        private DateTime _last = DateTime.UtcNow;

        public LoadAverage(double initial) => One = Five = Fifteen = initial;

        public double One { get; private set; }

        public double Five { get; private set; }

        public double Fifteen { get; private set; }

        public void Update(double runnable, DateTime now)
        {
            var seconds = Math.Max(1, (now - _last).TotalSeconds);
            _last = now;
            One = Damp(One, runnable, seconds, 60);
            Five = Damp(Five, runnable, seconds, 300);
            Fifteen = Damp(Fifteen, runnable, seconds, 900);
        }

        private static double Damp(double load, double runnable, double seconds, double window)
        {
            var factor = Math.Exp(-seconds / window);
            return (load * factor) + (runnable * (1 - factor));
        }
    }

    private sealed record BcCounter(PerformanceCounter Counter, string Name, string? Unit, string Description);

    private sealed class BcInstanceCounters : IDisposable
    {
        public BcInstanceCounters(string displayName, PerformanceCounter[] counters)
        {
            DisplayName = displayName;
            var kept = new List<BcCounter>();
            foreach (var c in counters)
            {
                if (BcCounterNames.IsBaseCounter(c.CounterType))
                {
                    c.Dispose();
                    continue;
                }

                var (name, nameUnit) = BcCounterNames.MetricName(c.CounterName);
                var help = string.Join(' ', c.CounterHelp.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                kept.Add(new BcCounter(c, name, BcCounterNames.Unit(c.CounterType, nameUnit), $"{c.CounterName}: {help}"));
                c.NextValue();
            }

            Counters = kept;
        }

        public string DisplayName { get; }

        public IReadOnlyList<BcCounter> Counters { get; }

        public void Dispose()
        {
            foreach (var c in Counters)
            {
                c.Counter.Dispose();
            }
        }
    }
}
