# Phase 7B-1: Safe Cleanup Foundation

Baseline: dev `837383ddd1f905232945be1f574a826228c39c4b`.

This phase adds an independent Core candidate model and a read-only analyzer in
FileOperations. It adds no project, package, database schema, UI, executor or DI
registration. Existing code and tests are unchanged. FileItem remains an index
entity. Source/advisor types are deferred until there is an actual consumer.

## Contract and rules

`ICleanupAnalyzer.Analyze(FileItem item, DateTime referenceTime)` requires an
explicit UTC reference time. It returns null for an unmatched rule, otherwise a
candidate with a reason and immutable evidence list. The risk and recommendation
are immutable. Safe + Recommended starts selected; Review starts unselected
and permits explicit selection; Protected rejects selection. None authorizes
deletion. Reversible is false because analysis cannot establish recycle-bin
capability. RequiresElevation is a hint for system/permission cases, never an
instruction to bypass a safety rejection.

| Rule | Age, using live LastWriteTimeUtc | Result after safety checks |
| --- | --- | --- |
| .tmp / .temp (case insensitive) | >= 7 days | Safe / Recommended |
| ~$ prefix + .doc/.docx/.xls/.xlsx/.ppt/.pptx | >= 1 day | Safe / Recommended |
| .dmp | >= 7 days | Safe / Recommended |
| Recent matching temp/Office temp/.dmp | Below threshold | Review / NeedsReview |
| .log | >= 30 days | Review / NeedsReview; never Safe |
| Recent .log | < 30 days | No candidate |
| Live empty directory | Existence and enumeration confirmed | Review / NeedsReview |
| Non-empty directory, unknown file | No applicable rule | No candidate |
| Existing deletion policy rejects target/ancestor link or path | Any | Protected; never selected |
| System protected / recognized Windows maintenance path | Any | Protected / SystemManagedCleanupOnly |
| Missing target, type mismatch, inaccessible evidence, changed metadata | Any | Review / InsufficientEvidence |
| Future/invalid timestamp for temp or dump | Any | Review / InsufficientEvidence |

The analysis first reuses DeletionSafetyPolicy.Evaluate. It does not duplicate
system-directory authorization rules. Windows.old, SoftwareDistribution,
WinSxS, DriverStore, DeliveryOptimization/Delivery Optimization, rollback and
upgrade staging segments are **classification** hints requiring official
Windows cleanup mechanisms, including inside test sandboxes. They grant no
deletion permission. Matching whole segments avoids names such as WinSxSBackup.

The analyzer reads live attributes/type/LinkTarget/time/length. It checks the
existing IsFileLocked heuristic and then requires a read-only exclusive open:
the existing heuristic can return false for access denied. If either check
fails or evidence changes, the target cannot become Safe. Indexed size, time,
name and extension are not authority; a stale indexed ReparsePoint flag is
conservatively refused. Directory enumeration includes MoveNext within error
handling and does not recurse.

An old temp/dump in a recognizable Desktop, Downloads, AppData or cache context
is still Review. These are conservative context hints, not a comprehensive
cache locator. Because the normal Windows temp root is usually under AppData,
only its relative suffix is inspected for those context hints. Normal temp
files remain eligible; nested cache/AppData/user-folder contexts do not. This
phase does not recommend caches, installers, archives, duplicates, large files
or ordinary zero-byte files based on size or location.

## Future permission boundary

Indexed file -> deterministic analysis -> risk classification -> optional AI
advice -> explicit user confirmation -> FileOperationService ->
DeletionSafetyPolicy -> Recycle Bin.

**AI can advise. AI cannot authorize deletion.** There is no AI package, HTTP
call, prompt or key in this phase. The analyzer contains no mutation or deletion
calls. Test teardown alone removes its own GUID sandbox under Path.GetTempPath.

## Limitations and validation gates

Analysis is a point-in-time observation, not an atomic identity guarantee or a
lease. Locks, directory contents, paths and metadata may change afterwards.
ReclaimableBytes is the observed logical file length, not guaranteed physical
disk savings (compressed/sparse files, hard links, recycle-bin retention).
Holding the read probe and rechecking evidence narrows uncertainty but cannot
eliminate TOCTOU or establish Windows reparse identity atomically. A future
executor must revalidate independently, and no executor is supplied here.

Reference time is explicit; determinism is for the same reference time and
observed filesystem state, not across concurrent disk changes. The analyzer is
synchronous and performs disk I/O; a future UI must use an appropriate worker.
No global scan, persistent candidate table, cleanup UI or batch operation is
included. Context hints intentionally favor false positives requiring Review.

Tests create real files and links exclusively in GUID sandboxes under the OS
temp root. Symbolic-link creation failures must fail with the Developer Mode /
elevation requirement. The junction test explicitly fails off Windows. Links
are unlinked before recursive sandbox teardown; teardown failures are visible.

Current Work environment has no dotnet SDK. All five requested commands must
be attempted and recorded in the work bundle; there is no build/test PASS claim.
Existing 63 cases remain in source unchanged, but are not revalidated here.

Independent baseline blockers were found before editing: FileRepository has
malformed C# backslash string literals; MainViewModel has newlines inside
ordinary interpolated string literals; RC223 repository-test fixture paths
contain missing separators and embedded control characters. These appear in
the authoritative Git revision and are outside this phase's additive patch.
The user-reported Windows 63/63 result cannot verify those exact source bytes.
They must be reconciled before accepting the complete solution build.
