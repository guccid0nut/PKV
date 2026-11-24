---
id: FN-011
kind: rendering
title: "K2 Hidden-Container Scrollbox Zero-Height Trap, Bidirectional grow=1, and Latin-1 dyn Font Glyph Limits"
binary_or_subsystem: "k2_x64.dll | override/ui/fe2 | K2 XML UI"
rvas: ["0xC1F330"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-26
tags: [k2, ui, xml, scrollbox, scrollbar, font, latin-1, unicode, layout]
---

# FN-011: K2 Hidden-Container Scrollbox Zero-Height Trap, Bidirectional grow=1, and Latin-1 dyn Font Glyph Limits

## Summary
Building scrollable text modals in K2 XML UI (`main_v2.interface` / `.package` files) exposes three coupled layout and typography traps inside `k2_x64.dll`: (1) `<scrollbox>` fails to measure `fity="true"` / `<instance>` children while an ancestor container is `visible="0"`, leaving `maxScroll == 0` (`vsbVis == false`) when opened; (2) `<panel grow="1">` expands **both** X and Y axes (`growy` does not exist in K2), stretching horizontal content width past its clip viewport if child rows also set `grow="1"`; and (3) K2's dynamic `dyn_*` UI font atlases only populate Latin-1 (`U+0020`–`U+00FF`), rendering Unicode punctuation (`—` `U+2014`, curly quotes `“ ” ’`, and emoji) as solid missing-glyph `█` rectangles.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) Hidden modals (`visible="0"`) using `<scrollbox>` open with no scrollbar and ignore mousewheel scrolling despite content overflowing the panel. (2) Centered child rows with `grow="1"` widen the parent scroll content beyond the clip container (`cw > rw`), clipping right-edge text. (3) Em-dashes (`—`), curly quotes, or emoji inside `<label content="...">` render as solid `█` boxes. |
| **Root Cause** | (1) K2 `<scrollbox>` skips child auto-fit height accumulation while hidden and does not recompute scrollbar range on parent `SetVisible(true)`. (2) K2 `IWidget` parses `grow="1"` as 2D auto-sizing along both width and height. (3) K2 `CFontMap` for standard `dyn_9`..`dyn_14` UI fonts rasterizes the 8-bit Latin-1 codepage only; codepoints `> 0xFF` map to the fallback missing-glyph quad. |
| **Fix / Workaround** | (1) Use the canonical K2 manual clip-scroll pattern (`<panel clip="1">` viewport + `<panel grow="1" regrow="1" width="100%">` content + explicit `<scrollbar>` calling `RecalculateSize()` and `SetMaxValue(math.max(0, totalH - rootH))` on open and after a `35ms` `Sleep` tick). (2) Give child rows inside a `grow="1"` vertical stack explicit `width="100%"` + fixed `height` rather than `grow="1"`. (3) Keep `<label>` copy strictly within ASCII/Latin-1 and render typographic long dashes (such as signature rules) using a crisp vector `<panel width="1.35h" height="0.18h" valign="center" />` beside the `<label>`. |

## Verification Oracle
Exact command or script that proves the invariant holds:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never use `<scrollbox>` inside `visible="0"` modals, never set `grow="1"` on full-width child rows inside a `grow="1"` vertical stack, and never place raw Unicode em-dashes (`U+2014`), curly quotes, or emoji inside K2 `dyn_*` font labels.
