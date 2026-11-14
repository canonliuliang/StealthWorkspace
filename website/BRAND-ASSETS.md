# Website assets and release identity

The r15 website is a buildless static site hosted with the project's existing GitHub Pages workflow. No external fonts, icon libraries, analytics scripts or embedded frames are loaded.

## Stealth identity and screenshots

- `assets/app-icon.svg` and `assets/app-icon.png` are copied from the project's editable r15 brand assets in `Assets/`. The mark uses flat jade and graphite geometry; it is not a third-party logo.
- `assets/browser.png` is the r15 screenshot from `docs/images/r15-browser-dark.png`. It shows the actual browser in a controlled test window, not a rendered product mockup.
- `assets/media-library.png` is the r15 light-theme screenshot from `artifacts/overhaul-final/vault-light-640.png`. It is an empty, isolated test library with no personal media. The previous r12 media screenshot has been replaced rather than relabeled as r15.
- Screenshots retain their original content and aspect ratio. Images are local to the site, with explicit dimensions; the below-fold media image is lazy-loaded.

## Other marks

- The retained `assets/github-mark.svg` is the unmodified `GitHub Logos/SVG/GitHub_Invertocat_Black.svg` from [GitHub's official logo download](https://brand.github.com/GitHub_Logos.zip), retrieved 2026-11-14. [GitHub brand guidance](https://brand.github.com/foundations/logo). The r15 page currently uses a text GitHub link instead.
- The inline `windows-mark` is a four-pane vector used to identify Windows downloads. Windows and Microsoft marks belong to Microsoft.

Third-party marks are secondary to the Stealth brand. No affiliation or endorsement is implied.

## Release and claims

Both architecture download links target `v2026.11.14-r15-preview`. The existing Pages release gate must see both completed ZIP assets and `SHA256SUMS.txt` before publishing the website. The site does not bypass that gate.

The displayed 30% and 81% improvements refer respectively to the refactoring-stage .NET 8 same-host Debug ARM64 blank-control construction (6.18 to 4.32 ms) and the refactoring-stage .NET 8 50,000-entry synthetic no-match bookmark query (31-sample median, 3.0615 to 0.5950 ms). Shared-lock timing is described independently. The page makes no claim of universal website speedup, higher web FPS, reduced total process memory or anonymous browsing. Core-test counts are per architecture, with environment skips disclosed.