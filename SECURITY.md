# Security policy

Deguffer deletes directories on your machine, and it can be relaunched with administrator rights to
read the volume's master file table. That is a larger surface than most desktop tools have, so
security reports are very welcome.

It runs entirely locally. It makes no outbound network requests, it has no server, and it sends
nothing anywhere. What it does do is run your installed toolchains as subprocesses to ask them where
their caches are, read your filesystem, and delete the directories you confirm.

## Reporting a vulnerability

**Report a vulnerability privately. Do not open a public issue.**

1. Open the [Security tab](https://github.com/BootBlock/Deguffer/security) of this repository.
2. Click **Report a vulnerability**.
3. Describe the issue, its impact, and how to reproduce it.

You will get a response as soon as reasonably possible. Please allow reasonable time for a fix
before disclosing publicly.

**Redact your paths before you write the report.** Deguffer's output carries your user name, your
machine name and your directory layout. A private advisory becomes public when it is published, so
replace anything like `C:\Users\yourname\...` with `C:\Users\<user>\...`, and check any screenshot
for the same. Describing a directory by what owns it is usually clearer than the literal path, and
always safer.

## What counts as a vulnerability here

Report privately if you have found a way for **someone or something other than the user** to
influence what Deguffer removes or reads:

- A path that a third party controls reaching a delete, through a junction, a symbolic link, a
  reparse point, or a crafted directory name.
- A provider that can be steered onto a directory outside the cache it owns, including by a
  malicious `.npmrc`, `gradle.properties` or equivalent config the provider reads.
- Anything that lets a subprocess Deguffer invokes run code the user did not intend, such as a tool
  resolved from a writable directory earlier on `PATH`.
- Misuse of the elevated path: work done with administrator rights that did not need them, or a
  privilege that outlives the operation.
- A real filesystem path, user name or machine name written somewhere it can escape the machine.

## What is an ordinary bug, not a vulnerability

**Deguffer removing or offering the wrong directory, with no attacker involved, is a bug report.**
It is the most important kind, and it has its own
[issue template](https://github.com/BootBlock/Deguffer/issues/new/choose). File it publicly, with
the paths redacted, so it can be fixed in the open.

The dividing line is whether someone other than the user can trigger it.

## What to include

- Which provider or subsystem is affected, and whether the app was elevated.
- The Windows version and the app version, shown on the About screen.
- The steps to reproduce, with every path redacted.
- A minimal proof of concept, if you have one. Do not include one that destroys real data.

## Supported versions

This is an actively developed project. Only the latest `main`, and the most recent release built
from it, is supported. Fixes land on `main`, so please retest there before reporting.
