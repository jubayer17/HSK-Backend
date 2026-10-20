-- 0004: 1-Night logic, room moves/checks, staff, daily close, emails, prices.

-- 1-Night logic: third choice after 2-Tages and 3-Tages.
ALTER TABLE dbo.rhythm_configuration DROP CONSTRAINT CK_rhythm_configuration_type;
ALTER TABLE dbo.rhythm_configuration ADD CONSTRAINT CK_rhythm_configuration_type CHECK (rhythm_type IN ('TwoTages', 'ThreeTages', 'OneNight'));
ALTER TABLE dbo.guest_rhythm_assignment DROP CONSTRAINT CK_guest_rhythm_assignment_type;
ALTER TABLE dbo.guest_rhythm_assignment ADD CONSTRAINT CK_guest_rhythm_assignment_type CHECK (rhythm_type IN ('TwoTages', 'ThreeTages', 'OneNight'));
GO

-- All three logics per room; "printed" marks the one on the sheet (old rows = printed).
ALTER TABLE dbo.room_cleaning_status ADD
    printed         BIT         NOT NULL CONSTRAINT DF_room_cleaning_status_printed DEFAULT 1,
    cleaned_by_type VARCHAR(16) NULL CONSTRAINT CK_room_cleaning_status_by CHECK (cleaned_by_type IS NULL OR cleaned_by_type IN ('External', 'HotelEmployee'));
GO
CREATE INDEX IX_room_cleaning_status_day ON dbo.room_cleaning_status (hotel_id, report_date, logic_version)
    INCLUDE (room_no, final_colour, service_code, cleaned, printed, cleaned_by_type);
GO

-- 1-Night cleaner availability per hotel and day. No row = no cleaner.
CREATE TABLE dbo.cleaner_availability (
    hotel_id       TINYINT      NOT NULL CONSTRAINT FK_cleaner_availability_hotel REFERENCES dbo.HotelMaster (hotel_id),
    report_date    DATE         NOT NULL,
    available      BIT          NOT NULL,
    run_id         UNIQUEIDENTIFIER NULL,
    created_by     UNIQUEIDENTIFIER NOT NULL,
    created_at_utc DATETIME2(3) NOT NULL CONSTRAINT DF_cleaner_availability_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_cleaner_availability PRIMARY KEY CLUSTERED (hotel_id, report_date)
);
GO

-- room_no = room left (full clean); no cleaner today => clean_date = move_date + 1.
CREATE TABLE dbo.room_move (
    move_id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_room_move_id DEFAULT NEWSEQUENTIALID(),
    hotel_id          TINYINT       NOT NULL CONSTRAINT FK_room_move_hotel REFERENCES dbo.HotelMaster (hotel_id),
    move_date         DATE          NOT NULL,
    clean_date        DATE          NOT NULL,
    room_no           VARCHAR(4)    NOT NULL,
    to_room_no        VARCHAR(4)    NULL,
    cleaner_available BIT           NOT NULL,
    cleaned_by_type   VARCHAR(16)   NOT NULL CONSTRAINT CK_room_move_by CHECK (cleaned_by_type IN ('External', 'HotelEmployee')),
    -- Hotel employees only; cleared after 30 days.
    employee_name     NVARCHAR(100) NULL,
    note              NVARCHAR(200) NULL,
    run_id            UNIQUEIDENTIFIER NULL,
    created_by        UNIQUEIDENTIFIER NOT NULL,
    created_at_utc    DATETIME2(3)  NOT NULL CONSTRAINT DF_room_move_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_room_move PRIMARY KEY NONCLUSTERED (move_id),
    CONSTRAINT CK_room_move_name CHECK (cleaned_by_type = 'HotelEmployee' OR employee_name IS NULL),
    CONSTRAINT CK_room_move_dates CHECK (clean_date >= move_date AND clean_date <= DATEADD(DAY, 1, move_date))
);
CREATE CLUSTERED INDEX CIX_room_move ON dbo.room_move (hotel_id, clean_date, room_no);
CREATE INDEX IX_room_move_date ON dbo.room_move (move_date, hotel_id);
GO

-- Staff list. hotel_id NULL = works in both hotels.
CREATE TABLE dbo.staff_member (
    staff_id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_staff_member_id DEFAULT NEWSEQUENTIALID(),
    hotel_id            TINYINT       NULL CONSTRAINT FK_staff_member_hotel REFERENCES dbo.HotelMaster (hotel_id),
    display_name        NVARCHAR(100) NOT NULL,
    company             VARCHAR(16)   NOT NULL CONSTRAINT CK_staff_member_company CHECK (company IN ('External', 'Hotel')),
    role                VARCHAR(20)   NOT NULL CONSTRAINT CK_staff_member_role CHECK (role IN (
                            'Cleaner', 'Hausdame', 'HausdameAssistenz', 'FloorSupervisor', 'Hausmann',
                            'RphFloorSupervisor', 'RphHausmann', 'ProjectManager', 'AssistantPM')),
    monthly_check_limit INT           NULL CONSTRAINT CK_staff_member_limit CHECK (monthly_check_limit IS NULL OR monthly_check_limit >= 0),
    is_active           BIT           NOT NULL CONSTRAINT DF_staff_member_active DEFAULT 1,
    created_by          UNIQUEIDENTIFIER NOT NULL,
    created_at_utc      DATETIME2(3)  NOT NULL CONSTRAINT DF_staff_member_created DEFAULT SYSUTCDATETIME(),
    updated_at_utc      DATETIME2(3)  NOT NULL CONSTRAINT DF_staff_member_updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_staff_member PRIMARY KEY NONCLUSTERED (staff_id)
);
CREATE CLUSTERED INDEX CIX_staff_member ON dbo.staff_member (role, display_name);
GO

-- Room checks. room_no NULL when only a count was entered.
CREATE TABLE dbo.room_check_log (
    check_id        BIGINT IDENTITY(1,1) NOT NULL,
    hotel_id        TINYINT       NOT NULL CONSTRAINT FK_room_check_log_hotel REFERENCES dbo.HotelMaster (hotel_id),
    check_date      DATE          NOT NULL,
    room_no         VARCHAR(4)    NULL,
    checker_role    VARCHAR(20)   NOT NULL CONSTRAINT CK_room_check_log_role CHECK (checker_role IN ('ProjectManager', 'AssistantPM', 'CleanerSelbstcheck')),
    staff_id        UNIQUEIDENTIFIER NULL CONSTRAINT FK_room_check_log_staff REFERENCES dbo.staff_member (staff_id),
    checked_by_name NVARCHAR(100) NOT NULL,
    created_by      UNIQUEIDENTIFIER NOT NULL,
    created_at_utc  DATETIME2(3)  NOT NULL CONSTRAINT DF_room_check_log_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_room_check_log PRIMARY KEY NONCLUSTERED (check_id)
);
CREATE CLUSTERED INDEX CIX_room_check_log ON dbo.room_check_log (check_date, hotel_id, checker_role);
GO

-- Daily close: entered once a day; feeds the report PDFs and the dashboard.
CREATE TABLE dbo.daily_close (
    close_id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_daily_close_id DEFAULT NEWSEQUENTIALID(),
    report_date           DATE          NOT NULL,
    status                VARCHAR(10)   NOT NULL CONSTRAINT CK_daily_close_status CHECK (status IN ('draft', 'confirmed')),
    comment               NVARCHAR(300) NULL,
    extra_reinigung_hours DECIMAL(5,2)  NOT NULL CONSTRAINT DF_daily_close_extra DEFAULT 0,
    updated_by            UNIQUEIDENTIFIER NOT NULL,
    updated_at_utc        DATETIME2(3)  NOT NULL CONSTRAINT DF_daily_close_updated DEFAULT SYSUTCDATETIME(),
    confirmed_by          UNIQUEIDENTIFIER NULL,
    confirmed_at_utc      DATETIME2(3)  NULL,
    CONSTRAINT PK_daily_close PRIMARY KEY NONCLUSTERED (close_id),
    CONSTRAINT CK_daily_close_confirmed CHECK (status = 'draft' OR (confirmed_by IS NOT NULL AND confirmed_at_utc IS NOT NULL))
);
CREATE UNIQUE CLUSTERED INDEX CIX_daily_close ON dbo.daily_close (report_date);
GO

CREATE TABLE dbo.daily_close_hotel (
    close_id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_daily_close_hotel_close REFERENCES dbo.daily_close (close_id),
    hotel_id           TINYINT       NOT NULL CONSTRAINT FK_daily_close_hotel_hotel REFERENCES dbo.HotelMaster (hotel_id),
    richter_staff_name NVARCHAR(100) NULL,
    flat_rate_day      BIT           NOT NULL,
    pauschale_checks   INT           NOT NULL,
    sauber             INT           NOT NULL CONSTRAINT DF_daily_close_hotel_sauber DEFAULT 0,
    abzuege            INT           NOT NULL CONSTRAINT DF_daily_close_hotel_abzuege DEFAULT 0,
    informationen      NVARCHAR(2000) NULL,
    -- Snapshots at confirmation, so a confirmed day never changes.
    total_rooms_sheet  INT           NULL,
    checks_pm          INT           NULL,
    checks_apm         INT           NULL,
    checks_selbst      INT           NULL,
    CONSTRAINT PK_daily_close_hotel PRIMARY KEY CLUSTERED (close_id, hotel_id),
    CONSTRAINT CK_daily_close_hotel_numbers CHECK (pauschale_checks >= 0 AND sauber >= 0 AND abzuege >= 0)
);
GO

CREATE TABLE dbo.daily_close_cleaner (
    close_id     UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_daily_close_cleaner_close REFERENCES dbo.daily_close (close_id),
    hotel_id     TINYINT       NOT NULL,
    line_no      SMALLINT      NOT NULL,
    staff_id     UNIQUEIDENTIFIER NULL,
    cleaner_name NVARCHAR(100) NOT NULL,
    gereinigt    INT           NOT NULL,
    ns           INT           NOT NULL,
    rk           INT           NOT NULL,
    ks           INT           NOT NULL,
    start_time   TIME(0)       NULL,
    end_time     TIME(0)       NULL,
    pause_hours  DECIMAL(4,2)  NOT NULL CONSTRAINT DF_daily_close_cleaner_pause DEFAULT 0,
    CONSTRAINT PK_daily_close_cleaner PRIMARY KEY CLUSTERED (close_id, hotel_id, line_no),
    CONSTRAINT CK_daily_close_cleaner_numbers CHECK (gereinigt >= 0 AND ns >= 0 AND rk >= 0 AND ks >= 0 AND pause_hours >= 0)
);
GO

-- HD, HDA, FS, RPH FS, HM, RPH HM: shared by both hotels.
CREATE TABLE dbo.daily_close_supervisor (
    close_id    UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_daily_close_supervisor_close REFERENCES dbo.daily_close (close_id),
    role        VARCHAR(8)    NOT NULL CONSTRAINT CK_daily_close_supervisor_role CHECK (role IN ('HD', 'HDA', 'FS', 'RPH_FS', 'HM', 'RPH_HM')),
    staff_id    UNIQUEIDENTIFIER NULL,
    person_name NVARCHAR(100) NULL,
    start_time  TIME(0)       NULL,
    end_time    TIME(0)       NULL,
    pause_hours DECIMAL(4,2)  NOT NULL CONSTRAINT DF_daily_close_supervisor_pause DEFAULT 0,
    CONSTRAINT PK_daily_close_supervisor PRIMARY KEY CLUSTERED (close_id, role)
);
GO

-- Room lists for the Übergabe Etage – Empfang form.
CREATE TABLE dbo.daily_close_room (
    close_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_daily_close_room_close REFERENCES dbo.daily_close (close_id),
    hotel_id TINYINT     NOT NULL,
    category VARCHAR(16) NOT NULL CONSTRAINT CK_daily_close_room_category CHECK (category IN ('NS', 'RK', 'KS', 'LATE_CHECKOUT', 'LIEGEZIMMER')),
    room_no  VARCHAR(4)  NOT NULL,
    CONSTRAINT PK_daily_close_room PRIMARY KEY CLUSTERED (close_id, hotel_id, room_no)
);
GO

-- Prices, Kostenstelle, flat-rate checks. Text values; empty = not set.
CREATE TABLE dbo.app_setting (
    setting_key    VARCHAR(60)   NOT NULL CONSTRAINT PK_app_setting PRIMARY KEY,
    setting_value  NVARCHAR(400) NULL,
    updated_by     UNIQUEIDENTIFIER NULL,
    updated_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF_app_setting_updated DEFAULT SYSUTCDATETIME()
);
INSERT INTO dbo.app_setting (setting_key, setting_value) VALUES
    ('objekt', N'Novotel / Ibis Budget Münster'),
    ('kostenstelle', N'4900'),
    ('pauschale_checks', N'30'),
    ('pauschale_label', N'5T/W'),
    ('email_enabled', N'true'),
    ('price_room_novotel', NULL),
    ('price_room_ibis', NULL),
    ('price_check', NULL),
    ('rate_hausdame', NULL),
    ('rate_hausdame_assistenz', NULL),
    ('rate_floor_supervisor', NULL),
    ('rate_hausmann', NULL),
    ('rate_extra_reinigung', NULL),
    ('cost_internal_room', NULL);
GO

CREATE TABLE dbo.report_recipient (
    recipient_id   UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_report_recipient_id DEFAULT NEWSEQUENTIALID(),
    report_kind    VARCHAR(20)   NOT NULL CONSTRAINT CK_report_recipient_kind CHECK (report_kind IN ('DAILY_TASK_SHEET', 'DAILY_HANDOVER', 'MONTHLY')),
    email          NVARCHAR(254) NOT NULL,
    created_by     UNIQUEIDENTIFIER NOT NULL,
    created_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF_report_recipient_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_report_recipient PRIMARY KEY NONCLUSTERED (recipient_id)
);
CREATE UNIQUE CLUSTERED INDEX CIX_report_recipient ON dbo.report_recipient (report_kind, email);
GO

CREATE TABLE dbo.report_email_log (
    email_id     UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_report_email_log_id DEFAULT NEWSEQUENTIALID(),
    report_kind  VARCHAR(20)    NOT NULL,
    period       DATE           NOT NULL,
    recipients   NVARCHAR(2000) NOT NULL,
    status       VARCHAR(10)    NOT NULL CONSTRAINT CK_report_email_log_status CHECK (status IN ('sent', 'outbox', 'failed', 'skipped')),
    detail       NVARCHAR(500)  NULL,
    triggered_by UNIQUEIDENTIFIER NULL,
    at_utc       DATETIME2(3)   NOT NULL CONSTRAINT DF_report_email_log_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_report_email_log PRIMARY KEY NONCLUSTERED (email_id)
);
CREATE CLUSTERED INDEX CIX_report_email_log ON dbo.report_email_log (at_utc DESC);
GO
