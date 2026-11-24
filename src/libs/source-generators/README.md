<div align="center">

# ⚙️ Roslyn Source Generators (`Kongor.SourceGenerators`)
### *Compile-Time C# Metaprogramming & High-Performance Logging*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Roslyn](https://img.shields.io/badge/Roslyn-Incremental%20Generator-0078D4?style=for-the-badge)](#)
[![Zero Boxing](https://img.shields.io/badge/Logging-Zero%20Allocation-2ECC71?style=for-the-badge)](#)

**Roslyn Source Generators provides compile-time C# code generation for high-performance structured logging, automated member stubs, and serialization verification.**

[🌟 Key Features](#-key-features)

---

</div>

## 🌟 Key Features

* **⚡ Compile-Time Source Generated Logging**: High-performance `LoggerMessageAttribute` generators eliminating runtime reflection and boxing overhead.
* **🔨 `[AutoImplementMissingMembers]` Generator**: Automates infrastructure stubs and Redis fake implementations for sub-second unit testing.
* **🛡️ Incremental Generation**: Built upon the Roslyn `IIncrementalGenerator` architecture for instant IDE compilation.
