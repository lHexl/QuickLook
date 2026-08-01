// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.

using QuickLook.Common.Plugin;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Common.Helpers;

public static class PreviewPerformanceLogger
{
    private sealed class TraceState
    {
        public int Id { get; set; }
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
    }

    private static readonly ConditionalWeakTable<ContextObject, TraceState> Traces = new();
    private static readonly ConcurrentQueue<string> PendingLines = new();
    private static int _nextTraceId;
    private static int _writerScheduled;

    public static void Begin(ContextObject context, string path, string plugin)
    {
        if (context == null) return;

        Traces.Remove(context);
        var trace = new TraceState { Id = Interlocked.Increment(ref _nextTraceId) };
        Traces.Add(context, trace);
        Enqueue(trace, "BEGIN", $"path={path}; plugin={plugin}; process={Process.GetCurrentProcess().Id}; os={Environment.OSVersion}");
    }

    public static void Mark(ContextObject context, string stage, string details = null)
    {
        if (context != null && Traces.TryGetValue(context, out var trace))
            Enqueue(trace, stage, details);
        else
            WriteGlobal(stage, details);
    }

    public static void WriteGlobal(string stage, string details = null)
    {
        PendingLines.Enqueue(
            $"{DateTime.Now:O} [PreviewPerf] trace=- elapsed=- thread={Environment.CurrentManagedThreadId} stage={stage}" +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $"; {details}"));
        ScheduleWriter();
    }

    private static void Enqueue(TraceState trace, string stage, string details)
    {
        PendingLines.Enqueue(
            $"{DateTime.Now:O} [PreviewPerf] trace={trace.Id} elapsed={trace.Stopwatch.Elapsed.TotalMilliseconds:F3}ms " +
            $"thread={Environment.CurrentManagedThreadId} stage={stage}" +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $"; {details}"));
        ScheduleWriter();
    }

    private static void ScheduleWriter()
    {
        if (Interlocked.CompareExchange(ref _writerScheduled, 1, 0) == 0)
            _ = Task.Run(DrainQueue);
    }

    private static void DrainQueue()
    {
        try
        {
            var logPath = Path.Combine(SettingHelper.LocalDataPath, "QuickLook.Performance.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));

            using var writer = new StreamWriter(
                new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
            while (PendingLines.TryDequeue(out var line))
                writer.WriteLine(line);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        finally
        {
            Interlocked.Exchange(ref _writerScheduled, 0);
            if (!PendingLines.IsEmpty)
                ScheduleWriter();
        }
    }
}
