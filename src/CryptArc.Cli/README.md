# CryptArc.Cli

Command line front end. Thin by design: it parses arguments, calls `Core` and
formats output. No algorithm logic lives here.

## Commands

| Command | Does |
|---|---|
| `analyze <file>` | entropy, repeat and text ratios, CRC-32, recommended codec |
| `test <file>` | round-trips the file through every registered codec and prints size, ratio and throughput |
| `pack <archive> <path>...` | creates an encrypted archive from files and folders; refuses to replace an existing file |
| `unpack <archive> [directory]` | restores every file, into the current directory by default; never overwrites |
| `list <archive>` | size, stored size, modification time and path of every file |
| `verify <archive>` | decrypts, decompresses and checks everything, writing nothing |
| `--help` | usage |

The archive commands ask for the password interactively (twice for `pack`), or
read one line from redirected standard input.

## Running

```bash
dotnet run --project src/CryptArc.Cli -c Release -- analyze README.md
dotnet run --project src/CryptArc.Cli -c Release -- test README.md
dotnet run --project src/CryptArc.Cli -c Release -- pack docs.cryptarc docs README.md
dotnet run --project src/CryptArc.Cli -c Release -- unpack docs.cryptarc restored
```

The `--` matters: everything after it goes to the program rather than to
`dotnet`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | runtime failure, a codec round-trip mismatch, a wrong password or a damaged archive |
| 2 | bad usage |

`test` returning 1 is how a corrupted codec shows up in a script or in CI.
