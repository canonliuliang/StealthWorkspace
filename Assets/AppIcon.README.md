# Stealth app icon — r10

Original generated artwork for the Stealth private browser, created on 2026-11-13 using the built-in `image_gen` tool (generate mode, transparent background). The tool does not expose a selectable model-version parameter, so this asset is not labelled as a verified “Image 2.6” output.

- Selected master: `AppIcon.Source.png`, 1254 × 1254, original generated RGBA preserved.
- Runtime bitmap: `AppIcon.png`, 256 × 256.
- Windows icon: `AppIcon.ico`, classic 32-bit DIB/AND-mask frames at 16, 20, 24, 32, 40, 48, 64 and 128 pixels, plus a 256-pixel PNG frame for large displays. Small frames use the Windows Shell compatibility format.
- `../tools/New-AppIcon.ps1` mechanically resamples and packages the master; it no longer draws the old S/shield icon.
- `AppIcon.Legacy.svg` preserves the previous icon for reference only; it is not used by the application.
- Two additional generated variants were evaluated but not selected. The first version has the preferred compact silhouette.

Rebuild from the project directory with `./tools/New-AppIcon.ps1`. The ICO is embedded in the executable and used by the window; the PNG is used by the title bar and browser start page. The shortcut installer copies the same ICO to `%LOCALAPPDATA%\StealthWorkspace\Icons`, checks its SHA-256, and uses a filename containing its content hash. This separates the shell resource from the build directory and avoids stale Explorer caching.

On the current Windows 11 ARM64 26200.9974 host, the Shell's shortcut icon handler returned `E_FAIL` for the icon under the Chinese project path even though direct `ExtractIconExW` succeeded. Both desktop shortcuts returned the correct icon through the same handler after using the per-user local copy. This is an observation on this machine, not a claim that all Unicode Windows paths are unsupported. Native shortcut creation and removing the expandable icon metadata did not solve the problem and were not added to the installer.

## Prompt used for the selected master

Use case: logo-brand. Asset type: production desktop application icon, square 1024x1024 PNG with real transparent alpha, for a Windows 11 private browser named Stealth. Create ONE original polished Fluent-style app icon, not a concept board and not a mockup. Subject: a single bold, softly rounded protective shell formed from two interlocking folded ribbons, suggesting a private browsing window sheltered inside. Strong immediately recognizable silhouette; a simple central rounded negative-space opening. Rich cobalt and azure blue body with a restrained luminous cyan inner fold. Subtle layered 3D depth, smooth satin/glass-like material, soft top-left studio light, precise clean edges. Mostly front-facing, not a dramatic isometric object. Occupy approximately 84% of the square with even clear margins. The shape must remain readable at 16, 24, 32 and 48 pixels. Aim for the visual refinement and simplicity of native Windows 11 Fluent application icons while remaining an original identity. No letter S, no text, no words, no padlock, no gear, no fingerprint, no outline badge, no tiny decoration, no checkerboard drawn into the image, no solid background plate, no Windows logo, no copied Microsoft product symbol, no watermark. Transparent outside the object; keep any object shadow extremely subtle.

Visual reference: [Microsoft app icon design guidance](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design). This is an independent app identity, not an official Microsoft icon or affiliation.
