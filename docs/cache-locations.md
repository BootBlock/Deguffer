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
| **3 — User data in a cache costume** | Logs, histories, saved sessions. Deleting loses it permanently. | Offered, **never pre-selected**, and the confirmation says plainly that the loss is permanent. How hard that confirmation is to give is yours to set: up to typing the item's name out, down to none at all. |
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

## Poetry package cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\pypoetry\Cache`, unless moved |
| **Method** | `poetry cache clear <name> --all` for the repository caches; a path deletion for `artifacts` |
| **Typical size** | Hundreds of MB to several GB, depending on how many projects you build |

### What it is

Poetry is a dependency manager and packaging tool for Python. One folder holds three quite
different things:

- **`artifacts`** — the package archives Poetry downloaded, and the wheels it built itself from
  packages that ship only as source.
- **`cache\repositories\<name>`** — the package metadata Poetry resolves dependency versions
  against, one directory per repository it has talked to.
- **`virtualenvs`** — **every virtual environment Poetry has created, for every project on the
  machine.** These are not a cache. Each one is a full dependency install.

### What Deguffer does

It asks `poetry config cache-dir` where the folder is and `poetry config virtualenvs.path` where the
environments are, because both are settings and neither constrains the other. It then clears the two
caches by different routes, because Poetry only ships a command for one of them:

- **The repository caches** go through `poetry cache clear <name> --all`, once per cache
  `poetry cache list` names. The per-name form is used rather than the documented bare
  `poetry cache clear --all` because the cache argument was mandatory until recently, so the short
  form fails outright on an older Poetry.
- **`artifacts`** is removed by path. No Poetry command reaches it — `cache clear` builds a file
  cache over the repository directory and flushes that, and nothing else touches the downloads. It
  is a long-standing gap in Poetry itself, and the reason its users are told to delete the folder by
  hand.

The `Cache` folder itself is never removed, and neither is `virtualenvs`.

### What is protected

`virtualenvs`, first and most importantly — asserted to have survived every run, not merely left out
of the plan. Also the `Cache` folder that contains it, `%LOCALAPPDATA%\pypoetry` above that, and
`%APPDATA%\pypoetry`, which holds `config.toml` and the `auth.toml` that can carry credentials for a
private package repository.

The environments are checked twice over: by the name `virtualenvs`, which is what stops the folder
being offered anywhere in the app, and separately by their **resolved path**. Both settings can be
pointed anywhere, so a `virtualenvs.path` inside a folder Deguffer would otherwise clear takes that
folder off the plan, with the reason shown.

Anything else inside the cache folder that Deguffer does not recognise is left alone and named.

### What it costs you

The next `poetry install` re-downloads package archives, re-fetches the metadata Poetry resolves
against, and rebuilds any wheel it had built from a source distribution.

**Your virtual environments, and everything installed into them, are untouched.** That is the whole
reason this provider works the way it does: the default location for those environments is a child
of the folder being cleaned, so a rule that reclaimed the cache by removing the folder would delete
every project's environment on the machine.

### Why Tier 1

Everything removed is a copy of something obtainable from a package repository, or rebuildable from
a source distribution, with no input from you. The rebuild cost is real for a package with C
extensions, but it is time rather than data — and the one thing in the folder that *is* data is
never removed.

---

## Cargo crate cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%USERPROFILE%\.cargo`, or wherever `CARGO_HOME` points |
| **Method** | Delete `registry\cache`, `registry\src` and `git\checkouts` |
| **Typical size** | Reported to reach 50 GB on a working Rust machine |

### What it is

Cargo is Rust's package manager, and everything it downloads for every project on the machine lands
in one shared home. Five directories inside it matter, and they come in pairs of *original* and
*derived*:

- **`registry\cache`** — the `.crate` archives exactly as crates.io served them.
- **`registry\src`** — those archives unpacked, which is what the compiler actually reads.
- **`registry\index`** — the metadata for every published crate, used to resolve versions.
- **`git\db`** — a bare clone of each dependency you pulled straight from a git repository.
- **`git\checkouts`** — a working copy of one revision, checked out from that clone.

`registry\src` is unpacked from `registry\cache`, and `git\checkouts` is checked out from `git\db`.
That relationship is the whole design of what Deguffer removes.

### What Deguffer does

It removes the two derived directories and the archives: `registry\cache`, `registry\src` and
`git\checkouts`. It resolves the home through `CARGO_HOME` before falling back to the default, and
if that variable holds a relative path it offers nothing, because Cargo would resolve it against a
working directory Deguffer is not. If the home turns out to be a link to somewhere else — a common
way to move Cargo off the system drive — Deguffer says so and leaves it alone, because nothing on
the far side of a link has been classified.

Cargo has no eviction command to call. Its garbage collector is still unstable and reachable only
through a nightly toolchain, and `cargo clean` is a different thing entirely — it empties one
project's `target` directory and never touches the shared home.

### What is protected

**`credentials.toml`**, which holds the registry tokens `cargo login` wrote, and **`config.toml`**,
which is your Cargo configuration and may name private registries. Both sit in the same folder as
the caches. Deguffer asserts both survived the run, along with the home itself.

**`bin`** is left alone. It holds every binary you installed with `cargo install` or rustup, it is
normally on your `PATH`, and nothing re-creates what is in it.

Two more are left alone deliberately, and both are the *originals* the removed directories were
derived from:

- **`git\db`** is the only copy of a git dependency's history on your machine. The checkout beside
  it is rebuilt from it with no network at all, and it can only be fetched again while the remote
  repository still exists, is still reachable, and still carries the revision your lock file names.
- **`registry\index`** can be fetched again, but it is what lets a build resolve versions offline,
  and it is small next to the archives. The reclaim is not worth the cost.

Anything else in the home, at any of the three levels Deguffer looks at, stays in Tier 4 and is
reported as left alone.

### What it costs you

The next `cargo build` downloads the crate archives it needs again and unpacks them, so it spends
longer fetching before it compiles. Nothing has to be re-configured and no command has to be run.

Your git dependencies do not need re-cloning, because their clones stay.

### Why Tier 1

Everything removed comes back on its own the next time Cargo needs it. Cargo's own documentation
draws the same line when it says which parts of the home are worth carrying between CI runs:
`registry\index`, `registry\cache` and `git\db` are, and `registry\src` and `git\checkouts` are not,
precisely because those two are re-derived locally.

The uncomfortable half of the tier is the git clones, and the answer was to *not remove them* rather
than to argue the tier. "Regenerable" is a claim about somebody else's server still being there,
and for a git remote that claim is often false. Splitting the two halves is possible here because
the split runs along a directory boundary, so it was split.

---

## Go build and module caches

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | Whatever `go env GOCACHE` and `go env GOMODCACHE` report |
| **Method** | `go clean -cache` and `go clean -modcache` (the tool's own commands) |
| **Typical size** | Several GB each on a machine building regularly |

### What it is

Go keeps two separate caches, and they hold different kinds of thing:

- **The build cache** holds compiled packages and test results, keyed by content. It is what makes a
  second `go build` fast.
- **The module cache** holds the source of every module version any project on the machine has
  depended on, extracted and read-only.

### What Deguffer does

It asks Go where both are, and clears each with the command Go ships for it. Deguffer deletes no
path here at all.

That matters more than usual for the module cache. Go marks every extracted file read-only so that a
build cannot modify a dependency in place, and a cleaner that deleted the path would be refused file
by file — reclaiming nothing while reporting that it had finished. `go clean -modcache` is what
knows how to take the cache apart.

Each location is a separate row you can tick on its own, so you can clear the build cache and keep
the downloaded modules, or the other way round.

### What is protected

The Go workspace, `GOPATH\bin` and `GOPATH\src`. The module cache is `pkg\mod` *inside* the
workspace by default, so what the command empties has the binaries you installed with `go install`
and your own source as its siblings. Deguffer asserts all three survived.

### What it costs you

The next `go build` downloads the modules it needs again and recompiles every package from source,
so it takes noticeably longer once and then behaves exactly as before. Your own code and the Go
toolchain are untouched.

**One caveat is worth knowing.** A module from a private host — anything matching `GOPRIVATE`, or
behind a proxy that happens to be down — comes back only while that host is available. Nothing on
disk distinguishes those entries from public ones, so Deguffer states the caveat rather than
pretending to sort them.

### Why Tier 1

Both caches refill themselves the next time Go needs them, with no command from you and nothing to
re-configure. The private-module caveat is a reason to say so plainly, not a reason to treat a
regular Go build as a risk.

---

## pnpm store

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | Wherever `pnpm store path` reports, ordinarily under `%LOCALAPPDATA%\pnpm\store` |
| **Method** | `pnpm store prune` (the tool's own command) |
| **Typical size** | Several GB on a machine with a few Node projects on it |

### What it is

pnpm installs Node packages differently from npm, and the difference is the whole reason this entry
needs its own explanation.

npm gives every project its own full copy of every dependency. pnpm keeps **one** copy of each
package version in a single store, and then **hard-links** that copy into each project's
`node_modules`. A hard link is not a shortcut or a copy: it is a second name for the same blocks on
disk. Ten projects using the same version of the same library share one set of blocks between them,
which is why pnpm uses far less disk than npm for the same projects.

The store grows because pnpm does not remove anything on its own. Upgrade a dependency and the old
version stays in the store, in case something still wants it.

### What Deguffer does

It asks pnpm where the store is, because a `store-dir` setting moves it, and pnpm keeps a separate
store per drive. It then runs `pnpm store prune`, which removes exactly the packages that **no
project on your machine still references**. Deguffer deletes no path here at all.

That selectivity is why this is a better eviction than npm's. `npm cache clean` empties the lot;
`pnpm store prune` keeps everything in use and takes only the rest.

Deguffer does **not** pass `--force`. To pnpm, force means "also remove alien files" — anything in
the store the package manager did not put there. Deguffer never deletes what no rule can name, so
that flag is left alone.

### The size shown is smaller than the store, deliberately

**This is the one thing worth understanding about this row.** Because the store's files are
hard-linked into every project using them, adding up the file sizes in the store would count each
package once for the store and again for every project that links it. On a machine with several
projects the total can be several times what pruning could ever free.

So Deguffer counts only the files in the store that **nothing outside the store links** — the ones
whose blocks really would come back. That is the number on the row, and it is smaller, and it is
the true one.

It is still shown as an approximation, because it is a prediction. Link counts change every time a
project installs or removes a dependency, and pnpm decides what to prune from its own records
rather than by counting links.

### What is protected

**The store directory itself**, first of all. `pnpm store prune` works *inside* the store, so the
directory has to still be there when it finishes — and a check that watched only its surroundings
would call a run that removed every package on the machine a success.

Around it: the directory holding the store, pnpm's home directory, and **`global`** inside it — the
packages you installed with `pnpm add --global`, which are not a cache. pnpm's own launcher lives in
the home directory too. Deguffer asserts all four survived the run.

Where pnpm reports something that is not a usable directory, or names a drive root, Deguffer offers
nothing rather than guessing. There is no documented default to fall back on: the store directory
carries a layout version in its name, which moves between pnpm releases.

### What it costs you

A later install that needs one of the removed packages downloads it again. Nothing else changes.

**Your projects are untouched.** Anything a project still links stays in the store, so no
`node_modules` breaks and no install has to be re-run. pnpm's own documentation puts it plainly:
pruning the store is not harmful, it may only slow a future install.

### Why Tier 1

Everything removed is, by pnpm's own accounting, referenced by nothing. It refills itself the next
time a project asks for it, with no command from you and nothing to re-configure.

---

## Maven local repository

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `%USERPROFILE%\.m2\repository`, or the `localRepository` in your `settings.xml` |
| **Method** | Delete the local repository directory |
| **Typical size** | Several GB on a machine with a few Java projects on it |

### What it is

Maven copies every dependency it resolves into one local repository, laid out by group, artifact and
version. It is the folder every Maven build reads from and writes to.

**It is filled from two different places, and that is the whole reason this is Tier 2.** Most of what
is in there was downloaded from Maven Central or another remote. But `mvn install` writes into the
same tree, in the same layout — and what it writes was built on your machine and exists on no remote
at all.

### What Deguffer does

It reads `localRepository` from `%USERPROFILE%\.m2\settings.xml` before falling back to the default,
because that element genuinely moves the repository. `${user.home}` in that value is resolved; any
other property, or a relative path, leaves the value unreadable and Deguffer offers nothing rather
than guessing. A value naming `.m2` itself, anything above it, or one of the things
Deguffer promises to leave alone inside it, is refused outright — that would make the folder holding
your credentials the thing being deleted, and `${user.home}/.m2` is a plausible typo for the correct
`${user.home}/.m2/repository`.

Two other ways of moving it are out of reach, and both fail safe. Maven merges a global
`settings.xml` from its own installation directory, which your file overrides anyway; and
`-Dmaven.repo.local` is chosen per command and is written down nowhere. Where either is in use,
Deguffer measures the directory your user settings name, so the failure is a smaller reclaim rather
than a wrong target.

Maven ships no machine-wide purge. `dependency:purge-local-repository` is a per-project goal that
removes one project's dependencies and immediately resolves them again, which is neither the scope
nor the effect wanted here.

### What is protected

**`settings.xml`**, which holds your server credentials and private repository URLs, and
**`settings-security.xml`**, which holds the master password those are encrypted against. Both sit
in `.m2` beside the repository. Deguffer names the one directory it removes rather than listing the
root that contains them, and asserts both files survived.

`toolchains.xml` and `.m2\wrapper` are protected too. The wrapper folder holds Maven distributions
the wrapper downloaded; they are small, and this provider does not remove them.

### What it costs you

The next Maven build downloads every dependency it needs again, which for a large project is
gigabytes over the network.

**Anything you installed locally with `mvn install` was never on a remote.** A build that depends on
one of those fails to resolve it until you rebuild the project that produced it. If you work on a
multi-module codebase, or on libraries that other local projects consume, that is the cost to weigh.

### Why Tier 2, not Tier 1

Because the two halves cannot be told apart. A downloaded artefact usually carries a
`_remote.repositories` marker naming where it came from, and a locally installed one usually does
not — but that file is a Maven implementation detail rather than a promise, it is missing from older
trees, and a rule that deleted a version directory on the strength of it would be guessing about
exactly the case that cannot be undone.

So the whole is offered at the more cautious tier: never pre-selected, and needing an
acknowledgement. That is the honest form of "some of this is a slower build and some of it is a
broken one".

---

## vcpkg build caches

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\vcpkg\archives`, plus `buildtrees`, `downloads` and `packages` under the vcpkg clone |
| **Method** | Delete the recognised directories |
| **Typical size** | Several GB, and much more on a machine building large libraries |

### What it is

vcpkg builds C and C++ libraries from source. Four directories accumulate as it does:

- **`archives`** — the binary cache. After each port is built, vcpkg keeps the result here so it does
  not have to build it again.
- **`downloads`** — the source archives and the tools it downloaded to build with.
- **`buildtrees`** — intermediate build output, one directory per port.
- **`packages`** — the staging area a built port is assembled in before it is installed.

### What Deguffer does

It removes all four where it can find them, as separate rows you can tick individually.

**It may see one location or four, and it says which.** The binary cache is in your profile, so it is
always findable — Deguffer follows vcpkg's documented search order of `VCPKG_DEFAULT_BINARY_CACHE`,
then `%LOCALAPPDATA%\vcpkg\archives`, then `%APPDATA%\vcpkg\archives`. The other three live inside
the vcpkg clone, which is a git checkout you put wherever you liked, so Deguffer looks for it in
`VCPKG_ROOT`, then in the file `vcpkg integrate install` wrote into your profile, then beside the
`vcpkg` executable on your `PATH`. Whichever route answers, the directory has to carry
`.vcpkg-root` — vcpkg's own marker for its root — before Deguffer looks inside it. That check is
what stops a stray copy of `vcpkg.exe` making your `Downloads` folder a target. If none of the three
answers, the plan says so in as many words rather than quietly reporting a quarter of the subject.

`VCPKG_DOWNLOADS` is honoured where it has moved the downloads directory out of the clone. None of
the three variables can point Deguffer at the clone itself, or at your own vcpkg folder: those say
where a cache is, and they are not a way to ask for the directory holding the tool.

vcpkg ships no cache-eviction command. Its own answer to a cache that has grown is the
`--clean-after-build` family of flags on `vcpkg install`, which cleans as it goes, and its
documentation says outright that `buildtrees`, `downloads` and `packages` under the root are safe to
delete.

### What is protected

**`installed`** — the libraries vcpkg has actually installed, which every project on the machine
links against. It sits in the same folder as the three scratch directories and it is refilled by
exactly the same command, so it looks disposable and is not. `ports`, `triplets`, `versions`,
`scripts` and `vcpkg.exe` are protected beside it, and the clone itself is never a target.

In your profile, `vcpkg.path.txt` and `registries` are protected: one records which clone is
integrated with Visual Studio, and the other holds the registry clones your manifests resolve
against.

### What it costs you

The next `vcpkg install` rebuilds the affected libraries from source instead of unpacking a cached
binary, and downloads their source archives again as it goes. For something the size of Boost or Qt
that is tens of minutes to hours.

Libraries already installed stay installed. Nothing you have already built against stops working.

### Why Tier 2, not Tier 1

Because restoring one of these is a compile rather than a download. Tier 2's definition covers
"re-created, but only by re-downloading gigabytes or re-indexing for minutes", and a from-source
rebuild of a large C++ library is the same cost with the clock running the other way.

Two of the four — `buildtrees` and `packages` — are genuinely scratch and would be Tier 1 on their
own. A plan carries one tier, and the more cautious of the two governs it. You are not denied them:
each directory is its own row, so you can take the scratch and leave the binary cache.

---

## Conda package cache

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | Every writable package cache `conda info` reports, ordinarily `%PROGRAMDATA%\miniconda3\pkgs` or `%USERPROFILE%\.conda\pkgs` |
| **Method** | `conda clean --index-cache --packages --tarballs --tempfiles` (the tool's own command) |
| **Typical size** | Anaconda's own documentation puts it at tens to hundreds of GB on a machine in daily use |

### What it is

Conda downloads each package once, as an archive, and unpacks it into a shared `pkgs` directory.
Creating an environment then **hard-links** those unpacked files into the environment rather than
copying them, so ten environments using the same version of the same library share one copy on disk.

`pkgs` therefore holds three different things: the downloaded archives, the unpacked packages that
environments link to, and a cache of the channel index conda uses to resolve versions.

### What Deguffer does

It asks conda where its caches are, then asks conda **what its own clean command would remove**, and
shows you that figure. It deletes no path itself.

Conda is not on your `PATH` by default, so Deguffer looks for it in three places: your `PATH`, the
`CONDA_EXE` variable conda's own shell integration sets, and the documented install locations for
Anaconda, Miniconda and Miniforge.

Two categories are deliberately left out of the command:

- **`--all` is not used**, because it also removes conda's log files. A log is a record of something
  that already happened, which Deguffer treats as your data rather than as cache.
- **`--force-pkgs-dirs` is never used.** It removes every writable cache whole, and conda's own help
  says outright that it breaks environments whose packages are linked back to the cache.

### The size shown is conda's own figure, not a measurement

Measuring `pkgs` directly would count every package your environments are still using, because they
are all hard-linked out of it. On a machine with a few environments that number is several times
what the clean could free.

Conda already solves this for itself: its clean skips any file with more than one hard link, so what
its dry run reports is exactly what its clean would remove. Deguffer shows that, and adds only its
own measurement of the channel index cache, which conda lists but does not size.

**Where conda will not report, Deguffer offers nothing.** The only other figure available is the one
that counts your environments' packages, and showing it would promise space that cannot be freed.

### What is protected

The conda installation itself, including its base environment; **every environment directory conda
reports**, whose packages hard-link back into the cache being cleaned; each package cache directory
itself; and **`.condarc`**, your conda configuration, which can name private channels with tokens
embedded in the URL. Deguffer asserts they all survived the run.

**One case can raise an alarm over nothing.** `--tempfiles` looks through the whole conda installation,
environments folder included, and deletes conda's leftover temporary files there (names ending in
`.c~` or `.trash`), leaving the folders. If an environments folder inside the installation holds no
environment and nothing but such leftovers, it ends the run holding only empty folders, and Deguffer
reports it as emptied. No environment was in it, so nothing you created is lost.

### What it costs you

The next `conda install` downloads the packages it needs again and re-fetches the channel index,
which for a large environment is gigabytes over the network.

**Your environments keep working.** Conda keeps every package an environment still links, so nothing
you have already created stops functioning.

### Why Tier 2, not Tier 1

Two reasons, and neither is the one you might expect. The command touches no environment at all, so
this is not about environments being expensive to rebuild.

- **The refill is large.** Tier 2 covers what is "re-created, but only by re-downloading gigabytes",
  and conda packages are among the largest a package manager fetches.
- **Conda decides what is unused by counting hard links, and its own documentation warns that this
  does not see an environment linked by symlink instead.** Windows environments use hard links or
  copies unless symlinks were deliberately enabled, so the case is unlikely here — but a rule that
  can be wrong in the destructive direction belongs at the tier that is never ticked for you.

---

## GPU shader caches

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\NVIDIA\DXCache` and `GLCache`, `%USERPROFILE%\AppData\LocalLow\NVIDIA\DXCache`, `%LOCALAPPDATA%\AMD\DxCache`, `%LOCALAPPDATA%\Intel\ShaderCache`, `%LOCALAPPDATA%\D3DSCache` |
| **Method** | Delete the recognised cache directories |
| **Typical size** | A few megabytes to several gigabytes. 3.2 GB was measured on one workstation, nearly all of it NVIDIA's `DXCache`. On another, the two NVIDIA folders held 163 MB and 161 MB |

### What it is

A shader is a small program that runs on the graphics card, and it arrives as source that has to be
compiled for the exact card and driver in front of it. Compiling is slow, so the driver keeps the
result. The next time the same shader is wanted it is loaded rather than rebuilt.

These caches are therefore a pure by-product. The driver keys every entry to its own version and
throws the lot away itself whenever it is updated, which is why a folder that has been growing for
a year can vanish on a Tuesday without anybody noticing.

Windows keeps one of its own beside the vendors': `%LOCALAPPDATA%\D3DSCache` is Direct3D's system
shader cache, holding one opaque container per application that has used it.

**NVIDIA keeps two, in two different parts of your profile.** One is under `%LOCALAPPDATA%\NVIDIA`
and the other under `AppData\LocalLow\NVIDIA`, which is where a program running with reduced
privileges writes because it may not write to the first. They are separate folders rather than one
folder seen twice, and on the machine they were measured on they were much the same size as each
other. A tool that clears only the first leaves about half of it behind.

### What Deguffer does

It deletes the cache directories it recognises, one step each, so you can keep one vendor's and
clear another's. The two NVIDIA folders are separate steps for the same reason, so clearing one and
keeping the other is your choice to make. No graphics vendor ships a command that clears its own
shader cache, and deleting the directory is what every published instruction says to do.

Windows is the one exception, and it is unresolved rather than ruled out. Disk Cleanup carries a
"DirectX Shader Cache" item, but it runs as code rather than as a list of folders, so what it
actually clears is not something the registration reveals. Deguffer deletes `%LOCALAPPDATA%\D3DSCache`
directly because that is what it can name, offer and check afterwards.

Only `DXCache` is recognised in the LocalLow folder, because that is the only cache that has been
observed there. If NVIDIA writes a `GLCache` there on your machine, Deguffer leaves it alone and
says so, rather than assuming a name it has seen elsewhere applies here too.

`%LOCALAPPDATA%\D3DSCache` is the one entry removed whole rather than child by child. It has no
configuration to sit beside: everything in it belongs to Direct3D, arriving as opaque per-application
containers whose names could not be checked against anything. Its parent is your profile's local
application data, which Deguffer never enumerates and never touches.

### What is protected

Each vendor's folder itself, and everything in it Deguffer does not recognise.

**`%LOCALAPPDATA%\NVIDIA\accounts` is the one to know about.** It sits directly beside the two
NVIDIA caches and holds account and sign-in state, not shader blobs — and it is a file rather than a
folder, so the rule that classifies folders never sees it at all. Deguffer names it explicitly and
asserts it survived the run, the same treatment `gradle.properties` gets. The LocalLow `NVIDIA`
folder is protected the same way, by name, whether or not it currently holds one.

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
| **Location** | Any Chromium user-data folder one level under `%APPDATA%` or `%LOCALAPPDATA%`, each Chromium-based browser's own user-data folder, and the Battle.net launcher's built-in browser |
| **Method** | Delete the seven cache directories Chromium writes, per profile |
| **Typical size** | Tens of MB per application. 0.8 GB across ten applications was measured on one workstation, and a single heavily used chat client is reported at 2 to 5 GB. One browser's `Code Cache` alone came to 194 MB on the same workstation |

### What it is

A great many desktop applications are a web application with the Chromium browser engine wrapped
around it — chat clients, editors, note-takers, package-manager front ends. Each one runs a full
browser inside itself, and each one therefore keeps a full browser's caches: downloaded web content,
compiled JavaScript, and compiled graphics pipelines.

Because the engine is the same in all of them, the cache directories have the same seven names in all
of them, sitting in whatever data folder the vendor chose. That is what Deguffer recognises. It does
not need to know the application.

| Directory | What it holds |
| --- | --- |
| `Cache\Cache_Data` | Web content saved so the same thing is not fetched twice |
| `Code Cache` | JavaScript and WebAssembly compiled ahead of time |
| `GPUCache` | Compiled graphics pipelines |
| `DawnGraphiteCache`, `DawnWebGPUCache` | Compiled WebGPU pipelines |
| `DawnCache` | Compiled WebGPU pipelines, under the name older builds of the engine gave that cache. Battle.net's engine still writes it |
| `Service Worker\CacheStorage` | Responses a service worker stored for offline use |

The browsers built on Chromium keep the same seven directories, but further down: under a vendor
folder and a product folder rather than directly in `%APPDATA%` or `%LOCALAPPDATA%`. Deguffer knows
where each of these keeps its folder, including the beta, developer and nightly builds:

| Browser | User-data folder |
| --- | --- |
| Google Chrome | `%LOCALAPPDATA%\Google\Chrome\User Data` (`Chrome Beta`, `Chrome Dev`, `Chrome SxS` and `Chrome for Testing` beside it) |
| Chromium | `%LOCALAPPDATA%\Chromium\User Data` |
| Microsoft Edge | `%LOCALAPPDATA%\Microsoft\Edge\User Data` (`Edge Beta`, `Edge Dev` and `Edge SxS` beside it) |
| Brave | `%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data` (`-Beta` and `-Nightly` beside it) |
| Vivaldi | `%LOCALAPPDATA%\Vivaldi\User Data` |
| Opera, Opera GX | `%APPDATA%\Opera Software\Opera Stable`, `Opera GX Stable` |

The Battle.net launcher is on the same list for the same reason. It embeds the engine through the
Chromium Embedded Framework, and keeps that browser's folder a level below its own, in
`%LOCALAPPDATA%\Battle.net\BrowserCaches`. Inside it the caches sit in a partition the launcher
names itself (`common` on the machine this was measured on, 220 MB), rather than in `Default`. The
launcher's own cache and logs beside that folder are two rows of their own:
[Battle.net launcher cache](#battlenet-launcher-cache) and
[Battle.net launcher logs](#battlenet-launcher-logs).

### What Deguffer does

**It identifies the folder before it looks inside it.** Any directory on your disk may happen to be
called `GPUCache`, so a cache name is never on its own a reason to go in. Deguffer looks for
`Local State`, the file Chromium writes into the user-data folder it owns, and only a folder holding
that file is examined at all. The browser table above only says where to look: a browser's folder
has to hold `Local State` too.

The framework Battle.net uses writes `LocalPrefs.json` instead, into the folder and into each
partition inside it. Deguffer accepts that file only where Battle.net keeps its folder, because an
application of any kind might give its settings that name. A partition is any directory in the
folder that holds its own `LocalPrefs.json`, so the launcher's choice of name never has to be
guessed, and a directory without the file is not looked inside.

Within such a folder it removes exactly the seven directories above and nothing else, one step each,
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
| `Local State`, or `LocalPrefs.json` in Battle.net's folder and each of its partitions | Application settings, and the key that decrypts the three above |

Nothing outside the seven names is ever a candidate, whatever it is called — a directory named
`SuperCache` stays exactly where it is. Deguffer asserts afterwards that every one of these
survived, the ones that are files rather than folders included — those would otherwise never be
checked at all, because the rule that classifies a folder never sees a file.

Deguffer also refuses to delete through a link. If you have redirected an application's cache to
another drive with a junction, it removes nothing there and tells you why. The same applies to a
browser's folder and every folder above it: if you have moved `%LOCALAPPDATA%\Microsoft` onto
another drive with a link, Deguffer names the link and leaves Edge alone.

### What it costs you

Each application starts more slowly once. It fetches the web content it had cached, recompiles its
scripts, and then behaves exactly as before.

**You stay signed in.** Sign-ins, saved passwords, settings and offline data are all in the
neighbouring directories, not in the seven. An application that works offline needs to be online once
to refill what its service worker had stored.

Close the applications first if you can. A running one keeps its cache files open, and anything held
open is left in place rather than removed. Edge can keep running in the background after its last
window closes, so check the notification area for it.

### Why Tier 1

Every one of the seven is derived content with an authoritative source elsewhere: web content the
server still has, and compiled output of scripts that are still on your disk. The engine refills all
of it without being asked, and the cost is a slower first launch.

### Not reached: packaged applications

An application installed from the Microsoft Store does not write to `%APPDATA%`. Windows redirects
it under `%LOCALAPPDATA%\Packages`, and reaching a Chromium cache there is a separate piece of work
that is not done yet. If one of your Store applications embeds Chromium, Deguffer does not currently
see its cache.

### Not reached: what a browser keeps beside its profiles

Edge keeps `component_crx_cache`, `extensions_crx_cache` and `GrShaderCache` in its user-data folder,
beside the profiles, and on one workstation they came to about 210 MB together. None of them is one
of the seven, so all three stay in place until somebody classifies them deliberately.

Opera keeps its web cache in `%LOCALAPPDATA%\Opera Software\Opera Stable`, apart from its settings.
That folder holds no `Local State`, so Deguffer does not identify it, and Opera's web cache stays in
place. Any of the seven that Opera keeps beside its settings in `%APPDATA%` is reached.

---

## VS Code editor caches

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | Any Code - OSS editor's folder one level under `%APPDATA%` — `Code`, `Code - Insiders`, `VSCodium`, `Cursor` and the rest |
| **Method** | Delete the four cache directories the editor writes, plus the web cache inside each webview partition |
| **Typical size** | 2.0 GB was measured on one workstation for a single editor |

### What it is

Visual Studio Code is a Chromium application, so [Chromium application
caches](#chromium-application-caches) above already reaches the seven engine cache directories inside
its folder. Those seven are the small part. The editor keeps four more caches of its own, under names
that belong to VS Code rather than to Chromium. On the measured machine the engine caches came to about
15 MB and these four to 2.0 GB.

| Directory | What it holds |
| --- | --- |
| `CachedData` | The editor's own code, compiled ahead of time, in one folder per build. Sixteen builds were present on the measured machine, of which at most one is installed |
| `CachedExtensionVSIXs` | The installer package of every extension the editor downloaded, kept after it installed it. 775.6 MB across 22 files, two of them successive builds of the same extension at 103 MB each |
| `CachedExtensions`, `CachedProfilesData` | The result of the last extension scan, once overall and once per editor profile |
| `WebStorage\<n>\CacheStorage` | Web content one webview saved so it would not fetch the same thing twice. This is the same kind of content as `Service Worker\CacheStorage`, which the Chromium rules already recognise, under a different parent |

Because every editor built on the Code - OSS base writes the same names into its own `%APPDATA%`
folder, one set of rules reaches all of them. Deguffer does not need to know the editor.

### What Deguffer does

**It identifies the folder before it looks inside it.** `CachedData` is a name any directory on your
disk may happen to have, so a cache name is never on its own a reason to go in. A folder is examined
only if it holds *both* Chromium's `Local State`, which says an Electron application owns it, and
`User\globalStorage\state.vscdb`, which the editor's own storage service creates on first run.

Within such a folder it removes exactly the directories above and nothing else, one step each, so
you can clear one editor and keep another, or clear the extension packages and keep the compiled
code.

`WebStorage` is **not** removed, and neither is any of the numbered folders inside it. Each of those
is one webview's storage, holding what that view *saved* beside what it merely cached, so Deguffer
takes the one recognised cache inside each and leaves the folder standing. The plan says so, so you
are not left wondering why those folders are still there afterwards.

**`CachedData` goes whole, including the folder for the build you are running.** The editor names
each folder after the build that wrote it, and a folder for a build you no longer have can never be
used again — but nothing inside the editor's folder records which build is installed, so Deguffer
cannot tell them apart without guessing, and guessing is the one thing it will not do. Keeping the
live build's folder would save you very little in any case: it is compiled output of code still on
your disk, so the whole cost of removing it is one slower start.

### What is protected

**Everything in `User`, which is the most valuable directory in the folder.** The founding audit
measured `workspaceStorage` alone at 11.3 GB, and not one byte of any of it is a cache:

| Neighbour | What it really is |
| --- | --- |
| `User\workspaceStorage` | The state of every workspace you have opened — the editors, terminals and layout the editor restores |
| `User\globalStorage` | What every installed extension has stored: sign-ins, indexes and its own settings |
| `User\History` | Your local undo history. For a file you never committed it is the only copy of what came before |
| `User\settings.json`, `User\keybindings.json`, `User\snippets`, `User\profiles` | Everything you have configured |

Nothing outside the recognised names is ever a candidate, whatever it is called — a folder named
`CachedSomethingNew` stays exactly where it is, and so does anything Deguffer finds inside a webview
partition that is not the one cache it recognises. Deguffer asserts afterwards that each of the
directories above survived.

Deguffer also refuses to delete through a link. If you have redirected one of these caches to
another drive with a junction, it removes nothing there and tells you why.

### What it costs you

The editor starts more slowly once. It recompiles its own code, rescans your installed extensions,
and then behaves exactly as before. Your extensions themselves are installed elsewhere and are
untouched; only the downloaded installer packages go, and the marketplace supplies one again if it
is ever needed.

Close the editor first if you can. A running one keeps files open, and anything held open is left in
place rather than removed.

### Why Tier 1

Every one of these is derived content with an authoritative source elsewhere: the editor's own code
is on your disk, the extension packages are in the marketplace, and the webview content is on the
server that served it. The editor refills all of it without being asked.

---

## VS Code editor logs and crash reports

**Tier 3 — user data in a cache costume.** Never pre-selected.

| | |
| --- | --- |
| **Location** | `logs` and `Crashpad`, in the same editor folder as the caches above |
| **Method** | Delete the two directories |
| **Typical size** | 0.29 GB was measured on one workstation, across 65 sessions |

### What it is

The editor writes a new folder under `logs` **every single time it starts**, holding what it and
every installed extension wrote to their output channels. It removes none of them. `Crashpad` beside
it is the crash reporter's database: the dump and the metadata for every time the editor stopped
unexpectedly.

Both grow for as long as the editor is installed. On the measured machine `logs` held 141.7 MB
across 65 session folders, and `Crashpad` 152.9 MB.

### What Deguffer does

It removes the two directories, one step each, in every editor folder it identifies — the same two
positive tests as the caches above. The directories themselves are re-created the next time the
editor starts.

### What is protected

The same `User` tree as above, named the same way and asserted to survive in the same way. The
editor's **caches** are protected here too: they are offered separately, under Tier 1, because a
Tier 3 confirmation is not the one you should be giving to delete a regenerable cache.

### What it costs you

**Permanently.** Nothing re-creates the log of a session that has already ended, or the dump of a
crash that will not happen again to order. If an extension author has asked you for a log, this is
where it is. If you are halfway through a bug report, the evidence is here and there is no other
copy.

There is no age cut-off, deliberately. A log written this morning may be exactly the one you need,
and Deguffer will not decide that for you: the tier keeps the row unselected and the confirmation
says plainly that the loss is permanent. If you want a cut-off, the guard on recently changed files
is yours to set, and it protects the session you are running now without anything having to guess
which one that is.

### Why Tier 3

Tier 1 requires that whatever produced the content re-creates it on demand, so that nothing is lost.
Nothing re-creates a record of something that happened. This is the same judgement as [crash dumps
and error reports](#crash-dumps-and-error-reports) and [Windows servicing
logs](#windows-servicing-logs) below, for the same reason.

---

## Claude Code MCP server logs

**Tier 3 — user data in a cache costume.** Never pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\claude-cli-nodejs\Cache\<project>\mcp-logs-<server>` |
| **Method** | Delete each server's log folder |
| **Typical size** | 320 KB across 242 files on the machine this was measured on, the oldest ten weeks old |

### What it is

Every time Claude Code starts an MCP server, it writes what that server reported to a new file, in a
folder per server per project. The folders sit outside Claude Code's own folder, and
`CLAUDE_CONFIG_DIR` does not move them. On the measured machine, eleven of the files were older than
the 30 days after which Claude Code removes a session's own folders, so nothing clears them.

### What Deguffer does

It removes each `mcp-logs-<server>` folder, one step each, and shows when each was last written to.
Nothing else in a project's folder is recognised.

### What is protected

The cache folder, the `claude-cli-nodejs` folder above it, every project's folder, and everything in a
project's folder that is not a server's log folder.

### What it costs you

**Permanently.** Nothing re-creates the log of a server run that has already ended. If a server's
author has asked you for its log, this is where it is.

There is no age cut-off, deliberately, for the reason given for the VS Code logs above: the log you
need may be this morning's. The guard on recently changed files is yours to set on top of that.

### Why Tier 3

Tier 1 requires that whatever produced the content re-creates it on demand, so that nothing is lost.
Nothing re-creates a record of something that happened. This is the same judgement as [VS Code editor
logs and crash reports](#vs-code-editor-logs-and-crash-reports) above, for the same reason.

---

## Claude Code rewind snapshots

**Tier 3 — user data.** Never pre-selected.

| | |
| --- | --- |
| **Location** | `%USERPROFILE%\.claude\file-history\<session>`, or inside `CLAUDE_CONFIG_DIR` where that is set |
| **Method** | Delete each session's snapshot folder |
| **Typical size** | About 105 MB across 6,279 files on the machine this was measured on |

### What it is

Before Claude Code edits a file, it saves a copy, so that a session can be rewound to how its files
were. It keeps one folder of these per session in its own folder. Claude Code removes a session's
folder itself once nothing has written to it for its retention period, 30 days unless you changed it.

### What Deguffer does

It removes each session's folder, one step each, and shows when each was last written to.

- **A session is dated by its folder, never by the snapshots in it.** A snapshot keeps the date of
  the file it copied. On the measured machine, 574 of 1,159 sampled snapshots were more than an hour
  older than their folder, some by five months. Dated by its files, a session in use this morning
  would look months old.
- **A session Claude Code lists as running is left alone**, and each entry in that list is checked
  against Windows. If the list cannot be read, no session is offered, and the row says so. The list is
  read again when you press Clean, immediately before each session's snapshots are removed, so a
  session you resume after the scan keeps every snapshot it may rewind to.
- **Nothing written in the last 7 days is offered**, whatever the list says, and whatever the guard on
  recently changed files is set to. A session can run for days. The same cut-off applies again when you
  press Clean, so a session you resume after the scan keeps the snapshots it has taken since.
- **Only a folder named for a session is recognised.** Anything else in `file-history` is left alone.

### What is protected

Claude Code's folder, `file-history` itself, the folder of any session that is running or was written
to in the last 7 days, and everything in `file-history` that is not a session's folder. The rest of
Claude Code's folder is outside this row: [Claude Code session leftovers](#claude-code-session-leftovers)
covers it.

### What it costs you

**Permanently.** Those sessions can no longer rewind the files they edited to how they were before.
The files as they are now, your conversations and your settings are untouched, and new sessions take
their own snapshots as before.

With the typed confirmation turned on, clearing this and
[Claude Code MCP server logs](#claude-code-mcp-server-logs) in one pass means typing both names. They
are separate rows because what each must leave standing, and how each decides that something is
finished with, differ. VS Code has three rows for the same reason.

### Why Tier 3

Nothing re-creates a snapshot. It is the record of a file as it was, which is the judgement made for
[crash dumps and error reports](#crash-dumps-and-error-reports).

---

## Firefox caches

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\<profile>`, for each profile `profiles.ini` names |
| **Method** | Delete five recognised cache directories, per profile |
| **Typical size** | 350 MB across the five in one profile on the machine this was measured on, of which `cache2` was 322 MB |

### What it is

Firefox is not Chromium, so nothing the Chromium provider recognises applies to it. It also keeps a
profile in **two** places rather than one, and the split is the whole of why this is safe:

| Half | Where | What is in it |
| --- | --- | --- |
| **Roaming** | `%APPDATA%\Mozilla\Firefox\Profiles\<profile>` | Bookmarks, history, saved passwords, certificates, every preference |
| **Local** | `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\<profile>` | The disk cache and the other files Firefox refills by itself |

Deguffer plans against the local half only. Nothing in the roaming half is ever a candidate, whatever
it is called.

| Directory | What it holds |
| --- | --- |
| `cache2` | Web content saved so the same thing is not fetched twice |
| `startupCache` | Interface and script data Firefox precompiles for its own start-up |
| `safebrowsing` | The Safe Browsing lists Firefox checks sites against |
| `thumbnails` | The page images on the new-tab page |
| `jumpListCache` | Icons for Firefox's taskbar jump list |

### What Deguffer does

**It asks Firefox which directories are profiles.** `%APPDATA%\Mozilla\Firefox\profiles.ini` is
Mozilla's own register of them, and a directory it does not name as a profile is never entered — a
directory called `cache2` in a folder nobody registered is left exactly where it is. Each profile's
caches are their own steps, so you can clear one profile and leave another alone.

Firefox has no command that clears its cache from outside the running browser, so these are deleted
directly rather than by asking the tool, and only by exact name.

**A profile you moved somewhere yourself is reported and not examined.** Where `profiles.ini` records
an absolute path, Firefox keeps that profile's cache in the same directory as its bookmarks and
passwords rather than separately from them, and the argument above stops applying. Deguffer says so
in the plan instead of guessing.

### What is protected

**The whole roaming half, and everything in the local half that is not one of the five.** Deguffer
asserts afterwards that these survived:

| Neighbour | What it really is |
| --- | --- |
| `places.sqlite` | Your bookmarks and browsing history |
| `key4.db` | The key that decrypts your saved passwords |
| `logins.json` | Your saved usernames and passwords |
| `cert9.db` | The certificates and exceptions you have accepted |
| `prefs.js` | Every setting you have changed |
| `profiles.ini` | The register itself. Losing it loses every profile |

`remote-settings` is the one directory Deguffer names and then leaves alone. It is datasets Firefox
synchronises for itself, most of it the Firefox Suggest data, and it was four fifths of the local
profile on the machine this was measured on — 1.5 GB of a 1.9 GB folder. It is not user data, but
Mozilla documents no way to remove it and nobody has established what re-downloading it would cost,
so Deguffer measures it, tells you how big it is, and does not offer it.

Deguffer also refuses to delete through a link, and it checks the whole path rather than just the
last folder. If you have moved `%LOCALAPPDATA%\Mozilla` onto another drive with a junction, it
removes nothing there and tells you why.

### What it costs you

Firefox fetches pages from the network rather than from disk for a while, and rebuilds its startup
cache the first time it opens, so one start is slower. The Safe Browsing lists are downloaded again
shortly after that start, and the new-tab thumbnails are drawn again as you revisit the pages.

**Bookmarks, history, saved passwords, open tabs and settings are untouched.** They are all in the
other half of the profile, which Deguffer never removes anything from.

Close Firefox first if you can. A running browser keeps its cache files open, and anything held open
is left in place rather than removed.

### Why Tier 1

Every one of the five is derived content with an authoritative source elsewhere: pages the server
still has, lists Mozilla still publishes, and compiled output of code that is still on your disk.
Firefox refills all of it without being asked.

### Not reached: Thunderbird

Thunderbird keeps the identical layout — its own `profiles.ini`, the same two roots, the same
`cache2`. Deguffer does not reach it yet, because nothing here has been measured against it.
---

## Epic Games launcher web cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache*`, for each such folder on disk |
| **Method** | Delete the recognised cache directories inside each folder, never the folder itself |
| **Typical size** | 339 MB of a 343 MB folder on the machine this was measured on, of which the HTTP cache alone was 287 MB |

### What it is

The launcher does not draw its store itself. It embeds a browser and points it at Epic's web pages,
and that browser keeps its data under the launcher's own `Saved` folder.

**The folder is renamed by every launcher update, and the old ones are left behind.** The suffix is
the engine build the launcher was updated to, so a machine that has run the launcher for years can
be holding `webcache`, `webcache_4147` and `webcache_4430` at once. That is why Deguffer matches a
pattern rather than a name — a known word *and* a number, so `webcache_backup` is not one of these.

Inside each of them is an ordinary browser profile:

| Directory | What it holds |
| --- | --- |
| `Cache` | Pages and pictures saved so the same thing is not fetched twice |
| `Code Cache` | Compiled JavaScript and WebAssembly from the store's own pages |
| `Service Worker\CacheStorage` | Responses a background worker stored so the store works offline |
| `Service Worker\ScriptCache` | The background workers' own scripts |

### What Deguffer does

**It reaches inside the folder rather than removing it, and Epic's own advice is the other way
round.** Epic's support article tells you to delete each `webcache*` folder whole, and for
troubleshooting a broken launcher that is the right instruction. It is the wrong one for reclaiming
space, because the folder also holds `Cookies`, `Local Storage`, `Session Storage` and `IndexedDB` —
your sign-in and the store's saved data. Deleting it signs you out.

Naming the caches inside costs about four megabytes of the 343 MB and keeps you signed in. It is
also the same rule Deguffer already applies to every other embedded browser: see
[Chromium application caches](#chromium-application-caches), which refuses to take a profile folder
whole for exactly this reason.

The launcher exposes no command that clears its cache, so these are deleted directly and only by
exact name.

**`Cache` is removed whole and `Service Worker` is not**, and the difference is what is known to sit
beside the cache. `Cache` is the browser's HTTP disk cache and its entire content is cache entries.
`Service Worker` is not: `Database` inside it is the register of which background workers are
installed for which pages, which is not a cache, so only the two caches beside it are named.

### What is protected

**The `Saved` folder, every `webcache*` folder, and everything in one that is not a recognised
cache.** Deguffer asserts afterwards that these survived:

| Neighbour | What it really is |
| --- | --- |
| `Cookies` | Your sign-in to the store. Removing it signs you out |
| `Local Storage`, `Session Storage` | What the store's pages saved in your browser |
| `IndexedDB` | The store's offline data |
| `Service Worker\Database` | Which background workers are registered for which pages |
| `Config` | The launcher's settings, including the library folders you have added |
| `Saves` | Cloud saves the launcher keeps on your behalf |
| `Data`, `UserVaultSettings` | The launcher's own state and your vault settings |

Deguffer also refuses to delete through a link, and it checks the whole path rather than just the
last folder. If you have moved `%LOCALAPPDATA%\EpicGamesLauncher` onto another drive with a
junction, it removes nothing there and tells you why.

### What it costs you

The store fetches its pages and pictures from the network instead of from disk the first time the
launcher is opened again, and recompiles the scripts behind them, so the store fills in more slowly
once.

**You stay signed in, and nothing in your library changes.** Installed games are not in this folder
at all.

Close the launcher first if you can. It keeps its browser's cache files open while it runs, and
anything held open is left in place rather than removed.

### Why Tier 1

Every one of these is derived content with an authoritative source elsewhere: pages Epic's servers
still have, and compiled output of scripts those servers still send. The browser refills all of it
without being asked, and the folder it refills into is left standing.

---

## Epic Games launcher store artwork

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%PROGRAMDATA%\Epic\EpicGamesLauncher\Data\ContentCache` |
| **Method** | Delete that one folder, and nothing else under `%PROGRAMDATA%\Epic` |
| **Typical size** | 497 MB in 3,917 files on the machine this was measured on, 3,912 of them JPEGs |

### What it is

The launcher keeps a **second** data directory, outside anybody's profile and shared by every
account on the machine. The store's pictures go there: the cover images, screenshots and banners
behind every page you have opened.

Nothing ever removes one. On the machine this was measured on the timestamps ran from November 2022
to September 2026 — nearly four years of artwork, in one flat folder with no subdirectories at all,
including games that have long since left the store.

It is a different folder from the [web cache](#epic-games-launcher-web-cache) above, which is the
embedded browser's own cache inside your profile. A machine that has run the launcher has both.

### What Deguffer does

It removes `ContentCache` and nothing else. Epic documents no command that clears this directory, so
there is nothing to prefer to deleting the path, and the folder is named outright rather than found
by looking around. Nothing under `%PROGRAMDATA%\Epic` is ever classified, so nothing there can
become a candidate by being noticed.

**It does not need administrator rights**, even though `%PROGRAMDATA%` belongs to the machine rather
than to you. Epic's own installer gives every account on the machine full control of
`%PROGRAMDATA%\Epic`, and that carries down to the artwork, so you can clear it as you are. On a
machine whose permissions have been tightened the clean stops, tells you nothing was removed, and
leaves the folder exactly as it was.

### What is protected

**Everything else under `%PROGRAMDATA%\Epic`.** What sits beside the artwork matters more here than
on most rows:

| Neighbour | What it really is |
| --- | --- |
| `Data\Manifests` | **The launcher's record of which games are installed.** Losing it makes the launcher forget your installed library |
| `Data\ManifestTemp` | Where the launcher assembles a new copy of that record before replacing it |
| `VaultCache` | Downloaded game data the launcher is keeping on purpose |
| `Data\DownloadManager`, `Data\Update` | Downloads and updates that are part-finished |
| `Data\Catalog`, `Data\SDMeta`, `Data\ThirPartyManagedApps` | Store and integration data the launcher reads rather than re-fetches |
| `Data\Launcher.manifest`, `Data\Launcher.manifest.meta` | The launcher's record of the build it is running |
| `UnrealEngineLauncher\LauncherInstalled.dat` | The machine's record of where its Epic games are installed, which other launchers read to find them |
| `EpicOnlineServices` | The services Epic games sign in and play online through |

Deguffer asserts afterwards that every one of them survived.

**`Data\EMS` is left alone deliberately**, and it is 79 MB that could have been offered. It holds
promotional images, but beside them sit `.layout`, `.sdmeta` and `.ini` files describing the panels,
and nobody has established what that metadata is for. §5.2's answer to something unidentified is to
leave it, and 79 MB does not justify guessing.

Deguffer also refuses to delete through a link, and it checks the whole path rather than just the
last folder. If you have moved `%PROGRAMDATA%\Epic\EpicGamesLauncher` onto another drive with a
junction, it removes nothing there and tells you why.

### What it costs you

The store downloads each picture again the first time the page showing it is opened, so the
storefront fills in more slowly once.

**Your installed games, your library and your sign-in are untouched.** No game data is in this
folder.

Close the launcher first if you can. It writes artwork into this folder as you browse the store, and
anything it holds open is left in place rather than removed.

### Why Tier 1

Every file in there is a picture Epic's servers still hold. The launcher fetched it on demand and
fetches it again on demand, and nothing else is the authority for any of it.

---

## Epic Games launcher logs and crash reports

**Tier 3 — user data.** Never pre-selected, and confirmed before it runs.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\EpicGamesLauncher\Saved\Crashes` and `...\Logs` |
| **Method** | Delete the two recognised directories |
| **Typical size** | 56 MB of crash reports and 0.7 MB of logs on the machine this was measured on |

### What it is

The launcher writes a log every time it or its updater runs, and gathers a full crash report
whenever it fails. Neither folder is ever trimmed, so on a machine that has run the launcher for
years the crash reports alone can be tens of megabytes.

### What Deguffer does

It removes the two directories by name. They sit in the same folder as the launcher's settings, its
cloud saves and the store's browser data, so nothing else in that listing is ever a candidate.

**This is a separate row from the web cache above, deliberately.** They are two different kinds of
thing with two different costs, and keeping them apart lets you clear 339 MB of browser cache
without touching the evidence of a crash. It is the same split
[Windows servicing logs](#windows-servicing-logs) made against
[crash dumps and error reports](#crash-dumps-and-error-reports).

**There is no age cut-off, and each row carries the newest write inside it instead.** A report
written this morning may be the only evidence in a support ticket somebody is still writing, so the
decision stays yours: the row is never ticked for you, and it shows you how recently something was
written.

### What is protected

Everything else in the launcher's folder: `Config`, `Data`, `Saves`, `UserVaultSettings` and every
`webcache*` folder. Deguffer asserts afterwards that they survived.

### What it costs you

The record of every crash and every session the launcher has already had is destroyed, so none of it
can be attached to a support ticket afterwards. **This is permanent.** Nothing re-creates a crash
report.

The launcher writes a fresh log the next time it starts, and nothing about how it runs changes.

### Why Tier 3, not Tier 1

Tier 1 requires that whatever produced the content re-creates it, so that nothing is lost. What is
re-created here is the *next* log, never the ones removed: a crash report is the record of an event,
and the event will not happen again to order. That is the property that puts logs and records in
Tier 3, and the consequence column there says the loss is permanent — which is exactly right.

---

## Battle.net launcher cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\Battle.net\Cache` |
| **Method** | Delete that one folder, and nothing else in the launcher's folder |
| **Typical size** | 40.4 MB on the machine this was measured on |

### What it is

Blizzard's desktop launcher keeps a cache of what it downloads in its own folder in your profile.
Every file in it is named by 32 hexadecimal digits and filed under a folder named by the first two
of them. That is the shape of a store of downloads looked up by a hash, which the launcher fills
from Blizzard's servers.

It is not the launcher's built-in browser cache, which is larger (220 MB on the same machine) and
sits beside it in `BrowserCaches`. That one is reached by the
[Chromium application caches](#chromium-application-caches) row.

### What Deguffer does

It removes `Cache` and nothing else. The launcher has no command that clears it, so there is nothing
to prefer to deleting the path, and the folder is named outright rather than found by looking
around. Nothing else in the launcher's folder is ever classified, so nothing there can become a
candidate by being noticed.

### What is protected

Everything else in `%LOCALAPPDATA%\Battle.net`, and Deguffer asserts afterwards that it survived:

| Neighbour | What it really is |
| --- | --- |
| `Account` | The launcher's data for each account that has signed in on this machine |
| `CachedData.db` | A database the launcher keeps. Nobody has established what is in it, so it is left alone |
| `BrowserCaches`, and the `LocalPrefs.json` in it | The built-in browser, with your sign-in to it and the key that decrypts it |
| `Logs` | The launcher's logs, which have a [row of their own](#battlenet-launcher-logs) |

Deguffer also refuses to delete through a link, and it checks the launcher's folder as well as the
cache. If you have moved either onto another drive with a junction, it removes nothing there and
tells you why.

### What it costs you

The launcher downloads what it had cached again the next time it needs it, so it may start more
slowly once. **Your sign-in, your games and your settings are untouched.**

Close the launcher first if you can. Anything it holds open is left in place rather than removed.

### Why Tier 1

The folder has the shape of a store of downloads looked up by a hash, and every file in it came
from Blizzard's servers, which still hold it. The launcher downloads what it needs again, so the
cost of clearing it is that download.

### Not offered: the machine-wide folders

Blizzard's support article
([Deleting the Battle.net cache folder](https://us.support.blizzard.com/en/article/34721)) has
players delete the `Blizzard Entertainment` folder under `%PROGRAMDATA%`. Deguffer does not offer
that folder, and it does not offer the launcher's other machine-wide folder,
`%PROGRAMDATA%\Battle.net` (25.9 MB on the same machine), either. That folder holds `Agent`, the
update agent's live state, and nobody has established what removing either folder costs. Deguffer
leaves both alone until somebody does.

---

## Battle.net launcher logs

**Tier 3 — user data.** Never pre-selected, and confirmed before it runs.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\Battle.net\Logs` |
| **Method** | Delete that one folder, and nothing else in the launcher's folder |
| **Typical size** | 1.0 MB on the machine this was measured on |

### What it is

The launcher writes a log every time it starts, and its built-in browser writes another beside it.
Nothing removes the old ones.

### What Deguffer does

It removes `Logs` by name, and nothing else in the launcher's folder. It is a separate row from the
[launcher cache](#battlenet-launcher-cache) because the two cost different things: you can clear the
cache without touching the record of what the launcher did. The row is never ticked for you, and it
shows how recently a log was written.

### What is protected

The same neighbours as the cache row: `Account`, `CachedData.db`, `BrowserCaches` and the
`LocalPrefs.json` in it, and the launcher's `Cache`. Deguffer asserts afterwards that they survived.

### What it costs you

The record of every session the launcher has already had is destroyed, so none of it can be attached
to a support ticket afterwards. **This is permanent.** The launcher writes a fresh log the next time
it starts, and nothing about how it runs changes.

### Why Tier 3, not Tier 1

What is re-created here is the *next* log, never the ones removed. That is the reasoning the
[Epic Games launcher logs](#epic-games-launcher-logs-and-crash-reports) row gives, and it applies
unchanged.

---

## Steam web cache

| **Location** | `%LOCALAPPDATA%\Steam\htmlcache`, and `appcache\httpcache` under wherever Steam is installed |
| **Method** | Delete the two named caches |
| **Typical size** | 472 MB in `htmlcache` on the machine this was measured on, out of 513 MB for Steam's whole folder in the profile |

### What it is

The Steam client draws its store, its library and the in-game overlay in a browser built into the
client, and that browser saves what it downloads. Steam splits the result across **two** directories,
and only one of them is in your profile:

| Where | What is in it |
| --- | --- |
| `%LOCALAPPDATA%\Steam\htmlcache` | The embedded browser's cache — store, library and community pages |
| `<Steam install>\appcache\httpcache` | The client's own HTTP cache, kept beside the program |

The install directory is not under your profile. It moves with whichever drive you gave your game
library, and the same folder holds every game you have installed.

### What Deguffer does

**It asks Windows where Steam is rather than assuming.** Steam records its own install directory as
it starts, and Deguffer reads that record. It then treats the directory as an install only if the
Steam program is actually sitting in it — a record pointing somewhere Steam is not gets a sentence in
the plan, not a deletion. If nothing on the machine says where Steam is, the plan says that too, and
that cache is neither cleared nor ruled out. It is never guessed at.

**Neither folder is ever listed.** Deguffer names the two caches outright and looks at nothing else,
so there is no route by which something beside them could be found and classified. Each cache is its
own step, so you can clear one and leave the other.

Steam has no command that clears either cache from outside the running client, so these are deleted
directly rather than by asking the tool.

### What is protected

**Everything else in both folders**, and the things that matter most are asserted by name rather than
covered by an assertion on the folder above them:

| Neighbour | What it really is |
| --- | --- |
| `steamapps` | Your installed games |
| `steamapps\common` | The games themselves, on disk |
| `steamapps\downloading` | The half-downloaded part of an update. Removing it restarts the download |
| `steamapps\workshop` | Workshop content you subscribed to |
| `userdata` | Your Steam settings, cloud saves and screenshots, per account |
| `config` | Steam's own configuration, including who is signed in on this computer |
| `appcache\appinfo.vdf`, `appcache\packageinfo.vdf` | Steam's own indexes, sitting in the same folder as the cache |
| `local.vdf` | The Steam client's settings for this computer, sitting in the same folder as the browser cache |

Two things are recognised and then deliberately left alone. `widevine` is a content-decryption
module Steam downloaded so protected video will play, which is downloaded software rather than a
cache. `cefdata` is the embedded browser's working data, and nobody has established what removing it
costs. `appcache\librarycache`, the artwork Steam downloaded for your library, is not part of this
row either: it has a row of its own, [Steam library artwork](#steam-library-artwork).

Deguffer also refuses to delete through a link. If you have moved either cache onto another drive
with a junction, it removes nothing there and tells you why.

### What it costs you

The client fetches store, library and community pages from the network instead of from disk for a
while, so they draw more slowly the first time. It may ask you to sign in again to the pages it shows
inside the client.

**Your installed games, any download in progress, your Workshop content, your cloud saves and your
settings are untouched.**

Close Steam first if you can. A running client keeps both caches open, and anything held open is left
in place rather than removed.

### Why Tier 1

Both are copies of pages and files Valve's servers still have. The client downloads what it needs
again the next time it needs it, and nothing that only exists on your disk is in either of them.

### The shader cache is a separate row

`steamapps\shadercache`, the shaders Steam downloads for each game, is not part of this row. Getting
it back costs a download from Valve rather than a slower page, so it is Tier 2 and has a row of its
own: see [Steam shader pre-cache](#steam-shader-pre-cache).

---

## Steam library artwork

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `appcache\librarycache\<app id>` under wherever Steam is installed |
| **Method** | Delete the folders for single games, one item each |
| **Typical size** | 1,675 MB across 3,911 games on the machine this was measured on |

### What it is

Steam downloads the pictures it shows for every game in your library: the capsule, the banner, the
hero image behind the game's page and its logo. That includes games you own and have never
installed. It keeps them per game, in a folder named by the game's Steam application id:

```
<Steam install>\appcache\librarycache\<app id>\library_600x900.jpg
```

**Steam keeps them for years.** On the machine this was measured on, the oldest files were years old
and the newest were from that day. The folder grows with your library, and it sits with the Steam program, which is
often on the system drive even when the games are on another one.

### What Deguffer does

**Only what belongs to a single game is offered.** Inside `librarycache`, a folder is removed only if
its name is a Steam application id: digits only, and no larger than Steam allows. Older Steam clients
wrote each picture straight into `librarycache` as `<app id>_<picture>.jpg` or `.png`, and those files
are offered too, as part of their game. Anything else, such as `backup`, `440.old` or a file that is
not a picture, stays in Tier 4, and the plan names it.

**Each game is its own item.** Deguffer names a game from Steam's manifest for it where the game is
installed, and shows the app id where it is not. Keeping a game keeps all of its artwork, in either
layout.

**Steam's index of the artwork, `assetcache.vdf`, is never removed.** Removing a game's folder does
not need it to change: with the folders for three games removed and the index left listing them,
Steam drew their artwork and downloaded the files again within seconds of showing the games.

Steam's **Settings → Downloads → Clear Download Cache** is reported to clear `appcache`, but Valve
has not said that it reaches this folder, and it is a button in a running client rather than a
command Deguffer can run. So the folders are deleted directly.

### What is protected

| Neighbour | What it really is |
| --- | --- |
| `appcache\librarycache` itself | The container. Only what belongs to single games inside it goes |
| `appcache\librarycache\assetcache.vdf` | Steam's index of the artwork it has saved |
| `appcache\appinfo.vdf`, `appcache\packageinfo.vdf` | Steam's own indexes |
| `steamapps`, `steamapps\common`, `steamapps\downloading` | Your games, and the half-downloaded part of an update |
| `userdata` | Your settings, cloud saves, screenshots, and artwork you chose through Steam, per account |
| `config` | Steam's own configuration, including who is signed in on this computer |

Explore enforces the same rule: it refuses `librarycache`, the index and anything unrecognised in it,
and allows only what belongs to a single game. It looks at what each entry is, not only at its name,
so a file named like a game's folder, or a folder named like a picture, is refused there too.

Deguffer also refuses to look through a link. If `librarycache` or a game's folder in it is a link to
another drive, it removes nothing there and tells you why.

### What it costs you

Steam downloads a game's artwork again the next time it shows the game in your library. While you
are offline, a game may show a blank picture until it can.

**If you replaced any of these pictures by hand, that picture is lost.** Changing a game's artwork by
overwriting the files in this folder is a long-standing trick, and Deguffer cannot tell such a file
from one Steam downloaded. Keep that game, or set the picture through Steam's own **Manage → Set
custom artwork**, which stores it under `userdata` where Deguffer never goes.

### Why Tier 2

Almost every file is a copy of a picture Valve's servers still have, and Steam fetches it again on
demand. That was observed on a real client rather than assumed, and on that evidence alone this
would be Tier 1.

It is not, because of the pictures replaced by hand. Tier 1 is for what loses nothing, and it is
ticked without asking. A replaced picture is lost for good, and Deguffer cannot tell it from a
downloaded one. So the row is offered but never ticked for you, and you acknowledge it before it
runs.

---

## Steam shader pre-cache

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `steamapps\shadercache\<app id>` in every Steam library |
| **Method** | Delete the folders for single games, one item each |
| **Typical size** | Nothing measured here: pre-caching was off, and the folder was empty. Reports from machines with large libraries and pre-caching on run past 60 GB |

### What it is

A game compiles its shaders for your graphics card the first time it needs them, and it stutters
while it does. With **Shader Pre-Caching** switched on in Steam's settings, Steam downloads those
shaders already compiled for your card and driver, and keeps them per game:

```
<Steam library>\steamapps\shadercache\<app id>\fozpipelinesv6\...
```

Every Steam library has one, and a library can be on any drive. The folder is named by the game's
Steam application id, such as `440`, rather than by the game's name.

### What Deguffer does

**It reads Steam's own list of your libraries.** Steam records every library you have added in
`steamapps\libraryfolders.vdf` under the install directory, and Deguffer reads that file rather than
searching your drives. Both the current layout of the file and the older one are understood. If the
list cannot be read or does not make sense, Deguffer still looks in the library beside the Steam
program, and the plan says plainly that any other library was neither cleared nor ruled out.

**Only folders named as a game are offered.** Inside `shadercache`, a child is removed only if its
name is a Steam application id: digits only, and no larger than Steam allows. Anything else, such as
`backup` or `440.old`, stays in Tier 4, and the plan names it.

**Each game is its own item, named as Steam names it.** Deguffer lists the games in the row's items,
so you can choose them one by one and keep one you want to hold on to. It reads each game's name from
Steam's manifest for it, `appmanifest_<app id>.acf`, and says whether the game is still installed in
any library. A cache for a game you have uninstalled costs nothing to remove. Where a manifest is
missing, the item shows the app id instead. Where Deguffer could not look in every library, it says
nothing about whether a game is installed rather than guess.

**A library the list names but Deguffer cannot place**, an entry with no full path, is named on the
plan as a warning, and a shader cache in it is neither cleared nor ruled out.

**No row where there is nothing to reclaim.** An empty `shadercache`, or one holding only empty game
folders, produces no row. Steam keeps the folder even with pre-caching switched off.

Steam can delete pre-cached shaders itself, from **Settings → Shader Pre-Caching**. That is a button
in a running client, not a command Deguffer can run, so the folders are deleted directly.

### What is protected

**Everything in the library except the game folders inside `shadercache`**, and the things that matter
most are asserted by name after a clean rather than covered by an assertion on the folder above them:

| Neighbour | What it really is |
| --- | --- |
| The library folder and `steamapps` | Your games, and Steam's records of them |
| `steamapps\common` | The games themselves, on disk |
| `steamapps\downloading` | The half-downloaded part of an update. Removing it restarts the download |
| `steamapps\temp` | Steam's working space for an update in progress |
| `steamapps\workshop` | Workshop content you subscribed to |
| `steamapps\sourcemods` | Mods for Source games that you installed yourself |
| `steamapps\libraryfolders.vdf` | Steam's list of your game libraries |
| `libraryfolder.vdf` | Steam's record that a folder is one of its libraries |
| `steamapps\appmanifest_<app id>.acf` | Steam's record that a game is installed, for each game whose cache is removed |
| `steamapps\shadercache` itself | The container. Only the game folders inside it go |

Explore enforces the same rule: in every library it refuses `steamapps`, everything in it, and
`shadercache` itself, and allows only a game's folder inside `shadercache`.

Deguffer also refuses to look through a link. If `steamapps` or `shadercache` is a link to another
drive, or a game's folder is, it removes nothing there and tells you why.

### What it costs you

**While pre-caching is on, Steam downloads the shaders again from Valve for each game you play**,
which can be several gigabytes. With it off, a game compiles its own shaders as it runs, and stutters
the first time each scene appears.

**Your games, your saves, your Workshop content and any download in progress are untouched.**

Close Steam first if you can. The client downloads and processes shader caches while it runs, and a
plan made with Steam open warns you.

### Why Tier 2, not Tier 1

A shader cache the game or driver rebuilds on your machine is Tier 1, as the
[GPU shader caches](#gpu-shader-caches) are. This one is downloaded. Switching pre-caching off and on
again is reported to fetch all of it again from Valve
([steam-for-linux#13215](https://github.com/ValveSoftware/steam-for-linux/issues/13215)), so the
cost of removing it is bandwidth and time rather than a moment of stutter. That is the second tier
by definition.

---

## Unreal Engine derived data cache

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\UnrealEngine\Common\DerivedDataCache`, `%LOCALAPPDATA%\UnrealEngine\Common\Zen\Data`, `%PROGRAMDATA%\Epic\Zen\Data`, and any Zen store a setting moves |
| **Method** | Delete each cache folder as a whole |
| **Typical size** | Nothing measured here: the machine held Unreal's settings and no cache. Community reports put an active project's cache at 16 to 100 GB, growing by 1.5 to 2 GB each working day |

### What it is

Unreal Engine compiles shaders and prepares every asset in a form the editor can use, and it keeps
the results in a *derived data cache* so it does not have to do that work again each time a project
opens. One cache is shared by every project on the machine, in one of two forms:

- **The filesystem cache**, used by Unreal Engine 5.3 and earlier, at
  `%LOCALAPPDATA%\UnrealEngine\Common\DerivedDataCache`. Later versions put it into a mode that only
  deletes, and expire anything nothing has used for eight days.
- **A Zen store**, used by Unreal Engine 5.1 and later and run by a small server, `zenserver`, that
  the editor starts. Its default moved at 5.4: 5.1 to 5.3 keep it at `%PROGRAMDATA%\Epic\Zen\Data`,
  and 5.4 and later at `%LOCALAPPDATA%\UnrealEngine\Common\Zen\Data`. Epic's documentation still
  gives the first for 5.4, which is out of date. A machine that ran both can hold both.

A setting can move the Zen store:

| Setting | Where the store goes |
| --- | --- |
| `UE-LocalDataCachePath`, as an environment variable or the editor's **Global Local DDC Path** preference | A `Zen` folder inside the path |
| `UE-ZenDataPath` or `UE-ZenSubprocessDataPath`, as an environment variable, or `DataPath` under `HKEY_CURRENT_USER\Software\Epic Games\Zen` | The path itself |

Deguffer reads every one of these. A path given on one editor's command line is recorded nowhere, and
cannot be found.

### What Deguffer does

**It looks for content, not for folders.** `%LOCALAPPDATA%\UnrealEngine` exists on any machine that
has run the Epic launcher, and on the machine this was measured on it held only settings. A cache
folder that holds nothing produces no row.

**Each cache is deleted whole.** Epic documents no command that clears either one, and Epic's own
advice for clearing one by hand is to delete the folder and let it fill again.

**A folder a setting names is reached only where it is Zen's alone.** It is a folder somebody chose,
and Zen writes into whatever folder it is given. So Deguffer removes the store only where Zen's own
`root_manifest` file is there and every entry at its top is one Zen writes. A single file of anybody
else's, and the folder is left alone. A store inside or around Unreal's own folders, or inside or
around another store a setting names, is never followed either. The filesystem cache an older
engine wrote straight into a local cache path is not removed: nothing tells its entries apart from
anything else in that folder, and the delete-only mode empties it anyway.

**While `zenserver` is running, every Zen store is left alone.** The server can be set to keep
running after the editor closes, and removing a store's data under the server writing it is not
provably safe. The plan says so, Explore refuses the store too, and closing the editor and the server
and scanning again includes it. A running editor is a warning beside the filesystem cache, and
anything it holds open stays.

### What is protected

| Neighbour | What it really is |
| --- | --- |
| `%LOCALAPPDATA%\UnrealEngine` and `Common` | Unreal's own folders. Only the caches inside them go |
| Every engine version's folder beside `Common`, and its `Saved\Config` | That version's editor settings and crash reports |
| `Common\Zen\Install` and `%PROGRAMDATA%\Epic\Zen\Install` | The Zen server itself |
| `%PROGRAMDATA%\Epic`, `EpicGamesLauncher`, `EpicOnlineServices` | Epic's other machine-wide data |
| `UnrealEngineLauncher\LauncherInstalled.dat` | The machine's record of where its Epic games and engines are installed |
| A folder a setting names | Somebody's own folder. Only the store inside it goes |
| Anything else beside a cache in `Common`, `Common\Zen` or `%PROGRAMDATA%\Epic\Zen` | Not recognised as part of the cache, so it is named and left alone |

Explore enforces the same rule: it refuses Unreal's folder, `Common`, each default `Zen` folder and
everything in them except the caches themselves. Deguffer also refuses to look through a link.

### What it costs you

**The next time you open a project, Unreal compiles its shaders and prepares its assets again.** On a
large project that can take tens of minutes. Your projects, your engine settings and your installed
engines are untouched.

### Why Tier 2, not Tier 1

Nothing in the cache is anyone's only copy, so it is not Tier 3. But the refill is a shader compile
measured in tens of minutes on a real project, which is §3's definition of the second tier: re-created,
but only by re-indexing for minutes.

---

## Spotify streaming cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\Spotify\Data` for the edition Spotify's own installer puts in place, and `LocalCache\Spotify\Data` inside the Microsoft Store edition's package folder |
| **Method** | Delete the named cache folder, never the folder around it |
| **Typical size** | Not measured: Spotify was not installed on the machine this was researched on |

### What it is

Spotify's own storage article says the app uses your disk to "Store parts of music and podcasts as
cache, so it can play without lagging" and to "Store downloaded music and podcasts to play offline".
Those are two stores in two folders, and only the first is a cache:

| Folder | What is in it |
| --- | --- |
| `Data` | The streaming cache |
| `Storage` | The music and podcasts you downloaded to play offline |

**Spotify documents neither path.** Its article names no folder on Windows at all. The locations here
are what Spotify's community moderators, its volunteer experts and other tools report, so they are
observed rather than Spotify's word. The sources are listed at the end of this section.

The Microsoft Store edition lays the same things out differently. Its cache is under
`LocalCache\Spotify` inside `%LOCALAPPDATA%\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0`, and its
downloads sit beside its settings under `LocalState\Spotify`.

### What Deguffer does

**It removes `Data` by name, and never lists either Spotify folder.** There is no route by which
anything beside the cache could be found and classified.

**It never offers `Storage`, at any tier.** Downloads are a Premium feature, so getting them back
needs an active subscription, and Deguffer cannot see whether you have one. If your Premium has
lapsed, deleting them loses offline listening for good. At least two general-purpose cleaners treat
`Storage` as cache; Deguffer does not.

**It reads where Spotify keeps its storage rather than assuming.** Spotify's Settings page can move
its storage to another folder, and Spotify records the move in its `prefs` settings file. Which of
the two stores moves is not something anyone can say for certain: Spotify's help says the button
stores "your cache somewhere else", the app labels it offline storage, and Spotify's moderators say
the cache cannot be moved at all. So Deguffer assumes neither:

- **Nothing in a folder the settings move storage to is measured or removed.** Deguffer says it was
  neither cleared nor ruled out, checks afterwards that it is still there, and refuses it on the
  Explore page unless it is a whole drive, which Explore would otherwise lose entirely. A folder
  above Spotify's own is refused along with everything else in it, except what another row
  recognises in a folder of its own further down.
- If that folder **is** a cache folder, sits inside one or holds one, that cache is **not offered**,
  because your downloads may be in it.
- If the settings file **cannot be read**, or names a location Deguffer cannot place, **no Spotify
  cache is offered**, for the same reason. A file that is not plain UTF-8 text counts as one Deguffer
  cannot place, and any location beside the one it cannot place is still protected.

Spotify's documented way to clear the cache is a button inside the running app: **Settings**,
**Storage**, **Clear cache**. Nothing does the same from outside the app, so this is the path-based
case §5.2 governs rather than §5.1's command.

### What is protected

**Everything else in Spotify's folders**, and the things that matter most are asserted by name rather
than covered by an assertion on the folder above them:

| Neighbour | What it really is |
| --- | --- |
| `Storage` | The music and podcasts you downloaded. Getting them back needs Premium |
| `offline.bnk` | Spotify's record of what you downloaded |
| `prefs` | Spotify's settings, including where it keeps your downloads and who is signed in |
| `Users` | Spotify's settings and saved state for each account signed in on this computer |
| A moved storage folder | Wherever Spotify's settings say its storage is now, or was before |

In the installer's edition, `Storage` and `offline.bnk` sit beside the cache in
`%LOCALAPPDATA%\Spotify`, and `prefs` and `Users` sit with the program in `%APPDATA%\Spotify`. In the
Store edition all four sit in `LocalState\Spotify`.

Deguffer also refuses to delete through a link. If you have moved the cache onto another drive with a
junction, it removes nothing there and tells you why.

### What it costs you

Songs and podcasts Spotify had kept on disk are streamed again the next time they play, so they may
take a moment to start and use more data for a while.

**The music and podcasts you downloaded, your settings and your sign-in are untouched.**

Close Spotify first if you can. It keeps its cache files open while it plays, and anything held open
is left in place rather than removed.

### Why Tier 1

Everything in the cache is a copy of something Spotify's servers still have, and Spotify fetches it
again on the next play. Spotify's own instruction for clearing it carries no warning about what
clearing removes, which is the vendor's own assessment of the cost. The downloads beside it are the
opposite case, which is why they are kept out entirely rather than offered at a higher tier.

### Sources

- Spotify's storage article, for what the cache and the downloads are and how the app clears the
  cache: <https://support.spotify.com/us/article/storage-information/>
- Spotify Community threads in which Spotify's moderators say the desktop app's cache location
  cannot be changed, and that the storage setting moves offline storage:
  <https://community.spotify.com/t5/Desktop-Windows/Changing-the-Cache-location/td-p/4758102> and
  <https://community.spotify.com/t5/Desktop-Windows/Data-on-quot-C-quot-drive-still-grows-even-when-I-select-offline/td-p/4807400>
- A Spotify Community thread on the Microsoft Store edition's `LocalCache` and `LocalState` folders:
  <https://community.spotify.com/t5/Desktop-Windows/localState-folder-and-localCache-folder/td-p/5153299>
- Winapp2, which lists `Storage` for both editions, in a section of its own named for downloaded
  songs: <https://github.com/MoscaDotTo/Winapp2>
- spicetify, which reads `offline.bnk` from `%LOCALAPPDATA%\Spotify`: <https://github.com/spicetify/cli>
- A comment in a proposed BleachBit cleaner rule, which records that the storage location is saved
  in `%APPDATA%\Spotify\prefs` and shows the format of the `storage.location` line:
  <https://github.com/bleachbit/cleanerml/blob/f28fbdaec0e8264c38e00c6c6463d39c081cadf6/pending/spotify.xml>

---

## Affinity machine-learning models

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `%USERPROFILE%\.affinity\Common\<version>\modelcache` and `%APPDATA%\Affinity\Common\<version>\modelcache` |
| **Method** | Delete the whole `modelcache` folder inside each version folder, and nothing else in it |
| **Typical size** | 404 MB for Affinity 2 and 326 MB for Affinity 3 on the measured machine; ~730 MB together |

### What it is

Affinity Photo, Designer and Publisher run machine-learning models for subject selection, object
selection and the other selection tools that work out what is in a picture. Those models are not in
the installer. The application downloads them the first time one of the features needs one, and
keeps them in `modelcache` inside the folder its products share.

The shared folder is per major version, and the location has moved: Affinity 1 kept it under
`%APPDATA%\Affinity`, Affinity 2 moved it to a hidden `.affinity` folder in your profile, and
Affinity 3 moved it back. Both roots are live on a machine that has run more than one version, so
Deguffer reads both and works out the version folders inside them rather than looking for `2.0` or
`3.0` by name.

**A version you have uninstalled keeps its models.** On the measured machine the whole Affinity 2
tree had not been written to for eleven months, and 404 MB of it was models nothing would ever load
again.

### What Deguffer does

Inside `Common`, Deguffer looks in a folder only if its **whole name is two dotted numbers** —
`1.0`, `2.0`, `3.0`. That is the shape Affinity has always written. `3`, `3.0.1`, `v3` and anything
you created yourself do not qualify: they stay in Tier 4, Deguffer does not look inside them, and it
tells you it is leaving them alone.

Inside a version folder, **`modelcache` is the only child Deguffer will ever remove.** Every other
entry is Tier 4 by construction, whether or not Deguffer has heard of it.

A version folder Deguffer is not allowed to list is left alone entirely, even though the cache
inside it can still be reached by its full name. Listing a folder and passing through it are
separate permissions, so a folder that refuses the first would let Deguffer remove the cache while
never having seen what was standing beside it — and there would then be nothing to check afterwards.

### What is protected

**Everything else in the version folder, by name**, which is the point of this entry rather than a
footnote to it. `modelcache` sits directly beside:

- **`user`** — your asset library and your raster and vector brushes. On the measured machine
  `assets.propcol` alone was 903 MB, which is more than every model put together.
- **`Licences`** and **`Receipts`** — what keeps the product activated.
- **`Settings`**, **`Plugins`** and **`migrate`** — your settings, your plugins, and what Affinity
  carried forward from the version before.
- **`locks`**, **`clipboard`**, **`ipc.dat`**, **`cs.dat`** and **`sp.db`** — live state three
  applications share while they are running.

Also protected: both profile roots, the `Common` folder, and every version folder. A cleaner that
removed `Common\<version>` instead of `Common\<version>\modelcache` would take your whole asset
library with the models, which is why Deguffer names every entry it leaves standing and checks
afterwards that each one is still there.

The per-product folders beside `Common` — `Photo`, `Designer`, `Publisher`, `Affinity` — are not
touched at all. `autosave`, `backup` and `temp-critical` in them hold recovery copies of documents
you have not saved.

### What it costs you

**A download, the next time a selection tool needs a model.** Affinity fetches it again before the
feature will run: a few hundred megabytes over an internet connection, and on Affinity 3 through the
account you are signed in with. The application keeps working; it is the machine-learning features
that wait.

Your documents, brushes, assets, licences and settings are untouched.

### Why Tier 2, not Tier 1

Nothing fetches a model back in the background. The next subject selection is what discovers it has
gone, and getting it back needs a connection you may not have at that moment.

**Affinity has its own control for this, and it is the one to prefer.** Settings → Machine Learning
lists the model categories and offers install and uninstall for each, and Affinity's help says the
models can be uninstalled to reclaim space and reinstalled when the features are next wanted. It is
a window rather than a command, so Deguffer cannot call it on your behalf, but §5.1's preference
still stands and the row says so.

**Serif publishes nothing about clearing this folder.** What is written here is direct observation
of what Affinity writes, and Deguffer says that in the row rather than leaving it out.

Sources:

- Affinity Photo 2 help — machine learning:
  <https://affinity.help/photo2/en-US.lproj/pages/Extras/machineLearning.html>

---

## Squirrel updater leftovers

| **Location** | `%LOCALAPPDATA%\SquirrelTemp`, and the `packages` folder inside each application Squirrel installed |
| **Method** | Delete what the updater unpacked, and the update packages an application's own index has stopped naming |
| **Typical size** | 466 MB of staging, and 87 MB of spent packages across three applications, on the machine this was measured on |

### What it is

Squirrel is the updater behind a large family of Windows desktop applications. An application using
it installs into `%LOCALAPPDATA%\<the application's name>` and keeps three things there: `Update.exe`,
one `app-<version>` folder per installed build, and a `packages` folder holding what it downloaded.
Installs and updates are unpacked somewhere else again — `%LOCALAPPDATA%\SquirrelTemp`, which **every
Squirrel application on the machine shares**.

Both locations are meant to be temporary, and neither reliably is:

| Where | Why it accumulates |
| --- | --- |
| `SquirrelTemp` | The unpacked copy is deleted when the update finishes. An application killed part-way through never gets there, and what it unpacked stays |
| `packages` | After an update, Squirrel deletes every package its index no longer names — in a loop with no error handling, so the first failure abandons the rest |

This is not a rare edge. Squirrel's own issue tracker carries reports of 718 MB, 2 GB, 6.2 GB, 13 GB
and 35 GB in one staging folder, over nine years, and it was never fixed. The maintainer's answer
both times was that deleting the folder is safe, and that the library cannot do it itself: Squirrel
is a library, several applications use it, and one of them cannot know that another is not
installing through that folder at this moment.

### What Deguffer does

**It identifies an application positively, and needs both halves.** A folder qualifies only if it
holds `Update.exe` *and* a child named for a version Deguffer can read. Other software ships a file
called `Update.exe`, and a folder holding a directory whose name begins `app-` is not evidence of an
updater. A folder that fails the pair is invisible, and nothing inside it is offered.

**In the staging folder it removes only what Squirrel's own name generator produced.** Those names
are the word `temp` followed by a single character from a fixed 360-character alphabet — `tempa`,
`tempb`, and so on — because the updater hands out the first free name each time. A directory whose
name does not match is somebody else's and is left alone.

That rule is deliberately narrower than the generator. Given more than 360 staging directories at
once the updater starts producing longer names, and Deguffer leaves those alone too, because a
longer run of letters cannot be told from an ordinary word: matching `temp` followed by *one or
more* characters would also claim `templates`, `temporary` and `tempdata`. Under the default folder
that would cost nothing, since nothing else writes there. Under a `SQUIRREL_TEMP` pointing at a
folder you share with anything else, it would offer your directory for deletion. The location is read from `SQUIRREL_TEMP` where that is set,
because Squirrel reads it too — and if it is set to something that is not a full path, Deguffer says
so and leaves the folder alone rather than guessing.

**Before removing a staging directory it asks whether anything is running from inside it, and
refuses the ones that are.** This is the collision the maintainer named, and it is a refusal rather
than a warning. It asks again when you press Clean, immediately before each directory is removed, so
an install that starts after the scan keeps the directory it is running from.

**In a `packages` folder it reads the application's own index.** `RELEASES` lists the packages the
application still needs. Deguffer removes package files that index has stopped naming, and nothing
else. If the index is missing or will not parse, nothing in that folder is offered at all — without
it there is no way to tell a spent package from the one the next update is built against.

**A package for a build newer than the one installed is never removed, even when the index does not
name it.** Squirrel writes a downloaded update into that folder *before* it rewrites the index, so an
unnamed package can be an update part-way through downloading rather than debris.

Squirrel has no clean-up command. `Update.exe` can install, uninstall, download, update, make and
remove shortcuts, start a process, update itself and check for updates, and nothing else — so these
are deleted directly rather than by asking the tool.

### What is protected

| Neighbour | What it really is |
| --- | --- |
| `SquirrelTemp` itself, and the setup logs in it | The shared folder, which stays, and the record of past installs |
| `packages\RELEASES` | The application's index of its own packages. **Its shortcut reads this file to decide which build to start**, and reads it without error handling — so removing it stops the application launching |
| `packages\.betaId` | The identifier deciding whether this computer gets that application's staged releases early |
| The package the index still names | The base the next update is applied as a patch against |
| A package for a newer build | Possibly an update part-way through downloading |
| `Update.exe` | The updater itself |
| Every `app-<version>` folder | A build. This row removes packages and staging, never a build |
| The application's own folder | Never a target |

Deguffer also refuses to delete through a link. If you have moved the staging folder onto another
drive with a junction, it removes nothing there and tells you why.

### What it costs you

Nothing. Every application still starts, still updates itself, and still applies its next update as
a patch rather than a whole download. The next install or update unpacks itself into the staging
folder again, exactly as it would have done.

If an application is installing or updating while you clean, Deguffer leaves what it is using alone
and says so.

### Why Tier 1

Every file here is one the updater itself was supposed to delete and did not. Nothing reads the
staging directories once an update has finished, and a package the application's own index has
stopped naming is one that application will never open again.

---

## Claude Code session leftovers

**Tier 1 — regenerable cache.** Pre-selected wherever there is something to remove.

| | |
| --- | --- |
| **Location** | Named folders inside `%USERPROFILE%\.claude`, or wherever `CLAUDE_CONFIG_DIR` points |
| **Method** | Delete what a session or an editor left behind, once whatever wrote it has ended |
| **Typical size** | Under 4 MB on the machine this was measured on, across more than a thousand entries |

### What it is

Claude Code, Anthropic's coding agent, keeps each session's working state in one folder, beside your
conversations, each project's memory, your settings and your sign-in. It clears old sessions out
itself after 30 days by default. Some of what a session leaves behind is outside that clean-up, and
the rest stays for the whole 30 days:

| Leftover | Where | What shows it is finished with |
| --- | --- | --- |
| An editor's handshake file | `ide\<port>.lock` | The editor process it names has ended |
| A messaging key | `sessions\<process>.<hash>.key` | The Claude Code process it names has ended |
| Spilled tool output | `projects\<project>\<session>\`, holding only `tool-results` | No project folder holds that session's conversation, and the session is not running |
| A hook environment | `session-env\<session>\` | The same |
| A shell capture | `shell-snapshots\snapshot-*.sh` | It was taken before every running session began |
| Unsent usage events | `telemetry\1p_failed_events.<session>.<id>.json` | The session it names is not running |

On the measured machine, 203 of 217 handshake files named an editor that had closed, the oldest three
months earlier, and each still held that editor's connection token. 428 environment folders belonged
to sessions that had ended, and every one of them was empty.

### What Deguffer does

**Nothing is offered on its name alone.**

- **A file that names a process** is offered once Deguffer has asked Windows about that process and
  found it has ended. A process it could not ask about keeps its file. A handshake file an editor
  wrote outside Windows, and a key written in another process namespace such as WSL, are never
  offered, because the id they record is not one this machine issued. Where a key records when its
  process started, a process id that has since passed to another program is told apart from the
  process itself.
- **Anything that names a session** is offered only where Claude Code's own list of running sessions
  (`sessions\<process>.json`) does not list it, and each entry in that list is checked against
  Windows too. If the list cannot be read, nothing that names a session is offered, and the row says
  so. The list is read again when you press Clean, immediately before each item is removed, so what a
  session you resume after the scan left behind stays.
- **Whether a session still has a conversation is asked of every project folder at once.** A session's
  folders can sit under a different project folder from its transcript. If any project folder cannot
  be listed, no session is called an orphan.
- **Nothing that names a session, or no process at all, is offered if Claude Code wrote it in the last
  7 days**, whatever the list says. A session can run for days, and an older version of Claude Code
  does not keep the list at all. A file that names a process needs no such wait, because that process
  has been asked about directly. Anything written after the scan read its evidence is kept when you
  press Clean, so a handshake file an editor started after the scan writes under the same port stays.
  A folder is dated
  by its own timestamp and its immediate contents, never by the files deep inside it: a file copied
  into place keeps the date of the file it was copied from.
- **A session folder holding anything besides spilled tool output**, such as a subagent's
  conversation, is left for you.

An empty folder frees no bytes, so the row counts what it removes in entries as well. A row made only
of empty leftovers says how many items it would remove rather than showing "0 B" and offering nothing.

§5.1 was asked, and the answer is no. Claude Code has no command that removes these alone: its own
purge removes a whole project's conversations and memory together.

### What is protected

| Neighbour | What it really is |
| --- | --- |
| `.claude` itself, and every folder at its top level | Your conversations, memory, settings, hooks and styles, and anything a later release adds. Nothing at that level is ever removed |
| `.credentials.json` | Your sign-in |
| `settings.json`, `CLAUDE.md` | Your settings and your own instructions |
| `projects\<project>\memory` | That project's memory, which Claude Code's own clean-up never removes either |
| `sessions\<process>.json` | The list of running sessions, which Deguffer reads and never removes |
| `%USERPROFILE%\.claude.json` | Claude Code's own configuration: your account, and each project's trust decisions |
| `%USERPROFILE%\.claude-swap-backup` | Not Claude Code's at all. Another program's saved sign-ins, under a name that begins the same way |

A conversation that still exists, and the output beside it, is never removed here. Whether to remove
a conversation is a choice about your own history, and this row never makes it.

### What it costs you

Nothing. Every conversation, its memory, your settings and your sign-in stay. A session that starts
afterwards writes its own handshake files, keys and shell capture exactly as before.

### Why Tier 1

Everything here was left by a process or a session that has ended, and nothing reads it again. An
editor that has closed never connects through its handshake file, and Claude Code itself treats a key
whose process has gone as unusable. Losing any of it loses nothing of yours: the usage events are
Anthropic's diagnostics, not your data.

---

## Superseded application versions

| **Location** | The `app-<version>` folders, other than the newest, inside each application Squirrel installed |
| **Method** | Delete the folders holding builds the application has replaced |
| **Typical size** | 719 MB for one application on the machine this was measured on, sitting beside the 722 MB it actually runs |

### What it is

A Squirrel application installs each version into a folder of its own — `app-3.6.3`, then `app-3.6.4`
beside it — and launches whichever is newest. Updating does not replace a folder; it adds one.

Squirrel does delete the old ones, but it deliberately keeps two. Its clean-up excludes both the
build it has just installed and the one that build replaced, so after an update a **full second copy
of the application** sits beside the one you use, until the update after next removes it.

### What Deguffer does

It offers every build except the newest, on the same rule the application's own shortcut uses to
decide which to launch: the highest version number wins.

**If any version folder carries a number Deguffer cannot order, that application gives up nothing at
all.** A pre-release version such as `2.0.0-beta1` sorts below its own release under one reading and
above it under another. Rather than choose, Deguffer stops: naming the wrong folder superseded would
remove the build you are running.

**An application that is running gives up nothing either.** This is a refusal, not a warning. The
question it answers is whether the application is running at all, not whether the old folder itself
is busy — the process holding it open runs from the build that replaced it. It is asked again when you
press Clean, immediately before each old build is removed, so an application you start after the scan
keeps its old builds.

### What is protected

| Neighbour | What it really is |
| --- | --- |
| The newest `app-<version>` | The build you are using |
| `Update.exe` | The updater, and the program many of these applications' shortcuts actually run |
| `packages` | The packages the application updates itself from, and its index of them |
| A version folder whose number could not be read | Deguffer cannot tell whether it is the one in use |
| The application's own folder | Never a target |

Your settings, your sign-ins and anything an application saved live somewhere else entirely — a
Squirrel application's data is not kept in its version folders.

### What it costs you

Every application still starts, and starts the version you are using now.

What you give up is the build it replaced. **There is no supported way back to it**: Squirrel has no
rollback, and its own documentation says so. You also give up one step the updater would have taken
for you. Before deleting an old build, Squirrel runs that build's own tidy-up hook, which is where an
application undoes what that version registered. Deguffer removes the folder without running it,
because starting a vendor's executable to tidy up after a deletion is not something this tool does.

### Why Tier 2, not Tier 1

Tier 1 means the tool re-creates what was removed. Nothing re-creates an old build: once that folder
is gone, that version is gone unless you can find its installer again. That alone rules Tier 1 out,
whatever the application's own updater intends to do with the folder later.

It is not Tier 3 either. These are not your files. They are a vendor's superseded program, which the
vendor's own updater treats as already uninstalled — its clean-up comment calls the old versions
dead — and which it deletes itself at the next update. So the row is offered, never selected for you,
and it asks for the extra acknowledgement Tier 2 carries.

---

## Dart analysis server cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\.dartServer` |
| **Method** | Delete `.analysis-driver` and `.pub-package-details-cache` only |
| **Typical size** | Grows without limit. 3.2 GB was measured on one workstation, all but 0.7 MB of it `.analysis-driver` |

### What it is

Every editor with Dart or Flutter support — VS Code, Android Studio, IntelliJ — runs the same
program behind the scenes: the Dart analysis server, which is what produces the errors, the
completions and the go-to-definition. Analysing a package from source is slow, so the server writes
a summary of each file it has read and reuses it the next time. That store is `.analysis-driver`,
and Dart's own performance guidance names the folder holding it when it tells you to exclude it from
real-time virus scanning.

Nothing trims it. It gains entries for every package you have ever opened, keeps them after the
project is gone, and so grows out of all proportion to the work in front of it.

`.pub-package-details-cache` is a much smaller companion: the package listing fetched from pub.dev
so that typing a dependency name can complete it.

### What Deguffer does

It deletes those two children and nothing else. There is no eviction command to prefer under §5.1 —
the Dart SDK ships none for this store, and the remedy in Dart's own issue tracker is deleting the
directory.

The folder is also never removed through a link. If you have redirected `%LOCALAPPDATA%\.dartServer`
onto another drive with a junction to keep 3 GB off a small system disk, Deguffer removes nothing
and tells you why: what the link points at is a folder it never looked inside.

### What is protected

The `.dartServer` folder itself, and the three children that are not caches. All five children are
dot-named directories sitting side by side, which is exactly the arrangement an over-broad rule gets
wrong while looking correct:

| Child | Why it stays |
| --- | --- |
| `.prompts` | Your own answers to the questions the server asks, so that it stops asking. A preference file, and nothing regenerates it. |
| `.plugin_manager` | State for the analyzer plugins the server loads. |
| `.instrumentation` | The server's instrumentation log and the identifier it is keyed to. |

Deguffer names all three explicitly and asserts they survived the run, the same treatment
`gradle.properties` gets. Anything else that turns up in there is unrecognised, so it is left alone
and Deguffer says so.

**An analysis server may be running.** One is started by whichever editor has a Dart or Flutter
project open, and it holds this store while it runs, so Deguffer warns you when it sees one. An
access-denied on a file the server is using is an ordinary outcome here, not a failure: the file is
skipped and the rest of the store still goes.

### What it costs you

One slow analysis pass, per project, the next time you open it. Errors, completion and navigation
are unavailable or incomplete until it finishes, and then everything behaves exactly as before.

### Why Tier 1

Nothing here originated with you. It is derived from Dart sources that are still on your disk, by an
analyser that is still installed, and the server rebuilds what it needs without being asked. The one
thing in the folder that *is* yours, `.prompts`, is never a candidate.

**Not to be confused with the pub cache**, which is a different folder holding downloaded packages,
and which Deguffer does not offer for an unrelated reason — see
[Dart/Flutter pub cache](#dartflutter-pub-cache--clean-uninstalls-your-global-tools) below.

---

## Roslyn solution index cache

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\Microsoft\VisualStudio\Roslyn\Cache` |
| **Method** | Delete a program's whole set of indexes, once everything in it is recognised |
| **Typical size** | Grows without limit. 538 MB was measured on one workstation across 19 sets, 280 MB of it in five sets not written to since 2024 or earlier |

### What it is

Roslyn is the engine behind C# and Visual Basic in Visual Studio and in the C# extension for VS Code.
When it opens a solution it builds an index of the code — what is declared where, and what refers to
it — and saves that in a small SQLite database, so that searching, navigating and finding references
work straight away the next time.

It keeps those databases per program. Each program's folder is named after the program and a
checksum of the full path it ran from, and inside it there is one folder per solution:

```
Cache\<program>-<checksum>\<solution>-<checksum>\sqlite3\v2\
    storage.ide, storage.ide-wal, storage.ide-shm, db.lock
```

So a program that runs from a new folder starts a new set. The C# extension for VS Code installs each
update into a folder named after its version, which makes every update do this, and nothing ever
removes the set left behind.

Microsoft documents neither this folder nor any way to clear it. The layout above is what was observed
on disk, and it matches Roslyn's own source code, which is where the folder names are built.

### What Deguffer does

It deletes a program's whole set, one row per set, each dated by the newest entry anywhere inside
it, folders included.
A set is deleted only when everything in it is exactly what Roslyn writes: solution folders named the
way Roslyn names them, each holding `sqlite3\v2`, and in that the database with its three companion
files. One file or folder that does not fit, anywhere in the set, and Deguffer leaves the whole set
alone and says so. Explore applies the same test before it removes a set.

There is no eviction command to prefer under §5.1. Neither Visual Studio nor Roslyn offers one.

**Keeping the set in use.** If you have told Deguffer to leave recently changed files alone, it leaves
each file changed within that time where it is. A set nothing has written to within that time still
goes whole, and the set in use keeps what it wrote recently. The index of a solution in that set that
you have not opened within that time still goes, and is rebuilt the next time you open it. Each row's
date shows which set is which.

Nothing is removed through a link. If `Roslyn` or its `Cache` folder is a junction onto another drive,
Deguffer removes nothing and tells you why.

### What is protected

`%LOCALAPPDATA%\Microsoft\VisualStudio` is never a target, because it holds far more than this cache:

| Child | Why it stays |
| --- | --- |
| The per-installation folders | Each Visual Studio installation's settings and extensions. |
| `SettingsBackup_*` | Earlier copies of your settings. |
| `BackupFiles` | Visual Studio's recovery copies of documents you had not saved. Losing them is permanent. |
| `Roslyn` and `Roslyn\Cache` | The folders the indexes are kept in. Only sets inside `Cache` go. |

After every run Deguffer asserts that `VisualStudio`, `BackupFiles`, `Roslyn` and `Cache` survived,
along with every set it left alone. Explore refuses the same folders, and every other child of
`VisualStudio`, because Deguffer does not recognise them.

**Visual Studio may be running.** It holds the databases of any solution it has open, and so does the
C# extension's language server, so Deguffer warns you when it sees either. An access-denied on a file
in use is an ordinary outcome here, not a failure: the file is skipped and the rest still goes.

### What it costs you

The next time you open a solution whose index went, Roslyn builds it again from the source. Searching,
navigating and finding references are slower or incomplete until that finishes, and then everything
behaves exactly as before. A set left behind by a program that no longer runs from that folder costs
nothing at all, because nothing will read it again.

### Why Tier 1

Nothing here originated with you. Every index is derived from source code still on your disk, by a
Roslyn that rebuilds it without being asked.

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

## Test browser profiles

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | Directly inside this account's temporary folder, ordinarily `%LOCALAPPDATA%\Temp`, and inside `C:\Windows\Temp` |
| **Method** | Delete each recognised profile folder, taking only what nothing has touched for the number of days set for temporary files — seven by default, and 0 means no age limit |
| **Typical size** | 6,750.9 MB across 1,862 profiles on one workstation, the oldest 70 days old |

### What it is

Each time a test starts a browser, Playwright and Puppeteer make a fresh profile for it in the
temporary folder and delete it when the browser closes. That deletion runs inside the test runner as
it exits. A test run that is stopped part way — a cancelled build, a debugger stopped mid-suite, a
crashed runner — never gets to it, and nothing else ever collects the profile. A machine that runs
browser tests every day collects them by the thousand.

This is a different location from [Playwright browsers](#playwright-browsers), which are the browser
builds themselves. `PLAYWRIGHT_BROWSERS_PATH` moves those and has no effect on these.

### What Deguffer does

It looks at the immediate children of each temporary folder and recognises a profile by its name
alone. Each tool builds the name and hands it to Node's `mkdtemp`, which adds exactly six random
letters and digits:

| Name | Written by |
| --- | --- |
| `playwright_chromiumdev_profile-XXXXXX` | Playwright, for Chromium |
| `playwright_firefoxdev_profile-XXXXXX` | Playwright, for Firefox |
| `playwright_webkitdev_profile-XXXXXX` | Playwright, for WebKit |
| `puppeteer_dev_chrome_profile-XXXXXX` | Puppeteer, for Chrome |
| `puppeteer_dev_firefox_profile-XXXXXX` | Puppeteer, for Firefox |

Anything else in the folder is left alone, including names that are nearly right: a different
number of characters after the hyphen, a capital letter at the start, or a browser name neither tool
uses. Playwright's `playwright-artifacts-XXXXXX` folders are not profiles, and are not offered here.
A link with a profile's name is named and never followed.

It does not need Playwright or Puppeteer to be installed. The name says which tool wrote the folder,
and the machine with abandoned profiles is often one where the tool has since been removed.

### What is protected

Each temporary folder itself, and every sibling named like a profile but not in either tool's
shape, are checked after the clean to prove they survived. Nothing else in the folder is touched.

A profile a running browser is using is left alone. A test browser runs from its own install folder
and works wherever the test runner does, so neither of those shows which profile it has open. Its
command line does: Playwright starts Chromium with `--user-data-dir=` and Firefox with `-profile`,
and Puppeteer uses `--user-data-dir=` and `--profile`, each naming the profile's path. Deguffer reads
the command line of every running program it may inspect, and leaves alone any profile one was
started with. It also leaves alone a profile a program is running from or working in.

The age limit is the other half. A test that is running now has a profile it wrote to moments ago,
so only profiles nothing has touched for the number of days set for temporary files are offered.
The profile folder's own times count as well as its files', because an empty profile has no files.
On the workstation measured, the default of seven days still offered 5,140.2 MB of the 6,750.9 MB.

A browser that runs as another account or as administrator cannot be inspected from an ordinary
Deguffer, so its profile is then protected by the age limit alone. With the limit set to 0 and no
guard on recently changed files, nothing protects it, and the row says so in a warning.

A running browser's profile is not checked after the clean, because its test runner deletes it as
soon as the browser closes. Its going is not something Deguffer did.

### What it costs you

Nothing. The next test run makes a new profile for every browser it starts, and nothing reads an old
one again. Playwright's WebKit is not given its profile at all unless a test asks for a persistent
one, so a WebKit profile is normally an empty folder. It is still offered, because removing the
leftover folder is the whole of what this row is for.

### Why Tier 1

A profile made for one launch holds nothing a later launch reads, and nothing of yours. There is no
command to prefer under §5.1, because the only cleanup the tools have is the exit hook that never ran.

The [Windows temporary folders](#windows-temporary-folders) row leaves every profile to this one,
so each is offered once, here.

---

## Azure Functions Core Tools releases

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\AzureFunctionsTools\Releases` |
| **Method** | Delete whole release directories (`4.18.1`, `3.40.0`, …) |
| **Typical size** | ~150 MB per release; 599 MB across four on the measured machine |

### What it is

The Azure Functions Core Tools are what runs a Functions project on your own machine. Visual Studio
and the Azure Functions extension do not use a copy you installed. They read a feed, work out which
release each version of the Functions runtime needs, and download it themselves into your profile.

Each release is a complete, self-contained copy: the `func` host under `cli_x64`, the project
templates, and one isolated worker runtime per .NET version it supports. A v4 release is around
140 MB and a v3 release around 210 MB.

**This is why the folder grows.** Nothing removes an old release. When the tooling decides it needs
a newer one it downloads it beside the last, so a machine that has followed several updates holds
every release it has ever fetched.

Beside `Releases` the tooling keeps two things it reads rather than downloads: `feed-v<sequence>.json`,
the cached feed telling it what is available and what it already has, and `Tags\v1` … `Tags\v4`,
each holding a `LastKnownGood-v<sequence>` file whose whole content is a release version. Those tag
files are the tooling saying, in its own words, which copy it will reach for.

### What Deguffer does

Within `Releases`, a child is removed only if its **whole name is three dotted numbers** — `4.18.1`,
`2.60.0`, `4.0.5455`. That is the shape the feed has always served. `v4`, `4`, `4.18`,
`4.18.1-backup` and anything you created yourself do not qualify: they stay in Tier 4 and Deguffer
tells you it is leaving them alone.

**Every release is offered, including the one the tooling still points at.** Deguffer cannot know
whether you still have a Functions v2 project, and withholding 166 MB on the chance that you might
would be Deguffer making your decision for you. What it does instead is tell you which is which:
each row carries the date its release arrived, and its description says whether the tooling's own
tag records still name it — "the release it uses for Functions v4", against "the tooling's own
records no longer name".

That description is read from `Tags` rather than worked out by comparing version numbers, because
the tooling keeps one release per runtime line and "the newest" is four different answers. If **any**
of those records cannot be read, every row says neither. "Nothing points at this release" is a claim
about all four records, so a partial reading cannot support it, and describing a release you still
use as superseded is a worse answer than saying nothing.

### What is protected

The tooling's folder, `Releases` itself, **`Tags`** and the cached **`feed-v<sequence>.json`**.
The last two are the subtle ones: both look like more cache, and both are how the tooling knows
which releases it already holds and which one each runtime line should use. Removing a release is
something the tooling recovers from by downloading it again. Removing the record of what it has
interferes with the part that decides.

### What it costs you

**A wait, the next time a project needs a release you removed.** Visual Studio downloads it again
before the project will start — a few minutes and a few hundred megabytes over the network.

Your function projects, their code and their settings are untouched. So are any releases you kept.

### Why Tier 2, not Tier 1

The same distinction Playwright's browsers make. A package cache refills itself the moment the tool
next needs it and you notice a slower build. A release does not refill itself so much as get fetched
again, on the tooling's schedule rather than yours, and the wait lands in the middle of opening a
project rather than in the background.

Deguffer uses no eviction command here because there is none. Neither Visual Studio nor the Core
Tools offers a way to remove a downloaded release, and the documented remedy for a bad download is
to delete the directory — so §5.1's preferred route does not exist, and §5.2's path-based one is
what is left.

---

## Graphics driver installer files

**Tier 2 — regenerable, with cost.** Offered but **never pre-selected**, and requires an
acknowledgement.

| | |
| --- | --- |
| **Location** | `C:\NVIDIA\DisplayDriver`, `%PROGRAMDATA%\NVIDIA Corporation\Downloader` and `C:\AMD`, where `C:` is the drive Windows is installed on |
| **Method** | Delete the recognised driver packages one by one, never the folders that hold them |
| **Typical size** | Not measured: none of these folders was on the machine this was researched on. An NVIDIA driver installer is about 900 MB today, and reports put `C:\AMD` at around 2 GB |

### What it is

A graphics driver installer unpacks the whole driver package onto the disk before it installs
anything. The driver it installs runs from the copy Windows keeps in its own driver store, so the
unpacked package has no part to play once the installation has finished. Older installers left it
behind, one folder per release:

| Folder | What wrote it |
| --- | --- |
| `C:\NVIDIA\DisplayDriver\<release>` | NVIDIA's driver installer, for releases up to 576.52. From 576.80 on it unpacks into a temporary folder and removes it |
| `%PROGRAMDATA%\NVIDIA Corporation\Downloader` | GeForce Experience, which kept the driver packages it downloaded |
| `C:\AMD\AMD-Software-Installer` | AMD's installer since 23.7.1. It removes the folder when it finishes, so one still here is from an installation that stopped part-way |
| `C:\AMD\<release folder>` | AMD's installers before 23.7.1, one folder per release, under names that were never consistent |

AMD's installer clears its older release folders itself from Adrenalin 24.1.1 on, "to save space"
in AMD's words, so AMD's own installer treats them as disposable.

### What Deguffer does

**It never lists the top of the drive.** `C:\NVIDIA` and `C:\AMD` sit beside `Program Files`,
`Users` and `Windows`, so Deguffer reaches each by name, checks every folder on the way down for
being a link, and lists only `DisplayDriver`, `Downloader` and `C:\AMD`. Inside each one it
recognises:

- in `DisplayDriver`, a folder named as NVIDIA names a release: three digits, a point and two
  digits, such as `546.33`;
- in `Downloader`, `latest` and folders named by a long hexadecimal identifier;
- in `C:\AMD`, `AMD-Software-Installer`, and any other folder holding AMD's display driver package
  (`Packages\Drivers\Display`), because the older release folders cannot be recognised by name.

Anything else is left alone, and Deguffer says so.

**A folder with nothing in it is not offered.** An installer that tidies up after itself removes
the files and can leave the folders that held them, and an AMD machine keeps `C:\AMD` for its
chipset driver alone. Neither is shown as something to reclaim.

**Each release is its own item**, so you can keep the one you might reinstall. None of these
vendors offers a command that removes its downloaded packages, so this is the path-based case §5.2
governs rather than §5.1's command.

### What is protected

| Neighbour | What it really is |
| --- | --- |
| `C:\AMD\Chipset_Software` | The chipset driver's install source. Windows Installer reads it to repair, upgrade or remove the chipset driver, and a reinstall fails with error 1308 once it has gone. Refused by name, whatever it holds |
| `C:\AMD\Chipset_Driver_Installer` | An older chipset driver's install source. Nothing establishes that it is safe to remove |
| `C:\AMD\Chipset_SoftwareLogs` | The chipset installer's logs |
| `C:\AMD\WU-CCC2` | What a driver Windows Update installed is removed through |
| `Downloader\config` and `status.json` | GeForce Experience's settings and its record of what it downloaded |
| The rest of `%PROGRAMDATA%\NVIDIA Corporation` | Including the NVIDIA app's own update store, `NVIDIA app\UpdateFramework`. It can run to tens of gigabytes, but somebody who cleared part of it reported games crashing afterwards, and nothing establishes which part is safe. Deguffer does not reach it |
| The rest of `C:\NVIDIA` | Including `PhysX`, where the PhysX installer unpacks itself |

Deguffer also refuses to delete through a link. If one of these folders is a junction to another
drive, it removes nothing there and tells you why.

### What it costs you

**The driver you are running is untouched.** To reinstall a release you removed, download its
installer again from the vendor. An older AMD Software installation may ask for its setup files if
you repair it, and the answer is the same.

### Why Tier 2, not Tier 1

The proposal for this location said Tier 1, because nothing installed depends on these files. That
is true, but Tier 1 also asks that whatever wrote the content re-creates it on demand, and nothing
re-creates an installer payload: the only thing that needs one again is reinstalling that release,
which means downloading several hundred megabytes. That is Tier 2's consequence. It also keeps a
location nobody has measured out of the default selection.

### Sources

- The extraction settings built into NVIDIA's driver installers from 341.96 to 581.80, read from the
  installers downloaded from `us.download.nvidia.com`, for the `DisplayDriver\<release>` layout and
  for the change at 576.80.
- NVIDIA's driver installation guide, for how the driver is removed, which does not use `C:\NVIDIA`:
  <https://docs.nvidia.com/datacenter/tesla/driver-installation-guide/windows.html>
- How-To Geek on GeForce Experience's `Downloader` folder, for what in it is a package and what is
  its own state:
  <https://www.howtogeek.com/342322/why-does-nvidia-store-gigabytes-of-installer-files-on-your-hard-drive/>
- AMD's release notes for Adrenalin 24.1.1, which say the installer "will now automatically clear
  previously installed drivers located in the C:\AMD folder to save space":
  <https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-24-1-1.html>
- The Guru3D thread for Adrenalin 23.7.1, on the fixed `AMD-Software-Installer` folder:
  <https://forums.guru3d.com/threads/amd-software-adrenalin-edition-23-7-1-driver-download-and-discussion.448586/page-2>
- A Ten Forums thread in which a chipset driver reinstall fails with error 1308 after
  `C:\AMD\Chipset_Software` was deleted:
  <https://www.tenforums.com/drivers-hardware/190784-amd-chipset-driver-3-10-22-706-wont-install-3.html>
- A Guru3D thread on what `C:\AMD` holds, including `WU-CCC2`:
  <https://forums.guru3d.com/threads/whats-in-the-c-amd-folder-and-do-i-need-it.402897/>
- An NVIDIA forum thread reporting crashes after files in the NVIDIA app's update store were
  removed:
  <https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/569266/grd-post-processing-folder-in-nividia-corpapp-fi/>

---

## Local copies of cloud files

**Tier 2 — regenerable, with cost.** Offered, **never pre-selected**, and confirmed with an
acknowledgement. Nothing is deleted, here or in the cloud.

| | |
| --- | --- |
| **Location** | The folder OneDrive keeps in step with the cloud, for each account and each SharePoint or Teams library its client syncs |
| **Method** | Mark each chosen file as not needed on this PC, through Windows' own Cloud Files API. OneDrive then releases its local copy |
| **Typical size** | Everything you have opened from OneDrive since it was last freed. On a machine syncing a large shared library, tens of gigabytes |

### What it is

OneDrive's Files On-Demand keeps every file listed on this PC, and downloads a file's contents the
first time you open it. From then on the file is **locally available**: its contents stay on the
disk as well as in the cloud, until something frees them. OneDrive's own *Free up space* command
does that one file or folder at a time. This row does it for everything that qualifies.

### What Deguffer does

It marks each file as **online-only** with `CfSetPinState`, the call Microsoft opens to any
application, and never with `CfDehydratePlaceholder`, which belongs to the sync app. OneDrive then
releases the local copy in the background. Each file stays in its folder, keeps its name, and opens
normally while the machine is online: opening it downloads it again.

**The figure is a request, not a result.** Windows documents that unpinning a file gives "no
guarantee" it is freed straight away, and on a test sync root unpinning alone freed nothing: the
sync app does the freeing.
So the preview shows what the files hold on this PC, and after a clean the result card shows it as
*Asked to release*, apart from *Removed by Deguffer*. The free space change beside them shows what
OneDrive had freed by then, which can be less.

**OneDrive must be running.** A request made while it is closed releases nothing. The row says so,
offers nothing, and asks you to start it and scan again. Deguffer asks again at the moment of the
clean.

### What is protected

Every one of these stays exactly as it is, and each rule is Deguffer's own, because Windows' call
refuses none of them:

- **Files you chose to always keep on this device**, and everything inside a folder you chose that
  for. Releasing one would undo that choice, and the only way back is a full download. A file inside
  a pinned folder carries no pin of its own, so Deguffer reads each folder above it.
- **Files with changes OneDrive has not uploaded yet.** A file is released only when OneDrive has
  marked it as matching the cloud and it holds no bytes the cloud lacks.
- **Files excluded from sync**, which the cloud may not hold.
- **Ordinary files and links inside the folder.** Only a file Windows reports as a cloud file is
  touched, and a junction or symbolic link is never followed.
- **Anything changed inside your guard window**, if you set one.
- **Sync apps Deguffer does not recognise.** Nextcloud and Proton Drive use the same mechanism, and
  are added once a real install has shown how each registers. Until then their folders are named in
  the row and left alone.

Each file is judged again at the moment of the clean, through the handle that marks it. A file that
gained an edit or a pinned folder while the preview was on screen is left alone. After the clean,
Deguffer checks that every file it named is still there and still a cloud file (§5.6).

### What it costs you

A released file downloads again the next time you open it, which costs time and bandwidth. **While
offline, a released file will not open until you reconnect.** Nothing is lost.

### Why Tier 2

§3's Tier 2 is "re-created only by re-downloading". That is exactly what opening a released file
does, and offline it is a file you cannot open. So the row is never pre-selected, and is confirmed on
its own.

---

## Per-project build output

**Tier 1 for .NET intermediate output, Tier 2 for the rest.** The only thing Deguffer looks for
inside your own folders, and the only thing it will not look for anywhere else.

| Directory | What it belongs to | Tier |
| --- | --- | --- |
| `obj` | .NET intermediate build output | 1 |
| `Library` | A Unity project's imported assets and caches | 2 |
| `Intermediate` | An Unreal project's intermediate build files | 2 |
| `DerivedDataCache` | An Unreal project's own derived data cache | 2 |
| `target` | Rust build output | 2 |
| `node_modules` | Installed Node.js dependencies | 2 |
| `.venv`, `venv` | A Python virtual environment | 2 |

### It only looks where you tell it to

Every other location in this document belongs to a tool, and Deguffer knows where to find it. These
are your project folders, and Deguffer has no idea where you keep them — so it does not guess.
**Settings → Source folders** is the whole of its permission: it searches inside the folders listed
there and nowhere else, and with the list empty these providers find nothing at all.

That holds even when finding something would be free. On a machine running as administrator,
Deguffer reads the volume's file table and already knows about every `node_modules` on the disk. It
still offers only the ones inside a folder you added. A cheap answer is not permission.

**The one folder Deguffer will not search is one you added on a cloud drive.** A cloud client that
shows its storage as an ordinary drive or folder looks exactly like a disk, and reading a folder on
it downloads every file inside onto this computer — the computer you are clearing space on. What a
clean would then free is space in the cloud rather than space here. So Settings warns you when you
add such a folder, and searches it only if you say to go ahead. A folder added before that warning
existed, or one you declined, is left alone and the scan says which folder it was. Add it again
to change your mind. The row in Settings says which of your folders carry that permission, and
removing the folder is how you take it back.

Approving a folder approves it for **every** kind of build output in the table above, which is what
the Settings description says. If you want a narrower scope, add narrower folders.

**A folder does not have to hold your own code.** The single largest thing this section can offer is
usually a Python virtual environment that nobody would think to declare: a machine-learning or
image-generation application installs into its own folder, builds an environment there holding a
CUDA-enabled `torch`, `tensorflow` and their neighbours, and never comes near your projects. One
measured at 7.3 GB, which is worth roughly ten of the environments a web project keeps. It meets
every condition below — `pyvenv.cfg` inside it, a dependency manifest beside it — and it is Tier 2
like any other. Deguffer simply never looks there, because you never said it could. Add the
application's own folder if you want that environment offered.

The model weights such an application downloads are a different matter, and are never offered: they
sit outside the environment, they are not regenerable from a manifest, and re-fetching them is not a
slow build but a download of tens of gigabytes.

### What Deguffer does

**A directory's name is not evidence.** `build`, `target` and `Library` are ordinary English words,
and a folder called one of them is as likely to hold somebody's exports as a compiler's output. So
recognition is never by name: it is by the project standing around the directory, and by the marker
the tool itself writes inside it.

| Directory | Must sit beside it | Must be inside it |
| --- | --- | --- |
| `obj` | A project file the restore manifest names | `project.assets.json`, and the generated NuGet imports for that same project |
| `Library` | `Assets`, `Packages`, `ProjectSettings` | — |
| `Intermediate` / `DerivedDataCache` | A file ending in `.uproject`, whose name is the project's own | — |
| `target` | `Cargo.toml` | `CACHEDIR.TAG`, which Cargo writes |
| `node_modules` | `package.json`, **and a lock file** | — |
| `.venv` / `venv` | A dependency manifest: `requirements.txt`, `pyproject.toml`, `Pipfile`, `setup.py` or `environment.yml` | `pyvenv.cfg` |

Every condition has to hold. A directory that fails any of them is left alone, and the scan says
how many were left and why. `obj` carries an extra check of its own: git is asked whether the
directory holds any tracked file, because intermediate output never should, and one that does is not
intermediate output whatever the manifest beside it claims.

Two of those requirements are there for a reason worth stating, because both look like fussiness and
neither is:

- **`node_modules` needs a lock file.** "Regenerable" is a claim that reinstalling gives you back
  what was there, and only a lock file makes that true. Without one, `npm install` re-resolves
  version ranges and can hand you a different dependency tree — which is a change to your project,
  not a regeneration of it. A `node_modules` with no lock file beside it is left alone.
- **A virtual environment needs a manifest.** With no record of what was installed into it, the
  environment is the only copy of its own contents, and removing it destroys information rather than
  freeing space. `pyvenv.cfg` proves the folder is an environment; the manifest proves it can be
  rebuilt. Neither alone is enough.

Nested directories belong to their parent. A `node_modules` inside another one, or a vendored crate's
`target` inside the outer `target`, is removed with the directory that contains it rather than
offered as a second row.

### A project you are using now is never offered

This is the part with no equivalent anywhere else in this document. Clearing a cache under a running
tool costs a slower next use. Removing a build directory under a live editor or a build in flight
breaks the work you are doing at that moment, and nothing re-downloads the afternoon.

So before anything is offered, Deguffer asks whether each project is in use, and holds back
the ones that are. It needs no administrator rights to ask. Three signals, each of them positive
evidence rather than a guess:

- **The tool's own lock file is held open.** Unity writes `Library\UnityLockfile` when the editor
  opens a project and removes it when the editor closes, and Windows will say which process is
  holding it. Whether the file *exists* is not the test — a crashed editor leaves one behind for ever
  — so what is asked is whether anything has it open.
- **A running program lives inside the directory.** An activated virtual environment runs
  `.venv\Scripts\python.exe`, and a Rust binary you started runs out of `target\debug`.
- **A running program is working inside the project.** A build in flight, a terminal sitting in the
  folder, or an editor with the solution open. Visual Studio's working directory is the solution's
  own folder, which is how a solution you have open is recognised.

A held-back project is listed as something left alone, with what is using it named, so you can close
it and scan again. The same question is asked again when you press Clean, immediately before each
directory is removed, so a project you open or build after the scan keeps its build directory, and the
result says what is using it.

**The Unreal Editor is found by its log.** It works in the engine's folder rather than the
project's, so the second and third signals do not see it. It does hold its log in the project's
`Saved\Logs` open for the whole session, and Deguffer asks about every log there, so a project the
editor has open is held back like any other. A switch on the editor's command line can move the log
out of the project, so a plan made while an Unreal editor, a commandlet or Unreal Build Tool runs
also says so by name. Explore does not find a project by its log, as it does not find a Unity
project by its lockfile.

**It can miss, and it never fires wrongly.** Three things it does not see, all of them stated here
because a safeguard whose limits are unwritten gets trusted past them:

- A compiler holding a file deep inside a tree that is neither its own program nor its working
  folder. Nothing can ask that question about a directory without administrator rights.
- **A program running as administrator, or as another user.** Deguffer runs unelevated, and Windows
  will not let it inspect those at all — so a build started from an elevated terminal is invisible.
- A project whose path is longer than 260 characters, for the lock-file signal only. Windows'
  Restart Manager refuses a path that long, so Unity's lockfile cannot be asked about there. The
  other two signals are unaffected, and the scan says the check could not run rather than
  reporting the project idle.

Where a check cannot run at all, the scan says so rather than staying quiet — "could not tell"
and "nothing is using it" are different answers, and only the second is permission. The age column
beside each row carries the rest of the decision: a project nobody has touched in a year is where the
space actually is.

### What is protected

Everything except the one directory in each row, asserted by name after the deletion runs:

- **The project folder itself** — only the build directory inside it is removed.
- **Every file the rebuild reads.** `Assets`, `Packages` and `ProjectSettings` for Unity;
  `Cargo.toml`; `package.json` and the lock file; the Python manifest; `pubspec.yaml` and
  `.dart_tool`. These are what make the directory regenerable, so losing one would falsify the whole
  claim.
- **For Unreal, the `.uproject` descriptor, `Source`, `Content`, `Config` and `Plugins`, and `Saved`
  and `Binaries`.** `Saved` holds the editor's autosaves, which are the only copy of work nobody
  saved, and your editor settings. `Binaries` is compiled output, but on a team an artist often
  receives the compiled game from source control and has no compiler to rebuild it, so on that
  machine it is the only copy. Neither is ever offered.
- **`bin`, for .NET, and `src` and `Cargo.lock` for Rust.** None of them identifies the directory
  being removed; each is named because it is what a rule reaching one level too far would take.
  `bin` in particular looks equivalent to `obj` and is not — it can hold hand-placed native
  dependencies and copied assets that no build reproduces — so it is out of scope entirely.
- **Every directory that was left alone**, whether because it could not be confirmed or because
  something is using it. Those sit in the same trees as the targets, separated only by evidence,
  which is exactly the situation where an over-broad rule takes one with the other.

### What it costs you

| Directory | The next use |
| --- | --- |
| `obj` | The next build regenerates it, so that build is slower. Nothing here is unique. |
| `Library` | Unity reimports every asset when you next open the project. On a large one that is tens of minutes, and packages it had downloaded are fetched again. |
| `Intermediate` | A C++ project's next build compiles its code from the start, and its Visual Studio solution shows the projects as unavailable until you choose **Generate Visual Studio project files** on the `.uproject`. A Blueprint-only project opens as before. |
| `DerivedDataCache` | Unreal compiles the project's shaders again the next time it opens, which on a large project can take tens of minutes. |
| `target` | The next build recompiles the project and every dependency, per profile — minutes to hours on a large workspace, and anything you built and are running from `target` goes with it. |
| `node_modules` | The project will not build or run until dependencies are installed again. The lock file pins the versions, so what comes back is what was there. |
| `.venv` / `venv` | The environment has to be created again and the install re-run from the manifest. **A manifest only lists what somebody wrote down** — check it covers what you had before removing an environment you still use. |

Where a package manager's own cache still holds the downloads, most of this is offline. Clearing that
cache in the same run is what makes it a download — so a run that takes both the npm cache and a
`node_modules` needs the network afterwards.

### Why `obj` is Tier 1 and the others are Tier 2

Tier 1 means the tool re-creates it on demand and you lose only time. That is `obj`: a missing one
makes the next build slower and nothing else.

The others cost more than a slower next use, and §3's definition of Tier 2 — "re-created, but
only by re-downloading gigabytes or re-indexing for minutes" — is met by each of them differently:

- **Unity** is the re-indexing case exactly. Nothing in `Library` is anyone's only copy; all of it is
  built from `Assets`, `Packages` and `ProjectSettings`, which is why every Unity `.gitignore`
  excludes it. What makes it Tier 2 is the reimport, plus the packages in `Library\PackageCache` that
  are fetched again.
- **Unreal's `Intermediate` and `DerivedDataCache`** are the same two cases in one project: a
  C++ recompile, and a shader compile. They are separate rows because they cost different things,
  and a row that took both would state one price for two.
- **Rust `target`** is Tier 2 rather than the Tier 1 it might look like, and the reason is worth
  being clear about: restoring it is not a slower build, it *is* the build. Every dependency is
  compiled from source, per profile and per feature set, which is where the five to twenty gigabytes
  came from. That is the same argument that puts vcpkg's binary cache in Tier 2 — a cache whose
  entries are recovered by compiling rather than by downloading — and it applies here with more
  force, because there is no cache to fall back on.
- **`node_modules` and a virtual environment** are the ordinary Tier 2 shape: a download, and a
  project that does not run until it finishes.

None of them is pre-selected, and each needs an acknowledgement before it runs.

### Not reached: `dist`, Dart's `build`, and Visual Studio's `.vs`

All three are per-project directories that look like obvious candidates and are not offered.

**`dist` has no evidence to go on.** It is produced by a build script on some projects and curated by
hand on others, and nothing inside it or beside it tells the two apart. The name is the only signal,
and §5.2 says an unrecognised thing stays in Tier 4.

**A Dart or Flutter `build` has the same problem, one step further along.** A `pubspec.yaml` and a
`.dart_tool` beside it prove a Dart package is there and that `pub get` has run — both facts about
the *parent*, neither about the directory. The toolchain does not always create a top-level `build`,
so a folder of that name beside a package can be somebody's own, and unlike Unity's `Library` there
is nothing inside it that only the toolchain writes: `.last_build_id` appears in some `build`
directories and not others, which was checked against real projects rather than assumed. Every other
directory here is recognised by something written inside it, or by a parent whose identity implies
the child. This one would be recognised by its name and its neighbours alone.

**`.vs` is not the small, disposable folder it appears to be.** It holds the IntelliSense database,
the design-time build cache and the file-content index, all of which are rebuilt on the next solution
open. It also holds `copilot-chat\sessions`, which is AI conversation history, `CopilotSnapshots`,
which is a record of file states, code-coverage data, and the `.suo` and window-layout files that
hold your open documents, breakpoints and expanded nodes. On the machine this was surveyed, roughly a
quarter of `.vs` by size was material of the second kind. That is §3's founding mistake — Visual
Studio Code's `workspaceStorage`, dominated by chat history — in a second costume, and it needs the
recognised-children rule applied *inside* `.vs` rather than a rule about `.vs` as a whole. It is
tracked as its own item rather than folded in here.

---

## Recycle Bin

**Tier 3 — user data.** Offered, **never pre-selected**, and confirmed by a dialog that says the
loss is permanent. Switch *Type a name to delete user data* on in Settings and that dialog asks you
to type the words out.

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
`$Recycle.Bin` folder that contains them. Your own folder is left standing and empty when Windows
does the emptying, and re-created on your next delete when Deguffer does it directly. Either way the
bin keeps working exactly as it did.

Each row carries the date that bin last changed, because that is what the decision turns on. A
drive you last deleted something on eight months ago is a different proposition from one you were
clearing out this morning, and the two are indistinguishable by size.

Drives that are not fixed are left out entirely. A network drive has no Recycle Bin — Windows
deletes across one outright — so a `$RECYCLE.BIN` sitting on a share belongs to the server's users
rather than to you. Removable media can be swapped between the scan and the clean, which would
put a plan you approved for one disk in front of another. A fixed drive that is not ready to be
read, which is unusual but possible, is skipped as well.

**Windows has a command for this, and Deguffer uses it.** §5.1 says to prefer a tool's own eviction
command, and `SHEmptyRecycleBin` is one: it empties the bin on a drive you name. Asking Windows
rather than deleting the files means Windows knows the bin changed, so a Recycle Bin window you
already had open, the desktop icon and anything else watching all agree with the disk straight away.

The scan is unaffected. The call takes a *drive*, and Deguffer's plan still names the exact
folder on each drive, how large it is and when you last used it, and still checks afterwards that
everything it promised to keep is still there. Those are two different paths, and only the second is
what you are shown.

**The reason to doubt it was §5.2, and it was measured rather than assumed.** A command that names a
drive and no account looks, from its shape alone, like exactly the too-broad rule that section
refuses. On a scratch drive carrying this account's bin, a second account's bin beside it, and a
folder that was not an account identifier at all, the call removed this account's entries and left
all three of the others exactly as they were. That test ran with administrator rights, which is the
case worth stating: a token that may delete anything did not widen what Windows chose to delete.
Deguffer still asserts the survivors on every run, so the answer is proved again each time rather
than resting on that one measurement.

**It is slower than deleting the files, which is why the other route is still here.** Against a bin
holding 1,000 deleted files the call took between 4 and 6 seconds where removing the same files took
under a fifth of a second; at 3,000 files the two were 60 seconds and under a second. The gap widens
the more the bin holds, so it is worst on the bin most worth emptying. **Empty Recycle Bins without
Windows**, on the Settings page, takes the fast side instead and gives up the notification, so a
Recycle Bin window left open may keep showing the old contents until you refresh it. That is a stale
picture rather than a stale deletion. The same folder is emptied either way, and other accounts'
bins are untouched by both.

**Leaving recently changed files alone always uses the direct route.** Windows empties a bin whole
and offers no way to hold anything back, so a plan made under that setting removes the files itself
whatever the setting above says, and tells you it is doing so.

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

So it is never pre-selected, and it is the first location in Deguffer whose confirmation says the
loss is permanent rather than costly. Switch *Type a name to delete user data* on and it asks you to
type the name out as well.

---

## Windows File History

**Tier 3 — user data.** Offered, **never pre-selected**, and confirmed by a dialog that says the
loss is permanent. Switch *Type a name to delete user data* on in Settings and that dialog asks you
to type the words out.

| | |
| --- | --- |
| **Location** | The drive File History is set to save to, under `FileHistory\<account>\<machine>\Data` |
| **Method** | Run Windows' own `FhManagew.exe -cleanup <days>`. Nothing on the drive is ever deleted by Deguffer |
| **Typical size** | Whatever has accumulated. File History's shipped retention is *never delete*, so these targets routinely reach tens of gigabytes |

### What it is

File History saves a copy of every file in your protected folders each time it changes, onto a
drive you chose. Each copy is a **version**: what that file looked like at one moment. Going back
to how a document was three months ago is what the feature exists for.

**Its default retention is "never delete".** Windows' own `FH_RETENTION_TYPES` documents
`FH_RETENTION_DISABLED` as the default — "previous versions are never deleted from the backup
target" — so the drive fills up because that is what it was configured to do, not because anything
went wrong. That is why a File History drive is frequently the largest thing on a machine that
nothing else accounts for.

File History is **not** a deprecated feature. It appears on neither Microsoft's deprecated-features
nor removed-features list. Its configuration *API* is deprecated, which is a different thing, and
Backup and Restore (Windows 7) is a different feature again.

### What Deguffer does

It runs the command Microsoft ships for exactly this job:

```
FhManagew.exe -cleanup <days> -quiet
```

**Deguffer deletes nothing on the backup drive itself, and that is the whole design.** The command
is documented to remove a version only when *both* of two conditions hold:

- the version is older than the age given, **and**
- the file is no longer in the protection scope, **or** a newer version of it is already on the
  drive.

The second condition is what guarantees the last remaining copy of a file you are still protecting
survives. **That guarantee belongs to the command**, and no rule Deguffer could write about folders
would reproduce it. §5.2 rules out the folders independently: the layout of a File History target
is documented nowhere by Microsoft, so every folder inside it is unrecognised and therefore Tier 4.

**How the drive is found.** Windows uses exactly one backup target at a time, and a machine that has
changed drives keeps a complete, stale `FileHistory` folder on the old one. Deguffer reads the File
History settings in your own profile to learn which drive is in use, rather than trimming whichever
folder it finds first. If those settings name no drive it can reach, the row says so and offers
nothing. It does not guess.

**The age is yours to set.** *Keep File History versions for*, on the Settings page, defaults to
**365 days** — which is `FH_RETENTION_AGE`'s own documented default, so an untouched install asks
Windows for what it would have done had a retention policy been switched on. The minimum is 1 day,
and that is a safety floor rather than a tidy number: `-cleanup 0` keeps only the newest version of
files *currently in the protection scope*, which silently discards every version of everything you
have since moved, renamed or deleted. Deguffer will not ask for it.

### About the size shown

**It is a ceiling, not a forecast, and the row says "about".** `FhManagew.exe` reports nothing
before it runs, so there is no way to ask Windows what a cleanup would free. What Deguffer shows
instead is its own measurement of the *first* of the two conditions above: this machine's saved
versions older than the age you set. The second condition can only take away from that, never add to
it, so the real reclaim is usually smaller — Windows keeps the newest copy of every file it is still
protecting, however old that copy is. Nothing else on the drive is counted.

The figure that is not a forecast is the one measured afterwards. Deguffer measures that same folder
before and after the command and reports the difference.

**A drive holding nothing old enough reads as "Nothing old enough", never "Already clear".** Those
are different claims, and only one of them would be true of a full drive whose versions are all
recent.

### What is protected

These are named on every plan, and the run checks afterwards that each is still there:

- **Every other account's File History.** A backup drive is routinely shared, and another person's
  history sits beside yours under a folder named for their account.
- **Your own File History of every other machine.** One drive holds one folder per machine you back
  up, and this run covers only the machine it is running on.
- **The catalogue**, in `Configuration` beside `Data`. It is what makes the saved versions
  restorable. Removing it would leave every version on the drive, intact and unreachable, and no
  comparison of sizes would show that had happened.
- **Your File History settings** in your own profile, which record what is protected and where it is
  saved.

Everything else on the drive is untouched as well — a File History target is very often an ordinary
external disk with the rest of your files on it — but Deguffer names the folders above rather than
the whole drive, so those are the ones the check covers.

**What the check proves, exactly.** The command is Windows' own, so what it reaches is Windows'
decision rather than Deguffer's, and §5.6 is the answer to that. After the run, each folder above
must still be there, and each one that held something must still hold something somewhere inside
it: a file, a link, or a folder Deguffer is not allowed to open. A cleanup that removed another
account's backup is reported as a failure, and so is one that left the folders standing and took
everything out of them. The folders that lead down to this machine's saved versions are the
exception to the second half, because Windows was sent there: they may end the run holding nothing
when every version in them was old enough to go.

**What it cannot prove.** A cleanup that trimmed another account's old versions the way it trims
yours would leave that account's newest versions and its catalogue behind. The folder still holds
files, so the check passes. Only a comparison of sizes could see it, and the size of a folder
Deguffer does not own changes between a scan and a clean for ordinary reasons.

The emptied-in-place half also depends on what your account may read. A folder Deguffer is not
allowed to open is never recorded as holding anything, so emptying it is never reported. A folder
inside it that Deguffer may not open counts as something still there, however much went from around
it. Both only ever keep an alarm from being raised: neither can report a failure that did not
happen.

### What it costs you

**Older versions of your files stop existing.** A version is a snapshot of a file as it was, so
nothing can regenerate one — the state it captured exists nowhere else once it goes. If you might
want to go back to how a document was before the cut-off date, do not clean this.

What does not change: File History keeps running exactly as before, the newest copy of everything it
protects stays on the drive, and the files on your own machine are untouched.

### Why Tier 3, and not Tier 2

Tier 2 means regenerable at a cost — re-downloaded, or rebuilt. **A superseded version of a file is
not regenerable at any cost.** No command, download or rebuild recreates the way a spreadsheet
looked in March. That test settles it, and it is why this is offered but never selected for you, and
why its confirmation says the loss is permanent rather than costly.

---

## Crash dumps and error reports

**Tier 3 — user data.** Offered, **never pre-selected**, and confirmed by a dialog that says the
loss is permanent. Switch *Type a name to delete user data* on in Settings and that dialog asks you
to type the words out.

| | |
| --- | --- |
| **Location** | `%LOCALAPPDATA%\CrashDumps`, `%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive` and `ReportQueue`, `C:\Windows\LiveKernelReports`, `C:\Windows\Minidump`, and `C:\Windows\MEMORY.DMP` |
| **Method** | Delete each of those six, one row each |
| **Typical size** | Usually tens of megabytes. `MEMORY.DMP` is the exception: on a machine set to write a complete dump it is the size of your installed memory, so one stop error leaves 32 or 64 GB behind |

### What it is

When a program crashes, Windows writes down what it was doing at the time. There are three kinds
here and they come from different places:

- **`CrashDumps`** in your own profile holds dumps of ordinary applications that stopped working.
- **`WER`** — Windows Error Reporting — holds the reports Windows prepares to send to Microsoft.
  `ReportQueue` is what has not been sent yet, and `ReportArchive` is the record of what has.
- **`Minidump`, `LiveKernelReports` and `MEMORY.DMP`** are the kernel's own. A minidump is written
  for each blue screen; a live kernel report is written when a driver was reset without stopping the
  machine; and `MEMORY.DMP` is the full dump from the most recent stop error.

### What Deguffer does

It removes the six locations above, one row each, so you can clear the application dumps and keep
the kernel ones — or the reverse.

**`C:\Windows` itself is never listed and never touched.** This is the strictest rule in Deguffer,
and it works differently from every other location in this document. Elsewhere Deguffer looks inside
a folder and decides what each thing in it is. Here it does not look inside at all: it holds a list
of exact paths, and nothing else under the Windows directory is reachable, whatever it is called.
`WinSxS` and `Windows\Installer` — the two large folders Deguffer refuses to go near, and the two
that break Windows if you get them wrong — are named as things that must still be there when the run
finishes, and Deguffer checks that they are.

**Most of it needs administrator rights.** Only `%LOCALAPPDATA%\CrashDumps` is yours to clear.
Deguffer shows the rest either way, tells you which they are, and leaves them unticked until you
restart it as administrator with the **Elevate** button. It does not hide them, because a folder you
are never told about is one you can never decide about.

Each row carries the date something last wrote to it. For `MEMORY.DMP` that date is the moment the
machine stopped.

### What is protected

`C:\Windows`, `%PROGRAMDATA%`, your profile's local application data, and everything Deguffer never
named — which is everything else in those folders. `WinSxS`, `Windows\Installer` and
`%PROGRAMDATA%\Package Cache` are checked explicitly afterwards.

Deguffer also refuses to delete through a link, at any level. If one of these folders, or any folder
on the way down to it, has been redirected elsewhere, it removes nothing there and says so.

### What it costs you

**The record of every crash on this list stops existing.** If you are in the middle of a bug report,
or somebody has asked you for a dump, this is the copy — nothing re-creates it, and no undo exists at
any level.

Nothing that is running is affected, and Windows keeps writing new dumps exactly as before.

### Why Tier 3

A crash dump is a record of something that already happened, and the crash will not happen again to
order. That is the whole of the argument. Tier 1 means "whatever produced it makes it again on
demand, so nothing is lost", and nothing here meets that: what Windows re-creates is the *next*
dump, never the ones you removed.

It is worth being blunt about this because the obvious reading is the other one. These folders are
full of files nobody has looked at, in a location that sounds like scratch space, and every disk
cleaner treats them as disposable. They usually are. But "usually disposable" and "regenerable" are
not the same claim, and the tier model exists to keep them apart.

---

## Windows servicing logs

**Tier 3 — user data.** Offered, **never pre-selected**, and confirmed by a dialog that says the
loss is permanent. Switch *Type a name to delete user data* on in Settings and that dialog asks you
to type the words out.

| | |
| --- | --- |
| **Location** | `C:\Windows\Logs\CBS`, `C:\Windows\Logs\WindowsUpdate`, `C:\Windows\Panther`, and `C:\Windows\System32\LogFiles\WMI\RtBackup`; and the logs a reset leaves in `C:\$SysReset\Logs`, `C:\$SysReset\OldOSLogs` and `C:\Windows\Logs\PBR` |
| **Method** | Delete each of the first four, one row each. The reset logs are one row, cleared by Windows' own *System recovery log files* cleanup |
| **Typical size** | 64 MB was measured on one workstation. Machines with a long update history are regularly reported in the gigabytes |

### What it is

The trail Windows leaves while maintaining itself.

| Folder | What wrote it |
| --- | --- |
| `Logs\CBS` | Component servicing — what Windows added, removed or repaired. `sfc /scannow` writes here too |
| `Logs\WindowsUpdate` | Windows Update's own trace files |
| `Panther` | Setup logs, from the original installation and from every in-place upgrade since |
| `System32\LogFiles\WMI\RtBackup` | Backup trace files for the event sessions the WMI service runs |
| `$SysReset\Logs`, `$SysReset\OldOSLogs`, `Logs\PBR` | Reset this PC, and the recovery environment's reset and refresh |

### What Deguffer does

It removes the first four, one row each. The reset logs are different, because Windows has a
cleanup of its own for them: Disk Cleanup's *System recovery log files*. Its registration names no
folder, but the handler behind it names these three, so Deguffer asks Windows to run that cleanup
rather than deleting them itself (§5.1). Where Windows does not offer that cleanup, the reset logs
are left where they are. `C:\Windows`, `C:\Windows\Logs` and the three folders above
`RtBackup` are all left standing — Deguffer takes the folder it named, never the one holding it, and
it never lists the Windows directory to find out what else is in there. The rule and the protections
are the same ones described under **Crash dumps and error reports** above.

All four need administrator rights, so on an ordinary run every row is shown, sized, and left
unticked with the reason on it.

**Windows holds some of these files open, and that is normal.** The WMI service keeps its current
trace files locked, and the servicing stack keeps whatever log it is writing. Anything held open is
left exactly where it is, so reclaiming less than the size shown is the expected result here rather
than a failure.

### What is protected

The Windows directory, every folder passed through on the way down, `WinSxS` and
`Windows\Installer` — all of those are named in the plan and checked after the run. For the reset
logs, so are `C:\$SysReset` itself and what Windows keeps at the top of the drive: see
[Previous Windows installation](#previous-windows-installation) below.

Everything else in there is protected differently, and the difference is worth knowing: it is never
reached, rather than reached and then spared. Deguffer holds a list of exact paths and never asks
what else is in the folder, so a folder it does not name is not something it decided to keep — it is
something it never looked at.

### What it costs you

**You lose the history of what this machine has already done to itself.** The next update writes a
fresh log, so nothing stops working — but if an update failed and you wanted to find out why, the
answer was in these files.

That is the case worth pausing on. Each row shows when something last wrote to it, taken from the
newest file inside rather than the folder's own date, so a log being written right now reads as
minutes old rather than months.

### Why Tier 3, and not Tier 1

The same reasoning as the crash dumps, and it is the less obvious of the two. Every guide on the
internet treats these as free space, and most of the time they are. But a log is a record of an
operation that has finished, the operation does not run again on request, and what Windows re-creates
is the next log rather than the ones that went. That is Tier 3's definition and not Tier 1's, so
Deguffer offers them without ticking them, and confirms that the loss is permanent before it
acts.

---

## Previous Windows installation

**Tier 2 — regenerable with cost.** Offered, **never pre-selected**.

| | |
| --- | --- |
| **Location** | `C:\Windows.old`, `C:\$Windows.~BT`, and `C:\$Windows.~WS` with `C:\ESD\Windows` and `C:\ESD\Download` |
| **Method** | Windows' own Disk Cleanup handlers — *Previous Installations*, *Temporary Setup Files* and *Windows ESD installation files* — one row each |
| **Typical size** | `Windows.old` is an entire Windows installation: tens of gigabytes is normal. The other two range from almost nothing to several gigabytes |

### What it is

Upgrading Windows moves the previous installation aside into `Windows.old`, so the upgrade can be
undone from Settings, and leaves Setup's working folder (`$Windows.~BT`) and the installation files
it downloaded (`$Windows.~WS` and `ESD`) beside it. Windows keeps all of it for the *uninstall
window*, ten days after the upgrade unless that has been changed, and then removes it by itself.
Sometimes it does not, and then it stays until somebody notices.

### What Deguffer does

It asks Windows to run its own cleanup for each of them, the same one Disk Cleanup's *Clean up
system files* runs. It never deletes these folders itself. Windows registers the previous-installation
cleanup to remove the uninstall record as well, which is what Settings offers to go back from, and a
folder deleted by hand would leave that record behind, naming something that is gone.

Immediately before Windows is asked, Deguffer looks inside the folders once more. An Outlook data
file, a file changed since the preview that your recent-files setting would keep, a subfolder Windows
will not let Deguffer look inside, or an update that has started since, and nothing is removed.

A folder is offered only when all of these are true:

- **The upgrade can no longer be undone.** Neither the folder nor anything directly inside it has
  been written for longer than the uninstall window, which Deguffer reads from the machine rather than assuming ten days. Until then
  the row says how many days are left.
- **No update is unfinished.** No restart is owed for an update, neither the servicing stack nor
  Windows Setup is running, and no restart is due to move a file inside the folder. While any of
  that is true the row reads *Update in progress*.
- **Windows' own cleanup says it has something to clear there.** Deguffer asks it first, the way
  Disk Cleanup does, and a folder it does not count as its own is left standing. Windows answers
  that question only for a program running as administrator, so until Deguffer is elevated the row
  is shown as needing it.

Running the cleanup needs administrator rights. An Outlook data file inside any of these folders
stops the row: Windows clears the folder whole and cannot be told to leave one file.

### What is protected

The top of the drive is never listed. Deguffer holds a list of exact names, so a folder you keep
there is never a candidate. What Windows keeps there — `Windows`, `Program Files`,
`Program Files (x86)`, `ProgramData`, `Users`, `Recovery` and `$Recycle.Bin` — is named in the plan
and checked after the run, and so is the `ESD` folder.

### What it costs you

**You can no longer go back to the previous version of Windows**, and anything an upgrade left
behind in the previous installation goes with it. If a file from before the upgrade is missing,
look in `Windows.old\Users` first. Windows describes the downloaded installation files as needed to
reset this PC, so a reset afterwards needs Windows downloaded again or installation media. Windows
itself keeps working exactly as it is.

### Why Tier 2

Nothing here comes back except by upgrading again, and what you pay is a named capability — going
back, and resetting from local files — rather than a slower next use. That is Tier 2's shape. It is
not Tier 3, because none of it is a record of something you did.

---

## Leftover Windows update folders

**Tier 2 — regenerable with cost.** Offered, **never pre-selected**.

| | |
| --- | --- |
| **Location** | `C:\$WinREAgent` and `C:\$GetCurrent` |
| **Method** | Delete each of the two, whole or not at all, one row each |
| **Typical size** | `$WinREAgent` measured 1.7 GB on one workstation, left by the most recent cumulative update. `$GetCurrent` is usually small |

### What it is

- **`$WinREAgent`** is where an update stages its work on the recovery environment: a new recovery
  image, a backup of the old one, and the state it would roll back to.
- **`$GetCurrent`** is the update assistant's working folder: its logs and the files it downloaded.

Neither is cleared afterwards.

### This one is Deguffer's judgement, not Microsoft's

**Microsoft documents neither folder, and no Windows cleanup names either of them.** Every disk
cleaner deletes them, and nothing first-party says that is safe. So Deguffer offers them on narrow
terms, and the row says that the judgement is Deguffer's own:

- **Nothing inside has been created or written for 30 days.** Whether the update that wrote the
  rollback state has finished cannot be asked of Windows without administrator rights, so age stands
  in for it. The copy on the audited machine was nine days old, and this keeps exactly that back.
- **No update is unfinished**, on the same three tests as the previous installation above.
- **Each folder goes whole or not at all.** A rollback manifest means nothing without the image it
  restores, so a folder with anything recent inside it is held back whole rather than having its
  older half removed. The clean looks again before removing it: a file written since the preview, a
  subfolder Windows will not let Deguffer look inside, or an update that has started since, and
  nothing in the folder is removed.

Removing them needs administrator rights.

### What is protected

The same as the previous installation above: the top of the drive is never listed, and what Windows
keeps there is checked after the run.

### What it costs you

The files the last update staged for the recovery environment, and the rollback state beside them,
are gone, as are the update assistant's logs and downloads. Windows creates the folders again the
next time it needs them.

### Why Tier 2, and why `$SysReset` is not here

The folders come back only when Windows next services itself, which is Tier 2's shape. `$SysReset`
is the third member of the family, but Windows does have a cleanup for its logs, so it is cleared
through that and listed under [Windows servicing logs](#windows-servicing-logs) with the other logs.

---

## Windows temporary folders

**Tier 2 — regenerable, with cost.** Offered, **never pre-selected**, and it needs an extra
acknowledgement before it runs.

| | |
| --- | --- |
| **Location** | This account's own temporary folder, ordinarily `%LOCALAPPDATA%\Temp`, plus `C:\Windows\Temp` |
| **Method** | Empty each folder in place, taking only what nothing has touched for seven days — a setting, and 0 means no age limit |
| **Typical size** | 5.4 GB across the two on one workstation. It grows without limit, so a machine that has never been cleaned holds more |

### What it is

Where every program on the machine puts something it means to throw away. Installers unpack their
payload here, compilers write intermediate output, browsers spool downloads, test runners build
scratch trees. Each of them is supposed to clear up afterwards. A great many never do, usually
because they were killed, crashed, or simply never had the code.

There are two of these folders and they belong to different people:

| Folder | Who writes it |
| --- | --- |
| `%LOCALAPPDATA%\Temp` | Everything running as you |
| `C:\Windows\Temp` | Windows itself, and the services running under system accounts |

**The account's folder can be moved, and Deguffer follows it rather than assuming.** `%TMP%` and
`%TEMP%` are separate settings that are allowed to disagree, and Windows hands a program the first
of the two that is set — so a machine configured that way has a second temporary folder that
ordinary tools never look at. Deguffer resolves both, and the default location as well, and offers
each distinct folder once.

### What Deguffer does

It empties each folder and leaves the folder itself. That is not a nicety: every program on the
machine expects `%TEMP%` to exist, and Windows does not put it back once it is gone, so a profile
whose temporary folder had been deleted would start failing installers for a reason nobody would
connect to a disk cleaner.

Two rules decide what comes out, and both of them hold back more than a plain "empty it" would.

- **Nothing touched in the last seven days.** A temporary folder holds live working files among
  abandoned ones and they look identical — same folder, same kind of name, often the same size.
  Age is the only thing that separates them. Seven days is the interval Windows' own Disk Cleanup
  and Storage Sense apply to these two folders, so an untouched Deguffer offers what the machine
  would have removed on its own rather than inventing a threshold of its own.

  **You can change it, and 0 means no age limit at all.** Settings has *Only offer temporary files
  older than*. Raising it is uneventful. Setting it to 0 is the one value on that page that removes
  a safety rule rather than adjusting one: everything in both folders is then offered however
  recently it was written, including the file a program wrote a second ago and still expects to
  find. The two protections below it survive — Windows will not release a file something holds
  open, and an entry a running program is working in is left alone whatever the setting says — but
  neither covers the program that wrote a file, closed it, and wants it back, which is precisely
  what the age filter was for. The row says so on a warning whenever 0 is in force.
- **Nothing a running program is working in.** Before it plans anything, Deguffer reads the process
  table once and asks which entries of each folder something is running from or working inside. Any
  entry that answers is left alone whatever its age, is named on its row so you can see what is
  holding it, and is checked afterwards to prove it is still there. This catches the case the age
  filter cannot: a program that has been running for a month, working in a scratch directory whose
  files are all older than the cut-off.

**An entry a tool's own row offers is left to that row.** Where Deguffer knows what wrote an entry
of a temporary folder — Node's compile cache, a Roslyn session, VS Code's downloaded update, NuGet's
scratch folder — the row for that tool offers it, under that tool's rules, and this row leaves it
out and says which rows have it. So each entry is counted once, and an entry that row would keep is
not taken here for being a week old: a Roslyn session still in use, or Blender's `quit.blend`. See
[Tool caches in temporary folders](#tool-caches-in-temporary-folders) and the two sections after it.

The size shown already has all of those taken out of it, so the number the scan reports is what the
clean will actually take — with the one exception described next.

**A file Windows will not release is left where it is, and the next scan stops offering it.**
Two kinds of refusal reach a temporary folder. A program that still has a file open releases it
when the program closes. Windows can also refuse for reasons no amount of waiting changes: an access
rule, or security software guarding a folder. On one workstation, something below the access rules
— most likely security software protecting browser data — refused every file in the browser
profiles that automated test runs had left in `%TEMP%`: nearly two thousand folders and 5.9 GB. It
refused an administrator too.

Deguffer cannot know about a refusal before it has tried, so the first scan offers those files.
The clean then reports what it could not take, with its size and which kind of refusal it was, and
records where it happened. Every later scan asks Windows again about those places only, leaves
out whatever is still refused, and says so on the row; a row with nothing else to offer reads
*Refused by Windows* rather than *Already clear*. If the refusal lifts, the next scan offers the
files again, and every clean tries them again.

One refusal stays invisible to a scan: a running program's own files. Windows lets Deguffer
open a running executable, or a library a running program has loaded, as though it could delete
it, and refuses only the deletion itself. The clean reports those files as refused, and the scan
goes on offering them until the program exits.

This is not specific to temporary folders. Every location Deguffer empties or deletes itself
behaves the same way.

**These rows show no date, deliberately.** Every other row states when its location was last written
to, which is how you tell a project built this morning from one abandoned last year. A temporary
folder is written to by everything on the machine, so its answer is always "moments ago" — beside an
offer that excludes everything newer than the cut-off, which is the opposite of what the row does. A
date that cannot be made to mean anything is left off rather than shown, because a date is what
invites you to delete something.

`C:\Windows\Temp` needs administrator rights. On an ordinary run it is shown, sized, and left
unticked with that reason on it.

### What is refused

A temporary folder is the one location Deguffer is *told* about rather than knowing, because
`%TEMP%` is an environment variable anything on the machine may have written. Four settings are
declined outright, with the reason shown on the row:

- One pointing at the root of a drive or a share, where emptying it would take the whole volume.
- One that *contains* a directory Windows is built out of — the profile, either program directory,
  the Windows directory, or the machine-wide application data.
- **One Deguffer does not recognise as a temporary folder at all.** This is the rule that does the
  work. Neither `C:\Users\<user>\Documents` nor `C:\Windows\System32` holds anything structural, so
  the test above passes them both — and a row labelled "Temporary files" would then delete every
  file in them older than a week. What Deguffer recognises is a folder called `Temp` or `Tmp`, or
  one sitting directly inside one: the default location, a `D:\Temp` you chose, and the numbered
  per-session folders a Remote Desktop host hands out. A scratch folder called something else is
  left alone and says so. That is deliberate rather than a limitation — Deguffer will not empty a
  folder on the strength of a setting alone.
- **One that nests with a folder already being offered.** Two temporary folders where one sits
  inside the other is the pairing that destroys a live `%TEMP%`: emptying the outer one deletes the
  inner folder rather than clearing it, and Windows does not put it back. A Remote Desktop session
  host produces exactly that by default, so the folder your programs actually resolve to is the one
  kept.

A temporary folder that turns out to be a link to somewhere else is declined too, on the rule that
applies everywhere in Deguffer: it does not delete through a link, because what is on the far side
was never classified.

`C:\Windows\Temp` is declared before any of your account's settings are read, so a `%TEMP%`
pointing into it is offered once, with the administrator rights it needs, rather than twice.

### What is protected

Both folders themselves, the folder holding each of them, every entry a running program is using,
and — for `C:\Windows\Temp` — the Windows directory, `WinSxS` and `Windows\Installer`. All of them
are named in the plan and checked after the run.

The check on a spared entry is stronger than "is it still there". An over-broad rule here would
leave every folder standing and empty one it promised not to, which no test of existence can see, so
Deguffer records whether each spared entry held anything before the run and reports it as a failure
if it is standing empty afterwards.

### What it costs you

Almost always nothing, at the default. Everything offered was abandoned more than a week ago by a
program that has finished with it, and nothing is running out of it.

The exception is a program that treats a temporary folder as storage rather than as scratch and
still expects to find something there — an installer keeping resume state between reboots, a crash
reporter holding a report you have not sent. That is rare and it is bad practice, but it happens,
and it is the reason this is not Tier 1.

**At an age limit of 0 the cost is a different one**, and it is worth stating separately rather than
as a footnote to the paragraph above: what you lose is whatever any program on the machine was
part-way through. The age filter is the only thing that distinguishes that from rubbish, so with it
switched off there is nothing left to make the distinction with.

### Why Tier 2, and not Tier 1

Tier 1 promises that nothing is lost, because whatever produced the content re-creates it on demand.
Nothing re-creates a temporary file. It is what a program left behind, and the program has gone.

What makes this safe to offer is the age filter and the live-program check, and neither of those is
a claim that the content is regenerable — they are a claim that it is *abandoned*, which is a
different and weaker thing. The practical difference between the two tiers is whether the row is
ticked before you have read it, and on the folder the spec calls the classic mistake it should not
be.

### Why not Disk Cleanup or Storage Sense

Windows does ship a route to both folders, and Deguffer does not use it. Selecting Disk Cleanup's
handler means writing settings into your registry on your behalf, and the run then reports nothing
back — no list of what it took, no size, and nothing to check afterwards. Storage Sense is a setting
rather than a command, and switching it on is not Deguffer's to do. If you would rather Windows did
this on a schedule, Storage Sense is the right answer and Settings is where to turn it on.

---

## Tool caches in temporary folders

**Tier 1 — regenerable cache.** Offered and pre-selected.

| | |
| --- | --- |
| **Location** | Named entries in this account's temporary folders: `node-compile-cache`, `flutter_tools.<number>`, `dart_test.<number>`, `mozilla-temp-files`, and the session folders under `Roslyn` and `VBCSCompiler`. Also the folder `NODE_COMPILE_CACHE` names, where that is set |
| **Method** | Delete what each tool's own name identifies, under the live check that tool allows |
| **Typical size** | About 2 GB on one workstation, most of it Node's compile cache |

### What it is

A temporary folder belongs to nobody, so nothing in it can be attributed by where it sits. On the
workstation this was measured on, a 1.39 GB folder with a random name held Visual Studio's staged
update, and a 7.27 GB one was a running program's live scratch. An age rule on its own would have
offered both. What makes an entry here offerable is a name that only one tool writes, taken from
that tool's own source:

| Entry | What wrote it |
| --- | --- |
| `node-compile-cache` | Node.js, when a program switches on its module compile cache. It has no size limit and nothing ever removes old entries, so it grows with every Node version used. It measured 1.5 GB across about 237,000 files |
| `flutter_tools.<number>` | The Flutter tool, for one run. It removes the folder when it exits, and leaves it when the run is killed |
| `dart_test.<number>` | Dart's test runner, on the same terms |
| `mozilla-temp-files` | Firefox, for media, printing and the clipboard. Firefox deletes the folder itself once it has been idle for a while |
| A 32-character folder under `Roslyn` or `VBCSCompiler` | Roslyn, the compiler behind C# and Visual Basic, for one editing or build session: copies of the analyzers it loaded, so the originals are not locked |

### What Deguffer does

It removes only entries these names identify, and leaves everything else in the temporary folder to
the *Temporary files* row. Each tool gets the live check its own design allows, and none of them
depends on age:

- **Roslyn says which sessions are alive, and Deguffer asks it the same way.** Roslyn holds a mutex
  named after each session folder for as long as the session runs, and when it starts it deletes
  the folders whose mutex has gone. Deguffer removes exactly those folders and no others, so the two
  cannot disagree.
- **A Flutter or Dart test folder is left alone while Dart is running.** Its name is a random number,
  so nothing ties a folder to the run using it. The only safe answer is that no run is going on.
- **Firefox's folder is left alone while Firefox is running**, or Thunderbird, LibreWolf,
  Waterfox, Floorp, Zen, Pale Moon, SeaMonkey or Basilisk, which are built on the same code.
- **Node's cache needs no check.** Node reads a cache file whole when it loads a module and writes new
  entries only when it exits. A file that has gone is a cache miss, and the module is compiled again.

Any recognised folder a running program is working inside is left alone as well, whatever its tool.

`NODE_COMPILE_CACHE` moves Node's cache anywhere. Where it is set, Deguffer treats that folder as
Node's and removes only the per-version folders Node makes inside it, named for the Node version,
the architecture and a code-cache tag. Anything else in the folder stays, so a setting that points
at the wrong folder costs nothing. A setting that names a drive root, or a folder holding a
temporary folder or one Windows is built out of, is declined outright, and the row says so: other
rows remove things there, and this row would otherwise count each of those removals as a failure.

### What is protected

The temporary folder itself. `Roslyn` and `VBCSCompiler`, their `AnalyzerAssemblyLoader` and
`AnalyzerPathResolver` folders, Roslyn's shared analyzer cache, and anything in them that is not a
session folder. A session that is still open. The folder `NODE_COMPILE_CACHE` names, and everything
in it that is not a per-version folder. Deguffer does not delete through a link, so a junction named
like one of these entries is left alone.

### What it costs you

Nothing is lost. Node compiles the modules it loads again, a little more slowly the first time.
Roslyn copies the analyzers it needs for the next session.

### Why Tier 1

Node's documentation says to clean its cache by removing the directory, and that it is recreated
the next time it is used. A Flutter or test-runner folder belongs to a run that has ended. Firefox
removes its own folder. A Roslyn session's copies are of analyzers that are still where they were
copied from.

### Sources

- [Node.js: module compile cache](https://nodejs.org/api/module.html#module-compile-cache)
- [Flutter tool: the temporary directory it makes for each run](https://github.com/flutter/flutter/blob/master/packages/flutter_tools/lib/src/base/file_system.dart)
- [Firefox: the anonymous temporary files folder](https://github.com/mozilla-firefox/firefox/blob/main/xpcom/io/nsAnonymousTemporaryFile.cpp)
- [Roslyn: shadow copies and the session mutex](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/DiagnosticAnalyzer/ShadowCopyAnalyzerPathResolver.cs)

---

## Installer downloads in temporary folders

**Tier 2 — regenerable, with cost.** Offered, **never pre-selected**, and it needs an extra
acknowledgement before it runs.

| | |
| --- | --- |
| **Location** | Named entries in this account's temporary folders: VS Code's updater folder, `DockerDesktopUpdates`, the packages the Visual Studio Installer stages, and Blender's session folders |
| **Method** | Delete what each application's own name identifies, only while that application is not running |
| **Typical size** | 1.9 GB on one workstation, most of it Visual Studio's staged Windows SDK |

### What it is

Applications that update themselves download the new installer into the temporary folder and keep
it there:

| Entry | What wrote it |
| --- | --- |
| `vscode-stable-user-x64` and the other forms of that name | VS Code's updater: the downloaded installer, and the files it uses to apply an update when VS Code restarts |
| `DockerDesktopUpdates` | Docker Desktop's updater, when it downloads updates in the background |
| The packages in the Visual Studio Installer's staging folder | The Visual Studio Installer. It downloads packages into a randomly named folder and keeps them for the next install or update |
| `blender_<letter><five digits>` | Blender, for one session. It removes the folder on exit, and leaves it when Blender is killed |

The Visual Studio Installer's folder has a random name that cannot be told from a thousand others.
Deguffer finds it the only reliable way: each installed Visual Studio records it, as
`temporaryCache`, in a state file under `%LOCALAPPDATA%\Microsoft\VisualStudio\Packages\_Instances`.
Deguffer follows that only to a folder directly inside one of your temporary folders, and removes
only the packages in it, named for the package and a 20-digit code.

### What Deguffer does

Everything here waits for its application to close. None of these names ties a folder to the
process using it, and VS Code applies a downloaded update from its folder when it restarts, so
deleting while it runs can change what the update does. Docker does not publish its update
behaviour, so it gets the same treatment. The Visual Studio Installer waits for its installer and
its background downloader. A folder any running program is working inside is left alone as well.

**Blender's recovery files are recognised so that nothing takes them.** `quit.blend` and the
autosave files sit loose in the temporary folder, and hold work that may never have been saved
anywhere else. Before this row existed, the *Temporary files* row would take them once they were a
week old. This row names them, keeps them, and checks afterwards that they are still there.

### What is protected

The temporary folder itself. The Visual Studio Installer's staging folder, and anything in it that
is not a staged package. `quit.blend`, and every Blender autosave. Anything an application is using.
Among VS Code's other folders, `vscode-typescript` is the TypeScript server's working folder while
VS Code runs, and it does not match.

### What it costs you

The next update downloads what it needs again. For Visual Studio that can be more than a gigabyte:
the installer keeps these packages so that its next update does not have to download them.

A Blender session folder costs something different. A bake made for a file that was never saved is
written into the session folder. If you recover that file later, the bake is gone and has to be
made again.

### Why Tier 2, not Tier 1

Nothing re-creates an installer by itself. The application downloads it again when it next needs it,
which is Tier 2's consequence exactly. The Visual Studio Installer keeps its packages on purpose, and
the Blender case is a bake that has to be made again.

### Sources

- [VS Code: where the updater downloads](https://github.com/microsoft/vscode/blob/main/src/vs/platform/update/electron-main/updateService.win32.ts)
- [Docker Desktop: downloading updates in the background](https://docs.docker.com/desktop/settings-and-maintenance/settings/)
- [Visual Studio: downloading updates](https://learn.microsoft.com/en-us/visualstudio/install/update-visual-studio)
- [Blender: the session folder and its recovery files](https://github.com/blender/blender/blob/main/source/blender/blenkernel/intern/appdir.cc)

---

## Tool logs in temporary folders

**Tier 3 — user data.** Offered, **never pre-selected**, and the confirmation says the loss is
permanent.

| | |
| --- | --- |
| **Location** | `DiagOutputDir\RdClientAutoTrace` and `DiagOutputDir\Windows365\Logs`, `servicehub\logs`, and `vscode-inno-updater-<time>.log`, in this account's temporary folders |
| **Method** | Empty each log folder in place, and delete the updater's log files |
| **Typical size** | 569 MB of Remote Desktop traces on one workstation. One report puts ServiceHub's logs past 6 GB |

### What it is

| Entry | What wrote it |
| --- | --- |
| `DiagOutputDir\RdClientAutoTrace` | The Remote Desktop client, which records traces of every connection automatically in case one has to be diagnosed |
| `DiagOutputDir\Windows365\Logs` | The Windows App, on the same terms |
| `servicehub\logs` | ServiceHub, the services behind Visual Studio and the C# tooling in VS Code |
| `vscode-inno-updater-<time>.log` | The helper VS Code's updater runs to replace its files |

None of these is read back by anything. Microsoft's troubleshooting pages send a person to them by
hand, and nothing limits how much they keep.

### What Deguffer does

It empties the folders the logs are written into and leaves the folders, because the tools write
into them again. `DiagOutputDir` is shared by more than one Microsoft client, so only the two log
folders Deguffer knows inside it are emptied, and anything else in it stays. No application has to
be closed: a log that is still being written is held open, Windows refuses to delete it, and it
stays.

### What is protected

The temporary folder itself. `DiagOutputDir`, `servicehub`, and the log folders inside them.
Everything in `DiagOutputDir` and `servicehub` that is not one of those log folders.

### What it costs you

The record of past connections, services and updates. Nothing depends on it, but it is what support
asks for after something has gone wrong, and it cannot be had again.

### Why Tier 3

A log is a record, and nothing rebuilds it. This project treats every log that way, and these are no
different for sitting in a temporary folder.

### Sources

- [Remote Desktop client: where its traces are](https://learn.microsoft.com/en-us/previous-versions/remote-desktop-client/troubleshoot-client-windows)
- [Windows App: collecting its logs](https://learn.microsoft.com/en-us/windows-app/troubleshoot-collect-logs)
- [Visual Studio: log collection, including ServiceHub's](https://devblogs.microsoft.com/setup/visual-studio-and-net-log-collection-utility/)

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

### Other names in the temporary folder — not identified well enough, or live

The three rows for named tools in temporary folders cover what could be tied to one tool by its
own source. These were looked at and left to the *Temporary files* row, which offers only what is a
week old and not in use:

- **`VSTelem` and `VSTelem.Out`.** Visual Studio's responsiveness monitoring creates them, but nothing
  published says what they hold or whether anything reads them back. They were empty when measured.
- **`.net` and `_MEI<number>`.** Where .NET single-file applications and PyInstaller applications
  unpack themselves. A running application loads its code from there, and the owning program can be
  anything.
- **`pip-<kind>-<random>`, `pub_<number>`, `MSBuildTemp<…>`.** Each tool's own name shape, but nothing
  ties a folder to the run using it, and pip keeps some on purpose when asked to.
- **`chocolatey` and `WinGet`.** Package-manager download caches. §5.1 prefers their own commands, and
  WinGet's folder holds logs beside its downloads.
- **Anything in `C:\Windows\Temp`.** The rows for named tools look in this account's temporary
  folders only. Services running as the system write to the machine's folder, and the live checks
  answer for this account: a Roslyn session a service holds would read as ended from here.

### Dart/Flutter pub cache — `clean` uninstalls your global tools

This is not the Dart analysis server's byte store, which *is* offered — see
[Dart analysis server cache](#dart-analysis-server-cache) above. Different folder, different
contents, different answer.

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

### The Visual Studio installer's package caches — no way to clear them safely

Two directories, both large, both holding the installation packages the Visual Studio installer has
downloaded, and both left alone:

| Location | Measured | What it holds |
| --- | ---: | --- |
| `C:\ProgramData\Microsoft\VisualStudio\Packages` | 7.7 GB | A manifest and a payload for every component of every product the installer has put on the machine |
| `C:\ProgramData\Package Cache` | 6.7 GB | The same idea for products installed as a bundle — Visual Studio, the Visual C++ redistributables, the .NET SDKs |

The first is the largest single location no provider reaches, and on the machine where it was
measured it was larger than everything the shipped providers found there put together. Deguffer still
does not offer it, for three reasons that all point the same way.

**There is no command that clears it.** `vs_installer.exe --nocache`, and the
`KeepDownloadedPayloads` policy behind it, are the routes Microsoft documents — and neither of them
frees any space when it runs. They tell the installer to stop keeping payloads, and the existing ones
go during the *next* install, modify or repair of the product they belong to. That operation is long,
needs administrator rights, and is something the user has to want for its own sake. Deguffer's whole
promise is a scan and then a number, and there is no honest number to show for a step that
reclaims nothing.

**The folder cannot be split into "safe" and "unsafe" children.** The measured machine had 1,249 of
them, one per payload, named by component and version, and different on any other machine. No
allow-list can be written, so §5.2 puts every one of them in Tier 4. Nor is the folder uniformly
disposable: `_Instances` holds each installed product's own record of what it is made of, sitting
directly beside the payloads. That is the same trap as `gradle.properties` next to `.gradle\caches`,
which is what §5.2 exists to catch.

**Losing it costs a repair you cannot do offline.** With a network, the installer downloads what it
needs and nothing is lost. Without one, a repair or a change to Visual Studio cannot proceed — which
is precisely why the sibling `Package Cache` was excluded from the start.

So Deguffer reports these and never offers them. Explore refuses to remove anything under
`C:\ProgramData`, and hovering either folder says what it is and what clearing it costs, which is the
question a size picture actually raises. If you want the space back, the supported route is the
installer's own `--nocache` switch, run before the next repair or modify.

`InstallCleanup.exe` turns up in search results for this and is not an answer. Microsoft documents it
as a last resort after a repair or an uninstall has already failed, and warns that it can remove
features belonging to other products.

### .NET workload packs — the command that clears them cannot say what it would free

`%PROGRAMFILES%\dotnet\packs` holds the workload packs: the Android, iOS and MacCatalyst SDKs, and
the Mono and NativeAOT runtime packs those platforms run on. A full set is kept per SDK feature band
and per workload manifest version, and an SDK update adds a set rather than replacing one.

It measured **4,599.5 MB** on the audited machine, out of 8,604.4 MB for
`%PROGRAMFILES%\dotnet` as a whole. Classifying every pack against the four installed feature bands
put **1,881.9 MB of it — 41% —** on bands (`8.0.100` and `9.0.100`) with no installed SDK, or on
superseded same-band workload SDK packs. The rest of the folder is in use, and part of it is not
workload packs at all: the reference assemblies every .NET project compiles against
(`Microsoft.NETCore.App.Ref` and its siblings) sit in the same directory and arrive with the SDK.

`dotnet workload clean` is the right command for it, and it is the right *shape* of command.
Microsoft documents it as removing "workload components that might have been left behind from
previous updates and uninstallations", and it is reference-counted rather than path-matched: it drops
a pack's installer dependency reference when that pack's `"SDK feature band … does not match any
installed feature bands"`, and removes the pack only once nothing refers to it any more. That is what
§5.1 asks for, and the packs are Tier 2 — regenerable, but only by a workload re-install that is a
large download.

**It is not offered, for two reasons, and the first is disqualifying on its own.**

**There is no read-only estimate.** The command takes `--all` and `--help` and nothing else. There is
no `--dry-run`, no `--whatif`, and no listing mode, in the CLI or in the documentation. Deguffer's
promise is a number shown before the act and a §5.6 check against a predicted set afterwards, and
neither is available here. Where a provider's method is a command rather than a path, it has a way
to find the number first: `dotnet nuget locals --list` names the directories to measure, and the
conda provider's figure is conda's own dry run.

**On a machine with Visual Studio the reclaim may be zero.** `dotnet workload list` reports an
installation source of `VS <version>` for workloads Visual Studio installed, and Visual Studio holds
the installer dependency references on their packs — so the count never falls to zero and nothing is
removed. The command says so itself rather than failing: it prints *"Workload '…' was not removed
because it is installed and managed by Visual Studio"*, and the code that prints it is commented as
existing "to increase user awareness that they must uninstall via VS". Deguffer would have shown a
1.88 GB row and delivered nothing.

A read-only mode on `dotnet workload clean`, or any documented way to enumerate what it would remove,
is what would change this. Until then the folder is reported in Explore and never offered — the same
treatment as the installer caches above.

### Superseded SDK versions — large, version-partitioned, and still an uninstall

Three separate finds, one verdict. Each keeps one directory per version, each was measured in the
gigabytes, and in each case §5.2 looks satisfiable because the versions are cleanly partitioned. All
three are **Tier 4** anyway, because removing a version is an uninstall and §2 rules those out:
deleting the folder leaves the installer's records claiming the version is still present.

| Location | Measured | Supported route |
| --- | ---: | --- |
| `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA` | 3,130.1 MB in one superseded version | The toolkit's own entries in Settings, under Apps |
| `%PROGRAMFILES(X86)%\Windows Kits\10` | 1,691.9 MB for one superseded version | Each version's own "Windows Software Development Kit" entry, under Apps |
| `%PROGRAMFILES%\dotnet\sdk` and `shared` | 1,660.9 MB reachable | Each version's own entry under Apps |

The last of those has a tool behind it, and it was considered properly. **`dotnet-core-uninstall`**
reaches that 1,660.9 MB of superseded within-band SDKs and runtime patches, and unlike
`dotnet workload clean` it *does* have a dry run. It is still declined, for three reasons: it is an
uninstaller, which §2 rules out; it is not installed and is a separate download, so Deguffer would be
recommending an install in order to enable a delete; and its own documentation says it can only
remove components installed by particular routes — "the tool might not be able to uninstall all of
the .NET SDKs and runtimes on your machine". A provider whose method may silently apply to only part
of what it listed is not one that can make Deguffer's promise.

All three are reported in Explore. The CUDA toolkit and the Windows SDK carry an entry of their own,
and `%PROGRAMFILES%\dotnet` carries one the map answers with from anywhere inside it, so hovering
says what the folder is and which uninstaller owns it.

### Docker — freeing space inside the disk image does not free it on disk

`docker system prune` reclaims space *inside* `docker_data.vhdx`, while the host file stays exactly
the same size. Reporting one number would be actively misleading. This needs the two figures
reported separately, and the second cannot be measured from the filesystem — it comes from the
container tool's own accounting. See §5.4.

### Outlook's offline mailbox (`.ost`) — the vendor's own route stops short on purpose

An `.ost` is Outlook's local copy of an Exchange, Microsoft 365 or Outlook.com mailbox, kept in
`%LOCALAPPDATA%\Microsoft\Outlook` by default. It is routinely the largest single file on a business
machine: Microsoft's [Cached Exchange Mode planning guide][ost-plan] gives **50 GB** as the default
maximum, and says local files are *"50 percent to 80 percent larger than the mailbox size reported in
Exchange Server"*. Advice to delete it is everywhere, because Outlook downloads it again.

Deguffer never offers it, no route removes one, and Explore refuses it. The reasons, in the order
that decides it:

**1. §5.2 disposes of it before tiering does.** The `.ost` is not a recognised child of a cache
directory. It is the tool's primary data file, sitting in the tool's own folder beside `RoamCache`,
`Offline Address Books` and, on older versions of Outlook, `.pst` files. A rule permitting
`Outlook\*.ost` has already conceded the shape §5.2 exists to forbid.

**2. The vendor's own eviction is a shrink, and it deliberately stops short.** The route is the
*Mail to keep offline* setting, renamed *Download email for the past* in later versions. Lowering it
makes Outlook [*"do a local-only deletion of excess data that's cached in the OST
files"*][ost-subset], and Microsoft states that the setting does not affect the Calendar, Contacts,
Tasks, Journal, Notes or **Outbox** folders. Deleting the file is not a larger version of that route.
It is the operation the vendor declined to build. Deguffer cannot move the setting for you, and it
will not write `SyncWindowSetting` into your profile: the same line it draws at selecting Disk
Cleanup's handlers on your behalf — see
[Why not Disk Cleanup or Storage Sense](#why-not-disk-cleanup-or-storage-sense).

**3. Microsoft documents content inside the `.ost` that exists nowhere else.** Of the Sync Issues
folder, [the article on retention policies][ost-sync] says *"the folder is a client-side folder
only"* and *"The contents of the Sync Issues folder aren't copied to your server, and you can't view
the items in the Sync Issues folder from any other computer."* Of its `Conflicts`, `Local Failures`
and `Server Failures` subfolders it warns: *"Automatically deleting this content could result in data
loss."* `Local Failures` is where an item that failed to upload goes.

**4. The one sentence authorising deletion is conditional, and the condition cannot be checked.**
[Repair Outlook Data Files][ost-repair] says *"If you're using an Exchange email account, you can
delete the offline Outlook Data File (.ost) and Outlook will recreate the offline Outlook Data File
(.ost) the next time you open Outlook."* Deguffer cannot establish that the account still exists,
that the mailbox is reachable, that you have not been offboarded, that the Outbox is empty, or that
`Local Failures` is. Those are questions for Outlook's own messaging interface, and a disk-cleaning
tool that opens a mail store to answer them has become something else.

**5. Tier 3 is not an escape hatch.** Tier 3 still shows the row, and a 40 GB row is the most
attractive thing on the screen. This is the one location investigated where a single confirmation
could permanently destroy a message you believe you sent, on a machine where you can no longer reach
the mailbox.

**What would change the answer**, stated so the refusal can be tested against it: a vendor-supported,
non-interactive command that both evicts and compacts without writing to your profile; an
authoritative statement that the store is a complete mirror of the server with no client-only
folders, which the Sync Issues article directly contradicts; or a cheap, read-only way to prove the
account is live and both folders are empty. None of these is close.

If you want the space back, *Mail to keep offline* makes the copy smaller, and *Compact Now* in the
data file's own settings gives the freed space back to the disk.

### Outlook data files (`.pst`) — excluded by name, because no path finds them

A `.pst` is not a copy of anything. Microsoft's [introduction to Outlook data files][pst-intro] says
that for a POP or IMAP account *"all of your Outlook information is stored in an Outlook data file,
also known as a Personal Storage Table (.pst) file"*. It is also what an archive is written to, and
where items moved off a mail server to keep the mailbox small end up. New ones are saved in
`Documents\Outlook Files` by default, and in `%LOCALAPPDATA%\Microsoft\Outlook` on older versions.

**It is on an explicit exclusion list, not merely absent from an inclusion list.** No provider
targets one, and that is not enough. A `.pst` is frequently several gigabytes, which is exactly what
puts it in front of somebody in a size picture, and it can be saved anywhere — a data disk, a folder
you named, a share, a temporary folder an archive was opened from — so no list of paths can find
every one. Deguffer therefore asks one question of every file, by every route it removes anything
by: is it an `.ost` or a `.pst`? If it is, it stays. The question is asked of the name whatever else
the file is: a OneDrive placeholder or a deduplicated file carries the same mark Windows puts on a
link, and removing one removes its content, so a store carrying that mark stays too — and so does a
real link named like one, which costs nothing.

In Explore that is a refusal by type rather than by place:

| Refused | Why |
| --- | --- |
| Any file ending in `.pst` or `.ost`, on any drive | The mail store itself, wherever it was saved |
| `%LOCALAPPDATA%\Microsoft\Outlook`, and everything in it | Outlook's own folder: removing it would take the stores inside with it |
| Any folder named `Outlook Files`, and everything in it | Outlook's default folder for data files, found by name because OneDrive and folder redirection move Documents |

A name that only resembles one — `archive.pst.txt`, `archive.pstx`, a folder called
`Outlook Files backup` — is ordinary. A folder of your own that holds a `.pst`, `Documents` included,
is not refused, because a folder is not a store — but the store inside it still stays. Moving the
folder to the Recycle Bin is refused, naming the store, because Windows moves a folder whole.
Removing it permanently takes everything else, leaves the store and the folders holding it, and
checks afterwards that the store is still there. Hovering any of these says what it is, and
*Compact Now* is the supported way to make one smaller.

**The Storage page never removes one either.** A row whose removal Deguffer performs itself steps
over a store and leaves the folders holding it standing — the *Temporary files* row, a build
directory, a cache folder — and its figure already leaves the store out. A row whose removal is not
Deguffer's to steer is withheld while a store is inside its reach:

| Route | With a store inside |
| --- | --- |
| A removal Deguffer performs itself | The store and the folders holding it stay, the rest goes, and the figure leaves the store out |
| A tool's own clean command (§5.1) | The command is not run, and the scan names the store: the tool cannot be told to leave one file |
| Windows' File History cleanup | Not run while a saved version of a store is on the backup drive, because it can remove the last saved copy of a store you have since deleted |
| A Recycle Bin | That bin is left exactly as it is. Windows empties a bin whole, and emptying around the store would take the record that lets a deleted folder holding it be restored |

Every store a scan finds is named in its notes, and every clean checks that it is still there
afterwards — on the rows the clean does not run as well as on the rows it does — so a clean that lost
one reports a verification failure. A scan runs minutes before the clean, so immediately before
a tool's command runs or a Recycle Bin is emptied, by either route, Deguffer looks on the disk again,
and does none of these where a store has arrived since. A row holding nothing but stores reads
*Outlook data kept*, never *Already clear*.

### Outlook's secure temporary folder — a Tier 1 candidate that needs measuring first

Outlook copies an attachment you open into a folder of its own, `Content.Outlook`, and this is the
one clean Tier 1 candidate in this area. Two things have to hold before any code:

- **Its path is read, never assumed.** Microsoft's [article on attachments left behind][olk-temp]
  says Outlook first reads `OutlookSecureTempFolder` under
  `HKEY_CURRENT_USER\Software\Microsoft\Office\<version>\Outlook\Security` (or the matching policy
  key), and creates a randomly named subfolder under the Temporary Internet Files directory only when
  that value is missing or invalid. That article covers Outlook 2003 to 2010, so the value's
  behaviour on current Outlook is not established from it.
- **It needs measuring on a real Outlook machine.** The accumulation that made the folder famous —
  attachments left behind when Outlook exited or crashed while they were open — is, in Microsoft's
  words, *"resolved in Microsoft Outlook 2010 Service Pack 1 (SP1) and in the Microsoft Office Outlook
  2007 hotfix package dated June 29, 2010."* Whether it still grows to a size worth a row is unknown.

### Office Document Cache — pending uploads, and a vendor warning against deleting it by hand

The Office Document Cache holds the files Office is uploading to a server, so that it can show how
each upload is progressing and whether any needs attention. It is not offered.

- **It can hold work that exists nowhere else.** From Version 2512, [Microsoft
  says][odc-size] Office keeps *"items that have pending changes to upload or items with upload
  errors for a maximum of 365 days"* by default. Before that version, [a file was
  removed][odc-settings] *"only when there are no changes pending upload"*.
- **The documented route moved, and it is interactive.** The Upload Center's *Delete cached files*
  was the old route, and [Microsoft states][odc-upload] that *"In Microsoft 365 apps, the Office
  Upload Center has been removed."* The current route is inside an Office application, under
  *File > Options > Save > Cache Settings > Delete cached files*, which asks separately before it
  deletes files with pending uploads.
- **Microsoft warns against exactly what a path-based cleaner would do.** *"Deleting the Office
  document cache programmatically or by manually navigating to user app data is not recommended.
  Doing this may lead to Office Client crashes and/or unrecoverable data loss."*

Nothing in this Outlook and Office area could be measured: no `.ost` or `.pst` existed on the machine
these notes were written against, and no Outlook profile had ever been created on it.

[ost-plan]: https://learn.microsoft.com/en-us/microsoft-365-apps/outlook/configuration/cached-exchange-mode
[ost-subset]: https://learn.microsoft.com/en-us/troubleshoot/outlook/user-interface/only-subset-items-synchronized
[ost-sync]: https://learn.microsoft.com/en-us/troubleshoot/exchange/compliance/mrm-not-process-sync-issues-folder
[ost-repair]: https://support.microsoft.com/en-us/office/repair-outlook-data-files-pst-and-ost-25663bc3-11ec-4412-86c4-60458afc5253
[pst-intro]: https://support.microsoft.com/en-us/office/introduction-to-outlook-data-files-pst-and-ost-222eaf92-a995-45d9-bde2-f331f60e2790
[olk-temp]: https://learn.microsoft.com/en-us/previous-versions/troubleshoot/outlook/attachments-issues-outlook
[odc-size]: https://support.microsoft.com/en-us/topic/managing-office-document-cache-size-ea64af72-b597-408e-8ecf-fd55daa02476
[odc-settings]: https://support.microsoft.com/en-us/office/office-document-cache-settings-4b497318-ae4f-4a99-be42-b242b2e8b692
[odc-upload]: https://support.microsoft.com/en-us/office/microsoft-office-upload-center-f08161d9-ab64-4486-af69-7cd30b34df71
