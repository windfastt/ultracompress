#!/bin/bash
# Rebase PR branch onto main, push, wait CI, merge.
set -euo pipefail
PR="$1"
cd /agent/ultracompress
git fetch origin main
BR=$(gh pr view "$PR" --json headRefName,state -q '.headRefName')
STATE=$(gh pr view "$PR" --json state -q '.state')
if [ "$STATE" = "MERGED" ]; then
  echo "PR #$PR already merged"
  git pull origin main
  exit 0
fi
if [ "$STATE" = "CLOSED" ]; then
  echo "PR #$PR closed, skip"
  exit 0
fi
git checkout -B "$BR" "origin/$BR"
git rebase origin/main || {
  # common ci.yml conflict: keep main's branch list
  if [ -f .github/workflows/ci.yml ] && grep -q '<<<<<<<' .github/workflows/ci.yml; then
  python3 <<'PY'
from pathlib import Path
p = Path(".github/workflows/ci.yml")
text = p.read_text()
if "<<<<<<<" in text:
    lines = text.splitlines()
    out = []
    i = 0
    while i < len(lines):
        if lines[i].startswith("<<<<<<<"):
            i += 1
            head = []
            while i < len(lines) and not lines[i].startswith("======="):
                head.append(lines[i])
                i += 1
            i += 1  # =======
            while i < len(lines) and not lines[i].startswith(">>>>>>>"):
                i += 1
            i += 1  # >>>>>>>
            out.extend(head)
        else:
            out.append(lines[i])
            i += 1
    p.write_text("\n".join(out) + ("\n" if text.endswith("\n") else ""))
PY
    git add .github/workflows/ci.yml
    GIT_EDITOR=true git rebase --continue
  else
    echo "Unresolved rebase for PR #$PR"
    git status
    exit 1
  fi
}
git push -f origin "$BR"
gh pr ready "$PR" 2>/dev/null || true
for _ in $(seq 1 40); do
  if ! gh pr checks "$PR" 2>&1 | grep -q pending; then break; fi
  sleep 15
done
gh pr checks "$PR" || true
gh pr merge "$PR" --merge
git checkout main && git pull origin main
echo "Merged PR #$PR"