-- 0005: weekly report (Phase 3, step 9) gets its own recipients.
ALTER TABLE dbo.report_recipient DROP CONSTRAINT CK_report_recipient_kind;
ALTER TABLE dbo.report_recipient ADD CONSTRAINT CK_report_recipient_kind
    CHECK (report_kind IN ('DAILY_TASK_SHEET', 'DAILY_HANDOVER', 'WEEKLY', 'MONTHLY'));
GO
