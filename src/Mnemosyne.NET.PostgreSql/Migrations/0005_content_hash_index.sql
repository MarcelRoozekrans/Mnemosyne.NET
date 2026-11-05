-- Phase 3.6: a remember without a parent whose graph is already stored in the scope records an execution of that
-- Reasonlet instead of inserting another one. The store finds the Reasonlet by its content hash, and this migration
-- adds the index that lookup reads:
--
--   reasonlets_scope_content_hash  (scope, content_hash), so a scope's Reasonlets of one shape are found without a scan
--                                  of the scope.
--
-- The index is not unique, on purpose. One hash may sit on several rows of a scope: a shape that came back as a new
-- version in another lineage, and the duplicates that parentless remembers stored before this migration. The store
-- keeps two concurrent remembers of one shape from both inserting with a transaction-scoped advisory lock on the
-- scope and hash, not with a constraint.
--
-- The ZeroAlloc.ORM migration runner applies each file in one transaction, with its history row, so this file applies
-- whole or not at all. It has no BEGIN or COMMIT of its own, which would end the runner's transaction early. For the
-- same reason the index is a plain CREATE INDEX and not CREATE INDEX CONCURRENTLY, which PostgreSQL refuses inside a
-- transaction. The build holds a SHARE lock on the Reasonlets table while it reads the whole table. Remember inserts,
-- approve, erase and prune wait for it; appends and feedback do not, because their row locks do not conflict with SHARE.
--
-- min_binary_schema stays at 3: a binary from before this migration works against the extra index, so only
-- schema_version moves. Such a binary still inserts a new Reasonlet for a parentless repeat, as it did before.

CREATE INDEX reasonlets_scope_content_hash ON mnemosyne.reasonlets (scope, content_hash);

-- EnsureMigratedAsync reads schema_version: a database below the binary's schema needs MigrateAsync. Idempotent, as in
-- 0003.
INSERT INTO mnemosyne.settings (key, value) VALUES ('schema_version', '5')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;
