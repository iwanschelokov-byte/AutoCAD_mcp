using Newtonsoft.Json.Linq;

namespace AutoCADMCP.RuntimeVerify;

public static class Suites
{
    /// <summary>
    /// Tools deliberately not auto-exercised: they need real files, real
    /// plotters, or would disturb the user's session.
    /// </summary>
    public static readonly HashSet<string> SkipAlways = new(StringComparer.Ordinal)
    {
        "drawing_new", "drawing_open", "drawing_save",   // would swap the document
        "execute_command",                               // arbitrary command string
        "plot_to_pdf", "plot_layout",                    // needs a real output path
        "attach_xref", "import_block", "export_block_to_file",
        "read_external_dwg", "batch_query_dwgs",         // need real DWG paths
        "create_table_from_excel",                       // server-side, needs .xlsx
        "capture_screenshot",                            // returns a large blob
        "set_server_options",                            // would change safety posture
        "purge_drawing", "overkill", "undo_last",        // destructive on a real drawing
    };

    /// <summary>Representative tool from every category, chaining real handles.</summary>
    public static async Task CoreAsync(Runner r)
    {
        Console.WriteLine("\n[ System / introspection ]");
        var caps = await r.RunAsync("get_capabilities", "get_capabilities");
        if (caps != null)
            Console.WriteLine($"        -> {caps["tool_count"]} tools, " +
                              $"{caps["target_framework"]}, {caps["supports"]}");
        await r.RunAsync("list_methods", "list_methods");
        await r.RunAsync("system_status", "system_status");
        await r.RunAsync("get_server_options", "get_server_options");

        Console.WriteLine("\n[ Layers ]");
        await r.RunAsync("create_layer", "create_layer", new { name = "MCP_VERIFY", color = 3 });
        await r.RunAsync("list_layers", "list_layers");
        await r.RunAsync("set_layer_properties", "set_layer_properties",
                         new { name = "MCP_VERIFY", color = 5 });
        await r.RunAsync("save_layer_state", "save_layer_state",
                         new { name = "MCP_VERIFY_STATE", overwrite = true });
        await r.RunAsync("list_layer_states", "list_layer_states");
        await r.RunAsync("restore_layer_state", "restore_layer_state", new { name = "MCP_VERIFY_STATE" });

        Console.WriteLine("\n[ Entity creation ]");
        var line = await r.RunAsync("create_line", "create_line",
            new { start = new[] { 0, 0 }, end = new[] { 100, 0 }, layer = "MCP_VERIFY" });
        var circle = await r.RunAsync("create_circle", "create_circle",
            new { center = new[] { 50, 50 }, radius = 25, layer = "MCP_VERIFY" });
        await r.RunAsync("create_point", "create_point",
            new { position = new[] { 10, 10 }, layer = "MCP_VERIFY" });
        await r.RunAsync("create_polygon", "create_polygon",
            new { center = new[] { 200, 0 }, sides = 6, radius = 30, layer = "MCP_VERIFY" });
        await r.RunAsync("create_donut", "create_donut",
            new { center = new[] { 300, 0 }, outer_diameter = 40, inner_diameter = 20 });
        await r.RunAsync("create_xline", "create_xline", new { point = new[] { 0, 200 }, angle = 45 });
        var rect = await r.RunAsync("create_rectangle", "create_rectangle",
            new { corner1 = new[] { 0, 300 }, corner2 = new[] { 100, 400 }, layer = "MCP_VERIFY" });
        await r.RunAsync("create_text", "create_text",
            new { position = new[] { 0, 500 }, text = "MCP verify", height = 10 });

        Console.WriteLine("\n[ Text measurement ]");
        await r.RunAsync("measure_text", "measure_text", new { text = "MCP verify", height = 10 });
        await r.RunAsync("measure_texts", "measure_texts", new
        {
            items = new object[]
            {
                new { text = "A", height = 5 },
                new { text = "BB", height = 5 },
            },
        });

        Console.WriteLine("\n[ Query / measurement ]");
        await r.RunAsync("list_entities", "list_entities", new { layer = "MCP_VERIFY" });
        if (Id(line) is string lid)
        {
            await r.RunAsync("get_entity", "get_entity", new { id = lid });
            await r.RunAsync("get_bounding_box", "get_bounding_box", new { id = lid });
            await r.RunAsync("divide_entity", "divide_entity", new { id = lid, segments = 4 });
            await r.RunAsync("measure_entity", "measure_entity", new { id = lid, interval = 25 });
        }
        await r.RunAsync("measure_distance", "measure_distance",
            new { point1 = new[] { 0, 0 }, point2 = new[] { 3, 4 } });
        await r.RunAsync("select_by_window", "select_by_window",
            new { corner1 = new[] { -10, -10 }, corner2 = new[] { 400, 600 } });
        await r.RunAsync("entity_count_report", "entity_count_report", new { by_layer = true });
        await r.RunAsync("audit_drawing", "audit_drawing");

        Console.WriteLine("\n[ Modify ]");
        if (Id(circle) is string cid)
        {
            await r.RunAsync("move_entity", "move_entity", new { id = cid, offset = new[] { 10, 10 } });
            await r.RunAsync("copy_entity", "copy_entity", new { id = cid, offset = new[] { 0, 100 } });
            await r.RunAsync("set_entity_properties", "set_entity_properties", new { id = cid, color = 1 });
            await r.RunAsync("set_draworder", "set_draworder",
                new { ids = new[] { cid }, position = "top" });
            await r.RunAsync("flatten_entities", "flatten_entities", new { ids = new[] { cid }, z = 0 });
        }
        if (Id(rect) is string rid)
        {
            await r.RunAsync("polyline_edit", "polyline_edit", new { id = rid, closed = true });
            await r.RunAsync("reverse_polyline", "reverse_polyline", new { id = rid });
            await r.RunAsync("create_region", "create_region", new { ids = new[] { rid } });
        }

        Console.WriteLine("\n[ Fillet (real geometry) ]");
        var fa = await r.RunAsync("fillet:line_a", "create_line",
            new { start = new[] { 600, 0 }, end = new[] { 700, 0 } });
        var fb = await r.RunAsync("fillet:line_b", "create_line",
            new { start = new[] { 700, 0 }, end = new[] { 700, 100 } });
        if (Id(fa) is string faId && Id(fb) is string fbId)
            await r.RunAsync("fillet_entities", "fillet_entities",
                new { id1 = faId, id2 = fbId, radius = 20 });

        Console.WriteLine("\n[ Blocks ]");
        await r.RunAsync("list_blocks", "list_blocks");
        await r.RunAsync("count_block_references", "count_block_references");

        Console.WriteLine("\n[ Layouts / paper space ]");
        await r.RunAsync("list_layouts", "list_layouts");
        await r.RunAsync("create_layout", "create_layout", new { name = "MCP_VERIFY_LAYOUT" });
        await r.RunAsync("get_page_setup", "get_page_setup", new { layout = "MCP_VERIFY_LAYOUT" });
        await r.RunAsync("list_plot_devices", "list_plot_devices");
        await r.RunAsync("list_paper_sizes", "list_paper_sizes", new { layout = "MCP_VERIFY_LAYOUT" });
        var vp = await r.RunAsync("create_viewport", "create_viewport", new
        {
            layout = "MCP_VERIFY_LAYOUT",
            center = new[] { 420, 297 },
            width = 200,
            height = 150,
            scale = "1:100",
        });
        await r.RunAsync("list_viewports", "list_viewports", new { layout = "MCP_VERIFY_LAYOUT" });
        if (Id(vp) is string vpId)
        {
            await r.RunAsync("set_viewport_scale", "set_viewport_scale", new { id = vpId, scale = "1:50" });
            await r.RunAsync("lock_viewport", "lock_viewport", new { id = vpId, locked = true });
        }

        Console.WriteLine("\n[ 3D solids ]");
        var box = await r.RunAsync("create_box", "create_box",
            new { center = new[] { 1000, 0, 0 }, length = 50, width = 50, height = 50 });
        var sphere = await r.RunAsync("create_sphere", "create_sphere",
            new { center = new[] { 1030, 0, 0 }, radius = 30 });
        await r.RunAsync("create_cylinder", "create_cylinder",
            new { center = new[] { 1100, 0, 0 }, radius = 20, height = 60 });
        if (Id(box) is string boxId)
        {
            await r.RunAsync("get_solid_properties", "get_solid_properties", new { id = boxId });
            if (Id(sphere) is string sphereId)
                await r.RunAsync("boolean_solids", "boolean_solids",
                    new { target = boxId, others = new[] { sphereId }, operation = "union" });
        }

        Console.WriteLine("\n[ Groups / views / UCS ]");
        if (Id(line) is string gl && Id(circle) is string gc)
            await r.RunAsync("create_group", "create_group",
                new { name = "MCP_VERIFY_GROUP", ids = new[] { gl, gc } });
        await r.RunAsync("list_groups", "list_groups");
        await r.RunAsync("create_named_view", "create_named_view",
            new { name = "MCP_VERIFY_VIEW", min = new[] { 0, 0 }, max = new[] { 500, 500 } });
        await r.RunAsync("list_named_views", "list_named_views");
        await r.RunAsync("list_ucs", "list_ucs");
        await r.RunAsync("set_ucs", "set_ucs", new { name = "World" });

        Console.WriteLine("\n[ Drawing data ]");
        if (Id(line) is string xl)
        {
            await r.RunAsync("set_xdata", "set_xdata",
                new { id = xl, app_name = "MCP_VERIFY", values = new object[] { "tag", 42 } });
            await r.RunAsync("get_xdata", "get_xdata", new { id = xl, app_name = "MCP_VERIFY" });
        }
        await r.RunAsync("get_drawing_properties", "get_drawing_properties");
        await r.RunAsync("set_drawing_properties", "set_drawing_properties",
            new { comments = "Touched by AutoCAD MCP runtime verification" });

        Console.WriteLine("\n[ Xrefs ]");
        await r.RunAsync("list_xrefs", "list_xrefs");

        Console.WriteLine("\n[ Safety gate: destructive confirmation ]");
        if (Id(circle) is string eraseId)
        {
            // Being REFUSED with NeedsConfirm is the passing outcome here.
            var response = await r.RawAsync("erase_entity", new { id = eraseId });
            string? code = response["error"]?["data"]?["errorCode"]?.ToString();

            if (code == "NeedsConfirm")
                r.Note("destructive gate blocks unconfirmed erase", true, "");
            else if (response["error"] == null)
                r.Note("destructive gate", false, "erase succeeded WITHOUT __confirm");
            else
                r.Note("destructive gate", false, $"unexpected errorCode {code}");

            // And it should go through once confirmed.
            await r.RunAsync("erase_entity (confirmed)", "erase_entity",
                new Dictionary<string, object> { ["id"] = eraseId, ["__confirm"] = true });
        }

        Console.WriteLine("\n[ View ]");
        await r.RunAsync("zoom_extents", "zoom_extents");
    }

    /// <summary>Invoke every registered tool with no arguments as a crash check.</summary>
    public static async Task AllToolsAsync(Runner r)
    {
        var listing = await r.RunAsync("list_methods", "list_methods");
        if (listing?["methods"] is not JArray raw) return;

        var methods = raw.Select(m => m.ToString()).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Console.WriteLine($"\n[ Sweeping {methods.Count} registered tools (no args) ]");

        foreach (string method in methods)
        {
            if (SkipAlways.Contains(method))
            {
                r.Skip(method, "excluded from automated sweep");
                continue;
            }

            var response = await r.RawAsync(method);
            if (response["error"] is not JObject err)
            {
                r.Passed.Add(method);
                Console.WriteLine($"  PASS  {method}");
                continue;
            }

            string code = err["data"]?["errorCode"]?.ToString() ?? "?";
            string message = err["message"]?.ToString() ?? "";

            // Missing arguments / confirmation are correct behaviour for a bare call.
            if (code is "InvalidParam" or "NeedsConfirm" or "NotFound" or "Unsupported")
            {
                r.Skipped.Add(method);
                Console.WriteLine($"  ARGS  {method,-34} needs arguments ({code}) - expected");
            }
            else
            {
                r.Failed.Add((method, $"{code}: {message}"));
                Console.WriteLine($"  FAIL  {method,-34} [{code}] " +
                                  message[..Math.Min(60, message.Length)]);
            }
        }
    }

    private static string? Id(JObject? result) => result?["id"]?.ToString();
}
