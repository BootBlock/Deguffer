# Contributing

Thank you for wanting to help. Suggestions, bug reports and ideas are welcome, and they are the
most useful thing anyone can send.

## Pull requests are never accepted

**Deguffer does not accept external pull requests.** A pull request opened by anyone who is not a
collaborator on this repository is closed automatically, without being read. That is not a
judgement on the change or on the person who sent it (but it's gonna be a bot, though, innit?). Deguffer deletes directories on other
people's machines, so every line of it has to be understood and verified by the people who
maintain it, against the safety model in the
[specification](docs/todo/_spec.md). Reviewing a patch to that standard costs more than writing
the change does, so the project does not take patches at all.

## Open an issue instead

Please [open an issue](https://github.com/BootBlock/Deguffer/issues/new/choose) with a short
summary of the change you need. A line or two is enough:

- What you expected Deguffer to do, and what it did instead.
- For a new cache or location, where it lives and what is lost when it is deleted.

That description is what the work actually needs. The code is the easy part.

**Do not include real paths, usernames or machine names.** Redact them to `C:\Users\<user>\...`,
and crop any screenshot that shows them.

For a security vulnerability, follow [SECURITY.md](SECURITY.md) and report it privately. Do not
open a public issue.
