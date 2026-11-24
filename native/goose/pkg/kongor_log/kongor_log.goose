// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
//
// Project Kongor shared structured logging & actionable error package for standalone Goose CLIs.
// Emits UTC millisecond-timestamped + monotonic-elapsed [DEBUG]/[INFO]/[WARN]/[ERROR] lines
// to stderr (preserving clean CLI stdout for structured/JSON output) and optionally appends
// to userdata/logs/goose_<tool>.log.

namespace kongor_log;

import std;
import os;

var tool_tag:          u8[>..] = [];
var log_file_path:     u8[>..] = [];
var start_clock_ns:    i64     = 0;
var debug_enabled:     bool    = true;
var stderr_enabled:    bool    = true;
var file_sink_enabled: bool    = false;

// Initializes telemetry for a standalone CLI tool.
// Environment overrides:
//   KONGOR_GOOSE_QUIET=1        -> suppresses DEBUG/INFO on stderr (keeps WARN/ERROR)
//   KONGOR_GOOSE_NO_DEBUG=1     -> suppresses DEBUG lines
//   KONGOR_GOOSE_LOG_FILE=<p>   -> explicit log file path (or "0" to disable file sink)
fn init(name: const u8[:]) {
    start_clock_ns = clock_ns();
    tool_tag.resize(0);
    tool_tag.append(name);

    var env_buf: u8[>..] = [];
    if env("KONGOR_GOOSE_QUIET", env_buf) && env_buf == "1" {
        stderr_enabled = false;
    }

    env_buf.resize(0);
    if env("KONGOR_GOOSE_NO_DEBUG", env_buf) && env_buf == "1" {
        debug_enabled = false;
    }

    log_file_path.resize(0);
    env_buf.resize(0);
    if env("KONGOR_GOOSE_LOG_FILE", env_buf) {
        if env_buf != "0" && env_buf.len > 0 {
            log_file_path.append(env_buf);
            file_sink_enabled = true;
        } else {
            file_sink_enabled = false;
        }
        return;
    }

    // Default file sink under userdata/logs/goose_<tool>.log when userdata/ exists or can be created
    if make_dir("userdata") && make_dir("userdata/logs") {
        log_file_path.append("userdata/logs/goose_");
        log_file_path.append(name);
        log_file_path.append(".log");
        file_sink_enabled = true;
    }
}

fn elapsed_ms() -> f64 {
    if start_clock_ns == 0 { return 0.0; }
    return (clock_ns() - start_clock_ns) / 1000000.0;
}

// Howard Hinnant civil-from-days UTC formatter -> "YYYY-MM-DD HH:MM:SS.mmmZ"
fn append_utc_timestamp(out: u8[>..]&) {
    let ns = time_ns();
    let total_ms = if ns > 0 { ns / 1000000 } else { 0 };
    let ms = total_ms % 1000;
    let total_sec = total_ms / 1000;
    let sec_of_day = total_sec % 86400;
    let days = total_sec / 86400;

    let z = days + 719468;
    let era = (if z >= 0 { z } else { z - 146096 }) / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    var y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    if m <= 2 { y += 1; }

    let hh = sec_of_day / 3600;
    let mm = (sec_of_day % 3600) / 60;
    let ss = sec_of_day % 60;

    format_uint(out, y as u64, 10, 4, '0');
    out.push('-');
    format_uint(out, m as u64, 10, 2, '0');
    out.push('-');
    format_uint(out, d as u64, 10, 2, '0');
    out.push(' ');
    format_uint(out, hh as u64, 10, 2, '0');
    out.push(':');
    format_uint(out, mm as u64, 10, 2, '0');
    out.push(':');
    format_uint(out, ss as u64, 10, 2, '0');
    out.push('.');
    format_uint(out, ms as u64, 10, 3, '0');
    out.push('Z');
}

fn emit_raw(level: const u8[:], msg: const u8[:], force_stderr: bool) {
    var line: u8[>..] = [];
    line.push('[');
    append_utc_timestamp(line);
    line.append("] [+");
    format_flt(line, elapsed_ms(), 2);
    line.append("ms] [");
    if tool_tag.len > 0 {
        line.append(tool_tag);
    } else {
        line.append("kongor-goose");
    }
    line.append("] [");
    line.append(level);
    line.append("] ");
    line.append(msg);
    line.push('\n');

    if force_stderr || stderr_enabled {
        write_stderr(line);
    }
    if file_sink_enabled && log_file_path.len > 0 {
        append_file(log_file_path, line);
    }
}

fn debug(msg: const u8[:]) {
    if !debug_enabled { return; }
    emit_raw("DEBUG", msg, false);
}

fn info(msg: const u8[:]) {
    emit_raw("INFO", msg, false);
}

fn warn(msg: const u8[:]) {
    emit_raw("WARN", msg, true);
}

fn error(msg: const u8[:]) {
    emit_raw("ERROR", msg, true);
}

// Project Kongor Actionable Error Invariant ("Errors Are for AI Agents, Not Humans"):
// Explicitly emits (1) what failed, and (2) the exact command or script to fix or diagnose it.
fn error_actionable(what_failed: const u8[:], diagnose_command: const u8[:]) {
    var full: u8[>..] = [];
    full.append(what_failed);
    full.append(" | Diagnose/Fix: ");
    full.append(diagnose_command);
    emit_raw("ERROR", full, true);
}
