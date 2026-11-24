# ADR-0004: Tailwind CSS v4 Standalone CLI Build Pipeline

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

The `DAWNBRINGER` Web Portal UI was historically using the Tailwind Play CDN script (`https://cdn.tailwindcss.com`). The CDN script downloads a ~300KB JavaScript runtime on every page visit and compiles utility classes dynamically in the client's browser, leading to flash-of-unstyled-content (FOUC), slower first paint times, and a hard external runtime network dependency. Furthermore, Project Kongor is a pure .NET ecosystem with zero Node.js/npm dependencies in its toolchain.

## Decision

1. **Standalone CLI Binary:** Adopt Tailwind CSS v4 standalone executable (`tailwindcss.exe` / `tailwindcss-linux-x64`) stored under `src/services/web-ui/tools/`.
2. **CSS-First Architecture:** Configure styling via [Styles/input.css](../../src/services/web-ui/Styles/input.css) using `@import "tailwindcss";` and `@source` directives targeting Razor views and script directories, eliminating the need for `package.json`, Node.js, and legacy `tailwind.config.js`.
3. **Incremental MSBuild Target:** Build precompiled CSS into `wwwroot/css/tailwind.css` using MSBuild `Inputs` and `Outputs` attributes to guarantee sub-millisecond incremental build performance during normal development.
4. **CLI Automation:** Provide `./kongor.ps1 tailwind [build|watch|dev|setup]` commands for interactive development and automated environments.

## Alternatives Considered

1. **Node.js / npm Toolchain:** Rejected. Adding `node_modules` and npm to a .NET repository adds hundreds of megabytes of third-party dependencies and complicates CI/CD builds.
2. **Tailwind v3 Standalone CLI:** Evaluated, but rejected in favor of v4 because Tailwind v4 provides a cleaner CSS-first configuration model, faster compilation times (under 650ms), and smaller generated bundles.
3. **Keep CDN Script:** Rejected. Inacceptable for production deployment, offline LAN play, or air-gapped environments.

## Consequences

- **Positive:** Zero Node.js footprint; instant page load without client JS runtime; 100% offline self-hosted assets; fast incremental compilation.
- **Negative:** Standalone binary (~100 MB) must be installed locally or fetched on initial setup.
- **Neutral:** Razor view authoring remains unchanged; all existing custom CSS rules in `_Layout.cshtml` remain intact.
