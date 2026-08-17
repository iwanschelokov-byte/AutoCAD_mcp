# AutoCAD MCP — Upgrade Plan

> **Execution status: see [STATUS.md](STATUS.md).** As of 2026-08-17, Phases 0, 1
> and 4 are complete and Phase 3 is 80/116 tools delivered (73 → 158 MCP tools,
> AutoCAD 2021–2027). Phase 2 (C# server port) is deliberately not started — it
> changes the deployment model and needs sign-off. Phase 5 (CI/installer) is
> partial. The plan text below is unchanged as the original reference.

> Goal: bring the AutoCAD MCP up to the maturity of the Revit MCP (`C:\Users\haris\Desktop\Revit_mcp`),
> expand tool coverage from ~73 to 200+, and support AutoCAD 2021–2027 (+ a decision point on LT).
> Drafted 2026-08-17. Repo confirmed in sync with `origin/main` at commit `7d31a4e`.

---

## 1. Where we are vs where the Revit MCP is

| Dimension | AutoCAD MCP (today) | Revit MCP (reference) |
|---|---|---|
| Tools | 71 MCP tools (+2 C#-only: `measure_text/s` not exposed in Python) | 242 tools |
| Server | Python FastMCP (`server.py`) → TCP 8081 → C# plugin | All-C# server (net8, hand-rolled MCP, per-tool JSON schemas in `ToolSchemas.cs`) |
| Threading | `IdleActionRunner` (Application.Idle queue) — everything marshaled | `ExternalEventBridge` + **DirectCommand split** (introspection never blocks) |
| Errors | Free-text `CommandResult.Fail(string)` | Typed `ErrorCode` enum surfaced in `error.data.errorCode` |
| Safety | None | Read-only mode, destructive confirmation (`"__confirm": true` bypass), Guardian rules, JSONL audit log |
| Schemas | FastMCP infers from Python signatures | Hand-authored per-tool schemas, decoupled from plugin (ship via `dotnet publish` only) |
| Versions | 2022–2024 (net48) + 2025–2026 (net8) via one `.bundle` | 2023–2027 (net48/net8/net10) via `-p:RevitVersion` + `IdCompat.cs` shim |
| Build/CI | `install.bat`, committed DLLs in `dist/`, no CI | `build-all.ps1`, GitHub Actions, Inno Setup, signing, choco/winget manifests |
| Testing | `test_all_tools.py` (manual raw-socket smoke) | 3 live harnesses incl. all-242 coverage runner; results committed to `docs/VERIFICATION.md` |
| Docs | 2 READMEs (plugin one stale) | MASTER_PLAN, STATUS, CHANGELOG, LESSONS, 8 subsystem docs |

The two projects already share DNA (TCP JSON-RPC bridge, `ICommand` + `CommandRegistry`, one-class-per-tool,
main-thread marshaling, `.bundle`/addin autoload, multi-target csproj). The Revit MCP is simply several
hardening/packaging generations ahead — most of its improvements port back almost mechanically.

---

## 2. Phase 0 — Quick wins (hours, no architecture change)

1. **Expose `measure_text` / `measure_texts`** in `server.py` — already implemented + registered in C#
   (`Commands/MeasureTextCommands.cs`), just missing `@mcp.tool()` wrappers. Instant +2 tools.
2. **Repo hygiene**: rename `revit_mcp.sln` → `autocad_mcp.sln`; fix the stale
   `autocad-plugin/README.md` (claims 33 tools, references nonexistent `SearchCommands.cs`).
3. **AutoCAD 2021 support (cheap widening)**: `PackageContents.xml` currently gates net48 at
   `SeriesMin="R24.1"` (2022). 2021 (R24.0) is the same .NET Framework 4.8 ABI family.
   Action: rebuild net48 leg against the *oldest* reference assemblies (AutoCAD.NET NuGet `24.0.x`
   instead of `24.2.*`), verify no API used is newer than 2021, then set `SeriesMin="R24.0"`.
   One DLL then covers 2021–2024.
4. **Adopt the docs skeleton now**: create `STATUS.md`, `CHANGELOG.md`, `docs/LESSONS.md` — copy the
   Revit MCP's operational gotchas that apply (PowerShell em-dash bug, MOTW/SmartScreen, MSIX Claude
   Desktop config path, etc.) so knowledge accrues from day one.

## 3. Phase 1 — Foundation hardening (port from Revit MCP, ~1 sprint)

Port these Revit MCP patterns into the C# plugin (`src/AutoCADMCPPlugin/`):

- **A1. Typed errors** — `ErrorCode` enum (`NotFound, InvalidParam, ReadOnly, NoDocument, NeedsConfirm,
  Unsupported, TxnFailed, Timeout, Internal`) carried in `CommandResult` and surfaced as
  JSON-RPC `error.data.errorCode`. (Ref: Revit `Models/CommandResult.cs`.)
- **A2. `CommandClassifier`** — infer read/write/destructive from name prefixes
  (`list_/get_/measure_/search_/find_/select_` = read; `erase_/delete_/purge_/bulk_erase` = destructive),
  with per-class override. Avoids annotating ~75+ classes. (Ref: Revit `Core/CommandClassifier.cs`.)
- **A3. Safety layer** — read-only mode setting; destructive ops require `"__confirm": true`
  (returns `NeedsConfirm` otherwise). (Ref: Revit `Core/ConfirmationHelper.cs`, `Settings.cs`.)
- **A4. Direct vs marshaled command split** — `DirectCommand` base class executing on the socket thread
  for `system_status`, `list_methods`, `get_capabilities` so introspection stays responsive when AutoCAD
  has a modal dialog up (today a modal blocks the Idle queue → 30 s timeout on *everything*).
- **A5. `get_capabilities` tool** — reports version, active doc, read-only state, tool count, per-category
  availability. (Ref: Revit `Commands/` + `Core/App.cs`.)
- **A6. JSONL audit log** — append-only `ActivityLogger` with `user/machine/drawing` fields from day one
  ("central-ready"; the Revit dashboard can consume it later with minimal changes).
- **A7. Forgiving parameter aliases** — accept `id|entity_id|handle`, `point|location`, etc. in
  `EntityHelper.Parse*`. The Revit team found these mismatches only via live agent testing.
- **A8. Settings store + MCPSTART UX** — persisted port/read-only/confirm settings; keep autoload but
  consider server-off-by-default with explicit `MCPSTART` (matches Revit's security posture — decide).

## 4. Phase 2 — Server architecture decision (~1 sprint)

**Recommendation: port the Python server to C# (net8), mirroring `RevitMCP.Server`.** Rationale:
- Single self-contained exe → no Python runtime on end-user machines; enables the same Inno/choco/winget
  packaging pipeline the Revit MCP already has (reuse `build-all.ps1`, `RevitMCP.iss`, CI workflows nearly verbatim).
- Hand-authored per-tool JSON schemas in a `ToolSchemas.cs` (schema fixes ship with `dotnet publish`,
  no plugin rebuild / AutoCAD restart).
- One codebase convention across both products; the hand-rolled MCP layer in `RevitMCP.Server/McpServer.cs`
  is copy-portable (only `PluginClient` target port and tool tables differ).

Keep `server.py` during transition as the schema reference (exactly what Revit MCP did with `src/mcp_server/`).

**Carry-over feature to preserve:** `create_table_from_excel` does client-side openpyxl work — in C# use
ClosedXML (or keep this one tool Python-side until parity).

## 5. Phase 3 — Tool expansion: 73 → 200+ (3–5 sprints)

Grouped in priority order; names follow existing conventions. Each tool = one `ICommand` class + registry
line + schema entry (+ verify-harness step).

### Sprint A — Layouts, paper space, plotting (highest value gap; ~22 tools)
- `list_layouts`, `create_layout`, `delete_layout`, `rename_layout`, `set_current_layout`, `copy_layout`
- `create_viewport` (pspace), `list_viewports`, `set_viewport_view` (center/scale/target), `lock_viewport`,
  `set_viewport_scale`, `viewport_layer_override` (VP freeze/thaw)
- `get_page_setup`, `set_page_setup` (paper size, plot style table, plot area), `list_plot_styles` (CTB/STB),
  `list_paper_sizes`, `list_plotters`
- `plot_layout` (extend `plot_to_pdf` to arbitrary layouts/devices), `publish_layouts` (multi-sheet PDF/DWF batch)
- `create_layout_from_template`, `get_plot_extents`, `preview_plot` (screenshot of layout)

### Sprint B — Xrefs, underlays, external data (~14 tools)
- `attach_xref`, `detach_xref`, `reload_xref`, `unload_xref`, `bind_xref`, `list_xrefs`, `set_xref_path`
- `attach_image`, `list_images`, `attach_pdf_underlay`, `attach_dwf_underlay`
- `read_external_dwg` (side-database `ReadDwgFile` — query entities/layers/blocks of a DWG *without* opening it),
  `extract_from_external_dwg` (copy objects in), `batch_query_dwgs` (folder sweep — huge for audits)

### Sprint C — Blocks deep support + attributes (~12 tools)
- `list_block_attributes`, `get_attribute_values`, `set_attribute_values` (per block ref)
- `get_dynamic_block_properties`, `set_dynamic_block_property` (DynamicBlockReferenceProperty)
- `redefine_block`, `rename_block`, `delete_block_definition`, `export_block_to_file` (WBLOCK)
- `replace_block_references`, `count_block_references` (schedule/BOM extraction), `sync_attributes` (ATTSYNC)

### Sprint D — Modify/edit operations (~16 tools)
- `fillet_entities`, `chamfer_entities`, `trim_entity`, `extend_entity`, `break_entity`, `lengthen_entity`
- `align_entities`, `stretch_entities` (window+delta)
- `polyline_edit` (add/remove vertex, set width, open/close, fit/spline), `reverse_polyline`
- `set_draworder` (front/back/above/below), `flatten_entities` (Z→0)
- `overkill` (delete duplicates/overlaps), `divide_entity` / `measure_entity` (point/block placement)
- `create_boundary` (BPOLY at point), `create_region`

### Sprint E — Annotation & tables completion (~14 tools)
- `create_multileader` + `create_mleader_style`, `create_ordinate_dimension`, `create_arclength_dimension`,
  `create_tolerance`, `create_centerline` / `create_center_mark`
- `edit_dimension_text`, `update_dimensions`, `set_annotation_scale` (CANNOSCALE + annotative flag),
  `list_annotation_scales`, `add_annotation_scale_to_entity`
- `set_table_cell`, `get_table_data`, `merge_table_cells`, `create_table_style`
- `edit_mtext`, `create_wipeout`, `create_revision_cloud`

### Sprint F — Entities: 3D + remaining 2D (~14 tools)
- 2D: `create_point` (+`set_point_style`), `create_ray`, `create_xline`, `create_donut`, `create_polygon`,
  `create_3d_polyline`, `create_helix`
- 3D solids: `create_box`, `create_cylinder`, `create_sphere`, `create_wedge`, `extrude_profile`,
  `revolve_profile`, `boolean_solids` (union/subtract/intersect), `get_solid_properties` (volume/centroid/moments)

### Sprint G — Organization, data, views (~18 tools)
- Groups: `create_group`, `list_groups`, `add_to_group`, `explode_group`
- Layer states: `save_layer_state`, `restore_layer_state`, `list_layer_states`; `isolate_layers`, `lock_layer`
- Named views/UCS: `create_named_view`, `list_named_views`, `restore_view`, `set_ucs`, `list_ucs`
- Data: `get_xdata`, `set_xdata`, `get_drawing_properties` / `set_drawing_properties` (DWGPROPS custom fields),
  `create_field_text`
- Utilities: `audit_drawing`, `entity_count_report` (by type/layer), `drawing_compare` (two DWGs, side-DB diff)

### Sprint H — Sheet Set Manager via COM (optional, ~6 tools)
`AcSmSheetSetMgr` has no managed wrapper — needs COM interop from the plugin:
`open_sheet_set`, `list_sheets`, `add_sheet`, `set_sheet_properties`, `publish_sheet_set`, `create_sheet_set`.
Gate behind a capability flag (fails gracefully where unavailable).

**Total: ~116 new tools → ~190; with the classifier + alias + schema plumbing from Phase 1 these are
almost purely mechanical.** Prioritize A → B → C first: layouts/plot, xrefs, and attributes are the three
biggest real-world workflow gaps for production drawing work.

## 6. Phase 4 — Multi-version strategy (2021 → 2027, verticals, LT)

Facts (verified 2026-08):
- 2021=R24.0, 2022=R24.1, 2023=R24.2, 2024=R24.3 → .NET Framework 4.8, one ABI family
- 2025=R25.0, 2026=R25.1 → .NET 8, one family (2025/2026 pair)
- **2027=R26.0 → .NET 10, full recompile, VS 2026** (announced; plan the third target now)

Actions:
1. **net48 leg**: build against oldest refs (24.0) → covers 2021–2024; widen bundle to `SeriesMin="R24.0"`.
2. **net8 leg**: unchanged, `R25.0`–`R25.1`.
3. **net10 leg (2027 — in scope NOW)**: `AutoCAD.NET 26.0.0` is already published on NuGet (verified
   2026-08-17), so this is a current deliverable, not future-proofing:
   - Prerequisite: install the **.NET 10 SDK** on the build machine (only 8.0.121 present today) and
     ensure CI uses `actions/setup-dotnet` with both 8.x and 10.x.
   - Add `net10.0-windows` to `<TargetFrameworks>` with `AutoCAD.NET 26.0.*` (+ matching
     `System.Drawing.Common` for `capture_screenshot`).
   - Third `ComponentEntry` in `PackageContents.xml`: `SeriesMin="R26.0" SeriesMax="R26.0"`,
     `Contents/net10.0-windows/`; overall bundle `SeriesMax` → `R26.0`.
   - Commit pre-built DLLs to `dist/net10.0-windows/` and extend `install.bat` / `install-prebuilt.bat`
     to copy the third leg.
   - Expect minor API drift vs net8 — funnel any `#if` differences through the `AcadCompat.cs` shim (item 4).
   - Verify on a real AutoCAD 2027 install (runtime harness smoke run) before release.
4. **`AcadCompat.cs` shim** (mirror of Revit's `IdCompat.cs`): one file isolating all `#if NET48 / NET8_0 /
   NET10_0` API drift; the other ~90 files stay source-identical. Add `ACAD20XX` DefineConstants per target.
5. **Verticals for free**: Civil 3D / Plant 3D / Map 3D / Mechanical / Architecture all host vanilla
   AutoCAD `.bundle` plugins — document + test, no code needed. (Civil 3D-specific tools = future opt-in
   assembly, not this plan.)
6. **AutoCAD LT**: **cannot load .NET plugins at any version** (no NETLOAD). LT 2024+ has AutoLISP + COM only.
   Recommendation: declare LT out of scope for now; if demanded later, a separate thin bridge
   (AutoLISP socket poller or out-of-process COM via version-pinned ProgID `AutoCAD.Application.2X`)
   exposing a reduced tool set. Do not contort the main architecture for it.
7. **COM fallback transport** (optional): out-of-process COM survives .NET runtime breaks without
   recompilation — worth a spike only if plugin distribution becomes a friction point.

## 7. Phase 5 — Build, packaging, CI, verification (~1–2 sprints, mostly reuse)

Port from Revit MCP with path/name changes:
- `build/build-all.ps1` — auto-detect installed AutoCAD versions (`acad.exe` under
  `C:\Program Files\Autodesk\AutoCAD 20XX\`), loop targets, stage `dist/`, publish server, optional `-Installer`.
- GitHub Actions: `build.yml` (every push — build all targets with NuGet refs, no AutoCAD needed) +
  `release.yml` (on `v*` tag: build, sign, Inno installer, choco/winget manifests, GitHub Release).
- `installer/AutoCADMCP.iss` (Inno Setup): bundle → `%APPDATA%\Autodesk\ApplicationPlugins`, server exe →
  Program Files, silent-install flags for IT push. + `scripts/configure-mcp.ps1` (merge-safe Claude config writer).
- Code signing: reuse `setup-signing.ps1` / `sign.ps1` and the CI secrets pattern.
- **Verification harness**: `tests/runtime_verify.py` (fast smoke, fresh drawing via `drawing_new`, chained
  create→query→modify per category) and `tests/runtime_verify_all.py` (every tool, PASS/COND/FAIL report).
  Results committed to `docs/VERIFICATION.md` after every release-candidate run — this practice caught
  multiple real bugs in the Revit MCP (schema/handler mismatches, collector crashes).

## 8. Suggested execution order & effort

| # | Phase | Effort | Outcome |
|---|---|---|---|
| 0 | Quick wins | ~½ day | +2 tools, 2021 support path, docs skeleton |
| 1 | Foundation hardening | 1 sprint | Typed errors, safety, DirectCommand, audit log |
| 2 | C# server port | 1 sprint | Single-exe deploy, real schemas, packaging-ready |
| 3A–C | Layouts/plot, xrefs, blocks | 2 sprints | The 3 biggest workflow gaps (~48 tools) |
| 5 | Build/CI/installer/tests | 1–2 sprints (parallel w/ 3) | Reuse Revit pipeline |
| 3D–H | Remaining tool sprints | 2–3 sprints | ~190–200 tools total |
| 4 | net10/2027 target | ~2–3 days (do alongside Phase 0/1) | Full 2021–2027 coverage — refs already on NuGet |

Decision points to confirm before starting Phase 1/2:
1. Port server to C# (recommended) vs keep Python?
2. Server autoload-and-listen (current) vs off-by-default + explicit MCPSTART (Revit posture)?
3. Is AutoCAD LT support actually needed by any user? (Determines whether the COM/LISP bridge track exists.)
4. Guardian-style rules layer for AutoCAD (layer-standards enforcement, protected-layer detection) — v2 or v3?
