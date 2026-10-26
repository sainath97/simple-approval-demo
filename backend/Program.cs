using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ApprovalDemo.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5080");

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<RequestService>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddCors(o => o.AddPolicy("frontend", p =>
    p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

var jwt = builder.Configuration.GetSection("Jwt");
var signingKey = jwt["SigningKey"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt["Issuer"],
            ValidateAudience = true, ValidAudience = jwt["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("ManagerOnly", p => p.RequireRole("Manager"));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("frontend");
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

// Demo convenience only. Replace EnsureCreated with versioned EF migrations for production.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapPost("/api/demo/token", (DemoTokenRequest request, IConfiguration config) =>
{
    if (request.Role is not ("Employee" or "Manager"))
        return Results.BadRequest(new { error = "Role must be Employee or Manager." });

    var section = config.GetSection("Jwt");
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(section["SigningKey"]!));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, request.UserId.ToString()),
        new Claim(ClaimTypes.Name, request.DisplayName),
        new Claim(ClaimTypes.Role, request.Role)
    };
    var token = new JwtSecurityToken(section["Issuer"], section["Audience"], claims,
        expires: DateTime.UtcNow.AddHours(4), signingCredentials: creds);
    return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token),
        userId = request.UserId, displayName = request.DisplayName, role = request.Role });
}).AllowAnonymous();

app.MapPost("/api/requests", async (CreateRequestDto dto, HttpContext http, RequestService svc, CancellationToken ct) =>
{
    if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var raw) ||
        !Guid.TryParse(raw.ToString(), out var key))
        return Results.Problem("A GUID Idempotency-Key header is required.", statusCode: 400);

    var userId = http.User.GetUserId();
    var result = await svc.CreateAsync(dto, userId, key, ct);
    return result.Replayed
        ? Results.Content(result.Body, "application/json", statusCode: result.StatusCode)
        : Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
}).RequireAuthorization();

app.MapGet("/api/requests/mine", async (HttpContext http, AppDbContext db,
    int? pageSize, string? cursor, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var q = db.Requests.AsNoTracking().Where(r => r.RequesterId == userId);
    var decoded = CursorCodec.Decode(cursor);
    if (decoded is not null)
        q = q.Where(r => r.CreatedUtc < decoded.CreatedUtc ||
            (r.CreatedUtc == decoded.CreatedUtc && r.Id.CompareTo(decoded.Id) < 0));

    var rows = await q.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id)
        .Take(size + 1)
        .Select(r => new RequestListItem(r.Id, r.RequesterId, r.Title, r.Description, r.Amount, r.Status.ToString(),
            r.CreatedUtc, r.DecidedBy, r.DecidedAtUtc, Convert.ToBase64String(r.RowVersion)))
        .ToListAsync(ct);
    var more = rows.Count > size;
    if (more) rows.RemoveAt(rows.Count - 1);
    var next = more ? CursorCodec.Encode(rows[^1].CreatedUtc, rows[^1].Id) : null;
    return Results.Ok(new PageResponse<RequestListItem>(rows, next));
}).RequireAuthorization();

app.MapGet("/api/manager/requests", async (AppDbContext db, int? pageSize, string? cursor,
    string? status, HttpContext http, CancellationToken ct) =>
{
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var managerId = http.User.GetUserId();
    var q = db.Requests.AsNoTracking().Where(r => r.RequesterId != managerId);
    if (Enum.TryParse<RequestStatus>(status, true, out var parsed))
        q = q.Where(r => r.Status == parsed);
    var decoded = CursorCodec.Decode(cursor);
    if (decoded is not null)
        q = q.Where(r => r.CreatedUtc < decoded.CreatedUtc ||
            (r.CreatedUtc == decoded.CreatedUtc && r.Id.CompareTo(decoded.Id) < 0));

    var rows = await q.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id)
        .Take(size + 1)
        .Select(r => new RequestListItem(r.Id, r.RequesterId, r.Title, r.Description, r.Amount, r.Status.ToString(),
            r.CreatedUtc, r.DecidedBy, r.DecidedAtUtc, Convert.ToBase64String(r.RowVersion)))
        .ToListAsync(ct);
    var more = rows.Count > size;
    if (more) rows.RemoveAt(rows.Count - 1);
    var next = more ? CursorCodec.Encode(rows[^1].CreatedUtc, rows[^1].Id) : null;
    return Results.Ok(new PageResponse<RequestListItem>(rows, next));
}).RequireAuthorization("ManagerOnly");

app.MapGet("/api/requests/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, CancellationToken ct) =>
{
    var req = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    if (req is null) return Results.NotFound();
    var isManager = http.User.IsInRole("Manager");
    if (!isManager && req.RequesterId != http.User.GetUserId()) return Results.Forbid();
    var history = await db.DecisionHistories.AsNoTracking().Where(x => x.RequestId == id)
        .OrderBy(x => x.DecidedAtUtc)
        .Select(x => new DecisionDtoResponse(x.ManagerId, x.Decision.ToString(), x.Comment, x.DecidedAtUtc))
        .ToListAsync(ct);
    return Results.Ok(new RequestDetailResponse(req.Id, req.RequesterId, req.Title, req.Description,
        req.Amount, req.Status.ToString(), req.CreatedUtc, req.DecidedBy, req.DecidedAtUtc,
        Convert.ToBase64String(req.RowVersion), history));
}).RequireAuthorization();

app.MapPost("/api/requests/{id:guid}/decision", async (Guid id, DecisionInput input, HttpRequest request,
    HttpContext http, RequestService svc, CancellationToken ct) =>
{
    var raw = request.Headers.IfMatch.ToString().Trim('"');
    if (string.IsNullOrWhiteSpace(raw))
        return Results.Problem("If-Match header with the row version is required.", statusCode: 428);
    byte[] version;
    try { version = Convert.FromBase64String(raw); }
    catch (FormatException) { return Results.BadRequest(new { error = "Invalid If-Match row version." }); }

    if (!Enum.TryParse<RequestStatus>(input.Decision, true, out var decision) ||
        decision is not (RequestStatus.Approved or RequestStatus.Rejected))
        return Results.BadRequest(new { error = "Decision must be Approved or Rejected." });

    var result = await svc.DecideAsync(id, http.User.GetUserId(), decision, input.Comment, version, ct);
    return result.Kind switch
    {
        "Success" => Results.Ok(result),
        "NotFound" => Results.NotFound(),
        "Forbidden" => Results.Forbid(),
        "AlreadyDecided" => Results.Problem(title: "Request already decided",
            detail: $"Current status: {result.CurrentStatus}", statusCode: 409,
            extensions: new Dictionary<string, object?> { ["code"] = "ALREADY_DECIDED", ["status"] = result.CurrentStatus }),
        "Stale" => Results.Problem(title: "Request changed; refresh before deciding",
            statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "STALE_VERSION" }),
        _ => Results.StatusCode(500)
    };
}).RequireAuthorization("ManagerOnly");

app.Run();

public record DemoTokenRequest(Guid UserId, string DisplayName, string Role);
public record CreateRequestDto(string Title, string Description, decimal Amount);
public record DecisionInput(string Decision, string? Comment);
public record RequestListItem(Guid Id, Guid RequesterId, string Title, string Description, decimal Amount, string Status,
    DateTime CreatedUtc, Guid? DecidedBy, DateTime? DecidedAtUtc, string Version);
public record PageResponse<T>(IReadOnlyList<T> Items, string? NextCursor);
public record DecisionDtoResponse(Guid ManagerId, string Decision, string? Comment, DateTime DecidedAtUtc);
public record RequestDetailResponse(Guid Id, Guid RequesterId, string Title, string Description, decimal Amount,
    string Status, DateTime CreatedUtc, Guid? DecidedBy, DateTime? DecidedAtUtc, string Version,
    IReadOnlyList<DecisionDtoResponse> History);
