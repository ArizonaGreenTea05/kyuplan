@AGENTS.md

## Issue workflow
When asked to address a GitHub issue, after implementing and testing:
1. Spawn the `issue-reviewer` agent via the Agent tool, passing only the issue number.
   Do not describe your changes or reasoning to it.
2. If it ends with `VERDICT: CHANGED`, spawn a fresh `issue-reviewer` again.
3. Stop on `VERDICT: CLEAN`, or after 5 rounds. If the cap is hit, report what is still open.
4. Then summarize: what changed, rounds taken, test result.
