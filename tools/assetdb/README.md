# 🗄️ AssetDB (`tools/assetdb`)

> **SQLite + Content-Addressable Storage (CAS) index, VFS mount resolver, and 7-rule `.s2z` repack quality gate (Python stdlib only).**

`tools/assetdb` tracks VFS asset provenance across `games/.asset_manifest/assets.db`, enforces K2 `.s2z` constraints (`< 1.5 GiB`, non-Zip64, case-collision & shadow guards), and supports hermetic `--repo-root` sandboxes for CI and testing.

---

## ⚡ Quick Start

```powershell
# Initialize isolated scratch AssetDB schema (never mutate live repo without --repo-root)
python tools/assetdb/sync_record.py --init-schema --repo-root artifacts/assetdb_scratch

# Run hermetic AssetDB test suite
python -m pytest tools/assetdb/test_assetdb.py tools/assetdb/test_canonical_architecture.py tools/assetdb/test_kpatch_preview_guards.py
```

---

## 🏗️ Key Modules

- **`sync_record.py`:** Synchronizes CRC32, byte size, timestamps, and CAS chunks in `assets.db` (supports `--evict-override`, `--init-schema`, `--repo-root`).
- **`gate.py`:** 7-rule `.s2z` repack quality gate (`resources\d{1,2}.s2z`, case collisions, VFS mount shadowing, broken asset refs, Lua concat warnings, 1.5 GiB cap, redundant CRC writes).
- **`cas.py`:** Content-addressable chunk hashing and deduplication.
- **`build.py` / `query.py` / `query_injections.py`:** Full manifest builder and SQLite query helpers.
- **`patch_engine.py` / `repack_canonical.py`:** Non-destructive archive patching and canonical `.s2z` repacking.
- **`mounts.json`:** Declarative K2 VFS mount precedence specification.
