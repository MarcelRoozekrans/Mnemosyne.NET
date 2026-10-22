-- The Mnemosyne schema. Every statement names the schema in full; there is no search_path.
-- The embedding column is left as an untyped vector here: a migration file is static, so it
-- cannot know the configured dimension. MigrateAsync alters it to vector(N) and creates the
-- HNSW index after this runs, and records N in mnemosyne.settings.

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
    content_hash  char(64)    NOT NULL,
    -- SET NULL: erasing a Reasonlet keeps its descendants and cuts their lineage link. The lineage is
    -- derived from parent_id alone, so an orphan's subtree becomes its own lineage with its numbering intact.
    parent_id     uuid        NULL REFERENCES mnemosyne.reasonlets (id) ON DELETE SET NULL,
    status        smallint    NOT NULL,
    source        text        NOT NULL,
    run_id        text        NULL,
    subject_id    text        NULL,
    tags          jsonb       NULL,
    created_at    timestamptz NOT NULL,
    embedding     vector      NOT NULL
);

CREATE INDEX reasonlets_scope_status_intent ON mnemosyne.reasonlets (scope, status, intent);
CREATE INDEX reasonlets_scope_parent_id ON mnemosyne.reasonlets (scope, parent_id);
-- Unscoped: ON DELETE SET NULL finds the rows to cut by parent_id alone.
CREATE INDEX reasonlets_parent_id ON mnemosyne.reasonlets (parent_id);
CREATE INDEX reasonlets_scope_content_hash ON mnemosyne.reasonlets (scope, content_hash);
CREATE INDEX reasonlets_scope_subject_id ON mnemosyne.reasonlets (scope, subject_id);

CREATE TABLE mnemosyne.reasonlet_executions (
    id              uuid             PRIMARY KEY,
    reasonlet_id    uuid             NOT NULL REFERENCES mnemosyne.reasonlets (id) ON DELETE CASCADE,
    scope           text             NOT NULL,
    executed_at     timestamptz      NOT NULL,
    success         boolean          NOT NULL,
    duration_ticks  bigint           NOT NULL,
    score           double precision NULL,
    source          text             NOT NULL,
    run_id          text             NULL,
    subject_id      text             NULL,
    tags            jsonb            NULL
);

CREATE INDEX reasonlet_executions_scope_subject_id ON mnemosyne.reasonlet_executions (scope, subject_id);
CREATE INDEX reasonlet_executions_reasonlet_id ON mnemosyne.reasonlet_executions (reasonlet_id);

-- One row per execution: recording feedback again replaces it.
CREATE TABLE mnemosyne.reasonlet_feedback (
    execution_id  uuid             PRIMARY KEY REFERENCES mnemosyne.reasonlet_executions (id) ON DELETE CASCADE,
    scope         text             NOT NULL,
    utility       double precision NOT NULL,
    correctness   double precision NOT NULL,
    reusability   double precision NOT NULL,
    recorded_at   timestamptz      NOT NULL
);

-- The dimensions key records the embedding dimension MigrateAsync applied.
CREATE TABLE mnemosyne.settings (
    key    text PRIMARY KEY,
    value  text
);
