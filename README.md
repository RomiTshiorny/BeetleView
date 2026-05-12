# BeetleView

A simple WinUI 3 viewer for `.beetle` files produced by [KirillOsenkov/beetle](https://github.com/KirillOsenkov/beetle).

Browse processes, filter exceptions, and view resolved stack traces — without going through the MCP server.

## What it shows

- **Session info bar**: start time (UTC), duration, process count, total exception count, events lost, file size.
- **Processes pane** (left): every process recorded in the session, with image name, pid, and exception count. Sorted by exception count desc. `(All processes)` is a pseudo-entry that merges everything chronologically. **Filter box** above the list narrows by name or pid.
- **Exceptions pane** (top right): timestamp, exception type, and message. Free-text filter (case-insensitive substring over type + message). Capped at 5,000 rows for UI responsiveness. Rows stream in incrementally (batches of 200) so you can start scrolling before population finishes.
- **Stack trace pane** (bottom right): managed stack trace resolved against the JIT'd methods + module symbols recorded in the same file. Computed lazily on selection via `Session.ComputeStackTrace`.
- **Progress indicators**: a `ProgressRing` next to the Open button while the file is being deserialized, and an overlay over the exceptions list while it populates.

## Build & run

Requires the .NET 10 SDK with a Windows 10/11 build ≥ 17763.

```powershell
cd C:\repos\BeetleView
dotnet build
.\bin\Debug\net10.0-windows10.0.26100.0\win-x64\BeetleView.exe
```

The project is configured as an **unpackaged** WinUI 3 app (`WindowsPackageType=None`), so the `.exe` runs directly — no MSIX install and no Developer Mode required. The Windows App SDK bootstrapper is initialized automatically.

## "Open With" / file association

The .exe accepts a file path as its first command-line argument, so:

- Right-click any `.beetle` file → **Open With** → **Choose another app** → browse to `BeetleView.exe`. Tick "Always use this app" to make it the default.
- Or click **Register .beetle association** in the toolbar — this writes the necessary `HKCU\Software\Classes` entries so double-clicking a `.beetle` file launches BeetleView. Per-user; no admin required.
- Or drag a `.beetle` file directly onto `BeetleView.exe` in Explorer.

> If you rebuild the .exe to a different path, re-run the **Register .beetle association** button (the registry stores an absolute path).

## How it uses Beetle.Core

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

## Icon

The app icon is the Windows-native 🪲 (BEETLE, U+1FAB2) glyph from Segoe UI Emoji, rendered at 7 sizes (16/24/32/48/64/128/256) and packed into `Assets\AppIcon.ico`. The icon is **embedded in the .exe** via `<ApplicationIcon>` (so Explorer/taskbar pick it up) and also referenced at runtime via `AppWindow.SetIcon` (for the window title bar).

To regenerate after changes, run the standalone generator in the sibling project `..\BeetleView-IconGen\`:

```powershell
cd C:\repos\BeetleView-IconGen
dotnet run -c Release -- ..\BeetleView\Assets\AppIcon.ico
```

## Publish

```powershell
cd C:\repos\BeetleView
dotnet publish -c Release -r win-x64
# Output: bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\BeetleView.exe
```

The `Properties\PublishProfiles\win-x64.pubxml` profile is configured for **single-file self-contained** publish — the result is **literally one `BeetleView.exe`** (~85 MB) plus a tiny `.pdb` (safe to delete). Everything else — .NET 10 runtime, Windows App SDK runtime, native libs, compiled XAML, and the resources index — is bundled inside the exe and self-extracts to `%LOCALAPPDATA%\Temp\.net\BeetleView\<hash>\` on first launch.

Key knobs in the .csproj / .pubxml:
- `PublishSingleFile=true` + `WindowsAppSDKSelfContained=true` + `IncludeNativeLibrariesForSelfExtract=true` + `IncludeAllContentForSelfExtract=true` + `EnableCompressionInSingleFile=true`
- The `IncludeXbfAndPriInPublish` MSBuild target promotes the WinUI 3 SDK's `.xbf` and `.pri` files from `CopyToPublishDirectory=Never` to `PreserveNewest` so they're included in the bundle.
- A `[ModuleInitializer]` in `App.xaml.cs` sets `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` to `AppContext.BaseDirectory` before any WinAppSDK code runs (required for single-file self-extract to find the runtime).

Profiles for `win-x86` and `win-arm64` also exist. Just copy `BeetleView.exe` to any Windows 10 1809+ machine and run.

## Notes / known limitations

- `SessionSerializer.Load` is monolithic — we can't show partial data while the file is being parsed. After the load completes, the process list appears immediately and the exception list populates in batches so the UI is responsive even for sessions with hundreds of thousands of exceptions.
- The exception list is capped at 5,000 rows after filtering. Narrow the process selection or use the filter boxes for huge sessions.
- Selection changes cancel any in-flight exception population (via `CancellationToken`), so flipping through processes never stalls the UI.
- Process diff (the BeetleMcp `diff_beetles` tool equivalent) is not implemented; this is read-only single-file browse.
