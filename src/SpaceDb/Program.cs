using Api.AspNetCore.Filters;
using Api.AspNetCore.Helpers;
using Api.AspNetCore.Models.Configuration;
using Api.AspNetCore.Services;
using Data.Repository.Dapper;
using Data.Repository.Helpers;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Serilog;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using SpaceDb.Data.SpaceDb.DapperContext;
using SpaceDb.Services;
using SpaceDb.Helpers;
using SpaceDb.Data.SpaceDb.Entities;
using SpaceDb.Data.SpaceDb.Ids;
using SpaceDb.Data.SpaceDb.DatabaseContext;
using Magic.Kernel;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var services = builder.Services;
services.Configure<KestrelServerOptions>(options =>
{
    options.AllowSynchronousIO = true;
});

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
);

// Add services to the container.

services.AddScoped<IDapperDbContext, SpaceDbDapperDbContext>();

services.AddHealthChecks();
services.AddControllers(options => options.SuppressOutputFormatterBuffering = true).AddJsonOptions(opt =>
{
    opt.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
}).AddNewtonsoftJson(_ => _.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore).AddXmlDataContractSerializerFormatters();
services.AddEndpointsApiExplorer();
services.AddSwaggerGen(c =>
{
    c.OperationFilter<AuthorizeCheckOperationFilter>();
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = @"JWT Authorization header using the Bearer scheme
Enter 'Bearer' [space] and then your token in the text input below.
Example: 'Bearer 12345abcdef'"
    });
    var xmlFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly).ToList();
    xmlFiles.ForEach(xmlFile => c.IncludeXmlComments(xmlFile));

    c.OperationFilter<FormatXmlCommentProperties>();
    // Include DataAnnotation attributes on Controller Action parameters as Swagger validation rules (e.g required, pattern, ..)
    // Use [ValidateModelState] on Actions to actually validate it in C# as well!
    c.OperationFilter<GeneratePathParamsValidationFilter>();

    c.CustomSchemaIds(type => type.ToString());
});

var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") ??
    builder.Configuration.GetConnectionString("PostgresConnection");
services.AddEntityFrameworkNpgsql().AddDbContext<SpaceDbContext>(options =>
{
    options.UseNpgsql(connectionString,
        builder =>
        {
            builder.MigrationsAssembly(Assembly.GetExecutingAssembly().FullName);
            builder.EnableRetryOnFailure();
        });
});

// Only show PII in development environment for debugging
if (builder.Environment.IsDevelopment())
{
    IdentityModelEventSource.ShowPII = true;
}

services.Configure<TokenManagement>(configuration.GetSection("tokenManagement"));
var token = configuration.GetSection("tokenManagement").Get<TokenManagement>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(_ =>
    {
        _.Authority = token.Authority;
        // Require HTTPS metadata in production for security
        _.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        _.SaveToken = true;
        _.TokenValidationParameters = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(token.Secret)),
            ValidateAudience = false,
            ValidAudience = token.Audience,
            ValidateIssuer = true,
            ValidIssuer = token.Issuer
        };
    });

services.AddAuthorize<MicroserviceAuthorizeService>();

builder.AddServices();
builder.AddMapping();
builder.AddProviders();

builder.AddSecurity();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHealthChecks($"/api/v1/health");

// Enable HTTPS redirection in production for security
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

// global cors policy
app.UseCors(x =>
{
    x.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
});

using (var serviceScope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope())
{
    var kernel = serviceScope.ServiceProvider.GetService<MagicKernel>();
    var defaultSpaceDisk = serviceScope.ServiceProvider.GetService<RocksDbSpaceDisk>();
    var defaultSSCCompiler = serviceScope.ServiceProvider.GetService<Magic.Kernel.Devices.SSC.ISSCompiler>();
    kernel.Devices.Add(defaultSpaceDisk);
    kernel.StartKernel().Wait();
    if (kernel != null)
        kernel.Configuration.DefaultSSCCompiler = defaultSSCCompiler;

    // Apply disk config AFTER StartKernel() (it overwrites ISpaceDisk.Configuration).
    var spaceName = builder.Configuration["SpaceDisk:SpaceName"];
    var vSeq = builder.Configuration["SpaceDisk:VertexSequenceIndex"];
    var rSeq = builder.Configuration["SpaceDisk:RelationSequenceIndex"];
    var sSeq = builder.Configuration["SpaceDisk:ShapeSequenceIndex"];

    if (defaultSpaceDisk != null)
    {
        if (!string.IsNullOrWhiteSpace(spaceName))
            defaultSpaceDisk.Configuration.SpaceName = spaceName.Trim();
        defaultSpaceDisk.Configuration.VertexSequenceIndex ??= long.TryParse(vSeq, out var v) ? v : 0;
        defaultSpaceDisk.Configuration.RelationSequenceIndex ??= long.TryParse(rSeq, out var r) ? r : 0;
        defaultSpaceDisk.Configuration.ShapeSequenceIndex ??= long.TryParse(sSeq, out var s) ? s : 0;
    }
}

app.Run();
