# StarDexs Companion

A small Windows tray app that puts your StarDexs file into the game when you
press **Send to my PC** in your stardexs.com account. The code is published so
you can check exactly what it does. See [LICENSE](LICENSE): you can read it and
build it to check it, but not copy or redistribute it.

## It only acts when you ask

It doesn't check in on a timer, listen on a network port, or update itself.
Nobody else can make it do anything, not even stardexs.com's own staff: the
website has no way to reach your PC. The app only talks to stardexs.com when
you press **Send to my PC** (or **Sync** in its menu), and only for an account
you linked on this PC yourself.

When you do, it:

1. Reads new lines in `LIVE\Game.log` and `LIVE\logbackups\*.log`. It picks
   out only two things: received blueprint names with their date, and
   completed contract titles with their mission id. It sends those to that
   account.
2. Asks stardexs.com whether that account's file is current. If not, it
   downloads it.
3. If you agreed when you set it up, it installs the file:
   - It waits until the game is closed. It checks only the local process list
     for this, nothing online.
   - It writes a temporary file beside `global.ini` and swaps it in, so the
     game never sees half a file.
   - In `user.cfg`, only the `g_language` and `g_languageAudio` lines change.
   - It tells stardexs.com the file's SHA-256 hash, so the website can keep
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

The PC keeps no backup copies. stardexs.com keeps the last two files you
installed, and your account page lists them. From the tray menu you can:

- **Put previous file back:** downloads the older of the two and installs it.
- **Remove StarDexs from the game:** deletes the `global.ini` the app
  installed, and only that file. The language lines in `user.cfg` stay, since
  english is the game's default anyway.

The app also keeps a short `journal.log` of what it wrote (the last 200
lines), in `%LOCALAPPDATA%\StarDexs\Sync`.

## Game updates

When the game updates, the app notices by reading `build_manifest.id` on your
PC. It then tells you your file is for the last patch. Nothing goes online
until you press **Send to my PC**.

## More than one account

You can link several stardexs.com accounts on one PC. Each account's
**Send to my PC** link includes that account's public id: 16 characters that
aren't secret. The app uses that id only to choose between the accounts
already linked on that PC. Each account gets its own token. Whichever account
you press **Send to my PC** in, that account's file goes in.

## The `stardexs-sync:` link

The Send to my PC button opens a `stardexs-sync://sync/<account id>` link.
This app registers that link for your Windows account only.

The app reads nothing from the link except that id, and it must be exactly 16
hexadecimal characters. Any web page can open the link, but all it can do is
start the same sync with stardexs.com for an account you already linked. It
can't pass in data, a different site, or a command.

## Downloads must be game text

The app installs a download only if it's UTF-8 text made of `key=value`
lines, 64 MB at most. Even a compromised server could only ever put text in
that one file. The app never runs anything it downloads.

## Administrator rights

The app runs as you. Windows may stop it writing to your game folder, for
example under Program Files. If so, the app asks once whether to run
`StarDexsCompanion.exe --grant`. Windows then shows its own administrator prompt.

That step gives your Windows account permission to change only:

- `LIVE\data\Localization\english`;
- `LIVE\user.cfg`.

Then it exits. See `StarDexs.Sync.Core/FolderAccess.cs`.

## Linking

The app shows a code, opens stardexs.com, and checks every 3 seconds until you
approve the code, for up to 10 minutes. It then stores that account's token,
encrypted with Windows DPAPI, so only your Windows account on this PC can read
it.

The app sends only the PC's name, which is shown on your account. You can
unlink the PC from either side: the website's account page, or the app's menu.

## Checking a download

Planned for the first release:

- Each release on stardexs.com will list its SHA-256 hash and be signed by
  AJ Solutions, LLC. Windows will show that name as the publisher.
- Each release will be built by GitHub Actions from a tagged commit, with a
  published build attestation. That lets anyone confirm the download came from
  that exact source.

To check the code yourself, build the tagged commit with the .NET 10 SDK:

```
dotnet publish StarDexs.Sync -c Release
```

## Layout

| Folder | What's in it |
|---|---|
| `StarDexs.Sync.Core` | Reading logs, finding the game, safe writes, the stardexs.com calls. No user interface. |
| `StarDexs.Sync` | The tray icon, menu, and dialogs (plain Win32, no UI framework). |
| `StarDexs.Sync.Tests` | Tests. They use temporary folders and a fake site, never a real game install. |
