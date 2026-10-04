# Changelog

## RC2.2.3

### Fixed

- Fixed directory-tree lazy loading that could remain stuck on `Loading...`.
- Prevented placeholder/empty paths from reaching directory filesystem APIs.
- Bound tree expansion state so UI expansion triggers lazy loading correctly.
- Added selected-drive/directory search scoping.
- Added exact extension interpretation for common queries such as `pdf`, `.pdf`, and `*.pdf`.
- Preserved normal filename search for short words such as `code` and `test`.
- Enforced path-boundary behavior for scoped search.
- Added RC2.2.3 repository search regression coverage.

### Validated

- Debug tests: 55 / 55 passed.
- Release build: 0 warnings, 0 errors.
- Release tests: 55 / 55 passed.
- Windows UI navigation/search acceptance passed.
- Recycle and permanent deletion acceptance passed.

## RC2.2.2

### Hardened

- Recycle deletion uses the Windows `IFileOperation` recycle path and remains fail-closed.
- Recycle failure never falls back to permanent deletion.
- Strengthened real symbolic-link/reparse-point safety tests.
- Preserved file-lock/result mapping and post-delete database/audit synchronization.
