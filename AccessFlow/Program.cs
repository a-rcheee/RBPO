using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using AccessFlow;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=accessflow.db"));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Вставь JWT-токен (без слова Bearer)"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

var jwtKey = "demo-secret-key-for-ek1-accessflow-please-change-in-prod";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    if (!db.Rooms.Any())
    {
        db.Rooms.AddRange(
            new Room { Name = "Лаборатория A", ManagerId = "manager-1", CapacityLimit = 10 },
            new Room { Name = "Мастерская B", ManagerId = "manager-2", CapacityLimit = 5 }
        );
        db.SaveChanges();
    }
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/rooms", async (AppDbContext db) =>
    await db.Rooms.Select(r => new { r.Id, r.Name, r.CapacityLimit }).ToListAsync());

app.MapPost("/api/requests", async (CreateRequestDto dto, AppDbContext db, ClaimsPrincipal user) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null) return Results.Unauthorized();

    if (dto.StartAt >= dto.EndAt)
        return Results.BadRequest(new { error = "Начало должно быть раньше окончания" });
    if ((dto.EndAt - dto.StartAt).TotalHours > 4)
        return Results.BadRequest(new { error = "Длительность не более 4 часов" });

    var room = await db.Rooms.FindAsync(dto.RoomId);
    if (room is null) return Results.NotFound(new { error = "Помещение не найдено" });

    var req = new AccessRequest
    {
        RoomId = dto.RoomId,
        ApplicantId = userId,
        StartAt = dto.StartAt,
        EndAt = dto.EndAt
    };
    db.Requests.Add(req);
    db.AuditLog.Add(new AuditLogEntry
    {
        UserId = userId,
        Action = "CreateRequest",
        RequestId = req.Id
    });
    await db.SaveChangesAsync();
    return Results.Created($"/api/requests/{req.Id}", new { req.Id, req.Status });
});

app.MapPost("/api/requests/{id}/approve", async (Guid id, AppDbContext db, ClaimsPrincipal user) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null) return Results.Unauthorized();

    var req = await db.Requests.FindAsync(id);
    if (req is null) return Results.NotFound();

    if (req.Status != "PendingApproval")
        return Results.Conflict(new { error = "Заявка не в статусе PendingApproval" });
    if (req.ApplicantId == userId)
        return Results.StatusCode(403);

    var room = await db.Rooms.FindAsync(req.RoomId);
    if (room is null || room.ManagerId != userId)
        return Results.StatusCode(403);

    var overlapping = await db.Requests
        .Where(r => r.RoomId == req.RoomId
                    && r.Id != req.Id
                    && r.Status == "Approved"
                    && r.StartAt < req.EndAt
                    && r.EndAt > req.StartAt)
        .CountAsync();
    if (overlapping >= room.CapacityLimit)
        return Results.Conflict(new { error = "Превышен лимит вместимости" });

    req.Status = "Approved";
    req.ReviewedBy = userId;
    req.ReviewedAt = DateTime.UtcNow;
    db.AuditLog.Add(new AuditLogEntry
    {
        UserId = userId,
        Action = "ApproveRequest",
        RequestId = req.Id
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { req.Id, req.Status });
});

app.MapPost("/api/access/validate", async (ValidateDto dto, AppDbContext db) =>
{
    var req = await db.Requests.FindAsync(dto.RequestId);
    if (req is null) return Results.Ok(new { result = "Deny", reason = "not_found" });

    var now = DateTime.UtcNow;
    if (req.Status != "Approved" || now < req.StartAt || now > req.EndAt)
        return Results.Ok(new { result = "Deny", reason = "status_or_time" });

    req.Status = "Active";
    db.AuditLog.Add(new AuditLogEntry
    {
        UserId = "skud",
        Action = "AccessGranted",
        RequestId = req.Id
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { result = "Allow" });
});

app.MapGet("/api/requests/my", async (AppDbContext db, ClaimsPrincipal user) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null) return Results.Unauthorized();
    return Results.Ok(await db.Requests.Where(r => r.ApplicantId == userId).ToListAsync());
});

app.MapPost("/api/dev/token", (TokenRequestDto dto) =>
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, dto.UserId),
        new Claim(ClaimTypes.Role, dto.Role)
    };
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        claims: claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: creds);
    return Results.Ok(new { token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token) });
});

app.Run();

record CreateRequestDto(Guid RoomId, DateTime StartAt, DateTime EndAt);
record ValidateDto(Guid RequestId);
record TokenRequestDto(string UserId, string Role);