# Release roadmap

Canonical repository: [github.com/windfastt/ultracompress](https://github.com/windfastt/ultracompress)

## Shipped (`main`)

| Version | Scope | Status |
|---------|--------|--------|
| **v0.3.1** | WinUI tray, engine pipe IPC, job queue UI, Serilog, product marketing site, benchmark smoke tests, MIT, CI on `windows-latest` | **On `main`** (`6126e3b`, PR #15 merged) |

## In flight (open PRs → `main`)

| PR | Branch | Merge after |
|----|--------|-------------|
| #1 | `feat/core-schema` | — (foundation) |
| #2 | `feat/7z-backend` | #1 |
| #5 | `feat/zstd-backend` | #1 |
| #3 | `feat/engine-service` | core |
| #8 | `feat/recipe-db` | #1 |
| #9 | `feat/advisor-v1` | #1 |
| #11 | `feat/probe` | core |
| #12 | `feat/golden-ci` | backends |
| #13 | `feat/distributed-grpc` | engine (#3) |
| #14 | `feat/brotli-deflate` | core |
| #6 | `feat/tournament` | backends + advisor |
| #10 | `feat/installer-wix` | engine + tray |
| #16 | `feat/shell-ext` | core |

## Public site (GitHub Pages)

- **Product marketing site** — [windfastt.github.io/ultracompress](https://windfastt.github.io/ultracompress/) (SaaS/product positioning, not a repo file browser)
- Hero, features, install/download CTA; developer docs linked from GitHub only
- Static deploy pipeline (peaceiris → `gh-pages`); also runs on `docs/**` branch pushes

## v0.4.0 — integrated desktop (target)

- [ ] All feature PRs above merged; `main` green
- [ ] Git tag `v0.3.1` (or `v0.4.0` if semver bump) + GitHub Release with MSI artifact
- [ ] WiX bundle installs Engine + Tray + VC++ redist
- [ ] Golden CI on Windows for zstd + 7z (+ brotli where bundled)
- [ ] Docs site reflects install + CLI quickstart

## v1.0.0 — ultra-compressor product

- [ ] Tournament / Laboratory profiles wired end-to-end
- [ ] SQLite per-file recipes + settings export
- [ ] Distributed worker fleet (optional second machine)
- [ ] Explorer shell extension (signed package path documented)
- [ ] PAQ / experimental codecs: download-on-first-use only

## Communication guidelines

- User-facing README and Pages avoid interim or personal fork references
- Operator and CI runbooks stay out of the marketing site