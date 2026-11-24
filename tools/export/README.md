# 📦 Playtest & CDN Export Pipeline (`tools/export`)

> **Cleanroom playtest bundle builder, ECDSA P-256 `pkv-v5` CDN manifest generator, and 100% VFS CRC32 parity verifier.**

`tools/export` packs eligible K2 VFS tiers into strict 32-bit `.s2z` archives (`< 1.45 GB`, `< 60,000` entries, `allowZip64=False`), generates cryptographically signed `pkv-v5` CAS manifests, verifies Mach-O/ELF/PE binary linkage, and checks 100% VFS parity with zero PII/secret leaks.

---

## ⚡ Quick Start

```powershell
# Run VFS parity & export unit tests
python -m pytest tools/export/test_verify_vfs_parity.py

# Inspect CLI options for VFS parity verification & CDN manifest generation
python tools/export/verify_vfs_parity.py --help
python tools/export/pkv_cdn_manifest.py --help
```

---

## 🏗️ Key Modules

- **`verify_vfs_parity.py`:** `sanitize-staged`, `pack-tier`, `write-manifests`, `verify-parity`, and `scan-secrets` subcommands modeling the K2 VFS mount chain (`override` > `base` > `game/resources*.s2z` > `ClientPackageFiles`).
- **`pkv_cdn_manifest.py`:** Multi-OS (`wac`, `lac`, `mac`, `las`, `was`) `pkv-v5` CAS manifest generator with IEEE P1363 ECDSA P-256 SHA-256 signatures.
- **`binary_linkage_audit.py`:** Cross-platform PE/ELF/Mach-O dynamic linkage auditor.
- **`ExportDeterministic.targets` / `docker-compose.standalone.yml`:** Deterministic MSBuild export targets and standalone host packaging compose template.
