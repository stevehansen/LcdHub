# LcdHub: documentation

This folder holds the **per-domain documentation layer**. Each major domain gets one deep living
spec, paired with a thin agent-priming skill. For build, architecture and conventions, see
[`../CLAUDE.md`](../CLAUDE.md). For users, see [`../README.md`](../README.md).

## Domain index

| Domain | Living spec | Priming skill | Governing issues |
|---|---|---|---|
| Panels | [`panels.md`](panels.md) | [`.claude/skills/panels/SKILL.md`](../.claude/skills/panels/SKILL.md) | — |
| Scheduling | [`scheduling.md`](scheduling.md) | [`.claude/skills/scheduling/SKILL.md`](../.claude/skills/scheduling/SKILL.md) | — |
| Producer protocol | [`producer-protocol.md`](producer-protocol.md) | [`.claude/skills/producer-protocol/SKILL.md`](../.claude/skills/producer-protocol/SKILL.md) | — |

## Other references

- [`../UBIQUITOUS_LANGUAGE.md`](../UBIQUITOUS_LANGUAGE.md): the canonical glossary. Specs link
  *down* into it.
- [`../CLAUDE.md`](../CLAUDE.md): repo guidance (build, conventions, doc-sync rules).

## Adding a new domain

Each domain gets two files, split by audience:

- A deep, human-facing living spec at `docs/<domain>.md`.
- A thin, agent-facing priming skill at `.claude/skills/<domain>/SKILL.md`, which links *down* to
  the spec.

Use lowercase filenames.

**Living-spec sections:**

1. Title and purpose
2. Status
3. What it is
4. Core entities and relationships
5. Invariants and rules
6. Key files
7. Gotchas
8. Executable references
9. Links

**Priming-skill shape:**

1. Frontmatter: `name`, plus a `description` with trigger phrases and an explicit "Not for …"
   exclusion.
2. One line on what the domain is, with a link to the spec.
3. The invariants to get right.
4. Key files.
5. Gotchas.

**Same-PR sync rule:** a change to a domain's behavior updates its living spec in the same PR as
the code change, never as a follow-up. If the change alters a load-bearing invariant, update the
priming skill too.

**The iron rule:** the skill links, it never duplicates.
