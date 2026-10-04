# SmartFile AI

SmartFile AI is a Windows desktop file-management prototype built with **.NET 8, WPF, MVVM, SQLite, and Entity Framework Core**. It focuses on local file indexing, scoped search, asynchronous scanning, and safety-first deletion workflows.

> Current validated baseline: **RC2.2.3**

## Features

- Browse local drives and directories with lazy-loaded tree navigation.
- Scan directories asynchronously with progress reporting and cancellation.
- Persist file metadata in a local SQLite index.
- Search by filename or common extension tokens such as `pdf`, `.pdf`, and `*.pdf`.
- Scope search to the currently selected drive or directory.
- Send files/directories to the Windows Recycle Bin through the native Windows shell path.
- Permanently delete files/directories only through the explicit permanent-delete flow.
- Fail closed on protected paths, drive roots, UNC/device paths, alternate data streams, reparse points, junctions, and symbolic links.
- Keep operation audit records and database state synchronized after physical deletion.

## Tech stack

- .NET 8
- WPF
- MVVM / CommunityToolkit.Mvvm
- SQLite
- Entity Framework Core 8
- xUnit
- Windows native shell / COM interop for Recycle Bin operations

## Solution layout

```text
SmartFileAI.sln
src/
  SmartFileAI.Core/
  SmartFileAI.Database/
  SmartFileAI.Scanner/
  SmartFileAI.FileOperations/
  SmartFileAI.UI/
tests/
  SmartFileAI.Tests/
docs/
  VALIDATION.md
```

### Projects

| Project | Responsibility |
| --- | --- |
| `SmartFileAI.Core` | Domain models and interfaces |
| `SmartFileAI.Database` | SQLite / EF Core indexing and audit persistence |
| `SmartFileAI.Scanner` | Asynchronous filesystem scanning |
| `SmartFileAI.FileOperations` | Recycle/permanent deletion and safety policy |
| `SmartFileAI.UI` | Windows WPF user interface |
| `SmartFileAI.Tests` | xUnit unit and integration-style tests |

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK

The WPF application targets `net8.0-windows` and is intended to run on Windows.

## Build and test

```powershell
dotnet restore SmartFileAI.sln
dotnet build SmartFileAI.sln --configuration Debug
dotnet test tests\SmartFileAI.Tests\SmartFileAI.Tests.csproj --configuration Debug

dotnet build SmartFileAI.sln --configuration Release
dotnet test tests\SmartFileAI.Tests\SmartFileAI.Tests.csproj --configuration Release
```

The two real symbolic-link safety tests require Windows Developer Mode or an elevated shell because they create temporary symbolic links to verify the fail-closed safety policy.

## Run

```powershell
dotnet run --project src\SmartFileAI.UI\SmartFileAI.UI.csproj --configuration Release
```

The local database is created under the current user's local application-data directory:

```text
%LOCALAPPDATA%\SmartFileAI\smartfile.db
```

## Validated RC2.2.3 baseline

The current baseline was validated on Windows with:

- Debug automated tests: **55 / 55 passed**
- Release build: **0 warnings, 0 errors**
- Release automated tests: **55 / 55 passed**
- Manual directory-tree navigation: passed
- Manual scoped PDF search: passed
- Manual Recycle Bin file deletion: passed
- Manual Recycle Bin directory deletion: passed
- Manual permanent deletion: passed

See [`docs/VALIDATION.md`](docs/VALIDATION.md) for the acceptance scope.

## Safety model

Deletion is intentionally conservative:

- Recycle failure does **not** fall back to permanent deletion.
- Drive roots and protected Windows directories are denied.
- UNC paths and extended device paths are denied in the current version.
- Reparse points, junctions, and symbolic links are denied for deletion targets/ancestors.
- The safety policy is checked again immediately before the physical deletion action.
- Database/log synchronization failures after a successful physical deletion return a warning result rather than pretending the whole operation failed.

## Current scope

SmartFile AI is currently a Windows desktop engineering project and validated RC baseline, not a production-grade filesystem replacement. Back up important files before using destructive operations.

## License

No open-source license has been selected yet. Until a license is added, normal copyright restrictions apply.
