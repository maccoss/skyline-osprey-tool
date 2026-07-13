#nullable enable

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using SkylineTool;

namespace OspreyTool.Skyline;

/// <summary>
/// Connection factory for Skyline's JSON-RPC pipe. Skyline closes the pipe after each request/response
/// (the connect-per-call pattern), so this class does NOT hold a pipe open - it remembers how to connect
/// and opens a fresh pipe inside each <see cref="Execute"/> call. Modeled on skyline-prism's SkylineSession.
/// </summary>
public sealed class SkylineSession : ISkylineExecutor
{
    public string PipeName { get; }
    public TimeSpan ConnectTimeout { get; }

    private SkylineSession(string pipeName, TimeSpan timeout)
    {
        PipeName = pipeName;
        ConnectTimeout = timeout;
    }

    /// <summary>
    /// Construct a session from <c>args[0]</c> - the <c>$(SkylineConnection)</c> pipe name Skyline passes
    /// external tools. Returns null if no connection argument was supplied (tool launched standalone).
    /// </summary>
    public static SkylineSession? FromArguments(string[] args, TimeSpan? timeout = null)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            return null;
        }
        // Skyline expands $(SkylineConnection) to the LEGACY ToolService pipe name; the JSON-RPC server
        // listens on a derived name - transform via GetJsonPipeName.
        var raw = args[0];
        var jsonName = raw.StartsWith(JsonToolConstants.JSON_PIPE_PREFIX, StringComparison.Ordinal)
            ? raw
            : JsonToolConstants.GetJsonPipeName(raw);
        return new SkylineSession(jsonName, timeout ?? TimeSpan.FromSeconds(5));
    }

    public T Execute<T>(Func<ISkylineClient, T> action)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        pipe.ReadMode = PipeTransmissionMode.Message;
        return action(new JsonClientAdapter(new SkylineJsonToolClient(pipe)));
    }

    public void Execute(Action<ISkylineClient> action)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        pipe.ReadMode = PipeTransmissionMode.Message;
        action(new JsonClientAdapter(new SkylineJsonToolClient(pipe)));
    }

    /// <summary>Forwards <see cref="ISkylineClient"/> calls to the concrete JSON-RPC client.</summary>
    private sealed class JsonClientAdapter : ISkylineClient
    {
        private readonly SkylineJsonToolClient _c;
        public JsonClientAdapter(SkylineJsonToolClient c) => _c = c;

        public string GetDocumentPath() => _c.GetDocumentPath();
        public string GetVersion() => _c.GetVersion();
        public string RunCommandSilent(string[] args) => _c.RunCommandSilent(args);
        public string[] GetSettingsListNames(string listType) => _c.GetSettingsListNames(listType, null) ?? Array.Empty<string>();
        public string[] GetSettingsListSelectedItems(string listType) => _c.GetSettingsListSelectedItems(listType) ?? Array.Empty<string>();
        public string? GetSettingsListItem(string listType, string itemName) => _c.GetSettingsListItem(listType, itemName);
        public string? GetSelectedElementLocator(string elementType) => _c.GetSelectedElementLocator(elementType);
        public string? GetReplicateName() => _c.GetReplicateName();

        public ReportRows? GetReport(IReadOnlyList<string> selectColumns)
        {
            var def = new ReportDefinition
            {
                Select = selectColumns.ToArray(),
                PivotReplicate = false,
                DataSource = "document_grid",
            };
            // count=0 returns the shape (TotalRows); then pull every row.
            var shape = _c.GetReportFromDefinitionRows(def, 0, 0, false, "invariant");
            var total = shape?.TotalRows ?? 0;
            var res = total > 0
                ? _c.GetReportFromDefinitionRows(def, 0, total, false, "invariant")
                : shape;
            if (res?.Columns is null || res.Rows is null)
            {
                return null;
            }
            return new ReportRows(res.Columns.Select(col => col.Name).ToArray(), res.Rows);
        }
    }
}
