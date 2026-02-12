# Copilot instructions (VilsSharpX)

## Project snapshot
- WPF app targeting `net8.0-windows` (`UseWPF=true`) that visualizes 8-bit grayscale frames (Gray8) and a computed diff.
- Three panes in the UI: **A (AVTP/Generator)**, **B (mock LVDS)**, **DIFF |A−B|** (see `MainWindow.xaml`).

## Big-picture data flow (most important)
- Ethernet AVTP frame -> `AvtpLiveCapture` sniffs ethertype 0x22F0 -> `AvtpRvfParser.TryParseAvtpRvfEthernet` -> `RvfReassembler.Push()` copies lines into a 320×80 frame -> when `EndFrame` emits `OnFrameReady(outFrame, meta)`.
- UI updates must be marshaled to the UI thread: `MainWindow` subscribes to `OnFrameReady` and uses `Dispatcher.Invoke(...)` to update `_avtpFrame` + status + `RenderAll()`.
- When no AVTP frames have arrived yet, A uses the fallback loaded/generated PGM (`GetASourceBytes()` in `MainWindow.xaml.cs`).

## RVF/AVTP protocol assumptions (don't break these)
- AVTP frames use ethertype 0x22F0; RVF payload parsing is in `AvtpRvfParser`.
- Stream is currently strict for this device: width=320, height=80 (`RvfProtocol.W/H`). Reassembly ignores chunks outside this.
- Chunk payload is `numLines * width` bytes. `line` is **1-based**.
- `RvfReassembler` tracks missing sequences as “gaps” and emits a **copy** of the frame so the next frame can keep assembling.

## Rendering/concurrency patterns
- Rendering uses `WriteableBitmap` with `PixelFormats.Gray8` and `WritePixels` (see `Blit(...)` in `MainWindow.xaml.cs`).
- Background loops are started with `Task.Run(...)` and stopped via `CancellationTokenSource` (`Start/Stop`). Keep UI thread free.
- If you add new callbacks from background threads, use `Dispatcher.Invoke/BeginInvoke` before touching WPF controls.

## Logs and crash handling
- App is `WinExe` (no console). Runtime diagnostics are written to files:
  - `diagnostic.log` for AVTP/capture diagnostic traces (written via `DiagnosticLogger`).
  - `crash.log` for unhandled exceptions (see `App.xaml.cs`).
- Prefer file logging (append) over `Console.WriteLine` for anything important.

## PGM loader notes
- `Pgm.Load(...)` supports `P2` (ASCII) and `P5` (binary), but the `P5` path uses a simple "skip 4 newlines" heuristic to find pixel bytes; it is not a fully robust PGM parser (see `Pgm` in `MainWindow.xaml.cs`).

## Developer workflows
- Build: `dotnet build` (from the project folder).
- Run: `dotnet run` (or `dotnet run --project VilsSharpX.csproj`).
- No test project is present; changes are typically validated by running the app and observing A/B/DIFF + `diagnostic.log`.

## When making changes
- Keep constants consistent: `W=320`, active height `80`, LVDS height `84` with bottom 4 metadata lines cropped (see `MainWindow.xaml`/`.cs`).
- Preserve protocol compatibility in `RvfProtocol.cs`, `AvtpRvfParser.cs`, and `RvfReassembler.cs`.
- If you refactor frame handling, mind that `Frame` clones buffers for safety today; avoid introducing shared-buffer race bugs.
