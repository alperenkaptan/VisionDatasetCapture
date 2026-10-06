# VisionDatasetCapture

VisionDatasetCapture is a WPF (.NET 6) app for capturing Windows window screenshots and building image datasets for computer vision work.

## Capabilities

- Capture screenshots from a selected process window
- Auto-timed capture or manual keystroke capture
- Live preview stream with zoom and pan
- Eyedropper / color picker for sampling pixels from the preview
- Multi-rule color effects with HSV tolerances
- Post-processing pipeline for crop, brightness, contrast, saturation, gamma, grayscale, and resize
- Freeze-frame and mask-overlay options for color effects
- Export, import, and reset capture settings
- Save numbered PNG files without overwriting previous sessions

## Typical workflow

1. Select a process/window.
2. Choose the capture mode.
3. Configure post-processing or color effects if needed.
4. Enter a dataset name.
5. Click **Start** and review the live preview.
6. Click **Stop** when finished.

## Output

Images are saved to:

`<app output dir>\<DatasetName>\<DatasetName>_N.png`

Example:

`bin\Debug\net6.0-windows\Bobber\Bobber_0.png`

## Notes

- The app captures the visible window area.
- Minimized windows cannot be captured.
- It is intended for dataset capture and image preparation, not model training or detection.

