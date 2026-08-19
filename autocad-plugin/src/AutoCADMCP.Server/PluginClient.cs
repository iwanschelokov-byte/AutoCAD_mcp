using System.Diagnostics;
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
///
/// Every failure to reach the plugin is classified before it is reported. The
/// socket error on its own cannot tell "AutoCAD is not running" from "AutoCAD is
/// running but the plugin was never started" from "the port is blocked" - all
/// three refuse a loopback connect in exactly the same way - and none of them
/// reads any differently from AutoCAD having crashed under the caller. Since the
/// four need four different remedies, the error code is combined with a look at
/// the process list and with what this client has already seen work.
///
/// Every one of those sentences names the plugin. That is not decoration: with
/// the plugin down a tool call degrades to isError carrying this text, and
/// build/verify-mcp-server.ps1 asserts the text is actionable by looking for the
/// word. A diagnosis that talks only about acad.exe leaves the caller without
/// the one term that identifies what is missing.
/// </summary>
public sealed class PluginClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private int _nextId;

    // Remembered so a failure can be described in terms of what changed: a
    // bridge that never worked and a bridge that stopped working need different
    // advice. "Ever connected" deliberately means "ever answered a call" - a
    // listener that accepts the socket and then does nothing is not a bridge
    // that was working.
    private volatile bool _everConnected;
    private long _lastAnswerAtTicks;

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
                $"Timed out connecting to the AutoCAD plugin at {_host}:{_port}. A connect that " +
                "hangs instead of being refused usually means a firewall is dropping loopback " +
                "packets silently." + ProcessNote());
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
        {
            throw new PluginUnavailableException(Diagnose(ex, midCall: false));
        }

        client.ReceiveTimeout = _timeoutMs;
        client.SendTimeout = _timeoutMs;

        using var stream = client.GetStream();

        string line;
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(request.ToString(Formatting.None) + "\n");
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);

            line = await ReadLineAsync(stream, ct);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
        {
            // The socket was open and died while this call was in flight. That is
            // a different event from a connect that never succeeded, and it is
            // the signature of the process at the other end going away.
            throw new PluginUnavailableException($"'{method}' failed. " + Diagnose(ex, midCall: true));
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            // A clean EOF on a socket that was working is the classic signature
            // of the process on the other end terminating.
            throw new PluginUnavailableException(
                $"'{method}' failed. " +
                Describe("the plugin closed the connection without answering", midCall: true, refused: false));
        }

        try
        {
            JObject response = JObject.Parse(line);
            Interlocked.Exchange(ref _lastAnswerAtTicks, DateTime.UtcNow.Ticks);
            _everConnected = true;
            return response;
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
                    "Timed out waiting for the AutoCAD plugin. It did not even report its own " +
                    "timeout, so AutoCAD's message loop is almost certainly blocked: a modal dialog " +
                    "(Save As, Options, Page Setup, a font or plot-style prompt) is waiting on " +
                    "screen. Application.Idle does not fire while such a dialog is open, so no call " +
                    "can be served and the plugin cannot close it. Dismiss the dialog in AutoCAD by " +
                    "hand, then call system_status to confirm the bridge is back.");
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

    /// <summary>
    /// Why the probe above came back false, in the same words a failed call
    /// would use. Lets `--check` say which of the four situations this is
    /// instead of only that the plugin is not there.
    /// </summary>
    public string DescribeUnreachable()
    {
        return Describe("nothing accepted the connection", midCall: false, refused: true);
    }

    // ------------------------------------------------------------------ //
    // Diagnosis                                                          //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Is acad.exe alive? "running", "absent", or null when the question cannot
    /// be answered (not Windows, or the process list is not readable).
    ///
    /// This is what separates "AutoCAD crashed" from "the plugin was never
    /// started": both refuse the connection in exactly the same way.
    /// </summary>
    private static string? AutoCadProcessState()
    {
        if (!OperatingSystem.IsWindows()) return null;

        Process[]? found = null;
        try
        {
            found = Process.GetProcessesByName("acad");
            return found.Length > 0 ? "running" : "absent";
        }
        catch
        {
            return null;
        }
        finally
        {
            if (found != null)
            {
                foreach (Process p in found) p.Dispose();
            }
        }
    }

    /// <summary>
    /// Every socket error in the exception tree.
    ///
    /// "localhost" resolves to both ::1 and 127.0.0.1 and the runtime tries them
    /// in turn, so a failure on every address can arrive as an AggregateException
    /// carrying one exception per address, or as a single exception holding only
    /// the last attempt's code. Collecting the whole tree means the diagnosis
    /// does not depend on which of the two the runtime chose.
    /// </summary>
    private static HashSet<SocketError> SocketErrors(Exception? ex)
    {
        var codes = new HashSet<SocketError>();

        void Walk(Exception? e, int depth)
        {
            if (e == null || depth > 6) return;
            if (e is SocketException se) codes.Add(se.SocketErrorCode);
            if (e is AggregateException agg)
            {
                foreach (Exception inner in agg.InnerExceptions) Walk(inner, depth + 1);
            }
            Walk(e.InnerException, depth + 1);
        }

        Walk(ex, 0);
        return codes;
    }

    private string LastSeen()
    {
        long ticks = Interlocked.Read(ref _lastAnswerAtTicks);
        if (ticks == 0) return "";

        var when = new DateTime(ticks, DateTimeKind.Utc);
        double age = (DateTime.UtcNow - when).TotalSeconds;
        return $" It last answered at {when.ToLocalTime():HH:mm:ss}, {age:0} s ago.";
    }

    private static string ProcessNote()
    {
        return AutoCadProcessState() switch
        {
            "absent" => " There is no acad.exe process, so AutoCAD is not running at all.",
            "running" => " acad.exe is running, so this is the plugin or the port rather than AutoCAD.",
            _ => ""
        };
    }

    private string Diagnose(Exception ex, bool midCall)
    {
        HashSet<SocketError> codes = SocketErrors(ex);

        // A refusal anywhere in the set wins: it means something answered the
        // connection attempt and said nobody is listening, which is more specific
        // than the access error the IPv6 attempt tends to raise alongside it.
        bool refused = codes.Contains(SocketError.ConnectionRefused);

        if (!midCall && !refused)
        {
            if (codes.Contains(SocketError.AccessDenied))
            {
                return $"Connecting to the AutoCAD plugin on {_host}:{_port} was DENIED " +
                       $"({ex.Message}). The port is not " +
                       "refusing the connection, it is forbidding it: a firewall or antivirus rule, " +
                       "or another process already holding the port. This is not an AutoCAD fault - " +
                       $"check with `netstat -ano | findstr {_port}` which process owns it, and " +
                       "check the firewall rules for loopback connections.";
            }

            if (codes.Contains(SocketError.NetworkUnreachable) ||
                codes.Contains(SocketError.HostUnreachable) ||
                codes.Contains(SocketError.NetworkDown) ||
                codes.Contains(SocketError.HostNotFound))
            {
                return $"The AutoCAD plugin's address {_host}:{_port} is unreachable " +
                       $"({ex.Message}). This server only ever talks to the plugin over loopback, so " +
                       "this usually means the host name resolves somewhere unexpected - set " +
                       "AUTOCAD_MCP_HOST to 127.0.0.1 explicitly.";
            }
        }

        return Describe(ex.Message, midCall, refused);
    }

    /// <summary>
    /// The half of the diagnosis that depends on the process list rather than on
    /// the error code: which of AutoCAD, the plugin, or neither is actually up.
    /// </summary>
    private string Describe(string detail, bool midCall, bool refused)
    {
        string? state = AutoCadProcessState();
        string where = $"{_host}:{_port}";

        if (midCall)
        {
            string head = $"The connection to the AutoCAD plugin on {where} was open and died in " +
                          $"the middle of a call ({detail}).";

            if (state == "absent")
            {
                return head + " acad.exe is no longer in the process list, so AUTOCAD ITSELF " +
                       "TERMINATED - this is a crash or a close, not a bridge problem." + LastSeen() +
                       " Nothing can be recovered through MCP; restart AutoCAD, reopen the drawing " +
                       "and check whether it offers to recover unsaved work.";
            }

            if (state == "running")
            {
                return head + " acad.exe is still running, so AutoCAD survived and it is THE PLUGIN " +
                       "that stopped answering - it may have been unloaded, or its listener thread " +
                       "died. Run MCPSTART in AutoCAD to bring it back." + LastSeen();
            }

            return head + " A connection that dies mid-call usually means AutoCAD terminated." +
                   LastSeen() + " Check whether AutoCAD is still on screen; if it is, run MCPSTART.";
        }

        if (state == "absent")
        {
            string crashed = _everConnected
                ? " AutoCAD was answering earlier in this session, so it has since crashed or been " +
                  "closed." + LastSeen()
                : "";

            return $"AUTOCAD IS NOT RUNNING: there is no acad.exe process, so the plugin cannot " +
                   $"be listening on {where} ({detail}).{crashed} Start AutoCAD, open a drawing, and " +
                   "run MCPSTART.";
        }

        if (state == "running" && refused)
        {
            return $"AutoCAD IS running, but nothing is listening on {where} ({detail}), so THE " +
                   "PLUGIN IS NOT UP: either it was never loaded (the bundle is not installed, or " +
                   "NETLOAD was not run), or MCPSTART has not been run in this AutoCAD session, or " +
                   "the plugin crashed while AutoCAD itself survived." +
                   (_everConnected ? LastSeen() : "") + " Run MCPSTART in AutoCAD.";
        }

        if (state == "running")
        {
            return $"AutoCAD is running but the connection to the plugin on {where} failed " +
                   $"({detail}). Run MCPSTART in AutoCAD; if it reports the listener is already " +
                   $"started, the port is being intercepted - check it with " +
                   $"`netstat -ano | findstr {_port}`.";
        }

        return $"Cannot reach the AutoCAD plugin on {where} ({detail}), and whether AutoCAD is " +
               "running could not be determined. Check that AutoCAD is open and that MCPSTART has " +
               "been run.";
    }
}

public sealed class PluginUnavailableException : Exception
{
    public PluginUnavailableException(string message) : base(message) { }
}
