using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

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