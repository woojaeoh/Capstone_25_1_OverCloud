using DB.overcloud.Repository;

var builder = WebApplication.CreateBuilder(args);

// DB 자격증명은 환경변수(ConnectionStrings__Default)로만 주입한다.
// (docs/3TIER_ARCHITECTURE.md 5.4 — UI 클라이언트의 DbConfig.cs에 있던 root 계정 하드코딩을
//  서버 쪽으로 옮기면서 반드시 최소권한 계정으로 재발급/로테이션할 것)
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "DB 연결 문자열이 없습니다. 환경변수 ConnectionStrings__Default 를 설정하세요.");

builder.Services.AddSingleton<IAccountRepository>(_ => new AccountRepository(connectionString));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Phase 1 스켈레톤 확인용: 서버 프로세스가 실제로 DB까지 왕복하는지 검증하는 엔드포인트.
// 인증/인가는 아직 없음 — Phase 2에서 JWT 미들웨어가 붙기 전까지 이 엔드포인트는 배포하지 말 것.
app.MapGet("/api/accounts/{userId}", (string userId, IAccountRepository accountRepository) =>
{
    var accounts = accountRepository.GetAllAccounts(userId);
    return Results.Ok(accounts);
});

// async 전환 샘플 엔드포인트 — GetAllAccountsAsync 호출 경로 확인용 (5.7 참고)
app.MapGet("/api/accounts/{userId}/async", async (string userId, IAccountRepository accountRepository) =>
{
    var accounts = await accountRepository.GetAllAccountsAsync(userId);
    return Results.Ok(accounts);
});

app.Run();
