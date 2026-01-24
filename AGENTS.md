# OsuGrind Agent Guidelines

This document provides essential information for AI agents working on the OsuGrind repository.

## 🛠 Project Overview
OsuGrind is a high-performance analytics and tracking tool for osu!.
- **Backend**: C# .NET 8.0 (WPF). Handles memory reading, data processing, and local API.
- **Frontend**: HTML/JS/CSS hosted in WebView2. Uses Vanilla JS (ES6+).
- **Native**: Interops with `rosu_pp_wrapper.dll` (Rust) for performance math via P/Invoke.

## 🚀 Environment & Build Commands

### Paths
- **Source Root**: `\\wsl.localhost\Ubuntu\mnt\remote_c\project\OsuGrind`
- **Build Output**: `bin\Debug\net8.0-windows\`
- **Native Libs**: `lib/` (Contains `rosu_pp_wrapper.dll`)

### Commands
- **Restore**: `dotnet restore`
- **Build**: `dotnet build "OsuGrind.csproj" -c Debug`
- **Clean**: `dotnet clean`
- **Lint (C#)**: `dotnet format`
- **Test (All)**: `dotnet test`
- **Run Single Test**: `dotnet test --filter "FullyQualifiedName~TestName"`
- **Run Tests in File**: `dotnet test --filter "FullyQualifiedName~Namespace.ClassName"`

## 📁 Project Structure

| Directory | Purpose |
|-----------|---------|
| `Api/` | HTTP API server, OAuth authentication, request/response models |
| `Assets/` | Static images for desktop app (logos, icons, mod images) |
| `Import/` | Score import services for osu! Lazer and Stable |
| `LiveReading/` | Memory reading for live gameplay (`LazerMemoryReader.cs`, `StableMemoryReader.cs`) |
| `Models/` | Data models: `PlayRow.cs`, `LiveSnapshot.cs`, `BeatmapRow.cs`, `AnalyticsData.cs` |
| `Services/` | Core services: `TrackerDb.cs`, `RosuService.cs`, `SettingsManager.cs`, `DebugService.cs` |
| `WebUI/` | Frontend HTML/JS/CSS application hosted in WebView2 |
| `WebUI/js/` | JavaScript modules: `api.js`, `app.js`, `live.js`, `history.js`, `analytics.js` |
| `WebUI/css/` | CSS stylesheets: `style.css` (main), per-module styles |
| `lib/` | Native DLLs: `rosu_pp_wrapper.dll` (Rust PP calculation) |

## 📏 Coding Standards

### 🛡 C# (Backend)

#### Imports
Order imports as follows:
1. `System.*` namespaces (alphabetically)
2. Third-party namespaces (`Microsoft.*`, `Newtonsoft.*`, `OsuParsers.*`)
3. Internal namespaces (`OsuGrind.Models`, `OsuGrind.Services`)

#### Namespace & Formatting
- **Namespace**: Always use file-scoped namespaces (e.g., `namespace OsuGrind.Services;`).
- **Braces**: Allman style (braces on new lines).
- **Indentation**: 4 spaces.
- **var**: Use when the type is apparent (e.g., `var list = new List<string>();`).
- **Target-typed new**: Use `new()` when type is clear from context: `List<string> list = new();`.

#### Naming Conventions
- `PascalCase`: Classes, Methods, Public Properties, Events.
- `_camelCase`: Private and internal fields (e.g., `private readonly RosuService _rosu;`).
- `camelCase`: Parameters, local variables.
- `IInterface`: Prefix interfaces with `I`.
- `Async` suffix: Always suffix methods returning `Task` or `ValueTask` with `Async`.

#### Modern C# Features
- **Records**: Use for immutable data types and DTOs (e.g., `public record TotalStats(...)`).
- **Primary Constructors**: Prefer for dependency injection in services where applicable.
- **Collection Expressions**: Use `[]` for array/list initialization where possible.
- **Raw string literals**: Use `"""` for multi-line SQL or JSON strings (see `TrackerDb.cs`).
- **Nullable reference types**: Enabled. Use `string?` and proper null checks.

#### Models & Data Flow
- **`IsPreview` Flag**: Snapshots from Song Select/Menu are marked `IsPreview = true`. These MUST be ignored by recording logic.
- **Results Validation**: Stable recording requires `IsResultsReady = true` AND a stabilization period (2+ frames) to avoid memory spikes.
- **SQLite**: Use raw SQL in `TrackerDb.cs`. Ensure all parameters use `$prefix` (e.g., `$score_id`) for consistency with current code.

#### Error Handling
- Log exceptions using `DebugService.Log(message, "Error")`.
- Use `try-catch` around Native Interop, Database operations, and File I/O.
- For high-frequency loops, use `DebugService.Throttled` to avoid log spam.

### 🌐 JavaScript (Frontend)

#### Architecture
- **Class-based**: Each module is a class (e.g., `class LiveModule`).
- **Global exposure**: Modules exposed via `window.moduleName = new Module()`.
- **No bundler**: Vanilla ES6+ with script tags. Do not use `import/export`.

#### Naming Conventions
- `PascalCase`: Classes.
- `camelCase`: Methods, variables, properties.
- `UPPER_SNAKE_CASE`: Constants.

#### DOM & Events
- Use `document.querySelector` or `document.getElementById`.
- Use `data-` attributes for stable DOM selection in JS.
- Always clean up `addEventListener()` if a module is destroyed/reloaded.

#### Communication (C# <-> JS Bridge)
- **C# -> JS**: `window.chrome.webview.postMessage(json)` or WebSocket (`ws://localhost/ws/live`).
- **JS -> C#**: `window.chrome.webview.postMessage({ action: 'name', data: {} })`.

## 🌉 Interop & Integration
- **Rosu (Rust)**: Native math via `rosu_pp_wrapper.dll`. Always check `IntPtr.Zero` when handling native pointers.
- **Database**: `TrackerDb.cs` handles all SQLite interactions. Use `Raw string literals` for SQL.
- **Memory Reading**: `MemoryScanner.cs` handles signature scanning. Avoid allocations in `ReadLoop`.

## 🧪 Testing Patterns
- **XUnit**: Primary test framework.
- **Naming**: `MethodName_StateUnderTest_ExpectedBehavior`.
- **Mocks**: Use interfaces to mock services.
- **Data-Driven**: Use `[Theory]` and `[InlineData]` for math/parsing tests.

## 🔒 Security & Best Practices
- **Secrets**: Never commit `secrets.json` or `*.db`.
- **UI Thread**: Always use `Application.Current.Dispatcher.Invoke` when updating WPF UI from background tasks.
- **WebView2**: Ensure `CoreWebView2` is initialized before calling `ExecuteScriptAsync`.

---
*Document Version: 2.0.0*
*Note: This file is optimized for consumption by agentic workflows.*
