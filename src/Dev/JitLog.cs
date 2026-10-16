using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace MoreRolesPlus.Dev;

// 確認用: その場でコンパイルされたメソッドを集める (準備段で先にコンパイルし損ねた物を探す)。jitlog start → 操作 → jitlog stop
internal sealed class JitLog : EventListener
{
    private const long JitKeyword = 0x10;
    private const int MethodJittingStarted = 145;
    private static JitLog _on;
    private static readonly List<string> Names = new();
    private bool _ready;

    internal static string Start()
    {
        lock (Names) Names.Clear();
        _on ??= new JitLog();
        return "OK jitlog started";
    }

    internal static string Stop(int max)
    {
        var on = _on;
        _on = null;
        on?.Dispose();
        var sb = new StringBuilder();
        lock (Names)
        {
            sb.Append("OK jitlog n=").Append(Names.Count);
            for (int i = 0; i < Names.Count && i < max; i++) sb.Append('\n').Append("JIT ").Append(Names[i]);
        }
        return sb.ToString();
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != "Microsoft-Windows-DotNETRuntime") return;
        EnableEvents(source, EventLevel.Verbose, (EventKeywords)JitKeyword);
        _ready = true;
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_ready || e.EventId != MethodJittingStarted || e.Payload == null || e.Payload.Count < 6) return;
        string name = $"{e.Payload[4]}::{e.Payload[5]}";
        lock (Names) Names.Add(name);
    }
}
