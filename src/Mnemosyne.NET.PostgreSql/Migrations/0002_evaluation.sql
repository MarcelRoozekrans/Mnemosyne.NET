-- Phase 2.1: the verdict on each run. An execution keeps every input to its verdict, and the effective success
-- that the counters, search, erase and prune read:
--
--   agent_success      the agent's own flag, the column that 0001 named success;
--   evaluated_success  an evaluator's verdict, with its evaluation_score and the evaluator's name, all three null
--                      unless an evaluator judged the run;
--   feedback_worked    a human's verdict from feedback, null until one is recorded;
--   success            the effective success: coalesce(feedback_worked, evaluated_success, agent_success).
--
-- The two CHECKs let the database itself hold the rule, so the stored success cannot drift from its inputs.
--
-- The ZeroAlloc.ORM migration runner applies each file in one transaction, with its history row, so this file
-- applies whole or not at all; MigrationTests pins that. It has no BEGIN or COMMIT of its own, which would end
-- the runner's transaction early. The ALTERs take an ACCESS EXCLUSIVE lock on the executions table, and the
-- backfill rewrites every row, so the upgrade holds the table for as long as that takes.
--
-- Existing feedback carries no verdict, so feedback_worked starts null and every existing run keeps its success.
-- A binary from before this migration fails loudly against the new schema, because its insert omits the NOT NULL
-- agent_success; stop old instances before migrating.

ALTER TABLE mnemosyne.reasonlet_executions RENAME COLUMN success TO agent_success;

ALTER TABLE mnemosyne.reasonlet_executions
    ADD COLUMN evaluated_success boolean          NULL,
    ADD COLUMN evaluation_score  double precision NULL,
    ADD COLUMN evaluator         text             NULL CHECK (length(evaluator) <= 100),
    ADD COLUMN feedback_worked   boolean          NULL,
    ADD COLUMN success           boolean          NULL;

UPDATE mnemosyne.reasonlet_executions SET success = agent_success;

ALTER TABLE mnemosyne.reasonlet_executions
    ALTER COLUMN success SET NOT NULL,
    ADD CONSTRAINT reasonlet_executions_effective_success
        CHECK (success = coalesce(feedback_worked, evaluated_success, agent_success)),
    ADD CONSTRAINT reasonlet_executions_evaluation_complete
        CHECK ((evaluated_success IS NULL) = (evaluator IS NULL) AND (evaluated_success IS NULL) = (evaluation_score IS NULL));

-- EnsureMigratedAsync reads it, so a binary that needs this schema answers Unavailable on a database without it.
INSERT INTO mnemosyne.settings (key, value) VALUES ('schema_version', '2');
