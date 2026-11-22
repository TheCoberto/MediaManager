---
name: deploy
description: Pull and publish this app to its IIS sites (MediaManager, SecretManager), then verify each site is live on the new build. Use when asked to deploy, publish or update the server.
---

# Deploy

Both IIS sites run the same build of this repo. Each has its own folder publish profile:

| Profile | Site folder | Check URL |
|---|---|---|
| `MediaManager` | `C:\inetpub\MediaManager` | `http://localhost/` |
| `SecretManager` | `C:\inetpub\SecretManager` | `http://secrets.local/` |

`Site.config` and `ConnectionStrings.config` in each site folder are not part of the publish, so they are never overwritten.

## Steps

1. **Pull.** On `master`, `git pull --ff-only`. Report which PRs came in (`git log --oneline` of the new commits). Done when the working tree is clean and matches `origin/master`.
2. **Publish** each site the user named, from the repo root: `.\deploy\Publish-All.ps1 -Profile <Profile>`. "Deploy everything" means both profiles. No admin rights needed. Done when the script prints `Published: <Profile>`.
3. **Verify** each published site:
   - `Get-FileHash` of `bin\MediaManager.dll` in the site folder equals the build's `obj\Release\Package\PackageTmp\bin\MediaManager.dll`.
   - `Invoke-WebRequest <Check URL> -UseBasicParsing` returns 200 at `/Login?returnUrl=/` (the first request after a publish can take a minute while the app starts).

   Done when both checks pass for every site published.
4. **Report** what changed, which sites were published, and the check results. If the deploy changed any `Scripts/*.js`, tell the user to hard-refresh (Ctrl+F5) on each device until script links carry a version (see `IDEAS.md`); phones otherwise keep running the old script.

## Gotchas

- `Import-Module WebAdministration` and the IIS logs need an elevated shell, which this session doesn't have. Read site bindings from `C:\Windows\System32\inetsrv\config\applicationHost.config` instead.
- Commit or push only when the user asks; this repo merges through PRs.
