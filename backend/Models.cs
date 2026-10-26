using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ApprovalDemo.Api;

public enum RequestStatus { Pending = 0, Approved = 1, Rejected = 2 }
public enum OutboxStatus { Pending = 0, Processing = 1, Done = 2, DeadLetter = 3 }

public sealed class PurchaseRequest
{
    public Guid Id { get; set; }
    public Guid RequesterId { get; set; }
    [MaxLength(160)] public string Title { get; set; } = "";
    [MaxLength(4000)] public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public sealed class DecisionHistory
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid ManagerId { get; set; }
    public RequestStatus Decision { get; set; }
    [MaxLength(2000)] public string? Comment { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}

public sealed class IdempotencyRecord
{
    public Guid UserId { get; set; }
    public Guid Key { get; set; }
    public string RequestHash { get; set; } = "";
    public int? ResponseCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Type { get; set; } = "ManagerNotification";
    public string Payload { get; set; } = "{}";
    [MaxLength(250)] public string DedupeKey { get; set; } = "";
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    [MaxLength(100)] public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public DateTime? ProcessedUtc { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<PurchaseRequest> Requests => Set<PurchaseRequest>();
    public DbSet<DecisionHistory> DecisionHistories => Set<DecisionHistory>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PurchaseRequest>().ToTable(t => t.HasCheckConstraint("CK_Request_Amount", "[Amount] > 0"));
        b.Entity<PurchaseRequest>().HasIndex(x => new { x.RequesterId, x.CreatedUtc, x.Id });
        b.Entity<PurchaseRequest>().HasIndex(x => new { x.Status, x.CreatedUtc, x.Id });
        b.Entity<DecisionHistory>().HasIndex(x => x.RequestId).IsUnique();
        b.Entity<IdempotencyRecord>().HasKey(x => new { x.UserId, x.Key });
        b.Entity<OutboxMessage>().HasIndex(x => x.DedupeKey).IsUnique();
        b.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.NextAttemptUtc });
    }
}

public static class ClaimsExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException("Missing user ID claim.");
    }
}

public sealed record Cursor(DateTime CreatedUtc, Guid Id);
public static class CursorCodec
{
    public static string Encode(DateTime date, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Cursor(date, id))));
    public static Cursor? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(token))); }
        catch { throw new ArgumentException("Invalid cursor token."); }
    }
}
