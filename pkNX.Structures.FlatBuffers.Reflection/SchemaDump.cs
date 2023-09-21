using System;
using System.IO;
using System.Linq;
using FlatSharp;

namespace pkNX.Structures.FlatBuffers.Reflection;

/// <summary>
/// Exports fbs, cs
/// </summary>
public static class SchemaDumpOld
{
    public static Schema HandleReflection(byte[] data, string filePath, string dir, SchemaDumpSettings settings)
    {
        var schema = Schema.Serializer.Parse(data, FlatBufferDeserializationOption.GreedyMutable);
        DumpSchema(schema, filePath, dir, settings);
        return schema;
    }

    public static void DumpSchema(Schema schema, string filePath, string dir, SchemaDumpSettings settings)
    {
        var rootName = schema.RootTable?.Name ?? Path.GetFileNameWithoutExtension(filePath);

        var fileCS = Path.Combine(dir, $"{rootName}.cs");
        using var fsCS = File.Create(fileCS);
        using var cs = new StreamWriter(fsCS);

        var fileFBS = Path.Combine(dir, $"{rootName}.cs");
        using var fsFBS = File.Create(fileFBS);
        using var fbs = new StreamWriter(fsFBS);

        DumpSchema(schema, fbs, cs, settings);
    }

    public static void DumpSchema(byte[] data, TextWriter fbs, TextWriter cs, SchemaDumpSettings settings)
    {
        var schema = Schema.Serializer.Parse(data, FlatBufferDeserializationOption.GreedyMutable);
        DumpSchema(schema, fbs, cs, settings);
    }

    public static void DumpSchema(Schema schema, TextWriter fbs, TextWriter cs, SchemaDumpSettings settings)
    {
        WriteHeaderFBS(schema, fbs, settings.FileNamespace);
        WriteHeaderCS(schema, cs, settings.FileNamespace);

        DumpEnums(schema, fbs, settings.StripNamespace);
        DumpObjects(schema, fbs, cs, settings.StripNamespace);

        if (schema.RootTable?.Name is { } x)
            fbs.WriteLine($"root_type {x};");
    }

    private static void WriteHeaderFBS(Schema schema, TextWriter fbs, string fileNameSpace)
    {
        fbs.WriteLine($"namespace {fileNameSpace};");
        fbs.WriteLine();
        fbs.WriteLine("attribute \"fs_vector\";");
        fbs.WriteLine("attribute \"fs_serializer\";");
        fbs.WriteLine("attribute \"fs_valueStruct\";");
        fbs.WriteLine("attribute \"fs_nonVirtual\";");
        fbs.WriteLine("attribute \"fs_unsafeStructVector\";");
        fbs.WriteLine();
        fbs.WriteLine("// Generated by FlatSharp");
        fbs.WriteLine($"// FileIdentifier: {schema.FileIdent}");
        fbs.WriteLine($"// FileExtension: {schema.FileExt}");
        fbs.WriteLine($"// Object Count: {schema.Objects.Count}");
        fbs.WriteLine($"// Enum Count: {schema.Enums.Count}");
    }

    private static void WriteHeaderCS(Schema schema, TextWriter cs, string fileNameSpace)
    {
        cs.WriteLine("using System.Collections.Generic;");
        cs.WriteLine("using System.ComponentModel;");
        cs.WriteLine("using FlatSharp.Attributes;");
        cs.WriteLine("// ReSharper disable UnusedMember.Global");
        cs.WriteLine("// ReSharper disable ClassNeverInstantiated.Global");
        cs.WriteLine("// ReSharper disable UnusedType.Global");
        cs.WriteLine("#nullable disable");
        cs.WriteLine();
        cs.WriteLine($"namespace {fileNameSpace};");
        cs.WriteLine();
        cs.WriteLine("// Generated by FlatSharp");
        cs.WriteLine($"// FileIdentifier: {schema.FileIdent}");
        cs.WriteLine($"// FileExtension: {schema.FileExt}");
        cs.WriteLine($"// Object Count: {schema.Objects.Count}");
        cs.WriteLine($"// Enum Count: {schema.Enums.Count}");
        cs.WriteLine($"// Root Type: {schema.RootTable?.Name ?? "None Specified"}");
    }

    private static ReadOnlySpan<char> GetName(ReadOnlySpan<char> defName, bool stripNamespace)
    {
        if (!stripNamespace)
            return defName;
        var idx = defName.LastIndexOf('.');
        return idx < 0 ? defName : defName[(idx + 1)..];
    }

    private static void DumpObjects(Schema schema, TextWriter fbs, TextWriter cs, bool stripNamespace)
    {
        var list = schema.Objects;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var obj = list[i];

            cs.WriteLine();
            cs.WriteLine("[TypeConverter(typeof(ExpandableObjectConverter))]");
            cs.WriteLine($"public partial {(obj.IsStruct ? "struct" : "class")} {GetName(obj.Name, stripNamespace)} {{ }}");

            DumpObjectFBS(schema, fbs, stripNamespace, obj);
        }
    }

    private static void DumpObjectFBS(Schema schema, TextWriter fbs, bool stripNamespace, Object def)
    {
        fbs.WriteLine();
        fbs.Write(def.IsStruct ? "struct" : "table");
        fbs.Write(" ");
        fbs.Write(GetName(def.Name, stripNamespace));
        if (schema.RootTable?.Name == def.Name)
            fbs.Write(" (fs_serializer)");
        fbs.WriteLine(" {"); // end of class header

        // Write fields to fbs
        foreach (var field in def.Fields.OrderBy(z => z.Id))
        {
            fbs.Write("    ");
            fbs.Write(field.Name);
            fbs.Write(":");
            fbs.Write(field.Type.ToFBS(schema, field.Id));

            WriteAttributes(fbs, field);

            fbs.WriteLine(";"); // end of line
        }
        fbs.WriteLine("}");
    }

    private static void WriteAttributes(TextWriter fbs, Field field)
    {
        bool any = false;

        void Write(string value)
        {
            fbs.Write(!any ? " (" : ", ");
            fbs.Write(value);
            any = true;
        }

        if (field.Key)
            Write("key");
        if (field.Required)
            Write("required");
        if (field.Optional)
            Write("optional");
        if (field.Deprecated)
            Write("deprecated");

        if (any)
            fbs.Write(")");
    }

    private static void DumpEnums(Schema schema, TextWriter fbs, bool stripNamespace)
    {
        var list = schema.Enums;
        for (var i = 0; i < list.Count; i++)
        {
            var def = list[i];
            fbs.WriteLine();

            var typeName = def.UnderlyingType.ToFBS(schema, i, true);
            var implType = $" : {typeName}";
            var defName = GetName(def.Name, stripNamespace);
            fbs.WriteLine($"enum {defName}{implType}");
            fbs.WriteLine("{");
            {
                for (int j = 0; j < def.Values.Count; j++)
                {
                    var value = def.Values[j];
                    fbs.WriteLine($"    {j} = {value},");
                }
            }
            fbs.WriteLine("}");
        }
    }

    private static string ToFBS(this Type type, Schema schema, int index, bool force = false)
    {
        var size = type.FixedLength;
        var sizeStr = size > 0 ? $":{size}" : "";

        if (index != -1)
        {
            var idx = type.Index;
            if (type.BaseType == BaseType.Array)
                return $"[{GetName(schema, idx, type.Element)}{sizeStr}]";
            if (type.BaseType == BaseType.Vector)
                return $"[{GetName(schema, idx, type.Element)}{sizeStr}]";
            if (type.BaseType == BaseType.Obj)
                return $"{schema.Objects[idx].Name}";
            if (!force)
                return $"{schema.Enums[idx].Name}";
        }

        var baseType = type.BaseType;
        if (baseType is BaseType.Array)
            return $"[{GetName(type.Element)}{sizeStr}]";
        if (baseType is BaseType.Vector)
            return $"[{GetName(type.Element)}{sizeStr}]";

        var x = GetName(baseType);
        if (type.Element != BaseType.None)
            return $"[{x}]";
        return x;
    }

    public static string GetName(BaseType type) => type.ToString().ToLower();

    private static string GetName(Schema schema, int idx, BaseType type)
    {
        if (type is BaseType.Obj)
            return schema.Objects[idx].Name;
        return schema.Enums[idx].Name;
    }
}
