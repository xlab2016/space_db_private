using Api.AspNetCore.Filters;
using Api.AspNetCore.Helpers;
using Api.AspNetCore.Models.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using QAi.Data.QAiDb.DatabaseContext;
using QAi.Helpers;
using Serilog;
using System.Text;
using System.Text.Json.Serialization;

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
    .WriteTo.Console()
);

// Add services to the container.

services.AddHealthChecks();
services.AddControllers(options => options.SuppressOutputFormatterBuffering = true).AddJsonOptions(opt =>
{
    //opt.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<AuthorizeCheckOperationFilter>();
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = @"JWT Authorization header using the Bearer scheme.   
Enter 'Bearer' [space] and then your token in the text input below.
Example: 'Bearer 12345abcdef'",
    });
    var xmlFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly).ToList();
    xmlFiles.ForEach(xmlFile => c.IncludeXmlComments(xmlFile, true));

    c.OperationFilter<FormatXmlCommentProperties>();
    // Include DataAnnotation attributes on Controller Action parameters as Swagger validation rules (e.g required, pattern, ..)
    // Use [ValidateModelState] on Actions to actually validate it in C# as well!
    c.OperationFilter<GeneratePathParamsValidationFilter>();
});

//AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") ?? 
    builder.Configuration.GetConnectionString("PostgresConnection");
services.AddEntityFrameworkNpgsql().AddDbContext<QAiDbContext>(options =>
{
    options.UseNpgsql(connectionString, 
        builder =>
        {
            builder.MigrationsAssembly(typeof(QAiDbContext).Assembly.FullName);
            builder.EnableRetryOnFailure();
        });
});

IdentityModelEventSource.ShowPII = true;

services.Configure<TokenManagement>(configuration.GetSection("tokenManagement"));
var token = configuration.GetSection("tokenManagement").Get<TokenManagement>();
//builder.AddAuthentication();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(_ =>
    {
        _.Authority = token.Authority;
        _.RequireHttpsMetadata = false;
        _.SaveToken = true;
        _.TokenValidationParameters = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(token.Secret)),
            ValidateAudience = true,
            ValidAudience = token.Audience,
            ValidateIssuer = true,
            ValidIssuer = token.Issuer
        };
    });

//services.AddScoped<IUserManagementService, MicroserviceUserManagementService>();
//services.AddScoped<IAuthenticateService, MicroserviceAuthenticationService>();
//services.AddAuthorize<MicroserviceAuthorizeService>();

builder.AddServices();
//builder.AddMapping();
builder.AddWorkflows();

builder.AddSecurity();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHealthChecks($"/api/v1/health");
//app.UseHttpsRedirection();

//app.MapControllers();
//app.UseRouting();
app.UseAuthorization();
app.MapControllers();
//app.UseEndpoints(endpoints =>
//{
//    endpoints.MapControllers();
//});

// global cors policy
app.UseCors(x =>
{
    //x.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    x.WithOrigins("http://localhost:3000", "http://92.38.48.16:3013").AllowAnyMethod().AllowAnyHeader().AllowCredentials()
            .SetIsOriginAllowed((host) => true);
});

// Set WebSocketsOptions
var webSocketOptions = new WebSocketOptions()
{
    ReceiveBufferSize = 8 * 1024
};

// Accept WebSocket
app.UseWebSockets(webSocketOptions);

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Encoding.GetEncoding("windows-1251");
Console.OutputEncoding = System.Text.Encoding.UTF8;

app.Run();
