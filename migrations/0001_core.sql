-- 0001 Core: hotels, rooms, accounts, audit.
-- Table rules (plan section 8): GUID primary key as non-clustered key + clustered index on the natural key.
-- No staff or guest names in any table (rules 3 and 4). app_user holds the sign-in accounts only.

CREATE TABLE dbo.HotelMaster (
    hotel_id      TINYINT       NOT NULL CONSTRAINT PK_HotelMaster PRIMARY KEY,
    printed_label NVARCHAR(100) NOT NULL,
    short_label   NVARCHAR(40)  NOT NULL,
    brand_colour  CHAR(7)       NOT NULL
);
INSERT INTO dbo.HotelMaster (hotel_id, printed_label, short_label, brand_colour) VALUES
    (1, N'ibis budget Münster City', N'ibis budget', '#2471A3'),
    (2, N'Novotel Münster City', N'Novotel', '#C0392B');
GO

CREATE TABLE dbo.room_inventory (
    room_id       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_room_inventory_id DEFAULT NEWSEQUENTIALID(),
    hotel_id      TINYINT     NOT NULL CONSTRAINT FK_room_inventory_hotel REFERENCES dbo.HotelMaster (hotel_id),
    room_no       VARCHAR(4)  NOT NULL,
    floor_no      TINYINT     NOT NULL,
    display_order SMALLINT    NOT NULL,
    is_active     BIT         NOT NULL CONSTRAINT DF_room_inventory_active DEFAULT 1,
    CONSTRAINT PK_room_inventory PRIMARY KEY NONCLUSTERED (room_id)
);
CREATE UNIQUE CLUSTERED INDEX CIX_room_inventory ON dbo.room_inventory (hotel_id, room_no);
GO

-- 117 Novotel rooms, 123 ibis budget rooms (Room Grid blueprint).
WITH ranges AS (
    SELECT * FROM (VALUES
        (2, 101, 126), (2, 201, 226), (2, 301, 326), (2, 401, 426), (2, 501, 513),
        (1, 101, 128), (1, 201, 228), (1, 301, 328), (1, 401, 428), (1, 501, 511)
    ) AS r (hotel_id, from_no, to_no)
),
numbers AS (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n FROM sys.all_objects)
INSERT INTO dbo.room_inventory (hotel_id, room_no, floor_no, display_order)
SELECT r.hotel_id, CAST(r.from_no + n.n AS VARCHAR(4)), (r.from_no + n.n) / 100,
       ROW_NUMBER() OVER (PARTITION BY r.hotel_id ORDER BY r.from_no + n.n)
FROM ranges r JOIN numbers n ON r.from_no + n.n <= r.to_no;
GO

CREATE TABLE dbo.app_user (
    user_id       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_app_user_id DEFAULT NEWSEQUENTIALID(),
    username      VARCHAR(64)   NOT NULL,
    display_label NVARCHAR(100) NOT NULL,
    role          VARCHAR(16)   NOT NULL CONSTRAINT CK_app_user_role CHECK (role IN ('Director', 'Operations')),
    password_hash VARCHAR(200)  NOT NULL,
    is_active     BIT           NOT NULL CONSTRAINT DF_app_user_active DEFAULT 1,
    created_at_utc DATETIME2(3) NOT NULL CONSTRAINT DF_app_user_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_app_user PRIMARY KEY NONCLUSTERED (user_id)
);
CREATE UNIQUE CLUSTERED INDEX CIX_app_user ON dbo.app_user (username);
GO

-- Sign-in sessions. Only a SHA-256 hash of the cookie token is stored.
CREATE TABLE dbo.app_session (
    session_id     UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_app_session_id DEFAULT NEWSEQUENTIALID(),
    token_hash     BINARY(32)       NOT NULL,
    user_id        UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_app_session_user REFERENCES dbo.app_user (user_id),
    created_at_utc DATETIME2(3)     NOT NULL CONSTRAINT DF_app_session_created DEFAULT SYSUTCDATETIME(),
    expires_at_utc DATETIME2(3)     NOT NULL,
    revoked_at_utc DATETIME2(3)     NULL,
    CONSTRAINT PK_app_session PRIMARY KEY NONCLUSTERED (session_id)
);
CREATE UNIQUE CLUSTERED INDEX CIX_app_session ON dbo.app_session (token_hash);
GO

-- Rule 5: every confirmed entry is logged with the values the user confirmed.
CREATE TABLE dbo.UserConfirmationLog (
    confirmation_id  UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_UserConfirmationLog_id DEFAULT NEWSEQUENTIALID(),
    session_id       UNIQUEIDENTIFIER NULL,
    user_id          UNIQUEIDENTIFIER NOT NULL,
    action           VARCHAR(40)      NOT NULL,
    entity_type      VARCHAR(40)      NOT NULL,
    entity_id        UNIQUEIDENTIFIER NULL,
    confirmed_values NVARCHAR(MAX)    NOT NULL CONSTRAINT CK_UserConfirmationLog_json CHECK (ISJSON(confirmed_values) = 1),
    confirmations    TINYINT          NOT NULL CONSTRAINT CK_UserConfirmationLog_twice CHECK (confirmations >= 2),
    confirmed_at_utc DATETIME2(3)     NOT NULL CONSTRAINT DF_UserConfirmationLog_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_UserConfirmationLog PRIMARY KEY NONCLUSTERED (confirmation_id)
);
CREATE CLUSTERED INDEX CIX_UserConfirmationLog ON dbo.UserConfirmationLog (confirmed_at_utc, user_id);
GO

CREATE TABLE dbo.DirectorAuditLog (
    audit_id     UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_DirectorAuditLog_id DEFAULT NEWSEQUENTIALID(),
    user_id      UNIQUEIDENTIFIER NOT NULL,
    action       VARCHAR(40)      NOT NULL,
    hotel_id     TINYINT          NULL,
    details      NVARCHAR(MAX)    NOT NULL CONSTRAINT CK_DirectorAuditLog_json CHECK (ISJSON(details) = 1),
    reason       NVARCHAR(500)    NULL,
    at_utc       DATETIME2(3)     NOT NULL CONSTRAINT DF_DirectorAuditLog_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_DirectorAuditLog PRIMARY KEY NONCLUSTERED (audit_id)
);
CREATE CLUSTERED INDEX CIX_DirectorAuditLog ON dbo.DirectorAuditLog (at_utc, hotel_id);
GO

-- From the client's OpenDataLoader wrapper document. No PII: metadata only. Keep 90 days.
CREATE TABLE dbo.PdfProcessingLog (
    LogId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PdfProcessingLog PRIMARY KEY NONCLUSTERED,
    CorrelationId  CHAR(32)        NOT NULL,
    SourceBlobName NVARCHAR(200)   NOT NULL,
    Outcome        VARCHAR(20)     NOT NULL,
    DurationMs     INT             NOT NULL,
    ByteCount      INT             NOT NULL,
    ErrorDetail    NVARCHAR(2000)  NULL,
    OccurredAtUtc  DATETIME2(3)    NOT NULL CONSTRAINT DF_PdfProcessingLog_at DEFAULT SYSUTCDATETIME()
);
CREATE CLUSTERED INDEX CIX_PdfProcessingLog ON dbo.PdfProcessingLog (OccurredAtUtc DESC, CorrelationId);
GO
