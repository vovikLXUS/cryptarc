# Reference archives (Project 2)

The instructor's SARC v1 acceptance set from the Project 2 team package
(`reference/` in section 6.1 of the assignment). These files are not ours and are
never regenerated or edited: they are the oracle that `FORMAT_SPEC_P2.md` §9
describes.

| File | Checks | Expected |
|---|---|---|
| `ref_01_single_file.arc` | basic parsing, canonical codes, LZ77 with one repeat (the §8 control example) | restores |
| `ref_02_empty_file.arc` | a zero-byte file | restores |
| `ref_03_degenerate.arc` | a one-symbol alphabet, 5000 bytes | restores |
| `ref_04_seven_groups.arc` | one file from each of the seven corpus groups | restores |
| `ref_05_no_compression.arc` | already-compressed data, `NO_COMPRESSION` | restores |
| `ref_06_two_volumes.part0…2.arc` | three volumes, files joined across them | restores |
| `bad_01` … `bad_06` | tampered byte, tampered tag, truncation, magic, reserved, CRC | the code in `expected_failures.json` |

Each `ref_*.json` lists the files with their sizes and CRC-32; the password of
every archive is `algo2026` (it is in the manifests). `SarcReferenceArchiveTests`
runs the whole set on every `dotnet test`, so CI fails if the reader stops
conforming.

By hand:

```bash
echo algo2026 | dotnet run --project src/CryptArc.Cli -c Release -- sarc-unpack restored team/reference/ref_06_two_volumes.part*.arc
echo algo2026 | dotnet run --project src/CryptArc.Cli -c Release -- sarc-verify team/reference/bad_05_reserved.arc
```

**Provenance.** Copied unchanged (SHA-256 checked) from the issued package as
published in team T01's repository, `HrychanokIhor/SmartSecureFileArchiver`,
commit `b944a08`. The archives are not built from any team's corpus — the files in
`ref_06` share names with T01's corpus but not contents — so the set appears to be
the same for every team. If our own package differs, replace these files with ours.

Only `reference/` is kept here. The corpus, the training set and `team_config.json`
are generated from each team's own seed and must come from our package, not
another team's.
