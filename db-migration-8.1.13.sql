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
