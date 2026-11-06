---
title: Daedalus and Thalos
sidebar_position: 10
---

# Daedalus and Thalos

This guide is for the author of an application that hosts agents and wants them to remember procedures. It lists what a host wires, in order, and how Daedalus and Thalos, the two hosts Mnemosyne is built alongside, fit that list.

## The two hosts

- [Thalos.NET](https://github.com/MarcelRoozekrans/Thalos.NET) is an agent framework for .NET on Microsoft Agent Framework, with AI.Sentinel security and MCP tools. Its repository description also calls it "Hermes-style, ZeroAlloc-native", which this guide does not rely on.
- [Daedalus.NET](https://github.com/MarcelRoozekrans/Daedalus.NET) is a .NET 10 application for AI-driven task execution. A person plans work on a task board and starts a manufacture run for a task. Its agents run on Thalos.NET.

Neither repository's public README mentions Mnemosyne today. This guide therefore says what Mnemosyne offers a host, and leaves the wiring in each host to that host's repository.

## What Mnemosyne never does

Mnemosyne never executes a procedure. It stores the steps of one, finds the best known one for a question, and records how a run went. The host decides what to do with the steps: follow them, adapt them, or ignore them. A host that expects Mnemosyne to run a graph, retry a step or call a tool will not find that code in it. [Overview](../concepts/overview.md) shows where each package sits.

## What a host wires

In this order:

1. **A store.** PostgreSQL 15 or later with pgvector 0.8.0 or later, registered with `AddMnemosynePostgreSql`, and a host that calls `MigrateAsync` at startup. See [PostgreSQL store](postgresql.md#set-up-the-store) and [Migrations](postgresql.md#migrations).
2. **Embeddings.** An `IEmbeddingGenerator<string, Embedding<float>>` from Microsoft.Extensions.AI. The store's `Dimensions` must match what it returns. [Getting started](../getting-started.md#2-start-postgresql-with-pgvector) shows the pairing.
3. **`AddMnemosyne`.** It registers the facade and the default selector. Register the store, the generator and every other service the facade uses as a singleton. [Getting started](../getting-started.md#3-register-the-services) has the registrations.
4. **Capture.** `AddReasonletCapture` registers the queue and the worker that remember finished runs in the background. [Capture, redaction and review](capture-and-review.md) follows a capture from transcript to store.
5. **The Agent Framework provider.** A `ReasonletContextProvider` on each agent, with a `ScopeResolver` that returns the run's scope and subject. The default resolver uses the agent's name as the scope and sets no subject, which a multi-tenant host replaces. See [Scope and subject](agent-framework.md#scope-and-subject).
6. **The review gate.** `MnemosyneOptions.RequireReview` holds a new capture as `PendingReview` until a person approves it. See [The review gate](data-lifecycle.md#the-review-gate).
7. **The lifecycle jobs.** The host schedules `PruneAsync` on the store and calls `EraseAsync` when a tenant leaves or a person asks. Mnemosyne never prunes on its own. See [Retention](data-lifecycle.md#retention) and [Erasure](data-lifecycle.md#erasure).

Steps 1 to 5 give an agent recall and capture. Steps 6 and 7 are the controls a host adds before real users reach it.

## How the two hosts map

Thalos.NET builds on Microsoft Agent Framework, and `Mnemosyne.NET.AgentFramework` is Mnemosyne's package for that framework. Its `ReasonletContextProvider` is an Agent Framework `AIContextProvider`, so a host built on Agent Framework adds it to an agent's `AIContextProviders`, as step 5 describes. [Agent Framework](agent-framework.md) has the setup.

Daedalus runs its agents through Thalos.NET: its README describes a `Daedalus.Agents` project hosted in `Daedalus.Api`, with sessions, tools, AI.Sentinel and a Telegram channel. Its README documents the memory it uses today, `Thalos.NET.Memory`, and does not mention Mnemosyne.

Whether a Thalos or Daedalus release wires Mnemosyne in, and under which registration, is not in either README. For that, read the host repositories: [Thalos.NET](https://github.com/MarcelRoozekrans/Thalos.NET) and [Daedalus.NET](https://github.com/MarcelRoozekrans/Daedalus.NET). This guide does not claim that an adapter exists.

## A checklist for a new host

- **Scope and subject per run.** Return a stable scope, usually the tenant, and the person's id as the subject when erasure by person matters. A renamed agent starts a new scope under the default resolver. See [Scopes and subjects](../concepts/scopes-and-subjects.md).
- **A judge.** Without an evaluator the agent's own verdict counts for every run unless a person gives feedback, and an agent-only verdict weighs less in ranking. Pick [Minos](minos-evaluator.md) or [a chat model](chat-evaluator.md), or [write your own](custom-evaluator.md). [Evaluation](../concepts/evaluation.md) says who decides whether a run worked.
- **A capture guard.** `Mnemosyne.NET.Sentinel` runs AI.Sentinel's detectors over a capture before it is stored. A scan in which a detector throws fails closed as `Unavailable` unless another detector blocked; [A throwing detector fails the capture closed](capture-and-review.md#a-throwing-detector-fails-the-capture-closed) has the detail.
- **Retention.** Schedule `PruneAsync` with a `ReasonletRetention` policy that fits how long runs are useful. See [Retention](data-lifecycle.md#retention).
- **Telemetry.** Add the `Mnemosyne.NET` activity source and meter to your OpenTelemetry setup. See [Telemetry](telemetry.md#turn-it-on) and its [Privacy](telemetry.md#privacy) section.
- **Erasure.** Decide who may call `EraseAsync`, and test an erase of a scope and of a subject against a copy of your data. See [Erasure](data-lifecycle.md#erasure) and [What erasure leaves on disk](postgresql.md#what-erasure-leaves-on-disk).
- **Data protection.** [Data protection](data-protection.md) lists the duties a deployer carries and what Mnemosyne does not do for you.
- **Upgrades.** Stop every instance of the old release before you migrate. See [Upgrading](postgresql.md#upgrading) and [Compatibility](../concepts/compatibility.md).
