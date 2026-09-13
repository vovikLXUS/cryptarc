# CryptArc.Cli

Command line front end. Thin by design: it parses arguments, calls `Core` and
formats output. No algorithm logic lives here.

## Commands

| Command | Does |
|---|---|
| `analyze <file>` | entropy, repeat and text ratios, CRC-32, recommended codec |
| `test <file>` | round-trips the file through every registered codec and prints size, ratio and throughput |
| `--help` | usage |

Planned: `pack`, `unpack`, `list`, `verify` — waiting on the container.

## Running

```bash
dotnet run --project src/CryptArc.Cli -c Release -- analyze README.md
dotnet run --project src/CryptArc.Cli -c Release -- test README.md
```

The `--` matters: everything after it goes to the program rather than to
`dotnet`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | runtime failure, or a codec round-trip mismatch |
| 2 | bad usage |

`test` returning 1 is how a corrupted codec shows up in a script or in CI.
