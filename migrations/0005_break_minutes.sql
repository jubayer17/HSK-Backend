-- Final Phase 1 decision: prepare cleaner time records for minute-based break reporting.
-- Keep decimal minutes so existing fractional-hour values survive conversion without rounding loss.
ALTER TABLE dbo.daily_close_cleaner ADD
    break_minutes DECIMAL(6,2) NOT NULL CONSTRAINT DF_daily_close_cleaner_break_minutes DEFAULT 0;
GO
UPDATE dbo.daily_close_cleaner SET break_minutes = pause_hours * 60;
GO
ALTER TABLE dbo.daily_close_cleaner ADD CONSTRAINT CK_daily_close_cleaner_break_minutes CHECK (break_minutes >= 0);
-- pause_hours remains a compatibility column for older installations; new reads use break_minutes.
