// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file main.cpp
 * @brief Project Kongor - Native Cross-Language Protocol & Telemetry Metagen CLI
 * 
 * Architecture: Adapted from RAD Debugger (Ryan Fleury / Allen Webster)
 * Backed by KongorArena and String8 for sub-millisecond, zero-heap code generation.
 * Parses declarative protocol schemas and generates synchronized C++, C#, and Lua bindings.
 */

#include "kongor_arena.h"
#include "kongor_string.h"

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <stdbool.h>
#include <chrono>
#include <filesystem>
#include <string>

// ============================================================================
// ZERO-HEAP ARENA JSON PARSER
// ============================================================================

enum JsonValueType {
    JSON_NULL,
    JSON_BOOL,
    JSON_NUMBER,
    JSON_STRING,
    JSON_ARRAY,
    JSON_OBJECT
};

struct JsonValue;

struct JsonMember {
    String8 key;
    JsonValue *value;
    JsonMember *next;
};

struct JsonValue {
    JsonValueType type;
    String8 string_val;
    double number_val;
    bool bool_val;
    // For arrays
    JsonValue *first_element;
    JsonValue *next_element;
    size_t array_count;
    // For objects
    JsonMember *first_member;
};

static void SkipWhitespaceAndComments(String8 input, size_t *pos) {
    while (*pos < input.size) {
        uint8_t c = input.str[*pos];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
            (*pos)++;
        } else if (c == '/' && *pos + 1 < input.size && input.str[*pos + 1] == '/') {
            // Line comment
            *pos += 2;
            while (*pos < input.size && input.str[*pos] != '\n') {
                (*pos)++;
            }
        } else {
            break;
        }
    }
}

static int ParseHexNibble(uint8_t c) {
    if (c >= '0' && c <= '9') return (int)(c - '0');
    if (c >= 'a' && c <= 'f') return (int)(c - 'a' + 10);
    if (c >= 'A' && c <= 'F') return (int)(c - 'A' + 10);
    return -1;
}

static bool ParseHex4(String8 input, size_t pos, uint32_t *outVal) {
    if (pos + 4 > input.size) return false;
    int d0 = ParseHexNibble(input.str[pos]);
    int d1 = ParseHexNibble(input.str[pos + 1]);
    int d2 = ParseHexNibble(input.str[pos + 2]);
    int d3 = ParseHexNibble(input.str[pos + 3]);
    if (d0 < 0 || d1 < 0 || d2 < 0 || d3 < 0) return false;
    *outVal = ((uint32_t)d0 << 12) | ((uint32_t)d1 << 8) | ((uint32_t)d2 << 4) | (uint32_t)d3;
    return true;
}

static size_t EncodeUtf8Codepoint(uint32_t cp, uint8_t *dst) {
    if (cp <= 0x7Fu) {
        dst[0] = (uint8_t)cp;
        return 1;
    } else if (cp <= 0x7FFu) {
        dst[0] = (uint8_t)(0xC0u | (cp >> 6));
        dst[1] = (uint8_t)(0x80u | (cp & 0x3Fu));
        return 2;
    } else if (cp <= 0xFFFFu) {
        dst[0] = (uint8_t)(0xE0u | (cp >> 12));
        dst[1] = (uint8_t)(0x80u | ((cp >> 6) & 0x3Fu));
        dst[2] = (uint8_t)(0x80u | (cp & 0x3Fu));
        return 3;
    } else if (cp <= 0x10FFFFu) {
        dst[0] = (uint8_t)(0xF0u | (cp >> 18));
        dst[1] = (uint8_t)(0x80u | ((cp >> 12) & 0x3Fu));
        dst[2] = (uint8_t)(0x80u | ((cp >> 6) & 0x3Fu));
        dst[3] = (uint8_t)(0x80u | (cp & 0x3Fu));
        return 4;
    }
    return 0;
}

static String8 ParseJsonString(KongorArena *arena, String8 input, size_t *pos, bool *parseError) {
    if (*pos >= input.size || input.str[*pos] != '"') {
        *parseError = true;
        return str8_zero();
    }
    (*pos)++; // Skip leading quote
    size_t start = *pos;

    // Scan to closing quote; unescaped UTF-8 output is strictly <= raw span bytes
    size_t scan = start;
    while (scan < input.size && input.str[scan] != '"') {
        if (input.str[scan] == '\\') {
            if (scan + 1 >= input.size) {
                *parseError = true;
                return str8_zero();
            }
            scan += 2;
        } else {
            if (input.str[scan] < 0x20u) {
                *parseError = true;
                return str8_zero();
            }
            scan++;
        }
    }
    if (scan >= input.size || input.str[scan] != '"') {
        *parseError = true;
        return str8_zero();
    }

    size_t maxLen = scan - start;
    String8 result = str8_alloc(arena, maxLen);
    size_t out_idx = 0;

    while (*pos < input.size && input.str[*pos] != '"') {
        if (input.str[*pos] == '\\') {
            (*pos)++;
            if (*pos >= input.size) {
                *parseError = true;
                return str8_zero();
            }
            uint8_t esc = input.str[*pos];
            (*pos)++;
            if (esc == 'n') result.str[out_idx++] = '\n';
            else if (esc == 't') result.str[out_idx++] = '\t';
            else if (esc == 'r') result.str[out_idx++] = '\r';
            else if (esc == 'b') result.str[out_idx++] = '\b';
            else if (esc == 'f') result.str[out_idx++] = '\f';
            else if (esc == '"') result.str[out_idx++] = '"';
            else if (esc == '\\') result.str[out_idx++] = '\\';
            else if (esc == '/') result.str[out_idx++] = '/';
            else if (esc == 'u') {
                uint32_t cp = 0;
                if (!ParseHex4(input, *pos, &cp)) {
                    *parseError = true;
                    return str8_zero();
                }
                *pos += 4;
                if (cp >= 0xD800u && cp <= 0xDBFFu) {
                    // UTF-16 high surrogate must be paired with \uDC00..\uDFFF low surrogate
                    if (*pos + 6 <= input.size && input.str[*pos] == '\\' && input.str[*pos + 1] == 'u') {
                        uint32_t low = 0;
                        if (ParseHex4(input, *pos + 2, &low) && low >= 0xDC00u && low <= 0xDFFFu) {
                            cp = 0x10000u + (((cp - 0xD800u) << 10) | (low - 0xDC00u));
                            *pos += 6;
                        } else {
                            *parseError = true;
                            return str8_zero();
                        }
                    } else {
                        *parseError = true;
                        return str8_zero();
                    }
                } else if (cp >= 0xDC00u && cp <= 0xDFFFu) {
                    *parseError = true;
                    return str8_zero();
                }
                size_t encoded = EncodeUtf8Codepoint(cp, result.str + out_idx);
                if (encoded == 0) {
                    *parseError = true;
                    return str8_zero();
                }
                out_idx += encoded;
            } else {
                *parseError = true;
                return str8_zero();
            }
        } else {
            result.str[out_idx++] = input.str[(*pos)++];
        }
    }
    if (*pos < input.size && input.str[*pos] == '"') {
        (*pos)++; // Skip trailing quote
    } else {
        *parseError = true;
        return str8_zero();
    }
    result.size = out_idx;
    result.str[out_idx] = '\0';
    return result;
}

static JsonValue *ParseJsonValue(KongorArena *arena, String8 input, size_t *pos, bool *parseError);

static JsonValue *ParseJsonObject(KongorArena *arena, String8 input, size_t *pos, bool *parseError) {
    if (*pos >= input.size || input.str[*pos] != '{') {
        *parseError = true;
        return NULL;
    }
    (*pos)++; // Skip '{'

    JsonValue *obj = kongor_arena_push_struct(arena, JsonValue);
    obj->type = JSON_OBJECT;
    JsonMember *tail = NULL;
    bool closed = false;

    while (*pos < input.size && !(*parseError)) {
        SkipWhitespaceAndComments(input, pos);
        if (*pos < input.size && input.str[*pos] == '}') {
            (*pos)++;
            closed = true;
            break;
        }

        String8 key = ParseJsonString(arena, input, pos, parseError);
        if (*parseError) return NULL;

        SkipWhitespaceAndComments(input, pos);
        if (*pos >= input.size || input.str[*pos] != ':') {
            *parseError = true;
            return NULL;
        }
        (*pos)++;

        SkipWhitespaceAndComments(input, pos);
        JsonValue *val = ParseJsonValue(arena, input, pos, parseError);
        if (*parseError || !val) {
            *parseError = true;
            return NULL;
        }

        JsonMember *mem = kongor_arena_push_struct(arena, JsonMember);
        mem->key = key;
        mem->value = val;
        mem->next = NULL;

        if (!obj->first_member) {
            obj->first_member = mem;
            tail = mem;
        } else {
            tail->next = mem;
            tail = mem;
        }

        SkipWhitespaceAndComments(input, pos);
        if (*pos < input.size && input.str[*pos] == ',') {
            (*pos)++;
        } else if (*pos < input.size && input.str[*pos] == '}') {
            // Handled at top of loop
        } else {
            *parseError = true;
            return NULL;
        }
    }

    if (!closed) {
        *parseError = true;
        return NULL;
    }
    return obj;
}

static JsonValue *ParseJsonArray(KongorArena *arena, String8 input, size_t *pos, bool *parseError) {
    if (*pos >= input.size || input.str[*pos] != '[') {
        *parseError = true;
        return NULL;
    }
    (*pos)++; // Skip '['

    JsonValue *arr = kongor_arena_push_struct(arena, JsonValue);
    arr->type = JSON_ARRAY;
    JsonValue *tail = NULL;
    bool closed = false;

    while (*pos < input.size && !(*parseError)) {
        SkipWhitespaceAndComments(input, pos);
        if (*pos < input.size && input.str[*pos] == ']') {
            (*pos)++;
            closed = true;
            break;
        }

        JsonValue *elem = ParseJsonValue(arena, input, pos, parseError);
        if (*parseError || !elem) {
            *parseError = true;
            return NULL;
        }
        arr->array_count++;
        if (!arr->first_element) {
            arr->first_element = elem;
            tail = elem;
        } else {
            tail->next_element = elem;
            tail = elem;
        }

        SkipWhitespaceAndComments(input, pos);
        if (*pos < input.size && input.str[*pos] == ',') {
            (*pos)++;
        } else if (*pos < input.size && input.str[*pos] == ']') {
            // Handled at top of loop
        } else {
            *parseError = true;
            return NULL;
        }
    }

    if (!closed) {
        *parseError = true;
        return NULL;
    }
    return arr;
}

static JsonValue *ParseJsonValue(KongorArena *arena, String8 input, size_t *pos, bool *parseError) {
    SkipWhitespaceAndComments(input, pos);
    if (*pos >= input.size) {
        *parseError = true;
        return NULL;
    }

    uint8_t c = input.str[*pos];
    if (c == '{') {
        return ParseJsonObject(arena, input, pos, parseError);
    } else if (c == '[') {
        return ParseJsonArray(arena, input, pos, parseError);
    } else if (c == '"') {
        String8 s = ParseJsonString(arena, input, pos, parseError);
        if (*parseError) return NULL;
        JsonValue *val = kongor_arena_push_struct(arena, JsonValue);
        val->type = JSON_STRING;
        val->string_val = s;
        return val;
    } else if ((c >= '0' && c <= '9') || c == '-') {
        size_t start = *pos;
        while (*pos < input.size) {
            uint8_t nc = input.str[*pos];
            if ((nc >= '0' && nc <= '9') || nc == '-' || nc == '+' || nc == '.' || nc == 'e' || nc == 'E' || nc == 'x' || nc == 'X' || (nc >= 'a' && nc <= 'f') || (nc >= 'A' && nc <= 'F')) {
                (*pos)++;
            } else {
                break;
            }
        }
        String8 num_slice = str8_substr(input, start, *pos);
        if (num_slice.size == 0 || (num_slice.size == 1 && num_slice.str[0] == '-')) {
            *parseError = true;
            return NULL;
        }
        JsonValue *val = kongor_arena_push_struct(arena, JsonValue);
        val->type = JSON_NUMBER;
        val->string_val = num_slice;
        char tempBuf[64];
        size_t copyLen = num_slice.size < 63 ? num_slice.size : 63;
        memcpy(tempBuf, num_slice.str, copyLen);
        tempBuf[copyLen] = '\0';
        char *endPtr = NULL;
        if (copyLen > 2 && (tempBuf[1] == 'x' || tempBuf[1] == 'X')) {
            val->number_val = (double)strtoull(tempBuf, &endPtr, 16);
        } else {
            val->number_val = strtod(tempBuf, &endPtr);
        }
        if (!endPtr || *endPtr != '\0') {
            *parseError = true;
            return NULL;
        }
        return val;
    } else if (c == 't') {
        if (*pos + 4 <= input.size && memcmp(input.str + *pos, "true", 4) == 0) {
            *pos += 4;
            JsonValue *val = kongor_arena_push_struct(arena, JsonValue);
            val->type = JSON_BOOL;
            val->bool_val = true;
            return val;
        }
        *parseError = true;
        return NULL;
    } else if (c == 'f') {
        if (*pos + 5 <= input.size && memcmp(input.str + *pos, "false", 5) == 0) {
            *pos += 5;
            JsonValue *val = kongor_arena_push_struct(arena, JsonValue);
            val->type = JSON_BOOL;
            val->bool_val = false;
            return val;
        }
        *parseError = true;
        return NULL;
    } else if (c == 'n') {
        if (*pos + 4 <= input.size && memcmp(input.str + *pos, "null", 4) == 0) {
            *pos += 4;
            JsonValue *val = kongor_arena_push_struct(arena, JsonValue);
            val->type = JSON_NULL;
            return val;
        }
        *parseError = true;
        return NULL;
    }
    *parseError = true;
    return NULL;
}

static JsonValue *JsonObjectGet(JsonValue *obj, const char *key) {
    if (!obj || obj->type != JSON_OBJECT) return NULL;
    String8 keyStr = str8_from_cstr(key);
    for (JsonMember *m = obj->first_member; m != NULL; m = m->next) {
        if (str8_equal(m->key, keyStr)) {
            return m->value;
        }
    }
    return NULL;
}

static String8 JsonGetString(JsonValue *val, const char *default_val = "") {
    if (!val || val->type != JSON_STRING) return str8_from_cstr(default_val);
    return val->string_val;
}

static double JsonGetNumber(JsonValue *val, double default_val = 0.0) {
    if (!val || val->type != JSON_NUMBER) return default_val;
    return val->number_val;
}

// ============================================================================
// PROTOCOL METAMODEL DATA STRUCTURES
// ============================================================================

struct ConstantDef {
    String8 name;
    String8 type;
    String8 value;
    String8 description;
};

struct EnumMemberDef {
    String8 name;
    String8 value;
    String8 description;
};

struct EnumDef {
    String8 name;
    String8 underlying_type;
    String8 description;
    EnumMemberDef *values;
    size_t value_count;
};

struct FieldDef {
    String8 name;
    String8 type;
    String8 description;
    bool is_array;
    String8 element_type;
    uint32_t array_count;
    uint32_t offset;
    uint32_t size;
};

struct StructDef {
    String8 name;
    uint32_t expected_size;
    uint32_t calculated_size;
    String8 description;
    FieldDef *fields;
    size_t field_count;
    
    // Topological sorting
    size_t dep_count;
    StructDef **deps;
    int visit_state; // 0 = unvisited, 1 = visiting (cycle detection), 2 = visited
};

typedef StructDef* StructDefPtr;

struct ProtocolModel {
    String8 title;
    String8 version;
    String8 description;
    ConstantDef *constants;
    size_t constant_count;
    EnumDef *enums;
    size_t enum_count;
    StructDef *structs;
    size_t struct_count;
    StructDef **sorted_structs;
    size_t sorted_count;
};

static uint32_t GetPrimitiveTypeSize(String8 type) {
    if (str8_equal(type, str8_lit("uint8")) || str8_equal(type, str8_lit("int8"))) return 1;
    if (str8_equal(type, str8_lit("uint16")) || str8_equal(type, str8_lit("int16"))) return 2;
    if (str8_equal(type, str8_lit("uint32")) || str8_equal(type, str8_lit("int32"))) return 4;
    if (str8_equal(type, str8_lit("uint64")) || str8_equal(type, str8_lit("int64"))) return 8;
    if (str8_equal(type, str8_lit("float32")) || str8_equal(type, str8_lit("float"))) return 4;
    if (str8_equal(type, str8_lit("float64")) || str8_equal(type, str8_lit("double"))) return 8;
    return 0;
}

static StructDef *FindStruct(ProtocolModel *model, String8 name) {
    for (size_t i = 0; i < model->struct_count; i++) {
        if (str8_equal(model->structs[i].name, name)) {
            return &model->structs[i];
        }
    }
    return NULL;
}

static bool TopologicalSortDFS(StructDef *s, StructDef **out_sorted, size_t *out_count) {
    if (s->visit_state == 1) {
        fprintf(stderr,
                "[ERROR] Cyclic dependency detected involving struct '%.*s'! | Diagnose/Fix: Break recursive struct embedding in schema JSON and run 'powershell.exe -File scripts/re/test_metagen_parity.ps1'\n",
                (int)s->name.size, s->name.str);
        return false;
    }
    if (s->visit_state == 2) {
        return true;
    }
    s->visit_state = 1; // Visiting
    for (size_t i = 0; i < s->dep_count; i++) {
        if (!TopologicalSortDFS(s->deps[i], out_sorted, out_count)) {
            return false;
        }
    }
    s->visit_state = 2; // Visited
    out_sorted[(*out_count)++] = s;
    return true;
}

// ============================================================================
// CODE GENERATORS (C++, C#, LUA)
// ============================================================================

static String8 ToCppType(String8 type) {
    if (str8_equal(type, str8_lit("uint8"))) return str8_lit("uint8_t");
    if (str8_equal(type, str8_lit("uint16"))) return str8_lit("uint16_t");
    if (str8_equal(type, str8_lit("uint32"))) return str8_lit("uint32_t");
    if (str8_equal(type, str8_lit("uint64"))) return str8_lit("uint64_t");
    if (str8_equal(type, str8_lit("int8"))) return str8_lit("int8_t");
    if (str8_equal(type, str8_lit("int16"))) return str8_lit("int16_t");
    if (str8_equal(type, str8_lit("int32"))) return str8_lit("int32_t");
    if (str8_equal(type, str8_lit("int64"))) return str8_lit("int64_t");
    if (str8_equal(type, str8_lit("float32"))) return str8_lit("float");
    if (str8_equal(type, str8_lit("float64"))) return str8_lit("double");
    return type; // Struct name
}

static String8 ToCsType(String8 type) {
    if (str8_equal(type, str8_lit("uint8"))) return str8_lit("byte");
    if (str8_equal(type, str8_lit("uint16"))) return str8_lit("ushort");
    if (str8_equal(type, str8_lit("uint32"))) return str8_lit("uint");
    if (str8_equal(type, str8_lit("uint64"))) return str8_lit("ulong");
    if (str8_equal(type, str8_lit("int8"))) return str8_lit("sbyte");
    if (str8_equal(type, str8_lit("int16"))) return str8_lit("short");
    if (str8_equal(type, str8_lit("int32"))) return str8_lit("int");
    if (str8_equal(type, str8_lit("int64"))) return str8_lit("long");
    if (str8_equal(type, str8_lit("float32"))) return str8_lit("float");
    if (str8_equal(type, str8_lit("float64"))) return str8_lit("double");
    return type; // Struct name
}

static String8 GenerateCppHeader(KongorArena *arena, ProtocolModel *model) {
    String8List list = {0};

    str8_list_push(arena, &list, str8_lit("// <auto-generated>\n"));
    str8_list_push(arena, &list, str8_lit("// Generated by kongor-metagen from data/schemas/protocol.json\n"));
    str8_list_push(arena, &list, str8_lit("// DO NOT MODIFY DIRECTLY\n"));
    str8_list_push(arena, &list, str8_lit("// </auto-generated>\n\n"));

    str8_list_push(arena, &list, str8_lit("#ifndef KONGOR_PROTOCOL_H\n"));
    str8_list_push(arena, &list, str8_lit("#define KONGOR_PROTOCOL_H\n\n"));
    str8_list_push(arena, &list, str8_lit("#include <stdint.h>\n"));
    str8_list_push(arena, &list, str8_lit("#include <stdbool.h>\n"));
    str8_list_push(arena, &list, str8_lit("#include <stddef.h>\n\n"));

    str8_list_push(arena, &list, str8_lit("#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"));

    str8_list_push(arena, &list, str8_lit("// ============================================================================\n"));
    str8_list_push(arena, &list, str8_lit("// PROTOCOL CONSTANTS\n"));
    str8_list_push(arena, &list, str8_lit("// ============================================================================\n\n"));

    for (size_t i = 0; i < model->constant_count; i++) {
        ConstantDef *c = &model->constants[i];
        str8_list_pushf(arena, &list, "/* %.*s */\n", (int)c->description.size, c->description.str);
        str8_list_pushf(arena, &list, "#define %.*s %.*s\n\n",
                        (int)c->name.size, c->name.str,
                        (int)c->value.size, c->value.str);
    }

    str8_list_push(arena, &list, str8_lit("// ============================================================================\n"));
    str8_list_push(arena, &list, str8_lit("// PROTOCOL STRUCTURES (PACKED 1-BYTE ALIGNMENT)\n"));
    str8_list_push(arena, &list, str8_lit("// ============================================================================\n\n"));

    str8_list_push(arena, &list, str8_lit("#pragma pack(push, 1)\n\n"));

    for (size_t i = 0; i < model->sorted_count; i++) {
        StructDef *s = model->sorted_structs[i];
        str8_list_pushf(arena, &list, "/**\n * @brief %.*s\n */\n", (int)s->description.size, s->description.str);
        str8_list_pushf(arena, &list, "typedef struct %.*s {\n", (int)s->name.size, s->name.str);

        for (size_t f = 0; f < s->field_count; f++) {
            FieldDef *field = &s->fields[f];
            if (field->is_array) {
                String8 elemType = ToCppType(field->element_type);
                str8_list_pushf(arena, &list, "    %.*s %.*s[%u]; /* offset %u, size %u bytes | %.*s */\n",
                                (int)elemType.size, elemType.str,
                                (int)field->name.size, field->name.str,
                                field->array_count, field->offset, field->size,
                                (int)field->description.size, field->description.str);
            } else {
                String8 fieldType = ToCppType(field->type);
                str8_list_pushf(arena, &list, "    %.*s %.*s; /* offset %u, size %u bytes | %.*s */\n",
                                (int)fieldType.size, fieldType.str,
                                (int)field->name.size, field->name.str,
                                field->offset, field->size,
                                (int)field->description.size, field->description.str);
            }
        }
        str8_list_pushf(arena, &list, "} %.*s;\n\n", (int)s->name.size, s->name.str);

        // Compile-time static assertions for C++ consumers
        str8_list_push(arena, &list, str8_lit("#ifdef __cplusplus\n"));
        str8_list_pushf(arena, &list, "static_assert(sizeof(%.*s) == %u, \"%.*s size mismatch\");\n",
                        (int)s->name.size, s->name.str, s->calculated_size, (int)s->name.size, s->name.str);
        for (size_t f = 0; f < s->field_count; f++) {
            FieldDef *field = &s->fields[f];
            str8_list_pushf(arena, &list, "static_assert(offsetof(%.*s, %.*s) == %u, \"%.*s.%.*s offset mismatch\");\n",
                            (int)s->name.size, s->name.str,
                            (int)field->name.size, field->name.str,
                            field->offset,
                            (int)s->name.size, s->name.str,
                            (int)field->name.size, field->name.str);
        }
        str8_list_push(arena, &list, str8_lit("#endif\n\n"));
    }

    str8_list_push(arena, &list, str8_lit("#pragma pack(pop)\n\n"));
    str8_list_push(arena, &list, str8_lit("#ifdef __cplusplus\n}\n#endif\n\n"));

    if (model->enum_count > 0) {
        str8_list_push(arena, &list, str8_lit("#ifdef __cplusplus\n"));
        str8_list_push(arena, &list, str8_lit("// ============================================================================\n"));
        str8_list_push(arena, &list, str8_lit("// PROTOCOL STRONGLY-TYPED ENUMS\n"));
        str8_list_push(arena, &list, str8_lit("// ============================================================================\n\n"));
        for (size_t i = 0; i < model->enum_count; i++) {
            EnumDef *e = &model->enums[i];
            String8 cppUnderlying = ToCppType(e->underlying_type);
            str8_list_pushf(arena, &list, "/**\n * @brief %.*s\n */\n", (int)e->description.size, e->description.str);
            str8_list_pushf(arena, &list, "enum class %.*s : %.*s {\n",
                            (int)e->name.size, e->name.str,
                            (int)cppUnderlying.size, cppUnderlying.str);
            for (size_t v = 0; v < e->value_count; v++) {
                EnumMemberDef *mem = &e->values[v];
                str8_list_pushf(arena, &list, "    %.*s = %.*s, /* %.*s */\n",
                                (int)mem->name.size, mem->name.str,
                                (int)mem->value.size, mem->value.str,
                                (int)mem->description.size, mem->description.str);
            }
            str8_list_push(arena, &list, str8_lit("};\n\n"));
        }
        str8_list_push(arena, &list, str8_lit("#endif // __cplusplus\n\n"));
    }

    str8_list_push(arena, &list, str8_lit("#endif // KONGOR_PROTOCOL_H\n"));

    return str8_list_join(arena, &list, str8_zero());
}

static String8 GenerateCsFile(KongorArena *arena, ProtocolModel *model) {
    String8List list = {0};

    str8_list_push(arena, &list, str8_lit("// <auto-generated>\n"));
    str8_list_push(arena, &list, str8_lit("// Generated by kongor-metagen from data/schemas/protocol.json\n"));
    str8_list_push(arena, &list, str8_lit("// DO NOT MODIFY DIRECTLY\n"));
    str8_list_push(arena, &list, str8_lit("// </auto-generated>\n\n"));

    str8_list_push(arena, &list, str8_lit("#nullable enable\n"));
    str8_list_push(arena, &list, str8_lit("using System;\n"));
    str8_list_push(arena, &list, str8_lit("using System.Runtime.InteropServices;\n\n"));

    str8_list_push(arena, &list, str8_lit("namespace ProjectKongor.Contracts.Generated\n{\n"));

    str8_list_push(arena, &list, str8_lit("    /// <summary>\n    /// Authoritative Engine and Fast-Bus Protocol Constants\n    /// </summary>\n"));
    str8_list_push(arena, &list, str8_lit("    public static class KongorConstants\n    {\n"));
    for (size_t i = 0; i < model->constant_count; i++) {
        ConstantDef *c = &model->constants[i];
        String8 csType = ToCsType(c->type);
        str8_list_pushf(arena, &list, "        /// <summary>%.*s</summary>\n", (int)c->description.size, c->description.str);
        str8_list_pushf(arena, &list, "        public const %.*s %.*s = %.*s;\n\n",
                        (int)csType.size, csType.str,
                        (int)c->name.size, c->name.str,
                        (int)c->value.size, c->value.str);
    }
    str8_list_push(arena, &list, str8_lit("    }\n\n"));

    for (size_t i = 0; i < model->enum_count; i++) {
        EnumDef *e = &model->enums[i];
        String8 csUnderlying = ToCsType(e->underlying_type);
        str8_list_pushf(arena, &list, "    /// <summary>\n    /// %.*s\n    /// </summary>\n", (int)e->description.size, e->description.str);
        str8_list_pushf(arena, &list, "    public enum %.*s : %.*s\n    {\n",
                        (int)e->name.size, e->name.str,
                        (int)csUnderlying.size, csUnderlying.str);
        for (size_t v = 0; v < e->value_count; v++) {
            EnumMemberDef *mem = &e->values[v];
            str8_list_pushf(arena, &list, "        /// <summary>%.*s</summary>\n", (int)mem->description.size, mem->description.str);
            str8_list_pushf(arena, &list, "        %.*s = %.*s,\n\n",
                            (int)mem->name.size, mem->name.str,
                            (int)mem->value.size, mem->value.str);
        }
        str8_list_push(arena, &list, str8_lit("    }\n\n"));
    }

    for (size_t i = 0; i < model->sorted_count; i++) {
        StructDef *s = model->sorted_structs[i];
        str8_list_pushf(arena, &list, "    /// <summary>\n    /// %.*s\n    /// </summary>\n", (int)s->description.size, s->description.str);
        str8_list_push(arena, &list, str8_lit("    [StructLayout(LayoutKind.Sequential, Pack = 1)]\n"));
        str8_list_pushf(arena, &list, "    public unsafe struct %.*s\n    {\n", (int)s->name.size, s->name.str);
        str8_list_pushf(arena, &list, "        public const int SizeInBytes = %u;\n\n", s->calculated_size);

        for (size_t f = 0; f < s->field_count; f++) {
            FieldDef *field = &s->fields[f];
            str8_list_pushf(arena, &list, "        /// <summary>%.*s (offset %u, size %u bytes)</summary>\n",
                            (int)field->description.size, field->description.str, field->offset, field->size);
            if (field->is_array) {
                String8 elemType = ToCsType(field->element_type);
                str8_list_pushf(arena, &list, "        public fixed %.*s %.*s[%u];\n\n",
                                (int)elemType.size, elemType.str,
                                (int)field->name.size, field->name.str,
                                field->array_count);
            } else {
                String8 fieldType = ToCsType(field->type);
                str8_list_pushf(arena, &list, "        public %.*s %.*s;\n\n",
                                (int)fieldType.size, fieldType.str,
                                (int)field->name.size, field->name.str);
            }
        }
        str8_list_push(arena, &list, str8_lit("    }\n\n"));
    }

    str8_list_push(arena, &list, str8_lit("}\n"));

    return str8_list_join(arena, &list, str8_zero());
}

static String8 GenerateLuaFile(KongorArena *arena, ProtocolModel *model) {
    String8List list = {0};

    str8_list_push(arena, &list, str8_lit("-- <auto-generated>\n"));
    str8_list_push(arena, &list, str8_lit("-- Generated by kongor-metagen from data/schemas/protocol.json\n"));
    str8_list_push(arena, &list, str8_lit("-- DO NOT MODIFY DIRECTLY\n"));
    str8_list_push(arena, &list, str8_lit("-- </auto-generated>\n\n"));

    str8_list_push(arena, &list, str8_lit("KongorProtocol = KongorProtocol or {}\n\n"));

    str8_list_push(arena, &list, str8_lit("KongorProtocol.Constants = {\n"));
    for (size_t i = 0; i < model->constant_count; i++) {
        ConstantDef *c = &model->constants[i];
        str8_list_pushf(arena, &list, "    %.*s = %.*s, -- %.*s\n",
                        (int)c->name.size, c->name.str,
                        (int)c->value.size, c->value.str,
                        (int)c->description.size, c->description.str);
    }
    str8_list_push(arena, &list, str8_lit("}\n\n"));

    if (model->enum_count > 0) {
        str8_list_push(arena, &list, str8_lit("KongorProtocol.Enums = {\n"));
        for (size_t i = 0; i < model->enum_count; i++) {
            EnumDef *e = &model->enums[i];
            str8_list_pushf(arena, &list, "    %.*s = {\n", (int)e->name.size, e->name.str);
            for (size_t v = 0; v < e->value_count; v++) {
                EnumMemberDef *mem = &e->values[v];
                str8_list_pushf(arena, &list, "        %.*s = %.*s, -- %.*s\n",
                                (int)mem->name.size, mem->name.str,
                                (int)mem->value.size, mem->value.str,
                                (int)mem->description.size, mem->description.str);
            }
            str8_list_push(arena, &list, str8_lit("    },\n"));
        }
        str8_list_push(arena, &list, str8_lit("}\n\n"));
    }

    str8_list_push(arena, &list, str8_lit("KongorProtocol.StructSizes = {\n"));
    for (size_t i = 0; i < model->sorted_count; i++) {
        StructDef *s = model->sorted_structs[i];
        str8_list_pushf(arena, &list, "    %.*s = %u,\n", (int)s->name.size, s->name.str, s->calculated_size);
    }
    str8_list_push(arena, &list, str8_lit("}\n\n"));

    str8_list_push(arena, &list, str8_lit("KongorProtocol.FieldOffsets = {\n"));
    for (size_t i = 0; i < model->sorted_count; i++) {
        StructDef *s = model->sorted_structs[i];
        str8_list_pushf(arena, &list, "    %.*s = {\n", (int)s->name.size, s->name.str);
        for (size_t f = 0; f < s->field_count; f++) {
            FieldDef *field = &s->fields[f];
            str8_list_pushf(arena, &list, "        %.*s = %u,\n",
                            (int)field->name.size, field->name.str, field->offset);
        }
        str8_list_push(arena, &list, str8_lit("    },\n"));
    }
    str8_list_push(arena, &list, str8_lit("}\n\n"));

    str8_list_push(arena, &list, str8_lit("return KongorProtocol\n"));

    return str8_list_join(arena, &list, str8_zero());
}

static bool ReadFileToString(KongorArena *arena, const char *path, String8 *out_str) {
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size < 0) { fclose(f); return false; }
    String8 res = str8_alloc(arena, (size_t)size);
    size_t read_bytes = fread(res.str, 1, (size_t)size, f);
    fclose(f);
    res.size = read_bytes;
    res.str[read_bytes] = '\0';
    *out_str = res;
    return true;
}

static bool WriteStringToFile(const char *path, String8 content) {
    std::filesystem::path p(path);
    if (p.has_parent_path()) {
        std::filesystem::create_directories(p.parent_path());
    }
    FILE *f = fopen(path, "wb");
    if (!f) {
        fprintf(stderr, "[ERROR] Could not open file for writing: %s\n", path);
        return false;
    }
    size_t written = fwrite(content.str, 1, content.size, f);
    fclose(f);
    return written == content.size;
}

// ============================================================================
// MAIN ENTRY POINT
// ============================================================================

struct ArenaScopeGuard {
    KongorArena *arena;
    explicit ArenaScopeGuard(KongorArena *a) : arena(a) {}
    ~ArenaScopeGuard() {
        if (arena) {
            kongor_arena_destroy(arena);
        }
    }
};

int main(int argc, char **argv) {
    auto startTime = std::chrono::high_resolution_clock::now();

    const char *schemaPath = "data/schemas/protocol.json";
    const char *cppOutPath = "native/include/generated/kongor_protocol.h";
    const char *csOutPath  = "src/libs/contracts/Generated/KongorProtocol.g.cs";
    const char *luaOutPath = "games/client/override/ui/scripts/generated/protocol.lua";
    bool checkOnly = false;
    bool verbose = false;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--schema") == 0 && i + 1 < argc) {
            schemaPath = argv[++i];
        } else if (strcmp(argv[i], "--cpp-out") == 0 && i + 1 < argc) {
            cppOutPath = argv[++i];
        } else if (strcmp(argv[i], "--cs-out") == 0 && i + 1 < argc) {
            csOutPath = argv[++i];
        } else if (strcmp(argv[i], "--lua-out") == 0 && i + 1 < argc) {
            luaOutPath = argv[++i];
        } else if (strcmp(argv[i], "--check") == 0) {
            checkOnly = true;
        } else if (strcmp(argv[i], "--verbose") == 0) {
            verbose = true;
        } else if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            printf("Usage: kongor-metagen [options]\n");
            printf("Options:\n");
            printf("  --schema <path>   Input protocol schema JSON (default: data/schemas/protocol.json)\n");
            printf("  --cpp-out <path>  Output C++ header file (default: native/include/generated/kongor_protocol.h)\n");
            printf("  --cs-out <path>   Output C# contract file (default: src/libs/contracts/Generated/KongorProtocol.g.cs)\n");
            printf("  --lua-out <path>  Output Lua lookup file (default: games/client/override/ui/scripts/generated/protocol.lua)\n");
            printf("  --check           Verify parity without modifying files (returns 1 on drift)\n");
            printf("  --verbose         Print detailed telemetry and layout traces\n");
            return 0;
        } else {
            fprintf(stderr,
                    "[ERROR] Unknown or incomplete CLI option '%s' | Diagnose/Fix: Run 'kongor-metagen --help' or 'powershell.exe -File scripts/re/test_metagen_parity.ps1'\n",
                    argv[i]);
            return 1;
        }
    }

    printf("[KONGOR-METAGEN] Initializing arena and reading schema '%s'...\n", schemaPath);

    KongorArena arena = kongor_arena_create(32 * 1024 * 1024); // 32MB reserve
    ArenaScopeGuard arenaGuard(&arena);

    String8 schemaContent;
    if (!ReadFileToString(&arena, schemaPath, &schemaContent)) {
        fprintf(stderr,
                "[ERROR] Failed to read schema file: %s | Diagnose/Fix: Verify schema path exists and run 'powershell.exe -File scripts/re/test_metagen_parity.ps1'\n",
                schemaPath);
        return 1;
    }

    size_t parsePos = 0;
    bool parseError = false;
    JsonValue *root = ParseJsonValue(&arena, schemaContent, &parsePos, &parseError);
    if (!parseError) {
        SkipWhitespaceAndComments(schemaContent, &parsePos);
        if (parsePos != schemaContent.size) {
            parseError = true;
        }
    }
    if (parseError || !root || root->type != JSON_OBJECT) {
        fprintf(stderr,
                "[ERROR] Malformed schema JSON or non-object root in '%s' (offset %zu) | Diagnose/Fix: Validate JSON syntax in '%s' and run 'powershell.exe -File scripts/re/test_metagen_parity.ps1'\n",
                schemaPath, parsePos, schemaPath);
        return 1;
    }

    ProtocolModel model = {0};
    model.title = JsonGetString(JsonObjectGet(root, "title"), "Protocol");
    model.version = JsonGetString(JsonObjectGet(root, "version"), "1.0.0");
    model.description = JsonGetString(JsonObjectGet(root, "description"), "");

    // Parse Constants
    JsonValue *constArray = JsonObjectGet(root, "constants");
    if (constArray && constArray->type == JSON_ARRAY) {
        model.constant_count = constArray->array_count;
        model.constants = (ConstantDef *)kongor_arena_push_array(&arena, ConstantDef, model.constant_count);
        size_t cIdx = 0;
        for (JsonValue *cElem = constArray->first_element; cElem != NULL; cElem = cElem->next_element) {
            ConstantDef *c = &model.constants[cIdx++];
            c->name = JsonGetString(JsonObjectGet(cElem, "name"));
            c->type = JsonGetString(JsonObjectGet(cElem, "type"), "uint32");
            JsonValue *vVal = JsonObjectGet(cElem, "value");
            if (vVal && vVal->type == JSON_NUMBER) {
                c->value = vVal->string_val;
            } else if (vVal && vVal->type == JSON_STRING) {
                c->value = vVal->string_val;
            } else {
                c->value = str8_lit("0");
            }
            c->description = JsonGetString(JsonObjectGet(cElem, "description"));
        }
    }

    // Parse Enums
    JsonValue *enumArray = JsonObjectGet(root, "enums");
    if (enumArray && enumArray->type == JSON_ARRAY) {
        model.enum_count = enumArray->array_count;
        model.enums = (EnumDef *)kongor_arena_push_array(&arena, EnumDef, model.enum_count);
        size_t eIdx = 0;
        for (JsonValue *eElem = enumArray->first_element; eElem != NULL; eElem = eElem->next_element) {
            EnumDef *e = &model.enums[eIdx++];
            e->name = JsonGetString(JsonObjectGet(eElem, "name"));
            e->underlying_type = JsonGetString(JsonObjectGet(eElem, "underlying_type"), "uint16");
            e->description = JsonGetString(JsonObjectGet(eElem, "description"));

            JsonValue *valsArr = JsonObjectGet(eElem, "values");
            if (valsArr && valsArr->type == JSON_ARRAY) {
                e->value_count = valsArr->array_count;
                e->values = (EnumMemberDef *)kongor_arena_push_array(&arena, EnumMemberDef, e->value_count);
                size_t vIdx = 0;
                for (JsonValue *vElem = valsArr->first_element; vElem != NULL; vElem = vElem->next_element) {
                    EnumMemberDef *mem = &e->values[vIdx++];
                    mem->name = JsonGetString(JsonObjectGet(vElem, "name"));
                    JsonValue *mvVal = JsonObjectGet(vElem, "value");
                    if (mvVal && (mvVal->type == JSON_NUMBER || mvVal->type == JSON_STRING)) {
                        mem->value = mvVal->string_val;
                    } else {
                        mem->value = str8_lit("0");
                    }
                    mem->description = JsonGetString(JsonObjectGet(vElem, "description"));
                }
            }
        }
    }

    // Parse Structs
    JsonValue *structArray = JsonObjectGet(root, "structs");
    if (structArray && structArray->type == JSON_ARRAY) {
        model.struct_count = structArray->array_count;
        model.structs = (StructDef *)kongor_arena_push_array(&arena, StructDef, model.struct_count);
        size_t sIdx = 0;
        for (JsonValue *sElem = structArray->first_element; sElem != NULL; sElem = sElem->next_element) {
            StructDef *s = &model.structs[sIdx++];
            s->name = JsonGetString(JsonObjectGet(sElem, "name"));
            s->expected_size = (uint32_t)JsonGetNumber(JsonObjectGet(sElem, "expected_size"), 0);
            s->description = JsonGetString(JsonObjectGet(sElem, "description"));

            JsonValue *fieldsArr = JsonObjectGet(sElem, "fields");
            if (fieldsArr && fieldsArr->type == JSON_ARRAY) {
                s->field_count = fieldsArr->array_count;
                s->fields = (FieldDef *)kongor_arena_push_array(&arena, FieldDef, s->field_count);
                size_t fIdx = 0;
                for (JsonValue *fElem = fieldsArr->first_element; fElem != NULL; fElem = fElem->next_element) {
                    FieldDef *f = &s->fields[fIdx++];
                    f->name = JsonGetString(JsonObjectGet(fElem, "name"));
                    f->type = JsonGetString(JsonObjectGet(fElem, "type"));
                    f->description = JsonGetString(JsonObjectGet(fElem, "description"));

                    if (str8_equal(f->type, str8_lit("array"))) {
                        f->is_array = true;
                        f->element_type = JsonGetString(JsonObjectGet(fElem, "element_type"), "uint8");
                        f->array_count = (uint32_t)JsonGetNumber(JsonObjectGet(fElem, "count"), 1);
                    } else {
                        f->is_array = false;
                        f->element_type = str8_zero();
                        f->array_count = 1;
                    }
                }
            }
        }
    }

    // Build Dependencies (including self-references so TopologicalSortDFS catches A -> A cycles)
    for (size_t i = 0; i < model.struct_count; i++) {
        StructDef *s = &model.structs[i];
        size_t maxDeps = s->field_count;
        s->deps = kongor_arena_push_array(&arena, StructDefPtr, maxDeps);
        s->dep_count = 0;

        for (size_t f = 0; f < s->field_count; f++) {
            FieldDef *field = &s->fields[f];
            String8 targetType = field->is_array ? field->element_type : field->type;
            StructDef *targetStruct = FindStruct(&model, targetType);
            if (targetStruct) {
                s->deps[s->dep_count++] = targetStruct;
            }
        }
    }

    // Topological Sort
    model.sorted_structs = kongor_arena_push_array(&arena, StructDefPtr, model.struct_count);
    model.sorted_count = 0;
    for (size_t i = 0; i < model.struct_count; i++) {
        if (!TopologicalSortDFS(&model.structs[i], model.sorted_structs, &model.sorted_count)) {
            return 1;
        }
    }

    // Calculate Offsets and Sizes in Topological Order
    for (size_t i = 0; i < model.sorted_count; i++) {
        StructDef *s = model.sorted_structs[i];
        uint32_t offset = 0;
        for (size_t f = 0; f < s->field_count; f++) {
            FieldDef *field = &s->fields[f];
            uint32_t elemSize = 0;
            String8 typeToEval = field->is_array ? field->element_type : field->type;
            elemSize = GetPrimitiveTypeSize(typeToEval);
            if (elemSize == 0) {
                StructDef *emb = FindStruct(&model, typeToEval);
                if (emb) {
                    elemSize = emb->calculated_size;
                } else {
                    fprintf(stderr,
                            "[ERROR] Unknown type '%.*s' in field '%.*s.%.*s'! | Diagnose/Fix: Define '%.*s' in schema structs or use a supported primitive type\n",
                            (int)typeToEval.size, typeToEval.str,
                            (int)s->name.size, s->name.str,
                            (int)field->name.size, field->name.str,
                            (int)typeToEval.size, typeToEval.str);
                    return 1;
                }
            }

            field->size = field->is_array ? (elemSize * field->array_count) : elemSize;
            field->offset = offset;
            offset += field->size;
        }
        s->calculated_size = offset;

        if (s->expected_size > 0 && s->calculated_size != s->expected_size) {
            fprintf(stderr,
                    "[ERROR] Size mismatch for struct '%.*s': schema expected %u bytes, calculated %u bytes! | Diagnose/Fix: Update 'expected_size' or padding fields in '%s'\n",
                    (int)s->name.size, s->name.str, s->expected_size, s->calculated_size, schemaPath);
            return 1;
        }

        if (verbose) {
            printf("[KONGOR-METAGEN] Struct %.*s: %u bytes (expected %u), %zu fields\n",
                   (int)s->name.size, s->name.str, s->calculated_size, s->expected_size, s->field_count);
        }
    }

    // Generate Outputs
    String8 cppCode = GenerateCppHeader(&arena, &model);
    String8 csCode  = GenerateCsFile(&arena, &model);
    String8 luaCode = GenerateLuaFile(&arena, &model);

    if (checkOnly) {
        printf("[KONGOR-METAGEN] Running in --check parity mode...\n");
        String8 curCpp, curCs, curLua;
        bool cppMatch = ReadFileToString(&arena, cppOutPath, &curCpp) && str8_equal(cppCode, curCpp);
        bool csMatch  = ReadFileToString(&arena, csOutPath, &curCs) && str8_equal(csCode, curCs);
        bool luaMatch = ReadFileToString(&arena, luaOutPath, &curLua) && str8_equal(luaCode, curLua);

        if (!cppMatch) fprintf(stderr, "[PARITY-DRIFT] C++ header drift detected: %s\n", cppOutPath);
        if (!csMatch)  fprintf(stderr, "[PARITY-DRIFT] C# contract drift detected: %s\n", csOutPath);
        if (!luaMatch) fprintf(stderr, "[PARITY-DRIFT] Lua script drift detected: %s\n", luaOutPath);

        if (cppMatch && csMatch && luaMatch) {
            printf("[PARITY-SUCCESS] All generated protocol artifacts match schema with 0 drift!\n");
            return 0;
        } else {
            fprintf(stderr,
                    "[PARITY-FAILURE] Protocol drift detected between schema and generated code! | Diagnose/Fix: Run 'powershell.exe -File scripts/re/generate_protocol.ps1'\n");
            return 1;
        }
    }

    // Write Files
    printf("[KONGOR-METAGEN] Emitting C++ Header -> %s\n", cppOutPath);
    if (!WriteStringToFile(cppOutPath, cppCode)) return 1;

    printf("[KONGOR-METAGEN] Emitting C# Contracts -> %s\n", csOutPath);
    if (!WriteStringToFile(csOutPath, csCode)) return 1;

    printf("[KONGOR-METAGEN] Emitting Lua Lookup  -> %s\n", luaOutPath);
    if (!WriteStringToFile(luaOutPath, luaCode)) return 1;

    auto endTime = std::chrono::high_resolution_clock::now();
    double elapsedMs = std::chrono::duration<double, std::milli>(endTime - startTime).count();

    printf("[KONGOR-METAGEN] Generation Complete: %zu constants, %zu structs generated in %.2f ms (Zero Heap Churn).\n",
           model.constant_count, model.sorted_count, elapsedMs);

    return 0;
}
