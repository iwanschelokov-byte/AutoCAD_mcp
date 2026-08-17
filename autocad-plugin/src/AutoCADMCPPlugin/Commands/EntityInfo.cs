using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AutoCADMCPPlugin.Commands
{
    /// <summary>
    /// Shared conversion between the JSON <c>handle</c> field and an
    /// <see cref="ObjectId"/>.
    ///
    /// This used to be <c>id.Handle.Value.ToString()</c>, which prints the
    /// handle as a decimal number ("9941366"). Nothing else in AutoCAD speaks
    /// that dialect: the properties palette, DXF group 5, the LIST command and
    /// AutoLISP's <c>handent</c> all use hexadecimal ("97B176"), so a handle
    /// taken out of an MCP selection could not be fed to a LISP script or
    /// looked up by hand — <c>(handent "9941366")</c> simply returns nil.
    ///
    /// Emission is therefore hexadecimal everywhere. Parsing stays lenient:
    /// hex first, then decimal, so handles captured by older callers keep
    /// working. Resolution is always against the database, which is what makes
    /// the two-base guess safe — a string that parses in both bases is only
    /// accepted in the base that actually names an object.
    /// </summary>
    internal static class Handles
    {
        /// <summary>Handle of an id as AutoCAD itself spells it (hex).</summary>
        public static string Format(ObjectId id)
        {
            try { return id.Handle.ToString(); }
            catch { return ""; }
        }

        /// <summary>Handle of an entity as AutoCAD itself spells it (hex).</summary>
        public static string Format(DBObject obj)
        {
            try { return obj.Handle.ToString(); }
            catch { return ""; }
        }

        /// <summary>
        /// Find the object named by <paramref name="text"/>. Hexadecimal is
        /// tried first (what this plugin now emits and what AutoCAD shows),
        /// decimal second (what this plugin emitted before).
        /// </summary>
        public static bool TryResolve(Database db, string text, out ObjectId id)
        {
            id = ObjectId.Null;
            if (db == null || string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

            foreach (int radix in new[] { 16, 10 })
            {
                try
                {
                    long value = Convert.ToInt64(s, radix);
                    if (value <= 0) continue;
                    if (db.TryGetObjectId(new Handle(value), out id) && !id.IsNull)
                        return true;
                }
                catch { }
            }

            id = ObjectId.Null;
            return false;
        }

        /// <summary>
        /// <see cref="TryResolve"/> plus the standard error text, so every
        /// command reports an unknown handle the same way.
        /// </summary>
        public static ObjectId Resolve(Database db, string text, out string error)
        {
            if (Handles.TryResolve(db, text, out ObjectId id)) { error = null; return id; }
            error = $"No entity with handle '{text}'. Handles are hexadecimal, as shown " +
                    "in the properties palette; decimal is still accepted for compatibility.";
            return ObjectId.Null;
        }
    }

    /// <summary>
    /// Shared helpers for describing and filtering entities.
    ///
    /// Historically every query command compared <c>ent.GetType().Name</c> to the
    /// caller's <c>type</c> string. That silently returns nothing for the names
    /// users actually see in AutoCAD ("AcDbBlockReference" in the properties
    /// palette, "INSERT" in DXF), which made type filtering unreliable.
    /// <see cref="TypeMatches"/> accepts all of those spellings plus a set of
    /// friendly aliases, and every entity we emit now carries both the .NET type
    /// name and the DXF name so the caller can round-trip either one.
    /// </summary>
    internal static class EntityInfo
    {
        /// <summary>DXF name of the entity ("INSERT", "LWPOLYLINE", ...), or "".</summary>
        public static string DxfName(Entity ent)
        {
            try { return ent.GetRXClass().DxfName ?? ""; }
            catch { return ""; }
        }

        /// <summary>AutoCAD class name of the entity ("AcDbBlockReference", ...), or "".</summary>
        public static string RxName(Entity ent)
        {
            try { return ent.GetRXClass().Name ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// True when <paramref name="filter"/> designates the type of
        /// <paramref name="ent"/>. <paramref name="filter"/> may be null/empty
        /// (matches everything) or a comma-separated list of type names; an
        /// entity matching any item passes.
        ///
        /// Each item is matched against the .NET type name ("BlockReference"),
        /// the AutoCAD class name ("AcDbBlockReference"), the DXF name
        /// ("INSERT"), and a small alias table ("block", "text", "pline", ...).
        /// </summary>
        public static bool TypeMatches(Entity ent, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;

            foreach (string raw in filter.Split(','))
            {
                string want = raw.Trim();
                if (want.Length == 0) continue;
                if (MatchesSingle(ent, want)) return true;
            }
            return false;
        }

        private static bool MatchesSingle(Entity ent, string want)
        {
            const StringComparison ic = StringComparison.OrdinalIgnoreCase;

            string net = ent.GetType().Name;
            string rx = RxName(ent);
            string dxf = DxfName(ent);

            if (want.Equals(net, ic)) return true;
            if (want.Equals(rx, ic)) return true;
            if (want.Equals(dxf, ic)) return true;
            // "BlockReference" given as "AcDbBlockReference" and vice versa.
            if (("AcDb" + want).Equals(rx, ic)) return true;
            if (want.StartsWith("AcDb", ic) && want.Substring(4).Equals(net, ic)) return true;

            switch (want.ToLowerInvariant())
            {
                case "text":
                    return ent is DBText && !(ent is AttributeDefinition);
                case "mtext":
                    return ent is MText;
                case "anytext":
                    return ent is DBText || ent is MText;
                case "block":
                case "insert":
                case "blockref":
                case "blockreference":
                    return ent is BlockReference;
                case "attdef":
                case "attributedefinition":
                    return ent is AttributeDefinition;
                case "pline":
                case "polyline":
                case "lwpolyline":
                    return ent is Polyline || ent is Polyline2d || ent is Polyline3d;
                case "dim":
                case "dimension":
                    return ent is Dimension;
                case "leader":
                    return ent is Leader || ent is MLeader;
                case "curve":
                    return ent is Curve;
                case "hatch":
                    return ent is Hatch;
                case "line":
                    return ent is Line;
                case "circle":
                    return ent is Circle && !(ent is Arc);
                case "arc":
                    return ent is Arc;
                default:
                    return false;
            }
        }

        /// <summary>GeometricExtents without throwing. Returns false for entities that have none.</summary>
        public static bool TryExtents(Entity ent, out Extents3d ext)
        {
            try
            {
                ext = ent.GeometricExtents;
                return true;
            }
            catch
            {
                ext = new Extents3d();
                return false;
            }
        }

        /// <summary>True when the entity bounding box lies entirely inside the window (AutoCAD "window" selection).</summary>
        public static bool Inside(Extents3d ext, double minX, double minY, double maxX, double maxY)
        {
            return ext.MinPoint.X >= minX && ext.MinPoint.Y >= minY &&
                   ext.MaxPoint.X <= maxX && ext.MaxPoint.Y <= maxY;
        }

        /// <summary>True when the entity bounding box overlaps the window (AutoCAD "crossing" selection).</summary>
        public static bool Crosses(Extents3d ext, double minX, double minY, double maxX, double maxY)
        {
            return ext.MinPoint.X <= maxX && ext.MaxPoint.X >= minX &&
                   ext.MinPoint.Y <= maxY && ext.MaxPoint.Y >= minY;
        }

        /// <summary>
        /// Common JSON description of an entity. <paramref name="detailed"/>
        /// adds per-type geometry (vertices, angles, lengths); without it the
        /// output stays small enough to list hundreds of entities at once but
        /// still carries what a caller needs to decide what to look at:
        /// handle, both type names, layer, colour, a representative position
        /// and the bounding box.
        /// </summary>
        public static JObject Summarize(Transaction tr, ObjectId id, Entity ent, bool detailed)
        {
            var o = new JObject
            {
                ["handle"] = Handles.Format(id),
                ["type"] = ent.GetType().Name,
                ["dxf_type"] = DxfName(ent),
                ["layer"] = ent.Layer,
                ["color"] = ent.ColorIndex
            };

            if (detailed)
            {
                o["linetype"] = ent.Linetype;
                o["visible"] = ent.Visible;
            }

            if (TryExtents(ent, out Extents3d ext))
            {
                o["bbox"] = new JArray(
                    Math.Round(ext.MinPoint.X, 4), Math.Round(ext.MinPoint.Y, 4),
                    Math.Round(ext.MaxPoint.X, 4), Math.Round(ext.MaxPoint.Y, 4));
            }

            if (ent is Line line)
            {
                o["start"] = Pt(line.StartPoint);
                o["end"] = Pt(line.EndPoint);
                if (detailed) o["length"] = line.Length;
            }
            else if (ent is Arc arc)
            {
                o["center"] = Pt(arc.Center);
                o["radius"] = arc.Radius;
                if (detailed)
                {
                    o["start_angle"] = arc.StartAngle * 180.0 / Math.PI;
                    o["end_angle"] = arc.EndAngle * 180.0 / Math.PI;
                    o["length"] = arc.Length;
                }
            }
            else if (ent is Circle circle)
            {
                o["center"] = Pt(circle.Center);
                o["radius"] = circle.Radius;
                if (detailed) o["area"] = circle.Area;
            }
            else if (ent is Polyline pline)
            {
                o["closed"] = pline.Closed;
                o["vertex_count"] = pline.NumberOfVertices;
                if (detailed)
                {
                    o["length"] = pline.Length;
                    var verts = new JArray();
                    for (int i = 0; i < pline.NumberOfVertices; i++)
                    {
                        Point2d pt = pline.GetPoint2dAt(i);
                        verts.Add(new JArray(pt.X, pt.Y));
                    }
                    o["vertices"] = verts;
                }
            }
            else if (ent is DBText text)
            {
                o["text"] = text.TextString;
                o["position"] = Pt(text.Position);
                o["height"] = text.Height;
                if (detailed) o["rotation"] = text.Rotation * 180.0 / Math.PI;
            }
            else if (ent is MText mtext)
            {
                o["text"] = mtext.Contents;
                o["position"] = Pt(mtext.Location);
                o["height"] = mtext.TextHeight;
            }
            else if (ent is BlockReference bref)
            {
                o["block_name"] = SafeBlockName(bref);
                o["position"] = Pt(bref.Position);
                o["rotation"] = bref.Rotation * 180.0 / Math.PI;
                o["scale_x"] = bref.ScaleFactors.X;
                o["scale_y"] = bref.ScaleFactors.Y;
                if (detailed && tr != null)
                {
                    var atts = new JObject();
                    try
                    {
                        foreach (ObjectId attId in bref.AttributeCollection)
                        {
                            var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (att != null) atts[att.Tag ?? ""] = att.TextString ?? "";
                        }
                    }
                    catch { }
                    if (atts.Count > 0) o["attributes"] = atts;
                }
            }
            else if (ent is Dimension dim)
            {
                try { o["measurement"] = dim.Measurement; } catch { }
                try { o["text"] = dim.DimensionText; } catch { }
            }

            return o;
        }

        private static string SafeBlockName(BlockReference bref)
        {
            try { return bref.Name; }
            catch { return null; }
        }

        private static JArray Pt(Point3d p)
        {
            return new JArray(Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4));
        }
    }
}
