# Website assets and release identity

The Stealth website is a buildless static site hosted with the project's existing GitHub Pages workflow. No external fonts, icon libraries, analytics scripts or embedded frames are loaded.

## Stealth identity and screenshots

- `assets/app-icon.svg` and `assets/app-icon.png` are copied from the project's editable r15 brand assets in `Assets/`. The mark uses flat jade and graphite geometry; it is not a third-party logo.
- `assets/browser.png` is the r15 screenshot from `docs/images/r15-browser-dark.png`. It shows the actual browser in a controlled test window, not a rendered product mockup.
- `assets/r16-tab-search.png` shows the r16 recently closed list in an isolated Windows test session. A lossless crop retains the entire 2158 × 1469 main window and excludes the WinApp CLI capture labels and separate tooltip panel.
- `assets/media-library.png` is the r15 light-theme screenshot from `artifacts/overhaul-final/vault-light-640.png`. It is an empty, isolated test library with no personal media. The previous r12 media screenshot has been replaced rather than relabeled as r15.
- Screenshots retain their original content and aspect ratio. Images are local to the site, with explicit dimensions; the below-fold media image is lazy-loaded.

## Other marks

- The retained `assets/github-mark.svg` is the unmodified `GitHub Logos/SVG/GitHub_Invertocat_Black.svg` from [GitHub's official logo download](https://brand.github.com/GitHub_Logos.zip), retrieved 2026-11-14. [GitHub brand guidance](https://brand.github.com/foundations/logo). The page currently uses a text GitHub link instead.
- The inline `windows-mark` is a four-pane vector used to identify Windows downloads. Windows and Microsoft marks belong to Microsoft.

Third-party marks are secondary to the Stealth brand. No affiliation or endorsement is implied.

## Release and claims

Both architecture links target the versioned x64 and ARM64 archives in `v2026.11.20-r16-preview`. The Pages workflow checks for both ZIPs and the checksum manifest before deploying the download page.
