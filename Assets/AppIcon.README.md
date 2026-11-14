# Stealth identity — offset aperture

Original geometric artwork created for this project on 2026-11-14. Two opposing corners create an open, offset aperture: a private space, with a subtle S silhouette. Flat jade (#91E5CD) on graphite (#152622), without gradients, shadows, fine outlines, borrowed marks or font dependencies.

- AppIcon.svg: editable vector source, 1024-unit view box.
- AppIcon.Source.png: reproducible 1024px raster master.
- AppIcon.png: 256px runtime image for the title bar, lock page and new tab.
- AppIcon.ico: 16/20/24/32/40/48/64/128px classic DIB frames and a 256px PNG frame.
- tools/New-BrandMaster.ps1: rebuilds matching geometry and runs the icon packager.
- tools/New-AppIcon.ps1: generates Windows-compatible size variants.

Regenerate with ./tools/New-BrandMaster.ps1. The pre-overhaul icon remains in the verified snapshot and backup/pre-overhaul-20261114 Git tag. AppIcon.Legacy.svg is older historical artwork and is not used at runtime.

The executable embeds the ICO; AppWindow.SetIcon uses that same file. The shortcut installer retains its content-hashed icon copy under the user's local application data directory to avoid stale Shell icon caching. No existing shortcuts are silently redirected by rebuilding the source.
