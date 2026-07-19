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

### Dart/Flutter pub cache — the tool forbids manual edits

`%LOCALAPPDATA%\Pub\Cache` measured ~451 MB. It ships a `README.md` stating its contents "should
only be modified using the `dart pub` and `flutter pub` commands", which rules out a path-based
provider outright.

`dart pub cache clean` exists, but its documentation does not say whether it also removes globally
activated packages, which live in the same folder under `bin` and `global_packages`. That is the uv
trap — a cache directory that is really a state directory — and confirming it means running a
destructive command. Until that behaviour is established, the tier cannot be assigned honestly.

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
