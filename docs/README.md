# docs

Long-form design documents. Short-form notes live next to the code they describe.

| File | Contents |
|---|---|
| `container-format.md` | the `.cryptarc` on-disk format: header, tables, blocks, trailer, key derivation |

Root-level companions:

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — layers, abstractions, data
  structures per algorithm, where the optimisation work lives. *What the system
  is.*
- [`../ADR.md`](../ADR.md) — architecture decision records. *Why it is that way.*
- [`../AGENTS.md`](../AGENTS.md) — working rules for the repository, read by
  Claude Code, Codex and Antigravity.

## Keeping the format document honest

`container-format.md` is a specification, not a description of the code: it was
written before the container existed. When the implementation and the document
disagree, one of them is a bug — decide which, fix it, and bump
`formatVersion` if the layout changed.
