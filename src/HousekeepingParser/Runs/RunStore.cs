using Dapper;
using HousekeepingParser.Data;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Runs;

/// <summary>Small shared writes for the run tables.</summary>
public sealed class RunStore(IDbConnectionFactory db)
{
    public sealed class FileRow
    {
        public Guid FileId { get; set; }
        public Guid RunId { get; set; }
        public byte UploadOrder { get; set; }
        public string FileLabel { get; set; } = "";
        public string BlobName { get; set; } = "";
        public byte? HotelId { get; set; }
        public DateOnly? ReportDate { get; set; }
        public DateTime? PublishedAtLocal { get; set; }
        public string Status { get; set; } = "";
        public string? MarkedBlobName { get; set; }
        public DateTime RunCreatedAtUtc { get; set; }
    }

    public async Task<FileRow> GetFileAsync(Guid fileId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleAsync<FileRow>(
            """
            SELECT f.file_id AS FileId, f.run_id AS RunId, f.upload_order AS UploadOrder, f.file_label AS FileLabel, f.blob_name AS BlobName,
                   f.hotel_id AS HotelId, f.report_date AS ReportDate, f.published_at_local AS PublishedAtLocal, f.status AS Status,
                   f.marked_blob_name AS MarkedBlobName, r.created_at_utc AS RunCreatedAtUtc
            FROM dbo.phase1_run_file f JOIN dbo.phase1_run r ON r.run_id = f.run_id
            WHERE f.file_id = @fileId
            """, new { fileId });
    }

    /// <summary>Upsert of one step's status (TRY UPDATE then INSERT, no MERGE — plan 8).</summary>
    public async Task StepAsync(Guid fileId, string key, string status, string? message = null, SqlConnection? connection = null, SqlTransaction? tx = null)
    {
        const string sql = """
            UPDATE dbo.phase1_run_step SET status = @status, message = @message, updated_at_utc = SYSUTCDATETIME()
            WHERE file_id = @fileId AND step_key = @key;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.phase1_run_step (file_id, step_key, status, message) VALUES (@fileId, @key, @status, @message);
            """;
        var args = new { fileId, key, status, message = Trim(message, 500) };
        if (connection is not null)
        {
            await connection.ExecuteAsync(sql, args, tx);
            return;
        }
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    public async Task WarnAsync(Guid fileId, string code, string message, SqlConnection? connection = null, SqlTransaction? tx = null)
    {
        const string sql = "INSERT INTO dbo.run_warning (file_id, code, message) VALUES (@fileId, @code, @message)";
        var args = new { fileId, code, message = Trim(message, 500) };
        if (connection is not null)
        {
            await connection.ExecuteAsync(sql, args, tx);
            return;
        }
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    public static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}
