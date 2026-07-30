using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

// ─────────────────────────────────────────────────────────────────────────────
// ARRANQUE EN CONTENEDOR (Render): vigilancia de archivos por SONDEO.
//
// Al construir el host, ASP.NET registra `appsettings.json` con reloadOnChange,
// y eso crea un FileSystemWatcher que en Linux consume una instancia de inotify.
// En contenedores ese límite (128) es POR UID en el kernel del HOST y no está
// aislado por contenedor, así que se agota con facilidad y la app moría antes
// siquiera de registrar servicios:
//
//   System.IO.IOException: The configured user limit (128) on the number of
//   inotify instances has been reached ...
//     at Microsoft.Extensions.FileProviders.Physical.PhysicalFilesWatcher...
//     at Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(String[] args)
//
// Con esta variable, los file providers de ASP.NET vigilan por sondeo en vez de
// usar inotify. TIENE QUE IR ANTES de CreateBuilder: es esa llamada la que crea
// el watcher. (Se puede definir también como variable de entorno en Render; aquí
// queda garantizado aunque falte allí.) Coste: un sondeo periódico en lugar de
// notificaciones, irrelevante para esta API, que no recarga configuración en
// caliente.
// ─────────────────────────────────────────────────────────────────────────────
Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");

var builder = WebApplication.CreateBuilder(args);

// ---------------------------
// SWAGGER
// ---------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ---------------------------
// SERVICIOS
// ---------------------------
builder.Services.AddSingleton<ITokenService, TokenService>();

// WarZero: acceso a Firestore + servicio de cierre de turno.
builder.Services.AddSingleton<WarZeroFirestore>();
builder.Services.AddSingleton<WarZeroService>();

// WarZero: orquestador de BOTS. Servicio en segundo plano que reparte los bots
// activos (colección `Bots`, activo==true) por las salas públicas más antiguas
// hasta llenarlas. Se apaga limpio con la aplicación. La pantalla EdicionBotsScreen
// (Flutter) es la que activa/desactiva cada bot.
builder.Services.AddHostedService<BotOrchestratorService>();

// ---------------------------
// JWT
// ---------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<FirebaseAuthService>();

FirebaseApp.Create(new AppOptions
{
    Credential = GoogleCredential.FromJson(
        builder.Configuration["FIREBASE_KEY_JSON"]
    )
});

var app = builder.Build();

// ---------------------------
// SWAGGER UI
// ---------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok("Fenrir API funcionando"));

app.MapAuthEndpoints();
app.MapTokenEndpoints();
app.MapWarZeroEndpoints();

app.Run();