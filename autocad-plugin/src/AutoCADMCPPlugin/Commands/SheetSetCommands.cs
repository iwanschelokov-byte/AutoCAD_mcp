using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    /// <summary>
    /// Sheet Set Manager access.
    ///
    /// Sheet sets have NO managed .NET API — the only programmatic surface is the
    /// AcSmComponents COM library. Everything here is therefore late-bound
    /// reflection over COM, which means:
    ///
    ///   * the ProgID differs between AutoCAD releases, so several are tried
    ///   * any failure degrades to ErrorCode.Unsupported with a clear message
    ///     rather than throwing
    ///
    /// Only read operations are exposed. Creating and editing sheets through this
    /// API is stateful and easy to corrupt, so it is deliberately not offered;
    /// use AutoCAD's own SHEETSET command for that.
    /// </summary>
    internal static class SheetSetCom
    {
        // Newest first — AutoCAD registers a version-suffixed ProgID, and some
        // releases also register the unsuffixed one.
        private static readonly string[] ProgIds =
        {
            "AcSmComponents.AcSmSheetSetMgr",
            "AcSmComponents26.AcSmSheetSetMgr",
            "AcSmComponents25.AcSmSheetSetMgr",
            "AcSmComponents24.AcSmSheetSetMgr",
            "AcSmComponents23.AcSmSheetSetMgr",
            "AcSmComponents22.AcSmSheetSetMgr",
            "AcSmComponents21.AcSmSheetSetMgr",
            "AcSmComponents20.AcSmSheetSetMgr"
        };

        private static object _manager;
        private static string _resolvedProgId;

        public static string ResolvedProgId { get { return _resolvedProgId; } }

        /// <summary>
        /// Get (or create) the sheet set manager COM object.
        /// Returns null when the COM server is unavailable on this machine.
        /// </summary>
        public static object GetManager(out string error)
        {
            error = null;
            if (_manager != null) return _manager;

            var tried = new List<string>();
            foreach (var progId in ProgIds)
            {
                try
                {
                    Type t = Type.GetTypeFromProgID(progId, false);
                    if (t == null) { tried.Add(progId); continue; }

                    _manager = Activator.CreateInstance(t);
                    if (_manager != null)
                    {
                        _resolvedProgId = progId;
                        return _manager;
                    }
                }
                catch (Exception)
                {
                    tried.Add(progId);
                }
            }

            error = "Sheet Set Manager COM server is not available. Tried: " +
                    string.Join(", ", tried.ToArray()) +
                    ". Sheet sets have no managed API, so this feature needs the " +
                    "AcSmComponents COM library registered by AutoCAD.";
            return null;
        }

        /// <summary>Late-bound method call; wraps COM failures into a readable message.</summary>
        public static object Call(object target, string method, params object[] args)
        {
            if (target == null) throw new InvalidOperationException("COM target is null");
            return target.GetType().InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                null,
                target,
                args);
        }

        public static string CallString(object target, string method, params object[] args)
        {
            try
            {
                object v = Call(target, method, args);
                return v == null ? "" : v.ToString();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Walk an IAcSmEnumComponent, which exposes Next()/Reset() rather than
        /// IEnumerable, yielding each component until it returns null.
        /// </summary>
        public static IEnumerable<object> Enumerate(object enumerator, int limit = 5000)
        {
            if (enumerator == null) yield break;

            try { Call(enumerator, "Reset"); } catch { }

            for (int i = 0; i < limit; i++)
            {
                object item;
                try { item = Call(enumerator, "Next"); }
                catch { yield break; }

                if (item == null) yield break;
                yield return item;
            }
        }
    }

    public abstract class SheetSetCommandBase : AcadCommand
    {
        /// <summary>Open a .dst and hand the sheet set to the implementation.</summary>
        protected CommandResult WithSheetSet(
            JObject parameters,
            Func<object, object, CommandResult> body)
        {
            string path = EntityHelper.ArgString(parameters, "path", "dst_path", "file_path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.BadParam("Parameter 'path' (a .dst sheet set file) is required");

            if (!File.Exists(path))
                return CommandResult.NotFound($"Sheet set file not found: {path}");

            string error;
            object mgr = SheetSetCom.GetManager(out error);
            if (mgr == null) return CommandResult.Unsupported(error);

            object db;
            try
            {
                // false = do not fail if the sheet set is already open
                db = SheetSetCom.Call(mgr, "OpenDatabase", path, false);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ErrorCode.Internal,
                    $"Could not open sheet set '{path}': {ex.Message}");
            }

            if (db == null)
                return CommandResult.Fail(ErrorCode.Internal, $"Sheet set '{path}' returned no database");

            object sheetSet;
            try
            {
                sheetSet = SheetSetCom.Call(db, "GetSheetSet");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ErrorCode.Internal,
                    $"Could not read the sheet set from '{path}': {ex.Message}");
            }

            if (sheetSet == null)
                return CommandResult.Fail(ErrorCode.Internal, $"'{path}' contains no sheet set");

            return body(db, sheetSet);
        }
    }

    public class OpenSheetSetCommand : SheetSetCommandBase
    {
        public override string MethodName => "open_sheet_set";

        // Opens a file for reading; does not modify the drawing.
        public override bool IsWrite => false;

        public override CommandResult Execute(JObject parameters)
        {
            return WithSheetSet(parameters, (db, sheetSet) =>
            {
                int sheetCount = 0;
                try
                {
                    object en = SheetSetCom.Call(sheetSet, "GetSheetEnumerator");
                    foreach (var s in SheetSetCom.Enumerate(en)) sheetCount++;
                }
                catch { }

                return CommandResult.Ok(new JObject
                {
                    ["name"] = SheetSetCom.CallString(sheetSet, "GetName"),
                    ["description"] = SheetSetCom.CallString(sheetSet, "GetDesc"),
                    ["sheet_count"] = sheetCount,
                    ["path"] = EntityHelper.ArgString(parameters, "path", "dst_path", "file_path"),
                    ["com_progid"] = SheetSetCom.ResolvedProgId ?? ""
                });
            });
        }
    }

    public class ListSheetsCommand : SheetSetCommandBase
    {
        public override string MethodName => "list_sheets";

        public override CommandResult Execute(JObject parameters)
        {
            return WithSheetSet(parameters, (db, sheetSet) =>
            {
                var sheets = new JArray();
                try
                {
                    object en = SheetSetCom.Call(sheetSet, "GetSheetEnumerator");
                    foreach (var sheet in SheetSetCom.Enumerate(en))
                    {
                        sheets.Add(new JObject
                        {
                            ["number"] = SheetSetCom.CallString(sheet, "GetNumber"),
                            ["title"] = SheetSetCom.CallString(sheet, "GetTitle"),
                            ["name"] = SheetSetCom.CallString(sheet, "GetName"),
                            ["description"] = SheetSetCom.CallString(sheet, "GetDesc")
                        });
                    }
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.Internal,
                        $"Could not enumerate sheets: {ex.Message}");
                }

                return CommandResult.Ok(new JObject
                {
                    ["sheet_set"] = SheetSetCom.CallString(sheetSet, "GetName"),
                    ["sheets"] = sheets,
                    ["count"] = sheets.Count
                });
            });
        }
    }

    public class CloseSheetSetCommand : SheetSetCommandBase
    {
        public override string MethodName => "close_sheet_set";

        public override bool IsWrite => false;

        public override CommandResult Execute(JObject parameters)
        {
            string path = EntityHelper.ArgString(parameters, "path", "dst_path", "file_path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.BadParam("Parameter 'path' is required");

            string error;
            object mgr = SheetSetCom.GetManager(out error);
            if (mgr == null) return CommandResult.Unsupported(error);

            try
            {
                object db = SheetSetCom.Call(mgr, "OpenDatabase", path, false);
                if (db != null) SheetSetCom.Call(mgr, "Close", db);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ErrorCode.Internal,
                    $"Could not close sheet set '{path}': {ex.Message}");
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["path"] = path
            });
        }
    }

    /// <summary>
    /// Reports whether the Sheet Set COM server is reachable, so a client can
    /// probe capability before relying on the other sheet set tools.
    /// </summary>
    public class SheetSetStatusCommand : AcadCommand
    {
        public override string MethodName => "get_sheet_set_status";

        public override bool IsWrite => false;

        public override CommandResult Execute(JObject parameters)
        {
            string error;
            object mgr = SheetSetCom.GetManager(out error);

            var data = new JObject
            {
                ["available"] = mgr != null,
                ["com_progid"] = SheetSetCom.ResolvedProgId ?? "",
                ["note"] = "Sheet sets have no managed .NET API; this uses the " +
                           "AcSmComponents COM library. Read-only operations only."
            };

            if (mgr == null) data["reason"] = error;

            return CommandResult.Ok(data);
        }
    }
}
