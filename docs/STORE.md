# Microsoft Store: where File Mind stands

Honest status as of v0.12.0. "Done" means it exists in this repository; "You" means only the account owner can do it.

## Verdict: not ready to submit yet

Two things block it, and one is a decision for you:

1. **No MSIX package exists.** The Store takes packaged apps (MSIX), and File Mind ships as a zip. The package needs the
   identity Partner Center gives you, so it cannot be finished before you reserve the app name.
2. **The Store's rules on downloaded code.** File Mind downloads PyTorch (native code) on first run. The Store's policy
   against apps that download and run code makes that a certification risk. The model files are data and are fine, but must
   be disclosed.
   **Recommended fix:** the Store build bundles the CPU PyTorch inside the package (about 0.6 GB larger), and offers the
   NVIDIA (CUDA) version as an optional, clearly described download inside Settings, or leaves it out of the Store build.
   The size limit is not a problem (MSIX allows 25 GB).
3. **Decision for you:** the MSIX must use `runFullTrust` (File Mind starts its engine as a separate process). That is
   allowed for desktop apps, but you must write a justification in Partner Center: "Launches the bundled local AI engine
   process that reads the user's chosen folders."

## Checklist

| Item | Status |
|---|---|
| Partner Center developer account | You (individual accounts pay a one-time registration fee; check the current price) |
| Reserve the name "File Mind" | You |
| MSIX project: `Package.appxmanifest` with the exact Identity/Publisher Partner Center gives, 4-part version (`0.12.0.0`), `TargetDeviceFamily Windows.Desktop` min `10.0.19041.0`, capabilities `runFullTrust`, `internetClient`, `microphone` | To do after the name is reserved |
| Tile and logo assets (44, 150, 310x150, 50 px StoreLogo, with scales) | To do (the icon source is `docs/icon.png`) |
| When packaged, keep data under the package's `LocalFolder` so Settings > Storage shows the real paths | To do with the MSIX |
| Self-update off in the Store build | Done: the app detects a `WindowsApps` install and never replaces its own files |
| Privacy policy page | Draft in `docs/PRIVACY.md`. You: add contact details and host it at a public URL (GitHub Pages works) |
| Third-party notices: FFmpeg (LGPL/GPL), fonts (SIL OFL), Phosphor icons (MIT), the model licence | Listed in the README. Confirm the model's terms on its Hugging Face card before listing |
| Age rating questionnaire | You (expect "everyone") |
| Listing text, at least one 1366x768 screenshot, support URL | Screenshots exist in `docs/`. You: write the text |
| Windows App Certification Kit on the built `.msix` | You, on a real PC, before submitting |
| Private test flight in Partner Center | You |
| Code signing | The Store signs the package for you. The zip stays unsigned (SmartScreen warns) until you buy a certificate |

## Suggested order
1. Reserve the name and create the account.
2. Tell me the Identity Name and Publisher from Partner Center; I build the MSIX project and the CPU-bundled Store variant.
3. You run the certification kit and a private flight; fix what it reports; submit.
