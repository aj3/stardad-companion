# StarDad Companion

A small Windows tray app that puts your StarDad file into the game when you
link your stardad.space account. It syncs every 3 hours by default, and you can
change or turn off the schedule in your account. The code is published so
you can check exactly what it does. See [LICENSE](LICENSE): you can read it and
build it to check it, but not copy or redistribute it.

## Installing

Download the installer from stardad.space/companion. It:

- shows the terms ([installer/License.rtf](installer/License.rtf)), which you
  accept before it installs;
- installs for your Windows account only, with no administrator prompt, into
  `%LOCALAPPDATA%\Programs\StarDad Companion`;
- adds Desktop and Start menu shortcuts and the `stardexs-sync:` link.

Uninstall it from Windows Settings; that removes the shortcuts and the link
too. The installer is built with WiX v5 from
[installer/Package.wxs](installer/Package.wxs).

## Its panel

Click or right-click the tray icon to open a panel in the website's colours.
It shows:

- the sync bar: a tracer runs along it to the red crosshair while a sync works,
  and lands a hit when it's done;
- each linked account, with its own **Sync** and **Unlink**;
- the app's actions.

More options expands within the screen's working area. When space is short,
the actions scroll and Quit remains visible. Syncs, schedule changes and update
checks stay quiet; results and required actions appear in the panel and website.
Account linking opens the approval page directly. An auto-detected game folder is
used for reading; installation permission remains a separate panel action.
Replacing an unrelated file, granting Windows access, restoring/removing files and
unlinking still require an explicit action and confirmation.

The panel is built with [Avalonia](https://avaloniaui.net), which is open
source and draws native windows; it doesn't embed a browser. Only Avalonia's
Windows backend is included.

## Automatic and manual syncs

It doesn't listen on a network port or update itself without you pressing
**Update**.
Nobody else can make it do anything, not even stardad.space's own staff: the
website has no way to reach your PC. The app only talks to stardad.space when
you press **Send to my PC** (or **Sync** in its menu), or on the default three-hour schedule or at the times you choose
for that PC under **Automatic sync** in your account, and only for an account
you linked on this PC yourself.

Newly linked PCs sync every 3 hours by default. Existing saved schedules, including
Off, are preserved. You can turn automatic syncing off, or pick every 3, 6 or 12
hours, or once a day at a set time on your PC's clock, and you can add "when the
game updates". While one is on, the app starts with Windows (for your Windows
account only), so it's running when a sync is due. When the last one is turned
off, or the app is uninstalled, it stops starting with Windows.

When you do, it:

1. Reads new lines in `LIVE\Game.log` and `LIVE\logbackups\*.log`. It picks
   out only two things: received blueprint names with their date, and
   completed contract titles with their mission id. It sends those to that
   account.
2. Asks stardad.space whether that account's file is current. If not, it
   downloads it.
3. If you agreed when you set it up, it installs the file:
   - It waits until the game is closed. It checks only the local process list
     for this, nothing online.
   - It writes a temporary file beside `global.ini` and swaps it in, so the
     game never sees half a file.
   - In `user.cfg`, only the `g_language` and `g_languageAudio` lines change.
   - It tells stardad.space the file's SHA-256 hash, so the website can keep
     that file as your backup.

## What it touches

| | Files |
|---|---|
| Reads | `LIVE\Game.log`, `LIVE\logbackups\*.log`, `LIVE\build_manifest.id` (the game's build number) |
| Writes | `LIVE\data\Localization\english\global.ini` (plus a temporary file beside it while swapping), `LIVE\user.cfg` |

Any other path is refused in code (`GameFolder.CheckWritable`). So is a
language folder that's really a link to somewhere else on your PC.

It never replaces a `global.ini` it didn't put there, for example one from
another tool or one you edited, without asking you first.

## Backups

The PC keeps one pending update while the game runs, surviving app/PC restarts.
Close the game and wait for Installed before relaunching it. stardad.space keeps the last two files you
installed, and your account page lists them. From the tray menu you can:

- **Put previous file back:** downloads the older of the two and installs it.
- **Remove StarDad from the game:** deletes the `global.ini` the app
  installed, and only that file. The language lines in `user.cfg` stay, since
  english is the game's default anyway.

The app also keeps a short `journal.log` of what it wrote (the last 200
lines), in `%LOCALAPPDATA%\StarDad\Sync`.

## Game updates

When the game updates, the app notices by reading `build_manifest.id` on your
PC. It then tells you your file is for the last patch. Nothing goes online
until you press **Send to my PC**.

## More than one account

You can link several stardad.space accounts on one PC. Each account's
**Send to my PC** link includes that account's public id: 16 characters that
aren't secret. The app uses that id only to choose between the accounts
already linked on that PC. Each account gets its own token. Whichever account
you press **Send to my PC** in, that account's file goes in.

## The `stardexs-sync:` link

The Send to my PC button opens a `stardexs-sync://sync/<account id>` link.
This app registers that link for your Windows account only.

The app reads nothing from the link except that id, and it must be exactly 16
hexadecimal characters. Any web page can open the link, but all it can do is
start the same sync with stardad.space for an account you already linked. It
can't pass in data, a different site, or a command.

## Downloads must be game text

The app installs a download only if it's UTF-8 text made of `key=value`
lines, 64 MB at most. Even a compromised server could only ever put text in
that one file. The app never runs anything it downloads.

## Administrator rights

The app runs as you. Windows may stop it writing to your game folder, for
example under Program Files. If so, the app asks once whether to run
`StarDadCompanion.exe --grant`. Windows then shows its own administrator prompt.

That step gives your Windows account permission to change only:

- `LIVE\data\Localization\english`;
- `LIVE\user.cfg`.

Then it exits. See `StarDexs.Sync.Core/FolderAccess.cs`.

## Linking

The app shows a code, opens stardad.space, and checks every 3 seconds until you
approve the code, for up to 10 minutes. It then stores that account's token,
encrypted with Windows DPAPI, so only your Windows account on this PC can read
it.

The app sends only the PC's name, which is shown on your account. You can
unlink the PC from either side: the website's account page, or the app's menu.

## Updates

When a sync, or **Check for updates** in the panel, finds a newer release, the
panel offers **Update to x.y.z**. The update happens only when you press it.

Each release is signed with StarDad' release key (ECDSA P-256), on the
maintainer's PC; the private key never goes anywhere else. The public half is
built into the app (`Updater.ReleaseKey`):

```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXBFazEisW/LQvPtUqhTjkeDxaSs04SWzT1VHURNF/9LlbAJwjKcv//Y1rOwia+84yXwdyuvW94o/JwqvX8RWqA==
```

`stardad.space/downloads/companion.json` names the release, its SHA-256 and the
signature over both (see `Updater.SignedText`). The app installs an update
only if:

- the signature checks out;
- the version is newer than its own;
- the download's SHA-256 matches.

So even someone in control of the website couldn't hand you a program that
wasn't signed with that key, or roll you back to an older one.

## Checking a download

Each release's SHA-256 is on stardad.space and in `companion.json`. To check
the code, build the commit for that release with the .NET 10 SDK:

```
dotnet publish StarDexs.Sync -c Release
```

Windows code signing (so Windows names the publisher) is planned.

## Layout

| Folder | What's in it |
|---|---|
| `StarDexs.Sync.Core` | Reading logs, finding the game, safe writes, the stardad.space calls. No user interface. |
| `StarDexs.Sync` | The tray icon (plain Win32), the panel (Avalonia), and the dialogs. |
| `StarDexs.Sync.Tests` | Tests. They use temporary folders and a fake site, never a real game install. |


### 0.4.4 reliability and resource use

Network metadata is capped at 64 KB and game-text downloads at 32 MB, including
responses without Content-Length. Malformed update metadata is rejected, redirects
are refused, and link approval stays on the configured website. Schedule checks
read cached findings off the UI thread; the panel animation pauses while hidden.
No new permissions, telemetry or background services are introduced.
