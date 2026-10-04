# Contributing

SmartFile AI is currently maintained as a small engineering project. Contributions should keep changes narrow and testable.

## Local validation

Before submitting a change on Windows:

```powershell
dotnet restore SmartFileAI.sln
dotnet build SmartFileAI.sln --configuration Debug
dotnet test tests\SmartFileAI.Tests\SmartFileAI.Tests.csproj --configuration Debug
```

For deletion-safety changes, also run the Release suite and manually validate only with disposable files/directories.

## Safety-sensitive areas

Changes to the following areas require extra review:

- `SmartFileAI.FileOperations`
- deletion safety policy
- symbolic-link/reparse-point handling
- Recycle Bin/native shell interop
- permanent deletion
- path normalization and scope boundaries

Do not weaken fail-closed behavior merely to make a test pass.
