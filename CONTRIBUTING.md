# Contributing

Bug reports are welcome. Please include the app version, Windows version, graphics card model, PCI hardware ID, and LCD firmware version if it appears in the app. Describe the steps to reproduce the issue and what the preview and physical LCD showed.

## Making a change

1. Build and run the checks using the commands in the [README](README.md).
2. Keep UI and media changes testable without the GPU. Run read-only identification checks before experimenting with hardware.
3. When proposing another card revision, identify its PCI IDs and LCD variant, and document which commands were confirmed on that card.
4. Open a pull request with the change and the verification you performed. A screenshot or short recording helps for visible changes.

Please share a small reproducible image or GIF only if you have permission to publish it. Use a generated test image when possible.
