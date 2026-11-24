<div align="center">

# ⚡ Kongor Protocol Core (`Kongor.ProtocolCore`)
### *Cryptographic Primitives, RFC 5054 SRP-6a & High-Speed Wire Serialization*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Zero Allocation](https://img.shields.io/badge/Memory-Span%3Cbyte%3E%20Optimized-2ECC71?style=for-the-badge)](#)
[![SRP-6a](https://img.shields.io/badge/Crypto-SRP--6a%202048--bit-blue?style=for-the-badge)](#)

**Kongor Protocol Core implements the core cryptographic algorithms, zero-allocation serialization engines, and wire-level binary protocol decoders connecting modern .NET 11 to the legacy K2 Engine.**

[🌟 Key Features](#-key-features)

---

</div>

## 🌟 Key Features

* **🔐 SRP-6a Cryptography Engine**: RFC 5054 compliant Secure Remote Password algorithm (2048-bit prime group, SHA-256) ensuring player credentials never cross the wire in plain text.
* **📜 High-Performance PHP Serializer**: Custom non-allocating parser/formatter for PHP serialized responses (`a:2:{s:3:"foo";s:3:"bar";}`) supporting hybrid associative and positional arrays.
* **📦 Span<byte> & MemoryPack Integration**: Extreme throughput binary framing using `System.Memory` primitives.
* **🧪 Property-Based Protocol Fuzzing**: Exhaustively verified against malicious, malformed, and truncated network packets.
