using System.Text.Json;
using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace HousekeepingParser.Functions;

public sealed class RunFunctions(Api api, IRunService runs, ILogger<RunFunctions> log)
{
    /// <summary>Multipart: "files" (2 PDFs), "requests" (JSON array), "confirmed" = "true". Plan name: phase1-start.</summary>
    [Function("RunStart")]
    public Task<IActionResult> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "runs")] HttpRequest req,
        [DurableClient] DurableTaskClient durable) =>
        api.RunAsync(req, async user =>
        {
            if (!req.HasFormContentType) throw BusinessRuleException.BadRequest("Please upload the task sheets as a form.");
            var form = await req.ReadFormAsync(req.HttpContext.RequestAborted);
            if (form["confirmed"] != "true") throw BusinessRuleException.BadRequest("The run must be confirmed.");

            var files = new List<UploadedFile>();
            foreach (var f in form.Files.Where(f => f.Name == "files"))
            {
                if (f.Length > RunService.MaxPdfBytes) throw BusinessRuleException.BadRequest($"\"{f.FileName}\" is larger than 20 MB.");
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, req.HttpContext.RequestAborted);
                files.Add(new UploadedFile(f.FileName, ms.ToArray()));
            }

            var requests = ParseRequests(form["requests"].ToString());
            var runId = await runs.CreateAsync(user, files, requests, req.HttpContext.RequestAborted);
            var instanceId = await durable.ScheduleNewOrchestrationInstanceAsync(
                nameof(MorningRunOrchestration.MorningRun), runId, new StartOrchestrationOptions(runId.ToString("N")), req.HttpContext.RequestAborted);
            await runs.SetInstanceAsync(runId, instanceId, req.HttpContext.RequestAborted);
            log.LogInformation("Run {RunId} started with {Requests} guest requests", runId, requests.Count);
            return Api.Ok(new StartRunResponse(runId.ToString()), 201);
        });

    [Function("RunList")]
    public Task<IActionResult> List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs")] HttpRequest req) =>
        api.RunAsync(req, async _ => Api.Ok(await runs.ListAsync(100, req.HttpContext.RequestAborted)));

    [Function("RunGet")]
    public Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id}")] HttpRequest req, string id) =>
        api.RunAsync(req, async _ =>
        {
            var run = Guid.TryParse(id, out var runId) ? await runs.GetAsync(runId, req.HttpContext.RequestAborted) : null;
            return run is null ? Api.Error(404, "Run not found.") : Api.Ok(run);
        });

    /// <summary>The marked PDF, after the permission check, never cached (plan 5.3 / 15).</summary>
    [Function("RunPdf")]
    public Task<IActionResult> Pdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{id}/pdf/{hotelId:int}")] HttpRequest req, string id, int hotelId) =>
        api.RunAsync(req, async _ =>
        {
            if (!Guid.TryParse(id, out var runId)) return Api.Error(404, "PDF not found.");
            var pdf = await runs.OpenMarkedPdfAsync(runId, RhythmFunctions.ParseHotel(hotelId), req.HttpContext.RequestAborted);
            if (pdf is null) return Api.Error(404, "PDF not found.");
            var download = req.Query.ContainsKey("download");
            req.HttpContext.Response.Headers.ContentDisposition = $"{(download ? "attachment" : "inline")}; filename=\"{pdf.Value.FileName}\"";
            req.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return new FileStreamResult(pdf.Value.Pdf, "application/pdf");
        });

    private static List<NewRequest> ParseRequests(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        List<PrePhaseRequestDto>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<PrePhaseRequestDto>>(json, Api.Json);
        }
        catch (JsonException)
        {
            throw BusinessRuleException.BadRequest("The guest requests could not be read.");
        }
        return (items ?? []).Select(i =>
        {
            var hotel = RhythmFunctions.ParseHotel(i.HotelId);
            if (!Enum.TryParse<RequestType>(i.Type, out var type) || !Enum.IsDefined(type))
                throw BusinessRuleException.BadRequest("Unknown request type.");
            ColourName? colour = null;
            if (!string.IsNullOrEmpty(i.Colour))
            {
                if (!Enum.TryParse<ColourName>(i.Colour, out var c) || !Enum.IsDefined(c)) throw BusinessRuleException.BadRequest("Unknown colour.");
                colour = c;
            }
            var note = string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim();
            return new NewRequest(hotel, (i.RoomNo ?? "").Trim(), type, colour, note);
        }).ToList();
    }
}
