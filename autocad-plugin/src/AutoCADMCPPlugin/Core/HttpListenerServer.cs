using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// HTTP shim around <see cref="JsonRpcHandler"/> so browser-based clients
    /// (e.g. internal web apps that can't speak raw TCP) can call the same
    /// 71 tools the existing TCP socket on 8081 already exposes.
    ///
    /// Wire protocol:
    ///   POST /jsonrpc        -- body: a JSON-RPC 2.0 request object.
    ///                           Response: the corresponding JSON-RPC response.
    ///   POST /                -- alias for /jsonrpc.
    ///   OPTIONS /jsonrpc     -- CORS preflight (also Chrome's
    ///                           Private-Network-Access preflight).
    ///   GET  /health         -- 200 {"ok": true} for liveness checks.
    ///
    /// CORS:
    ///   Allowed origins are taken from the env var
    ///   <c>AUTOCAD_MCP_HTTP_ORIGINS</c> (comma-separated). Default is
    ///   <c>*</c> (open) so internal tools work out of the box; production
    ///   deployments should pin to a specific origin (e.g.
    ///   <c>https://pdf.nco.ae</c>).
    ///
    ///   Chrome's Private Network Access requires
    ///   <c>Access-Control-Allow-Private-Network: true</c> on the preflight
    ///   when an https page calls a localhost server — always sent.
    /// </summary>
    public class HttpListenerServer
    {
        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
        private readonly HashSet<string> _allowedOrigins;
        private readonly bool _allowAnyOrigin;

        public int Port { get; }
        public bool IsRunning => _running;

        public HttpListenerServer(int port, string allowedOriginsCsv = null)
        {
            Port = port;

            // "*" or null/empty = allow any origin; otherwise comma-split
            // and trim. Stored as a HashSet for O(1) origin lookup.
            allowedOriginsCsv = (allowedOriginsCsv ?? "*").Trim();
            if (allowedOriginsCsv == "*" || allowedOriginsCsv.Length == 0)
            {
                _allowAnyOrigin = true;
                _allowedOrigins = new HashSet<string>();
            }
            else
            {
                _allowAnyOrigin = false;
                _allowedOrigins = new HashSet<string>(
                    allowedOriginsCsv
                        .Split(',')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Start()
        {
            if (_running) return;

            _listener = new HttpListener();
            // Bind to loopback only so the endpoint is never reachable from
            // the LAN. Loopback prefixes don't require admin to register.
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _running = true;

            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "MCP-HttpListener"
            };
            _listenerThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (HttpListenerException) when (!_running)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Listener error: {ex.Message}");
                    continue;
                }

                // Each request handled on its own thread so a slow tool call
                // (e.g. capture_screenshot) doesn't block the next request.
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                ApplyCorsHeaders(ctx);

                string method = ctx.Request.HttpMethod;
                string path = ctx.Request.Url.AbsolutePath ?? "/";

                if (method == "OPTIONS")
                {
                    // CORS preflight — no body, browser only checks headers.
                    ctx.Response.StatusCode = 204;
                    ctx.Response.OutputStream.Close();
                    return;
                }

                if (method == "GET" && path == "/health")
                {
                    WriteJson(ctx, 200, "{\"ok\":true,\"version\":\"" + Plugin.Version + "\"}");
                    return;
                }

                if (method != "POST" || (path != "/" && path != "/jsonrpc"))
                {
                    WriteJson(
                        ctx,
                        404,
                        "{\"error\":\"not found — POST /jsonrpc with a JSON-RPC 2.0 body\"}");
                    return;
                }

                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = sr.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    WriteJson(
                        ctx,
                        400,
                        "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32700,\"message\":\"Parse error: empty body\"},\"id\":null}");
                    return;
                }

                string responseJson;
                try
                {
                    // Reuse the existing JSON-RPC pipeline — same parsing,
                    // same command registry, same Application.Idle thread
                    // marshaling that the TCP path uses.
                    responseJson = JsonRpcHandler.ProcessRequest(body);
                }
                catch (Exception ex)
                {
                    Log($"JsonRpcHandler exception: {ex}");
                    string safe = ex.Message.Replace("\"", "'").Replace("\\", "\\\\");
                    responseJson =
                        "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"" +
                        safe + "\"},\"id\":null}";
                }

                if (string.IsNullOrEmpty(responseJson))
                {
                    // Notification (no id) — JSON-RPC says the server MUST NOT
                    // send a response. Return 204.
                    ctx.Response.StatusCode = 204;
                    ctx.Response.OutputStream.Close();
                    return;
                }

                WriteJson(ctx, 200, responseJson);
            }
            catch (Exception ex)
            {
                Log($"Request handler fatal: {ex.Message}");
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.OutputStream.Close();
                }
                catch { }
            }
        }

        private void ApplyCorsHeaders(HttpListenerContext ctx)
        {
            string origin = ctx.Request.Headers["Origin"];

            if (_allowAnyOrigin)
            {
                // Reflect the origin (or "*" when none was sent) so the
                // header is always something useful for the browser.
                ctx.Response.Headers["Access-Control-Allow-Origin"] =
                    string.IsNullOrEmpty(origin) ? "*" : origin;
                if (!string.IsNullOrEmpty(origin))
                {
                    // When reflecting a real origin, Vary helps caches.
                    ctx.Response.Headers["Vary"] = "Origin";
                }
            }
            else if (!string.IsNullOrEmpty(origin) && _allowedOrigins.Contains(origin))
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
                ctx.Response.Headers["Vary"] = "Origin";
            }
            // else: no Access-Control-Allow-Origin emitted → browser blocks.

            ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, GET, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] =
                "Content-Type, Authorization";
            // Chrome PNA: required when an https page calls a private-network
            // server (which 127.0.0.1 is, from the browser's perspective).
            ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            ctx.Response.Headers["Access-Control-Max-Age"] = "600";
        }

        private static void WriteJson(HttpListenerContext ctx, int statusCode, string json)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP-HTTP] {message}");
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                doc?.Editor?.WriteMessage($"\n[MCP-HTTP] {message}");
            }
            catch { }
        }
    }
}
