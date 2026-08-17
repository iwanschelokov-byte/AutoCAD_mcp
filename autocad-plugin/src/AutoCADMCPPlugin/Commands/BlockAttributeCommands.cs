using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    internal static class BlockHelper
    {
        public static ObjectId FindBlockDef(Transaction tr, Database db, string name)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return bt.Has(name) ? bt[name] : ObjectId.Null;
        }

        /// <summary>Read the attributes attached to one block reference.</summary>
        public static JObject ReadAttributes(Transaction tr, BlockReference br)
        {
            var attrs = new JObject();
            foreach (ObjectId attId in br.AttributeCollection)
            {
                var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (att == null) continue;
                attrs[att.Tag] = att.TextString;
            }
            return attrs;
        }
    }

    // ========================================================================
    // Attributes
    // ========================================================================

    public class ListBlockAttributesCommand : AcadCommand
    {
        public override string MethodName => "list_block_attributes";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "block_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' (block name) is required");

            var defs = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId btrId = BlockHelper.FindBlockDef(tr, doc.Database, name);
                if (btrId.IsNull) return CommandResult.NotFound($"Block '{name}' not found");

                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    var ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                    if (ad == null) continue;
                    defs.Add(new JObject
                    {
                        ["tag"] = ad.Tag,
                        ["prompt"] = ad.Prompt ?? "",
                        ["default"] = ad.TextString ?? "",
                        ["is_constant"] = ad.Constant,
                        ["is_invisible"] = ad.Invisible,
                        ["is_preset"] = ad.Preset,
                        ["is_verifiable"] = ad.Verifiable,
                        ["height"] = ad.Height,
                        ["position"] = new JArray(ad.Position.X, ad.Position.Y, ad.Position.Z)
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["block"] = name,
                ["attribute_definitions"] = defs,
                ["count"] = defs.Count
            });
        }
    }

    public class GetAttributeValuesCommand : AcadCommand
    {
        public override string MethodName => "get_attribute_values";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            string blockName = EntityHelper.ArgString(parameters, "name", "block_name");

            if (string.IsNullOrWhiteSpace(handle) && string.IsNullOrWhiteSpace(blockName))
                return CommandResult.BadParam("Provide either 'id' (a block reference) or 'name' (a block name)");

            Database db = doc.Database;
            var results = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!string.IsNullOrWhiteSpace(handle))
                {
                    ObjectId id = EntityHelper.ResolveHandle(db, handle);
                    if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) return CommandResult.BadParam($"Entity '{handle}' is not a block reference");

                    results.Add(new JObject
                    {
                        ["id"] = handle,
                        ["block"] = ResolveName(tr, br),
                        ["attributes"] = BlockHelper.ReadAttributes(tr, br)
                    });
                }
                else
                {
                    ObjectId btrId = BlockHelper.FindBlockDef(tr, db, blockName);
                    if (btrId.IsNull) return CommandResult.NotFound($"Block '{blockName}' not found");

                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    foreach (ObjectId refId in btr.GetBlockReferenceIds(true, true))
                    {
                        var br = tr.GetObject(refId, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;
                        results.Add(new JObject
                        {
                            ["id"] = refId.Handle.Value.ToString(),
                            ["block"] = ResolveName(tr, br),
                            ["position"] = new JArray(br.Position.X, br.Position.Y, br.Position.Z),
                            ["attributes"] = BlockHelper.ReadAttributes(tr, br)
                        });
                    }
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["references"] = results,
                ["count"] = results.Count
            });
        }

        internal static string ResolveName(Transaction tr, BlockReference br)
        {
            try
            {
                // Dynamic blocks report an anonymous name; resolve to the real one.
                ObjectId id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                return btr.Name;
            }
            catch { return br.Name; }
        }
    }

    public class SetAttributeValuesCommand : AcadCommand
    {
        public override string MethodName => "set_attribute_values";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' (block reference handle) is required");

            var values = EntityHelper.Arg(parameters, "attributes", "values") as JObject;
            if (values == null || !values.Properties().Any())
                return CommandResult.BadParam("Parameter 'attributes' must be an object of {TAG: value}");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) return CommandResult.BadParam($"Entity '{handle}' is not a block reference");

                var updated = new JArray();
                var notFound = new JArray();
                var wanted = values.Properties()
                                   .ToDictionary(p => p.Name, p => p.Value.ToString(),
                                                 StringComparer.OrdinalIgnoreCase);

                foreach (ObjectId attId in br.AttributeCollection)
                {
                    var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                    if (att == null) continue;

                    string newValue;
                    if (!wanted.TryGetValue(att.Tag, out newValue)) continue;

                    var writable = (AttributeReference)tr.GetObject(attId, OpenMode.ForWrite);
                    writable.TextString = newValue;
                    updated.Add(att.Tag);
                    wanted.Remove(att.Tag);
                }

                foreach (var leftover in wanted.Keys) notFound.Add(leftover);

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["updated"] = updated,
                    ["not_found"] = notFound,
                    ["updated_count"] = updated.Count
                });
            }
        }
    }

    public class SyncAttributesCommand : AcadCommand
    {
        public override string MethodName => "sync_attributes";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "block_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' (block name) is required");

            Database db = doc.Database;
            int added = 0, touched = 0;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId btrId = BlockHelper.FindBlockDef(tr, db, name);
                if (btrId.IsNull) return CommandResult.NotFound($"Block '{name}' not found");

                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);

                // Collect attribute definitions declared by the block.
                var defs = new List<AttributeDefinition>();
                foreach (ObjectId id in btr)
                {
                    var ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                    if (ad != null && !ad.Constant) defs.Add(ad);
                }

                foreach (ObjectId refId in btr.GetBlockReferenceIds(true, true))
                {
                    var br = tr.GetObject(refId, OpenMode.ForWrite) as BlockReference;
                    if (br == null) continue;
                    touched++;

                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (att != null) present.Add(att.Tag);
                    }

                    // Append any definition the reference is missing.
                    foreach (var ad in defs)
                    {
                        if (present.Contains(ad.Tag)) continue;

                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, br.BlockTransform);
                        ar.TextString = ad.TextString ?? "";
                        br.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                        added++;
                    }
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["block"] = name,
                ["references_processed"] = touched,
                ["attributes_added"] = added,
                ["message"] = $"Synced {touched} reference(s); added {added} missing attribute(s)."
            });
        }
    }

    // ========================================================================
    // Dynamic blocks
    // ========================================================================

    public class GetDynamicBlockPropertiesCommand : AcadCommand
    {
        public override string MethodName => "get_dynamic_block_properties";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' (block reference handle) is required");

            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) return CommandResult.BadParam($"Entity '{handle}' is not a block reference");

                if (!br.IsDynamicBlock)
                {
                    return CommandResult.Ok(new JObject
                    {
                        ["id"] = handle,
                        ["is_dynamic"] = false,
                        ["properties"] = new JArray(),
                        ["message"] = "This block reference is not dynamic."
                    });
                }

                var props = new JArray();
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                {
                    var o = new JObject
                    {
                        ["name"] = p.PropertyName,
                        ["value"] = JToken.FromObject(p.Value?.ToString() ?? ""),
                        ["read_only"] = p.ReadOnly,
                        ["show"] = p.Show,
                        ["units_type"] = p.UnitsType.ToString()
                    };

                    var allowed = new JArray();
                    try
                    {
                        foreach (var v in p.GetAllowedValues())
                            allowed.Add(v?.ToString() ?? "");
                    }
                    catch { }
                    o["allowed_values"] = allowed;

                    props.Add(o);
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["id"] = handle,
                    ["is_dynamic"] = true,
                    ["block"] = GetAttributeValuesCommand.ResolveName(tr, br),
                    ["properties"] = props,
                    ["count"] = props.Count
                });
            }
        }
    }

    public class SetDynamicBlockPropertyCommand : AcadCommand
    {
        public override string MethodName => "set_dynamic_block_property";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            string propName = EntityHelper.ArgString(parameters, "property", "name", "property_name");
            var valueToken = EntityHelper.Arg(parameters, "value");

            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' (block reference handle) is required");
            if (string.IsNullOrWhiteSpace(propName))
                return CommandResult.BadParam("Parameter 'property' is required");
            if (valueToken == null)
                return CommandResult.BadParam("Parameter 'value' is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var br = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                if (br == null) return CommandResult.BadParam($"Entity '{handle}' is not a block reference");
                if (!br.IsDynamicBlock)
                    return CommandResult.Unsupported($"Block reference '{handle}' is not a dynamic block");

                DynamicBlockReferenceProperty target = null;
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                {
                    if (string.Equals(p.PropertyName, propName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = p;
                        break;
                    }
                }

                if (target == null)
                    return CommandResult.NotFound($"Dynamic property '{propName}' not found on this block");
                if (target.ReadOnly)
                    return CommandResult.Unsupported($"Dynamic property '{propName}' is read-only");

                object converted;
                try
                {
                    converted = ConvertValue(target, valueToken);
                }
                catch (System.Exception ex)
                {
                    return CommandResult.BadParam($"Could not convert value for '{propName}': {ex.Message}");
                }

                try
                {
                    target.Value = converted;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam(
                        $"AutoCAD rejected value for '{propName}': {ex.Message}");
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["property"] = propName,
                    ["value"] = valueToken.ToString()
                });
            }
        }

        /// <summary>
        /// Dynamic block properties are strongly typed; coerce the incoming JSON
        /// to the type the property actually expects.
        /// </summary>
        private static object ConvertValue(DynamicBlockReferenceProperty prop, JToken token)
        {
            switch (prop.PropertyTypeCode)
            {
                case (short)DwgDataType.Real:
                    return token.Value<double>();
                case (short)DwgDataType.Int16:
                    return token.Value<short>();
                case (short)DwgDataType.Int32:
                    return token.Value<int>();
                case (short)DwgDataType.Bool:
                    return token.Value<bool>();
                default:
                    return token.ToString();
            }
        }

        // Mirrors the DXF group-code families used by dynamic block properties.
        private enum DwgDataType : short
        {
            Null = 0,
            Real = 1,
            Int32 = 2,
            Int16 = 3,
            Int8 = 4,
            Text = 5,
            Bool = 6
        }
    }

    // ========================================================================
    // Block definition management
    // ========================================================================

    public class RenameBlockCommand : AcadCommand
    {
        public override string MethodName => "rename_block";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "block_name", "old_name");
            string newName = EntityHelper.ArgString(parameters, "new_name");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(newName))
                return CommandResult.BadParam("Parameters 'name' and 'new_name' are required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(name)) return CommandResult.NotFound($"Block '{name}' not found");
                if (bt.Has(newName)) return CommandResult.BadParam($"Block '{newName}' already exists");

                var btr = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForWrite);
                if (btr.IsFromExternalReference)
                    return CommandResult.Unsupported("Xref blocks cannot be renamed this way; use set_xref_path");

                btr.Name = newName;
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["old_name"] = name,
                ["new_name"] = newName
            });
        }
    }

    public class DeleteBlockDefinitionCommand : AcadCommand
    {
        public override string MethodName => "delete_block_definition";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "block_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(name)) return CommandResult.NotFound($"Block '{name}' not found");

                var btr = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);

                var refIds = btr.GetBlockReferenceIds(true, true);
                if (refIds.Count > 0)
                {
                    return CommandResult.BadParam(
                        $"Block '{name}' is still referenced {refIds.Count} time(s). " +
                        "Erase those references first, or use purge_drawing.");
                }

                var writable = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForWrite);
                writable.Erase();
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["deleted"] = name
            });
        }
    }

    public class CountBlockReferencesCommand : AcadCommand
    {
        public override string MethodName => "count_block_references";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string filter = EntityHelper.ArgString(parameters, "name", "block_name");
            Database db = doc.Database;
            var counts = new JArray();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsLayout || btr.IsAnonymous) continue;
                    if (!string.IsNullOrWhiteSpace(filter) &&
                        !string.Equals(btr.Name, filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int n = btr.GetBlockReferenceIds(true, true).Count;

                    // Unused blocks are noise in a full listing, but when a specific
                    // block was asked for, a zero count is the answer.
                    if (n > 0 || !string.IsNullOrWhiteSpace(filter))
                    {
                        counts.Add(new JObject
                        {
                            ["block"] = btr.Name,
                            ["count"] = n,
                            ["is_xref"] = btr.IsFromExternalReference
                        });
                        total += n;
                    }
                }
                tr.Commit();
            }

            if (!string.IsNullOrWhiteSpace(filter) && counts.Count == 0)
                return CommandResult.NotFound($"Block '{filter}' not found");

            return CommandResult.Ok(new JObject
            {
                ["blocks"] = counts,
                ["distinct_blocks"] = counts.Count,
                ["total_references"] = total
            });
        }
    }

    public class ExportBlockToFileCommand : AcadCommand
    {
        public override string MethodName => "export_block_to_file";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "block_name");
            string path = EntityHelper.ArgString(parameters, "path", "output_path", "file_path");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                return CommandResult.BadParam("Parameters 'name' and 'path' are required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            {
                ObjectId btrId;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    btrId = BlockHelper.FindBlockDef(tr, db, name);
                    tr.Commit();
                }

                if (btrId.IsNull) return CommandResult.NotFound($"Block '{name}' not found");

                try
                {
                    using (Database newDb = db.Wblock(btrId))
                    {
                        newDb.SaveAs(path, DwgVersion.Current);
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.Internal, $"WBLOCK failed: {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.Internal, $"Could not write '{path}': {ex.Message}");
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["block"] = name,
                ["path"] = path
            });
        }
    }
}
