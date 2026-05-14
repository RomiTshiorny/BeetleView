# BeetleView

A simple WinUI 3 viewer for `.beetle` exception-log files produced by [KirillOsenkov/beetle](https://github.com/KirillOsenkov/beetle).

Browse processes, filter exceptions, and view resolved stack traces — without going through the MCP server.

## What it shows

- **Session info bar** — start time (UTC), duration, process count, total exception count, events lost, file size.
- **Processes pane** (left) — every recorded process with image name, pid, and exception count. Sorted by exception count desc. `(All processes)` is a pseudo-entry that merges everything chronologically. A filter box above the list narrows by name or pid.
- **Exceptions pane** (top right) — timestamp, exception type, and message. Free-text filter (case-insensitive substring over type + message). Capped at 5,000 rows for UI responsiveness. Rows stream in incrementally (batches of 200) so you can start scrolling before population finishes.
- **Stack trace pane** (bottom right) — managed stack trace resolved against the JIT'd methods + module symbols recorded in the same file. Computed lazily on selection via `Session.ComputeStackTrace`.
- **Progress indicators** — a `ProgressRing` next to the Open button while the file is being deserialized, and an overlay over the exceptions list while it populates.

## Requirements

- Windows 10 build 17763 or newer (Windows 11 fully supported).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — only needed to build from source; the published `.exe` is self-contained.

> **Note on MSVC:** The WinAppSDK's XAML markup compiler probes for the MSVC C++ build tools (`<VS>\VC\Tools\MSVC\`) at build time, even for pure-managed projects. BeetleView's `.csproj` sets stub `VCInstallPath32`/`VCInstallPath64` properties to short-circuit that probe so contributors without the C++ workload can still build. If your build fails with `GetLatestMSVCVersion` / `DirectoryNotFoundException` on `VC\Tools\MSVC`, either pull the latest `.csproj` or install the "MSVC v14x C++ build tools" component via the Visual Studio Installer.

## Build & run from source

```powershell
git clone https://github.com/RomiTshiorny/BeetleView.git
cd BeetleView
dotnet build
.\bin\Debug\net10.0-windows10.0.19041.0\win-x64\BeetleView.exe
```

The project is configured as an **unpackaged** WinUI 3 app (`WindowsPackageType=None`), so the `.exe` runs directly — no MSIX install and no Developer Mode required. The Windows App SDK bootstrapper is initialized automatically.

> **Note on the target framework:** WinUI 3 requires a Windows-flavoured TFM (`net10.0-windows10.0.<sdk>`). Plain `net10.0` is not an option because WinUI 3 is built on WinRT, which is Windows-only. The project targets the WinAppSDK floor (`10.0.19041.0`) at compile time and runs on Windows 10 1809 (build 17763) and newer at runtime.

## "Open With" / file association

The .exe accepts a file path as its first command-line argument, so:

- Right-click any `.beetle` file → **Open With** → **Choose another app** → browse to `BeetleView.exe`. Tick "Always use this app" to make it the default.
- Or click **Register .beetle association** in the toolbar — this writes the necessary `HKCU\Software\Classes` entries so double-clicking a `.beetle` file launches BeetleView. Per-user; no admin required.
- Or drag a `.beetle` file directly onto `BeetleView.exe` in Explorer.

> If you rebuild the .exe to a different path, re-run the **Register .beetle association** button (the registry stores an absolute path).

## Project layout

```
BeetleView/
├─ App.xaml(.cs)              # Application entry; ModuleInitializer for single-file runtime
├─ MainWindow.xaml(.cs)       # Hosts a Frame, navigates to MainPage
├─ MainPage.xaml(.cs)         # UI shell: toolbar, lists, filters, stack-trace view
├─ ViewModels/
│   ├─ ProcessViewModel.cs    # Row VM for the Processes list
│   └─ ExceptionViewModel.cs  # Row VM for the Exceptions list
├─ Services/
│   ├─ SessionFileLoader.cs       # Loads .beetle off-thread; builds process VMs + summary
│   └─ ExceptionViewModelBuilder.cs   # Materializes per-selection rows off-thread
├─ Helpers/
│   └─ DialogHelper.cs        # ContentDialog wrappers (ShowErrorAsync / ShowInfoAsync)
├─ FileAssociation.cs         # Per-user HKCU file-association registration
└─ Assets/AppIcon.ico
```

## How it uses `Beetle.Core`

```csharp
using GuiLabs.Dotnet.Recorder;

Session session = SessionSerializer.Load(path);   // monolithic; off the UI thread

foreach (Process p in session.Processes)
{
    foreach (ExceptionEvent ex in p.Exceptions)
    {
        // ex.Timestamp, ex.ExceptionType, ex.ExceptionMessage, ex.ThreadId
        string stack = session.ComputeStackTrace(p, ex);   // computed lazily on selection
    }
}
```

## Publish (for distribution)

A normal `dotnet publish` produces a folder of DLLs. For **end-user distribution**, the included publish profile produces a **single self-contained `.exe`** that bundles the .NET runtime, Windows App SDK, native libs, compiled XAML, and resources index — everything self-extracts to `%LOCALAPPDATA%\Temp\.net\BeetleView\<hash>\` on first launch.

```powershell
# Single-file release build (~85 MB exe, runs on any Win10 1809+ x64 box)
dotnet publish -c Release -p:Platform=x64
# Output: bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\BeetleView.exe
```

Profiles for `win-x86` and `win-arm64` also exist under `Properties\PublishProfiles\`.

Key knobs (in `.csproj` + `.pubxml`):
- `PublishSingleFile=true` + `WindowsAppSDKSelfContained=true` + `IncludeNativeLibrariesForSelfExtract=true` + `IncludeAllContentForSelfExtract=true` + `EnableCompressionInSingleFile=true`
- An MSBuild target promotes the WinUI 3 SDK's `.xbf` and `.pri` files from `CopyToPublishDirectory=Never` to `PreserveNewest` so they're included in the bundle.
- A `[ModuleInitializer]` in `App.xaml.cs` sets `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` to `AppContext.BaseDirectory` before any WinAppSDK code runs (required for single-file self-extract to find the runtime).

> Single-file publish is **only** needed when handing the `.exe` to an end user (or attaching it to a GitHub Release). Day-to-day development uses plain `dotnet build` / `dotnet run` and doesn't need it.

## Notes / known limitations

- `SessionSerializer.Load` is monolithic — partial data can't be shown while the file is being parsed. After the load completes, the process list appears immediately and the exception list populates in batches so the UI is responsive even for sessions with hundreds of thousands of exceptions.
- The exception list is capped at 5,000 rows after filtering. Narrow the process selection or use the filter boxes for huge sessions.
- Selection changes cancel any in-flight exception population (via `CancellationToken`), so flipping through processes never stalls the UI.
- Process diff (the BeetleMcp `diff_beetles` tool equivalent) is not implemented; this is read-only single-file browse.

## License

Released under the [MIT License](LICENSE).
