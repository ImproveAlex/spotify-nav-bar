# Working on Spotify Bar

Use Windows and the .NET 10 SDK, then run `./build.ps1` to build and check the project. `./build.ps1 -Publish -SmokeTest` also creates the portable executable and runs the native UI checks. See [README.md](README.md) for Spotify setup and manual playback verification.

## Keep personal data out of Git

The repository contains source code, tests, build configuration and project documentation. Each user enters their own Spotify Client ID in the running app and authenticates through Spotify. Do not put real Client IDs, client secrets, access tokens or refresh tokens in source files or documentation. Test tokens and identifiers must remain fictitious.

The app saves settings, encrypted sessions, logs, help and runtime files in the user's Windows Documents folder under `SpotifyBar`. Do not copy that folder into the repository. `.gitignore` also excludes these files if they are copied here, along with environment files, private keys, local SDKs, build output, IDE state and test reports/screenshots. Early personalized design notes and the original concept board remain local; the generic SVG mockup is included.

Before committing, review `git status --short` and `git diff --cached`. `.gitignore` does not remove files that were already committed, and encrypted session files are still private data. Avoid force-adding ignored files. Screenshots and logs may contain account details, song history or local paths; inspect them separately before sharing.

Share the portable executable separately from the source repository when needed. Recipients configure their own account; do not bundle `settings.json`, `session.dat` or any Documents data with it.
