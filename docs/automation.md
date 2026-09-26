# Unattended build workflow

Repository: `mikegotl/Real-Estate-Listing-SAAS`. The owner-provided plan is in [build-plan.md](build-plan.md). It assigns Codex implementation and Claude independent review. The existing `main` branch already has a Week 1 foundation, but its full run criterion needs validation. All milestone statuses are initially pending.

## Setup from an iPad or Mac

1. Review and merge the PR that adds this plan and `AGENTS.md`.
2. For implementation, connect this repository to **Codex cloud**. Start one task using the implementation prompt below. For recurring work, create a cloud or web scheduled task that can access this GitHub repository; a desktop local project schedule needs the host computer powered on. Confirm the scheduled task has repository write access before expecting a branch or PR. Run one task manually first and inspect the result.
3. At [Claude Code Routines](https://claude.ai/code/routines), create a **cloud** routine on this same repository. Use the reviewer prompt below, triggered by a new or updated pull request if available on your account. Select only the needed GitHub connector and repository. A routine consumes Claude subscription usage and runs with the MacBook closed.
4. Review build results and the Claude findings on your iPad. Ask Codex to repair defects on the existing PR. Merge only when acceptance evidence is satisfactory; this unlocks the next milestone on `main`.

## Codex implementation task prompt

```text
Work in mikegotl/Real-Estate-Listing-SAAS. Read AGENTS.md, ARCHITECTURE.md, README.md and docs/build-plan.md. Inspect the main branch, open PRs, and relevant code.

Choose only the first pending milestone whose predecessors are accepted. Week 1 has an existing foundation; first validate its documented completion criterion and fix only concrete failures. If an open PR already covers this milestone, work on that PR rather than creating a duplicate. Follow its Codex Prompt and acceptance criterion in docs/build-plan.md. Keep the existing architecture unless the milestone explicitly allows a change.

Implement one reviewable vertical slice. Run the relevant builds and tests, including dotnet build and dotnet test; report commands and results truthfully. Verify the milestone's workflow when the environment supports it. For missing credentials or services, use test doubles for automated tests and clearly record the blocked live check. Never fabricate production API results or listing facts.

Create or update a branch and PR for this milestone. Include the acceptance checklist, test evidence, decisions, blockers and any manual check the owner must do. Do not merge, deploy, alter billing plans or spend on external AI/video services. Do not advance to the next milestone until the owner merges the PR and marks it accepted in docs/build-plan.md.
```

## Claude review routine prompt

```text
Review new or updated PRs for mikegotl/Real-Estate-Listing-SAAS against AGENTS.md, ARCHITECTURE.md and the corresponding milestone in docs/build-plan.md. Check implementation, tenant isolation, security, data grounding, reliability, cost controls and tests. For Week 1 and Week 7 also review architecture and schema choices; for Week 16 perform the full pre-pilot review in the playbook.

Report specific actionable findings with file paths, severity, consequences and suggested fixes. Check whether acceptance evidence is present. Do not rewrite the feature, merge the PR, deploy, or start the next milestone. If no relevant open PR exists, do nothing and report that clearly. Avoid posting duplicate review comments on unchanged code.
```

## Operating rules

- An unattended run may complete a task while you are away, but the 16 milestones will not safely progress through unmerged PRs: every new cloud run starts from the repository state it sees. Review and merge at each gate.
- Keep the prompt source in the repo rather than only in an iPad document. Do not paste keys into either scheduled prompt. Configure credentials in each service's secure settings when a later milestone truly needs them.
- Scheduled runs and cloud coding sessions have plan limits. Check their history for actual command and task success; a green run status is not proof that the feature passed acceptance.
