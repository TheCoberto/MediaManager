# Media Manager

A self-hosted web app for browsing, playing and organising a folder of photos, videos, music and documents
from any device on your home network. It's an ASP.NET MVC 5 site (.NET Framework 4.7.2) that runs under
IIS on a Windows PC, with a small SQL Server database for favorites, the Recycle Bin and similar.

One build of the app can run as several IIS sites, each with its own media folder, password, database and
set of features (for example a family media site and a separate private one).

## Contents

- [Features](#features)
- [How it fits together](#how-it-fits-together)
- [Requirements](#requirements)
- [Installing](#installing)
- [Passwords and security](#passwords-and-security)
- [Site.config settings](#siteconfig-settings)
- [Updating a site](#updating-a-site)
- [Running more than one site](#running-more-than-one-site)
- [Debugging in Visual Studio](#debugging-in-visual-studio)
- [Scripts](#scripts)
- [Troubleshooting](#troubleshooting)

## Features

- **Gallery**: one page per top-level folder, with an infinite-scroll grid of images, videos, audio and
  documents. It shows the folder path with links to subfolders, and has an "Include subfolders" switch.
- **Viewer**: full-size images, streaming video and audio with seeking, and documents in the browser.
- **Add Media**: upload files of any size (they're sent in pieces) into any folder.
- **Favorites**: star files and see them all on one page.
- **Flagged**: mark files for deletion or as corrupted, review them in one place, and repair corrupted
  videos with ffmpeg.
- **Recycle Bin**: deleted files go to `<media folder>\.recycle` and can be restored or deleted for good.
- **Bulk download**: a whole folder, the selected files, or all favorites as one ZIP.
- **Guest login** (optional): a second password that can only see and upload images in one folder.
- **Downloaders** (optional, off by default): save audio or video from a link using yt-dlp. Turn on with
  `Feature:Downloaders` in Site.config.
- **Storage Random** (optional, off by default): shows a random picture, video or audio file from the
  whole media folder, or from one folder.

## How it fits together

```
IIS site "Media Manager"  (e.g. http://pc-name:8080/)
├── /        -> C:\inetpub\MediaManager   the app (published build), plus this site's
│                                           Site.config and ConnectionStrings.config
└── /media   -> D:\Media                  your files: an IIS virtual directory
```

- **The media folder is the `/media` virtual directory.** That's the only place the app looks for files,
  so each site's media folder is set in IIS, not in a config file. Requests can never reach outside it.
- **`Site.config`** (per site, next to `Web.config`) holds the site's name, password hashes, upload limits
  and which optional pages are on. See [Site.config settings](#siteconfig-settings).
- **`ConnectionStrings.config`** (per site) points at the site's SQL Server database.
- Neither file is in git or part of a publish, so publishing a new build never overwrites them.
  Templates: `Site.config.example`, `ConnectionStrings.config.example`.

## Requirements

- **Windows 10/11 or Windows Server** with IIS. Turn these on in *Turn Windows features on or off*:
  - Internet Information Services → Web Management Tools → **IIS Management Console**
  - Internet Information Services → World Wide Web Services → Application Development Features →
    **ASP.NET 4.8** (this also selects .NET Extensibility, ISAPI Extensions and ISAPI Filters)
  - Internet Information Services → World Wide Web Services → Common HTTP Features → **Static Content**
- **SQL Server** (Express or Developer is fine) on the same PC or the network.
- **Visual Studio 2019 or later** (or Build Tools) with the *ASP.NET and web development* workload, to build.
- **ffmpeg.exe and yt-dlp.exe** in one folder, for repairing videos and the Downloaders. Download them from
  [ffmpeg.org](https://ffmpeg.org/download.html) (a Windows build) and
  [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases), and put them in `C:\Tools\`, or anywhere else and set
  `ToolsFolder` (see [Site.config settings](#siteconfig-settings)).

## Installing

All commands are run from the repo folder in PowerShell. Steps marked *(admin)* need an elevated
PowerShell ("Run as administrator"). If scripts are blocked, prefix them with
`powershell -ExecutionPolicy Bypass -File`.

### 1. Create the database

```powershell
.\deploy\Setup-Database.ps1 -Database MediaManager                                   # Windows login
.\deploy\Setup-Database.ps1 -Database MediaManager -Server .\SQLEXPRESS -Credential (Get-Credential)   # SQL login
```

This creates the database if needed and applies `SQL\Schema.sql`. It's safe to run again at any time and
never deletes data.

### 2. Make the site's config folder

Make a folder for this site's two private files, e.g. `C:\MediaManagerConfig\MediaManager\`, and copy the
templates into it:

```powershell
$cfg = 'C:\MediaManagerConfig\MediaManager'
New-Item -ItemType Directory -Force $cfg | Out-Null
Copy-Item .\Site.config.example "$cfg\Site.config"
Copy-Item .\ConnectionStrings.config.example "$cfg\ConnectionStrings.config"
```

- Edit **`ConnectionStrings.config`**: server, database name, and either a SQL login
  (`User ID=...;Password=...`) or `Integrated Security=True`. With Integrated Security, the site connects
  as its app pool, so after step 5, give it access in SQL Server, e.g. for an app pool named `Media Manager`:

  ```sql
  CREATE LOGIN [IIS APPPOOL\Media Manager] FROM WINDOWS;
  USE MediaManager;
  CREATE USER [IIS APPPOOL\Media Manager] FOR LOGIN [IIS APPPOOL\Media Manager];
  ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\Media Manager];
  ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\Media Manager];
  GRANT EXECUTE TO [IIS APPPOOL\Media Manager];
  ```

- Edit **`Site.config`**: set `SiteName` and turn optional pages on or off.

### 3. Set the password

```powershell
.\deploy\New-PasswordHash.ps1 -SiteConfig C:\MediaManagerConfig\MediaManager\Site.config
```

It asks for the password twice and writes its hash into `Site.config`. **A site with no password can't be
logged into**: there's no built-in default. Optionally add a guest password the same way with
`-Key GuestPasswordHash`.

### 4. Publish the app to a folder

```powershell
.\deploy\Publish-All.ps1 -To "$env:LOCALAPPDATA\MediaManagerStaging\app"
```

### 5. Create the IIS site *(admin)*

```powershell
.\deploy\Setup-IIS.ps1 -SiteName 'Media Manager' -Port 8080 -MediaRoot 'D:\Media' `
    -ConfigDir 'C:\MediaManagerConfig\MediaManager' -GrantMediaAccess
```

This copies the app to `C:\inetpub\MediaManager` (change it with `-AppDir`), creates the site and an app
pool, points `/media` at `D:\Media`, and with `-GrantMediaAccess` lets the site change files there (needed
for uploads, moves, renames and the Recycle Bin). On a very large folder, the permission change can take a
few minutes. IIS's configuration is backed up first. Undo with `.\deploy\Rollback-IIS.ps1`.

For an **existing** IIS site, leave out `-Port`. The site is repointed at the new app folder, keeping its
bindings and app pool. All options are described at the top of `deploy\Setup-IIS.ps1`.

### 6. Let other devices in

Allow the site's port through Windows Firewall *(admin)*:

```powershell
netsh advfirewall firewall add rule name="Media Manager (TCP 8080)" dir=in action=allow protocol=TCP localport=8080
```

Then browse to `http://<pc-name>:8080/` from a phone or laptop on the same network. To use names like
`http://media.local` instead, run `deploy\Setup-LocalNames.ps1` *(admin)*. By default it gives the
"Media Manager" site the name `media.local`; pass `-Bindings` for other sites or names:

```powershell
.\deploy\Setup-LocalNames.ps1 -Bindings @{ 'Media Manager' = 'media.local'; 'Private Media' = 'private.local' }
```

## Passwords and security

**Who can log in**
- **Owner** (`LoginPasswordHash`): everything.
- **Guest** (`GuestPasswordHash`, optional): only the Uploads pages. Guests can see and upload images in
  `ImageUploadFolder` (default `Guest Uploads`) and can't open any other file.

**How passwords are stored.** Site.config holds a PBKDF2-SHA256 hash (210,000 rounds, random salt) made by
`deploy\New-PasswordHash.ps1`, never the password itself. Older sites may still have a plain SHA-256 hex
hash. Those still work so nobody is locked out, but they're far quicker to crack: replace them by running
`New-PasswordHash.ps1 -SiteConfig <path> -Restart`.

**Changing or resetting a password.** Run `New-PasswordHash.ps1 -SiteConfig <that site's Site.config> -Restart`.
You need write access to the file, not the old password. `-Restart` restarts the site so the change takes
effect immediately, which logs everyone out.

**Wrong passwords.** Each device gets 5 tries. After that, it has to wait before its next try is even
checked: 30 seconds, then 1, 2, 4 and 8 minutes, up to 15 minutes. A correct password, or an hour with no
wrong passwords, resets the count. Restarting the site resets it too.

**Media files need a login.** IIS serves the files under `/media` itself (that's what makes video seeking
work), so the app checks a separate, encrypted "media pass" cookie for those requests
(`Filters\MediaAuthModule.cs`). The cookie is issued at login and runs out with the session (20 minutes
idle). Without a valid pass, a file request gets *401*, or a redirect to the login page if it was opened
directly in a tab. Guests get *403* for anything outside their folder. The `.recycle` and `.uploads`
folders are never served at all.

**Things to know**
- The site uses plain **http**, so the password crosses your Wi-Fi unencrypted. That's fine on a trusted
  home network. **Don't expose the site to the internet** (port forwarding) without adding HTTPS
  (an IIS https binding with a certificate).
- Logins last 20 minutes idle (the ASP.NET session default) and end when the site restarts.
- If two sites share a host name and differ only by port (e.g. `localhost:80` and `localhost:81`), the
  browser shares their session cookie, so logging into one logs you out of the other. Separate host
  names avoid this.

## Site.config settings

Edits take effect when the site restarts. That happens on publish or app pool recycle, or you can trigger
it by re-saving `Web.config` in the same folder.

| Key | Default | What it does |
|---|---|---|
| `SiteName` | `Media Manager` | Name in the title bar and menus. |
| `LoginPasswordHash` | *(none: required)* | Owner password hash. Use `deploy\New-PasswordHash.ps1`. |
| `GuestPasswordHash` | *(none = no guest login)* | Guest password hash. |
| `ImageUploadFolder` | `Guest Uploads` | Folder (under the media folder) for guest uploads; the only files guests can open. |
| `MaxUploadMB` | `20480` (20 GB) | Largest file the owner can upload. |
| `GuestMaxUploadMB` | `50` | Largest image a guest can upload. |
| `ToolsFolder` | `C:\Tools\` (from `Web.config`) | Folder containing `ffmpeg.exe` and `yt-dlp.exe`. Set it here per site, or change the default in `Web.config` for all sites. |
| `Feature:Images` | `true` | The Uploads / Guest Uploads pages (and with them the guest login's pages). |
| `Feature:Downloaders` | `false` | Audio and Video Downloader (needs `yt-dlp.exe` in `ToolsFolder`). |
| `Feature:StorageRandom` | `false` | Storage Random page. |

## Updating a site

Publish a new build over the site's app folder. You won't need admin rights, because `Setup-IIS.ps1` gave
you write access to it:

```powershell
.\deploy\Publish-All.ps1                        # every folder publish profile you have
.\deploy\Publish-All.ps1 -Profile MediaManager  # just one
```

`Publish-All.ps1` uses the *Folder* publish profiles in `Properties\PublishProfiles\` (not in git). To make
one, open Visual Studio, choose *Publish*, then *Folder*, and give it the site's app folder
(e.g. `C:\inetpub\MediaManager`). Or skip profiles and run `Setup-IIS.ps1` again after `Publish-All.ps1 -To`.

If `SQL\Schema.sql` changed, also re-run `Setup-Database.ps1` for each site's database.

## Running more than one site

Repeat steps 1–6 with a different database, config folder, site name, port and media folder:

```powershell
.\deploy\Setup-Database.ps1 -Database PrivateMedia
.\deploy\Setup-IIS.ps1 -SiteName 'Private Media' -Port 8081 -MediaRoot 'D:\Private' `
    -ConfigDir 'C:\MediaManagerConfig\PrivateMedia' -GrantMediaAccess
```

Each site gets its own app folder, app pool, password and features. Since every site runs the same build,
publishing updates them all.

## Debugging in Visual Studio

Open `MediaManager.sln` and press F5 (IIS Express). The repo folder needs its own `Site.config` (with a
password hash) and `ConnectionStrings.config`. Both are git-ignored, so copy them from the examples as in
step 2. IIS Express has no `/media` by default, so run this once (after debugging once, so Visual Studio
creates its config):

```powershell
.\deploy\Setup-Debug.ps1 -MediaRoot 'D:\Media'
```

IIS Express may answer partial ("Range") requests with the whole file, so video seeking might not work
while debugging. It works under real IIS.

## Scripts

| Script | Admin | What it does |
|---|---|---|
| `deploy\Setup-Database.ps1` | | Creates a site database and applies `SQL\Schema.sql` (safe to re-run). |
| `deploy\New-PasswordHash.ps1` | | Makes a password hash, and optionally writes it into a Site.config and restarts the site. |
| `deploy\Publish-All.ps1` | | Builds Release and publishes to your folder profiles, or to any folder with `-To`. |
| `deploy\Setup-IIS.ps1` | ✔ | Creates or updates one IIS site: app folder, app pool, `/media`, config files, permissions. |
| `deploy\Rollback-IIS.ps1` | ✔ | Restores the IIS configuration from before the first `Setup-IIS.ps1` run. |
| `deploy\Setup-Debug.ps1` | | Adds `/media` to Visual Studio's IIS Express site. |
| `deploy\Setup-LocalNames.ps1` | ✔ | Makes `http://<name>.local` work on the home network (a tiny mDNS responder plus IIS host-name bindings). Names via `-Bindings`. |

## Troubleshooting

- **"This site has no password set yet."** `Site.config` has no `LoginPasswordHash`: see
  [step 3](#3-set-the-password), then restart the site.
- **Right password, but "Invalid password".** The hash in Site.config may have been edited by hand, or
  copied from `Site.config.example` with `REPLACE_ME` still in it. Set it again with
  `New-PasswordHash.ps1 ... -Restart`.
- **"Too many wrong passwords."** Wait for the time shown, or restart the site (recycle its app pool) to
  clear it straight away.
- **Pictures and videos don't load, but pages do.** Your login has probably expired: reload the page and
  log in again.
- **Gallery is empty.** Check the site's `/media` virtual directory points at the right folder, and that
  the app pool can read it (`Setup-IIS.ps1 ... -GrantMediaAccess`).
- **Uploads, moves or the Recycle Bin fail.** The app pool needs *modify* rights on the media folder.
- **HTTP Error 500.19.** IIS is missing ASP.NET 4.8 (see [Requirements](#requirements)), or the site's
  `ConnectionStrings.config` / `Site.config` is missing or isn't valid XML.
- **Database errors.** Check `ConnectionStrings.config`. With Integrated Security, check the app pool has
  a SQL login (step 2).
- **Other devices can't connect.** Check the firewall rule for the site's port (step 6) and that the
  devices are on the same network.

## License

MIT. See [LICENSE](LICENSE).
