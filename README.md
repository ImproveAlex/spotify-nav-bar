# Spotify Bar

A native Windows remote for the Spotify desktop app, built with C#, WPF, and .NET 10. The bar spans one monitor and stays **120 physical pixels high**, including with Windows display scaling. It stays above ordinary desktop windows; search, devices, settings, and sign-in appear only when needed.

## Run and test

Download **SpotifyBar-win-x64.zip** from the [latest GitHub release](https://github.com/ImproveAlex/spotify-nav-bar/releases/latest), extract it, and run `SpotifyBar.exe`.

Double-click **`artifacts\win-x64\SpotifyBar.exe`**. This is a portable executable containing the player and its runtime. No installer, administrator access, or separate .NET installation is needed. Share just this executable; each user configures their own account access.

1. Open the Spotify desktop app and sign in to your Premium account.
2. Launch Spotify Bar. An existing saved session remains usable; otherwise follow the account setup below.
3. Start a song in Spotify once if needed, then open **Devices** in the bar, refresh, and select this PC's Spotify app.
4. Minimize Spotify. Test play/pause, next/previous, seek, volume, shuffle/repeat, and **Search → Play now** or add to queue.
5. Close the bar with its X or Alt+F4. The bar exits completely and Spotify keeps playing independently.

**This build restores playback through the Spotify app. It includes no Chrome playback engine, Web Playback SDK, browser profile, or browser processes for playback.** A normal browser page is still needed for Spotify authorization when connecting/reconnecting an account. Dashboard/help links also open only when explicitly selected. Artwork and the track link open the native Spotify app.

The Spotify app must stay open or minimized to produce audio on this PC. Another available Spotify Connect device can also be controlled. The bar cannot play audio on its own or create a playback device.

For an account-free preview:

```powershell
.\artifacts\win-x64\SpotifyBar.exe --demo
```

The demo uses fictional songs, makes no Spotify requests, and does not load or change saved sessions or preferences. Its runtime/help files still go in Documents. Close the demo before launching normally.

## Account setup

1. Sign in at the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) and create a personal app with **Web API** enabled. Web Playback SDK is not required.
2. Register this exact redirect URI:

   ```text
   http://127.0.0.1:43821/callback
   ```

3. Copy the app's **Client ID** into **App setup** in Spotify Bar and save. Do not enter the client secret.
4. Choose **Connect Spotify** and complete Spotify's authorization in your normal browser. Keep the Spotify desktop app signed in to the same account.
5. Return to the bar and select the Spotify app in **Devices** if necessary.

Sign-in expires after three minutes and can continue after its panel closes; reopen it to cancel. Spotify Premium and developer app access are required. Additional accounts need access through the app's user management. Existing encrypted sessions from both previous builds remain compatible; this rollback does not force new streaming consent.

The requested permissions are `user-read-playback-state`, `user-read-currently-playing`, and `user-modify-playback-state`. The app uses Authorization Code with PKCE, a fresh verifier/state, and a temporary loopback callback. No password or client secret is collected. Token refresh happens silently; sign-out clears the saved session.

## Saved files

All app-managed saved files use the current user's **Windows Documents folder**, including when redirected to OneDrive or another drive:

```text
Documents\SpotifyBar\
  settings.json       Client ID, monitor, desktop-space preferences
  session.dat         Windows-user-encrypted Spotify session
  errors.log          Unexpected error details, only if needed
  README.txt          Automatically saved user guide
  SPOTIFY-SETUP.txt   Automatically saved setup instructions
  Runtime\            Embedded player and reusable bundled runtime cache
  PreviousData\       Conflicting legacy files, if needed
```

**Settings → Open data & help folder** opens this location. The executable can be kept anywhere. The launcher verifies the embedded player with SHA-256, extracts it under `Runtime`, and sets the runtime extraction directory for its child process only. It does not install a runtime, change global environment variables, register automatic startup, or fall back to AppData if Documents is unavailable.

Recognized settings/session/log files from the earliest AppData build migrate into Documents. Current Documents data takes priority; conflicts go into `PreviousData`. Unrelated files and user notes remain untouched. The help files are app-managed and update with each build.

A `Chrome` folder left by the former browser-playback build is unused and may be deleted after closing that build. The current app never opens or recreates it. Older versioned runtime caches may likewise be removed while the app is closed. To remove Spotify Bar entirely, close it and delete its executable and `Documents\SpotifyBar`; Spotify itself is unaffected.

## Controls and placement

- Play/pause, skip, seek, shuffle, repeat, volume, queue, search, and transfer all use Spotify's Web API. Mutations target the active playback device. Unsupported or restricted controls are disabled.
- Devices lists actual devices returned by Spotify, with **Open Spotify** and Refresh for discovery. Selecting a device transfers playback; a paused session stays paused.
- Search accepts song/artist text, Spotify track URLs, or track URIs. Play now replaces current playback; + adds to the queue.
- Settings selects a monitor, reserves desktop space or overlays windows, opens help/data, signs out, and quits.
- Escape, a panel's X, or clicking outside dismisses it. Only one panel can be open. The main X and Alt+F4 fully exit the bar, without a tray process.

The bar is always above ordinary windows. Reserve mode keeps maximized windows beneath it. Windows fullscreen applications and the secure desktop may appear above it. Disconnecting the selected monitor falls back to the primary display. The fixed height is measured in physical display pixels, so the controls occupy less physical space on high-density screens.

## Implementation and verification

The production app has no third-party NuGet dependencies and no browser runtime dependency. `SpotifyBar.Core` handles API calls, PKCE authentication, token refresh, state parsing, and loopback sign-in. The WPF app implements the bar, panels, placement, storage and DPAPI session encryption. `SpotifyBar.Launcher` packages the self-contained player and controls Documents-only runtime extraction.

Requests run asynchronously. Search, seek, and volume are debounced. Progress animates locally between playback polls (normally seven seconds while playing, twenty seconds while paused/idle). Network failures back off; API 429 responses stop requests until Retry-After, with a conservative fallback if absent. The bar does not claim to know Spotify's quota reset time.

Build on Windows with the .NET 10 SDK. The script prefers `.tools\dotnet` when present:

```powershell
# Build and run API/authentication/storage checks.
.\build.ps1

# Build, test, package the portable x64 executable, and check the native UI.
.\build.ps1 -Publish -SmokeTest

# Optional separate ARM64 package; test it on ARM64 hardware.
.\build.ps1 -Publish -Runtime win-arm64
```

The service checks use fake HTTP responses, a real local sign-in callback and Windows DPAPI. They verify playback command routing/encoding, playback metadata and device restrictions, PKCE, link validation, token refresh/revocation/concurrency, saved-session compatibility, sign-out races, rate backoff, Documents storage, migration, and launcher argument quoting.

The native smoke checks use demo data and an isolated `artifacts\portable-smoke\Documents\SpotifyBar` folder. They verify physical height/monitor width, topmost style, player-only startup, pause, on-demand panels, panel dismissal, help generation and actual native runtime extraction beneath Documents. Screenshots and the JSON report are in `artifacts\verification`. They do not authenticate or demonstrate live Spotify playback. Verify real playback with your account, plus monitor switching and 125%/150% scaling on your hardware.

### Automatic GitHub releases

Every push to `main` runs `.github/workflows/release.yml` on a clean Windows runner. It installs the .NET 10 SDK, runs the service checks, builds the self-contained portable x64 executable, and publishes a GitHub release only after its download assets are uploaded and verified. Failed builds or tests do not publish a release. Interactive UI checks remain a local verification step.

Each run uses a unique tag such as `v1.3.0-build.1.1` (app version, workflow run number, and attempt). Runs are not cancelled by newer pushes, so each successful push can produce its own release. An older run finishing later does not replace the current main commit's release as latest. The workflow can also be run manually from Actions on `main`.

Each release attaches `SpotifyBar-win-x64.zip`, containing only `SpotifyBar.exe`, and a separate `SHA256SUMS.txt`. Settings, sessions, local tools and test reports are never packaged. Build paths in managed debug information are mapped to a generic path. GitHub supplies the workflow's short-lived release token; no personal token or Spotify credentials are required in repository secrets.

## Troubleshooting

- **No device / no audio:** open Spotify on this PC, start a song once, refresh Devices, and select it. Check Spotify and Windows volume. Keep Spotify open or minimized.
- **Redirect error:** use the exact callback above, including port and `/callback`, with `127.0.0.1` rather than `localhost`.
- **Access refused:** check Premium, developer app user access, and device restrictions.
- **API limit:** wait for backoff; repeated clicking does not bypass it.
- **Sign-in port busy:** close the other listener on port 43821 before reconnecting.
- **Documents unavailable:** restore write access before starting; settings, session, documentation, and runtime cache use that location.

The original design record and mockups are in `design/`. This README describes the current app; the browser playback experiment has been rolled back.

## Source repository

Only source code, tests, build configuration and generic project documentation belong in Git. Local SDKs, executables, build/test artifacts, runtime data, saved settings/sessions, environment files and private keys are excluded by `.gitignore`. Each user configures their own account through the app. See [CONTRIBUTING.md](CONTRIBUTING.md) for the review steps before committing or sharing files.
