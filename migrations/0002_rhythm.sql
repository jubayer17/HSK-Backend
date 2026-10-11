-- 0002 Rhythm: the Director's cleaning rhythm per hotel (plan 9.6, rules 9 and 10).

CREATE TABLE dbo.rhythm_configuration (
    config_id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_rhythm_configuration_id DEFAULT NEWSEQUENTIALID(),
    hotel_id       TINYINT       NOT NULL CONSTRAINT FK_rhythm_configuration_hotel REFERENCES dbo.HotelMaster (hotel_id),
    rhythm_type    VARCHAR(10)   NOT NULL CONSTRAINT CK_rhythm_configuration_type CHECK (rhythm_type IN ('TwoTages', 'ThreeTages')),
    effective_from DATE          NOT NULL CONSTRAINT CK_rhythm_configuration_day1 CHECK (DAY(effective_from) = 1),
    status         VARCHAR(12)   NOT NULL CONSTRAINT CK_rhythm_configuration_status CHECK (status IN ('ACTIVE', 'SCHEDULED', 'SUPERSEDED', 'CANCELLED')),
    reason         NVARCHAR(500) NULL,
    created_by     UNIQUEIDENTIFIER NOT NULL,
    created_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF_rhythm_configuration_created DEFAULT SYSUTCDATETIME(),
    activated_at_utc DATETIME2(3) NULL,
    valid_from     DATETIME2(3) GENERATED ALWAYS AS ROW START NOT NULL,
    valid_to       DATETIME2(3) GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to),
    CONSTRAINT PK_rhythm_configuration PRIMARY KEY NONCLUSTERED (config_id)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.rhythm_configuration_history));
CREATE CLUSTERED INDEX CIX_rhythm_configuration ON dbo.rhythm_configuration (hotel_id, effective_from);
-- Only one active rhythm and only one planned change per hotel (handoff).
CREATE UNIQUE INDEX UX_rhythm_configuration_active ON dbo.rhythm_configuration (hotel_id) WHERE status = 'ACTIVE';
CREATE UNIQUE INDEX UX_rhythm_configuration_scheduled ON dbo.rhythm_configuration (hotel_id) WHERE status = 'SCHEDULED';
GO

CREATE TABLE dbo.rhythm_change_cancellation (
    cancellation_id  UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_rhythm_change_cancellation_id DEFAULT NEWSEQUENTIALID(),
    config_id        UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_rhythm_change_cancellation_config REFERENCES dbo.rhythm_configuration (config_id),
    reason           NVARCHAR(500)    NULL,
    cancelled_by     UNIQUEIDENTIFIER NOT NULL,
    cancelled_at_utc DATETIME2(3)     NOT NULL CONSTRAINT DF_rhythm_change_cancellation_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_rhythm_change_cancellation PRIMARY KEY NONCLUSTERED (cancellation_id)
);
CREATE UNIQUE CLUSTERED INDEX CIX_rhythm_change_cancellation ON dbo.rhythm_change_cancellation (config_id);
GO

-- Rule 9: a guest keeps the rhythm that was active on the arrival day. Never changed afterwards;
-- only the departure date moves when a stay is extended. Two stays in a row are two rows.
CREATE TABLE dbo.guest_rhythm_assignment (
    assignment_id  UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_guest_rhythm_assignment_id DEFAULT NEWSEQUENTIALID(),
    hotel_id       TINYINT     NOT NULL,
    room_no        VARCHAR(4)  NOT NULL,
    arrival_date   DATE        NOT NULL,
    departure_date DATE        NULL,
    rhythm_type    VARCHAR(10) NOT NULL CONSTRAINT CK_guest_rhythm_assignment_type CHECK (rhythm_type IN ('TwoTages', 'ThreeTages')),
    config_id      UNIQUEIDENTIFIER NULL,
    created_at_utc DATETIME2(3) NOT NULL CONSTRAINT DF_guest_rhythm_assignment_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_guest_rhythm_assignment PRIMARY KEY NONCLUSTERED (assignment_id)
);
CREATE UNIQUE CLUSTERED INDEX CIX_guest_rhythm_assignment ON dbo.guest_rhythm_assignment (hotel_id, room_no, arrival_date);
GO
