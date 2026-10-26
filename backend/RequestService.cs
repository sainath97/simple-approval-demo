using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ApprovalDemo.Api;

public sealed record CreateResult(int StatusCode, string Body, bool Replayed);
public sealed record DecisionResult(string Kind, string? CurrentStatus = null, Guid? DecidedBy = null,
    DateTime? DecidedAtUtc = null, bool AlreadyApplied = false);

public sealed class RequestService(AppDbContext db, TimeProvider clock)
{
    public async Task<CreateResult> CreateAsync(CreateRequestDto dto, Guid userId, Guid key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Title) || dto.Title.Length > 160)
            throw new ApiValidationException("Title is required and must be at most 160 characters.");
        if (string.IsNullOrWhiteSpace(dto.Description) || dto.Description.Length > 4000)
            throw new ApiValidationException("Description is required and must be at most 4000 characters.");
        if (dto.Amount <= 0) throw new ApiValidationException("Amount must be greater than zero.");

        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto)));
        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId && x.Key == key, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
                throw new ApiConflictException("Idempotency key reused with a different payload.");
            if (existing.ResponseBody is not null)
                return new CreateResult(existing.ResponseCode ?? 201, existing.ResponseBody, true);
            throw new ApiConflictException("Submission with this key is still processing.");
        }

        var request = new PurchaseRequest
        {
            Id = Guid.NewGuid(), RequesterId = userId, Title = dto.Title.Trim(),
            Description = dto.Description.Trim(), Amount = dto.Amount,
            Status = RequestStatus.Pending, CreatedUtc = clock.GetUtcNow().UtcDateTime
        };
        var body = JsonSerializer.Serialize(new { id = request.Id, status = "Pending", createdUtc = request.CreatedUtc });
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Requests.Add(request);
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId = userId, Key = key, RequestHash = hash, ResponseCode = 201,
            ResponseBody = body, CreatedUtc = now
        });

        // Demo role model: all active users with Manager role are demo recipients.
        // In a real system, resolve approvers by org/department/amount rules.
        var managers = DemoIdentity.ManagerIds;
        foreach (var managerId in managers)
        {
            var notification = JsonSerializer.Serialize(new
            {
                requestId = request.Id, managerId, title = request.Title,
                amount = request.Amount, createdUtc = request.CreatedUtc
            });
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), Type = "ManagerNotification",
                DedupeKey = $"{request.Id}:{managerId}:fake-email",
                Payload = notification, Status = OutboxStatus.Pending,
                OccurredUtc = now, NextAttemptUtc = now
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            // Concurrent same-key request: the unique PK determines the winner.
            var winner = await db.IdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId && x.Key == key, ct);
            if (winner?.RequestHash == hash && winner.ResponseBody is not null)
                return new CreateResult(winner.ResponseCode ?? 201, winner.ResponseBody, true);
            throw;
        }

        return new CreateResult(201, body, false);
    }

    public async Task<DecisionResult> DecideAsync(Guid id, Guid managerId, RequestStatus decision,
        string? comment, byte[] expectedVersion, CancellationToken ct)
    {
        if (comment?.Length > 2000) throw new ApiValidationException("Comment must be at most 2000 characters.");
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var changed = await db.Requests
            .Where(r => r.Id == id && r.Status == RequestStatus.Pending
                && r.RowVersion == expectedVersion && r.RequesterId != managerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, decision)
                .SetProperty(r => r.DecidedBy, managerId)
                .SetProperty(r => r.DecidedAtUtc, now), ct);

        if (changed == 0)
        {
            await tx.RollbackAsync(ct);
            var current = await db.Requests.AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => new { r.Status, r.DecidedBy, r.DecidedAtUtc, r.RequesterId })
                .SingleOrDefaultAsync(ct);
            if (current is null) return new("NotFound");
            if (current.RequesterId == managerId) return new("Forbidden");
            if (current.Status != RequestStatus.Pending)
            {
                if (current.DecidedBy == managerId && current.Status == decision)
                    return new("Success", current.Status.ToString(), current.DecidedBy, current.DecidedAtUtc, true);
                return new("AlreadyDecided", current.Status.ToString(), current.DecidedBy, current.DecidedAtUtc);
            }
            return new("Stale", current.Status.ToString());
        }

        db.DecisionHistories.Add(new DecisionHistory
        {
            RequestId = id, ManagerId = managerId, Decision = decision,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), DecidedAtUtc = now
        });
        var eventPayload = JsonSerializer.Serialize(new { requestId = id, managerId, decision = decision.ToString(), decidedAtUtc = now });
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = "RequestDecisionAuditEvent", DedupeKey = $"decision:{id}",
            Payload = eventPayload, Status = OutboxStatus.Done, OccurredUtc = now,
            NextAttemptUtc = now, ProcessedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new("Success", decision.ToString(), managerId, now);
    }
}

public sealed class ApiValidationException(string message) : Exception(message);
public sealed class ApiConflictException(string message) : Exception(message);

public static class DemoIdentity
{
    // Static demo recipients, intentionally explicit so the fake email mechanism is testable.
    public static readonly Guid[] ManagerIds =
    [
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333")
    ];
}
