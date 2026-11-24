// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
//
// Spike 1 (Track B): Experimental Goose implementation of kongor-metagen.
// Architecture: Zero-heap compiler-assigned data stacks + flat contiguous model tables
// + self-relative 4-byte JSON document tree links (`json::Node&<u32>?`).
// Produces byte-identical C++, C#, and Lua protocol bindings from data/schemas/protocol.json.

import std;
import os;
import json;
import kongor_log from "../../../goose/pkg/kongor_log";

struct ConstantDef {
    name:        const u8[:],
    val_type:    const u8[:],
    value:       const u8[:],
    description: const u8[:],
}

struct EnumMemberDef {
    name:        const u8[:],
    value:       const u8[:],
    description: const u8[:],
}

struct EnumDef {
    name:            const u8[:],
    underlying_type: const u8[:],
    description:     const u8[:],
    first_member:    i64,
    member_count:    i64,
}

struct FieldDef {
    name:         const u8[:],
    field_type:   const u8[:],
    description:  const u8[:],
    is_array:     bool,
    element_type: const u8[:],
    array_count:  i64,
    offset:       i64,
    size:         i64,
}

struct StructDef {
    name:            const u8[:],
    expected_size:   i64,
    calculated_size: i64,
    description:     const u8[:],
    first_field:     i64,
    field_count:     i64,
    first_dep:       i64,
    dep_count:       i64,
    visit_state:     i32,
}

struct DfsFrame {
    struct_idx: i64,
    next_dep:   i64,
}

fn has_unquoted_line_comment(raw: const u8[:]) -> bool {
    var in_str = false;
    var i = 0;
    while i < raw.len {
        let c = raw[i];
        if in_str {
            if c == '\\' { i += 2; continue; }
            if c == '"' { in_str = false; }
            i += 1;
            continue;
        }
        if c == '"' {
            in_str = true;
            i += 1;
            continue;
        }
        if c == '/' && i + 1 < raw.len && raw[i + 1] == '/' {
            return true;
        }
        i += 1;
    }
    return false;
}

fn strip_unquoted_line_comments(raw: const u8[:], out: u8[>..]&) {
    var in_str = false;
    var i = 0;
    while i < raw.len {
        let c = raw[i];
        if in_str {
            out.push(c);
            if c == '\\' && i + 1 < raw.len {
                out.push(raw[i + 1]);
                i += 2;
                continue;
            }
            if c == '"' { in_str = false; }
            i += 1;
            continue;
        }
        if c == '"' {
            in_str = true;
            out.push(c);
            i += 1;
            continue;
        }
        if c == '/' && i + 1 < raw.len && raw[i + 1] == '/' {
            i += 2;
            while i < raw.len && raw[i] != '\n' { i += 1; }
            continue;
        }
        out.push(c);
        i += 1;
    }
}

fn get_string_or(obj: json::Node?, key: const u8[:], fallback: const u8[:]) -> const u8[:] {
    let m = json::member(obj, key);
    let s, ok = json::as_string(m);
    if ok { return s; }
    return fallback;
}

fn get_int_or(obj: json::Node?, key: const u8[:], fallback: i64) -> i64, bool {
    let m = json::member(obj, key);
    guard m else { return fallback, true; }
    let v, ok = json::as_i64(m);
    if ok { return v, true; }
    return fallback, false;
}

fn get_value_literal(obj: json::Node?, key: const u8[:], scratch: u8[>..]&, fallback: const u8[:]) -> const u8[:], bool {
    let m = json::member(obj, key);
    guard m else { return fallback, true; }
    match m.val {
        Str s => { return s.s, true; },
        Int n => {
            let start = scratch.len;
            format(scratch, n.v);
            return scratch[start..], true;
        },
        Num x => {
            let start = scratch.len;
            json::format_number(scratch, x.v);
            return scratch[start..], true;
        },
        _ => { return fallback, false; },
    }
}

fn primitive_type_size(t: const u8[:]) -> i64 {
    if t == "uint8" || t == "int8" { return 1; }
    if t == "uint16" || t == "int16" { return 2; }
    if t == "uint32" || t == "int32" { return 4; }
    if t == "uint64" || t == "int64" { return 8; }
    if t == "float32" || t == "float" { return 4; }
    if t == "float64" || t == "double" { return 8; }
    return 0;
}

fn to_cpp_type(t: const u8[:]) -> const u8[:] {
    if t == "uint8" { return "uint8_t"; }
    if t == "uint16" { return "uint16_t"; }
    if t == "uint32" { return "uint32_t"; }
    if t == "uint64" { return "uint64_t"; }
    if t == "int8" { return "int8_t"; }
    if t == "int16" { return "int16_t"; }
    if t == "int32" { return "int32_t"; }
    if t == "int64" { return "int64_t"; }
    if t == "float32" { return "float"; }
    if t == "float64" { return "double"; }
    return t;
}

fn to_cs_type(t: const u8[:]) -> const u8[:] {
    if t == "uint8" { return "byte"; }
    if t == "uint16" { return "ushort"; }
    if t == "uint32" { return "uint"; }
    if t == "uint64" { return "ulong"; }
    if t == "int8" { return "sbyte"; }
    if t == "int16" { return "short"; }
    if t == "int32" { return "int"; }
    if t == "int64" { return "long"; }
    if t == "float32" { return "float"; }
    if t == "float64" { return "double"; }
    return t;
}

fn find_struct_index(structs: StructDef[>..]&, name: const u8[:]) -> i64 {
    for i in structs.len {
        if structs[i].name == name { return i; }
    }
    return -1;
}

fn ensure_parent_dirs(file_path: const u8[:]) {
    for c, i in file_path {
        if i > 0 && (c == '/' || c == '\\') {
            if i == 2 && file_path[1] == ':' { continue; }
            make_dir(file_path[..i]);
        }
    }
}

fn write_output_file(path: const u8[:], content: const u8[:]) -> bool {
    ensure_parent_dirs(path);
    if !write_file(path, content) {
        kongor_log::error_actionable(
            str("Failed to write generated protocol file '", path, "'"),
            "Verify directory write permissions and run powershell.exe -File scripts/re/generate_protocol.ps1",
        );
        return false;
    }
    return true;
}

fn generate_cpp_header(
    out:            u8[>..]&,
    constants:      ConstantDef[>..]&,
    enums:          EnumDef[>..]&,
    enum_members:   EnumMemberDef[>..]&,
    structs:        StructDef[>..]&,
    fields:         FieldDef[>..]&,
    sorted_indices: i64[>..]&,
) {
    out.append("// <auto-generated>\n");
    out.append("// Generated by kongor-metagen from data/schemas/protocol.json\n");
    out.append("// DO NOT MODIFY DIRECTLY\n");
    out.append("// </auto-generated>\n\n");

    out.append("#ifndef KONGOR_PROTOCOL_H\n");
    out.append("#define KONGOR_PROTOCOL_H\n\n");
    out.append("#include <stdint.h>\n");
    out.append("#include <stdbool.h>\n");
    out.append("#include <stddef.h>\n\n");

    out.append("#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n");

    out.append("// ============================================================================\n");
    out.append("// PROTOCOL CONSTANTS\n");
    out.append("// ============================================================================\n\n");

    for i in constants.len {
        let c = constants[i];
        format(out, "/* ", c.description, " */\n");
        format(out, "#define ", c.name, " ", c.value, "\n\n");
    }

    out.append("// ============================================================================\n");
    out.append("// PROTOCOL STRUCTURES (PACKED 1-BYTE ALIGNMENT)\n");
    out.append("// ============================================================================\n\n");

    out.append("#pragma pack(push, 1)\n\n");

    for idx in sorted_indices {
        let s = structs[idx];
        format(out, "/**\n * @brief ", s.description, "\n */\n");
        format(out, "typedef struct ", s.name, " {\n");

        for f_off in s.field_count {
            let field = fields[s.first_field + f_off];
            if field.is_array {
                let elem_t = to_cpp_type(field.element_type);
                format(
                    out,
                    "    ", elem_t, " ", field.name, "[", field.array_count,
                    "]; /* offset ", field.offset, ", size ", field.size,
                    " bytes | ", field.description, " */\n",
                );
            } else {
                let field_t = to_cpp_type(field.field_type);
                format(
                    out,
                    "    ", field_t, " ", field.name,
                    "; /* offset ", field.offset, ", size ", field.size,
                    " bytes | ", field.description, " */\n",
                );
            }
        }
        format(out, "} ", s.name, ";\n\n");

        out.append("#ifdef __cplusplus\n");
        format(out, "static_assert(sizeof(", s.name, ") == ", s.calculated_size, ", \"", s.name, " size mismatch\");\n");
        for f_off in s.field_count {
            let field = fields[s.first_field + f_off];
            format(
                out,
                "static_assert(offsetof(", s.name, ", ", field.name, ") == ", field.offset,
                ", \"", s.name, ".", field.name, " offset mismatch\");\n",
            );
        }
        out.append("#endif\n\n");
    }

    out.append("#pragma pack(pop)\n\n");
    out.append("#ifdef __cplusplus\n}\n#endif\n\n");

    if enums.len > 0 {
        out.append("#ifdef __cplusplus\n");
        out.append("// ============================================================================\n");
        out.append("// PROTOCOL STRONGLY-TYPED ENUMS\n");
        out.append("// ============================================================================\n\n");
        for i in enums.len {
            let e = enums[i];
            let cpp_under = to_cpp_type(e.underlying_type);
            format(out, "/**\n * @brief ", e.description, "\n */\n");
            format(out, "enum class ", e.name, " : ", cpp_under, " {\n");
            for m_off in e.member_count {
                let mem = enum_members[e.first_member + m_off];
                format(out, "    ", mem.name, " = ", mem.value, ", /* ", mem.description, " */\n");
            }
            out.append("};\n\n");
        }
        out.append("#endif // __cplusplus\n\n");
    }

    out.append("#endif // KONGOR_PROTOCOL_H\n");
}

fn generate_cs_file(
    out:            u8[>..]&,
    constants:      ConstantDef[>..]&,
    enums:          EnumDef[>..]&,
    enum_members:   EnumMemberDef[>..]&,
    structs:        StructDef[>..]&,
    fields:         FieldDef[>..]&,
    sorted_indices: i64[>..]&,
) {
    out.append("// <auto-generated>\n");
    out.append("// Generated by kongor-metagen from data/schemas/protocol.json\n");
    out.append("// DO NOT MODIFY DIRECTLY\n");
    out.append("// </auto-generated>\n\n");

    out.append("#nullable enable\n");
    out.append("using System;\n");
    out.append("using System.Runtime.InteropServices;\n\n");

    out.append("namespace ProjectKongor.Contracts.Generated\n{\n");

    out.append("    /// <summary>\n    /// Authoritative Engine and Fast-Bus Protocol Constants\n    /// </summary>\n");
    out.append("    public static class KongorConstants\n    {\n");
    for i in constants.len {
        let c = constants[i];
        let cs_t = to_cs_type(c.val_type);
        format(out, "        /// <summary>", c.description, "</summary>\n");
        format(out, "        public const ", cs_t, " ", c.name, " = ", c.value, ";\n\n");
    }
    out.append("    }\n\n");

    for i in enums.len {
        let e = enums[i];
        let cs_under = to_cs_type(e.underlying_type);
        format(out, "    /// <summary>\n    /// ", e.description, "\n    /// </summary>\n");
        format(out, "    public enum ", e.name, " : ", cs_under, "\n    {\n");
        for m_off in e.member_count {
            let mem = enum_members[e.first_member + m_off];
            format(out, "        /// <summary>", mem.description, "</summary>\n");
            format(out, "        ", mem.name, " = ", mem.value, ",\n\n");
        }
        out.append("    }\n\n");
    }

    for idx in sorted_indices {
        let s = structs[idx];
        format(out, "    /// <summary>\n    /// ", s.description, "\n    /// </summary>\n");
        out.append("    [StructLayout(LayoutKind.Sequential, Pack = 1)]\n");
        format(out, "    public unsafe struct ", s.name, "\n    {\n");
        format(out, "        public const int SizeInBytes = ", s.calculated_size, ";\n\n");

        for f_off in s.field_count {
            let field = fields[s.first_field + f_off];
            format(
                out,
                "        /// <summary>", field.description,
                " (offset ", field.offset, ", size ", field.size, " bytes)</summary>\n",
            );
            if field.is_array {
                let elem_t = to_cs_type(field.element_type);
                format(out, "        public fixed ", elem_t, " ", field.name, "[", field.array_count, "];\n\n");
            } else {
                let field_t = to_cs_type(field.field_type);
                format(out, "        public ", field_t, " ", field.name, ";\n\n");
            }
        }
        out.append("    }\n\n");
    }

    out.append("}\n");
}

fn generate_lua_file(
    out:            u8[>..]&,
    constants:      ConstantDef[>..]&,
    enums:          EnumDef[>..]&,
    enum_members:   EnumMemberDef[>..]&,
    structs:        StructDef[>..]&,
    fields:         FieldDef[>..]&,
    sorted_indices: i64[>..]&,
) {
    out.append("-- <auto-generated>\n");
    out.append("-- Generated by kongor-metagen from data/schemas/protocol.json\n");
    out.append("-- DO NOT MODIFY DIRECTLY\n");
    out.append("-- </auto-generated>\n\n");

    out.append("KongorProtocol = KongorProtocol or {}\n\n");

    out.append("KongorProtocol.Constants = {\n");
    for i in constants.len {
        let c = constants[i];
        format(out, "    ", c.name, " = ", c.value, ", -- ", c.description, "\n");
    }
    out.append("}\n\n");

    if enums.len > 0 {
        out.append("KongorProtocol.Enums = {\n");
        for i in enums.len {
            let e = enums[i];
            format(out, "    ", e.name, " = {\n");
            for m_off in e.member_count {
                let mem = enum_members[e.first_member + m_off];
                format(out, "        ", mem.name, " = ", mem.value, ", -- ", mem.description, "\n");
            }
            out.append("    },\n");
        }
        out.append("}\n\n");
    }

    out.append("KongorProtocol.StructSizes = {\n");
    for idx in sorted_indices {
        let s = structs[idx];
        format(out, "    ", s.name, " = ", s.calculated_size, ",\n");
    }
    out.append("}\n\n");

    out.append("KongorProtocol.FieldOffsets = {\n");
    for idx in sorted_indices {
        let s = structs[idx];
        format(out, "    ", s.name, " = {\n");
        for f_off in s.field_count {
            let field = fields[s.first_field + f_off];
            format(out, "        ", field.name, " = ", field.offset, ",\n");
        }
        out.append("    },\n");
    }
    out.append("}\n\n");

    out.append("return KongorProtocol\n");
}

fn print_usage() {
    print("Usage: kongor-metagen-goose [options]");
    print("Options:");
    print("  --schema <path>   Input protocol schema JSON (default: data/schemas/protocol.json)");
    print("  --cpp-out <path>  Output C++ header file (default: native/include/generated/kongor_protocol.h)");
    print("  --cs-out <path>   Output C# contract file (default: src/libs/contracts/Generated/KongorProtocol.g.cs)");
    print("  --lua-out <path>  Output Lua lookup file (default: games/client/override/ui/scripts/generated/protocol.lua)");
    print("  --check           Verify parity without modifying files (returns 1 on drift)");
    print("  --verbose         Print detailed telemetry and layout traces");
}

fn main() {
    let t0_ns = clock_ns();
    kongor_log::init("kongor-metagen-goose");

    let cli_args = args();
    var schema_path:  const u8[:] = "data/schemas/protocol.json";
    var cpp_out_path: const u8[:] = "native/include/generated/kongor_protocol.h";
    var cs_out_path:  const u8[:] = "src/libs/contracts/Generated/KongorProtocol.g.cs";
    var lua_out_path: const u8[:] = "games/client/override/ui/scripts/generated/protocol.lua";
    var check_only = false;
    var verbose = false;

    var arg_idx = 1;
    while arg_idx < cli_args.len {
        let a = cli_args[arg_idx];
        if a == "--schema" && arg_idx + 1 < cli_args.len {
            arg_idx += 1;
            schema_path = cli_args[arg_idx];
        } else if a == "--cpp-out" && arg_idx + 1 < cli_args.len {
            arg_idx += 1;
            cpp_out_path = cli_args[arg_idx];
        } else if a == "--cs-out" && arg_idx + 1 < cli_args.len {
            arg_idx += 1;
            cs_out_path = cli_args[arg_idx];
        } else if a == "--lua-out" && arg_idx + 1 < cli_args.len {
            arg_idx += 1;
            lua_out_path = cli_args[arg_idx];
        } else if a == "--check" {
            check_only = true;
        } else if a == "--verbose" {
            verbose = true;
        } else if a == "--help" || a == "-h" {
            print_usage();
            return;
        } else {
            kongor_log::error_actionable(
                str("Unrecognized or incomplete CLI option '", a, "'"),
                "Run 'kongor-metagen-goose --help' to inspect valid flags",
            );
            exit(1);
        }
        arg_idx += 1;
    }

    print("[KONGOR-METAGEN] Initializing Goose data stacks and reading schema '", schema_path, "'...");
    kongor_log::debug(str("CLI resolved: schema=", schema_path, " check=", check_only, " verbose=", verbose));

    var raw_schema: u8[>..] = [];
    guard read_file(schema_path, raw_schema) else {
        kongor_log::error_actionable(
            str("Failed to read schema file '", schema_path, "'"),
            str("Verify file exists and run powershell.exe -File scripts/re/test_metagen_parity.ps1 -SchemaPath ", schema_path),
        );
        exit(1);
    }

    guard raw_schema.len > 0 else {
        kongor_log::error_actionable(
            str("Schema file '", schema_path, "' is empty (0 bytes)"),
            "Restore valid JSON schema from git or inspect data/schemas/protocol.json",
        );
        exit(1);
    }

    var stripped_schema: u8[>..] = [];
    let use_stripped = has_unquoted_line_comment(raw_schema);
    if use_stripped {
        strip_unquoted_line_comments(raw_schema, stripped_schema);
    }
    let effective_schema: const u8[:] = if use_stripped { stripped_schema } else { raw_schema };

    var doc:     json::Node[>..] = [];
    var decoded: u8[>..]         = [];
    let root, parse_err = json::parse(effective_schema, doc, decoded);
    guard root else {
        kongor_log::error_actionable(
            str("Malformed JSON in schema '", schema_path, "': ", parse_err),
            str("Validate JSON syntax in '", schema_path, "' and re-run scripts/re/test_metagen_parity.ps1"),
        );
        exit(1);
    }

    match root.val {
        Obj => {},
        _ => {
            kongor_log::error_actionable(
                str("Root JSON value in '", schema_path, "' must be a JSON object '{}'"),
                str("Inspect root value of '", schema_path, "'"),
            );
            exit(1);
        },
    }

    kongor_log::debug(str("Parsed JSON tree into ", doc.len, " flat self-relative nodes (decoded=", decoded.len, " B)"));

    var lit_scratch:  u8[>..]            = [];
    var constants:    ConstantDef[>..]   = [];
    var enums:        EnumDef[>..]       = [];
    var enum_members: EnumMemberDef[>..] = [];
    var structs:      StructDef[>..]     = [];
    var fields:       FieldDef[>..]      = [];

    // 1. Parse Constants
    let const_arr = json::member(root, "constants");
    if const_arr {
        match const_arr.val {
            Arr => {},
            _ => {
                kongor_log::error_actionable("'constants' field must be a JSON array", str("Inspect ", schema_path));
                exit(1);
            },
        }
        var c_elem = const_arr.first;
        while c_elem {
            let c_name = get_string_or(c_elem, "name", "");
            guard c_name.len > 0 else {
                kongor_log::error_actionable("Constant entry is missing non-empty 'name'", str("Inspect ", schema_path));
                exit(1);
            }
            let c_type = get_string_or(c_elem, "type", "uint32");
            let c_val, val_ok = get_value_literal(c_elem, "value", lit_scratch, "0");
            guard val_ok else {
                kongor_log::error_actionable(str("Invalid 'value' in constant '", c_name, "'"), str("Inspect ", schema_path));
                exit(1);
            }
            let c_desc = get_string_or(c_elem, "description", "");
            constants.push(ConstantDef {
                name:        c_name,
                val_type:    c_type,
                value:       c_val,
                description: c_desc,
            });
            c_elem .= c_elem.next;
        }
    }

    // 2. Parse Enums
    let enum_arr = json::member(root, "enums");
    if enum_arr {
        match enum_arr.val {
            Arr => {},
            _ => {
                kongor_log::error_actionable("'enums' field must be a JSON array", str("Inspect ", schema_path));
                exit(1);
            },
        }
        var e_elem = enum_arr.first;
        while e_elem {
            let e_name = get_string_or(e_elem, "name", "");
            guard e_name.len > 0 else {
                kongor_log::error_actionable("Enum entry is missing non-empty 'name'", str("Inspect ", schema_path));
                exit(1);
            }
            let e_under = get_string_or(e_elem, "underlying_type", "uint16");
            let e_desc = get_string_or(e_elem, "description", "");
            let first_mem = enum_members.len;
            var mem_count = 0;

            let vals_arr = json::member(e_elem, "values");
            if vals_arr {
                match vals_arr.val {
                    Arr => {},
                    _ => {
                        kongor_log::error_actionable(str("'values' in enum '", e_name, "' must be a JSON array"), str("Inspect ", schema_path));
                        exit(1);
                    },
                }
                var v_elem = vals_arr.first;
                while v_elem {
                    let m_name = get_string_or(v_elem, "name", "");
                    guard m_name.len > 0 else {
                        kongor_log::error_actionable(str("Enum member in '", e_name, "' missing 'name'"), str("Inspect ", schema_path));
                        exit(1);
                    }
                    let m_val, m_ok = get_value_literal(v_elem, "value", lit_scratch, "0");
                    guard m_ok else {
                        kongor_log::error_actionable(str("Invalid value in enum member '", e_name, ".", m_name, "'"), str("Inspect ", schema_path));
                        exit(1);
                    }
                    let m_desc = get_string_or(v_elem, "description", "");
                    enum_members.push(EnumMemberDef {
                        name:        m_name,
                        value:       m_val,
                        description: m_desc,
                    });
                    mem_count += 1;
                    v_elem .= v_elem.next;
                }
            }

            enums.push(EnumDef {
                name:            e_name,
                underlying_type: e_under,
                description:     e_desc,
                first_member:    first_mem,
                member_count:    mem_count,
            });
            e_elem .= e_elem.next;
        }
    }

    // 3. Parse Structs
    let struct_arr = json::member(root, "structs");
    if struct_arr {
        match struct_arr.val {
            Arr => {},
            _ => {
                kongor_log::error_actionable("'structs' field must be a JSON array", str("Inspect ", schema_path));
                exit(1);
            },
        }
        var s_elem = struct_arr.first;
        while s_elem {
            let s_name = get_string_or(s_elem, "name", "");
            guard s_name.len > 0 else {
                kongor_log::error_actionable("Struct entry is missing non-empty 'name'", str("Inspect ", schema_path));
                exit(1);
            }
            let exp_size, exp_ok = get_int_or(s_elem, "expected_size", 0);
            guard exp_ok && exp_size >= 0 else {
                kongor_log::error_actionable(str("Invalid 'expected_size' in struct '", s_name, "'"), str("Inspect ", schema_path));
                exit(1);
            }
            let s_desc = get_string_or(s_elem, "description", "");
            let first_f = fields.len;
            var f_count = 0;

            let fields_arr = json::member(s_elem, "fields");
            if fields_arr {
                match fields_arr.val {
                    Arr => {},
                    _ => {
                        kongor_log::error_actionable(str("'fields' in struct '", s_name, "' must be a JSON array"), str("Inspect ", schema_path));
                        exit(1);
                    },
                }
                var f_elem = fields_arr.first;
                while f_elem {
                    let f_name = get_string_or(f_elem, "name", "");
                    let f_type = get_string_or(f_elem, "type", "");
                    guard f_name.len > 0 && f_type.len > 0 else {
                        kongor_log::error_actionable(str("Field in struct '", s_name, "' missing 'name' or 'type'"), str("Inspect ", schema_path));
                        exit(1);
                    }
                    let f_desc = get_string_or(f_elem, "description", "");
                    let is_arr = f_type == "array";
                    let elem_type: const u8[:] = if is_arr { get_string_or(f_elem, "element_type", "uint8") } else { "" };
                    var arr_cnt = 1;
                    if is_arr {
                        let parsed_cnt, cnt_ok = get_int_or(f_elem, "count", 1);
                        guard cnt_ok && parsed_cnt > 0 else {
                            kongor_log::error_actionable(
                                str("Invalid array count in field '", s_name, ".", f_name, "'"),
                                str("Inspect ", schema_path),
                            );
                            exit(1);
                        }
                        arr_cnt = parsed_cnt;
                    }

                    fields.push(FieldDef {
                        name:         f_name,
                        field_type:   f_type,
                        description:  f_desc,
                        is_array:     is_arr,
                        element_type: elem_type,
                        array_count:  arr_cnt,
                        offset:       0,
                        size:         0,
                    });
                    f_count += 1;
                    f_elem .= f_elem.next;
                }
            }

            structs.push(StructDef {
                name:            s_name,
                expected_size:   exp_size,
                calculated_size: 0,
                description:     s_desc,
                first_field:     first_f,
                field_count:     f_count,
                first_dep:       0,
                dep_count:       0,
                visit_state:     0,
            });
            s_elem .= s_elem.next;
        }
    }

    // 4. Build Dependency Edges
    var dep_edges: i64[>..] = [];
    for s_idx in structs.len {
        let first_d = dep_edges.len;
        var d_count = 0;
        let f_start = structs[s_idx].first_field;
        let f_len = structs[s_idx].field_count;
        for f_off in f_len {
            let field = fields[f_start + f_off];
            let target_type = if field.is_array { field.element_type } else { field.field_type };
            let target_idx = find_struct_index(structs, target_type);
            if target_idx >= 0 {
                if target_idx == s_idx {
                    kongor_log::error_actionable(
                        str("Self-referential struct cycle detected in '", structs[s_idx].name, "'"),
                        str("Break cycle in ", schema_path),
                    );
                    exit(1);
                }
                dep_edges.push(target_idx);
                d_count += 1;
            }
        }
        structs[s_idx].first_dep = first_d;
        structs[s_idx].dep_count = d_count;
    }

    // 5. Iterative Topological Sort (Bounded Data Stack, Cycle-Safe)
    var sorted_indices: i64[>..]       = [];
    var dfs_stack:      DfsFrame[>..<] = [];
    for root_idx in structs.len {
        if structs[root_idx].visit_state == 2 { continue; }
        structs[root_idx].visit_state = 1;
        dfs_stack.push(DfsFrame { struct_idx: root_idx, next_dep: 0 });

        while dfs_stack.len > 0 {
            let top = dfs_stack.len - 1;
            let cur_idx = dfs_stack[top].struct_idx;
            let dep_pos = dfs_stack[top].next_dep;
            if dep_pos < structs[cur_idx].dep_count {
                dfs_stack[top].next_dep = dep_pos + 1;
                let child_idx = dep_edges[structs[cur_idx].first_dep + dep_pos];
                if structs[child_idx].visit_state == 1 {
                    kongor_log::error_actionable(
                        str("Cyclic dependency detected involving struct '", structs[child_idx].name, "'"),
                        str("Inspect struct dependencies in ", schema_path),
                    );
                    exit(1);
                }
                if structs[child_idx].visit_state == 0 {
                    structs[child_idx].visit_state = 1;
                    dfs_stack.push(DfsFrame { struct_idx: child_idx, next_dep: 0 });
                }
            } else {
                structs[cur_idx].visit_state = 2;
                sorted_indices.push(cur_idx);
                dfs_stack.pop();
            }
        }
    }

    // 6. Calculate Byte Offsets and Sizes in Topological Order
    for s_idx in sorted_indices {
        var offset = 0;
        let f_start = structs[s_idx].first_field;
        let f_len = structs[s_idx].field_count;
        for f_off in f_len {
            let f_idx = f_start + f_off;
            let eval_type = if fields[f_idx].is_array { fields[f_idx].element_type } else { fields[f_idx].field_type };
            var elem_size = primitive_type_size(eval_type);
            if elem_size == 0 {
                let emb_idx = find_struct_index(structs, eval_type);
                if emb_idx >= 0 {
                    elem_size = structs[emb_idx].calculated_size;
                } else {
                    kongor_log::error_actionable(
                        str("Unknown type '", eval_type, "' in field '", structs[s_idx].name, ".", fields[f_idx].name, "'"),
                        str("Define struct or primitive type in ", schema_path),
                    );
                    exit(1);
                }
            }
            let total_field_size = if fields[f_idx].is_array { elem_size * fields[f_idx].array_count } else { elem_size };
            fields[f_idx].size = total_field_size;
            fields[f_idx].offset = offset;
            offset += total_field_size;
        }
        structs[s_idx].calculated_size = offset;

        let exp = structs[s_idx].expected_size;
        if exp > 0 && offset != exp {
            kongor_log::error_actionable(
                str("Size mismatch for struct '", structs[s_idx].name, "': schema expected ", exp, " bytes, calculated ", offset, " bytes"),
                str("Update expected_size or field layout in ", schema_path),
            );
            exit(1);
        }

        if verbose {
            print(
                "[KONGOR-METAGEN] Struct ", structs[s_idx].name, ": ",
                offset, " bytes (expected ", exp, "), ", f_len, " fields",
            );
        }
    }

    // 7. Emit Synchronized C++, C#, and Lua Buffers
    var cpp_code: u8[>..] = [];
    var cs_code:  u8[>..] = [];
    var lua_code: u8[>..] = [];

    generate_cpp_header(cpp_code, constants, enums, enum_members, structs, fields, sorted_indices);
    generate_cs_file(cs_code, constants, enums, enum_members, structs, fields, sorted_indices);
    generate_lua_file(lua_code, constants, enums, enum_members, structs, fields, sorted_indices);

    kongor_log::debug(str(
        "Generated buffers: C++=", cpp_code.len, " B, C#=", cs_code.len, " B, Lua=", lua_code.len, " B",
    ));

    if check_only {
        print("[KONGOR-METAGEN] Running in --check parity mode...");
        var cur_cpp: u8[>..] = [];
        var cur_cs:  u8[>..] = [];
        var cur_lua: u8[>..] = [];

        let cpp_match = read_file(cpp_out_path, cur_cpp) && cur_cpp == cpp_code;
        let cs_match = read_file(cs_out_path, cur_cs) && cur_cs == cs_code;
        let lua_match = read_file(lua_out_path, cur_lua) && cur_lua == lua_code;

        if !cpp_match {
            kongor_log::error_actionable(
                str("[PARITY-DRIFT] C++ header drift detected: ", cpp_out_path),
                "Run powershell.exe -File scripts/re/generate_protocol.ps1",
            );
        }
        if !cs_match {
            kongor_log::error_actionable(
                str("[PARITY-DRIFT] C# contract drift detected: ", cs_out_path),
                "Run powershell.exe -File scripts/re/generate_protocol.ps1",
            );
        }
        if !lua_match {
            kongor_log::error_actionable(
                str("[PARITY-DRIFT] Lua script drift detected: ", lua_out_path),
                "Run powershell.exe -File scripts/re/generate_protocol.ps1",
            );
        }

        if cpp_match && cs_match && lua_match {
            print("[PARITY-SUCCESS] All generated protocol artifacts match schema with 0 drift!");
            return;
        }
        exit(1);
    }

    print("[KONGOR-METAGEN] Emitting C++ Header -> ", cpp_out_path);
    if !write_output_file(cpp_out_path, cpp_code) { exit(1); }

    print("[KONGOR-METAGEN] Emitting C# Contracts -> ", cs_out_path);
    if !write_output_file(cs_out_path, cs_code) { exit(1); }

    print("[KONGOR-METAGEN] Emitting Lua Lookup  -> ", lua_out_path);
    if !write_output_file(lua_out_path, lua_code) { exit(1); }

    let elapsed_ms = (clock_ns() - t0_ns) / 1000000.0;
    var summary_line: u8[>..] = [];
    format(summary_line, "[KONGOR-METAGEN] Generation Complete: ", constants.len, " constants, ", sorted_indices.len, " structs generated in ");
    format_flt(summary_line, elapsed_ms, 2);
    summary_line.append(" ms (Zero Heap Churn).");
    print(summary_line);
    kongor_log::info(summary_line);
}
