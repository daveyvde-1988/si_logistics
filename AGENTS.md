# Logistics working instructions

- This `Silica-1283/si_logistics` folder is the user-selected source of truth. Do not switch to another copy or archive based on timestamps.
- Main code: `Si_Logistics.cs`; project: `Si_Logistics.csproj`. Consult `README.md` for behavior relevant to the requested change; do not reread all documentation for every edit.
- Implement the requested behavior with minimal scope. Search relevant source first; exclude `bin`, `obj`, unrelated mods, archives and full logs unless needed. Inspect game assemblies only to resolve a specific API uncertainty; do not guess signatures.
- Preserve dedicated-server Mono/netstandard2.1 compatibility. Use the project's referenced server assemblies; do not substitute IL2CPP assemblies. Preserve same-team transfer checks, cargo/storage limits, and resource accounting unless the requested change requires otherwise.
- The user handles tests and live verification. Do not run or add tests, or start/stop the server unless explicitly requested.
- After code changes, compile from this folder using:
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`
  This checks the bundled dependencies and builds `Si_Logistics.csproj` in Release. Avoid the interactive `Build Logistics.bat` wrapper.
- Output: `bin/Release/netstandard2.1/Si_Logistics.dll`. Compilation does not verify in-game behavior. For documentation-only edits, no build is needed.
- Fix build errors caused by the change and rebuild; stop after a successful build. If blocked by dependencies, report the specific blocker without installing tools or changing unrelated settings.
- Finish briefly: what changed, build result, and a short focused checklist for the user's in-game verification. Keep confirmed API discoveries or changed behavior in existing documentation only when useful for future work; avoid session transcripts.

## Shared project context

- Explicitly read `../../Silica-Shared-Context/AGENTS.md`, MOD_REGISTRY.md and only relevant API_CONTRACTS.md / DEPENDENCIES.md sections. These sibling instructions are not automatically inherited. If inaccessible, report the missing context.
- Use shared search ID `logistics`, adding `koh`, `ranks`, `formation` or `server-ui` when relevant; inspect actual source. Review KoH's reflected credit API, Ranks' hash-checked reward consumer, ModUI exports and native movement overlap as appropriate. Similarity is not dependency evidence.
- After authorized changes, review API/dependency impact, update verified local facts, propose shared changes and refresh affected index IDs. Preserve this file's pre-existing local edits and the test/build/server restrictions above. No cross-repository commits or pushes without explicit authorization.
