# RC2.2.3 Validation Record

## Status

**SmartFile AI RC2.2.3 — Windows Validation PASS**

Validation was performed on the Windows working copy before the GitHub source-clean package was produced.

## Automated validation

### Debug

- Build: passed
- Tests: **55 / 55 passed**

### Release

- Build: passed
- Warnings: **0**
- Errors: **0**
- Tests: **55 / 55 passed**

## Manual Windows UI acceptance

The following behaviors were manually checked:

- Local drive tree expansion loads child directories.
- The transient `Loading...` placeholder disappears after loading.
- The previous empty-path directory-loading failure no longer occurs.
- Repeated tree expansion does not introduce the previous stuck-loading behavior.
- Search token `pdf` returns PDF-extension results rather than arbitrary filename/path matches.
- `.pdf`, `*.pdf`, and case variants behave as extension searches.
- Normal short filename terms such as `code` and `test` remain filename searches.
- Search scope follows the selected drive or directory.
- A selected `D:\` scope does not leak results from `C:\`.
- Subdirectory scoping respects path boundaries such as `D:\Docs` versus `D:\DocsBackup`.
- Recycle Bin deletion was verified for a disposable file.
- Recycle Bin deletion was verified for a disposable directory.
- Permanent deletion was verified separately from Recycle Bin deletion.

## Symbolic-link test requirement

Two safety tests create real Windows symbolic links. They intentionally fail if the process cannot create a symbolic link, because silently skipping would create a false-green safety result.

Run the complete test suite from an elevated PowerShell window or enable Windows Developer Mode when necessary.

## Release boundary

This validation establishes the RC2.2.3 baseline. It does not claim that every filesystem, device, network share, cloud-sync provider, or unusual Windows shell environment has been tested.
