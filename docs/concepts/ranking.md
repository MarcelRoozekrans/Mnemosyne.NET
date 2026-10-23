---
title: Ranking
sidebar_position: 4
---

# Ranking

Recall answers one question: of the procedures stored for this scope, which are most worth trying for this question? Similarity alone is not enough. A procedure that matches the words but fails half the time should lose to one that matches a little less and always works. So recall ranks on three things: how similar the procedure is, how often it succeeds, and how recently it ran.

This page describes the default ranking, `ReasonletSelector`, with its default settings.

## What recall does

1. **Resolve the intent filter.** The query's intent if it has one, else an extracted intent when the extractor is at least 70% confident, else no filter. [Reasonlets](reasonlets.md#intent) explains this.
2. **Embed the question** with the registered embedding generator.
3. **Search** the scope for the nearest `Active` Reasonlets. `PendingReview` Reasonlets are never candidates. The search asks for a pool of 20 candidates, or for the query's `Top` when that is larger, up to 200.
4. **Cut off** every candidate whose similarity is below 0.5.
5. **Score** the rest and sort them, best first.
6. **Return** the first `Top` matches, 3 by default.

The pool is larger than `Top` on purpose. A candidate that is only fifth by similarity can still be the best procedure once its track record counts, and a pool of three would never have seen it.

## The cut-off

Similarity is the cosine similarity between the question's embedding and the stored question's embedding, from 0 to 1. A candidate below the cut-off is dropped before scoring, however good its record.

The default cut-off is 0.5. A query can set its own with `ReasonletQuery.MinSimilarity`, from 0 to 1, and you can change the default with `ReasonletRankingOptions.MinSimilarity`.

**Tune the cut-off for your embedding model.** Similarity scales differ between models: one model's 0.5 is a weak match, another's a strong one. Recall a few questions you know have a stored answer and a few you know don't, and set the cut-off between the two groups. The recall activity's candidate and returned counts, described under [Telemetry](../guides/telemetry.md#the-recall-activity), show how much the cut-off drops in production.

When nothing passes the cut-off, recall returns an empty list. That is a success, not an error: the agent has no stored procedure to go on and works from scratch.

## The score

Each candidate that passes the cut-off gets a total score:

```text
total = 0.5 × similarity + 0.3 × success rate + 0.2 × freshness
```

Each part is between 0 and 1, so the total is too. Every match carries all four numbers in its `ReasonletScore`: `Similarity`, `SuccessRate`, `Freshness` and `Total`. You can log them, show them to a user, or use them to decide whether the best match is good enough.

### Success rate

The success rate is the share of runs that succeeded, smoothed so that a handful of runs can't swing it to 0 or 1:

```text
success rate = (successes + 5 × prior) / (runs + 5)
```

The 5 is the prior weight: the prior counts as five extra runs. For a procedure with no parent, the prior is a neutral 0.5. So a brand-new procedure with one successful run scores (1 + 2.5) / 6 = 0.583, not 1. After 20 successes out of 20 it scores 22.5 / 25 = 0.900, and the prior matters less and less as runs accumulate.

Every remember records one run, and every `RecordExecutionAsync` records another. Whether a run counts as a success depends on its effective verdict, the first of these that exists:

1. **A human's verdict**, `ReasonletFeedback.Worked`, recorded with `RecordFeedbackAsync`.
2. **An evaluator's verdict**, when an `IReasonletEvaluator` is registered and the run was recorded with its answer.
3. **The agent's own verdict**, `ReasonletOutcome.Success`.

Feedback that changes a run's verdict moves the success count with it. Feedback with `Worked` null keeps an earlier verdict, so a verdict can be changed but not withdrawn. Feedback's three scores are stored, but ranking doesn't use them.

### A new version inherits upward only

A new version has had few runs of its own. With the neutral prior it would look as unproven as any new procedure and rank below the parent it improves on. So a version takes its parent's record as its prior, as long as that record is better than neutral:

```text
prior = max(parent's success rate, 0.5)
```

- The parent's success rate here is the parent's own, smoothed with the neutral 0.5 prior.
- **It only lifts.** A version of a proven procedure starts from its parent's record. A version of a failing procedure, which is usually a fix, starts at 0.5 like anything new, so its parent's failures don't drag it down.
- **It goes one level.** Only the parent counts, not the grandparent, because the parent is the version this one replaced.
- **It needs a parent.** When the parent has been erased, the link is cut and the version ranks as a root, with the 0.5 prior. See [Versions and lineage](versions.md#what-erasure-does-to-a-lineage).

As the version runs, its own successes and failures outweigh the inherited prior, just as they outweigh the neutral one.

### Freshness

Freshness halves every 30 days since the procedure last ran:

```text
freshness = 0.5 ^ (days since the last run / 30)
```

| Last run | Freshness |
|---|---|
| just now | 1.000 |
| 30 days ago | 0.500 |
| 60 days ago | 0.250 |
| 90 days ago | 0.125 |

"Last run" is the time of the latest recorded run, which a remember also sets. "Now" comes from the registered `TimeProvider`. If clock skew between your host and the database puts the last run in the future, freshness is 1.

### Ties

Matches sort by total, highest first. Equal totals sort by similarity, highest first, and then by id, so the order is always the same for the same data.

## A worked example

The [store-and-recall sample](../samples.md) stores three procedures in one scope and recalls "How do I refund an order?":

- **Proven original**, "refund an order" `1.0`: 20 runs, all successful.
- **New version** of it, `1.1`: the same question with an improved graph, and one successful run of its own.
- **Close match**, "refund a partial order" `1.0`: a question worded closer to the query, but only 5 successes in 10 runs.

All three ran seconds before the recall, so each freshness is 1.000 to three decimals. The sample prints:

| Reasonlet | Similarity | Success rate | Freshness | Total |
|---|---|---|---|---|
| New version, `1.1` | 0.816 | 0.917 | 1.000 | **0.883** |
| Proven original, `1.0` | 0.816 | 0.900 | 1.000 | 0.878 |
| Close match, `1.0` | 0.904 | 0.500 | 1.000 | 0.802 |

Working through each row:

- **Proven original.** It has no parent, so its prior is 0.5: (20 + 5 × 0.5) / (20 + 5) = 22.5 / 25 = 0.900. Total: 0.5 × 0.816 + 0.3 × 0.900 + 0.2 × 1.000 = 0.878.
- **New version.** Its parent's rate is 0.900, which beats 0.5, so its prior is 0.900. With one success of its own: (1 + 5 × 0.900) / (1 + 5) = 5.5 / 6 = 0.917. Total: 0.5 × 0.816 + 0.3 × 0.917 + 0.2 × 1.000 = 0.883.
- **Close match.** No parent, prior 0.5: (5 + 2.5) / (10 + 5) = 7.5 / 15 = 0.500. Total: 0.5 × 0.904 + 0.3 × 0.500 + 0.2 × 1.000 = 0.802.

Three things to take from it:

- **The close match has the highest similarity and still ranks last.** Its record costs it more than its similarity gains.
- **The new version ranks first after a single run.** It inherits the original's record, so the improvement isn't buried below what it improves on.
- **Without inheritance, it would rank last.** With the neutral prior its success rate would be (1 + 2.5) / 6 = 0.583, for a total of 0.783, below both others.

The similarities come from the sample's offline stand-in for an embedding model, which only counts shared words. A real model gives different numbers, but the same reasoning applies.

## When recall fails

Recall returns a result, and fails only for a real problem:

| Problem | Result |
|---|---|
| The query is invalid, for example a blank question or `Top` outside 1 to 50 | `Validation` |
| The embedding generator throws, times out or returns nothing | `Unavailable` |
| The embedding has the wrong number of dimensions, a NaN or infinite value, a zero norm, or a norm whose squares overflow | `Unavailable` |
| The store or the selector fails | its error, unchanged, usually `Unavailable` |
| The intent extractor is down | no error: recall searches every intent instead |
| The intent extractor answers an error code other than `Unavailable`, or an invalid intent | `InvalidOperationException` is thrown |
| The store returns an invalid candidate | `InvalidOperationException` is thrown |
| Your cancellation token fires | `OperationCanceledException` is thrown |

A dependency is **down** when it throws, or when it times out: an `OperationCanceledException` while your own token is not cancelled, such as an `HttpClient` timeout. An extractor that answers `Unavailable` is down too.

An unusable embedding is `Unavailable`, not `Validation`, because the generator made it, not you: nothing in your input is invalid. The facade checks it against the store's `IReasonletStore.Dimensions` before calling the store.

The two cases that throw are bugs in an adapter, not runtime conditions. The extractor is always given a valid question, so any other error code from it, or an invalid intent, means the adapter is wrong, and so does an invalid candidate from a store.

`RecallAsync` reports every exception through the task it returns, a null query included, rather than throwing before it returns, so the recall's telemetry records it. The exception surfaces when you await the task.

Remember fails the same way for an invalid capture, for an embedding problem and for an extractor bug. Unlike recall, it has no fallback when the intent extractor is needed and is down: it answers `Unavailable`, so a guessed intent is never stored. Store errors, `Validation` included, come back from both unchanged.

Remember and `RecordExecutionAsync` call the evaluator, when one is registered and the run carries an answer. A judge that is down doesn't fail either call: the run is stored with the agent's own verdict. A judge that takes longer than `MnemosyneOptions.EvaluationTimeout`, 30 seconds by default, is down: the facade cancels the token it passed the judge and stops waiting, even for a judge that ignores the token. Your cancellation token stops the wait the same way. Any other error code from the judge, or an invalid verdict, is a bug in the adapter, and `InvalidOperationException` is thrown.

An answer that is blank or longer than 32,000 Unicode code points is not judged. It is the agent's own output, so it doesn't fail the call either: the run is stored with the agent's verdict. Remember embeds the question before it calls the judge, so a remember that fails on the embedding never pays for a judge call.

Both calls return the verdict that counted when the run was stored. `RecordExecutionAsync` returns a `ReasonletRunReceipt`, and the receipt from remember carries the same two fields for the run it records: `Source` is `Evaluator` when the judge answered and `Agent` otherwise, and `Success` is that verdict. It is never `Feedback`, because feedback comes later.

No error from the facade's own dependencies quotes the question or an exception's message, because either could hold user data:

- **The embedding generator.** If it throws or times out, the error's detail names only the exception type. If it returns no embedding, there is no detail. If its embedding is unusable, the error's message is fixed and the detail gives only the reason: `length`, with both lengths for a wrong one, `non-finite` or `norm`.
- **The intent extractor at recall.** A failure is not surfaced at all, because recall searches without an intent filter instead.
- **The intent extractor at remember.** If it throws or times out, the remember answers `Unavailable` and the detail names only the exception type. If it answers `Unavailable` itself, the remember answers `Unavailable` with no detail, so none of the extractor's own text is passed on.
- **The evaluator.** A judge that is down is not surfaced, because the run is stored with the agent's verdict. The exception for a judge's bug names only its error code, or the field that failed validation and why. The run's answer never appears in an error.

A store error is different: its detail can carry the database's error message, a SQLSTATE code and the server's message text. That text doesn't include row values unless the host turns on Npgsql's `IncludeErrorDetail` connection setting.

## Settings

The ranking settings live in `ReasonletRankingOptions`, set through `MnemosyneOptions.Ranking`. The pool, the intent threshold and the judge's timeout are on `MnemosyneOptions` itself.

```csharp
services.AddMnemosyne(new MnemosyneOptions
{
    CandidatePool = 40,
    Ranking = new ReasonletRankingOptions { MinSimilarity = 0.6 },
});
```

| Option | Default | Allowed |
|---|---|---|
| `Ranking.SimilarityWeight` | 0.5 | 0 to 1 |
| `Ranking.SuccessRateWeight` | 0.3 | 0 to 1 |
| `Ranking.FreshnessWeight` | 0.2 | 0 to 1 |
| `Ranking.MinSimilarity` | 0.5 | 0 to 1 |
| `Ranking.PriorWeight` | 5 | finite and above 0 |
| `Ranking.FreshnessHalfLife` | 30 days | positive |
| `CandidatePool` | 20 | 1 to 200 |
| `MinIntentConfidence` | 0.7 | 0 to 1 |
| `EvaluationTimeout` | 30 seconds | positive and at most about 49.7 days, or `Timeout.InfiniteTimeSpan` for none |

The three weights must add up to 1, within 1e-9. `AddMnemosyne` checks the options when you register them and throws `ArgumentException` for an invalid value.

On each query, `Top` sets how many matches come back, from 1 to 50 with a default of 3, and `MinSimilarity` overrides the cut-off.
