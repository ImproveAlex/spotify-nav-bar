namespace SpotifyBar;

internal static class UserDocumentation
{
    public const string Readme = """
SPOTIFY BAR - PORTABLE SPOTIFY REMOTE

Double-click SpotifyBar.exe to run. No installer, administrator access, or
separate .NET installation is needed on a standard Windows 10/11 PC.
The Spotify desktop app produces the audio. Keep it open or minimized.
Spotify Bar does not run a browser or a separate audio engine for playback.
A browser page is used only for Spotify authorization when connecting an account.

YOUR FILES
All files saved by Spotify Bar belong in the Windows Documents folder:
  Documents\SpotifyBar\settings.json       - Client ID and player preferences
  Documents\SpotifyBar\session.dat         - Windows-user-encrypted Spotify session
  Documents\SpotifyBar\errors.log          - Only created for unexpected errors
  Documents\SpotifyBar\README.txt          - This guide
  Documents\SpotifyBar\SPOTIFY-SETUP.txt    - Sign-in instructions
  Documents\SpotifyBar\Runtime\            - Embedded player and bundled runtime cache

The Windows Documents location is used even if redirected to OneDrive or
another drive. Settings > Open data & help folder opens the actual location.
Runtime files are reusable support files for this executable, not an installed
program. The app does not register itself with Windows or set up automatic startup.
The launcher and player both exit when you close the bar.

GET CONNECTED
Read SPOTIFY-SETUP.txt, then open App setup in the player and connect Spotify.
Open the Spotify desktop app and sign in to the same account. Start a song there
once if the device is missing, then refresh Devices in the bar and select this PC.
Spotify can stay minimized while the bar controls it. An available Spotify
Connect device on another PC, phone, or speaker can also be selected.

CONTROLS
Play/pause, previous/next, shuffle, repeat, volume/mute and the seek slider
control Spotify through its Web API. Unsupported device controls are disabled.
Search accepts song/artist names and Spotify track links. Play starts the
selected track; + adds it to your queue. Devices changes the playback device.
Open Spotify and the artwork/track link use the Spotify desktop app.
Settings selects a monitor and reserves desktop space or overlays windows.
Only the 120-pixel player stays visible. Other panels open when clicked.
Escape, the panel X or clicking outside closes a panel. The player X or
Alt+F4 quits the bar completely. Spotify continues playing independently.

SESSION AND CLEANUP
Your Spotify password and client secret are never collected. Windows data
protection encrypts session.dat for your Windows account. Sign out deletes it.
Keep your own notes in a separate file; these two help files are app-managed.
When the app is closed, Runtime can be deleted and is recreated on launch.
To remove Spotify Bar, delete its executable and Documents\SpotifyBar.
This removes the saved Client ID and preferences and requires signing in again
if you run the executable later. No uninstaller is needed.

UPGRADING OR ROLLING BACK
Existing settings and encrypted sessions from earlier builds remain usable.
No streaming permissions or Web Playback SDK are required by this version.
A Chrome folder left by the former browser-playback build is unused and can
be deleted after closing that old build. No browser profile is created here.
Recognized files from the early AppData\Local\SpotifyBar build migrate into
Documents. Existing Documents data takes priority; conflicts are preserved in
PreviousData. No unrelated files are moved.

TROUBLESHOOTING
No device: open Spotify, start a song there once, then refresh Devices.
No audio: verify the selected device in the bar and volume in Spotify/Windows.
Access denied: check Premium, app user access and the device's restrictions.
Offline: check your connection; the bar retries with backoff.
API limit: wait for the retry interval; clicking again does not bypass it.
Fullscreen apps and the secure desktop can appear above the player.
If Documents is unavailable or read-only, restore access before running the app.
Spotify Bar will not silently save your data in another folder.
""";

    public const string Setup = """
SPOTIFY BAR - SPOTIFY SETUP

1. Sign in to https://developer.spotify.com/dashboard with your Premium account.
2. Create an app for personal use and enable Web API.
3. In that app's settings, register this exact redirect URI:

   http://127.0.0.1:43821/callback

   Use 127.0.0.1, not localhost. The port and /callback must match.
4. Copy the app's 32-character Client ID. Never use the client secret here.
5. Run SpotifyBar.exe. In App setup, paste the Client ID and save it.
6. Click Connect Spotify and finish authorization in your browser.
   The browser is used only for sign-in, not for playback.
7. Open the Spotify desktop app, sign in to the same account and start a song.
   Refresh Devices in the bar and select this PC, then minimize Spotify.
8. Use the bar to pause/resume, skip, seek, change volume, or search and Play now.

If another Spotify account uses your development app, add it to the app's user
management allowlist. Spotify app access and Premium are required.
Sign-in waits up to three minutes and can continue after its panel closes.
Reopen the panel to cancel or retry. Port 43821 must be free on this PC.

The app uses browser authorization with PKCE and these playback permissions:
  user-read-playback-state
  user-read-currently-playing
  user-modify-playback-state

The saved Client ID is public app configuration. Your refresh token is encrypted
in Documents\SpotifyBar\session.dat for your Windows user. Settings > Sign out
deletes the saved session. Earlier saved sessions do not need renewed streaming
consent. Web Playback SDK and Chrome are not needed for this version.

TESTING
With Spotify open or minimized, confirm play/pause, next/previous, seek, volume,
search, queue, shuffle/repeat, and device selection. Closing the bar must fully
exit it while Spotify keeps playing. Signing out must clear the bar's session.
Automated checks use simulated Spotify responses and demo UI data. Live Spotify
playback still needs verification with your account.
""";
}