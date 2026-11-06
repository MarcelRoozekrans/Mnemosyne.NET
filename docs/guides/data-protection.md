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
| The embedding of the question | The embedding generator you register, run over the capture's question | Yes, in principle. The question itself is not stored, but a vector made from it is, and it is built from the person's words. |
| The name | The capture's `Name`, or the first line of the question, cut to 200 code points, when you set none | Yes, when it is taken from the question. The chat middleware always sets a name made of tool names, so it avoids this. |
| The intent | The capture's `Intent`, or the label an `IIntentExtractor` returns | Possibly, when your extractor or your caller puts a person's details in the label. |
| The graph | The steps of the run: tool names and argument names, never argument values | Rarely. Tool and argument names are code, not input. |
| Provenance: source, run id, subject id, tags | Your application | Yes. The subject id names the person, and tags are free-form key-value pairs. |
| Runs | One row per recorded run: time, duration, verdicts, evaluator name, score, provenance | Yes, through the subject id and tags on the run. |
| Feedback | Three scores between 0 and 1 and an optional verdict per run | No text. It links to a person only through its run. |
| Review state | `Active` or `PendingReview` on each Reasonlet | No. No reviewer identity is stored. |
| Counters | Run and success counts and two timestamps per Reasonlet | No. They are bare numbers with no link to a person. |

The answer of a run reaches an evaluator when you register one, and is never stored. [Reasonlets](../concepts/reasonlets.md) describes each field, and [Scopes and subjects](../concepts/scopes-and-subjects.md#subjects) says how to name the person a record is about.

## Duty to feature

| Duty | What supports it | Where it is documented |
|---|---|---|
| Erase on request, Art. 17 | `EraseAsync` removes everything in a scope, or everything about one subject in a scope, in one transaction. Runs follow their procedure, and the counters of kept procedures are adjusted. | [Erasure](data-lifecycle.md#erasure) and [What an erase removes](data-lifecycle.md#what-an-erase-removes) |
| Data minimisation, Art. 5(1)(c) | The capture guard refuses a capture before it is stored, and the answer is never stored. | [The capture guard](capture-and-review.md#the-capture-guard) and [What it scans](capture-and-review.md#what-it-scans) |
| Data protection by design and by default, Art. 25 | `MnemosyneOptions.RequireReview` holds a new Reasonlet and a new version back from recall until a person approves it. Scopes keep tenants apart in every call. Telemetry carries no user data. | [The review gate](data-lifecycle.md#the-review-gate), [Scopes](../concepts/scopes-and-subjects.md#scopes) and [Privacy](telemetry.md#privacy) |
| Storage limitation, Art. 5(1)(e) | `PruneAsync` deletes old run records and Reasonlets nobody uses, on a schedule the host runs. | [Retention](data-lifecycle.md#retention) |
| Accountability, Art. 5(2) and records of processing, Art. 30 | Each Reasonlet and run carries its provenance. Receipts return the ids to correlate on. The table above lists the categories of data to put in your own record. Mnemosyne does not keep that record for you. | [Reasonlets](../concepts/reasonlets.md), [Telemetry](telemetry.md) |
| Security of processing, Art. 32 | Encryption and backup rotation are a storage concern for the database. Telemetry carries no question, subject or scope, so an exporter needs no scrubbing. The store's role needs only the privileges listed. | [What erasure leaves on disk](postgresql.md#what-erasure-leaves-on-disk), [Privileges](postgresql.md#privileges) and [Privacy](telemetry.md#privacy) |
| Access, Art. 15, and portability, Art. 20 | See below. The library offers no export. | [Access and portability](#access-and-portability) |

### Access and portability

`IReasonletMemory` has no call that lists or reads records by subject. It offers `RememberAsync`, `RecallAsync`, `RecordExecutionAsync`, `RecordFeedbackAsync`, `EraseAsync`, and the review calls `ListPendingAsync`, `ApproveAsync` and `RejectAsync`. `ListPendingAsync` lists one scope's pending Reasonlets, not a subject's. `IReasonletStore` adds `GetAsync`, which reads one Reasonlet by id, and `SearchAsync`, which finds the nearest Reasonlets to an embedding. Neither selects by subject, and no call returns the runs of a subject. There is no export format.

To answer an access or portability request, query the `mnemosyne` schema yourself. The subject id is a column on `reasonlets` and on `reasonlet_executions`, with an index on `(scope, subject_id)`. [PostgreSQL store](postgresql.md) describes the tables. Only the subject's own records are findable this way. Records captured without a subject id are not.

## What Mnemosyne does not do

Each item says what to do about it.

- **It does not scrub storage after an erase.** An erase deletes rows. Deleted data can stay in the data files, indexes, write-ahead log, replicas and backups until they are overwritten or rotated. Encrypt the storage, rotate backups and archived WAL on a schedule that matches what you tell people, and know that a restore brings erased data back. [What erasure leaves on disk](postgresql.md#what-erasure-leaves-on-disk) has the detail.
- **It does not remove personal data from an embedding before storing it.** An erase removes the row and its vector. The text was already sent to your embedding generator to make the vector, and what that provider keeps is outside Mnemosyne. Check the provider's retention terms, and use the capture guard to keep what you do not want embedded out of the question.
- **It does not make the guard fail closed for a single detector.** The guard fails closed when the whole pipeline throws or the timeout runs out. With AI.Sentinel 4.2.0, which this release pins, a single detector that throws is skipped, its result counts as clean and the capture is allowed. A fix that reports this is upstream but not adopted here; it is tracked as [Mnemosyne.NET#58](https://github.com/MarcelRoozekrans/Mnemosyne.NET/issues/58). Until then, set `SentinelOptions.OnDetectorFailure` in your AI.Sentinel options, alert on it, and do not treat a clean capture as proof that a detector ran. [A known limit: a throwing detector](capture-and-review.md#a-known-limit-a-throwing-detector) has the detail.
- **It does not guard everything.** Only `RememberAsync` passes the guard. `RecordExecutionAsync` and writes straight to `IReasonletStore` do not. The question and answer of a run go to your evaluator unguarded. The guard also sees only the question, the intent and the name, not the answer or the provenance, and at the default `High` threshold it lets an email address or a phone number through. Set `BlockAtSeverity` to `Medium` if you want those blocked, and check for false positives. See [What is guarded](capture-and-review.md#what-is-guarded) and the [adapter README](https://github.com/MarcelRoozekrans/Mnemosyne.NET/blob/main/src/Mnemosyne.NET.Sentinel/README.md).
- **It does not govern the services around it.** Your chat model, embedding generator, intent extractor, evaluator and logging middleware each see text that Mnemosyne passes to them. Their prompts, logs and retention are yours to set. Give a judge's client its own logging settings, as [Chat evaluator](chat-evaluator.md) describes.
- **It does not find a person's records without a subject id.** Erasure by subject matches the `SubjectId` on provenance. A record captured without one is not found. Set the subject on every capture that is about a person, or erase by scope.
- **It does not record who reviewed.** The review gate stores no reviewer identity or comment. Keep that in your own audit trail.
- **It does not enforce who may call it.** It does not check who may use a scope or call `EraseAsync`. Your application decides that before it calls.
- **It does not decide your legal position.** Controller and processor roles, the lawful basis, consent, notices to the people concerned and the record of processing are yours.

## The EU AI Act in short

[Regulation (EU) 2024/1689](https://eur-lex.europa.eu/eli/reg/2024/1689/oj/eng), the AI Act, regulates AI systems. The [Commission's overview](https://digital-strategy.ec.europa.eu/en/policies/regulatory-framework-ai) explains its risk-based approach. This section says where Mnemosyne stands against it. It classifies nothing and names no date.

- **Mnemosyne is a memory library.** Article 3(1) defines an AI system as a machine-based system that infers, from its input, how to generate outputs such as predictions, content, recommendations or decisions. Mnemosyne stores, finds and ranks procedures with an embedding and a score. It generates none of those outputs and trains no model. It is a component of your application.
- **The Act separates providers and deployers.** Article 3 defines a provider as the party that develops an AI system, or has it developed, and places it on the market or puts it into service under its own name, and a deployer as the party that uses an AI system under its own authority. Provider duties attach to a system or model, so they do not attach to Mnemosyne directly.
- **Your system may be in scope.** An agent that uses Mnemosyne next to a model may be an AI system, and you may be its provider, its deployer or both. Whether it is high-risk depends on what it is used for. Decide that with counsel, from the Act's text. This guide does not.

For a high-risk system, the Act asks for two things that Mnemosyne's features can feed. Article 12 requires that the system technically allows automatic recording of events over its lifetime, and Article 26(6) has deployers keep the logs it generates, to the extent they control them. Article 14 requires that natural persons can effectively oversee the system while it is in use, and Article 26(2) has deployers assign that oversight to people with the competence, training and authority for it.

| Need | What Mnemosyne offers | What it does not |
|---|---|---|
| Records of what happened | Provenance on every Reasonlet and run, the receipts with their ids, and the run records with verdicts and counters. See [Reasonlets](../concepts/reasonlets.md) and [Evaluation](../concepts/evaluation.md). | It does not log the system's inputs and outputs. Pruning deletes run records on your schedule, so set retention with your logging duty in mind. |
| Correlating events across systems | Telemetry with opaque Reasonlet ids and versions. See [Telemetry](telemetry.md). | It carries no question, answer, subject or scope, by design. |
| Human oversight of what an agent follows | The review gate: a new procedure is not recalled until a person approves it. See [The review gate](data-lifecycle.md#the-review-gate). | It does not store who reviewed. Keep that in your own audit trail. |
| A person's verdict on a run | `RecordFeedbackAsync` records it and overrides the evaluator's and the agent's verdict. | It records no author for the feedback. |

Mnemosyne does not make a system satisfy Article 12 or Article 14. It holds records and a gate that you can use as part of your own logging and oversight.

## A deployer checklist

- Set `SubjectId` on every capture and run that is about a person.
- Register a capture guard, choose `BlockAtSeverity`, and set `OnDetectorFailure`.
- Decide on `RequireReview`, and review the queue more often than the `ReasonletsUnusedFor` window, or retention deletes unreviewed Reasonlets.
- Schedule `PruneAsync` with a policy that matches the retention you state.
- Write the erase procedure: stop writing for the subject, call `EraseAsync`, retry on `Conflict`, then rotate backups.
- Encrypt the database storage, its WAL archive and its backups.
- Export telemetry as it is. It carries no user data.
- Write down who is controller and who is processor, and list the categories from the table above in your record of processing.
- Test an erase and an export against a copy of your data before the first request arrives.
