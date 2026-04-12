# Agent Teams — Master Reference Guide

> Source: https://code.claude.com/docs/en/agent-teams
> Last reviewed: 2026-03-23
> Requires: Claude Code v2.1.32+, `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`

---

## Quick Decision: Agent Teams vs. Subagents vs. Single Session

| Situation | Use |
|-----------|-----|
| Tasks can run independently with NO inter-agent communication needed | Subagents (cheaper) |
| Tasks need teammates to share findings, challenge each other, coordinate | **Agent Teams** |
| Sequential work, same-file edits, many dependencies | Single session |
| Research/review where parallel exploration adds value | **Agent Teams** |

**Key difference:** Subagents only report back to the caller. Agent teammates message *each other* directly and share a task list.

---

## Enabling Agent Teams

Set in `.claude/settings.local.json` (project-local, not committed):

```json
{
  "env": {
    "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS": "1"
  }
}
```

Or globally in `~/.claude/settings.json`. Already configured in this project.

---

## Architecture

```
Lead (main Claude Code session)
  ├── Task List (shared, file-locked)
  │     ├── Task A [pending / in-progress / completed]
  │     ├── Task B [depends on A]
  │     └── Task C
  ├── Teammate 1 (own context window)
  ├── Teammate 2 (own context window)
  └── Teammate N (own context window)
        └── Mailbox (bidirectional messaging between any agents)
```

**Storage locations:**
- Team config: `~/.claude/teams/{team-name}/config.json`
- Task list: `~/.claude/tasks/{team-name}/`

The `config.json` has a `members` array with each teammate's name, agent ID, and type — teammates can read this to discover each other.

---

## Starting a Team

Just describe the task and structure in natural language:

```
Create an agent team with 3 teammates:
- One for X
- One for Y
- One for Z
Use Sonnet for each teammate.
```

Claude will:
1. Create the team and task list
2. Spawn teammates with their spawn prompts
3. Coordinate work
4. Clean up when done (ask it to)

---

## Display Modes

| Mode | How | When |
|------|-----|------|
| `in-process` (default outside tmux) | All teammates in one terminal; Shift+Down to cycle | Any terminal |
| `tmux` / split panes | Each teammate in its own pane | Inside tmux session or iTerm2 |
| `auto` | Uses split panes if already in tmux, otherwise in-process | Default |

**Override in settings:**
```json
{ "teammateMode": "in-process" }
```

**Or per-session:**
```bash
claude --teammate-mode in-process
```

**Navigation (in-process):**
- `Shift+Down` — cycle through teammates
- `Enter` — view teammate session
- `Escape` — interrupt current turn
- `Ctrl+T` — toggle task list

---

## Controlling the Team

### Assign tasks
```
Ask the security teammate to review src/auth/ and the performance teammate to profile the API layer.
```

### Talk to a teammate directly
In in-process mode: Shift+Down to that teammate, then type. In split-pane: click into the pane.

### Require plan approval before implementation
```
Spawn an architect teammate to refactor the auth module. Require plan approval before they make any changes.
```
The lead reviews the plan and approves/rejects with feedback. Teammate stays in plan mode until approved.

### Force the lead to wait
```
Wait for your teammates to complete their tasks before proceeding.
```

### Shut down a teammate
```
Ask the researcher teammate to shut down.
```
Teammate can approve (graceful exit) or reject with explanation.

### Clean up the team
```
Clean up the team.
```
**Always use the lead for cleanup.** Teammates should not run cleanup — their team context may not resolve correctly, leaving resources in an inconsistent state.

---

## Task System

- States: **pending → in-progress → completed**
- Tasks can have **dependencies** — a task with unresolved deps cannot be claimed
- **File locking** prevents race conditions when multiple teammates claim simultaneously
- Lead assigns tasks OR teammates self-claim the next unblocked task after finishing

### Right-sizing tasks
- **Too small:** coordination overhead > benefit
- **Too large:** teammates run too long without check-ins, wasted effort risk
- **Just right:** self-contained with a clear deliverable (a function, a test file, a review)
- **Target ratio:** ~5–6 tasks per teammate

---

## Messaging Between Agents

- `message` — send to one specific teammate
- `broadcast` — send to all teammates simultaneously (**use sparingly** — cost scales with team size)

Messages are delivered automatically; the lead doesn't need to poll.
When a teammate finishes and goes idle, it automatically notifies the lead.

---

## Context & Permissions

**What teammates inherit at spawn:**
- Same project context as a regular session (CLAUDE.md, MCP servers, skills)
- Lead's permission settings (including `--dangerously-skip-permissions` if used)
- The spawn prompt from the lead

**What teammates do NOT inherit:**
- Lead's conversation history

**After spawning:** you can change individual teammate permission modes, but not at spawn time.

**Tip:** Pre-approve common operations in permission settings before spawning to reduce permission prompt interruptions during runs.

---

## Hooks for Quality Gates

| Hook | Trigger | Use |
|------|---------|-----|
| `TeammateIdle` | Teammate about to go idle | Exit code 2 to send feedback and keep them working |
| `TaskCompleted` | Task being marked complete | Exit code 2 to prevent completion and send feedback |

Example: "only approve tasks that include test coverage" — encode as a `TaskCompleted` hook.

---

## Team Size Guidelines

- **Start with 3–5 teammates** for most workflows
- Token costs scale **linearly** — each teammate = its own full context window
- Coordination overhead grows with team size
- Diminishing returns beyond ~5; three focused teammates often outperform five scattered ones
- Scale up only when work genuinely benefits from simultaneous parallel execution

---

## Best Practices

### 1. Give spawn prompts enough context
Teammates don't get the lead's history. Be explicit in the spawn prompt:

```
Spawn a security reviewer with the prompt: "Review src/auth/ for vulnerabilities.
Focus on token handling, session management, and input validation.
The app uses JWT tokens in httpOnly cookies. Report issues with severity ratings."
```

### 2. Avoid file conflicts
Two teammates editing the same file = overwrites. **Each teammate should own a distinct set of files.**

### 3. Use adversarial structure for debugging
For root-cause investigation, make teammates explicitly try to *disprove* each other's theories:

```
Spawn 5 teammates to investigate this bug. Have them debate and try to disprove
each other's theories. Update findings.md with whatever consensus emerges.
```

### 4. Start new to agent teams with research/review tasks
No code writing = no conflict risk. Good for learning the coordination mechanics.

### 5. Monitor and steer
Don't let teams run unattended too long. Check in, redirect failing approaches, synthesize findings as they arrive.

### 6. Use CLAUDE.md for shared guidance
All teammates read CLAUDE.md from their working directory. Use it to broadcast project-specific rules to the whole team automatically.

---

## Proven Prompt Patterns

### Parallel code review (split by domain)
```
Create an agent team to review PR #142. Three reviewers:
- Security implications
- Performance impact
- Test coverage validation
Have them each review and report findings.
```

### Competing hypothesis debugging
```
Users report [symptom]. Spawn 5 teammates to investigate different hypotheses.
Have them talk to each other to disprove each other's theories, like a scientific debate.
Update findings.md with whatever consensus emerges.
```

### New feature (parallel module ownership)
```
Create a team with 4 teammates to implement [feature].
Teammate 1 owns [module A], Teammate 2 owns [module B], etc.
Use Sonnet for each. Require plan approval before any implementation.
```

### Cross-layer change
```
Create a team: one for frontend changes in src/ui/, one for backend in src/api/,
one for tests in tests/. Each owns their layer completely. No shared files.
```

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Teammates not appearing | Press Shift+Down to cycle (in-process). Check if task was complex enough to warrant a team. |
| Too many permission prompts | Pre-approve operations in settings before spawning |
| Teammate stopped on error | Shift+Down to it, give direct instructions or spawn a replacement |
| Lead shuts down early | Tell it to keep going / wait for teammates |
| Task status stuck (blocking dependents) | Tell lead to nudge the teammate or update status manually |
| Orphaned tmux sessions | `tmux ls` then `tmux kill-session -t <name>` |

---

## Known Limitations (Experimental)

| Limitation | Impact |
|------------|--------|
| No session resumption with in-process teammates | `/resume` and `/rewind` don't restore teammates; lead may message ghosts |
| Task status can lag | Dependents can get stuck; nudge manually |
| Slow shutdown | Teammates finish current tool call before exiting |
| One team per session | Clean up before starting a new team |
| No nested teams | Teammates cannot spawn sub-teams; only the lead can manage the team |
| Lead is fixed | Can't promote a teammate or transfer leadership |
| Split panes only in tmux/iTerm2 | Not supported in VS Code integrated terminal, Windows Terminal, Ghostty |

---

## Token Cost Awareness

- Each teammate = full independent context window = proportional token cost
- Broadcast messages cost tokens proportional to team size — use sparingly
- For routine/sequential tasks: single session wins on cost
- For parallel research, review, or new-module work: the parallelism usually justifies the cost
- See: https://code.claude.com/docs/en/costs#agent-team-token-costs

---

## Aviates Air Tracker — Project-Specific Notes

Since this project is being migrated WPF → Blazor Hybrid, agent teams are well-suited for:

- **Parallel view migration:** one teammate per View/Page (SettingsView, DashboardView, etc.) — they own separate files with zero conflict risk
- **Cross-layer features:** frontend Razor component + ViewModel change + test, each owned by a different teammate
- **Code review:** security, performance, and MSFS-specific correctness as separate review lenses
- **Debugging SimConnect issues:** competing hypotheses pattern (reconnect logic, threading, HWND hook) works well since root causes are often unclear

**File ownership strategy for view migration:**
Each teammate owns exactly one `Pages/*.razor` + its corresponding `*ViewModel.cs` update — no two teammates touch the same file.
