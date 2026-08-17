# Changelog

All notable changes to the AutoCAD MCP plugin.

## [2.0.0] — 2026-08-17

The upgrade pass described in `UPGRADE_PLAN.md`: tool coverage more than doubled,
a real safety layer, typed errors, and AutoCAD 2021→2027 version coverage.

### Added — safety and introspection foundation

- **Typed error codes.** Every failure now carries `error.data.errorCode`
  (`NotFound`, `InvalidParam`, `ReadOnly`, `NoDocument`, `NeedsConfirm`,
  `Unsupported`, `TxnFailed`, `Timeout`, `Internal`) so clients can branch on the
  failure kind instead of matching error text.
- **Read-only mode.** `set_server_options({"read_only": true})` refuses every
  drawing-modifying tool — safe inspection of a live drawing.
- **Destructive confirmation, on by default.** The 10 destructive tools
  (`erase_entity`, `bulk_erase`, `delete_layer`, `delete_layout`,
  `delete_layer_state`, `delete_block_definition`, `purge_drawing`,
  `detach_xref`, `overkill`, `ungroup`) require `"__confirm": true`.
- **`DirectCommand` split.** `list_methods`, `get_capabilities`,
  `get_server_options` and `set_server_options` execute on the socket thread
  instead of being marshaled, so tool discovery still answers while AutoCAD is
  busy or showing a modal dialog. Previously a modal dialog blocked *everything*
  until the 30 s timeout.
- **`CommandClassifier`.** Read/write/destructive is inferred from the method
  name, with per-command override — no annotation needed on 179 classes.
- **Audit log.** Append-only JSONL at `%APPDATA%\AutoCADMCP\activity.jsonl`,
  carrying user / machine / drawing / duration / errorCode on every call.
- **Persisted settings** at `%APPDATA%\AutoCADMCP\settings.json`.
- **`get_capabilities`** reports build target, supported AutoCAD range, tool
  counts and the live destructive-tool list.
- **Forgiving parameter aliases.** `EntityHelper.Arg()` accepts
  `id | entity_id | handle | object_id`, `position | point | location`, etc.
- **Hex handle support.** `EntityHelper.ResolveHandle()` accepts both the decimal
  handles this plugin emits and the hexadecimal form AutoCAD's own UI displays.

### Added — C# MCP server (Phase 2)

- **`AutoCADMCP.Server`** — a hand-rolled MCP server over stdio, published as a
  single self-contained ~65 MB exe. No Python needed on the end user's machine.
- **Per-tool JSON schemas are generated, not hand-written.**
  `build/generate_tool_schemas.py` parses the Python server's typed signatures
  into `tools.json` (186 tools, 571 parameters, 239 required), embedded at build
  time. The two servers therefore cannot drift, and CI fails if it goes stale.
- Graceful degradation: with AutoCAD down, `tools/call` returns an MCP tool error
  with an actionable message instead of failing the protocol.
- The Python server is unchanged and still fully supported.

### Added — 102 new tools (73 → 186 after merging PR #4)

- **Layouts & paper space (15)** — layout CRUD, page setup (device, media, plot
  type, scale, rotation, CTB), plot device/media discovery, paper-space viewport
  creation with scale and lock, plot to PDF per layout.
- **External references (9)** — attach/overlay, reload, unload, detach, bind,
  repath, plus `read_external_dwg` and `batch_query_dwgs` which inspect DWG files
  through a **side database without opening them** (folder-wide audits).
- **Block attributes & dynamic blocks (10)** — read/write attributes by tag,
  ATTSYNC-equivalent, dynamic block parameters with allowed values, WBLOCK
  export, per-block insert counts.
- **Modify operations (11)** — break, fillet (real tangent-arc geometry),
  polyline editing, draw order, flatten, divide/measure marker placement,
  region and boundary creation, duplicate removal.
- **2D entities & 3D solids (16)** — point, xline, ray, polygon, donut, 3D
  polyline; box, sphere, cylinder, cone, wedge, torus; extrude, revolve, boolean
  operations and mass properties.
- **Groups, layer states, views, UCS (13)**.
- **Drawing data & audit (6)** — XData read/write, drawing properties, entity
  count report, and a drawing health audit.
- **Annotation completion (18)** — multileaders and styles, ordinate and
  arc-length dimensions, GD&T tolerance frames, dimension text override,
  dimension regeneration, annotation scaling (CANNOSCALE + per-entity
  representations), table read/write/merge, in-place text editing, wipeouts and
  revision clouds.
- **Sheet sets (4, read-only)** — `get_sheet_set_status`, `open_sheet_set`,
  `list_sheets`, `close_sheet_set`. Sheet sets have no managed .NET API, so these
  use the `AcSmComponents` COM library via late binding and degrade to
  `Unsupported` when it is unavailable. **Not verified against a live Sheet Set
  Manager.**
- **`measure_text` / `measure_texts`** — these existed in the plugin but had
  never been exposed as MCP tools, so no AI client could reach them.

### Changed — version coverage

- **AutoCAD 2021 support added.** The `net48` leg now compiles against AutoCAD
  2021 (`AutoCAD.NET 24.0.x`) reference assemblies — the oldest in its ABI family
  — so one binary covers 2021–2024. Bundle `SeriesMin` lowered `R24.1` → `R24.0`.
- **AutoCAD 2027 support added.** New opt-in `net10.0-windows` target
  (`AutoCAD.NET 26.0.x`, .NET 10) with its own `ComponentEntry` at `R26.0`.
  Build with `-p:IncludeNet10=true`; requires the .NET 10 SDK.
- Bundle range is now `R24.0`–`R26.0` (AutoCAD 2021 through 2027).
- **`Core/AcadCompat.cs`** added as the single place version drift is isolated,
  so the other ~100 source files stay identical across all three targets.
- All command classes migrated from `ICommand` to the `AcadCommand` base class.

### Fixed

- `measure_entity` was classified read-only by its `measure_` prefix but places
  markers in the drawing; now correctly flagged as a write.
- `README.md` claimed commands were auto-discovered by reflection. They are not —
  registration in `CommandRegistry` is explicit and required.
- `autocad-plugin/README.md` was stale (claimed 33 tools, referenced a
  `SearchCommands.cs` that does not exist).
- Solution file was named `revit_mcp.sln` — renamed to `autocad_mcp.sln`.
- `install.bat` / `install-prebuilt.bat` did not copy
  `Microsoft.Win32.SystemEvents.dll` for the .NET 8 leg.

### Added — build, CI and packaging

- `build/build-all.ps1` — builds every leg, regenerates schemas, publishes the
  server, stages an installable bundle. Auto-detects the .NET 10 SDK, and drops
  unbuilt legs from the **staged** manifest so a partial build never ships a
  manifest promising a missing DLL.
- `build/sign.ps1` / `build/setup-signing.ps1` — Authenticode signing, CI-secret
  aware, with a local dev-certificate path.
- `installer/AutoCADMCP.iss` — Inno Setup installer; refuses to run while AutoCAD
  is open, reports which AutoCAD releases were detected, supports silent IT push.
- `.github/workflows/build.yml` and `release.yml` — build/verify on every push;
  tag-triggered signed release with installer and portable bundle.

### Testing

Four automated gates, none of which need AutoCAD installed:

- `build/verify-assembly.ps1` (14 checks) — loads the built DLL with AutoCAD refs
  resolved and actually exercises the registry, classification, JSON-RPC framing,
  error codes and both safety gates. This caught 18 annotation command classes
  that had been written but never registered.
- `build/verify-mcp-server.ps1` (11 checks) — drives the C# server over real
  stdio: protocol negotiation, notification handling, tool schemas, and graceful
  degradation when the plugin is down.
- `build/verify_tool_parity.py` (9 checks) — proves the C# and Python tool
  surfaces agree, with no unregistered classes and no unexposed commands.
- `tests/runtime_verify.py` — integration harness for a **live** AutoCAD; chains
  real entity handles through create → query → modify across every category and
  asserts the destructive gate both blocks and then permits a confirmed erase.

### Not done (deliberately)

- `trim_entity` / `extend_entity` to a cutting edge: the .NET API has no
  equivalent and a command-driven wrapper would be unreliable. Use
  `execute_command`.
- `fillet_entities` handles two `Line` entities; other geometry returns
  `Unsupported` rather than shipping wrong geometry.
- `create_center_mark` / `create_centerline`: those classes do not exist in the
  AutoCAD 2021 managed API this build targets.
- Sheet set **write** operations: the COM API is stateful and easy to corrupt.

### Merged with PR #4

Rebased onto PR #4 (AutoCAD 2027 support, reworked `plot_to_pdf`, hex handles,
document control, command diagnostics). Its 6 new commands were converted to the
`AcadCommand` base class. Where the two overlapped, the better implementation
won in each case — see STATUS.md for the full table. Notably PR #4 was right
about AutoCAD 2027's bundled `Newtonsoft.Json`: referencing our own NuGet copy
for the net10 leg causes a runtime `MissingMethodException`, so the build now
references AutoCAD 2027's assembly when present.

`list_plot_devices`, `list_paper_sizes` and `plot_layout` were **removed** as
superseded by PR #4's `plot_devices` and `plot_to_pdf`, which do strictly more
(printable areas and margins; waiting for the file and trimming the page).
`plot_layout` in particular reported success before the PDF existed.

### Fixed — issue #5 (tool-behaviour pitfalls and parameter aliases)

- **Parameter aliases**, so the MCP tool schema and the direct JSON-RPC contract
  no longer diverge and agents need no translation table:
  `move_entity`/`copy_entity` accept `from`/`to` **and** `from_point`/`to_point`;
  `zoom_window` and `select_by_window` accept `min`/`max` **and**
  `min_point`/`max_point`.
- **`measure_between` no longer throws** when an entity has no geometric extents
  (an empty block reference, a degenerate curve). `center_distance`, `dx` and `dy`
  become null with a `center_distance_note`, and `closest_distance` is still
  computed for curve pairs. It now also uses each entity's *true* centre for
  circles, arcs, ellipses and points, flagging bounding-box fallbacks with
  `center_approximate` rather than silently passing them off as centres.
- **`search_text`** returns `first_text` for the common "did it find anything,
  and what?" case.
- **`drawing_info`** returns `path: null` (not `""`) for an unsaved drawing, plus
  an explicit `is_saved` flag.
- **`create_table`** distinguishes `rows` (data rows, as requested) from
  `total_rows` (including a title row), and echoes `title` when one was set.
- **`.gitattributes` added** so line endings normalise to LF in the repository
  (CRLF for `.bat`/`.cmd`/`.ps1`/`.iss`, which Windows needs) — the noisy-diff
  problem the issue reported.

Already covered by PR #4 and therefore not reimplemented: the `get_entity`
detail fields for Arc/Polyline/Spline/Ellipse/MText/BlockReference, and the
`plot_to_pdf` rewrite onto `-PLOT`.

### Added — code execution and the AutoCode Agent (issue #5, parts 3 and 4)

Both were initially deferred as security-posture decisions rather than fixes;
both now ship **gated off by default**.

- **`execute_python`** — runs Python in the MCP server process with a pre-wired
  `call(method, params)`, for multi-step geometry that would otherwise cost
  dozens of round-trips. Requires `AUTOCAD_MCP_ALLOW_EXEC=1`. The code is not
  sandboxed, but it reaches AutoCAD only through the plugin's JSON-RPC port, so
  read-only mode and destructive confirmation still apply to what it draws.
- **`mcpagent` AutoCode Agent** (`src/mcpagent/`) — a separate MCP server that
  turns a plain-language drawing request into generated AutoCAD code.
  `generate_drawing_code` returns a plan and the code without running it and
  needs no permission; `draw` also executes and requires
  `AUTOCAD_AGENT_ALLOW_EXEC=1`. Its tool catalogue is built from the same
  generated `tools.json` the C# server embeds, so a new plugin tool reaches the
  agent with no edit — and the issue's hardcoded-relative-path concern does not
  apply. Host and port are environment-configurable.

  **Not verified against the live Claude API** — built without credentials on
  the development machine. The request shape is confirmed valid (the SDK accepts
  every parameter and the API rejects a dummy-key request at authentication
  rather than as malformed), but no real generation has been run.

### Known gaps

- The AutoCAD 2027 (`net10.0-windows`) leg is wired but **was not compiled
  locally** — installing the .NET 10 SDK failed with MSI `0x80070641` due to a
  pending file-rename operation (queued reboot). CI builds it. See STATUS.md 4.3.
- Command bodies that call the AutoCAD API are **compile-verified only**. Run
  `tests/runtime_verify.py` against a live AutoCAD to validate runtime behaviour.
- The sheet set COM path is unverified against a real Sheet Set Manager.

## [1.0.0]

Initial release — 73 MCP tools, TCP + HTTP JSON-RPC transports, AutoCAD
2022–2026 support.
