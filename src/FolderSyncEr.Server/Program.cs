using System.Text;
using System.Threading.RateLimiting;
using FolderSyncEr.Server.Data;
using FolderSyncEr.Server.Endpoints;
using FolderSyncEr.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/server-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();

builder.Host.UseSerilog();

// Database
builder.Services.AddDbContext<ServerDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config.GetConnectionString("DefaultConnection") ?? "Data Source=foldersyncer.db";
    options.UseSqlite(connectionString);
});

// Identity Password Hasher
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

// Domain Services
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddScoped<ITrashService, TrashService>();
builder.Services.AddScoped<ILogService, LogService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();

// JWT Authentication
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "FolderSyncEr_Super_Secret_Development_Key_At_Least_32_Bytes_Long_2026_Secure!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "FolderSyncEr.Server";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "FolderSyncEr.Client";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// Rate limiting for login endpoint
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("LoginLimiter", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });
});

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "FolderSyncEr API",
        Version = "v1",
        Description = "Cross-device file sync backend service"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer {token}'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Kestrel large upload handling
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // MaxRequestBodySize will be controlled by settings/service limits up to 10GB
    serverOptions.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024;
});

var app = builder.Build();

// Forwarded headers for reverse proxies (Caddy/nginx)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Swagger in Development or all environments for convenient testing
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "FolderSyncEr API v1");
    c.RoutePrefix = "swagger";
});

app.UseSerilogRequestLogging();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Map Minimal API Endpoints
app.MapStatusEndpoints();
app.MapAuthEndpoints();
app.MapFileEndpoints();
app.MapTrashEndpoints();
app.MapLogEndpoints();
app.MapSettingEndpoints();
app.MapDeviceEndpoints();

// Initialize Database & Seed Defaults
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
    await db.Database.EnsureCreatedAsync();

    var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
    var adminUser = builder.Configuration["Bootstrap:AdminUsername"] ?? "admin";
    var adminPass = builder.Configuration["Bootstrap:AdminPassword"] ?? "AdminPassword123!";
    await auth.SeedInitialAdminAsync(adminUser, adminPass, CancellationToken.None);

    var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
    await settings.InitializeDefaultSettingsAsync(CancellationToken.None);

    var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
    blobStorage.CleanupTempFiles();
}

app.Run();

// Partial class for WebApplicationFactory in integration tests
public partial class Program { }
