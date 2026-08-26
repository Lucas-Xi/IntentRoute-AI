# Codex for Open Source readiness notes

This document records project facts for a future application. It is not an application form and makes no claim of acceptance or eligibility.

## Public-interest case

IntentRoute AI addresses an understandable open-source maintenance problem: Windows applications without proxy settings need inspectable per-process routing, while hand-authoring safe process/domain rules is difficult. The project combines optional OpenAI/local-Ollama assistance with a deterministic local validation and human-confirmation boundary. It favors a small control plane, a documented external data plane, traceable tagged releases, and explicit security boundaries.

## Repository evidence prepared

- OSI-style MIT license for project-owned code.
- Public source, issue templates, contributor guide, code of conduct, support and security policies.
- Automated Windows tests, builds, vulnerability checks, a packaged-WPF launch/close smoke gate, and release archives with SHA-256 checksums.
- Architecture and threat-model documentation.
- Honest supported/unsupported feature table.
- Maintainer instructions that prohibit secrets, fake telemetry, silent semantic fallbacks, and bundled network binaries.
- Provider-independent AI contracts, mocked provider tests, strict structured-output parsing, and public data-boundary documentation.

## Facts that must remain honest in a future application

- v0.2.0 is a new preview and does not yet have demonstrated broad adoption.
- sing-box supplies the TUN data plane and is a separate project.
- OpenAI and Ollama supply optional model inference; AI does not autonomously control the data plane.
- Current maintenance is primarily by one maintainer unless repository activity later demonstrates otherwise.
- Usage, contributors, issue response, releases, and downstream impact must be taken from current public GitHub evidence at application time; never estimate or inflate them.

## Evidence to gather before applying

- Stable public repository and successful CI/release links.
- Real issues or discussions showing user need and maintainer response.
- Documented compatibility results across supported Windows and sing-box versions.
- Current stars, forks, contributors, downloads, and release cadence from GitHub.
- Concrete explanation of how Codex credits would improve tests, security review, documentation, triage, and contributor throughput.

If the project still does not meet the published "widely used" profile, a future application should say so directly and explain any other public-interest value without presenting readiness work as adoption.
