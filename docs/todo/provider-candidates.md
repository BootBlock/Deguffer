# Provider candidates — the second survey

> **Status:** 🟢 ACTIVE — a researched candidate set from
> [#407](https://github.com/BootBlock/Deguffer/issues/407). Nothing in it is started. Mark an entry
> ✅ done when it ships, and flip to ✅ COMPLETE and `git mv` into `done/` when every *Build* and
> *Build later* entry has shipped or been declined.

[unreached-locations.md](unreached-locations.md) was the first survey of what the shipped providers
do not reach, and most of it has shipped. This is the second. It covers five areas: virtualisation,
global package-manager caches, per-project and IDE caches, end-user applications, and Windows
itself. Nothing already decided in that survey or in
[../cache-locations.md](../cache-locations.md) is repeated here, except where this research
contradicts it, and [Corrections](#corrections-to-what-the-repository-already-says) lists those.

Every entry says whether it was **measured** on one Windows 11 workstation, read-only and
unelevated, or only **researched**. A researched entry rests on vendor documentation or the tool's
own source, cited inline. Where a primary source could not be read, the entry says so, and nothing
is to be built on it until the source is read. Some vendor help sites refuse automated fetches;
claims taken from them are marked *(page not read directly)*.

Verdicts are one of four:

- **Build** — evidence and route are established. Ready to design.
- **Build later** — worth having, with a named gap to close first.
- **Report only** — the right row offers nothing to tick, as `VirtualDiskProvider` does.
- **Reject** — with the reason, so the question is not asked again.

---

## The short answer: VMware and Hyper-V

Deguffer has no VMware, Hyper-V or VirtualBox provider. The only virtualisation provider is
`VirtualDiskProvider`, which reports WSL 2 and Docker Desktop disks and offers nothing.

All three hypervisors fit that same shape, and none of them fits an offered row. Everything large in
a virtual machine's folder is the machine: a base disk, a snapshot's delta disk, a suspended
session, a saved state. Deleting any of them by path corrupts the machine or loses work. Every
vendor's own route to a smaller footprint is two steps — delete snapshots inside the hypervisor,
which merges them, then compact the disk with the machine off — and §5.4's lesson applies to both.

So the proposal is one report per hypervisor at Tier 3, found from the hypervisor's own records,
never from a search for `.vmdk`, `.vhdx` or `.vdi`. Each reports the size on the drive, the snapshot
count and delta bytes, and whether a suspended or saved state is present, and names the vendor's
route in order. The one offered row in this area is old Vagrant box versions, through Vagrant's own
`prune`.

---

## 1. Virtualisation

| Candidate | Verdict | Tier | Measured |
| --- | --- | --- | --- |
| VMware Workstation virtual machines | Report only | 3 | Yes: two VMs, one with a snapshot |
| Hyper-V virtual machines and checkpoints | Report only, where Hyper-V answers | 3 | Present, but nothing answers unelevated |
| VirtualBox virtual machines and media | Report only | 3 | Absent |
| Vagrant superseded box versions | **Build** | 2 | Absent |
| Rancher Desktop snapshots | Report only | 3 | Absent |
| Podman and Rancher Desktop distributions | Owner records in the WSL report | 3 | Absent |
| Rancher Desktop `cache`, Podman machine image cache | Build later | — | Absent |
| Packer, minikube, kind, Multipass, Windows Sandbox, Hyper-V Quick Create downloads | Reject | — | — |
| Any VM file by path: logs, `.lck`, `.vmem`, `.vmss`, `.vmsn`, delta disks, `.avhdx`, `.vmrs`, `.vmgs` | Reject | 4 | — |

### VMware Workstation — Report only, measured

**Records.** Workstation keeps no single complete list. `%APPDATA%\VMware\inventory.vmls` names
each VM in its library (`vmlistN.config` is the `.vmx` path), and `%APPDATA%\VMware\preferences.ini`
holds the recently opened list (`pref.mruVMn.filename`) and the default folder
(`prefvmx.defaultVMPath`). Broadcom documents neither file; community sources agree on both
([thread](https://communities.vmware.com/thread/429030)). On the measured machine the recent list
named a `.vmx` that no longer existed and that the library did not hold. So discovery is the union
of the two, each `.vmx` checked for existence, and a missing one reported as missing, never
inferred to be an orphan. Disks come from the `.vmx` (`scsiN:N.fileName` and its kin), snapshots
from the `.vmsd`.

**What is in a VM folder** (Broadcom,
[virtual machine files](https://techdocs.broadcom.com/us/en/vmware-cis/desktop-hypervisors/workstation-pro/17-0/using-vmware-workstation-pro/creating-virtual-machines-in-workstation-user-guide/virtual-machine-files.html)):
`.vmx` configuration, `.vmdk` disks, redo-log disks holding "disk changes when a VM has snapshots",
`.vmsd` snapshot metadata, `.vmsn` snapshot state, `.vmss` suspended state, and `.vmem`, which exists
"only while the VM runs or after a failure". Measured: one VM's `.vmx` pointed at its
`-000001.vmdk` delta, not its base disk, because it had a snapshot. Deleting that delta would leave
a VM that does not start and lose every write since the snapshot.

**Route** (§5.1). `vmrun listSnapshots` and `vmrun deleteSnapshot`, or the snapshot manager; then
*VM > Manage > Clean up Disks*, or `vmware-vdiskmanager -k` with the VM off. Broadcom: "If your disk
has snapshots, you must delete the snapshots before trying to reduce the size of the disk", it does
not apply to pre-allocated disks, and "do not shut down your virtual machine or the host machine
while the disk is shrinking"
([KB 315631](https://knowledge.broadcom.com/external/article/315631/defragmenting-and-shrinking-vmware-works.html)).
Shrinking frees host space. Deleting a snapshot frees it only once the delta has merged.

**Rejected inside it.** Old `vmware-N.log` rotations (about 1 MB measured, Tier 3 diagnostics, and
Workstation rotates them itself) and `caches\GuestAppsCache` (0.4 MB). Neither is worth a row.

### Hyper-V — Report only, where Hyper-V answers

**Discovery has to be Hyper-V's API.** `Get-VMHost`'s `VirtualHardDiskPath` is not where the disks
are: Microsoft says it "is not a functional setting… the Hyper-V API… and `New-VHD` don't use it"
([Set-VMHost](https://learn.microsoft.com/en-us/powershell/module/hyper-v/set-vmhost)). A disk can be
anywhere, so a provider asks each VM for its attached disks (`Get-VMHardDiskDrive`) and checkpoints
(`Get-VMSnapshot`). An unattached `.vhdx` in the default folder is not an orphan: it can belong to
WSL, Docker, Multipass or another host.

**Elevation decides everything.** Measured unelevated, as a filtered administrator token that is not
in Hyper-V Administrators: `Get-VM`, `Get-VMHost` and `Get-VHD` all fail with "You do not have the
required permission", `root\virtualization\v2` returns only the host, and
`%PROGRAMDATA%\Microsoft\Windows\Hyper-V` refuses a listing. Hyper-V Administrators "have complete
and unrestricted access to all the features in Hyper-V"
([security groups](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/understand-security-groups)).
So the row has to say *why* it is empty when it cannot ask, never that there are no VMs.

**Checkpoints.** Stored as `.avhdx` beside the `.vhdx`. Deleting a checkpoint makes Hyper-V merge
the two, and Microsoft says: "You should not delete the .avhdx files directly"
([checkpoints](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/checkpoints)).
The route is `Remove-VMCheckpoint`
([Remove-VMSnapshot](https://learn.microsoft.com/en-us/powershell/module/hyper-v/remove-vmsnapshot)),
then `Optimize-VHD` with the disk detached or attached read-only
([Optimize-VHD](https://learn.microsoft.com/en-us/powershell/module/hyper-v/optimize-vhd)). A `.vmrs`
holds "the running state of virtual machines" and is the size of the VM's memory while it runs
([KB 4056669](https://support.microsoft.com/en-in/help/4056669/hyper-v-vss-backs-up-a-large-vmrs-file-when-you-back-up-a-vm)).
No primary source was found for `.vmgs`, `.vsv` or `.bin`; all are live state, Tier 4.

**Rejected beside it.** Windows Sandbox: "closing it deletes all software, files, and state"
([Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)),
and its base image is about 500 MB managed by the feature itself. Quick Create's downloaded image:
the only source is a community answer that it stays in `%TEMP%`, where §5.3's rules already apply.

### VirtualBox — Report only, researched

`VirtualBox.xml` in `%USERPROFILE%\.VirtualBox` (or `VBOX_USER_HOME`) "includes global configuration
options and a list of registered virtual machines", and each VM folder has a `Logs` and a movable
`Snapshots` subfolder
([technical background](https://www.virtualbox.org/manual/topics/TechnicalBackground.html)). Logs
rotate to `.1`–`.3` on their own
([troubleshooting](https://www.virtualbox.org/manual/topics/Troubleshooting.html)). The route is
`VBoxManage snapshot delete`, zeroing free space inside the guest, then
`VBoxManage modifymedium --compact`
([VBoxManage](https://www.virtualbox.org/manual/topics/vboxmanage.html)). The report also names
registered media whose file is missing. `closemedium --delete` is never a step: it deletes the image.

### Vagrant superseded boxes — Build, Tier 2, researched

`VAGRANT_HOME` defaults to `~/.vagrant.d` and holds the boxes, "so it can actually become quite
large" ([environment variables](https://developer.hashicorp.com/vagrant/docs/other/environmental-variables)).
`vagrant box prune` "removes old versions of installed boxes", with `--dry-run`, `--force`, and
`--keep-active-boxes` ("when combined with `--force`, will keep boxes still actively in use")
([box](https://developer.hashicorp.com/vagrant/docs/cli/box)). Its source keeps the newest version
for each box name and provider
([prune.rb](https://raw.githubusercontent.com/hashicorp/vagrant/main/plugins/commands/box/command/prune.rb)).

- **Plan** with `--dry-run`, **clean** with `--force --keep-active-boxes`. Deguffer closes standard
  input, so a prompt would read end of input; how Vagrant treats that is unverified, which is why
  the pair of flags that never prompts is the step.
- **Tier 2**: a re-download. The machines made from a box live in the hypervisor, not in `boxes`.
- **Survivors** (§5.2): `Vagrantfile`, `data\machine-index`, `insecure_private_key`, `plugins.json`,
  `gems`. Nothing in `.vagrant.d` is a path target; `tmp` has no primary source, so it is not offered.

### Container tools on WSL

Podman's WSL machine is a distribution named `podman-<name>`
([machine.go](https://raw.githubusercontent.com/containers/podman/main/pkg/machine/wsl/machine.go)),
and Rancher Desktop registers `rancher-desktop` and `rancher-desktop-data`
([wsl.ts](https://raw.githubusercontent.com/rancher-sandbox/rancher-desktop/main/pkg/rancher-desktop/backend/wsl.ts)).
The WSL report already lists every registered distribution, so these appear today under a generic
WSL route. Each wants an owner and its own route named: `podman system prune` inside the machine, and
for Rancher `rancher-desktop-data` treated as Docker Desktop's data disk is.

Rancher Desktop's snapshots under `%LOCALAPPDATA%\rancher-desktop\snapshots` are `wsl --export`
copies of both distributions, managed by `rdctl snapshot delete`
([paths_windows.go](https://raw.githubusercontent.com/rancher-sandbox/rancher-desktop/main/src/go/rdctl/pkg/paths/paths_windows.go)):
report only, Tier 3. Its `cache` folder is commented as "caches that may be removed", but the same
source puts `cache\Rancher Desktop` there as old user data, so it waits on a real install's listing.

`WslRegistrations` tells Docker's distributions apart with a name switch. G1 says a new source is
never an edit to a switch, so owner recognition should become a record per tool before Podman and
Rancher are added.

### Rejected

- **Packer**: `packer_cache` is relative to the directory a build ran in
  ([configure](https://developer.hashicorp.com/packer/docs/configure)), and nothing records where that
  was. Finding it would need a disk-wide search.
- **minikube**: the only whole-cache route, `minikube delete --purge`, deletes "the '.minikube'
  folder", clusters with it ([delete](https://minikube.sigs.k8s.io/docs/commands/delete/)).
- **kind**: entirely inside Docker's disk, which is already reported.
- **Multipass**: machine-wide storage under `%PROGRAMDATA%`, owned by a service, and its instances
  are Hyper-V VMs ([storage](https://documentation.ubuntu.com/multipass/latest/how-to-guides/customise-multipass/configure-where-multipass-stores-external-data)).
- **Android emulator images**: already rejected with the Android SDK in
  [../cache-locations.md](../cache-locations.md). Genymotion's devices under
  `%LOCALAPPDATA%\Genymobile\Genymotion\deployed` are VirtualBox VMs, so they are the VirtualBox
  report's, if they register there (unverified).

### What the virtualisation reports need before any of them ships

**The negative.** §11 of the first survey records that no run checks afterwards that a registered
disk survived, and that Explore's refusal is the only guard. Three more reports make that gap three
times wider. A VMware VM's default home is under Documents, which other providers can scope. A
run-wide refusal of every registered VM folder and disk should land with, or before, the first new
report.

---

## 2. Global package-manager and language-tool caches

| Candidate | Verdict | Tier | Measured |
| --- | --- | --- | --- |
| Electron download cache | **Build** | 1 | 150.9 MB |
| electron-builder cache | **Build** | 1 | 63.9 MB |
| TypeScript typings cache | **Build** | 1 | 57.7 MB |
| Bun install cache | **Build**, through `prune` only | 1 | Absent |
| Yarn 1 cache | **Build** | 1 | Absent |
| Deno cache | **Build**, by path, never `deno clean` | 1 | Absent |
| Conan 2 | **Build**, `conan cache clean` only | 1 | Absent |
| Scoop download cache | **Build** | 1 | Absent |
| Composer | **Build**, low value | 1 | 30.1 MB |
| Cypress binaries | Build later | 2 | Absent |
| Yarn Berry global cache | Build later | 2 | Absent |
| PDM, Bazelisk, Coursier, opam, Julia, node-gyp, Chocolatey, Terraform plugin cache, RubyGems, Cabal, Stack, Hex | Build later | 1–2 | Absent |
| winget portable packages, rustup toolchains, pipx, Hatch, Volta, nvm-windows, fnm, `scoop cleanup` | Reject | — | — |

### Electron download cache — Build, Tier 1, measured at 150.9 MB

`%LOCALAPPDATA%\electron\Cache`, moved by `electron_config_cache`
([installation](https://github.com/electron/electron/blob/main/docs/tutorial/installation.md)). Each
entry is a folder named by the SHA-256 of the download URL's directory, holding one zip
([Cache.ts](https://raw.githubusercontent.com/electron/get/main/src/Cache.ts)). The zip is only
unpacked into a project's `node_modules`, so installed projects keep working; the cost is one
download at the next fresh install. No clean command exists, so path-based. Recognised children are
the 64-hex folders; any other name is Tier 4.

### electron-builder cache — Build, Tier 1, measured at 63.9 MB

`%LOCALAPPDATA%\electron-builder\Cache`, moved by `ELECTRON_BUILDER_CACHE`
([offline builds](https://www.electron.build/docs/tutorials/offline-air-gapped-builds)). Measured
children: `winCodeSign-<version>`, `nsis-<version>`, `nsis-resources-<version>`, `7zip@<version>`,
`icons@<version>`, `downloads`. Build-time tool downloads, fetched again at the next build. **Gap:**
the source that names these entries was not found, so the name patterns are observed, not verified.
A separate provider from Electron's (G1).

### TypeScript typings cache — Build, Tier 1, measured at 57.7 MB

`%LOCALAPPDATA%\Microsoft\TypeScript\<major.minor>`, confirmed from the shipped `_tsserver.js`,
whose `win32` branch joins `LOCALAPPDATA` with `Microsoft/TypeScript` and `versionMajorMinor`.
Measured: four version folders, the newest 48.2 MB, each an npm project of acquired typings. A
folder for a TypeScript version no editor runs any more is dead weight; the current one comes back
when a JavaScript project is next opened. Recognised children are folders named `\d+\.\d+`. Held
back while `tsserver` runs.

### Bun install cache — Build through `prune` only, Tier 1, researched

`~/.bun/install/cache`, moved by `BUN_INSTALL_CACHE_DIR`
([global cache](https://bun.com/docs/pm/global-cache)). **This corrects the first survey**, which
recorded no eviction command: `bun pm cache rm` and an age-based
`bun pm cache prune [--max-age N] [--dry-run]` now exist ([pm](https://bun.com/docs/pm/cli/pm)).

The trap is `<cache>\links`, the global store. With `install.globalStore` on, a project's
`node_modules` are links into it, and deleting it breaks each such project "until you run
`bun install` in it again" ([global store](https://bun.com/docs/pm/global-store)). `rm` deletes it;
`prune` keeps it, along with git clones and manifests. So the step is `prune` (or
`prune --max-age 0`), never `rm`, and `links` is never a path target. The Bun version that added
`prune` is not stated anywhere found; probe with `--dry-run`.

### Yarn 1 cache — Build, Tier 1, researched

`%LOCALAPPDATA%\Yarn\Cache\v6` (`getCacheDir()` and `CACHE_VERSION = 6` in
[user-dirs.js](https://raw.githubusercontent.com/yarnpkg/yarn/master/src/util/user-dirs.js) and
[constants.js](https://raw.githubusercontent.com/yarnpkg/yarn/master/src/constants.js)).
`yarn cache dir` reports it and `yarn cache clean` clears it
([cache](https://classic.yarnpkg.com/en/docs/cli/cache)). `.npmrc` and `.yarnrc` are read from the
working directory's parents, so the fixed working directory (§5.1) matters. `Yarn\Data` beside it
holds installed global packages: Tier 4.

### Deno cache — Build by path, Tier 1, researched

`DENO_DIR`, `%LOCALAPPDATA%\deno` by default
([installation](https://docs.deno.com/runtime/getting_started/installation/)). **A recorded
exception to §5.1**: `deno clean` deletes the whole folder
([clean.rs](https://raw.githubusercontent.com/denoland/deno/main/cli/tools/clean.rs)), and that
folder holds `location_data` — `localStorage` and the default `Deno.openKv()` database — and
`deno_history.txt`, the REPL history
([deno_dir.rs](https://raw.githubusercontent.com/denoland/deno/main/libs/resolver/cache/deno_dir.rs)).
The tool's own command destroys user data, so the provider deletes recognised children instead:
`remote`, `gen`, `npm`, `registries`, `dl`, the `*_cache_v2` files and `latest.txt`.
`location_data` and the history are named survivors.

### Conan 2 — Build `conan cache clean` only, Tier 1, researched

`CONAN_HOME`, `~/.conan2` by default
([environment](https://docs.conan.io/2/reference/environment.html)). `conan cache clean` removes
"non-critical folders… source, build and/or download"
([cache](https://docs.conan.io/2/reference/commands/cache.html)). Never `conan remove "*"`: it
removes packages, and one made with `conan create` and never uploaded exists nowhere else — Maven's
Tier 2 argument. A `.conanrc` in the working directory or any parent overrides `CONAN_HOME`
([conanrc](https://docs.conan.io/2/reference/config_files/conanrc.html)), and Deguffer's fixed folder
is under the profile, so a `.conanrc` there would apply; the row should say which home it asked.

### Scoop download cache — Build, Tier 1, researched

`$env:SCOOP_CACHE`, else `cache_path` in `%USERPROFILE%\.config\scoop\config.json`, else
`%USERPROFILE%\scoop\cache`
([core.ps1](https://raw.githubusercontent.com/ScoopInstaller/Scoop/master/lib/core.ps1)). The step is
`scoop cache rm *`
([scoop-cache.ps1](https://raw.githubusercontent.com/ScoopInstaller/Scoop/master/libexec/scoop-cache.ps1)).
**`scoop cleanup *` is rejected**: it removes old app versions under `apps\<app>\<version>`, which is
uninstalling (§2), and the first survey's long-tail table offered it beside the cache.

### Composer — Build, Tier 1, measured at 30.1 MB

`composer config --global cache-dir` reported `%LOCALAPPDATA%/Composer`;
`composer clear-cache` clears it
([ClearCacheCommand.php](https://raw.githubusercontent.com/composer/composer/main/src/Composer/Command/ClearCacheCommand.php)).
It loads a `composer.json` from the working directory first, so the fixed folder matters. Low value:
Composer already caps its own cache at 300 MiB and six months
([config](https://getcomposer.org/doc/06-config.md)). With only `COMPOSER_HOME` set the cache sits in
`$COMPOSER_HOME/cache` beside `auth.json`, which the command handles and a path fallback must not.

### Build later

| Tool | Route | Gap |
| --- | --- | --- |
| Cypress | `%LOCALAPPDATA%\Cypress\Cache`, per-version folders ([advanced installation](https://docs.cypress.io/app/references/advanced-installation)) | No global CLI to run, and "the version in use" is per project, so `prune` is undefined from a neutral folder. Tier 2: runs fail until `cypress install`, the Playwright shape |
| Yarn Berry | `%LOCALAPPDATA%\Yarn\Berry\cache` on Windows ([folderUtils.ts](https://raw.githubusercontent.com/yarnpkg/berry/master/packages/yarnpkg-core/sources/folderUtils.ts)); the docs' `~/.yarn/berry` is wrong there | Plug'n'Play reads packages straight from these zips ([offline cache](https://v3.yarnpkg.com/features/offline-cache)), so deleting it breaks every PnP project until `yarn install`. Needs a reference-aware answer, as pnpm did |
| PDM | `pdm cache clear`, which keeps a package any environment still refers to ([caches.py](https://raw.githubusercontent.com/pdm-project/pdm/main/src/pdm/models/caches.py)) | Windows location inferred from platformdirs, not observed. Never delete `packages` by path |
| Bazelisk | Downloaded Bazel versions; its FAQ says to "delete this directory at any time" ([bazelisk](https://github.com/bazelbuild/bazelisk)) | Windows location inferred, not observed. Bazel's own output bases are per workspace with a server, and are rejected for now |
| Coursier | `%LOCALAPPDATA%\Coursier\Cache\v1` ([cache](https://get-coursier.io/docs/cache)) | No clean command. Siblings (`data` holds installed apps) unverified. sbt's `~/.ivy2/local` is locally published, Tier 4 |
| opam | `opam clean -c -r` only ([opam-clean](https://opam.ocaml.org/doc/man/opam-clean.html)) | Its defaults include `--logs`, Tier 3. Windows `OPAMROOT` default unverified |
| Julia | `Pkg.gc()`, which removes what has been orphaned for 30 days ([managing packages](https://pkgdocs.julialang.org/v1/managing-packages/)) | Depot holds REPL history and environments; §5.2 in full |
| node-gyp | `%LOCALAPPDATA%\node-gyp\Cache`, `node-gyp remove <version>` ([node-gyp.js](https://raw.githubusercontent.com/nodejs/node-gyp/main/lib/node-gyp.js)) | Tens of MB per Node version |
| Chocolatey | `choco cache remove` (v2.1.0+) clears only the HTTP cache, and the system one only elevated ([cache](https://docs.chocolatey.org/en-us/choco/commands/cache/)) | `lib-bkp` has no vendor statement that it is safe to delete |
| Terraform plugin cache | Opt-in through `plugin_cache_dir`; "Terraform will never itself delete a plugin" ([CLI config](https://developer.hashicorp.com/terraform/cli/config/config-file)) | Whether initialised folders link into it is unverified, hence Tier 2 |
| Cabal | `$XDG_CACHE_HOME/cabal/packages` only ([config](https://cabal.readthedocs.io/en/stable/config.html)) | Never the store: "deleting this directory might cause installed programs to stop working". Windows XDG mapping unverified |
| RubyGems, Stack, Hex | — | `GEM_HOME\cache` is inside the Ruby install; Stack has no clean command; `~/.hex` holds the API key beside the cache |

### Rejected

| Item | Reason |
| --- | --- |
| winget `%LOCALAPPDATA%\Microsoft\WinGet\Packages` (79.3 MB measured) | **Installed portable apps, not a cache** ([settings](https://learn.microsoft.com/en-us/windows/package-manager/winget/settings)). Belongs on the negative list |
| rustup toolchains (1.51 GB measured) | Installs, removed with `rustup toolchain uninstall` (§2). Its `downloads` and `tmp` were empty |
| `scoop cleanup *`, `gem cleanup`, nvm-windows, fnm, Volta | Each removes or manages installed versions, which is uninstalling. Volta is also unmaintained, by its own README |
| pipx | The `pipx run` cache expires itself after 14 days ([constants.py](https://raw.githubusercontent.com/pypa/pipx/main/src/pipx/constants.py)); the rest is installed environments |
| Hatch | No clean command, and its cache sits inside the data folder beside `env` and `pythons` |

---

## 3. Per-project and IDE caches

| Candidate | Verdict | Tier | Measured |
| --- | --- | --- | --- |
| Godot `.godot\imported` and `.godot\shader_cache` | **Build**, named children only | 2 and 1 | **14.0 GB across 36 projects** |
| JetBrains-platform system directories of superseded versions | **Build** | 1 | 633 MB, one superseded Android Studio |
| Flutter/Dart `.dart_tool` | **Build** | 2 | 288 MB across 2 projects |
| Python tool caches that write `CACHEDIR.TAG` | **Build**, as a declared table | 1–2 | One `.pytest_cache` |
| Gradle per-project `.gradle` | Build later | 1 | 162 MB across 5 |
| Current-version JetBrains `caches`, `index`, `log`, `tmp` | Build later | 1 | About 160 MB per version |
| Terraform `.terraform\providers` | Build later | 2 | Absent |
| `.next\cache`, `.angular\cache`, `.turbo\cache`, `.parcel-cache` | Build later | 1 | Absent |
| Zig per-project `.zig-cache` | Build later | 1 | One |
| CMake build trees | Build later | 2 | Absent |
| `__pycache__`, Visual Studio `ComponentModelCache`, ReSharper caches, Gradle `build`, Maven `target`, Bazel `bazel-*`, `.idea` | Reject | — | — |

### Godot `.godot` — Build, named children only, measured at 14.0 GB

The largest per-project item on the measured machine, and not on any earlier list. Across 36
`.godot` folders, each beside a `project.godot`:

| Child | Measured | Tier |
| --- | ---: | --- |
| `imported` | 12,992 MB | 2: every asset reimported on next open |
| `shader_cache` | 1,030 MB | 1 |
| `editor` | 60 MB | 4: the project's editor state |
| `export_credentials.cfg` | tiny | **4** |
| Everything else (`mono`, `uid_cache.bin`, plugin files) | 61 MB | 4 |

**The whole folder must never be taken.** Godot's docs call `.godot` "project cache data"
([version control](https://docs.godotengine.org/en/stable/tutorials/best_practices/version_control_systems.html)),
and the same docs say `.godot/export_credentials.cfg` "contains export options that are considered
confidential, like passwords and encryption keys"
([exporting](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html)). Three
measured projects held one, and one also held a plugin's access token. That is §5.2's
`gradle.properties` beside `caches`, exactly.

**Evidence.** Godot keeps "imported resources hidden in a `res://.godot/imported/` folder", and if
they are erased "the asset or assets will be reimported automatically"
([import process](https://docs.godotengine.org/en/stable/tutorials/assets_pipeline/import_process.html)).
Inside, files are named `<asset>-<md5>.<ext>` with a matching `.md5`, observed. The rule: a sibling
`project.godot` (the Unity shape), and `imported` only where its files follow that naming. Every
`<asset>.import` file beside a source asset is the project's own configuration and is never a target.
No documented command clears the import cache. Godot 3 kept the same cache in a top-level `.import`
(5 MB across 4 here).

**Gap:** whether a running Godot editor holds files in `.godot` open. A live-project veto needs it.

### Superseded JetBrains-platform system directories — Build, Tier 1, measured

JetBrains documents the system (caches) directory as `%LOCALAPPDATA%\JetBrains\<product><version>`
and configuration as `%APPDATA%\JetBrains\<product><version>`
([directories](https://www.jetbrains.com/help/idea/directories-used-by-the-ide-to-store-settings-caches-plugins-and-logs.html)).
Android Studio keeps its system directory under `%LOCALAPPDATA%\Google`
([studio config](https://developer.android.com/studio/intro/studio-config)).

**The vendor's own rule, from its source.** Seven days after an update the IDE deletes groups whose
newest file is older than 180 days (`OLD_DIRECTORIES_SHELF_LIFE_DAYS = 180` in
[UpdateCheckerProjectActivity.kt](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-impl/update-checker/src/com/intellij/openapi/updateSettings/impl/UpdateCheckerProjectActivity.kt)).
For that automatic clean, `getRelatedDirectories(config, forAutoClean=true)` returns the **system
directory and the logs directory only**, never configuration or plugins
([ConfigImportHelper.java](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-impl/initial-config-import/src/com/intellij/openapi/application/ConfigImportHelper.java)).
A version counts as installed when the `.home` file inside its directory leads to an install whose
`product-info.json` `dataDirectoryName` matches the directory's name
([OldDirectoryCleaner.java](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-impl/initial-config-import/src/com/intellij/ide/OldDirectoryCleaner.java)).

**Proposal:** offer exactly what the IDE would delete by itself, under exactly its tests. Tier 1,
because the vendor deletes it without asking. Measured: a superseded Android Studio system directory
of 633 MB whose newest write was about 250 days old and whose `.home` led to an install now running a
later version. Configuration directories are never offered: measured Rider configuration folders
held `c.kdbx`, `c.pwd` and a licence key. There is no command to run; the vendor's action is inside
the IDE.

### Flutter/Dart `.dart_tool` — Build, Tier 2, measured at 288 MB

Unlike the declined Dart `build`, this one carries evidence inside: `.dart_tool\package_config.json`
has `"generator": "pub"`, which pub documents as "for `pub` this is always `'pub'`"
([package_config.dart](https://github.com/dart-lang/pub/blob/master/lib/src/package_config.dart)).
Siblings `pubspec.yaml` and `pubspec.lock` are required, on the `node_modules` argument.
`flutter clean` deletes it with `build`
([flutter CLI](https://docs.flutter.dev/reference/flutter-cli)), but per project, so Cargo's reasoning
for not running a per-project command applies. Tier 2: `pub get` re-resolves and the Flutter build
state is recompiled. **Hazard:** three measured `.dart_tool` folders sat inside a Flutter SDK checkout
as test fixtures, so the git tracked-file check `obj` already uses applies.

### Tools that write `CACHEDIR.TAG` — Build as a declared table

The [Cache Directory Tagging Specification](https://bford.info/cachedir/) requires the first 43
bytes to be `Signature: 8a477f597d28d172789f06886806bc55`, and calls a tag a hint.

| Directory | Writes the tag, from source | Producer line | Tier |
| --- | --- | --- | --- |
| Cargo `target` | Yes, only when Cargo creates the directory ([paths.rs](https://github.com/rust-lang/cargo/blob/master/crates/cargo-util/src/paths.rs)) | "created by cargo" | 2, shipped |
| `.pytest_cache` | Yes ([cacheprovider.py](https://github.com/pytest-dev/pytest/blob/main/src/_pytest/cacheprovider.py)) | "created by pytest" | 1 |
| `.mypy_cache` | Yes ([build.py](https://github.com/python/mypy/blob/master/mypy/build.py)) | "automatically created by mypy" | 1 |
| `.ruff_cache` | Yes, header only ([cache.rs](https://github.com/astral-sh/ruff/blob/main/crates/ruff/src/cache.rs)) | none; its `.gitignore` names ruff | 1 |
| `.tox` | Yes ([path.py](https://github.com/tox-dev/tox/blob/main/src/tox/util/path.py)) | "created by tox" | 2: environments |
| `.nox` | Yes, header only ([virtualenv.py](https://github.com/wntrblm/nox/blob/main/nox/virtualenv.py)) | none | 2 |

**A declared table, never "any tagged directory".** tox and nox tag directories that already exist,
so a tag does not prove the tool made the directory, and a user can tag a folder by hand to keep it
out of a backup. Each row names the directory, the verified header, the producer line or `.gitignore`
that identifies it, and a sibling manifest (`pyproject.toml`, `setup.cfg`, `tox.ini`, `noxfile.py`,
`mypy.ini`). `BuildDirectoryKind.RequiredContents` today checks only that the tag exists; reading the
header would strengthen Cargo's rule too. Value on the measured machine is near zero; the case is the
strength of the evidence.

Gradle tags only its user-home caches, not the per-project `.gradle`
([directory layout](https://docs.gradle.org/current/userguide/directory_layout.html)). Zig, CMake,
Bazel, Next.js, Turborepo, Parcel, Angular, Vite, pub and Terraform were not found to write one.

**A measured example of why evidence inside the directory matters.** Of nine Cargo `target` folders
measured (5.8 GB), two (1.08 GB) carried no tag, because something else had created the folder before
Cargo did, and `CargoTargetProvider` correctly declined both.

### Build later

| Candidate | Evidence and route | Gap |
| --- | --- | --- |
| Gradle per-project `.gradle` | Gradle calls it "transient files… to support features like incremental builds", and deletes unused version folders after 7 days itself ([directory layout](https://docs.gradle.org/current/userguide/directory_layout.html)) | No tag inside. The observed child names (`buildOutputCleanup`, `file-system.probe`) are not yet verified from Gradle's source |
| Current-version JetBrains children | *Invalidate Caches* is GUI-only, and deletes on restart ([invalidate caches](https://www.jetbrains.com/help/idea/invalidate-caches.html)) | A running-IDE veto. `LocalHistory` is Tier 3 and stays |
| Terraform `.terraform\providers` | `init` downloads them again ([init](https://developer.hashicorp.com/terraform/cli/init)) | Only that child. `.terraform\terraform.tfstate` holds backend configuration that "may contain sensitive credentials" ([backend](https://developer.hashicorp.com/terraform/language/backend)) |
| `.next\cache`, `.angular\cache`, `.turbo\cache`, `.parcel-cache` | Each is the tool's documented default ([Next.js](https://nextjs.org/docs/app/guides/ci-build-caching), [Angular](https://angular.dev/cli/cache), [Turborepo](https://turborepo.dev/docs/crafting-your-repository/caching), [Parcel](https://parceljs.org/features/development/)) | A name and neighbours only, which is what declined `dist`. Find a marker inside each from its source |
| Zig `.zig-cache` | "usually called `.zig-cache`" (`lib/std/Build.zig`); no tag | Sibling `build.zig`, children limited to `b`, `h`, `o`, `tmp`, `z` |
| CMake build trees | `CMakeCache.txt` marks the build directory ([CMAKE_CACHEFILE_DIR](https://cmake.org/cmake/help/latest/variable/CMAKE_CACHEFILE_DIR.html)) | An in-source build would make the source tree the target: the cache's home directory must differ from its own |

### Rejected

- **`__pycache__`**: never the only copy (a `.pyc` there "will be ignored" without its source,
  [PEP 3147](https://peps.python.org/pep-3147/)), but 8.9 MB across 71 measured. Tier 1 if value ever
  appears.
- **Visual Studio `ComponentModelCache`** (8.5 MB measured): a repair step, not space. The only
  official text is the Visual Studio 2015 `/UpdateConfiguration` page. This settles the first survey's
  §12 carve-out against it.
- **ReSharper caches**: ReSharper removes a solution's caches "if that solution hasn't been opened in
  more than 30 days" ([caches location](https://www.jetbrains.com/help/resharper/Configuring_Caches_Location.html)).
- **Gradle `build`, Maven `target`**: nothing inside proves the tool made them, the reason `dist`
  was declined.
- **Bazel `bazel-*`**: links into the output base; deleting one frees nothing
  ([output directories](https://bazel.build/remote/output-directories)).
- **`.idea`**: project configuration and `workspace.xml`. Tier 4.

---

## 4. End-user applications

| Candidate | Verdict | Tier | Measured |
| --- | --- | --- | --- |
| Lightroom Classic `Previews.lrdata` | **Build**, with a lost-original guard | 2 | Absent |
| Packaged (MSIX) Electron apps | **Build**, as a general rule | 1 | — |
| Classic Teams leftover `%APPDATA%\Microsoft\Teams` | Build later | 1 | Absent |
| iOS firmware `.ipsw` downloads | Build later | 2 | Absent |
| Camera Raw cache | Build later | 1 | Absent |
| Photoshop scratch files left by a crash | Build later | 1 | Absent |
| Kodi thumbnails with `Textures*.db` | Build later | 2 | Absent |
| NVIDIA app downloaded drivers | Build later, in `GraphicsDriverInstallerProvider` | 2 | Absent |
| Apple device backups | Explore only, never a cleanup row | 3 | Absent |
| Telegram, Zoom, Signal, Lightroom Smart Previews, Audacity, OBS, VLC, Blender, Google Earth, Steam extras, Ubisoft Connect, EA app | Reject | — | Some |
| Discord, unpackaged Slack, new Teams, WhatsApp | Already covered | — | Some |

Several vendor pages in this area could not be read directly. Every claim marked *(page not read
directly)* comes from a search engine's extract of the named page, and must be read in full before
anything is built on it.

### Lightroom Classic previews — Build, Tier 2, with a lost-original guard

`<Catalog> Previews.lrdata` sits beside each catalog, which can be anywhere, so discovery reads the
recent-catalog list in Lightroom's preferences
([file locations](https://helpx.adobe.com/lightroom-classic/desktop/kb/preference-file-and-other-file-locations.html),
*page not read directly*). Which key in the `.agprefs` holds the list is unverified. Adobe recommends
deleting the file when it is corrupt, and Lightroom limits it itself
([preview cache error](https://helpx.adobe.com/lightroom-classic/desktop/kb/lightroom-gives-error-preview-cache.html),
*page not read directly*). Forum reports put it in the hundreds of gigabytes.

**The §3 trap.** Adobe publishes a script to
[extract previews of lost photos](https://helpx.adobe.com/lightroom-classic/desktop/kb/extract-previews-for-lost-images-lightroom.html)
(*page not read directly*): for a photo whose original is gone, the preview is the last copy. So the
provider reads the `.lrcat` (SQLite) read-only and offers a catalog's previews only where every
original is present. **Survivors:** the `.lrcat` and its `-wal` and `-journal`, `Backups`, and
`Smart Previews.lrdata`, which is the only editable copy of an offline original (rejected below).
**In use:** a `<name>.lrcat.lock` beside the catalog, and a running Lightroom process, since a crash
can leave the lock behind.

### Packaged Electron apps — Build, as a general rule

[../cache-locations.md](../cache-locations.md) records "Not reached: packaged Electron applications".
The fix is the existing identification rule — a `Local State` file one level down — applied one level
under each package's `LocalCache\Roaming` and `LocalCache\Local`, the redirected roots the first
survey's §3 describes. It never treats `LocalCache` itself as permission to delete; Microsoft defines
it as "files that are not included in backup and restore", not as a cache
([app data](https://learn.microsoft.com/en-us/windows/apps/develop/data/store-and-retrieve-app-data)).
Measured: two packaged desktop apps' `LocalCache` folders held 16.2 GB between them. This rule is also
the first concrete step on the first survey's MSIX redirection item.

### Build later

| Candidate | Evidence | Gap |
| --- | --- | --- |
| Classic Teams leftover | Microsoft: for classic Teams, "delete all files and folders in the directory" ([clear Teams cache](https://learn.microsoft.com/en-us/troubleshoot/microsoftteams/teams-administration/clear-teams-cache)); its automatic removal "may fail" | Two levels under `%APPDATA%`, so the Chromium rule misses it. Declare it a Chromium host on a real leftover; never the whole folder, because uploaded meeting backgrounds may be inside (unverified) |
| iOS firmware | `%APPDATA%\Apple Computer\iTunes\iPhone Software Updates`, several GB per file | Only user reports for the location, and no current Apple page authorising deletion |
| Camera Raw cache | `%LOCALAPPDATA%\Adobe\CameraRaw\Cache`, with its own purge button and a size cap ([settings](https://helpx.adobe.com/camera-raw/using/camera-raw-settings.html), *page not read directly*) | The user can move it, and where that setting is stored was not found. Usually small because of the cap |
| Photoshop `Photoshop Temp*` | Adobe says to delete them after a scratch-disk-full error ([scratch disk](https://helpx.adobe.com/photoshop/desktop/troubleshoot/performance-stability-issues/troubleshoot-scratch-disk-full-errors-in-photoshop.html), *page not read directly*) | Scratch disks are named in preferences; the key and folder were not found. Only while no Photoshop runs |
| Kodi | Deleting `Textures*.db` and `Thumbnails` together is a "last resort" Kodi rebuilds from ([artwork cache](https://kodi.wiki/view/Artwork/Cache)) | Scraper sites purge images, so some artwork never returns. Exit Kodi first. The database number changes by version |
| NVIDIA app | `NVIDIA app\UpdateFramework\ota-artifacts` under `%PROGRAMDATA%\NVIDIA Corporation` | A forum post only; needs a machine running the NVIDIA app. Belongs in the existing driver-installer provider |

### Apple device backups — Explore only

Apple gives `%USERPROFILE%\Apple\MobileSync\Backup`, or `%APPDATA%\Apple Computer\MobileSync\Backup`,
and deletion only inside the app: "After you delete a backup, you can't recover it"
([locate backups](https://support.apple.com/en-us/108809)). Often the only backup of a phone. The
Outlook store argument applies: Explore names each backup by device and date, and nothing offers it.

### Rejected

| Item | Reason |
| --- | --- |
| Telegram Desktop | Caches are capped at about 1 GB each and a month by default ([storage_cache_types.h](https://github.com/desktop-app/lib_storage/blob/master/storage/cache/storage_cache_types.h)), inside `tdata` beside the session keys ([storage_account.cpp](https://github.com/telegramdesktop/tdesktop/blob/dev/Telegram/SourceFiles/storage/storage_account.cpp)) |
| Zoom | Zoom's own advice deletes `%APPDATA%\Zoom\data` whole ([clearing cache](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0058835)), and that tree holds the encrypted chat databases and custom backgrounds ([file directory](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0084064)). §5.2's root deletion with Tier 3 inside |
| Signal | The whole folder is the encrypted message store. Its Chromium caches are already reached by the `Local State` rule |
| Lightroom Smart Previews | The only editable copy of an offline original. Tier 3 |
| Audacity `SessionData` | Audio "that has never been saved as an Audacity project" ([directories](https://manual.audacityteam.org/man/directories_preferences.html)): crash recovery, Tier 3 |
| OBS, VLC, Blender, Google Earth | Under 0.5 GB measured, user data, or capped by the app. Blender's 361 MB measured was installed extensions |
| Steam `depotcache`, `dumps`, `logs`, `steamapps\temp` | 77 MB measured in all; dumps and logs are Tier 3 |
| Ubisoft Connect, EA app | Ubisoft says to *rename* the cache folder while logged out; `savegames` sits beside it. EA's route is in-app App Recovery; whether its shortcut is a command Deguffer could run is unverified |

**Already covered:** Discord's caches, by the `Local State` rule (its 186 MB `module_data` is
installed voice modules, not cache); unpackaged Slack; new Teams' WebView2 caches (the whole package
measured 42.7 MB); WhatsApp's WebView2 cache; NVIDIA's `DXCache` (2.83 GB measured), by
`GpuShaderCacheProvider`.

---

## 5. Windows itself

| Candidate | Verdict | Tier | Measured |
| --- | --- | --- | --- |
| Windows Search index, through `ISearchCatalogManager::Reset` | Build later | 2 | **7.45 GB** |
| *Upgrade Discarded Files* handler | **Build**, a fourth row in `PreviousWindowsInstallationProvider` | 2 | Absent |
| `LiveKernelReportsPath` and Task Manager's live kernel dumps | **Build**, in `CrashDumpProvider` | 3 | Absent |
| `Logs\DISM` and `LogFiles\setupcln` | Build later, in `WindowsServicingLogProvider` | 3 | 28 MB |
| *Windows Defender* handler (`LocalCopy`, `Support`) | Build later | 3 | Refused unelevated |
| *Diagnostic Data Viewer* and *Feedback Hub Archive* handlers | Build later | 3 | Refused unelevated |
| *Old ChkDsk Files* (`FOUND.00x`) | Build later | 3 | Absent |
| BranchCache | Build later, only where large | 1 | Refused unelevated |
| Reserved storage, hibernation, Compact OS, `MaxPatchCacheSize`, Defender retention, Diagnostic Data Viewer capacity | Name, never change | — | — |
| `DownloadsFolder` handler | Negative list | — | — |
| Defender definitions, AppX destaging, `wsreset`, `TempState`, `$PatchCache$`, Offline Files, `SoftwareDistribution`, DiagTrack logs, legacy handlers | Reject | — | Some |

### Windows Search index — Build later, Tier 2, measured at 7.45 GB

Microsoft confirms "*Windows.edb* (Windows 10) or *Windows.db* (Windows 11)" in
`C:\ProgramData\Microsoft\Search\Data\Applications\Windows`
([search performance](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/windows-search-performance-issues)).
Measured: `Windows.db` 6,862 MB, `Windows-gather.db` 518 MB, `Windows-usn.db` 66 MB, on-disk size
equal to length. The first survey measured 1.94 GB for the same file; it has more than tripled.

**New: a documented rebuild with no service control.**
[`ISearchCatalogManager::Reset`](https://learn.microsoft.com/en-us/windows/win32/api/searchapi/nf-searchapi-isearchcatalogmanager-reset)
"resets the underlying catalog by rebuilding the databases and performing a full indexing", and warns
it "can take a very long time, during which little or no information is available to be searched".
That is Windows' own route (§5.1), and if it shrinks the file, the first survey's blocker — stop the
service, delete, start it — no longer applies. **Gaps:** whether `Reset` needs elevation (its page
does not say), and whether it shrinks `Windows.db` or rebuilds it in place at the same allocation.
Both need a measurement on a disposable machine before design.

### Disk Cleanup handlers — the inventory

Windows' registered handlers under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches`
are Windows' own statement of what is safe to clear
([creating a handler](https://learn.microsoft.com/en-us/windows/win32/lwef/disk-cleanup)). The
measured machine registers 32.

| Handler | Status |
| --- | --- |
| Previous Installations, Temporary Setup Files, Windows ESD installation files | Covered by `PreviousWindowsInstallationProvider` |
| **Upgrade Discarded Files** (`SetupDirectories=$WINDOWS.~Q;$INPLACE.~TR;$Windows.~LS`) | **Not covered.** The same `setupcln.dll` as the three above, so a fourth row in `Cleanups`. Build |
| Windows Reset Log Files, Windows Upgrade Log Files | Covered by `WindowsServicingLogProvider`, which removes `Panther` by path deliberately and says why |
| Device Driver Packages, User file versions, Delivery Optimization Files, Update Cleanup, D3D Shader Cache, Recycle Bin, dump and error-report handlers | Covered by their providers |
| Temporary Files | Covered for `%TEMP%` and `C:\Windows\Temp`. See the `SystemTemp` question below |
| Windows Defender, Diagnostic Data Viewer database files, Feedback Hub Archive log files | Build later, below |
| Old ChkDsk Files | Build later, Tier 3: recovered fragments can be user data |
| BranchCache | Build later, below |
| Language Pack | Reject: what it removes is undocumented and could not be observed unelevated |
| Setup Log Files, RetailDemo Offline Content, Downloaded Program Files, Internet Cache Files, Offline Pages Files, Active Setup Temp Folders, Content Indexer Cleaner, Temporary Sync Files | Reject: legacy, or 0 to 8 MB measured |
| Thumbnail Cache | Already on the negative list |
| **DownloadsFolder** (`Folder=%USERPROFILE%\Downloads`, `FileList=*.*`, subfolders removed, no age) | **Never.** It empties the Downloads folder. Add it to the negative list by name, so no future handler host selects it |

**A hosting question for every handler below.** The documentation says a handler's `CleanupString`
is "run at the completion of the disk cleanup", which reads as the cleanup manager's job. Several
handlers depend on their pre- and post-cleanup commands. Whether the data-driven cleaner runs them
when Deguffer hosts it directly is neither documented nor verified, and it has to be settled first.

### Build later, in existing providers

- **`CrashDumpProvider`**: Windows lets `LiveKernelReports` move through
  `CrashControl\LiveKernelReports\LiveKernelReportsPath`
  ([live dump reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/kernel-live-dump-code-reference)),
  and `CrashDumpSettings` reads `DumpFile`, `MinidumpDir` and `DumpFolder` but not this value. The
  *Temporary Files* handler also names `%LOCALAPPDATA%\Microsoft\Windows\TaskManager\LiveKernelDumps`,
  dumps Task Manager writes on request, which nothing covers.
- **`WindowsServicingLogProvider`**: `Logs\DISM` (7.3 MB) and `System32\LogFiles\setupcln` (20.6 MB)
  are not covered. Same reasoning and tier as `CBS`.

### Build later, new

- **Windows Defender handler**: names `Windows Defender\LocalCopy` and `Support`. Windows' own
  route, but Microsoft does not document what `LocalCopy` holds, and both refused a listing
  unelevated. Measure elevated first.
- **Diagnostic Data Viewer** store under `Diagnosis\EventTranscript`: "can use up to 1 GB (by
  default)", and turning viewing off "clears the existing diagnostic data"
  ([viewer](https://learn.microsoft.com/en-us/windows/privacy/diagnostic-data-viewer-overview)). It
  exists only when the user turned viewing on, so the setting is the primary answer. **Feedback Hub
  Archive**: no Microsoft documentation found; only the handler vouches for it.
- **BranchCache**: `Clear-BCCache` "deletes all data in all data cache files and all hash cache files"
  ([Clear-BCCache](https://learn.microsoft.com/en-us/powershell/module/branchcache/clear-bccache)).
  Peer content, so Tier 1 like Delivery Optimization, but it matters only on managed machines.

### Settings Deguffer names and never changes

None of these is a cleanup; each is a setting with a cost, and Deguffer's rule for System
Protection's limit applies.

| Setting | Microsoft | Measured |
| --- | --- | --- |
| Reserved storage | "increases the likelihood that Windows updates can be downloaded and installed" ([storage reserve](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-storage-reserve?view=windows-11)) | The query refused unelevated |
| Hibernation file | `powercfg /h /type reduced` and `/h off` ([Compact OS](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/compact-os?view=windows-11)) | Hibernation off |
| Compact OS | `Compact.exe /CompactOS:always` (same page) | Windows had decided it was "not beneficial for this system" |
| Windows Installer baseline cache | `MaxPatchCacheSize`; setting it to 0 "does not affect files that have already been saved" ([MaxPatchCacheSize](https://learn.microsoft.com/en-us/windows/win32/msi/maxpatchcachesize)) | 174 MB of a 13.9 GB `Windows\Installer`. §9 stands |
| Defender scan history | `ScanPurgeItemsAfterDelay`, 15 days by default ([Set-MpPreference](https://learn.microsoft.com/en-us/powershell/module/defender/set-mppreference)) | Refused unelevated |

### Rejected

| Item | Reason |
| --- | --- |
| Defender definitions, `MpCmdRun -RemoveDefinitions` | A rollback for a bad update ([updates](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-updates)); used for space, it rolls protection back. Quarantine is never offered |
| AppX staged packages, `WindowsApps\Deleted` | Windows destages them itself, and "don't use the absence of a user registration as proof that Windows has already deleted every staged file" ([package lifetime](https://learn.microsoft.com/en-us/windows/msix/package/package-lifetime)) |
| `wsreset` | A repair, not a cleanup |
| Packages' `TempState` and `AC\INetCache` | Tier 1 by Microsoft's own words, but 3.6 MB and 4.1 MB measured, and Windows maintains them |
| Offline Files (CSC) | The only reset loses unsynchronised changes ([reinitialise](https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/re-initialize-offline-files-cache-database)), so it can hold the only copy of work |
| `SoftwareDistribution` | Updates waiting to install; the superseded part is the component store's. Stays protected |
| DiagTrack ETL logs, `wermgr -purgestores` | No Microsoft documentation found |
| Language packs and features on demand | `DISM /Remove-Capability` is uninstalling (§2) |

### `C:\Windows\SystemTemp` — a question, not a candidate

`TempRoots` excludes it because "nothing documents it as safe to empty". Windows' own *Temporary
Files* handler names it, with a seven-day `LastAccess` rule. A registration is not documentation, and
§9 keeps Deguffer out of servicing internals, so the exclusion stands, but the comment should record
that Windows' own cleanup does reach it, and an elevated measurement should say what is there.

---

## Corrections to what the repository already says

1. **Bun has eviction commands.** The first survey's long-tail table says "no eviction command as of
   early 2026". `bun pm cache rm` and `bun pm cache prune` exist ([pm](https://bun.com/docs/pm/cli/pm)).
2. **`scoop cleanup *` is an uninstall.** The same table lists it beside `scoop cache rm *`.
3. **`Optimize-VHD`'s refusal of sparse files is unsourced here.** The first survey's §11 and
   `VirtualDiskRoutes` say `Optimize-VHD` refuses a sparse, compressed or encrypted file. Microsoft's
   [cmdlet page](https://learn.microsoft.com/en-us/powershell/module/hyper-v/optimize-vhd) does not
   say so. Find the primary source, or reword the claim.
4. **`Optimize-VHD` needs Hyper-V rights.** Measured: the Hyper-V module refuses an unelevated user
   outside Hyper-V Administrators, so the WSL and Docker route that names it should say so.

Items 1 and 2 are corrected in place in [unreached-locations.md](unreached-locations.md). Items 3 and
4 change shipped text, and belong with the work that next touches the virtual disk report.

## Additions to the negative list

| Location | Why not |
| --- | --- |
| The `DownloadsFolder` Disk Cleanup handler | Empties the user's Downloads folder, with no age rule |
| `%LOCALAPPDATA%\Microsoft\WinGet\Packages` | Installed portable apps, in a folder named like a download cache |
| `.godot\export_credentials.cfg` | Export passwords and encryption keys, inside a folder Godot calls cache |
| Deno's `location_data` and `deno_history.txt` | `localStorage`, the default KV database and REPL history, removed by the tool's own `deno clean` |
| Bun's `<cache>\links` | The live global store projects link into |
| Any VM file by path: delta disks, `.avhdx`, `.vmss`, `.vmem`, `.vmsn`, `.vmrs` | The machine, its snapshot chain or its saved state |

## Patterns across the areas

- **Some tool commands destroy user data.** `deno clean`, `opam clean` with its defaults,
  `scoop cleanup`, `conan remove` and `bun pm cache rm` each go past the cache. §5.1 prefers a tool's
  command; it does not prefer one that deletes Tier 3 data. Each is recorded above with the narrower
  route.
- **Some caches are live stores.** Yarn Berry's Plug'n'Play cache, Bun's `links`, PDM's `packages`
  and Cabal's store are what projects run from. They need pnpm's reference-aware treatment or none.
- **A vendor's automatic rule is the best tier evidence.** JetBrains' 180-day rule, Gradle's 7-day
  rule, ReSharper's 30 days and Composer's cap each say what the vendor already deletes without
  asking. Where the vendor already deletes it, an offer is either redundant or Tier 1.
- **Hypervisors refuse to be listed unelevated.** Hyper-V answers nothing; the other two keep their
  records in the profile. A report row has to say why it is empty.

## Suggested order

Not a schedule: cost against value, given the machinery that exists.

1. **Godot `imported` and `shader_cache`** — the largest measured item, on the Unity provider's
   shape.
2. **Upgrade Discarded Files, `LiveKernelReportsPath`, `Logs\DISM`** — rows in existing providers.
3. **A run-wide refusal of registered virtual disks** — before any new virtualisation report.
4. **VMware, then VirtualBox, then Hyper-V reports**, and owner records for Podman and Rancher.
5. **Superseded JetBrains system directories** and **Vagrant `prune`**.
6. **Electron, electron-builder, TypeScript, Yarn 1, Scoop, Conan, Bun, Deno** — one class each.
7. **The `CACHEDIR.TAG` table** and **`.dart_tool`**.
8. **Lightroom previews**, once its Adobe pages are read and a catalog is available to test against.
9. **The Search index**, once `Reset` is measured.

## Open questions this survey raises

1. Does the data-driven Disk Cleanup handler run its `PreCleanupString` and `CleanupString` when
   Deguffer hosts it directly? Every handler-based entry above depends on the answer.
2. Does `ISearchCatalogManager::Reset` need elevation, and does it shrink `Windows.db`?
3. Does a running Godot editor, or a running JetBrains IDE, hold files open in the folders proposed
   for it? Each needs a live-project veto, and neither is established.
4. What does an elevated, read-only pass find behind the folders that refused a listing: Defender's
   `LocalCopy` and `Support`, `Diagnosis`, `SystemTemp`, BranchCache and `WindowsApps`?
