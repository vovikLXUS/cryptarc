# CryptArc.Cli

- **No logic here.** Anything worth testing belongs in `Core`. This project
  parses arguments, calls in, and prints.
- **Iterate over `CodecRegistry`**, never hard-code a list of algorithms — new
  codecs must appear in the output for free.
- Errors go to `stderr` with a non-zero exit code; results go to `stdout`.
  Keep the exit codes as documented (0 ok, 1 failure, 2 usage) — scripts and CI
  depend on them.
- Timing printed by `test` is indicative only. Real numbers come from
  `bench/CryptArc.Bench`; do not put CLI timings in the report.
- Argument parsing is hand-rolled on purpose. If it outgrows that, add
  `System.CommandLine` — and write an ADR for the dependency.
