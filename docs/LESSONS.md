# Lessons

Hard-won gotchas from building this plugin. Written down so they are not
rediscovered. Add to this file whenever something costs more than ten minutes.

## AutoCAD .NET API

### `Solid3dMassProperties` misspells its own members

The property is `MomentsOfIntertia`, not `MomentsOfInertia` — likewise
`ProductsOfIntertia`. This is Autodesk's typo in the shipped API and it is not
going to be fixed, because fixing it would be a breaking change. We expose the
correctly-spelled key (`moments_of_inertia`) in JSON and absorb the typo in
`Entity3DCommands.cs`.

### `LayerStateMasks` has no `All` member

Unlike most flag enums in the API. The full mask has to be spelled out —
see `LayerStateHelper.FullMask` in `OrganizationCommands.cs`. Passing an
incomplete mask silently saves a partial layer state, which looks like data loss
when it is restored.

### `PlotSettingsValidator.RefreshLists()` needs a `PlotSettings`

There is no parameterless overload. If you only want to enumerate plot devices
and have no layout in hand, create a throwaway `new PlotSettings(false)` — do not
refresh against a real layout, or you mutate the user's page setup as a side
effect of a *listing* call.

### `DatabaseSummaryInfo.CustomProperties` is an enumerator, not a collection

It returns a fresh `IDictionaryEnumerator`. There is no `NumCustomInfo` /
`GetCustomSummaryInfo(i, out k, out v)` pair on the managed side despite what
older ObjectARX samples suggest. `DatabaseSummaryInfo` is also immutable —
edits go through `DatabaseSummaryInfoBuilder` and then reassigning
`db.SummaryInfo`.

### Viewports cannot be switched on until they are in the database

`Viewport.On = true` throws unless the viewport has already been appended to a
`BlockTableRecord` **and** its layout is current with `TILEMODE = 0`. The order
in `CreateViewportCommand` is deliberate: set the layout current → append →
`On = true` → set scale. Restore the previous layout in a `finally`.

### Handles are hexadecimal in the UI, decimal in `Handle.Value`

`ObjectId.Handle.Value` is a `long`; printing it gives decimal, but AutoCAD's own
properties palette shows hex. Users copying a handle out of the UI will paste
hex. `EntityHelper.ResolveHandle()` tries decimal first (round-trips our own
output) then hex. Note `"123"` is valid in both bases — decimal-first keeps our
own handles unambiguous.

### Dynamic block properties are strongly typed

Assigning a string to a `Real` property throws `eInvalidInput` at the API
boundary, not at parse time. Coerce using `PropertyTypeCode` before assigning
(see `SetDynamicBlockPropertyCommand.ConvertValue`). Also: a dynamic block
reference's `Name` is the *anonymous* block name; use
`DynamicBlockTableRecord` to get the name the user recognises.

### `MLeaderStyle` has no `SetDatabaseDefaults`

Nearly every other `DBObject` does, so the omission looks like a mistake in your
code rather than the API. The constructor already seeds defaults and
`PostMLeaderStyleToDb` finishes the job.

### `ImageDisplayOptions` has no `ShowImage`, and wipeout frames are global

Wipeout frame visibility is the `WIPEOUTFRAME` system variable, not a per-entity
property. Any per-entity "show frame" parameter is a lie — expose
`set_system_variable` instead.

### `CenterMark` and `Centerline` do not exist in the 2021 managed API

They are real AutoCAD objects but were not surfaced in `AcDbMgd` for the release
this build targets. Check `Assembly.GetType(...)` before designing a tool around
a class you remember existing.

### `Database.ReadDwgFile` is the single highest-leverage API here

It opens a DWG as a side database with no document window, no UI, and no effect
on the open drawing. That is what makes `read_external_dwg` and
`batch_query_dwgs` possible — folder-wide audits without opening a single file.
Remember `CloseInput(true)` and always wrap in `using`.

## Threading and the Idle loop

### A modal dialog used to block every single tool

Everything was marshaled to the main thread via `Application.Idle`. When AutoCAD
shows a modal dialog the Idle event stops firing, so *every* request — including
"what tools do you have?" — hung until the 30 s timeout. The `DirectCommand`
split fixes this: commands that never touch the AutoCAD API answer on the socket
thread. Keep `DirectCommand` implementations strictly free of AutoCAD API calls,
or they will crash the socket thread instead of being safely marshaled.

### Always `LockDocument()` before writing

Work started from the Idle event is outside a normal command context, so the
document is not implicitly locked. Writing without `doc.LockDocument()` throws
`eLockViolation` intermittently — intermittently, because it depends on what else
AutoCAD is doing at that moment, which makes it a nightmare to reproduce.

## Multi-version builds

### Compile against the OLDEST refs in an ABI family

The `net48` leg targets AutoCAD 2021–2024. Building it against 2022 (24.2) refs
produced a DLL that would not load on 2021. Building against 24.0 and letting it
run on the newer three is the correct direction — a plugin can use an API that
exists in all of its targets, never one added later. The build is the test: if a
post-2021 API is used, compilation fails.

### Keep every `#if` in one file

`Core/AcadCompat.cs` is the only file with conditional compilation. The other
~100 files are byte-identical across net48 / net8.0 / net10.0. This is the
pattern the sister Revit MCP uses (`IdCompat.cs`) and it is what keeps a
three-target build from becoming three codebases.

### AutoCAD LT can never be supported

LT has no `NETLOAD` at any version. LT 2024+ added AutoLISP and ActiveX/COM but
explicitly not ARX/.NET. Supporting LT would mean a separate AutoLISP or
out-of-process COM bridge — not a variation on this plugin. Do not spend time
looking for a workaround; there isn't one.

### Version codes are not year numbers

R24.0=2021, R24.1=2022, R24.2=2023, R24.3=2024, R25.0=2025, R25.1=2026,
R26.0=2027. Note 2021–2024 all share major 24 while 2025/2026 share major 25 —
the mapping is not arithmetic and has caught people out in `PackageContents.xml`.

## Tooling on this machine

### `jq` is not installed

Use PowerShell's `ConvertFrom-Json` for JSON inspection, or Python. Bash
one-liners that assume `jq` fail with exit 127.

### Only the .NET 8 SDK is installed

So the `net10.0-windows` (AutoCAD 2027) leg is opt-in behind
`-p:IncludeNet10=true` rather than a default target. Making it default would
break the build for everyone without the .NET 10 SDK. Install the .NET 10 SDK
before enabling it.

### PowerShell 5.1 has no numeric digit separators

`$port = 59_999` is a parse error that reports as "the term '59_999' is not
recognized as the name of a cmdlet" — a message that sends you looking for a
missing command instead of a literal. Same family as the em-dash bug: 5.1 fails
loudly but misleadingly on syntax it does not know.

### Windows Python and Git Bash disagree about `/tmp`

Git Bash maps `/tmp` into its own install tree; Windows Python resolves the same
string to `C:\tmp`. A bash heredoc that writes `/tmp/out.txt` and then hands the
path to `python` silently reads a different (usually nonexistent) file. Use an
explicit absolute Windows path when a command crosses that boundary.

### An MSI that fails with 0x80070641 usually means a pending reboot

`ERROR_INSTALL_SERVICE_FAILURE` reads like the Windows Installer service is
broken, but the service is typically running fine. Check
`HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations`
first — a queued rename from an earlier install blocks new MSIs until reboot.
This is what stopped the .NET 10 SDK install here.

### Verify without AutoCAD by loading the assembly reflectively

You can get a long way without launching AutoCAD: load the built DLL with an
`AssemblyResolve` handler pointing at the NuGet reference assemblies, then invoke
`CommandRegistry.GetAllMethods()` and `JsonRpcHandler.ProcessRequest()` directly.
That exercises registration, classification, JSON-RPC framing, error codes and
both safety gates. It does **not** exercise anything that touches a `Database` —
those need a real AutoCAD session and `tests/runtime_verify.py`.

## Design decisions worth remembering

### Prefer `Unsupported` over a bad implementation

`fillet_entities` handles two lines with real tangent-arc geometry and returns
`Unsupported` (with a pointer to `execute_command`) for anything else. Shipping a
half-working fillet for splines would be worse than an honest refusal — an AI
client can act on `Unsupported`, but not on silently wrong geometry.

### Classification by name needs an escape hatch

`CommandClassifier` infers read/write from the method name, which is right ~95%
of the time and saves annotating 157 classes. But `measure_entity` *places
markers* despite its read-only-sounding prefix, and `batch_query_dwgs` only reads
despite lacking a read prefix. Both override the inferred value explicitly. When
adding a tool, check that its name tells the truth about what it does.

### A "did you register it?" check pays for itself immediately

`build/verify-assembly.ps1` compares concrete `ICommand` classes in the built
assembly against the registry, because registration is manual and forgetting it
produces *dead code that still compiles*. The first time it ran it caught 18
annotation commands that had been written, built, and were completely
unreachable. Nothing else in the pipeline would have noticed.

Its companion `build/verify_tool_parity.py` catches the same class of drift from
the other side — a Python wrapper calling a method the plugin does not implement,
or a registered command no AI client can reach (exactly how `measure_text` sat
unused for a whole release).

### Generate the second copy, never hand-maintain it

The C# server needs per-tool JSON schemas; the Python server already encodes the
same information as type hints. Hand-writing 180 schemas would guarantee drift,
so `build/generate_tool_schemas.py` derives them from the Python AST and CI fails
if the generated file is stale. When two artifacts must agree, make one of them a
build output.

### Stage a manifest that matches what you actually built

`PackageContents.xml` in source declares all three legs. If a build skips the
net10 leg and ships that manifest unchanged, AutoCAD 2027 reports a load error
for a missing DLL. `build-all.ps1` therefore filters `ComponentEntry` elements
down to the legs present in that run — the source manifest stays complete, the
shipped one stays honest.

### Keep the destructive list tight

An early draft flagged `explode_` and `join_` as destructive. Requiring
confirmation for routine, undoable edits trains users to pass `__confirm: true`
reflexively, which defeats the gate. Only genuinely destructive, hard-to-undo
operations are listed — currently 10 of 157.
