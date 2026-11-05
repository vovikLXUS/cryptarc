# Agent skills

Skills that Claude Code, OpenAI Codex and Google Antigravity load on demand.
They were picked from [skills.sh](https://skills.sh) and installed with the
[`skills` CLI](https://github.com/vercel-labs/skills). The files are copies of
the upstream skills. Do not edit them by hand: an edit is lost on the next
update. Put project-specific overrides in the root `AGENTS.md`.

## Where the files live

- `.agents/skills/<name>/` holds the files. Codex and Antigravity read this
  directory directly.
- `.claude/skills/<name>` is a symlink to the same directory for Claude Code.
  On Windows, Git creates real symlinks only when `core.symlinks` is enabled
  (Developer Mode). Without it, Claude Code does not see these skills.
- `skills-lock.json` in the repository root records the source and hash of each
  skill.

## Skills

| Skill | Use it for | Source | License |
|---|---|---|---|
| `code-review` | Review a branch or a diff against the repository's documented rules and the task it implements | [mattpocock/skills](https://github.com/mattpocock/skills) | MIT |
| `git-commit` | Stage one logical change and commit it with a Conventional Commits message | [github/awesome-copilot](https://github.com/github/awesome-copilot) | MIT |
| `create-pull-request` | Push a branch and open a GitHub pull request with `gh`, filling in the PR template | [cline/cline](https://github.com/cline/cline) | Apache-2.0 |
| `microbenchmarking` | Write and run BenchmarkDotNet benchmarks, including naive-versus-optimised comparisons | [dotnet/skills](https://github.com/dotnet/skills) | MIT |
| `analyzing-dotnet-performance` | Scan C# hot paths for allocation and API-usage anti-patterns | [dotnet/skills](https://github.com/dotnet/skills) | MIT |
| `vectorization` | Write or review SIMD code (`Vector128<T>` and wider), for example in codec inner loops | [dotnet/skills](https://github.com/dotnet/skills) | MIT |
| `csharp-xunit` | Write xUnit facts and theories | [github/awesome-copilot](https://github.com/github/awesome-copilot) | MIT |
| `csharp-docs` | Write XML documentation comments on public members | [github/awesome-copilot](https://github.com/github/awesome-copilot) | MIT |

The license texts are in [`LICENSES/`](LICENSES/).

These are the upstream commits at install time (2026-11-05):

| Repository | Commit |
|---|---|
| mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` |
| github/awesome-copilot | `96543145e9cb97711020b57b9d88f968062c4fc7` |
| cline/cline | `d3ecc4890e8f9bcb77182b96c77fa90575e8e145` |
| dotnet/skills | `9837da685079d576feb27f9dd4f87631d3d94107` |

## Managing skills

```bash
npx skills list                                   # installed skills
npx skills update -p -y                           # pull upstream changes
npx skills add <owner/repo> -s <skill> \
  -a claude-code -a codex -a antigravity -y       # add one more
npx skills remove <skill> -y
```

Read a skill in full before you add it. A skill runs with the agent's
permissions.
