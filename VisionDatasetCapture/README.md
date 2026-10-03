# VisionDatasetCapture

Minimal WPF (.NET 6) utility that captures screenshots of a selected Windows process window at a fixed interval and saves them as numbered PNGs. Use it to collect image datasets for computer-vision / object-detection work. It does no detection or training.

## Usage

1. Pick a process/window from the dropdown (the list refreshes each time it is opened).
2. Enter a dataset/class name, e.g. `Bobber`.
3. Enter an integer interval in seconds.
4. Press **Start**. The first screenshot is taken immediately, then one per interval.
5. Press **Stop** to end the session.

Images are saved to `<app output dir>\<DatasetName>\<DatasetName>_N.png`
(e.g. `bin\Debug\net6.0-windows\Bobber\Bobber_0.png`).

On every Start the folder is scanned for `{DatasetName}_N.png`; numbering continues from the highest N + 1, so existing images are never overwritten across sessions.

## How it works

| File | Role |
|---|---|
| `MainWindow.xaml(.cs)` | Single window, input validation, Start/Stop toggle, status area |
| `ProcessSelector.cs` | Lists processes that have a main window title |
| `ScreenshotCapture.cs` | Captures the window via Win32 `PrintWindow` (`PW_RENDERFULLCONTENT`), works even when partially covered |
| `DatasetWriter.cs` | Name validation, folder creation, next-number lookup, PNG saving |

Performance notes:
- The window handle is resolved once at Start and reused for every frame.
- Capture and PNG encoding run on a background task driven by a `PeriodicTimer`, so the UI never blocks and captures never overlap.
- UI updates are posted asynchronously; Stop cancels the loop via `CancellationToken`.
- The loop stops itself with a visible error if the window closes, is minimized, capture fails, or a file cannot be written.

## Limitations

- Minimized windows cannot be captured (capture stops with an error).
- Some GPU/exclusive-fullscreen or protected content may come out black with `PrintWindow`.
- The captured image is the full window including borders/title bar.
