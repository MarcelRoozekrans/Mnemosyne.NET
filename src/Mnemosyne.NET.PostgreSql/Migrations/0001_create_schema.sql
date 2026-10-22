-- The Mnemosyne schema. Every statement names the schema in full; there is no search_path.
-- The embedding column is left as an untyped vector here: a migration file is static, so it
-- cannot know the configured dimension. MigrateAsync alters it to vector(N) and creates the
-- HNSW index after this runs, and records N and the HNSW options in mnemosyne.settings.
--
-- Every foreign key carries scope next to the id it references, against a UNIQUE (scope, id),
-- so no row can point at a row in another scope. The column-list form of ON DELETE SET NULL
-- needs PostgreSQL 15 or later, which MigrateAsync checks.

CREATE SCHEMA IF NOT EXISTS mnemosyne;

CREATE EXTENSION IF NOT EXISTS vector;

-- One row per Reasonlet version.
CREATE TABLE mnemosyne.reasonlets (
    id            uuid        PRIMARY KEY,
    scope         text        NOT NULL,
    name          text        NOT NULL,
    intent        text        NOT NULL,
    version       text        NOT NULL,
    -- text, not jsonb: jsonb normalises numbers and key order, so the graph read back would no
    -- longer match content_hash.
    graph         text        NOT NULL,
    content_hash  text        NOT NULL CHECK (length(content_hash) = 64),
    parent_id     uuid        NULL,
    status        smallint    NOT NULL,
    source        text        NOT NULL,
    run_id        text        NULL,
    subject_id    text        NULL,
    tags          jsonb       NULL,
    created_at    timestamptz NOT NULL,
    embedding     vector      NOT NULL,
    UNIQUE (scope, id),
    -- SET NULL (parent_id): erasing a Reasonlet keeps its descendants and cuts their lineage link,
    -- leaving scope alone. The lineage is derived from parent_id alone, so an orphan's subtree
    -- becomes its own lineage with its numbering intact.
    FOREIGN KEY (scope, parent_id) REFERENCES mnemosyne.reasonlets (scope, id) ON DELETE SET NULL (parent_id)
);

CREATE INDEX reasonlets_scope_status_intent ON mnemosyne.reasonlets (scope, status, intent);
-- The walks down the lineage and the foreign key's SET NULL both look rows up by (scope, parent_id).
CREATE INDEX reasonlets_scope_parent_id ON mnemosyne.reasonlets (scope, parent_id);
CREATE INDEX reasonlets_scope_subject_id ON mnemosyne.reasonlets (scope, subject_id);
-- Retention judges a Reasonlet by when it was remembered as well as when it last ran, so it finds the recently
-- remembered ones by created_at, across scopes.
CREATE INDEX reasonlets_created_at ON mnemosyne.reasonlets (created_at);

CREATE TABLE mnemosyne.reasonlet_executions (
    id              uuid             PRIMARY KEY,
    reasonlet_id    uuid             NOT NULL,
    scope           text             NOT NULL,
    executed_at     timestamptz      NOT NULL,
    success         boolean          NOT NULL,
    duration_ticks  bigint           NOT NULL,
    score           double precision NULL,
    source          text             NOT NULL,
    run_id          text             NULL,
    subject_id      text             NULL,
    tags            jsonb            NULL,
    UNIQUE (scope, id),
    FOREIGN KEY (scope, reasonlet_id) REFERENCES mnemosyne.reasonlets (scope, id) ON DELETE CASCADE
);

CREATE INDEX reasonlet_executions_scope_subject_id ON mnemosyne.reasonlet_executions (scope, subject_id);
-- The foreign key's cascade and the erase, which recomputes a kept Reasonlet's timestamps from the executions it
-- leaves, look executions up by Reasonlet.
CREATE INDEX reasonlet_executions_scope_reasonlet_id ON mnemosyne.reasonlet_executions (scope, reasonlet_id);
-- Retention deletes the executions older than a cutoff, across scopes, so it finds them by executed_at.
CREATE INDEX reasonlet_executions_executed_at ON mnemosyne.reasonlet_executions (executed_at);

-- One row per execution: recording feedback again replaces it.
CREATE TABLE mnemosyne.reasonlet_feedback (
    execution_id  uuid             PRIMARY KEY,
    scope         text             NOT NULL,
    utility       double precision NOT NULL,
    correctness   double precision NOT NULL,
    reusability   double precision NOT NULL,
    recorded_at   timestamptz      NOT NULL,
    FOREIGN KEY (scope, execution_id) REFERENCES mnemosyne.reasonlet_executions (scope, id) ON DELETE CASCADE
);

-- One row per Reasonlet: its execution counts and times, kept in the transaction of every execution
-- insert, and subtracted by an erase that removes a subject's executions from a Reasonlet it keeps.
-- The counters are the totals of record, not an aggregate of the executions: retention prunes old
-- executions and leaves their counts here, so a recount from what remains would undercount. Search
-- reads this row instead of aggregating the executions.
CREATE TABLE mnemosyne.reasonlet_metrics (
    reasonlet_id      uuid        PRIMARY KEY,
    scope             text        NOT NULL,
    runs              integer     NOT NULL,
    successes         integer     NOT NULL,
    last_success_at   timestamptz NULL,
    -- NOT NULL: every Reasonlet has at least the execution its remember created, and an erase that
    -- removes every remaining run keeps the stored time. A null would read as never used to retention.
    last_executed_at  timestamptz NOT NULL,
    FOREIGN KEY (scope, reasonlet_id) REFERENCES mnemosyne.reasonlets (scope, id) ON DELETE CASCADE
);

-- Retention finds the Reasonlets that have not run since a cutoff by last_executed_at, across scopes.
CREATE INDEX reasonlet_metrics_last_executed_at ON mnemosyne.reasonlet_metrics (last_executed_at);

-- The dimensions, hnsw_m and hnsw_ef_construction keys record what MigrateAsync applied.
CREATE TABLE mnemosyne.settings (
    key    text PRIMARY KEY,
    value  text
);
