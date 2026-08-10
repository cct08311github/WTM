-- WTM 8.1.13 Database Migration (run BEFORE deploying)

-- ============================================================
-- PRE-FLIGHT CHECK -- run this BEFORE anything else in this file
-- ============================================================
-- Root cause of issue #1082: earlier revisions of the P0-1 statements below
-- ALTERed a table named "FrameworkUser" (singular). The real table is
-- "FrameworkUsers" (plural) -- see the [Table("FrameworkUsers")] attribute on
-- FrameworkUserBase in src/WalkingTec.Mvvm.Core/Models/FrameworkUser.cs.
-- Querying the wrong (singular) name for the column's current width returns
-- ZERO ROWS, which looks exactly like "column is already correct, nothing to
-- do" -- it is NOT that. Zero rows from a metadata query means "the query is
-- wrong", not "no action needed". Run BOTH queries for your engine below
-- (plural and singular) and use this rule to read the result:
--   * Only the PLURAL row appears  -> normal. Check its reported length and
--     decide whether the P0-1 statement for your engine below still needs
--     to run (it is also self-guarded/documented per-provider below).
--   * Only the SINGULAR row appears -> your schema has a different shape
--     than this script assumes. STOP and ask before running anything below.
--   * BOTH rows appear              -> unexpected; STOP and ask before
--     running anything below.
--   * NEITHER row appears           -> your query is wrong for this engine
--     or connection (wrong database/schema, wrong catalog view, wrong
--     identifier case). This is NOT "no action needed" -- go find the real
--     table name first.
--
-- Pick the block below for your database engine; run only that one.

-- --- SQL Server / Azure SQL ---
SELECT t.name AS table_name, c.name AS column_name, c.max_length
FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id
WHERE t.name IN ('FrameworkUsers', 'FrameworkUser') AND c.name = 'Password';

-- --- MySQL / MariaDB ---
SELECT table_name, column_name, character_maximum_length
FROM information_schema.columns
WHERE table_schema = DATABASE()
  AND table_name IN ('FrameworkUsers', 'FrameworkUser')
  AND column_name = 'Password';

-- --- PostgreSQL ---
SELECT table_name, column_name, character_maximum_length
FROM information_schema.columns
WHERE table_name IN ('FrameworkUsers', 'FrameworkUser')
  AND column_name = 'Password';

-- --- Oracle (identifiers are case-sensitive here because the DDL below
--     quotes "FrameworkUsers" / "Password" as mixed-case identifiers) ---
SELECT table_name, column_name, char_length
FROM user_tab_columns
WHERE table_name IN ('FrameworkUsers', 'FrameworkUser')
  AND column_name = 'Password';

-- --- SQLite (dev/test only; PRAGMA does not accept an IN() list, so run
--     both statements separately and see which one returns a row) ---
PRAGMA table_info(FrameworkUsers);
PRAGMA table_info(FrameworkUser);

-- ============================================================
-- SQL Server / Azure SQL
-- ============================================================
-- P0-1: Widen Password column for PBKDF2 hashes
-- Idempotent: sys.columns.max_length for NVARCHAR is bytes (2 bytes/char), so
-- NVARCHAR(256) reads as 512; -1 means NVARCHAR(MAX), already wide enough.
-- Only ALTERs when the live column is still narrower than that.
IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.tables t ON c.object_id = t.object_id
    WHERE t.name = 'FrameworkUsers'
      AND c.name = 'Password'
      AND c.max_length <> -1
      AND c.max_length < 512
)
BEGIN
    ALTER TABLE [FrameworkUsers] ALTER COLUMN [Password] NVARCHAR(256) NOT NULL;
END

-- P0-2: Create refresh token table
CREATE TABLE [FrameworkRefreshTokens] (
    [ID]              UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
    [Token]           NVARCHAR(256)    NOT NULL,
    [ITCode]          NVARCHAR(50)     NOT NULL,
    [TenantCode]      NVARCHAR(50)     NULL,
    [ExpiresUtc]      DATETIME2        NOT NULL,
    [CreatedUtc]      DATETIME2        NOT NULL DEFAULT GETUTCDATE(),
    [CreatedByIp]     NVARCHAR(50)     NULL,
    [RevokedUtc]      DATETIME2        NULL,
    [RevokedByIp]     NVARCHAR(50)     NULL,
    [ReplacedByToken] NVARCHAR(256)    NULL,
    [RevokeReason]    NVARCHAR(100)    NULL
);
CREATE INDEX IX_RefreshToken_Token  ON [FrameworkRefreshTokens]([Token]);
CREATE INDEX IX_RefreshToken_ITCode ON [FrameworkRefreshTokens]([ITCode]);

-- ============================================================
-- MySQL / MariaDB
-- ============================================================
-- P0-1: Widen Password column for PBKDF2 hashes
-- Idempotent: MySQL has no native conditional DDL, so build the ALTER as a
-- string and only PREPARE/EXECUTE it when the live column is still narrower
-- than 256; otherwise run a harmless no-op SELECT instead.
SET @wtm_1082_ddl = (
    SELECT IF(
        (SELECT character_maximum_length
           FROM information_schema.columns
          WHERE table_schema = DATABASE()
            AND table_name = 'FrameworkUsers'
            AND column_name = 'Password') < 256,
        'ALTER TABLE `FrameworkUsers` MODIFY COLUMN `Password` VARCHAR(256) NOT NULL;',
        'SELECT 1;'
    )
);
PREPARE wtm_1082_stmt FROM @wtm_1082_ddl;
EXECUTE wtm_1082_stmt;
DEALLOCATE PREPARE wtm_1082_stmt;

-- P0-2: Create refresh token table
CREATE TABLE IF NOT EXISTS `FrameworkRefreshTokens` (
    `ID`              CHAR(36)     NOT NULL PRIMARY KEY,
    `Token`           VARCHAR(256) NOT NULL,
    `ITCode`          VARCHAR(50)  NOT NULL,
    `TenantCode`      VARCHAR(50)  NULL,
    `ExpiresUtc`      DATETIME(6)  NOT NULL,
    `CreatedUtc`      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    `CreatedByIp`     VARCHAR(50)  NULL,
    `RevokedUtc`      DATETIME(6)  NULL,
    `RevokedByIp`     VARCHAR(50)  NULL,
    `ReplacedByToken` VARCHAR(256) NULL,
    `RevokeReason`    VARCHAR(100) NULL
);
CREATE INDEX IX_RefreshToken_Token  ON `FrameworkRefreshTokens`(`Token`);
CREATE INDEX IX_RefreshToken_ITCode ON `FrameworkRefreshTokens`(`ITCode`);

-- ============================================================
-- PostgreSQL
-- ============================================================
-- P0-1: Widen Password column for PBKDF2 hashes
-- Idempotent: DO block only issues the ALTER when the live column is still
-- narrower than 256. If the column isn't found, the subquery returns NULL,
-- the comparison is NULL (falsy), and the block does nothing rather than error.
DO $$
BEGIN
    IF (SELECT character_maximum_length
          FROM information_schema.columns
         WHERE table_name = 'FrameworkUsers'
           AND column_name = 'Password') < 256 THEN
        ALTER TABLE "FrameworkUsers" ALTER COLUMN "Password" TYPE VARCHAR(256);
    END IF;
END $$;

-- P0-2: Create refresh token table
CREATE TABLE IF NOT EXISTS "FrameworkRefreshTokens" (
    "ID"              UUID         NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "Token"           VARCHAR(256) NOT NULL,
    "ITCode"          VARCHAR(50)  NOT NULL,
    "TenantCode"      VARCHAR(50)  NULL,
    "ExpiresUtc"      TIMESTAMPTZ  NOT NULL,
    "CreatedUtc"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "CreatedByIp"     VARCHAR(50)  NULL,
    "RevokedUtc"      TIMESTAMPTZ  NULL,
    "RevokedByIp"     VARCHAR(50)  NULL,
    "ReplacedByToken" VARCHAR(256) NULL,
    "RevokeReason"    VARCHAR(100) NULL
);
CREATE INDEX IF NOT EXISTS IX_RefreshToken_Token  ON "FrameworkRefreshTokens"("Token");
CREATE INDEX IF NOT EXISTS IX_RefreshToken_ITCode ON "FrameworkRefreshTokens"("ITCode");

-- ============================================================
-- SQLite (dev/test only)
-- ============================================================
-- P0-1: SQLite has no ALTER COLUMN; VARCHAR(256) is TEXT in SQLite anyway — no action needed.
-- P0-2: Create refresh token table
CREATE TABLE IF NOT EXISTS "FrameworkRefreshTokens" (
    "ID"              TEXT NOT NULL PRIMARY KEY,
    "Token"           TEXT NOT NULL,
    "ITCode"          TEXT NOT NULL,
    "TenantCode"      TEXT NULL,
    "ExpiresUtc"      TEXT NOT NULL,
    "CreatedUtc"      TEXT NOT NULL DEFAULT (datetime('now')),
    "CreatedByIp"     TEXT NULL,
    "RevokedUtc"      TEXT NULL,
    "RevokedByIp"     TEXT NULL,
    "ReplacedByToken" TEXT NULL,
    "RevokeReason"    TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_RefreshToken_Token  ON "FrameworkRefreshTokens"("Token");
CREATE INDEX IF NOT EXISTS IX_RefreshToken_ITCode ON "FrameworkRefreshTokens"("ITCode");

-- ============================================================
-- Oracle
-- ============================================================
-- P0-1: Widen Password column for PBKDF2 hashes
-- NOT guarded with a conditional check (unlike the other providers above).
-- A query-based guard here would need to look up USER_TAB_COLUMNS by exact
-- identifier case, because the DDL in this file quotes "FrameworkUsers" and
-- "Password" as mixed-case identifiers -- and a case mismatch between that
-- lookup and however the live table was actually created is exactly the
-- failure mode issue #1082 is about (wrong name -> zero rows -> misread as
-- "nothing to do"). Rather than add a second lookup with the same risk, this
-- is left unguarded and documented instead:
-- Re-running MODIFY with a length/NOT NULL that already matches the current
-- column definition is expected to succeed as a no-op per Oracle's ALTER
-- TABLE MODIFY semantics (widening, or reapplying an existing inline NOT
-- NULL, does not raise an error) -- but this has NOT been executed against a
-- live Oracle instance in this environment, so treat it as documented
-- expectation, not a verified guarantee. Confirm on your own Oracle version
-- before relying on it, and use the pre-flight check at the top of this file
-- to see the column's current width first regardless.
ALTER TABLE "FrameworkUsers" MODIFY "Password" NVARCHAR2(256) NOT NULL;

-- P0-2: Create refresh token table
CREATE TABLE "FrameworkRefreshTokens" (
    "ID"              RAW(16)        DEFAULT SYS_GUID() NOT NULL PRIMARY KEY,
    "Token"           NVARCHAR2(256) NOT NULL,
    "ITCode"          NVARCHAR2(50)  NOT NULL,
    "TenantCode"      NVARCHAR2(50)  NULL,
    "ExpiresUtc"      TIMESTAMP      NOT NULL,
    "CreatedUtc"      TIMESTAMP      DEFAULT SYSTIMESTAMP NOT NULL,
    "CreatedByIp"     NVARCHAR2(50)  NULL,
    "RevokedUtc"      TIMESTAMP      NULL,
    "RevokedByIp"     NVARCHAR2(50)  NULL,
    "ReplacedByToken" NVARCHAR2(256) NULL,
    "RevokeReason"    NVARCHAR2(100) NULL
);
CREATE INDEX IX_RefreshToken_Token  ON "FrameworkRefreshTokens"("Token");
CREATE INDEX IX_RefreshToken_ITCode ON "FrameworkRefreshTokens"("ITCode");
