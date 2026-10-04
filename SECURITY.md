# Security Policy

SmartFile AI performs local filesystem operations, including deletion. Treat deletion-safety regressions as security-sensitive defects.

## Reporting

Please avoid publishing destructive proof-of-concept steps against real user data. Report the smallest reproducible case and use disposable files/directories.

## Safety expectations

The current baseline is designed to deny dangerous or ambiguous deletion targets, including protected system paths, drive roots, UNC/device paths, alternate data streams, and reparse/symbolic-link targets.

A Recycle Bin failure must never silently fall back to permanent deletion.
