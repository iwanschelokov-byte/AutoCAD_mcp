using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AutoCADMCPPlugin.Core.Plugin))]
[assembly: CommandClass(typeof(AutoCADMCPPlugin.Core.Plugin))]

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Main entry point for the AutoCAD MCP Plugin.
    /// Implements IExtensionApplication for automatic loading.
    /// Exposes MCPSTART / MCPSTOP commands for manual control.
    ///
    /// Two transports are exposed simultaneously:
    /// - TCP socket on <see cref="DefaultPort"/> (default 8081) for the
    ///   Python MCP server / Claude integration (newline-delimited JSON-RPC).
    /// - HTTP loopback on <see cref="DefaultHttpPort"/> (default 8082) for
    ///   browser apps (POST /jsonrpc, with CORS + Chrome Private-Network-Access).
    ///
    /// The HTTP listener is opt-out: set the env var
    /// <c>AUTOCAD_MCP_HTTP_PORT=0</c> to disable it.
    /// </summary>
    public class Plugin : IExtensionApplication
    {
        private static SocketServer _socketServer;
        private static HttpListenerServer _httpServer;
        private static readonly object _lock = new object();

        public const int DefaultPort = 8081;
        public const int DefaultHttpPort = 8082;
        public const string PluginName = "AutoCAD MCP Plugin";

        // Upstream feature version.
        public const string Version = "1.3.0";

        // Custom build tag for this fork. Bump this when you rebuild your own
        // version so `system_status` and the load message identify it clearly.
        // This build adds AutoCAD 2027 (.NET 10 / R26.0) support, an optional
        // "inputs" array for execute_command (interactive command + all of its
        // prompt responses sent as one string), document close/list commands,
        // command-line diagnostics (read_command_line), crossing selection,
        // robust entity type matching and paged entity listings. It also fixes
        // create_block applying the base point twice (inserts landed at
        // "insertion point - base point").
        //
        // 2027.5-custom additionally rewrites plot_to_pdf on the PlottingServices
        // publish engine (the old one sent "._-EXPORTPDF", which is not a
        // command-line command, and reported success without writing a file),
        // adds plot_devices for canonical media names, returns entity handles in
        // hexadecimal so they match the properties palette and (handent "..."),
        // and makes drawing_close report whether anything was actually discarded.
        //
        // 2027.6-custom breaks the paper=auto tie between the portrait and the
        // landscape copy of a sheet in favour of the one that needs no rotation,
        // and makes plot_devices echo the arguments it received and fall back to
        // the default PDF driver when none is named.
        //
        // 2027.7-custom takes plot_devices out of application context: reading a
        // device's media list has to apply every sheet to a PlotSettings before
        // it can read the size back, which walks the same machinery a real plot
        // does, so it now runs under a document lock and refuses to start while
        // a plot is in progress. Both plotting commands also accept "plotter" as
        // a synonym for "device" in the plugin itself, and say so when neither
        // arrives.
        //
        // 2027.8-custom fixes a crash in that change. plot_devices listed the
        // devices and style tables before checking that a drawing was open, on
        // the assumption that a plain list of driver names could not need one.
        // It does: those lists come from PlotSettingsValidator.Current, which is
        // current *for the active document*, and calling into it with no
        // document faults in unmanaged code and terminates AutoCAD - not an
        // exception, so no managed catch can intercept it. The active-document
        // check is now the first statement of the command, and the whole read
        // runs under one document lock.
        public const string Build = "2027.8-custom";

        public void Initialize()
        {
            // Start recording command activity immediately: execute_command is
            // asynchronous, so this log is the only way to report back what a
            // queued command actually did.
            CommandTracker.Install();

            Editor ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\n[MCP] {PluginName} v{Version} (build {Build}) loaded.");
            ed?.WriteMessage("\n[MCP] Use MCPSTART to start the server, MCPSTOP to stop it.");
        }

        public void Terminate()
        {
            CommandTracker.Uninstall();
            StopServers();
        }

        [CommandMethod("MCPSTART")]
        public static void StartCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            lock (_lock)
            {
                if (_socketServer != null && _socketServer.IsRunning)
                {
                    ed?.WriteMessage(
                        $"\n[MCP] Server already running on port {_socketServer.Port}.");
                    return;
                }

                int port = DefaultPort;

                // Allow user to specify a custom TCP port. HTTP port stays
                // env-driven (most users won't ever change it).
                PromptIntegerOptions opts = new PromptIntegerOptions("\n[MCP] Enter port number")
                {
                    DefaultValue = DefaultPort,
                    AllowNone = true,
                    AllowZero = false,
                    AllowNegative = false,
                    LowerLimit = 1024,
                    UpperLimit = 65535
                };

                PromptIntegerResult result = ed?.GetInteger(opts);
                if (result != null && result.Status == PromptStatus.OK)
                    port = result.Value;

                try
                {
                    _socketServer = new SocketServer(port);
                    _socketServer.Start();
                    ed?.WriteMessage($"\n[MCP] TCP server started on localhost:{port}");
                }
                catch (System.Exception ex)
                {
                    ed?.WriteMessage($"\n[MCP] Failed to start TCP server: {ex.Message}");
                }

                // Start HTTP shim if not explicitly disabled.
                int httpPort = ResolveHttpPort();
                if (httpPort <= 0)
                {
                    ed?.WriteMessage(
                        "\n[MCP] HTTP shim disabled (AUTOCAD_MCP_HTTP_PORT=0).");
                }
                else
                {
                    string allowedOrigins =
                        Environment.GetEnvironmentVariable("AUTOCAD_MCP_HTTP_ORIGINS");
                    try
                    {
                        _httpServer = new HttpListenerServer(httpPort, allowedOrigins);
                        _httpServer.Start();
                        ed?.WriteMessage(
                            $"\n[MCP] HTTP shim started on http://127.0.0.1:{httpPort}/jsonrpc");
                        if (!string.IsNullOrEmpty(allowedOrigins) && allowedOrigins != "*")
                        {
                            ed?.WriteMessage(
                                $"\n[MCP] HTTP allowed origins: {allowedOrigins}");
                        }
                        else
                        {
                            ed?.WriteMessage(
                                "\n[MCP] HTTP allowed origins: * (open) — set AUTOCAD_MCP_HTTP_ORIGINS to restrict.");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ed?.WriteMessage(
                            $"\n[MCP] Failed to start HTTP shim: {ex.Message}");
                    }
                }
            }
        }

        [CommandMethod("MCPSTOP")]
        public static void StopCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            lock (_lock)
            {
                bool any =
                    (_socketServer != null && _socketServer.IsRunning) ||
                    (_httpServer != null && _httpServer.IsRunning);
                if (!any)
                {
                    ed?.WriteMessage("\n[MCP] Server is not running.");
                    return;
                }

                StopServers();
                ed?.WriteMessage("\n[MCP] Server stopped.");
            }
        }

        [CommandMethod("MCPSTATUS")]
        public static void StatusCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (_socketServer != null && _socketServer.IsRunning)
            {
                ed?.WriteMessage(
                    $"\n[MCP] TCP server running on localhost:{_socketServer.Port}");
                ed?.WriteMessage(
                    $"\n[MCP] Active TCP connections: {_socketServer.ActiveConnections}");
            }
            else
            {
                ed?.WriteMessage("\n[MCP] TCP server is not running.");
            }
            if (_httpServer != null && _httpServer.IsRunning)
            {
                ed?.WriteMessage(
                    $"\n[MCP] HTTP shim running on http://127.0.0.1:{_httpServer.Port}/jsonrpc");
            }
            else
            {
                ed?.WriteMessage("\n[MCP] HTTP shim is not running.");
            }
        }

        private static int ResolveHttpPort()
        {
            string raw = Environment.GetEnvironmentVariable("AUTOCAD_MCP_HTTP_PORT");
            if (string.IsNullOrWhiteSpace(raw)) return DefaultHttpPort;
            if (int.TryParse(raw, out int p)) return p;
            return DefaultHttpPort;
        }

        private static void StopServers()
        {
            try
            {
                _socketServer?.Stop();
                _socketServer = null;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MCP] Error stopping TCP server: {ex.Message}");
            }
            try
            {
                _httpServer?.Stop();
                _httpServer = null;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MCP] Error stopping HTTP server: {ex.Message}");
            }
        }
    }
}
