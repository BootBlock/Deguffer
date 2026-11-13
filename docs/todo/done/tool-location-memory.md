# Remembering where a tool keeps its cache

> **Status:** ✅ COMPLETE — every stage below landed for #447. PlatformIO joins under #1053 once
> #591 has changed what its query reads.

## The decision

A tool's answer about **where** it keeps something may outlive the pass that asked, for as long as
everything the tool read to give it is unchanged. An answer about a cache's **contents** stays per
pass: `conda clean --dry-run`, `poetry cache list`, `pio pkg prune --dry-run` and DISM's figures are
asked again every time. DISM's component-store analysis stays out until its own key is designed,
because Windows Update changes the store on its own schedule.

Before this, every preview, every rescan, every Explore scan and every duplicate search started each
of these tools again (`scan-progress.md` §4: conda about 0.9-1.0 s, npm 0.4-0.65 s, pip about
0.45 s, Poetry's `config` 563 ms, Yarn 151 ms and 250 ms).

## The design

- **`ToolInputs`** is what a provider declares its tool reads for one answer: files, folders whose
  listing matters, and the environment the tool is started with. A declaration may depend on the
  answer itself, because some tools name the files they read (`npm config get cache userconfig
  globalconfig prefix`), and a file named by another file's content is covered by stamping the file
  that names it.
- **`ToolFingerprint`** is what those inputs held when read: for each path, whether it was there,
  its size, and its creation and last-write times, and the whole environment block the tool is
  given. The whole block, not a list of names, because npm, pnpm and Yarn expand any `${VAR}`
  written in their configuration, so no list of names can be complete. A path Windows refuses to
  describe makes the fingerprint incomplete, and an incomplete fingerprint never matches.
- **`ToolAnswerMemory`** holds each remembered answer with its inputs and fingerprint, one memory per
  `IProcessRunner`, so the answers the real runner received are shared process-wide (every Explore
  build constructs fresh providers) and a test's fake runner never sees another's. A provider reads
  it inside its `PassMemo`, so a pass reads the inputs once to serve a trusted answer, or before and
  after the tool where it asks.
- **An answer is trusted only once its inputs read the same before the tool started and after it
  answered, and the tool gave the same answer as last time.** A file edited, deleted, or moved in or away while the tool runs shows as a difference
  between the two readings, which nothing read afterwards can show: a deleted file leaves no time,
  and a moved one keeps its old times. The inputs read beforehand are those the last answer named, so
  the first answer to a question is held untrusted and the next ask confirms it.
- **What is never trusted:** a failed or cancelled ask; an answer the tool did not give (the
  documented default after a failed query, `AskedLocation.Answered` false); an answer whose inputs
  could not all be read; and an answer whose inputs were written at or after the moment the tool was
  started, less two seconds for a file system that keeps coarse times, where a write during the ask
  can keep the time it had before.
- **Rows that delete paths themselves** (Poetry's `artifacts`, Yarn Berry's `<globalFolder>\cache`)
  ask afresh when they plan, and store the fresh answer. Only their Explore declarations and their
  `CleanedPlacesAsync` read the memory. A missed input there cannot move a deletion (§5.2).
- **Rows that run the tool's own command** (§5.1) may plan from the memory: a missed input there only
  misstates the figure.

## Inventories

Every query runs in `%LOCALAPPDATA%\Deguffer\tool-working-directory` (`ProcessRunner`), so "each
folder up" below means that folder and every parent of it, up to the drive root. Every query
inherits Deguffer's own start-up environment, which the fingerprint compares whole.

| Tool | Executable and code | Configuration | Named by the answer |
| --- | --- | --- | --- |
| conda | the executable; `<root>\conda-meta` listing (a conda update) | `C:\ProgramData\conda\{.condarc, condarc, condarc.d}`; the same in `%CONDA_ROOT%`, `%XDG_CONFIG_HOME%\conda`, `~\.config\conda`, `~\.conda`, `<root>` and `%CONDA_PREFIX%`; `~\.condarc`; `%CONDARC%`. A `condarc.d` is listed. | `root_prefix` |
| Poetry | the executable | `config.toml` and `auth.toml` in `%POETRY_CONFIG_DIR%` or `%APPDATA%\pypoetry`; `pyproject.toml` and `poetry.toml` in each folder up | |
| pip | the launcher; its interpreter's prefix and `Lib\site-packages` listing; `pyvenv.cfg` | `C:\ProgramData\pip\pip.ini`; `~\pip\pip.ini`; `%APPDATA%\pip\pip.ini`; `<prefix>\pip.ini`; `%PIP_CONFIG_FILE%` | |
| npm | the shim; `node.exe` beside it and on `PATH`; `node_modules\npm\package.json` and `npmrc` beside the shim and under the prefix | `.npmrc`, `package.json` and `node_modules` in each folder up | `userconfig`, `globalconfig`, `prefix` |
| pnpm | the shim; `node.exe`; `node_modules\pnpm\package.json`; Corepack's home listing | `%LOCALAPPDATA%\pnpm\config\{rc, config.yaml, auth.ini}` (or under `%XDG_CONFIG_HOME%`); `~\.npmrc`; `.npmrc`, `pnpm-workspace.yaml` and `package.json` in each folder up | `globalconfig` (asked beside `store path` on a miss) |
| Yarn 1 | the shim; `node.exe`; `node_modules\yarn\package.json`; Corepack's home listing | `.yarnrc`, `.npmrc` and `package.json` in each folder up; `~\.yarnrc`, `~\.npmrc`; `%LOCALAPPDATA%\Yarn\Config`; `~\.config\yarn`; `%YARN_CONFIG%` | `yarn-path` (asked beside `yarn -v`) |
| Yarn Berry | as Yarn 1 | `.yarnrc.yml` (or `%YARN_RC_FILENAME%`), `package.json` and `yarn.lock` in each folder up; `~\.yarnrc.yml` | `yarnPath` (asked beside `yarn -v`) |

PlatformIO joins under #1053, once #591 has changed what its query reads.

## Stages

1. The shared types and their tests: the first answer is held untrusted, the next ask confirms it,
   and a confirmed answer is served while nothing changed; each kind of input changed (a file
   written, created or deleted, a folder's listing changed, a variable set, the executable replaced)
   asks again; a refused input, an unanswered ask, an input written during the ask and an answer
   that differs from the one it would confirm are not trusted; a second set of providers on the same
   runner shares the answer.
2. conda. 3. Poetry. 4. pip. 5. npm. 6. pnpm. 7. Yarn 1 and Yarn Berry. Each adopter has one test
   per declared input kind, a test that a set of providers on the same memory asks nothing once the
   answer is confirmed, and the §5.6 negative of its plan when the location moved between two
   passes. Each row that asks its tool when Explore builds its policy (conda, Poetry, pnpm and both
   Yarn rows) has a test that a policy build asks nothing once the answer is confirmed; npm's and
   pip's Explore declarations ask no tool.
8. `CLAUDE.md` G4, `docs/cache-locations.md` (each adopter's inputs, citing the tool's
   documentation) and the comments that said the answer must not outlive a pass.

## As built

- The first draft checked the folders above each input for a change during the ask instead of
  reading the inputs beforehand. On a real machine that refused nearly every answer, because tools
  write in those folders as they answer: pnpm leaves and removes an entry in its home folder and in
  the folder it runs in, and Deguffer writes its own estimates beside the tools' working folder
  during a pass. Measured over three planning passes, only pip's answer was ever served. A listed
  folder is now stamped as there without its own times, since its entries are read in full.
- A folder listing gives only the names of its entries, and each entry is stamped by its own
  attribute read. The size and times a listing reports are NTFS's copy in the parent's index, which
  lags the entry: a folder that just gained an entry lists with its old time.
- conda declares every search-path entry as a listed folder, because conda reads any entry that is a
  folder as the YAML files in it, and expands a variable's value as conda does (once, then a leading
  `~`, then a relative path from the tools' working folder).
- pip finds `sys.prefix` from the interpreter its launcher names (`DistlibLauncher`). A launcher it
  cannot read declares an unreadable input, so that answer is asked every pass.
- Yarn 1 also declares `~\.yarnrc.yml`, which it reads beside every `.yarnrc`. Both Yarn rows share
  one `yarn -v` question.
- pip's comments name a `%LOCALAPPDATA%\pip\pip.ini` pip never reads: #1054.
