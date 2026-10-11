-- 0003 Phase 1 morning run: runs, files, parsed task sheets, colours.
-- No column for a person's name (rule 3). sheet_no is text ("FLOAT").

CREATE TABLE dbo.phase1_run (
    run_id           UNIQUEIDENTIFIER NOT NULL,
    report_date      DATE         NULL,
    status           VARCHAR(12)  NOT NULL CONSTRAINT CK_phase1_run_status CHECK (status IN ('processing', 'completed', 'failed')),
    created_by       UNIQUEIDENTIFIER NOT NULL,
    session_id       UNIQUEIDENTIFIER NULL,
    created_at_utc   DATETIME2(3) NOT NULL CONSTRAINT DF_phase1_run_created DEFAULT SYSUTCDATETIME(),
    completed_at_utc DATETIME2(3) NULL,
    instance_id      VARCHAR(100) NULL,
    CONSTRAINT PK_phase1_run PRIMARY KEY NONCLUSTERED (run_id)
);
CREATE CLUSTERED INDEX CIX_phase1_run ON dbo.phase1_run (created_at_utc DESC);
GO

CREATE TABLE dbo.phase1_run_file (
    file_id            UNIQUEIDENTIFIER NOT NULL,
    run_id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_phase1_run_file_run REFERENCES dbo.phase1_run (run_id),
    upload_order       TINYINT       NOT NULL,
    file_label         NVARCHAR(260) NOT NULL,
    byte_count         INT           NOT NULL,
    sha256             BINARY(32)    NOT NULL,
    blob_name          VARCHAR(200)  NOT NULL,
    hotel_id           TINYINT       NULL CONSTRAINT FK_phase1_run_file_hotel REFERENCES dbo.HotelMaster (hotel_id),
    report_date        DATE          NULL,
    published_at_local DATETIME2(0)  NULL,
    status             VARCHAR(12)   NOT NULL CONSTRAINT CK_phase1_run_file_status CHECK (status IN ('processing', 'checked', 'completed', 'rejected')),
    reject_code        VARCHAR(40)   NULL,
    reject_reason      NVARCHAR(500) NULL,
    parsed_rooms       INT           NULL,
    line_endings_repaired BIT        NOT NULL CONSTRAINT DF_phase1_run_file_repaired DEFAULT 0,
    marked_blob_name   VARCHAR(200)  NULL,
    correlation_id     CHAR(32)      NULL,
    CONSTRAINT PK_phase1_run_file PRIMARY KEY NONCLUSTERED (file_id),
    CONSTRAINT UQ_phase1_run_file_order UNIQUE (run_id, upload_order)
);
CREATE CLUSTERED INDEX CIX_phase1_run_file ON dbo.phase1_run_file (run_id, upload_order);
-- Check 4: the same sheet (hotel + published date-time) is used once.
CREATE UNIQUE INDEX UX_phase1_run_file_sheet ON dbo.phase1_run_file (hotel_id, published_at_local)
    WHERE status IN ('checked', 'completed');
GO

CREATE TABLE dbo.phase1_run_step (
    file_id        UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_phase1_run_step_file REFERENCES dbo.phase1_run_file (file_id),
    step_key       VARCHAR(10)   NOT NULL CONSTRAINT CK_phase1_run_step_key CHECK (step_key IN ('rhythm', 'read', 'check', 'save', 'colour', 'mark')),
    status         VARCHAR(10)   NOT NULL CONSTRAINT CK_phase1_run_step_status CHECK (status IN ('pending', 'running', 'done', 'warning', 'failed')),
    message        NVARCHAR(500) NULL,
    updated_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF_phase1_run_step_updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_phase1_run_step PRIMARY KEY CLUSTERED (file_id, step_key)
);
GO

CREATE TABLE dbo.run_warning (
    warning_id     UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_run_warning_id DEFAULT NEWSEQUENTIALID(),
    file_id        UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_run_warning_file REFERENCES dbo.phase1_run_file (file_id),
    code           VARCHAR(40)   NOT NULL,
    message        NVARCHAR(500) NOT NULL,
    created_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF_run_warning_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_run_warning PRIMARY KEY NONCLUSTERED (warning_id)
);
CREATE CLUSTERED INDEX CIX_run_warning ON dbo.run_warning (file_id, created_at_utc);
GO

CREATE TABLE dbo.task_sheets (
    task_sheet_id   UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_task_sheets_id DEFAULT NEWSEQUENTIALID(),
    file_id         UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_task_sheets_file REFERENCES dbo.phase1_run_file (file_id),
    hotel_id        TINYINT       NOT NULL,
    sheet_no        VARCHAR(10)   NOT NULL,
    sheet_date      DATE          NULL,
    task_codes      VARCHAR(60)   NOT NULL,
    auto_generated  BIT           NULL,
    instructions    NVARCHAR(1000) NOT NULL,
    instructions_source VARCHAR(10) NOT NULL CONSTRAINT DF_task_sheets_source DEFAULT 'pdf',
    total_rooms     INT           NULL,
    departure_rooms INT           NULL,
    total_adults    INT           NULL,
    total_children  INT           NULL,
    total_credits   DECIMAL(10,2) NULL,
    clean_rooms     INT           NULL,
    dirty_rooms     INT           NULL,
    first_page      SMALLINT      NOT NULL,
    last_page       SMALLINT      NOT NULL,
    CONSTRAINT PK_task_sheets PRIMARY KEY NONCLUSTERED (task_sheet_id)
);
CREATE UNIQUE CLUSTERED INDEX CIX_task_sheets ON dbo.task_sheets (file_id, sheet_no);
GO

CREATE TABLE dbo.room_rows (
    row_id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_room_rows_id DEFAULT NEWSEQUENTIALID(),
    file_id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_room_rows_file REFERENCES dbo.phase1_run_file (file_id),
    hotel_id           TINYINT       NOT NULL,
    report_date        DATE          NOT NULL,
    sheet_no           VARCHAR(10)   NOT NULL,
    room_no            VARCHAR(4)    NOT NULL,
    room_status        VARCHAR(4)    NOT NULL,
    credits            DECIMAL(5,2)  NULL,
    reservation_status NVARCHAR(30)  NOT NULL,
    arrival_date       DATE          NULL,
    departure_date     DATE          NULL,
    adults             TINYINT       NULL,
    children           TINYINT       NULL,
    tasks              NVARCHAR(200) NOT NULL,
    row_marker         CHAR(1)       NULL,
    page_no            SMALLINT      NOT NULL,
    x_left             DECIMAL(7,2)  NOT NULL,
    y_top              DECIMAL(7,2)  NOT NULL,
    y_bottom           DECIMAL(7,2)  NOT NULL,
    CONSTRAINT PK_room_rows PRIMARY KEY NONCLUSTERED (row_id)
);
CREATE CLUSTERED INDEX CIX_room_rows ON dbo.room_rows (file_id, sheet_no, room_no);
CREATE INDEX IX_room_rows_hotel_date ON dbo.room_rows (hotel_id, report_date, room_no);
GO

CREATE TABLE dbo.report_summary (
    file_id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_report_summary_file REFERENCES dbo.phase1_run_file (file_id),
    total_task_sheets INT           NOT NULL,
    total_rooms       INT           NOT NULL,
    departure_rooms   INT           NOT NULL,
    dirty_rooms       INT           NOT NULL,
    clean_rooms       INT           NOT NULL,
    total_credits     DECIMAL(10,2) NOT NULL,
    CONSTRAINT PK_report_summary PRIMARY KEY CLUSTERED (file_id),
    CONSTRAINT CK_report_summary_rooms CHECK (dirty_rooms + clean_rooms <= total_rooms)
);
GO

CREATE TABLE dbo.pre_phase_requests (
    request_id     UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_pre_phase_requests_id DEFAULT NEWSEQUENTIALID(),
    run_id         UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_pre_phase_requests_run REFERENCES dbo.phase1_run (run_id),
    hotel_id       TINYINT       NOT NULL,
    room_no        VARCHAR(4)    NOT NULL,
    request_type   VARCHAR(16)   NOT NULL CONSTRAINT CK_pre_phase_requests_type CHECK (request_type IN ('CARD', 'DAILY_FULL', 'DAILY_TODAY', 'ROOM_SPECIFIC', 'SPECIAL')),
    colour         VARCHAR(8)    NULL CONSTRAINT CK_pre_phase_requests_colour CHECK (colour IS NULL OR colour IN ('Red', 'Green', 'Purple', 'Blue', 'Gold')),
    note           NVARCHAR(200) NULL,
    request_date   DATE          NOT NULL,
    created_by     UNIQUEIDENTIFIER NOT NULL,
    created_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF_pre_phase_requests_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_pre_phase_requests PRIMARY KEY NONCLUSTERED (request_id),
    CONSTRAINT CK_pre_phase_requests_special CHECK (request_type <> 'SPECIAL' OR colour IS NOT NULL)
);
CREATE CLUSTERED INDEX CIX_pre_phase_requests ON dbo.pre_phase_requests (hotel_id, room_no, request_date);
GO

CREATE TABLE dbo.room_cleaning_status (
    status_id       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_room_cleaning_status_id DEFAULT NEWSEQUENTIALID(),
    file_id         UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_room_cleaning_status_file REFERENCES dbo.phase1_run_file (file_id),
    hotel_id        TINYINT       NOT NULL,
    report_date     DATE          NOT NULL,
    sheet_no        VARCHAR(10)   NOT NULL,
    room_no         VARCHAR(4)    NOT NULL,
    base_colour     VARCHAR(8)    NULL,
    final_colour    VARCHAR(8)    NULL,
    service_code    VARCHAR(30)   NOT NULL,
    reason          NVARCHAR(200) NOT NULL,
    override_reason VARCHAR(20)   NULL,
    assigned_rhythm VARCHAR(10)   NULL,
    cycle_position  TINYINT       NULL,
    nights          SMALLINT      NULL,
    cleaned         BIT           NOT NULL,
    logic_version   TINYINT       NOT NULL CONSTRAINT DF_room_cleaning_status_logic DEFAULT 1,
    CONSTRAINT PK_room_cleaning_status PRIMARY KEY NONCLUSTERED (status_id)
);
CREATE CLUSTERED INDEX CIX_room_cleaning_status ON dbo.room_cleaning_status (file_id, sheet_no, room_no, logic_version);
GO
