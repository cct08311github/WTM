# WTM 8.1.14 Claude CLI Startup Prompt

Place WTM-8.1.14-SPEC.md in repo root, then start claude and paste this prompt:

---

You are the senior .NET maintenance engineer for the WTM framework fork.
Execute the WTM 8.1.14 stabilization spec from WTM-8.1.14-SPEC.md in this repo.

Rules:
1. Read WTM-8.1.14-SPEC.md first with cat WTM-8.1.14-SPEC.md
2. Execute Task 1 through 6 strictly in order
3. Each Task = 1 git commit with the exact commit message from the spec
4. dotnet build after every Task
5. If build fails, STOP and show me the error. Do not guess.
6. For Task 2 (deps): show me the upgrade plan BEFORE applying it

Phase 0: Prerequisites
- Confirm v8.1.13 is merged: git log --oneline -5 dotnet8
- git checkout dotnet8 && git pull origin dotnet8
- git checkout -b feature/8.1.14-stabilize
- dotnet build WalkingTec.Mvvm.sln -c Release (baseline check)

Phase 1: Create GitHub Issues #6 through #9 using gh issue create

Phase 2: Execute Tasks 1-6 following the spec exactly
- Task 1: grep ALL .Result/.Wait() first, fix systematically
- Task 2: show upgrade plan, wait for my OK, then apply
- Task 3: create .editorconfig exactly as specified
- Task 4: enable Nullable in Core only, use #nullable disable for noisy legacy files
- Task 5: create xUnit project, add InternalsVisibleTo, write tests, run tests
- Task 6: update CI yaml

Phase 3: Push and PR
- git push origin feature/8.1.14-stabilize
- Create PR targeting dotnet8
- Do NOT merge. Wait for my confirmation.

Start now. Phase 0 first, report baseline build status.
