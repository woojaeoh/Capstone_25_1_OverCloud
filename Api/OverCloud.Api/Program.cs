using System.Security.Claims;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using MySql.Data.MySqlClient;
using OverCloud.Api.Auth;
using OverCloud.Services;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.Services.StorageManager;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// DB 자격증명은 환경변수(ConnectionStrings__Default)로만 주입한다.
// (docs/3TIER_ARCHITECTURE.md 5.4 — UI 클라이언트의 DbConfig.cs에 있던 root 계정 하드코딩을
//  서버 쪽으로 옮기면서 반드시 최소권한 계정으로 재발급/로테이션할 것)
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "DB 연결 문자열이 없습니다. 환경변수 ConnectionStrings__Default 를 설정하세요.");

builder.Services.AddSingleton<IAccountRepository>(_ => new AccountRepository(connectionString));
builder.Services.AddSingleton<IStorageRepository>(_ => new StorageRepository(connectionString));
builder.Services.AddSingleton<IFileRepository>(_ => new FileRepository(connectionString));
builder.Services.AddSingleton<ICoopUserRepository>(_ => new CoopUserRepository(connectionString));
builder.Services.AddSingleton<IFileIssueRepository>(_ => new FileIssueRepository(connectionString));
builder.Services.AddSingleton<IFileIssueCommentRepository>(_ => new FileIssueCommentRepository(connectionString));
builder.Services.AddSingleton<IFileIssueMappingRepository>(_ => new FileIssueMappingRepository(connectionString));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GoogleOAuthService>();
builder.Services.AddSingleton<OneDriveOAuthService>();

// Phase 4 — /api/files에 필요한 서비스. 5.1(서버 프록시 안 함) 결정에 따라 실제 클라우드 API 호출
// (GoogleDriveService/OneDriveService 등 ICloudFileService 구현체, FileUploadManager 등)은 서버에
// 전혀 두지 않는다 — 서버는 메타데이터(CloudFileInfo 행)와 할당량 숫자만 다룬다.
// QuotaManager 생성자는 IEnumerable<ICloudFileService>를 요구하지만, 여기서 실제로 호출하는
// UpdateQuotaAfterUploadOrDelete는 그 필드를 쓰지 않으므로(순수 DB 산술) 빈 리스트로 충분하다 —
// SaveDriveQuotaToDB처럼 그 필드를 쓰는 다른 메서드는 서버에서 호출하지 않는다.
builder.Services.AddSingleton<IEnumerable<ICloudFileService>>(new List<ICloudFileService>());
builder.Services.AddSingleton<CloudTierManager>();
builder.Services.AddSingleton<QuotaManager>();

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

// 부트스트랩: CloudFileInfo 루트 sentinel 행(file_id=-1, parent_folder_id=-2)이 없으면 만든다.
// FileRepository.GetFullPath가 parent_folder_id 체인을 -2를 만날 때까지 타고 올라가는데(최상위 파일들의
// parent_folder_id는 관례상 -1), 이 행이 없으면 GetFileById(-1)이 null을 반환해 다음 반복에서
// NullReferenceException으로 죽는다. GetFileById는 소유자 필터 없이 file_id만 보므로(WHERE file_id=@id)
// 계정별이 아니라 시스템 전체에 이 행이 딱 하나만 있으면 된다 — 지금까지는 운영 DB에 수동으로 넣어둔 상태였다.
// 기존 값(ID='DEFAULT', file_name='ROOT')과 동일하게 맞춰서, 이미 이 행이 있는 지금 DB에는 아무 영향이 없다.
using (var bootstrapConn = new MySqlConnection(connectionString))
{
    bootstrapConn.Open();
    using var checkCmd = new MySqlCommand("SELECT COUNT(*) FROM CloudFileInfo WHERE file_id = -1", bootstrapConn);
    long existingRootCount = Convert.ToInt64(checkCmd.ExecuteScalar());
    if (existingRootCount == 0)
    {
        using var insertCmd = new MySqlCommand(@"
            INSERT INTO CloudFileInfo (file_id, file_name, file_size, cloud_storage_num, parent_folder_id, is_folder, ID)
            VALUES (-1, 'ROOT', 0, -1, -2, 1, 'DEFAULT')", bootstrapConn);
        insertCmd.ExecuteNonQuery();
        Console.WriteLine("✅ CloudFileInfo 루트 sentinel 행(file_id=-1) 생성됨 — 새 DB 부트스트랩");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

// 협업 계정 공용 인가 판단: sub(로그인한 본인)가 targetUserId 자신이거나, targetUserId가 협업 계정이고
// sub가 그 협업 계정의 정당한 멤버(CoopUserInfo)인 경우 허용한다. /api/quota, /api/files/*,
// /api/oauth/{provider}/access-token 전부 "이 클라우드 스토리지/파일이 어느 계정 소유인가"를
// sub 하나로만 가정하면 SharedAccountView(협업 계정으로 업/다운로드)가 항상 깨지므로 공통으로 뺐다.
bool IsAuthorizedForAccount(string sub, string targetUserId, ICoopUserRepository coopUserRepository)
{
    if (sub == targetUserId)
        return true;
    return coopUserRepository.connected_cooperation_account_nums(sub).Contains(targetUserId);
}

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

// refresh: DB에 저장된 해시와 대조 후 access token과 refresh token을 모두 재발급한다(rotation).
// 매 refresh마다 이전 refresh token 해시는 새 값으로 덮어써지므로, 탈취된 토큰이 재사용되면
// 정상 사용자가 먼저 refresh한 시점부터 값이 어긋나 다음 요청부터 거부된다.
app.MapPost("/api/auth/refresh", (RefreshRequest req, IAccountRepository accountRepository, JwtTokenService jwt) =>
{
    var (storedHash, expiry) = accountRepository.GetRefreshTokenInfo(req.UserId);
    if (storedHash == null || expiry == null || expiry < DateTime.UtcNow)
        return Results.Unauthorized();

    var incomingHash = jwt.HashRefreshToken(req.RefreshToken);
    if (incomingHash != storedHash)
        return Results.Unauthorized();

    var accessToken = jwt.IssueAccessToken(req.UserId);
    var (newRefreshToken, newRefreshHash, newRefreshExpiry) = jwt.IssueRefreshToken();
    accountRepository.SaveRefreshToken(req.UserId, newRefreshHash, newRefreshExpiry);

    return Results.Ok(new AuthResponse(accessToken, newRefreshToken, newRefreshExpiry));
});

// 로그아웃: DB에 저장된 refresh token 해시를 지워서 이후 refresh를 막는다.
// access token 자체는 stateless라 즉시 무효화는 불가 — TTL(기본 20분)이 지나야 완전히 끝난다 (5.5 참고).
app.MapPost("/api/auth/logout", (ClaimsPrincipal user, IAccountRepository accountRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    accountRepository.RevokeRefreshToken(sub);
    return Results.NoContent();
}).RequireAuthorization();

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

// Phase 4(5.6) — LAN 전송 signaling: 클라이언트가 Account 테이블에 직접 UPDATE/SELECT하던
// 온라인 상태·IP 조회를 API로 옮긴다. 갱신(POST)은 위조 방지를 위해 반드시 sub(본인)만 가능하고,
// 조회(GET)는 P2P 특성상 원래도 다른 사용자의 LAN IP를 알아야 하므로 대상 제한을 두지 않는다
// (로그인된 사용자라면 누구나 조회 가능 — 기존 GetLocalIp 동작과 동일, 새로 생긴 권한 아님).
app.MapPost("/api/presence", (
    PresenceUpdateRequest req,
    ClaimsPrincipal user,
    IAccountRepository accountRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    bool result = accountRepository.UpdateOnlineStatus(sub, req.IsOnline ? req.LocalIp : null, req.IsOnline);
    return result ? Results.Ok() : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/api/presence/{targetUserId}", (
    string targetUserId,
    IAccountRepository accountRepository) =>
{
    var localIp = accountRepository.GetLocalIp(targetUserId);
    return localIp != null ? Results.Ok(new { localIp }) : Results.NotFound();
}).RequireAuthorization();

// Phase 4 — 할당량 조회: QuotaManager의 집계 로직(UpdateAggregatedStorageForUser 등)과 동일하게
// "저장된 값을 합산"만 한다 — 클라우드 제공자에 실시간 조회(GetDriveQuotaAsync)는 하지 않음(느리고
// 이번 엔드포인트의 책임이 아님, 그건 SaveDriveQuotaToDB 쪽 역할). AccountService/QuotaManager는
// 이미 OverCloud.Api.csproj에 소스로 링크돼 있어(Phase 1) 새 DI 등록 없이 기존 IAccountRepository로 충분.
// SYSTEM 더미 스토리지(cloud_storage_num=-1, AccountRepository.cs 계정 생성 시 자동 삽입)는 제외한다.
//
// 인가 범위: userId가 sub(본인) 자신이거나, sub가 userId(협업 계정)의 정당한 멤버(CoopUserInfo에
// user_id=sub, coop_id=userId 행이 있음)인 경우까지 허용한다 — SharedAccountView가 자기 소유가 아닌
// 협업 계정 ID로도 할당량을 조회해야 하기 때문(5.1/5.6과 같은 이유로 클라이언트 직접 DB 접속을 대체).
// **신규 보안 체크**: 기존 클라이언트 DB 직접 경로(CloudTierManager.GetTotalRemainingQuotaInBytes)는
// 이 멤버십을 전혀 확인하지 않고 클라이언트가 넘긴 accountId를 그대로 믿었다 — 단순 이관이 아니라
// 여기서 처음으로 추가되는 검증이다.
app.MapGet("/api/quota/{userId}", (string userId, ClaimsPrincipal user, IAccountRepository accountRepository, ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (!IsAuthorizedForAccount(sub, userId, coopUserRepository))
        return Results.Forbid();

    var storages = accountRepository.GetAllAccounts(userId)
        .Where(c => c.CloudType != "SYSTEM")
        .Select(c => new
        {
            c.CloudStorageNum,
            c.CloudType,
            c.AccountId,
            TotalCapacityKB = c.TotalCapacity,
            UsedCapacityKB = c.UsedCapacity
        })
        .ToList();

    ulong totalKB = storages.Aggregate(0UL, (acc, s) => acc + s.TotalCapacityKB);
    ulong usedKB = storages.Aggregate(0UL, (acc, s) => acc + s.UsedCapacityKB);

    return Results.Ok(new { storages, totalCapacityKB = totalKB, usedCapacityKB = usedKB });
}).RequireAuthorization();

// Phase 4 — 파일 목록 조회: userId(본인 또는 협업 계정) 소유의 특정 폴더(parentFolderId) 안의 파일/폴더 목록.
// 최상위는 클라이언트 기존 관례와 동일하게 -1. all_file_list 쿼리 자체가 "WHERE ID = @user_id"로
// 걸려있어(FileRepository.cs) userId가 아닌 계정의 파일은 애초에 조회되지 않는다 — 다만 sub가 그
// userId를 조회할 자격이 있는지는 IsAuthorizedForAccount로 별도 확인해야 한다(협업 계정 케이스).
// 참고: 아직 어떤 클라이언트 코드도 이 엔드포인트를 호출하지 않는다 — HomeView/SharedAccountView는
// 여전히 FileRepository.all_file_list를 DB 직접 호출로 쓰고 있음(목록 조회는 이번 이관 범위 밖).
app.MapGet("/api/files/{userId}/{parentFolderId:int}", (
    string userId,
    int parentFolderId,
    ClaimsPrincipal user,
    IFileRepository fileRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();
    if (!IsAuthorizedForAccount(sub, userId, coopUserRepository))
        return Results.Forbid();

    var files = fileRepository.all_file_list(parentFolderId, userId);
    return Results.Ok(files);
}).RequireAuthorization();

// Phase 4 — 스토리지 선택: 파일 크기(KB)를 넣으면 CloudTierManager의 기존 티어 로직(OneDrive>Google>Dropbox,
// 동률이면 여유공간 많은 순)으로 업로드 대상 스토리지를 정해준다. 클라이언트는 이 응답의 CloudStorageNum으로
// POST /api/oauth/{provider}/access-token을 호출해 access token을 받고, 그 토큰으로 클라우드 API를
// "직접" 호출해 실제 바이트를 올린다(5.1 — 서버는 바이트를 중계하지 않는다).
// req.UserId는 업로드 대상 계정 — 본인 계정일 수도, 협업 계정일 수도 있다(SharedAccountView).
// sub 자신 또는 그 협업 계정의 정당한 멤버일 때만 허용한다(IsAuthorizedForAccount).
app.MapPost("/api/files/select-storage", (
    SelectStorageRequest req,
    ClaimsPrincipal user,
    CloudTierManager cloudTierManager,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();
    if (!IsAuthorizedForAccount(sub, req.UserId, coopUserRepository))
        return Results.Forbid();

    var best = cloudTierManager.SelectBestStorage(req.FileSizeKB, req.UserId);
    if (best == null)
        return Results.Conflict(new { error = "저장 가능한 클라우드가 없습니다 (용량 부족)." });

    return Results.Ok(new { best.CloudStorageNum, best.CloudType, best.AccountId });
}).RequireAuthorization();

// Phase 4 — 업로드 확정: 클라이언트가 클라우드에 바이트를 이미 올린 뒤(select-storage → access-token →
// 클라우드 API 직접 호출), 그 결과(cloudFileId)를 알려주면 서버는 CloudFileInfo 행을 만들고 할당량만 갱신한다.
// req.UserId(업로드 대상 계정, 본인 또는 협업 계정)로 GetCloud를 조회해야 한다 — select-storage가
// 그 계정 소유 스토리지를 골랐기 때문에, 여기서 sub로 조회하면(협업 계정 업로드일 때) 항상 404가 난다.
app.MapPost("/api/files/confirm-upload", (
    ConfirmUploadRequest req,
    ClaimsPrincipal user,
    IStorageRepository storageRepository,
    IFileRepository fileRepository,
    QuotaManager quotaManager,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();
    if (!IsAuthorizedForAccount(sub, req.UserId, coopUserRepository))
        return Results.Forbid();

    var cloud = storageRepository.GetCloud(req.CloudStorageNum, req.UserId);
    if (cloud == null)
        return Results.Forbid();

    var file = new CloudFileInfo
    {
        FileName = req.FileName,
        FileSize = req.FileSizeKB,
        UploadedAt = DateTime.Now,
        CloudStorageNum = req.CloudStorageNum,
        ParentFolderId = req.ParentFolderId,
        IsFolder = false,
        CloudFileId = req.CloudFileId,
        ID = req.UserId
    };

    int fileId = fileRepository.AddFileAndReturnId(file);
    quotaManager.UpdateQuotaAfterUploadOrDelete(req.CloudStorageNum, req.FileSizeKB, true, req.UserId);

    return Results.Ok(new { fileId });
}).RequireAuthorization();

// Phase 4 — 다운로드 위치 조회: 서버는 바이트를 중계하지 않으므로(5.1), fileId 소유자를 확인한 뒤
// 어느 클라우드의 어떤 파일인지(cloudStorageNum, cloudFileId)만 알려준다. 클라이언트가 이 값으로
// access-token을 받아 클라우드 API에서 직접 바이트를 받는다.
// 소유자(file.ID)가 sub 본인이거나 sub가 그 협업 계정의 정당한 멤버면 허용한다.
app.MapGet("/api/files/{fileId:int}/location", (
    int fileId,
    ClaimsPrincipal user,
    IFileRepository fileRepository,
    IStorageRepository storageRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var file = fileRepository.GetFileById(fileId);
    if (file == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, file.ID, coopUserRepository))
        return Results.Forbid();
    if (file.IsFolder)
        return Results.BadRequest(new { error = "폴더는 다운로드 대상이 아닙니다." });

    // 클라이언트가 어느 제공자(Google/OneDrive) API를 호출해야 하는지 알아야 하므로 cloudType도 같이 내려준다.
    // GetCloud는 file.ID(실제 소유 계정) 기준으로 조회해야 한다 — 협업 계정 파일이면 sub로는 항상 못 찾는다.
    var cloud = storageRepository.GetCloud(file.CloudStorageNum, file.ID);
    if (cloud == null)
        return Results.NotFound();

    return Results.Ok(new { file.CloudStorageNum, cloud.CloudType, file.CloudFileId, file.FileName, file.FileSize });
}).RequireAuthorization();

// Phase 4 — 파일 삭제(메타데이터): 클라이언트가 클라우드 API로 실제 파일을 이미 지운 뒤 호출하는 게 계약이다
// (업로드와 대칭 — 서버는 바이트를 만지지 않는다). 폴더는 원래 클라우드 측 삭제가 없는 논리 항목이라
// 곧바로 DB 행만 지우면 된다. 소유자(본인 또는 협업 계정 멤버) 확인 후 진행.
app.MapDelete("/api/files/{fileId:int}", (
    int fileId,
    ClaimsPrincipal user,
    IFileRepository fileRepository,
    QuotaManager quotaManager,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var file = fileRepository.GetFileById(fileId);
    if (file == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, file.ID, coopUserRepository))
        return Results.Forbid();

    bool dbDeleted = fileRepository.DeleteFile(fileId);
    if (dbDeleted && !file.IsFolder)
        quotaManager.UpdateQuotaAfterUploadOrDelete(file.CloudStorageNum, file.FileSize, false, file.ID);

    return dbDeleted ? Results.NoContent() : Results.Problem("삭제 실패", statusCode: StatusCodes.Status500InternalServerError);
}).RequireAuthorization();

// Phase 3 — Google Drive access_token 발급: client_secret은 서버 설정에서만 읽고,
// refresh_token은 GetCloud(cloudStorageNum, req.UserId)로 "그 계정 소유 row"만 조회해 IDOR을 막는다
// (WHERE cloud_storage_num=@num AND ID=@id 조건이 리포지토리 쿼리 안에 이미 있음).
// req.UserId는 본인 또는 협업 계정일 수 있다 — sub가 그 계정에 접근 권한이 있는지 먼저 확인한다.
app.MapPost("/api/oauth/google/access-token", async (
    OAuthAccessTokenRequest req,
    ClaimsPrincipal user,
    IStorageRepository storageRepository,
    ICoopUserRepository coopUserRepository,
    GoogleOAuthService googleOAuth) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();
    if (!IsAuthorizedForAccount(sub, req.UserId, coopUserRepository))
        return Results.Forbid();

    var cloud = storageRepository.GetCloud(req.CloudStorageNum, req.UserId);
    if (cloud == null)
        return Results.NotFound();

    if (string.IsNullOrEmpty(cloud.RefreshToken))
        return Results.BadRequest(new { error = "이 계정에 저장된 refresh token이 없습니다." });

    try
    {
        var (accessToken, expiry) = await googleOAuth.ExchangeRefreshTokenAsync(cloud.RefreshToken);
        return Results.Ok(new { accessToken, expiry });
    }
    catch (OAuthRefreshTokenInvalidException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
}).RequireAuthorization();

// Phase 3 — OneDrive access_token 발급: Google과 동일 패턴. OneDrive는 public client라
// client_secret 자체가 없고 client_id만 서버 설정(OAuth:OneDrive:ClientId)에서 읽는다.
app.MapPost("/api/oauth/onedrive/access-token", async (
    OAuthAccessTokenRequest req,
    ClaimsPrincipal user,
    IStorageRepository storageRepository,
    ICoopUserRepository coopUserRepository,
    OneDriveOAuthService oneDriveOAuth) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();
    if (!IsAuthorizedForAccount(sub, req.UserId, coopUserRepository))
        return Results.Forbid();

    var cloud = storageRepository.GetCloud(req.CloudStorageNum, req.UserId);
    if (cloud == null)
        return Results.NotFound();

    if (string.IsNullOrEmpty(cloud.RefreshToken))
        return Results.BadRequest(new { error = "이 계정에 저장된 refresh token이 없습니다." });

    try
    {
        var (accessToken, expiry) = await oneDriveOAuth.ExchangeRefreshTokenAsync(cloud.RefreshToken);
        return Results.Ok(new { accessToken, expiry });
    }
    catch (OAuthRefreshTokenInvalidException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
}).RequireAuthorization();

// Phase 4 — 이슈 트래커: FileIssueInfo.ID가 소유 협업 계정이다. 오늘 반복한 패턴 그대로,
// sub 본인 또는 그 협업 계정의 정당한 멤버만 접근 가능(IsAuthorizedForAccount). 이슈 단위 조작
// (수정/삭제/댓글/파일목록)은 issue_id로 GetIssueById를 먼저 조회해 소유 협업 계정을 알아낸 뒤 검증한다 —
// 기존 클라이언트 DB 직접 경로(IssueDetailView 등)는 이 소유권 확인을 전혀 하지 않았다(신규 검증).
app.MapPost("/api/issues", (
    IssueCreateRequest req,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    IFileIssueMappingRepository mappingRepository,
    IFileRepository fileRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();
    if (!IsAuthorizedForAccount(sub, req.CoopId, coopUserRepository))
        return Results.Forbid();

    // assigned_to는 account 테이블 FK라, 존재하지 않거나 이 협업 계정 멤버가 아닌 ID를 그대로 넘기면
    // MySqlException(FK 제약 위반)이 원시 500으로 노출된다 — 미리 검증해서 깔끔한 400을 준다.
    if (!string.IsNullOrEmpty(req.AssignedTo) && !coopUserRepository.GetUsersByCoopId(req.CoopId).Contains(req.AssignedTo))
        return Results.BadRequest(new { error = "존재하지 않거나 이 협업 계정 멤버가 아닌 담당자입니다." });

    var issue = new FileIssueInfo
    {
        ID = req.CoopId,
        Title = req.Title,
        Description = req.Description,
        CreatedBy = sub, // 클라이언트가 보낸 값이 아니라 토큰의 sub를 그대로 씀(위조 방지)
        AssignedTo = req.AssignedTo,
        Status = "OPEN",
        CreatedAt = DateTime.Now,
        DueDate = req.DueDate
    };

    int issueId = issueRepository.AddIssue(issue);
    if (issueId <= 0)
        return Results.Problem("이슈 등록 실패", statusCode: StatusCodes.Status500InternalServerError);

    // fileId가 실제로 이 협업 계정(req.CoopId) 소유인지 확인 후에만 매핑한다 — 다른 계정 파일에
    // 이슈를 붙이는 것을 막는다(기존 클라이언트 코드엔 이 확인이 없었다).
    if (req.FileIds != null)
    {
        foreach (var fileId in req.FileIds)
        {
            var file = fileRepository.GetFileById(fileId);
            if (file == null || file.ID != req.CoopId)
                continue;
            mappingRepository.AddMapping(issueId, fileId);
        }
    }

    return Results.Ok(new { issueId });
}).RequireAuthorization();

app.MapGet("/api/issues/{coopId}", (
    string coopId,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();
    if (!IsAuthorizedForAccount(sub, coopId, coopUserRepository))
        return Results.Forbid();

    return Results.Ok(issueRepository.GetAllIssues(coopId));
}).RequireAuthorization();

// 특정 파일에 달린 이슈 목록 — 인가는 이슈가 아니라 그 파일의 소유자(file.ID) 기준.
app.MapGet("/api/issues/by-file/{fileId:int}", (
    int fileId,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    IFileRepository fileRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var file = fileRepository.GetFileById(fileId);
    if (file == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, file.ID, coopUserRepository))
        return Results.Forbid();

    return Results.Ok(issueRepository.GetIssuesByFileId(fileId));
}).RequireAuthorization();

app.MapPut("/api/issues/{issueId:int}", (
    int issueId,
    IssueUpdateRequest req,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var existing = issueRepository.GetIssueById(issueId);
    if (existing == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, existing.ID, coopUserRepository))
        return Results.Forbid();

    // assigned_to는 account 테이블 FK라, 존재하지 않거나 이 협업 계정 멤버가 아닌 ID를 그대로 넘기면
    // MySqlException(FK 제약 위반)이 원시 500으로 노출된다 — 미리 검증해서 깔끔한 400을 준다.
    if (!string.IsNullOrEmpty(req.AssignedTo) && !coopUserRepository.GetUsersByCoopId(existing.ID).Contains(req.AssignedTo))
        return Results.BadRequest(new { error = "존재하지 않거나 이 협업 계정 멤버가 아닌 담당자입니다." });

    existing.Title = req.Title;
    existing.Description = req.Description;
    existing.AssignedTo = req.AssignedTo;
    existing.Status = req.Status;
    existing.DueDate = req.DueDate;

    bool updated = issueRepository.UpdateIssue(existing);
    return updated ? Results.Ok() : Results.Problem("이슈 수정 실패", statusCode: StatusCodes.Status500InternalServerError);
}).RequireAuthorization();

app.MapDelete("/api/issues/{issueId:int}", (
    int issueId,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    IFileIssueMappingRepository mappingRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var existing = issueRepository.GetIssueById(issueId);
    if (existing == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, existing.ID, coopUserRepository))
        return Results.Forbid();

    mappingRepository.DeleteMappingsByIssueId(issueId);
    bool deleted = issueRepository.DeleteIssue(issueId);
    return deleted ? Results.NoContent() : Results.Problem("이슈 삭제 실패", statusCode: StatusCodes.Status500InternalServerError);
}).RequireAuthorization();

app.MapGet("/api/issues/{issueId:int}/files", (
    int issueId,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    IFileIssueMappingRepository mappingRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var issue = issueRepository.GetIssueById(issueId);
    if (issue == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, issue.ID, coopUserRepository))
        return Results.Forbid();

    return Results.Ok(mappingRepository.GetFileIdsByIssueId(issueId));
}).RequireAuthorization();

app.MapGet("/api/issues/{issueId:int}/comments", (
    int issueId,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    IFileIssueCommentRepository commentRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var issue = issueRepository.GetIssueById(issueId);
    if (issue == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, issue.ID, coopUserRepository))
        return Results.Forbid();

    return Results.Ok(commentRepository.GetCommentsByIssueId(issueId));
}).RequireAuthorization();

app.MapPost("/api/issues/{issueId:int}/comments", (
    int issueId,
    IssueCommentRequest req,
    ClaimsPrincipal user,
    IFileIssueRepository issueRepository,
    IFileIssueCommentRepository commentRepository,
    ICoopUserRepository coopUserRepository) =>
{
    var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
    if (sub == null)
        return Results.Unauthorized();

    var issue = issueRepository.GetIssueById(issueId);
    if (issue == null)
        return Results.NotFound();
    if (!IsAuthorizedForAccount(sub, issue.ID, coopUserRepository))
        return Results.Forbid();

    var comment = new FileIssueComment
    {
        IssueId = issueId,
        CommenterId = sub, // 클라이언트가 보낸 값이 아니라 토큰의 sub를 그대로 씀(위조 방지)
        Content = req.Content,
        CreatedAt = DateTime.Now
    };

    bool added = commentRepository.AddComment(comment);
    return added ? Results.Ok() : Results.Problem("댓글 등록 실패", statusCode: StatusCodes.Status500InternalServerError);
}).RequireAuthorization();

app.Run();

record LoginRequest(string UserId, string Password);
record RefreshRequest(string UserId, string RefreshToken);
record AuthResponse(string AccessToken, string RefreshToken, DateTime RefreshTokenExpiry);
record OAuthAccessTokenRequest(string UserId, int CloudStorageNum);
record PresenceUpdateRequest(string? LocalIp, bool IsOnline);
record SelectStorageRequest(string UserId, ulong FileSizeKB);
record ConfirmUploadRequest(string UserId, int CloudStorageNum, string CloudFileId, string FileName, ulong FileSizeKB, int ParentFolderId);
record IssueCreateRequest(string CoopId, string Title, string? Description, string? AssignedTo, DateTime? DueDate, List<int>? FileIds);
record IssueUpdateRequest(string Title, string? Description, string? AssignedTo, string Status, DateTime? DueDate);
record IssueCommentRequest(string Content);
