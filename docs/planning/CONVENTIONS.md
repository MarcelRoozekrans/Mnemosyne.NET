# Project Conventions

> Written by `init-conventions`. Do not hand-edit — re-run the sub-skill instead; the Commit & Release Protocol reads these fields.

**Established:** 2026-10-21

## Stack

**Language / runtime:** .NET 10 and .NET 11
**Package manager:** NuGet (central package management)
**Framework:** none (library; Microsoft.Extensions.AI for embeddings)
**Datastore:** PostgreSQL + pgvector
**Build:** `dotnet build Mnemosyne.NET.slnx -c Release`
**Test:** `dotnet test tests/Mnemosyne.NET.Tests -c Release`

## Commits

**Format:** conventional
**Scopes:** free
**Scope source:** n/a
**Fallback when scope not allowed:** omit scope

## Branching

**Model:** feature-branch
**PR required:** yes (user, Main ruleset like the sibling repos)
**Protected branches:** main

## Versioning & Release

**Scheme:** semver (GitVersion)
**Released by:** release-please
**Milestone completion tags a release:** no
**Changelog:** auto (release-please)

## Deployment

**Deploy target:** NuGet.org
**Environments:** none
**Deployed by:** release workflow
