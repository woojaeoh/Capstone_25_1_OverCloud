using System.Security.Claims;
using DB.overcloud.Repository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using OverCloud.Api.Auth;
using OverCloud.Services;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// DB 자격증명은 환경변수(ConnectionStrings__Default)로만 주입한다.
// (docs/3TIER_ARCHITECTURE.md 5.4 — UI 클라이언트의 DbConfig.cs에 있던 root 계정 하드코딩을
//  서버 쪽으로 옮기면서 반드시 최소권한 계정으로 재발급/로테이션할 것)
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "DB 연결 문자열이 없습니다. 환경변수 ConnectionStrings__Default 를 설정하세요.");

builder.Services.AddSingleton<IAccountRepository>(_ => new AccountRepository(connectionString));
builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Swagger UI 우측 상단에 Authorize(자물쇠) 버튼을 띄우기 위한 Bearer 스킴 등록.
    // 이게 없으면 JwtBearer 미들웨어는 정상 동작해도 Swagger가 그 사실을 몰라 버튼 자체가 안 생긴다.
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "로그인 응답의 accessToken 값만 입력하면 됨 (앞에 \"Bearer \" 안 붙여도 Swagger가 자동으로 붙여줌)"
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
    });
});

// JWT 정책: docs/3TIER_ARCHITECTURE.md 5.5 — access token은 신원 증명 용도로만, TTL 짧게.
// Jwt:Key는 환경변수(Jwt__Key)로만 주입 — connectionString과 동일한 이유로 코드/설정 파일에 하드코딩 금지.
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT 서명 키가 없습니다. 환경변수 Jwt__Key 를 설정하세요.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "OverCloud.Api";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "OverCloud.Client";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 기본값(true)이면 검증 후 "sub" 클레임이 ClaimTypes.NameIdentifier로 자동 리매핑돼서
        // FindFirstValue(JwtRegisteredClaimNames.Sub)가 항상 null을 반환한다 — 반드시 꺼야 함.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 로그인: 기존 WPF 클라이언트(LoginWindow.xaml.cs)와 동일한 salt+SHA256 검증 로직을 서버에서 재현.
// 성공 시 access token(JWT, 짧은 TTL) + refresh token(opaque, DB에는 해시만 저장)을 발급한다.
app.MapPost("/api/auth/login", (LoginRequest req, IAccountRepository accountRepository, JwtTokenService jwt) =>
{
    var storedSalt = accountRepository.get_salt_by_id(req.UserId);
    if (storedSalt == null)
        return Results.Unauthorized();

    var hashed = PasswordHasher.HashPassword(req.UserId, req.Password, storedSalt);
    var storedPassword = accountRepository.get_password_by_id(req.UserId);
    if (storedPassword == null || hashed != storedPassword)
        return Results.Unauthorized();

    var loginResult = accountRepository.login_overcloud(req.UserId, hashed);
    if (string.IsNullOrEmpty(loginResult))
        return Results.Unauthorized();

    var accessToken = jwt.IssueAccessToken(req.UserId);
    var (refreshToken, refreshHash, refreshExpiry) = jwt.IssueRefreshToken();
    accountRepository.SaveRefreshToken(req.UserId, refreshHash, refreshExpiry);

    return Results.Ok(new AuthResponse(accessToken, refreshToken, refreshExpiry));
});

// refresh: DB에 저장된 해시와 대조 후 access token만 재발급한다 (refresh token 자체는 로테이션하지 않음 —
// 로테이션/동시 refresh 정책은 5.7에서 다룰 후속 논의 대상).
app.MapPost("/api/auth/refresh", (RefreshRequest req, IAccountRepository accountRepository, JwtTokenService jwt) =>
{
    var (storedHash, expiry) = accountRepository.GetRefreshTokenInfo(req.UserId);
    if (storedHash == null || expiry == null || expiry < DateTime.UtcNow)
        return Results.Unauthorized();

    var incomingHash = jwt.HashRefreshToken(req.RefreshToken);
    if (incomingHash != storedHash)
        return Results.Unauthorized();

    var accessToken = jwt.IssueAccessToken(req.UserId);
    return Results.Ok(new { accessToken });
});

// Phase 1 스켈레톤 확인용으로 만든 엔드포인트 — 이제 인증을 요구하고, IDOR 방지를 위해
// 토큰의 sub(로그인한 본인)와 route의 userId가 일치하는 경우만 허용한다 (8절 리스크 참고).
app.MapGet("/api/accounts/{userId}", (string userId, ClaimsPrincipal user, IAccountRepository accountRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub != userId)
        return Results.Forbid();

    var accounts = accountRepository.GetAllAccounts(userId);
    return Results.Ok(accounts);
}).RequireAuthorization();

// async 전환 샘플 엔드포인트 — GetAllAccountsAsync 호출 경로 확인용 (5.7 참고)
app.MapGet("/api/accounts/{userId}/async", async (string userId, ClaimsPrincipal user, IAccountRepository accountRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub != userId)
        return Results.Forbid();

    var accounts = await accountRepository.GetAllAccountsAsync(userId);
    return Results.Ok(accounts);
}).RequireAuthorization();

app.Run();

record LoginRequest(string UserId, string Password);
record RefreshRequest(string UserId, string RefreshToken);
record AuthResponse(string AccessToken, string RefreshToken, DateTime RefreshTokenExpiry);
