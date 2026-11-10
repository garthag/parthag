-- SiteNotes DPR – SQLite schema (single-file server database for small sites)
CREATE TABLE IF NOT EXISTS SyncRecords (
    Id            TEXT    NOT NULL,
    EntityType    TEXT    NOT NULL,
    ProjectId     TEXT    NOT NULL,
    Json          TEXT    NOT NULL,
    UpdatedAtUtc  TEXT    NOT NULL,
    IsDeleted     INTEGER NOT NULL DEFAULT 0,
    Version       INTEGER NOT NULL,
    DeviceId      TEXT    NOT NULL DEFAULT '',
    ReceivedAtUtc TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    PRIMARY KEY (EntityType, Id)
);
GO
CREATE INDEX IF NOT EXISTS IX_SyncRecords_Version ON SyncRecords (Version);
GO
CREATE TABLE IF NOT EXISTS SyncCounter (Id INTEGER PRIMARY KEY, Value INTEGER NOT NULL);
GO
INSERT OR IGNORE INTO SyncCounter (Id, Value) VALUES (1, 0);
