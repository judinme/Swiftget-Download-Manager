# SwiftGet Autonomous Engineering Protocol

This rule governs the development and maintenance of the SwiftGet Download Manager codebase.

## 1. Operating Protocol
1. **Planning Mode**: Before writing code for any milestone or major architecture change, produce an Implementation Plan artifact and wait for user approval.
2. **Task List**: Maintain a Task List artifact (`task_list.md`). Work strictly one milestone at a time (M0 to M9). Do not start a milestone until the previous one's acceptance criteria pass.
3. **Walkthrough**: After each milestone, produce a Walkthrough artifact (`walkthrough_mX.md`): what was built, tests run with real output, benchmark numbers, screenshots, and known limitations.
4. **Test-Driven Rigor**: Build and run tests after every meaningful change. Never claim something works without executing it. Never leave TODOs, stubs, mocked-only logic, or placeholder code in shipped paths.
5. **Dependency Discipline**: Verify every API, package name, and version against current official documentation before using it. Pin all versions. Explain each added dependency in one line.
6. **Decisiveness**: If a requirement is ambiguous or two designs trade off, state the options and recommendation, then continue with the recommendation unless objected to.
7. **Quality Workflow**: Run `pwsh scripts/run-all.ps1` to execute the full suite (test, benchmark, lint) before concluding any milestone.

## 2. Technical Stack Constraints
- **Core Engine**: C# 12, .NET 8 LTS, `SwiftGet.Engine` class library with ZERO UI or Windows-only dependencies.
- **Networking**: `SocketsHttpHandler` / `HttpClient` (HTTP/1.1 and HTTP/2), `System.Threading.Channels`, pooled buffers (`ArrayPool<byte>`), Polly for retry policies.
- **UI**: WinUI 3 (Windows App SDK, unpackaged and self-contained), MVVM via `CommunityToolkit.Mvvm`, virtualized list for 10,000+ rows, compiled bindings (`x:Bind`).
- **Storage**: SQLite (`Microsoft.Data.Sqlite`) in WAL mode with structured schema migrations.
- **Logging & DI**: Serilog with rolling structured files; `Microsoft.Extensions.Hosting`.
- **Media**: `yt-dlp` and `ffmpeg` as managed sidecar binaries.
- **Extension**: TypeScript, Manifest V3 (Chrome, Edge, Brave, Opera) + Firefox build.
- **Bridge**: Native Messaging host (.NET console) + loopback-only authenticated HTTP/WebSocket API.
- **Installer**: WiX v4 MSI (per-user and per-machine), native-messaging registry keys, uninstall cleanup, signed updater.
- **Tests**: xUnit, FluentAssertions, FsCheck (property-based), Kestrel fault-injection test server, Playwright for extension E2E, FlaUI for UI smoke tests.
- **CI**: GitHub Actions (.github/workflows/ci.yml).
