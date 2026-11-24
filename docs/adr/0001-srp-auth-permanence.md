# ADR-0001: SRP Authentication Permanence

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

The Heroes of Newerth (HoN) native game client (`hon_x64.exe`) authenticates with the master server using the SRP-6a (Secure Remote Password) protocol, implemented deep inside the compiled C++ K2 game engine binary. During this handshake, the client exchanges nonces and computes verifiers based on an MD5 pre-hash of the password. Because the game client binary protocol is fixed and cannot be recompiled without proprietary engine source access, the master server must support this exact cryptographic handshake indefinitely.

Simultaneously, the modern web portal (`DAWNBRINGER`) and REST API (`ZORGATH`) require contemporary cryptographic hashing (Argon2id / PBKDF2) to guard against credential stuffing and brute-force attacks.

## Decision

1. **Permanent SRP Support:** SRP-6a challenge-response remains the permanent authentication protocol for game clients connecting to `KONGOR.MasterServer`.
2. **Dual-Hash Entity Schema:** The `User` database entity permanently retains `SRPPasswordSalt` and `SRPPasswordHash` alongside modern web hashes (`Argon2idPasswordHash` and `PBKDF2PasswordHash`).
3. **Password Change Synchronization:** When users update passwords via the web UI or API, the backend updates both the SRP verifiers and modern password hashes in a single atomic transaction.

## Alternatives Considered

1. **Replace SRP Everywhere:** Rejected. The compiled C++ game client engine does not support TLS or token-based bearer authentication and cannot be updated.
2. **Proxy SRP via Web Hashes:** Rejected. The cryptographic nature of SRP zero-knowledge proofs means the server never sees the plaintext password during client authentication; thus, an Argon2id hash cannot verify an SRP exchange.

## Consequences

- **Positive:** Uncompromised backward compatibility with all native game clients while enabling OWASP-grade security on web services.
- **Negative:** Multiple password hashes coexist on user records.
- **Neutral:** Game client login workflow remains unchanged.
