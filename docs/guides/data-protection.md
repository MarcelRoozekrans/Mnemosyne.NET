---
title: Data protection
sidebar_position: 10
---

# Data protection

This guide is for the person who deploys Mnemosyne in an application and answers for the personal data in it. It lists the duties a deployer usually carries under the GDPR, says which Mnemosyne feature supports each one, and links the page that holds the mechanics.

This is engineering guidance, not legal advice. Mnemosyne is a library that runs inside your application. You decide the purposes, the lawful basis and the retention, and you carry the duties. Nothing here says that a deployment is compliant with any law. Ask your data protection officer or counsel about your own case.

## What data Mnemosyne holds

The PostgreSQL store keeps the columns below. Whether a column holds personal data depends on what your application puts in it.

| Data | Where it comes from | Can it hold personal data? |
|---|---|---|
| Scope | Your application | Yes, when a scope stands for one user, as [Scopes](../concepts/scopes-and-subjects.md#scopes) allows. Every row carries its scope. |
| The embedding of the question | The embedding generator you register, run over the capture's question | Yes, in principle. The question itself is not stored, but a vector made from it is, and it is built from the person's words. |
| The name | The capture's `Name`, or the first line of the question, cut to 200 code points, when you set none | Yes, when it is taken from the question. The chat middleware and the agent provider always set a name made of tool names, so they avoid this. See [Name](../concepts/reasonlets.md#name). |
| The intent | The capture's `Intent`, or the label an `IIntentExtractor` returns | Possibly, when your extractor or your caller puts a person's details in the label. |
| The graph | The steps of the run. See [The graph](../concepts/reasonlets.md#the-graph) | Yes, when you build the graph yourself: properties, node names and edge conditions are free-form. Captures from the chat middleware and the agent provider hold tool and argument names only. |
| Provenance: source, run id, subject id, tags | Your application | Yes. The subject id names the person, and tags are free-form key-value pairs. |
| Runs | One row per recorded run: time, duration, verdicts, evaluator name, score, provenance | Yes, through the subject id and tags on the run. |
| Feedback | Three scores between 0 and 1 and an optional verdict per run | No text. It links to a person only through its run. |
| Review state | `Active` or `PendingReview` on each Reasonlet | No. No reviewer identity is stored. |
| Counters | Run, success, judged-run and judged-success counts and two timestamps per Reasonlet | No. They are bare numbers with no link to a person. |

The answer of a run reaches an evaluator when you register one, and is never stored. [Reasonlets](../concepts/reasonlets.md) describes each field, and [Scopes and subjects](../concepts/scopes-and-subjects.md#subjects) says how to name the person a record is about.

## Duty to feature

| Duty | What supports it | Where it is documented |
|---|---|---|
| Erase on request, Art. 17 | `EraseAsync` removes everything in a scope, or everything about one subject in a scope, in one transaction. Runs follow their procedure, and the counters of kept procedures are adjusted. | [Erasure](data-lifecycle.md#erasure) and [What an erase removes](data-lifecycle.md#what-an-erase-removes) |
| Data minimisation, Art. 5(1)(c) | The capture guard refuses a capture before it is stored. The answer is never stored, and the question is kept only as an embedding. Captures from the chat middleware and the agent provider take their name and shape from tools, not from text. | [The capture guard](capture-and-review.md#the-capture-guard), [What it scans](capture-and-review.md#what-it-scans), [What a Reasonlet holds](../concepts/reasonlets.md#what-a-reasonlet-holds) and [Name](../concepts/reasonlets.md#name) |
| Data protection by design and by default, Art. 25 | `MnemosyneOptions.RequireReview` holds a new Reasonlet and a new version back from recall until a person approves it. Scopes keep tenants apart in every call. Telemetry carries no user data. | [The review gate](data-lifecycle.md#the-review-gate), [Scopes](../concepts/scopes-and-subjects.md#scopes) and [Privacy](telemetry.md#privacy) |
| Storage limitation, Art. 5(1)(e) | `PruneAsync` deletes old run records and Reasonlets nobody uses, on a schedule the host runs. | [Retention](data-lifecycle.md#retention) |
| Accountability, Art. 5(2) and records of processing, Art. 30 | Each Reasonlet and run carries its provenance. Receipts return the ids to correlate on. The table above lists the categories of data to put in your own record. Mnemosyne does not keep that record for you. | [Reasonlets](../concepts/reasonlets.md), [Telemetry](telemetry.md) |
| Security of processing, Art. 32 | Encryption and backup rotation are a storage concern for the database. Telemetry carries no question, subject or scope, so an exporter needs no scrubbing. The store's role needs only the privileges listed. Keep Npgsql's `IncludeErrorDetail` off in production, or a logged error can carry row values. | [What erasure leaves on disk](postgresql.md#what-erasure-leaves-on-disk), [Privileges](postgresql.md#privileges), [Errors](postgresql.md#errors) and [Privacy](telemetry.md#privacy) |
| Access, Art. 15, and portability, Art. 20 | See below. The library offers no export. | [Access and portability](#access-and-portability) |

### Access and portability

`IReasonletMemory` has no call that lists or reads records by subject. It offers `RememberAsync`, `RecallAsync`, `RecordExecutionAsync`, `RecordFeedbackAsync`, `EraseAsync`, and the review calls `ListPendingAsync`, `ApproveAsync` and `RejectAsync`. `ListPendingAsync` lists one scope's pending Reasonlets, not a subject's. `IReasonletStore` adds `GetAsync`, which reads one Reasonlet by id, and `SearchAsync`, which finds the nearest Reasonlets to an embedding. Neither selects by subject, and no call returns the runs of a subject. There is no export format.

To answer an access or portability request, query the `mnemosyne` schema yourself. The subject id is a column on `reasonlets` and on `reasonlet_executions`, with an index on `(scope, subject_id)`. [PostgreSQL store](postgresql.md) describes the tables. Only the subject's own records are findable this way. Records captured without a subject id are not.

## What Mnemosyne does not do

Each item says what to do about it.

- **It does not scrub storage after an erase.** An erase deletes rows. Deleted data can stay in the data files, indexes, write-ahead log, replicas and backups until they are overwritten or rotated. Encrypt the storage, rotate backups and archived WAL on a schedule that matches what you tell people, and know that a restore brings erased data back. [What erasure leaves on disk](postgresql.md#what-erasure-leaves-on-disk) has the detail.
- **It does not remove personal data from an embedding before storing it.** An erase removes the row and its vector. The text was already sent to your embedding generator to make the vector, and what that provider keeps is outside Mnemosyne. Check the provider's retention terms, and use the capture guard to keep what you do not want embedded out of the question.
- **It does not keep capturing while a detector is down.** The guard fails closed. When the whole pipeline throws, the timeout runs out, or a single detector throws and no other detector blocked, the capture is rejected as `Unavailable` and nothing is stored. A detector that depends on an endpoint rejects captures while the endpoint is down. Set `SentinelOptions.OnDetectorFailure` in your AI.Sentinel options to alert on it. [A throwing detector fails the capture closed](capture-and-review.md#a-throwing-detector-fails-the-capture-closed) has the detail.
- **It does not guard everything.** Only `RememberAsync` passes the guard. `RecordExecutionAsync` and writes straight to `IReasonletStore` do not. The question and answer of a run go to your evaluator unguarded. The guard also sees only the question, the intent and the name, not the answer, the graph or the provenance, and at the default `High` threshold it lets an email address or a phone number through. Keep values out of graph properties, or inspect them in your own `ICaptureGuard`. Set `BlockAtSeverity` to `Medium` if you want those blocked, and check for false positives. See [What is guarded](capture-and-review.md#what-is-guarded) and the [adapter README](https://github.com/MarcelRoozekrans/Mnemosyne.NET/blob/main/src/Mnemosyne.NET.Sentinel/README.md).
- **It does not govern the services around it.** Your chat model, embedding generator, intent extractor, evaluator and logging middleware each see text that Mnemosyne passes to them. Their prompts, logs and retention are yours to set. Give a judge's client its own logging settings, as [Chat evaluator](chat-evaluator.md) and [Minos evaluator](minos-evaluator.md) describe.
- **It does not find a person's records without a subject id.** Erasure by subject matches the `SubjectId` on provenance. A record captured without one is not found. Set the subject on every capture that is about a person, or erase by scope. The Agent Framework provider's default resolver sets no subject, so a host that erases by person replaces it. See [Scope and subject](agent-framework.md#scope-and-subject).
- **It does not keep a scope's members apart from each other.** Other users of a scope can be shown a recalled Reasonlet's name and steps. If personal data reaches a name, a graph or a tag, it can end up in another person's prompt. Choose the scope boundary to match who may see whose procedures, possibly one scope per user. Keep personal data out of names and graphs, and use the review gate to inspect a procedure before it is recalled. See [Name](../concepts/reasonlets.md#name).
- **It does not correct a stored record.** For the right to rectification, Art. 16, there is no update call on `IReasonletMemory` or `IReasonletStore`. Erase the record and capture it again.
- **It does not record who reviewed.** The review gate stores no reviewer identity or comment. Keep that in your own audit trail.
- **It does not enforce who may call it.** It does not check who may use a scope or call `EraseAsync`. Your application decides that before it calls.
- **It does not decide your legal position.** Controller and processor roles, the lawful basis, consent, notices to the people concerned and the record of processing are yours.

## The EU AI Act in short

[Regulation (EU) 2024/1689](https://eur-lex.europa.eu/eli/reg/2024/1689/oj/eng), the AI Act, regulates AI systems. The [Commission's overview](https://digital-strategy.ec.europa.eu/en/policies/regulatory-framework-ai) explains its risk-based approach. This section states what Mnemosyne does and leaves every classification to you. It names no date.

- **What Mnemosyne does.** It stores procedures and ranks them by similarity, track record and freshness. It contains and trains no model. Recall feeds a model's prompt, and the evaluators and the intent extractor call a model that you register. Whether your system, with Mnemosyne in it, meets the definition of an AI system in Article 3(1) is for you to assess.
- **The Act separates providers and deployers.** Article 3(3) defines a provider as the party that develops an AI system, or has it developed, and places it on the market or puts it into service under its own name. Article 3(4) defines a deployer as the party that uses an AI system under its own authority. Mnemosyne is a library you build into your system, and provider duties attach to the system or model placed on the market.
- **Your system may be in scope.** An agent that uses Mnemosyne next to a model may be an AI system, and you may be its provider, its deployer or both. Whether it is high-risk depends on what it is used for. Decide that with counsel, from the Act's text. This guide does not.

For a high-risk system, the Act asks for two things that Mnemosyne's features can feed. Article 12 requires that the system technically allows automatic recording of events over its lifetime, and Article 26(6) has deployers keep the logs it generates, to the extent they control them. Article 14 requires that natural persons can effectively oversee the system while it is in use, and Article 26(2) has deployers assign that oversight to people with the competence, training and authority for it.

| Need | What Mnemosyne offers | What it does not |
|---|---|---|
| Records of what happened | Provenance on every Reasonlet and run, the receipts with their ids, and the run records with verdicts and counters. See [Reasonlets](../concepts/reasonlets.md) and [Evaluation](../concepts/evaluation.md). | It does not log the system's inputs and outputs. Pruning deletes run records on your schedule, so set retention with your logging duty in mind. |
| Correlating events across systems | Telemetry with opaque Reasonlet ids and versions. See [Telemetry](telemetry.md). | It carries no question, answer, subject or scope, by design. |
| Human oversight of what an agent follows | The review gate: a new procedure is not recalled until a person approves it. See [The review gate](data-lifecycle.md#the-review-gate). | It does not store who reviewed. Keep that in your own audit trail. |
| A person's verdict on a run | `RecordFeedbackAsync` records it, and a `Worked` verdict overrides the evaluator's and the agent's. See [Feedback](../concepts/evaluation.md#feedback). | It records no author for the feedback. |

Mnemosyne does not make a system satisfy Article 12 or Article 14. It holds records and a gate that you can use as part of your own logging and oversight.

## A deployer checklist

- Set `SubjectId` on every capture and run that is about a person.
- Register a capture guard, choose `BlockAtSeverity`, and set `OnDetectorFailure`.
- Decide on `RequireReview`, and review the queue more often than the `ReasonletsUnusedFor` window, or retention deletes unreviewed Reasonlets.
- Schedule `PruneAsync` with a policy that matches the retention you state.
- Write the erase procedure: stop writing for the subject, call `EraseAsync`, retry on `Conflict`, then rotate backups.
- Encrypt the database storage, its WAL archive and its backups.
- Export telemetry as it is. It carries no user data. Keep `IncludeErrorDetail` off in production.
- Write down who is controller and who is processor, and list the categories from the table above in your record of processing.
- Test an erase and your access query against a copy of your data before the first request arrives.
