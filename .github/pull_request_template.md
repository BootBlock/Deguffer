<!-- What does this change do, and why? Describe the what and the why, never the plumbing. -->

> **Deguffer does not accept external pull requests.** One opened by anybody who is not a
> collaborator is closed automatically. Please
> [open an issue](https://github.com/BootBlock/Deguffer/issues/new/choose) instead, and see
> [CONTRIBUTING.md](https://github.com/BootBlock/Deguffer/blob/main/CONTRIBUTING.md).

## Summary

## Verification

<!-- G8: "verified" means observed, not compiled. Say what you ran and what you saw. -->

## Checklist

- [ ] `dotnet build Deguffer.sln` and `dotnet test Deguffer.sln` both pass, and I read the output.
- [ ] The behaviour change has a test that **failed before the fix**, for the right reason.
- [ ] If this changes what gets deleted, the test asserts the **negative** (§5.6): the tool root,
      the unrecognised siblings and anything in Tier 4 all survived.
- [ ] If this changes tier classification, a test covers the **unrecognised** child landing in
      Tier 4 (§5.2).
- [ ] If this touches path handling, a test exercises a path past `MAX_PATH` (§6.3).
- [ ] The tests run through `FakeUserEnvironment` and the `IProcessRunner` / `IProcessInspector`
      seams, not against a real npm, NuGet or Gradle install.
- [ ] Where the change has a runtime surface, I drove the app and observed it.
- [ ] No secrets, real filesystem paths, profile names or machine names in the diff, self-audited
      with `git diff`. Any screenshot is cropped or synthetic.
- [ ] The engineering gates hold: one responsibility per file (G1), no god objects (G2), no
      AI-trope code (G3), `EnumerateX` and bounded parallelism (G4), singletons and
      `static readonly` lookups reused (G5).
- [ ] If this adds or changes a rule in `CLAUDE.md`, `AGENTS.md` indexes it.
