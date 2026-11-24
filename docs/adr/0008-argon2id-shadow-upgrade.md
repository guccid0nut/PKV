# ADR-0008: Argon2id Shadow Password Hash Upgrades

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

Legacy Project Kongor web user accounts stored passwords hashed using ASP.NET Core Identity's default PBKDF2 implementation (`PasswordHasher<T>`). While historically sufficient, PBKDF2 lacks memory-hardness and is vulnerable to specialized GPU and ASIC parallel cracking attacks. Modern security baselines (such as OWASP 2026 standards) mandate Argon2id for password storage. However, we cannot force an abrupt global password reset on active players, nor can we hash unreadable stored PBKDF2 hashes into Argon2id without the original plaintext password.

## Decision

1. **Adopt Argon2id via `Konscious.Security.Cryptography.Argon2`:** Configure memory-hard Argon2id parameters (Memory: 64 MB, Iterations: 3, Parallelism: 4) for all new registrations.
2. **Shadow Upgrades on Successful Login:** When an existing user logs in with their password:
   - If `Argon2idPasswordHash` is present, verify directly against Argon2id.
   - If only `PBKDF2PasswordHash` is present, verify against PBKDF2.
   - Upon successful verification of PBKDF2, asynchronously compute the Argon2id hash from the verified plaintext password in memory and persist it to `User.Argon2idPasswordHash`.
3. **Dual Hash Preservation:** Both hashes coexist during the transition period to maintain compatibility while silently migrating all active accounts to Argon2id.

## Alternatives Considered

1. **Forced Global Password Reset:** Rejected. Disrupts player experience and creates unnecessary support friction.
2. **Hashing the PBKDF2 Hash with Argon2id:** Rejected. Adds complexity, makes future verification two-step, and does not provide genuine memory-hardness over the underlying low-entropy master password.

## Consequences

- **Positive:** Silent, zero-downtime migration of all active user accounts to state-of-the-art Argon2id security; immediate OWASP compliance.
- **Negative:** Dual password hash fields on the database entity until legacy PBKDF2 hashes are eventually deprecated.
- **Neutral:** Game client SRP credentials remain separate and untouched.
