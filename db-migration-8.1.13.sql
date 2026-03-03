-- WTM 8.1.13 Database Migration (run BEFORE deploying)

-- P0-1: Widen Password column for PBKDF2 hashes
ALTER TABLE [FrameworkUser] ALTER COLUMN [Password] NVARCHAR(256) NOT NULL;

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
ALTER TABLE `FrameworkUser` MODIFY COLUMN `Password` VARCHAR(256) NOT NULL;

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
ALTER TABLE "FrameworkUser" ALTER COLUMN "Password" TYPE VARCHAR(256);

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
ALTER TABLE "FrameworkUser" MODIFY "Password" NVARCHAR2(256) NOT NULL;

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
