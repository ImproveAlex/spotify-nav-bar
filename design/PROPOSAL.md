# Spotify Bar — design proposal

Original design proposal, prepared 15 October 2026 and revised after design feedback. Implementation is now in `src/`, and the standalone executable is in `artifacts/win-x64/SpotifyBar.exe`. See `../README.md` for setup, current behavior, and verification details.

Portability update: the current app stores preferences, encrypted sessions, logs, user documentation, and runtime support files under the user's Windows Documents folder in `SpotifyBar`. It runs from one portable executable. The earlier startup-registration option below has been replaced with Open data & help folder; the tables below remain the original design record.

Playback rollback (v1.3): at the user's request, the hidden-Chrome playback experiment has been removed. The current bar controls the Spotify app through the Web API again. Spotify must stay open or minimized to play on this PC; closing the bar leaves Spotify playing. There is no browser engine or Web Playback SDK dependency. Browser authorization is used only for account sign-in. See `../README.md` for current setup and verification.

## The idea

A native Windows music remote that stays at the top edge of one monitor. **The player is the only persistent interface.** A dark charcoal surface, restrained green accents, album artwork, and clear playback controls keep everything within a fixed 120 physical pixel height. Search, devices, and settings open temporary panels below it only when clicked; the bar itself never expands. Sign-in and setup appear only when authentication or initial configuration is needed.

`player-1920x120.svg` is the main-bar layout at 1920 × 120 and represents the normal running interface. Earlier concept boards and local design notes are excluded from the source repository. The temporary panels are never all visible in the running app. There is no separate states strip or notification feed. Displayed track names and artwork in the SVG are fictional placeholders. The app uses Spotify metadata and artwork with links to the current track.

## Technology and delivery

Recommendation: **C# + WPF on .NET 10 LTS**, using XAML for the interface and Windows APIs for monitor placement, topmost behavior, and optional desktop space reservation. WPF is a Windows desktop framework with the styling and layout features this app needs. .NET 10 is currently an LTS release. [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/), [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

Deliver a self-contained `SpotifyBar.exe` for Windows x64, with a separate ARM64 build if needed. Bundle the runtime so the user does not need to install .NET. A single executable can still extract native dependencies when launched and saves preferences under the user's local application data. This is a Windows desktop app using the managed .NET runtime. [Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

## Main window

| Area | Proposed content and behavior |
| --- | --- |
| Left | 72 × 72 artwork, track title, artist; click opens the track in Spotify. Long titles truncate with a tooltip. |
| Center | Shuffle, previous, play/pause, next, repeat; elapsed time, seek slider, total duration. Repeat cycles off → context → track. |
| Right | Search, output device, volume/mute, settings, and a persistent close button. |

Default placement is the main monitor's top edge, spanning that monitor's full width. Settings select another monitor. Width follows display changes; the bar does not span the combined multi-monitor desktop.

Interpret **120px literally as 120 physical display pixels**, including borders, at every display scaling setting. WPF's logical units must be converted using the selected monitor's DPI. A naive WPF Height=120 would become 180 physical pixels at 150% scaling, so that is not the proposed behavior. The physical limit means controls occupy less physical space on high-density displays. [WPF units and DPI](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).

The bar stays above ordinary desktop windows without repeatedly taking keyboard focus. Default “Reserve space below bar” is on, so maximized windows start below it. Switching this off makes it an overlay. Closing restores the reserved desktop area. The Windows appbar mechanism supports reserving an edge of the desktop. Fullscreen applications can cover an appbar; Windows specifically asks appbars to yield to fullscreen windows. The lock screen and secure desktop also remain controlled by Windows. [Windows appbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars), [Fullscreen notification](https://learn.microsoft.com/en-us/windows/win32/shell/abn-fullscreenapp).

At narrower widths, compact the device and volume controls and move secondary controls into an overflow panel, retaining play/pause, previous, next, search, and close. Support keyboard navigation, focus indicators, accessible button names, and tooltips. Space controls playback only while the player has focus and no text input is active. Escape closes a temporary panel; Alt+F4 quits.

## Screens and interactions

| Screen or state | Mockup and intended behavior |
| --- | --- |
| First launch / app setup | Only when configuration is missing, show a setup panel with a Client ID field, developer dashboard link, and the exact redirect URI to register. Save enables Connect Spotify. Otherwise App setup is accessible through Settings. The Client ID is public; no client secret is requested. |
| Signed out | When sign-in is needed, show “Connect to Spotify” with a green Connect Spotify button and an App setup link. Playback controls are inactive. Close remains available. Dismiss the panel after successful sign-in; it stays hidden during normal use. |
| Browser authentication | Open the system browser for Spotify consent. The bar says “Waiting for sign-in…” with Cancel and Retry. Successful consent returns to the connected player. |
| Playing | Real artwork and metadata, green pause button, current progress, current output device. Actions reflect confirmed playback state. |
| Paused | Play triangle replaces pause, progress stops, and the track remains visible. |
| Find a song | Only clicking Search opens a panel below it to accept a song/artist query or Spotify track link/URI. Results have artwork, title, artist, Play now, and Add to queue. Clicking Play now replaces current playback; adding to queue preserves it. Include loading, no results, invalid link, and search failure messages within this panel. |
| Choose a device | Only clicking the current device opens a panel listing available Spotify Connect devices, marking the active one, and offering Refresh. Selecting an eligible device transfers playback. Names shown in the concept are examples; only devices Spotify returns are selectable. |
| No device | “Open Spotify to start playback” with Open Spotify and Refresh. Launching Spotify is followed by rediscovery; it cannot guarantee a device becomes available immediately. |
| Settings | Only clicking the gear opens Settings: monitor selector; Reserve space below bar (on); Start with Windows (off); account name; App setup; Sign out; Quit Spotify Bar. Always-on-top is a fixed behavior. |
| Offline | Keep the last track visible, stop guessing progress, disable remote actions, and reconnect automatically with backoff. Show a small inline “Offline” message in the player only while this blocks use. |
| Expired session | Refresh the token silently; if revoked or refresh fails permanently, offer a small inline Reconnect action. Open sign-in when the user chooses it. |
| API restriction | Explain account/app access or device restrictions inline. Disable controls Spotify says are unavailable, including volume on unsupported devices. |
| Rate or quota limit | Show a temporary limit message and stop calls. Respect Retry-After where supplied; do not hammer the API or invent a quota reset time. |
| Closing | The rightmost X, Alt+F4, and Quit Spotify Bar fully terminate the program and its panels. Closing leaves Spotify playing; playback belongs to Spotify. No hidden tray process. |

Temporary panels dismiss on Escape, their close button, or click outside. Only one is open at a time, and all start closed on an authenticated launch. Pausing simply changes the player button to Play and stops the progress timer. Playback and connection conditions never create a separate states panel or notification feed; show a brief inline message only when user action is required or an operation cannot complete. A single application instance prevents duplicate bars. Settings and tokens are stored locally; sign-out clears stored tokens. No passwords are collected by the app.

## How Spotify connects

The Web API controls an existing Spotify client or Spotify Connect device. **It does not stream audio into this bar.** On this PC, Spotify must be open and discoverable; another available Connect device can also be controlled. Premium supports the requested playback controls. [Start/resume playback](https://developer.spotify.com/documentation/web-api/reference/start-a-users-playback).

For personal use, create an app in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard), select Web API, and enter its Client ID in App setup. Development mode requires a Premium app owner and supports up to five allowlisted authenticated users. These limits suit this personal utility but matter if it is later shared. [Quota modes](https://developer.spotify.com/documentation/web-api/concepts/quota-modes).

Use Authorization Code with PKCE, a fresh verifier and state per attempt, and a temporary loopback callback. Proposed registered redirect URI: `http://127.0.0.1:43821/callback`. The app checks the returned state and closes the listener after success, cancellation, or timeout. Spotify allows HTTP loopback IP addresses and disallows `localhost` as a redirect host. [PKCE](https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow), [Redirect URIs](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri).

Request `user-read-playback-state`, `user-read-currently-playing`, and `user-modify-playback-state` for the planned playback features. Protect refresh tokens using Windows user-scoped data protection; never embed a client secret or log tokens. Use asynchronous HTTP, serialize playback mutations, debounce search/volume/seek changes, and refresh playback state after commands. Use a local progress timer between bounded polls, with slower polling when idle. Tune polling against real API quota behavior during integration.

Spotify's current development-mode documentation still lists search, queue, playback state, devices, play/pause, seek, shuffle, repeat, volume, and transfer playback. Its July 2026 update makes development quotas shared across apps belonging to the same developer account. [Available endpoints](https://developer.spotify.com/documentation/web-api/references/changes/february-2026), [July 2026 update](https://developer.spotify.com/documentation/web-api/references/changes/july-2026).

## Implementation sequence after design review

1. Build the native bar and local demonstration states; verify exact physical dimensions, monitor switching, display scaling, focus, and process termination.
2. Add app setup, browser PKCE sign-in, protected token storage, and real device discovery.
3. Connect playback, search, queue, seek, volume, shuffle, repeat, and settings.
4. Verify authentication cancellation/refresh, no-device recovery, unsupported actions, network loss, and quota backoff; publish the self-contained executable.

Before implementation, the proposed defaults to review are top-edge placement, 120 physical pixels, desktop space reservation enabled, a dark theme, and closing the bar leaving Spotify playback running.
