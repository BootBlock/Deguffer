# What Deguffer cleans, and what it costs you

Reference documentation, not a plan: it describes what ships today and is updated whenever a
provider is added, changed, or retired. (The status-banner convention applies to `docs/todo/`,
which tracks work with a beginning and an end; this file has neither.)

This is the plain-language companion to the tier model in
[`docs/todo/_spec.md`](todo/_spec.md) §3. For every location Deguffer knows about it answers the
four questions worth asking before deleting anything:

1. **What is this?** — what actually put the files there.
2. **What is safe to remove**, and what sits next to it that is not.
3. **What it costs you** on the next use.
4. **Why it is in the tier it is in.**

## How to read the tiers

| Tier | Meaning | Deguffer's behaviour |
| --- | --- | --- |
| **1 — Regenerable cache** | The tool re-creates it automatically on demand. You lose time, never data. | Offered and pre-selected. |
| **2 — Regenerable, with cost** | Re-created only by a large re-download, a long rebuild, or an explicit command you must run yourself. | Offered, **never pre-selected**, and needs an extra acknowledgement. |
| **3 — User data in a cache costume** | Logs, histories, saved sessions. Deleting loses it permanently. | Needs a typed confirmation. |
| **4 — Do not touch** | Config, credentials, live state, or anything Deguffer cannot positively identify. | Excluded entirely — not even shown as an option. |

**Tier 4 is the default, not the exception.** Every provider names the children it recognises;
anything it does not recognise stays in Tier 4 and is left alone. If Deguffer finds something
unexpected next to a cache it tells you it is leaving it there rather than guessing.

---

## The rule that shapes all of this

Two rules from the spec explain nearly every design decision below:

- **§5.1 — prefer the tool's own eviction command.** Where a package manager can clear its own
  cache, Deguffer calls that command instead of deleting paths. The tool knows about locations
  Deguffer does not; `dotnet nuget locals all --clear` clears four separate directories, two of
  which are not under `.nuget` at all.
- **§5.2 — never delete a tool's root directory.** Configuration lives next to cache, routinely.
  `.gradle` holds disposable `caches` *and* `gradle.properties`, which may contain signing keys.
  Deguffer targets known-disposable children, never the folder that contains them.

---

## pip package cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\pip\Cache`, unless moved |
| **Method** | `pip cache purge` (the tool's own command) |
| **Typical size** | Tens of MB to several GB, depending on how much you install |

### What it is

pip is Python's package installer. When it downloads a package it keeps a copy so that installing
the same version again — in another virtual environment, or after a reinstall — does not re-fetch it
from the network. The cache holds two distinct things:

- **`http` / `http-v2`** — the downloaded archives exactly as PyPI served them.
- **`wheels`** — wheels pip **built locally** from a source distribution. When a package ships only
  as source, pip compiles it once and keeps the result here.

### What Deguffer does

It asks `pip cache dir` where the cache is rather than assuming, because `PIP_CACHE_DIR`,
`--cache-dir` and the `cache-dir` key in `pip.ini` can all move it. It then runs `pip cache purge`.
Deguffer never deletes the path itself.

### What is protected

`%LOCALAPPDATA%\pip` — the folder *containing* the cache — and `pip.ini` inside it. That file holds
index URLs and can carry credentials for a private package index. Reclaiming the cache by removing
its parent folder would take that with it, so Deguffer asserts both survived the run.

### What it costs you

The next `pip install` re-downloads packages. Anything pip had built from source is compiled again,
which for a package with C extensions is minutes rather than seconds.

**Your installed packages and virtual environments are not touched.** Those live in each
environment's `site-packages`, not in the cache. Clearing the cache never uninstalls anything.

### Why Tier 1

Nothing here is unique. Every entry is a copy of something obtainable from PyPI or rebuildable from
a source distribution, with no input from you. The rebuild cost is why the wording above is explicit
about compilation rather than promising a uniformly cheap refill — but the cost is time, not data.

---

## GPU shader caches

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\NVIDIA\DXCache` and `GLCache`, `%LOCALAPPDATA%\AMD\DxCache`, `%LOCALAPPDATA%\Intel\ShaderCache`, `%LOCALAPPDATA%\D3DSCache` |
| **Method** | Delete the recognised cache directories |
| **Typical size** | A few megabytes to several gigabytes. 3.2 GB was measured on one workstation, nearly all of it NVIDIA's `DXCache` |

### What it is

A shader is a small program that runs on the graphics card, and it arrives as source that has to be
compiled for the exact card and driver in front of it. Compiling is slow, so the driver keeps the
result. The next time the same shader is wanted it is loaded rather than rebuilt.

These caches are therefore a pure by-product. The driver keys every entry to its own version and
throws the lot away itself whenever it is updated, which is why a folder that has been growing for
a year can vanish on a Tuesday without anybody noticing.

Windows keeps one of its own beside the vendors': `%LOCALAPPDATA%\D3DSCache` is Direct3D's system
shader cache, holding one opaque container per application that has used it.

### What Deguffer does

It deletes the cache directories it recognises, one step each, so you can keep one vendor's and
clear another's. There is no eviction command to prefer here — no vendor ships one, and deleting
the directory is what every published instruction says to do.

`%LOCALAPPDATA%\D3DSCache` is the one entry removed whole rather than child by child. It has no
configuration to sit beside: everything in it belongs to Direct3D, arriving as opaque per-application
containers whose names could not be checked against anything. Its parent is your profile's local
application data, which Deguffer never enumerates and never touches.

### What is protected

Each vendor's folder itself, and everything in it Deguffer does not recognise.

**`%LOCALAPPDATA%\NVIDIA\accounts` is the one to know about.** It sits directly beside the two
NVIDIA caches and holds account and sign-in state, not shader blobs — and it is a file rather than a
folder, so the rule that classifies folders never sees it at all. Deguffer names it explicitly and
asserts it survived the run, the same treatment `gradle.properties` gets.

Any other **folder** you find in there gets the same treatment: unrecognised means untouched, and
Deguffer says so and asserts it survived. `%LOCALAPPDATA%\Intel` in particular is a shared Intel
folder holding several unrelated products; Deguffer takes `ShaderCache` from it and tells you what
it is leaving behind. A **file** sitting loose in one of these folders is never a candidate either —
nothing Deguffer deletes here is a file — but only `accounts` is named, so only `accounts` gets the
explicit survival check.

Deguffer also refuses to delete through a link. If you have redirected `%LOCALAPPDATA%\D3DSCache` to
another drive with a junction, it removes nothing and tells you why: what the link points at is a
folder it never looked inside.

### What it costs you

A few seconds of stutter. The first time a game or 3D application draws a scene after the cache has
gone, the driver compiles those shaders again and then behaves exactly as before.

### Why Tier 1

Nothing here originated with you, and nothing has to be fetched to replace it. The content is
derived from shaders that are still on your disk, by a compiler that is still installed, and the
driver does the work without being asked. It is the clearest Tier 1 case Deguffer has: the cost is
measured in seconds and there is no path by which anything is lost.

---

## Chromium application caches

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | Any Chromium user-data folder one level under `%APPDATA%` or `%LOCALAPPDATA%` |
| **Method** | Delete the six cache directories Chromium writes, per profile |
| **Typical size** | Tens of MB per application. 0.8 GB across ten applications was measured on one workstation, and a single heavily used chat client is reported at 2 to 5 GB |

### What it is

A great many desktop applications are a web application with the Chromium browser engine wrapped
around it — chat clients, editors, note-takers, package-manager front ends. Each one runs a full
browser inside itself, and each one therefore keeps a full browser's caches: downloaded web content,
compiled JavaScript, and compiled graphics pipelines.

Because the engine is the same in all of them, the cache directories have the same six names in all
of them, sitting in whatever data folder the vendor chose. That is what Deguffer recognises. It does
not need to know the application.

| Directory | What it holds |
| --- | --- |
| `Cache\Cache_Data` | Web content saved so the same thing is not fetched twice |
| `Code Cache` | JavaScript and WebAssembly compiled ahead of time |
| `GPUCache` | Compiled graphics pipelines |
| `DawnGraphiteCache`, `DawnWebGPUCache` | Compiled WebGPU pipelines |
| `Service Worker\CacheStorage` | Responses a service worker stored for offline use |

### What Deguffer does

**It identifies the folder before it looks inside it.** Any directory on your disk may happen to be
called `GPUCache`, so a cache name is never on its own a reason to go in. Deguffer looks for
`Local State`, the file Chromium writes into the user-data folder it owns, and only a folder holding
that file is examined at all.

Within such a folder it removes exactly the six directories above and nothing else, one step each,
so you can clear one application and keep another. Where an application keeps several profiles —
`Default`, `Profile 1` and so on — each profile's caches are their own steps too, so you can clear a
dormant profile and leave the one you use signed in and warm.

`Cache` and `Service Worker` are **not** removed, only the one directory inside each. `Service
Worker` keeps its registrations and scripts next to the responses they cached, and `Cache` is left
standing for the same reason any unrecognised folder is: Deguffer takes the directory it recognises,
never the one holding it. The plan says so, so you are not left wondering why those two folders are
still there afterwards.

### What is protected

**Everything else in the folder, and this is the folder where that matters most.** Sitting directly
beside the caches, in the same naming style, are:

| Neighbour | What it really is |
| --- | --- |
| `Local Storage`, `Session Storage` | Application state and drafts |
| `IndexedDB` | Offline application data |
| `Cookies`, `Network\Cookies` | Your sign-in cookies |
| `Login Data` | Saved usernames and passwords |
| `Web Data` | Saved addresses and payment cards |
| `Local State` | Application settings, and the key that decrypts the three above |

Nothing outside the six names is ever a candidate, whatever it is called — a directory named
`SuperCache` stays exactly where it is. Deguffer asserts afterwards that every one of these
survived, the ones that are files rather than folders included — those would otherwise never be
checked at all, because the rule that classifies a folder never sees a file.

Deguffer also refuses to delete through a link. If you have redirected an application's cache to
another drive with a junction, it removes nothing there and tells you why.

### What it costs you

Each application starts more slowly once. It fetches the web content it had cached, recompiles its
scripts, and then behaves exactly as before.

**You stay signed in.** Sign-ins, saved passwords, settings and offline data are all in the
neighbouring directories, not in the six. An application that works offline needs to be online once
to refill what its service worker had stored.

Close the applications first if you can. A running one keeps its cache files open, and anything held
open is left in place rather than removed.

### Why Tier 1

Every one of the six is derived content with an authoritative source elsewhere: web content the
server still has, and compiled output of scripts that are still on your disk. The engine refills all
of it without being asked, and the cost is a slower first launch.

### Not reached: packaged applications

An application installed from the Microsoft Store does not write to `%APPDATA%`. Windows redirects
it under `%LOCALAPPDATA%\Packages`, and reaching a Chromium cache there is a separate piece of work
that is not done yet. If one of your Store applications embeds Chromium, Deguffer does not currently
see its cache.

---

## Playwright browsers

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\ms-playwright`, or wherever `PLAYWRIGHT_BROWSERS_PATH` points |
| **Method** | Delete recognised browser builds (`chromium-1228`, `firefox-1532`, …) |
| **Typical size** | ~1 GB per Playwright version you have used |

### What it is

Playwright is a browser-automation and end-to-end testing framework. It does not drive the browsers
already installed on your machine; it downloads its own pinned builds so that a test run is
reproducible. Each Playwright release pins specific browser revisions, and the folder name records
which: `chromium-1228`, `firefox-1532`, `webkit-2210`. Alongside the browsers sit helper downloads
with the same naming — `ffmpeg-1011` for video capture, `winldd-1007` for dependency checks, and
`chromium_headless_shell-1228` for headless runs.

**This is why the folder grows.** Upgrading Playwright downloads a new revision; it does not
necessarily remove the old one straight away. A project that has moved through several Playwright
versions can be holding several complete Chromium builds.

### What Deguffer does

It resolves the location through `PLAYWRIGHT_BROWSERS_PATH` before falling back to the default. If
that variable is set to `0` — Playwright's sentinel meaning "install browsers inside each project's
`node_modules`" — Deguffer offers nothing, because there is no shared cache to clean and the
per-project copies belong to the projects.

Within the folder, a child is removed only if it is **both** a browser name Playwright is known to
publish **and** followed by a numeric revision. `chromium-1228` qualifies. `chromium`,
`chromium-abc`, `chromium-1228-backup` and anything you created yourself do not — they stay in
Tier 4 and Deguffer tells you it is leaving them alone.

### What is protected

The cache root itself, and **`.links`**. That directory is the subtle one: it looks like more cache,
but it is Playwright's record of which installations still reference which browser versions, and
Playwright reads it to decide when a version has no users left and may be removed. Deleting the
browsers is something Playwright recovers from cleanly. Deleting the registry that tracks them
breaks its own housekeeping.

### What it costs you

**Your Playwright tests stop running until you reinstall.** The next run fails with
`Executable doesn't exist` until somebody runs `playwright install`, which re-downloads roughly a
gigabyte.

Your test code, configuration and reports are untouched.

### Why Tier 2, not Tier 1

This is the distinction the tier model exists to make. A package cache refills itself the moment the
tool next needs it — you notice a slower build and nothing else. These binaries do not. Playwright
resolves its pinned browser at launch and fails outright if it is missing; recovery needs a
deliberate command from you.

So the honest description is not "a slower next test run" but "a broken next test run, followed by a
re-download you have to start". That is a decision to put in front of you rather than tick on your
behalf — hence Tier 2, never pre-selected.

Deguffer does **not** use `playwright uninstall`, despite §5.1's preference for a tool's own
command. Playwright's CLI is normally a per-project binary reached through `npx`, and without
`--all` it evicts only the browsers belonging to the installation in the current directory — the
wrong scope for a machine-wide cleaner, and unreachable when Playwright is a project dependency
rather than a global install.

---

## Recycle Bin

**Tier 3 — user data.** Offered, **never pre-selected**, and released only once you have typed the
words the dialog asks for.

| | |
| --- | --- |
| **Location** | `$Recycle.Bin` at the root of every fixed drive Deguffer can read |
| **Method** | Delete this account's own bin inside each one |
| **Typical size** | Whatever you have deleted and not yet purged. 3.6 GB across two drives was measured on one workstation, with the system drive holding nothing at all |

### What it is

Every drive keeps its own Recycle Bin. Deleting a file on `D:` fills `D:`'s bin, not `C:`'s, and
the bin on each drive holds those files at full size until something empties it.

**This is why the space is easy to miss.** Emptying the Recycle Bin from the desktop clears every
drive at once, but tools that go looking for the folder almost always look on the system drive
alone — where, on a machine whose work lives elsewhere, there is frequently nothing to find.

Inside a drive's `$Recycle.Bin` is one folder per Windows account, named after that account's
security identifier: a string like `S-1-5-21-…` that identifies exactly one person on exactly one
machine. Your deleted files are in yours, another user's are in theirs, and Windows itself keeps
one under `S-1-5-18`.

### What Deguffer does

It takes **your own** bin from each fixed drive, one row per drive, and it does not touch the
`$Recycle.Bin` folder that contains them. Windows re-creates your folder the next time you delete
something to that drive, so the bin keeps working exactly as it did.

Each row carries the date that bin last changed, because that is what the decision turns on. A
drive you last deleted something on eight months ago is a different proposition from one you were
clearing out this morning, and the two are indistinguishable by size.

Drives that are not fixed are left out entirely. A network drive has no Recycle Bin — Windows
deletes across one outright — so a `$RECYCLE.BIN` sitting on a share belongs to the server's users
rather than to you. Removable media can be swapped between the preview and the clean, which would
put a plan you approved for one disk in front of another. A fixed drive that is not ready to be
read, which is unusual but possible, is skipped as well.

**Windows has a command for this, and Deguffer does not use it.** §5.1 says to prefer a tool's own
eviction command, and `SHEmptyRecycleBin` is one: it empties the bin on a drive you name. The reason
it is not used is the preview. Deguffer's plan tells you the exact folder it will remove on each
drive, how large it is and when you last used it, and then checks afterwards that everything it
promised to keep is still there. A command that takes a drive and reports nothing back leaves both
of those with nothing to say — and it would move the decision about *whose* bin gets emptied out of
the code where that rule can be checked. The one thing given up is that Windows is not told what
changed, so a Recycle Bin window you already had open may keep showing the old contents until you
refresh it. That is a stale picture rather than a stale deletion.

### What is protected

The `$Recycle.Bin` folder on each drive, and **every account folder inside it that is not yours**.
Both are asserted to have survived the run.

That protection is the whole of the design here. The folder Deguffer removes and the folders it
must not touch are siblings under one parent, identical in every respect except the identifier they
carry — so a rule that was even slightly too broad would take another person's deleted files with
yours. Deguffer matches your identifier exactly, treats everything else as untouchable, and tells
you what it is leaving behind.

**If Deguffer cannot establish which account it is running as, it offers nothing at all.** With no
identifier to match, every bin on the machine belongs to somebody it cannot name, and guessing is
not available.

It also refuses to delete through a link. If a drive's `$Recycle.Bin` has been redirected
elsewhere, that drive is skipped and the reason is stated.

### What it costs you

**Everything in those bins stops being restorable.** The files are not moved anywhere: they are
removed, and no undo exists at any level. Anything you deleted meaning to think again about is gone
at that point.

Nothing else changes. Deleting a file afterwards still sends it to the Recycle Bin, and restoring
that file still works.

### Why Tier 3

The contents of a Recycle Bin are, by definition, files you deleted and can still get back. That is
recoverable user data, which is §3's Tier 3 exactly — and it is the one place where the "cache
costume" the tier model was built for is not even a disguise. The folder is full of things whose
only remaining purpose is to be restorable.

So it is never pre-selected, and it is the first location in Deguffer that asks you to type before
it will run.

---

## Locations deliberately not offered

Being large is not a reason to clean something. These were investigated and left out, and the
reasons are recorded so the decision can be revisited rather than re-litigated from scratch.

### `%USERPROFILE%\.cache` — mixed, needs per-subfolder rules

Measured at ~3.8 GB on the audited machine, but it is not one cache. It is a shared folder several
unrelated tools write into, and the largest occupants are **downloaded machine-learning model
weights** (`huggingface`, `torch`). Those are expensive to re-fetch and can include models that are
gated, private, or no longer published at all — a Tier 2/3 question, not a Tier 1 one, and different
for each subfolder.

A provider here is viable, but only as a per-subfolder allow-list where each entry is researched on
its own. Treating the folder as a unit is exactly the mistake §5.2 exists to prevent.

### Dart/Flutter pub cache — `clean` uninstalls your global tools

`%LOCALAPPDATA%\Pub\Cache` measured ~451 MB. It ships a `README.md` stating its contents "should
only be modified using the `dart pub` and `flutter pub` commands", which rules out a path-based
provider outright. That leaves `dart pub cache clean` — and it is the uv trap, confirmed.

**`dart pub cache clean` empties the whole `PUB_CACHE`, not just the cached downloads.** Pub's own
cache-layout documentation splits the directory in two:

| Child | What it is |
| --- | --- |
| `hosted/`, `hosted-hashes/`, `git/` | Downloaded package archives — genuinely cache |
| `global_packages/` | Packages installed with `dart pub global activate` |
| `bin/` | Binstubs — the launcher scripts for those packages |
| `log/` | Crash logs from failed pub runs |

`clean` removes all of it. Because `PUB_CACHE\bin` is commonly on `PATH`, clearing the cache stops
globally installed Dart command-line tools from running until each is activated again by hand. This
is a known and *currently unresolved* complaint against pub itself —
[dart-lang/pub#3783](https://github.com/dart-lang/pub/issues/3783), "`dart pub cache clean` probably
shouldn't delete globally activated packages", is open.

That makes it **Tier 2 at best**, not Tier 1: the cost is not a slower next build but a set of
missing commands the user has to notice and restore. It is not offered today because a provider
whose only available method takes working tooling with it is a poor trade for ~450 MB.

Two things would change that, and both need research before any code:

- **`dart pub cache gc`** appears in the CLI's own help — "Prunes unused packages from the system
  cache" — but is absent from the published documentation. If it prunes `hosted/` while leaving
  `global_packages/` and `bin/` intact, it is the §5.1 command this provider actually wants.
- **`dart pub cache repair`** reinstalls rather than deletes, so it is a recovery path rather than a
  reclaim, but it bears on how bad a mistake here would be.

One thing that is *no longer* a risk, and would have been on an older SDK: credentials moved out of
the cache in Dart 2.15, to `%APPDATA%\dart\pub-credentials.json`. A machine on an older SDK still has
them inside it.

### Android SDK — small reclaim, catastrophic failure mode

`%LOCALAPPDATA%\Android` is large (~6.7 GB observed) but almost entirely *installed SDK components*,
not cache. Its only true cache, `.android\cache`, is around 3 MB — and it sits directly beside
`debug.keystore`, `adbkey`/`adbkey.pub`, and `avd`. Losing the keystore changes your debug signing
identity and invalidates every API key registered against its fingerprint; losing `adbkey` revokes
every device's trust; an AVD holds user data that cannot be re-downloaded at any price.

A one-character slip in a name comparison there costs more than the entire reclaim is worth.

### `%LOCALAPPDATA%\Temp` — needs an age filter and live-process exclusions

Genuinely reclaimable, and genuinely dangerous to do naively. During the founding audit an active
session held 344 MB of live working files in Temp, with dozens of processes holding open handles.
Doing this properly needs an age filter, exclusion of paths belonging to running processes, and
treating "access denied" as normal rather than as an error. See §5.3.

### Docker — freeing space inside the disk image does not free it on disk

`docker system prune` reclaims space *inside* `docker_data.vhdx`, while the host file stays exactly
the same size. Reporting one number would be actively misleading. This needs the two figures
reported separately, and the second cannot be measured from the filesystem — it comes from the
container tool's own accounting. See §5.4.
