-- Phase 2.4: how many runs were judged. A run is judged when an evaluator or a human gave it a verdict, so when its
-- evaluated_success or feedback_worked is not null; any other run carries only the agent's own flag. Each metrics row
-- gains two totals of record, kept like runs and successes, so a prune keeps them too:
--
--   judged_runs       the Reasonlet's judged runs;
--   judged_successes  those of them whose effective success is true.
--
-- The backfill counts the executions still stored. A pruned run's verdict is gone with its row, so it counts as
-- agent-only. That understates how much of a Reasonlet's history was judged, which is the safe direction: its success
-- rate leans on its prior a little more.
--
-- The four CHECKs let the database itself hold the rules, so a writer that drifts fails loudly rather than storing a
-- count the rate would trust. The last one keeps the agent-only successes, successes - judged_successes, at or below
-- the agent-only runs, runs - judged_runs, which the first three alone allow to break. Every row the backfill writes
-- meets them as long as runs and successes count at least the stored executions and their successes, which every
-- writer has kept since 0001; a row that does not makes the migration fail whole rather than apply in part.
--
-- The ZeroAlloc.ORM migration runner applies each file in one transaction, with its history row, so this file applies
-- whole or not at all. It has no BEGIN or COMMIT of its own, which would end the runner's transaction early. The ALTERs
-- take an ACCESS EXCLUSIVE lock on the metrics table and the backfill reads every execution, so the upgrade holds the
-- metrics table for as long as that takes.
--
-- A binary from before this migration leaves the new counters alone, and the CHECKs refuse some of its writes; stop old
-- instances before migrating. min_binary_schema lets a later migration lock older binaries out by itself: a binary
-- answers Unavailable when the database asks for a newer schema than its own.

ALTER TABLE mnemosyne.reasonlet_metrics
    ADD COLUMN judged_runs      integer NOT NULL DEFAULT 0,
    ADD COLUMN judged_successes integer NOT NULL DEFAULT 0;

UPDATE mnemosyne.reasonlet_metrics m
SET judged_runs = x.judged_runs,
    judged_successes = x.judged_successes
FROM (
    SELECT scope, reasonlet_id,
           (count(*) FILTER (WHERE evaluated_success IS NOT NULL OR feedback_worked IS NOT NULL))::int AS judged_runs,
           (count(*) FILTER (WHERE (evaluated_success IS NOT NULL OR feedback_worked IS NOT NULL) AND success))::int AS judged_successes
    FROM mnemosyne.reasonlet_executions
    GROUP BY scope, reasonlet_id
) x
WHERE m.scope = x.scope AND m.reasonlet_id = x.reasonlet_id;

ALTER TABLE mnemosyne.reasonlet_metrics
    ADD CONSTRAINT reasonlet_metrics_judged_runs
        CHECK (judged_runs >= 0 AND judged_runs <= runs),
    ADD CONSTRAINT reasonlet_metrics_judged_successes
        CHECK (judged_successes >= 0 AND judged_successes <= judged_runs),
    ADD CONSTRAINT reasonlet_metrics_judged_successes_counted
        CHECK (judged_successes <= successes),
    ADD CONSTRAINT reasonlet_metrics_unjudged_successes
        CHECK (successes - judged_successes <= runs - judged_runs);

-- EnsureMigratedAsync reads both: a database whose schema_version is below the binary's schema needs MigrateAsync,
-- and a binary whose schema is below min_binary_schema is too old for the database. Idempotent, as in 0002.
INSERT INTO mnemosyne.settings (key, value) VALUES ('schema_version', '3'), ('min_binary_schema', '3')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;
