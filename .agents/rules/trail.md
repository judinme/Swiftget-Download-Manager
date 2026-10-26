# Trail Workspace Guidelines

For ANY task in this workspace:
- **Always query Trail first**: Use `trail ask "<task>" --source` to locate definitions, find where code lives, and understand data flows before grepping or manually opening files.
- **Trace call edges**: Run `trail callers <symbol> --depth 2` before modifying signatures, renaming, or deleting functions to verify blast radius.
- **Save learnings**: When solving complex issues, architectural decisions, or applying user instructions, record them with `trail note --title "<fact>"`.
- **Pre-commit checks**: Run `trail check` before concluding milestones or commits.
- **Report token savings**: At the end of each turn using Trail, report the total estimated tokens saved using `🌱 trail saved ~N tokens this turn`.
