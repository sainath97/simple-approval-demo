using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ApprovalDemo.Api;

public sealed class OutboxWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration,
    ILogger<OutboxWorker> logger, TimeProvider clock) : BackgroundService
{
    private readonly string workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private string OutputDirectory => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
        configuration["Notification:OutputDirectory"] ?? "../demo-notifications"));
    private int MaxAttempts => int.TryParse(configuration["Notification:MaxAttempts"], out var n) ? n : 6;
    private int PollMs => int.TryParse(configuration["Notification:PollMilliseconds"], out var n) ? n : 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(OutputDirectory);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0) await Task.Delay(PollMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox worker loop failed");
                await Task.Delay(PollMs, stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;

        // Atomic claim. UPDLOCK + READPAST allows multiple worker instances to skip each other's rows.
        var rows = await db.OutboxMessages.FromSqlInterpolated($@"
            SELECT TOP (20) * FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ([Status] = {(int)OutboxStatus.Pending} AND [NextAttemptUtc] <= SYSUTCDATETIME())
               OR ([Status] = {(int)OutboxStatus.Processing} AND [LockedUntilUtc] < SYSUTCDATETIME())
            ORDER BY [NextAttemptUtc]")
            .ToListAsync(ct);

        if (rows.Count == 0) return 0;

        foreach (var row in rows)
        {
            row.Status = OutboxStatus.Processing;
            row.LockedBy = workerId;
            row.LockedUntilUtc = now.AddMinutes(2);
            row.Attempts++;
        }
        await db.SaveChangesAsync(ct);

        foreach (var row in rows)
        {
            try
            {
                if (row.Type != "ManagerNotification")
                {
                    row.Status = OutboxStatus.Done;
                    row.ProcessedUtc = clock.GetUtcNow().UtcDateTime;
                    row.LockedUntilUtc = null;
                    continue;
                }

                var payload = JsonDocument.Parse(row.Payload).RootElement;
                var managerId = payload.GetProperty("managerId").GetGuid();
                var requestId = payload.GetProperty("requestId").GetGuid();
                var title = payload.GetProperty("title").GetString() ?? "(untitled)";
                var amount = payload.GetProperty("amount").GetDecimal();
                var finalPath = Path.Combine(OutputDirectory, $"{row.Id:N}.eml");

                // Deterministic filename makes a retry after a crash safe: overwrite the same file.
                var content = $@"To: manager-{managerId}@demo.local
Subject: Purchase request pending approval: {title}
X-Demo-Request-Id: {requestId}
X-Outbox-Message-Id: {row.Id}

A purchase request is waiting for your review.

Request ID: {requestId}
Title: {title}
Amount: {amount:0.00}
Open the demo application to approve or reject the request.
";
                var temp = finalPath + ".tmp";
                await File.WriteAllTextAsync(temp, content, ct);
                File.Move(temp, finalPath, true);

                row.Status = OutboxStatus.Done;
                row.ProcessedUtc = clock.GetUtcNow().UtcDateTime;
                row.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.LastError = ex.Message;
                row.LockedUntilUtc = null;
                if (row.Attempts >= MaxAttempts)
                {
                    row.Status = OutboxStatus.DeadLetter;
                    logger.LogError(ex, "Outbox message {MessageId} dead-lettered", row.Id);
                }
                else
                {
                    row.Status = OutboxStatus.Pending;
                    var seconds = Math.Min(Math.Pow(2, row.Attempts) * 2, 300);
                    row.NextAttemptUtc = clock.GetUtcNow().UtcDateTime
                        .AddSeconds(seconds + Random.Shared.NextDouble() * 3);
                    logger.LogWarning(ex, "Outbox message {MessageId} will retry", row.Id);
                }
            }
            row.LockedBy = null;
            if (row.Status != OutboxStatus.Processing) row.LockedUntilUtc = null;
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
