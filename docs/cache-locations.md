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

## VS Code editor caches

**Tier 1 — regenerable cache.** Pre-selected.

| | |
| --- | --- |
| **Location** | Any Code - OSS editor's folder one level under `%APPDATA%` — `Code`, `Code - Insiders`, `VSCodium`, `Cursor` and the rest |
| **Method** | Delete the four cache directories the editor writes, plus the web cache inside each webview partition |
| **Typical size** | 2.0 GB was measured on one workstation for a single editor |

### What it is

Visual Studio Code is a Chromium application, so [Chromium application
caches](#chromium-application-caches) above already reaches the six engine cache directories inside
its folder. Those six are the small part. The editor keeps four more caches of its own, under names
that belong to VS Code rather than to Chromium. On the measured machine the six came to about
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

Three things are recognised and then deliberately left alone. `widevine` is a content-decryption
module Steam downloaded so protected video will play, which is downloaded software rather than a
cache. `cefdata` is the embedded browser's working data, and nobody has established what removing it
costs. `appcache\librarycache` is artwork Steam downloaded for your library — a cache, but one whose
cost to fetch again was never established, so it is measured and not offered.

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

### Not reached: the shader cache

`steamapps\shadercache` holds compiled shaders per game and is commonly several gigabytes where
pre-caching is enabled. It sits inside a Steam library, and a library can be on any drive rather than
only beside the program, so reaching every copy of it means finding every library rather than reading
one recorded path. No machine with one to measure was available either, so it is left for its own
change.

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

## Per-project build output

**Tier 1 for .NET intermediate output, Tier 2 for the rest.** The only thing Deguffer looks for
inside your own folders, and the only thing it will not look for anywhere else.

| Directory | What it belongs to | Tier |
| --- | --- | --- |
| `obj` | .NET intermediate build output | 1 |
| `Library` | A Unity project's imported assets and caches | 2 |
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

Approving a folder approves it for **every** kind of build output in the table above, which is what
the Settings description says. If you want a narrower scope, add narrower folders.

### What Deguffer does

**A directory's name is not evidence.** `build`, `target` and `Library` are ordinary English words,
and a folder called one of them is as likely to hold somebody's exports as a compiler's output. So
recognition is never by name: it is by the project standing around the directory, and by the marker
the tool itself writes inside it.

| Directory | Must sit beside it | Must be inside it |
| --- | --- | --- |
| `obj` | A project file the restore manifest names | `project.assets.json`, and the generated NuGet imports for that same project |
| `Library` | `Assets`, `Packages`, `ProjectSettings` | — |
| `target` | `Cargo.toml` | `CACHEDIR.TAG`, which Cargo writes |
| `node_modules` | `package.json`, **and a lock file** | — |
| `.venv` / `venv` | A dependency manifest: `requirements.txt`, `pyproject.toml`, `Pipfile`, `setup.py` or `environment.yml` | `pyvenv.cfg` |

Every condition has to hold. A directory that fails any of them is left alone, and the preview says
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

So before anything reaches the preview, Deguffer asks whether each project is in use, and holds back
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
it and preview again.

**It can miss, and it never fires wrongly.** Three things it does not see, all of them stated here
because a safeguard whose limits are unwritten gets trusted past them:

- A compiler holding a file deep inside a tree that is neither its own program nor its working
  folder. Nothing can ask that question about a directory without administrator rights.
- **A program running as administrator, or as another user.** Deguffer runs unelevated, and Windows
  will not let it inspect those at all — so a build started from an elevated terminal is invisible.
- A project whose path is longer than 260 characters, for the lock-file signal only. Windows'
  Restart Manager refuses a path that long, so Unity's lockfile cannot be asked about there. The
  other two signals are unaffected, and the preview says the check could not run rather than
  reporting the project idle.

Where a check cannot run at all, the preview says so rather than staying quiet — "could not tell"
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
| `target` | The next build recompiles the project and every dependency, per profile — minutes to hours on a large workspace, and anything you built and are running from `target` goes with it. |
| `node_modules` | The project will not build or run until dependencies are installed again. The lock file pins the versions, so what comes back is what was there. |
| `.venv` / `venv` | The environment has to be created again and the install re-run from the manifest. **A manifest only lists what somebody wrote down** — check it covers what you had before removing an environment you still use. |

Where a package manager's own cache still holds the downloads, most of this is offline. Clearing that
cache in the same run is what makes it a download — so a run that takes both the npm cache and a
`node_modules` needs the network afterwards.

### Why `obj` is Tier 1 and the other four are Tier 2

Tier 1 means the tool re-creates it on demand and you lose only time. That is `obj`: a missing one
makes the next build slower and nothing else.

The other four cost more than a slower next use, and §3's definition of Tier 2 — "re-created, but
only by re-downloading gigabytes or re-indexing for minutes" — is met by each of them differently:

- **Unity** is the re-indexing case exactly. Nothing in `Library` is anyone's only copy; all of it is
  built from `Assets`, `Packages` and `ProjectSettings`, which is why every Unity `.gitignore`
  excludes it. What makes it Tier 2 is the reimport, plus the packages in `Library\PackageCache` that
  are fetched again.
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

So it is never pre-selected, and it is the first location in Deguffer whose confirmation says the
loss is permanent rather than costly. Switch *Type a name to delete user data* on and it asks you to
type the name out as well.

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
| **Location** | `C:\Windows\Logs\CBS`, `C:\Windows\Logs\WindowsUpdate`, `C:\Windows\Panther`, and `C:\Windows\System32\LogFiles\WMI\RtBackup` |
| **Method** | Delete each of those four, one row each |
| **Typical size** | 64 MB was measured on one workstation. Machines with a long update history are regularly reported in the gigabytes |

### What it is

The trail Windows leaves while maintaining itself.

| Folder | What wrote it |
| --- | --- |
| `Logs\CBS` | Component servicing — what Windows added, removed or repaired. `sfc /scannow` writes here too |
| `Logs\WindowsUpdate` | Windows Update's own trace files |
| `Panther` | Setup logs, from the original installation and from every in-place upgrade since |
| `System32\LogFiles\WMI\RtBackup` | Backup trace files for the event sessions the WMI service runs |

### What Deguffer does

It removes those four, one row each. `C:\Windows`, `C:\Windows\Logs` and the three folders above
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
`Windows\Installer` — all of those are named in the plan and checked after the run.

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

### `%LOCALAPPDATA%\Temp` — needs an age filter and live-process exclusions

Genuinely reclaimable, and genuinely dangerous to do naively. During the founding audit an active
session held 344 MB of live working files in Temp, with dozens of processes holding open handles.
Doing this properly needs an age filter, exclusion of paths belonging to running processes, and
treating "access denied" as normal rather than as an error. See §5.3.

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
promise is a preview and then a number, and there is no honest number to show for a step that
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

### Docker — freeing space inside the disk image does not free it on disk

`docker system prune` reclaims space *inside* `docker_data.vhdx`, while the host file stays exactly
the same size. Reporting one number would be actively misleading. This needs the two figures
reported separately, and the second cannot be measured from the filesystem — it comes from the
container tool's own accounting. See §5.4.
