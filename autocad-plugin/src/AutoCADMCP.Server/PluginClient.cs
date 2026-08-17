using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Server;

/// <summary>
/// JSON-RPC client for the AutoCAD plugin's TCP listener.
///
/// One connection per call, deliberately: the plugin handles each connection on
/// its own thread, and a fresh socket means a hung or half-read response can
/// never desynchronise later calls. The cost is a localhost connect per tool
/// call, which is negligible next to the AutoCAD round-trip.
/// </summary>
public sealed class PluginClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private int _nextId;

    public PluginClient(string host, int port, int timeoutMs = 120_000)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public string Host => _host;
    public int Port => _port;

    /// <summary>
    /// Send a method call to the plugin. Returns the raw JSON-RPC response object.
    /// Throws PluginUnavailableException when the plugin cannot be reached.
    /// </summary>
    public async Task<JObject> CallAsync(string method, JObject? parameters, CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _nextId);

        var request = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters ?? new JObject(),
            ["id"] = id
        };

        using var client = new TcpClient();

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_timeoutMs);
            await client.ConnectAsync(_host, _port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PluginUnavailableException(
                $"Timed out connecting to the AutoCAD plugin at {_host}:{_port}.");
        }
        catch (SocketException ex)
        {
            throw new PluginUnavailableException(
                $"Could not reach the AutoCAD plugin at {_host}:{_port} ({ex.SocketErrorCode}). " +
                "Make sure AutoCAD is running and MCPSTART has been issued.");
        }

        client.ReceiveTimeout = _timeoutMs;
        client.SendTimeout = _timeoutMs;

        using var stream = client.GetStream();

        byte[] payload = Encoding.UTF8.GetBytes(request.ToString(Formatting.None) + "\n");
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);

        string line = await ReadLineAsync(stream, ct);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new PluginUnavailableException(
                "The AutoCAD plugin closed the connection without responding.");
        }

        try
        {
            return JObject.Parse(line);
        }
        catch (JsonReaderException ex)
        {
            throw new PluginUnavailableException(
                $"The AutoCAD plugin returned malformed JSON: {ex.Message}");
        }
    }

    /// <summary>Read one newline-delimited message.</summary>
    private async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(_timeoutMs);

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PluginUnavailableException(
                    "Timed out waiting for the AutoCAD plugin. It may be showing a modal dialog.");
            }

            if (read == 0) break;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));

            // The plugin terminates every response with a newline.
            int nl = sb.ToString().IndexOf('\n');
            if (nl >= 0) return sb.ToString(0, nl);
        }

        return sb.ToString();
    }

    /// <summary>Cheap reachability probe used by the status tooling.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            await client.ConnectAsync(_host, _port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class PluginUnavailableException : Exception
{
    public PluginUnavailableException(string message) : base(message) { }
}
