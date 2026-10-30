-- Phase 3.3: the human review gate. A Reasonlet stored with status 2, pending review, is left out of search until a
-- reviewer approves it, and a reviewer lists what waits. This migration adds the partial index that list reads:
--
--   reasonlets_pending  (scope, id) over the rows with status = 2, so a scope's review queue is found and paged by id
--                       without a scan of its active Reasonlets.
--
-- No column changes: status 2 has been in the schema since 0001, and no writer used it before the gate.
--
-- The ZeroAlloc.ORM migration runner applies each file in one transaction, with its history row, so this file applies
-- whole or not at all. It has no BEGIN or COMMIT of its own, which would end the runner's transaction early. For the
-- same reason the index is a plain CREATE INDEX and not CREATE INDEX CONCURRENTLY, which PostgreSQL refuses inside a
-- transaction. The build holds a SHARE lock on the Reasonlets table, which blocks writes to it for as long as the build
-- takes; the index covers only pending rows, and there are none before the gate is turned on, so that is short.
--
-- min_binary_schema stays at 3: a binary from before this migration works against the extra index, so only
-- schema_version moves.

CREATE INDEX reasonlets_pending ON mnemosyne.reasonlets (scope, id) WHERE status = 2;

-- EnsureMigratedAsync reads schema_version: a database below the binary's schema needs MigrateAsync. Idempotent, as in
-- 0003.
INSERT INTO mnemosyne.settings (key, value) VALUES ('schema_version', '4')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;
