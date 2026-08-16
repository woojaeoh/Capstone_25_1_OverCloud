using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OverCloud.Services
{
    // Phase 4 1단계(docs/3TIER_ARCHITECTURE.md) — 클라이언트가 신규 API 서버와 처음 통신하는 지점.
    // 기존 DB 직접 접속 로그인 흐름은 전혀 건드리지 않고, 이 호출은 병행으로만 추가한다 —
    // API 서버가 꺼져 있거나 실패해도 기존 로그인은 그대로 동작해야 하므로 예외를 여기서 삼킨다.
    public static class OverCloudApiClient
    {
        // 로컬 개발 서버 기준(Api/OverCloud.Api/Properties/launchSettings.json). 배포용 주소는
        // 인프라가 정해지는 Phase 5 이후 설정값으로 분리 예정.
        // AuthRefreshHandler를 파이프라인에 꽂아서, 어떤 메서드에서 호출하든 401을 받으면
        // 자동으로 토큰을 갱신하고 원요청을 한 번 재시도한다.
        private static readonly HttpClient _http = new HttpClient(new AuthRefreshHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri("http://localhost:6513")
        };

        public static string AccessToken { get; private set; }
        public static string RefreshToken { get; private set; }
        public static bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken);

        private static string _userId;

        // 동시에 여러 요청이 401을 맞아도 실제 /api/auth/refresh 호출은 한 번만 나가도록 막는 락.
        // refresh token은 rotation 방식(Phase 2)이라, 락 없이 동시에 두 번 갱신하면 두 번째
        // 요청은 이미 무효화된 refresh token으로 호출해 실패한다.
        private static readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        public static async Task<bool> LoginAsync(string userId, string password)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { userId, password });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/auth/login", content);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ API 서버 로그인 실패: {(int)response.StatusCode}");
                    return false;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                AccessToken = json.RootElement.GetProperty("accessToken").GetString();
                RefreshToken = json.RootElement.GetProperty("refreshToken").GetString();
                _userId = userId;

                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

                Console.WriteLine("✅ API 서버 JWT 발급 성공");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ API 서버 연결 실패 (기존 로그인 흐름은 그대로 진행): {ex.Message}");
                return false;
            }
        }

        // 요청 시점의 AccessToken 스냅샷과 지금 AccessToken이 다르면 이미 누군가(다른 401 처리 중이던
        // 요청)가 갱신을 끝낸 것이므로 재사용하고, 같으면 락을 잡고 실제 refresh를 수행한다.
        // 락 안에서 한 번 더 비교하는 건 "락을 기다리는 동안 갱신이 끝났을 수도 있는" 경우를 막기 위함(double-check).
        private static async Task<bool> EnsureFreshTokenAsync(string tokenAtRequestTime)
        {
            if (!string.Equals(AccessToken, tokenAtRequestTime, StringComparison.Ordinal))
                return true;

            await _refreshLock.WaitAsync();
            try
            {
                if (!string.Equals(AccessToken, tokenAtRequestTime, StringComparison.Ordinal))
                    return true;

                return await RefreshAsync();
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        // 호출부 없이 AuthRefreshHandler(401 감지 시)와 EnsureFreshTokenAsync에서만 쓰인다.
        // 성공하면 AccessToken/RefreshToken을 새 값으로 교체(rotation)하고, 실패하면(refresh token
        // 만료/무효 등) 토큰을 비워서 이후 요청들이 재로그인 필요 상태로 자연스럽게 떨어지게 한다.
        private static async Task<bool> RefreshAsync()
        {
            if (string.IsNullOrEmpty(_userId) || string.IsNullOrEmpty(RefreshToken))
                return false;

            try
            {
                var payload = JsonSerializer.Serialize(new { userId = _userId, refreshToken = RefreshToken });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/auth/refresh", content);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ 토큰 갱신 실패: {(int)response.StatusCode} — 재로그인이 필요합니다.");
                    AccessToken = null;
                    RefreshToken = null;
                    _http.DefaultRequestHeaders.Authorization = null;
                    return false;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                AccessToken = json.RootElement.GetProperty("accessToken").GetString();
                RefreshToken = json.RootElement.GetProperty("refreshToken").GetString();

                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

                Console.WriteLine("✅ 토큰 자동 갱신 성공");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 토큰 갱신 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        // 401을 가로채 자동 갱신 + 원요청 재시도를 수행하는 핸들러. /api/auth/* 자체는 재시도 대상에서
        // 제외한다(로그인 실패/refresh 자체 실패를 401 재시도 루프에 태우면 무한 루프 위험이 있음 —
        // 실제로 이 두 엔드포인트는 JWT 인증이 필요 없어 401이 나지 않지만, 방어적으로 경로를 막아둔다).
        private class AuthRefreshHandler : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string tokenAtRequestTime = AccessToken;

                var response = await base.SendAsync(request, cancellationToken);

                if (response.StatusCode != HttpStatusCode.Unauthorized || string.IsNullOrEmpty(tokenAtRequestTime))
                    return response;

                string path = request.RequestUri?.AbsolutePath ?? "";
                if (path.StartsWith("/api/auth/"))
                    return response;

                bool refreshed = await EnsureFreshTokenAsync(tokenAtRequestTime);
                if (!refreshed)
                    return response; // 갱신 실패(재로그인 필요) — 같은 토큰으로 재시도해봐야 또 401이라 원래 응답을 그대로 반환

                response.Dispose();

                var retryRequest = await CloneRequestAsync(request);
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                return await base.SendAsync(retryRequest, cancellationToken);
            }

            // HttpRequestMessage는 한 번 보내면 재사용할 수 없어서, 재시도 전에 메서드/URI/헤더/바디를
            // 그대로 복제한 새 인스턴스를 만든다.
            private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
            {
                var clone = new HttpRequestMessage(original.Method, original.RequestUri)
                {
                    Version = original.Version
                };

                if (original.Content != null)
                {
                    var bytes = await original.Content.ReadAsByteArrayAsync();
                    var newContent = new ByteArrayContent(bytes);
                    foreach (var header in original.Content.Headers)
                        newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    clone.Content = newContent;
                }

                foreach (var header in original.Headers)
                    clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

                return clone;
            }
        }

        // Phase 4 2단계(5.6) — LanTransferService의 온라인 상태 갱신/피어 IP 조회를 API로 옮기는 단계.
        // 로그인이 안 돼있거나(IsLoggedIn == false) 요청 자체가 실패하면 호출부가 기존 DB 직접 조회로
        // 폴백할 수 있도록 예외를 삼키고 false/null만 반환한다.
        public static async Task<bool> UpdatePresenceAsync(string localIp, bool isOnline)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var payload = JsonSerializer.Serialize(new { localIp, isOnline });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/presence", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ presence 갱신 API 호출 실패 (DB 직접 갱신으로 폴백): {ex.Message}");
                return false;
            }
        }

        public static async Task<string> GetPresenceAsync(string targetUserId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/presence/{Uri.EscapeDataString(targetUserId)}");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                return json.RootElement.GetProperty("localIp").GetString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ presence 조회 API 호출 실패 (DB 직접 조회로 폴백): {ex.Message}");
                return null;
            }
        }

        // Phase 4 3단계 — 파일 업/다운로드 경로 교체(5.1: 서버는 바이트를 중계하지 않는다).
        // 아래 메서드들은 presence와 달리 DB 직접 접속 폴백이 없다 — CloudTierManager.SelectBestStorage
        // 등 클라이언트의 DB 직접 조회 코드를 "같이 쓰기"가 아니라 "교체"하기로 한 결정에 따른 것이라,
        // API 서버가 꺼져 있으면 업/다운로드는 실패한다(의도된 동작 — 기존처럼 조용히 DB로 폴백하지 않음).

        // provider: "google" 또는 "onedrive" (서버 라우트 세그먼트와 동일)
        // userId: 이 스토리지의 소유 계정(본인 또는 협업 계정) — 서버가 sub와 별개로 소유권/멤버십을 확인한다.
        public static async Task<string> GetOAuthAccessTokenAsync(string provider, string userId, int cloudStorageNum)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var payload = JsonSerializer.Serialize(new { userId, cloudStorageNum });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync($"/api/oauth/{provider}/access-token", content);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ {provider} access token 조회 실패: {(int)response.StatusCode}");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                return json.RootElement.GetProperty("accessToken").GetString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ {provider} access token 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        // 스토리지 추가 1단계: 인증 URL 구성에 필요한 client_id/redirect_uri/scope를 서버에서 받아온다
        // (로컬 C:\key\*.json 의존 제거).
        public static async Task<(string clientId, string redirectUri, string scope)?> GetOAuthClientConfigAsync(string provider)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/oauth/{provider}/client-config");
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ {provider} client-config 조회 실패: {(int)response.StatusCode}");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                return (
                    json.RootElement.GetProperty("clientId").GetString(),
                    json.RootElement.GetProperty("redirectUri").GetString(),
                    json.RootElement.GetProperty("scope").GetString()
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ {provider} client-config 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        // 스토리지 추가 2단계: authorization code -> access/refresh token 교환. client_secret은 서버가
        // 들고 있고 여기로는 절대 안 내려온다.
        public static async Task<(string accessToken, string refreshToken)?> ExchangeOAuthCodeAsync(string provider, string code)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var payload = JsonSerializer.Serialize(new { code });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync($"/api/oauth/{provider}/exchange-code", content);
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ {provider} authorization code 교환 실패: {(int)response.StatusCode} {errBody}");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                return (
                    json.RootElement.GetProperty("accessToken").GetString(),
                    json.RootElement.GetProperty("refreshToken").GetString()
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ {provider} authorization code 교환 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        // 스토리지 추가 3단계: 클라이언트가 이미 코드 교환 + 이메일/용량 조회까지 마친 뒤 호출 —
        // 서버는 CloudStorageInfo 행 생성 + 할당량 집계만 한다.
        public static async Task<(bool Success, string ErrorMessage)> AddStorageAsync(
            string userId, string cloudType, string accountId, string refreshToken, ulong totalCapacityKB, ulong usedCapacityKB)
        {
            if (!IsLoggedIn)
                return (false, "로그인이 필요합니다.");

            try
            {
                var payload = JsonSerializer.Serialize(new { userId, cloudType, accountId, refreshToken, totalCapacityKB, usedCapacityKB });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/storages", content);
                if (response.IsSuccessStatusCode)
                    return (true, null);

                var body = await response.Content.ReadAsStringAsync();
                try { using var json = JsonDocument.Parse(body); return (false, json.RootElement.GetProperty("error").GetString()); }
                catch { return (false, $"스토리지 추가 실패 ({(int)response.StatusCode})"); }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 스토리지 추가 API 호출 실패: {ex.Message}");
                return (false, "서버에 연결할 수 없습니다.");
            }
        }

        // userId: 업로드 대상 계정(본인 또는 협업 계정) — SelectBestStorage가 이 계정 소유 클라우드 중에서 고른다.
        // 409(진짜 용량 부족)와 그 외 실패(네트워크 예외/서버 다운/인증 오류 등)를 구분해서 반환한다 —
        // 전자만 분산 저장 폴백 대상이고, 후자는 폴백 없이 업로드 자체를 실패 처리해야 한다(5.1 원칙).
        public static async Task<SelectStorageResult> SelectStorageAsync(string userId, ulong fileSizeKB)
        {
            if (!IsLoggedIn)
                return new SelectStorageResult(null, ServerUnreachable: true);

            try
            {
                var payload = JsonSerializer.Serialize(new { userId, fileSizeKB });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/files/select-storage", content);

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    return new SelectStorageResult(null, ServerUnreachable: false); // 진짜 용량 부족 — 분산 저장 폴백 대상

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ 스토리지 선택 실패: {(int)response.StatusCode}");
                    return new SelectStorageResult(null, ServerUnreachable: true);
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                var selected = new SelectedStorage(
                    root.GetProperty("cloudStorageNum").GetInt32(),
                    root.GetProperty("cloudType").GetString(),
                    root.GetProperty("accountId").GetString());
                return new SelectStorageResult(selected, ServerUnreachable: false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 스토리지 선택 API 호출 실패 (서버 다운/네트워크 오류): {ex.Message}");
                return new SelectStorageResult(null, ServerUnreachable: true);
            }
        }

        // userId: 업로드 대상 계정(본인 또는 협업 계정) — 생성되는 CloudFileInfo 행의 소유자가 된다.
        public static async Task<int?> ConfirmUploadAsync(string userId, int cloudStorageNum, string cloudFileId, string fileName, ulong fileSizeKB, int parentFolderId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var payload = JsonSerializer.Serialize(new { userId, cloudStorageNum, cloudFileId, fileName, fileSizeKB, parentFolderId });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/files/confirm-upload", content);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ 업로드 확정 실패: {(int)response.StatusCode}");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                return json.RootElement.GetProperty("fileId").GetInt32();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 업로드 확정 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<FileLocation> GetFileLocationAsync(int fileId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/files/{fileId}/location");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                return new FileLocation(
                    root.GetProperty("cloudStorageNum").GetInt32(),
                    root.GetProperty("cloudType").GetString(),
                    root.GetProperty("cloudFileId").GetString(),
                    root.GetProperty("fileName").GetString(),
                    root.GetProperty("fileSize").GetUInt64());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 파일 위치 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        // Phase 4 4단계 — HomeView/SharedAccountView의 CloudTierManager.GetTotalRemainingQuotaInBytes
        // (DB 직접 조회) 교체용. GET /api/quota/{userId}의 집계값으로 남은 용량(byte)을 계산한다.
        public static async Task<ulong?> GetRemainingQuotaBytesAsync(string userId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/quota/{Uri.EscapeDataString(userId)}");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                ulong totalKB = root.GetProperty("totalCapacityKB").GetUInt64();
                ulong usedKB = root.GetProperty("usedCapacityKB").GetUInt64();
                ulong remainingKB = totalKB > usedKB ? totalKB - usedKB : 0;
                return remainingKB * 1024;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 할당량 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }
        // Phase 4 — 협업 계정(coop): 생성/가입/탈퇴/목록/멤버 조회. 이슈 화면들이 여기 의존하므로
        // 이슈 클라이언트 교체보다 먼저 옮긴다. presence와 달리 DB 직접 폴백은 없다(5.1과 같은 이유
        // — CoopUserRepository DB 직접 호출을 "같이 쓰기"가 아니라 "교체"하기로 함).
        public static async Task<bool> CreateCoopAsync(string coopId, string password)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var payload = JsonSerializer.Serialize(new { coopId, password });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/coop", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 협업 계정 생성 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> JoinCoopAsync(string coopId, string password)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var payload = JsonSerializer.Serialize(new { coopId, password });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/coop/join", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 협업 계정 가입 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> LeaveCoopAsync(string coopId)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var response = await _http.PostAsync($"/api/coop/{Uri.EscapeDataString(coopId)}/leave", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 협업 계정 탈퇴 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        // 본인이 속한 협업 계정 목록 — 로그인한 sub 기준(대상 계정 파라미터 없음).
        public static async Task<List<string>> GetMyCoopAccountsAsync()
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync("/api/coop/mine");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<string>>(body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 협업 계정 목록 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<List<string>> GetCoopMembersAsync(string coopId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/coop/{Uri.EscapeDataString(coopId)}/members");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<string>>(body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 협업 계정 멤버 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        // Phase 4 — 이슈 트래커. FileIssueInfo/FileIssueComment는 이미 클라이언트에 소스 링크돼 있는
        // DB.overcloud.Models 타입을 그대로 재사용한다(전역 네임스페이스라 using 불필요) — View들의
        // 기존 List<FileIssueInfo> 바인딩을 그대로 쓸 수 있게. 서버가 camelCase로 내려주므로
        // PropertyNameCaseInsensitive로 역직렬화한다.
        private static readonly JsonSerializerOptions _caseInsensitiveJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static async Task<int?> CreateIssueAsync(string coopId, string title, string description, string assignedTo, DateTime? dueDate, List<int> fileIds)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var payload = JsonSerializer.Serialize(new { coopId, title, description, assignedTo, dueDate, fileIds });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync("/api/issues", content);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ 이슈 등록 실패: {(int)response.StatusCode}");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                return json.RootElement.GetProperty("issueId").GetInt32();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 이슈 등록 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<List<FileIssueInfo>> GetIssuesAsync(string coopId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/issues/{Uri.EscapeDataString(coopId)}");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<FileIssueInfo>>(body, _caseInsensitiveJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 이슈 목록 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<List<FileIssueInfo>> GetIssuesByFileAsync(int fileId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/issues/by-file/{fileId}");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<FileIssueInfo>>(body, _caseInsensitiveJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 파일별 이슈 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<bool> UpdateIssueAsync(int issueId, string title, string description, string assignedTo, string status, DateTime? dueDate)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var payload = JsonSerializer.Serialize(new { title, description, assignedTo, status, dueDate });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PutAsync($"/api/issues/{issueId}", content);
                if (!response.IsSuccessStatusCode)
                    Console.WriteLine($"❌ 이슈 수정 실패: {(int)response.StatusCode}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 이슈 수정 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> DeleteIssueAsync(int issueId)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var response = await _http.DeleteAsync($"/api/issues/{issueId}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 이슈 삭제 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        public static async Task<List<int>> GetIssueFilesAsync(int issueId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/issues/{issueId}/files");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<int>>(body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 이슈별 파일 목록 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<List<FileIssueComment>> GetIssueCommentsAsync(int issueId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/issues/{issueId}/comments");
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<FileIssueComment>>(body, _caseInsensitiveJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 댓글 목록 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<bool> AddIssueCommentAsync(int issueId, string commentContent)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var payload = JsonSerializer.Serialize(new { content = commentContent });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync($"/api/issues/{issueId}/comments", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 댓글 등록 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        // Phase 4 — 스토리지 삭제(재분배) 3단계. select-storage/confirm-upload와 동일한 계약: 서버는 계획/기록만,
        // 실제 다운로드→업로드는 호출부(StorageRedistributionManager)가 CloudApi 토큰 클라이언트로 직접 수행한다.
        public static async Task<RedistributionPlan> GetRedistributionPlanAsync(int cloudStorageNum, string userId)
        {
            if (!IsLoggedIn)
                return null;

            try
            {
                var response = await _http.GetAsync($"/api/storages/{cloudStorageNum}/redistribution-plan?userId={Uri.EscapeDataString(userId)}");
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ 재분배 계획 조회 실패: {(int)response.StatusCode} {errBody}");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<RedistributionPlan>(body, _caseInsensitiveJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 재분배 계획 조회 API 호출 실패: {ex.Message}");
                return null;
            }
        }

        public static async Task<bool> RelocateConfirmAsync(int fileId, int newCloudStorageNum, string newCloudFileId)
        {
            if (!IsLoggedIn)
                return false;

            try
            {
                var payload = JsonSerializer.Serialize(new { newCloudStorageNum, newCloudFileId });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync($"/api/files/{fileId}/relocate-confirm", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 파일 재배치 확정 API 호출 실패: {ex.Message}");
                return false;
            }
        }

        // 실패 사유(분산 파일 있음/재분배 미완료 등)를 서버가 준 문자열 그대로 사용자에게 보여줘야 해서
        // 성공 여부와 에러 메시지를 함께 반환한다.
        public static async Task<(bool Success, string ErrorMessage)> DeleteStorageAsync(int cloudStorageNum, string userId)
        {
            if (!IsLoggedIn)
                return (false, "로그인이 필요합니다.");

            try
            {
                var response = await _http.DeleteAsync($"/api/storages/{cloudStorageNum}?userId={Uri.EscapeDataString(userId)}");
                if (response.IsSuccessStatusCode)
                    return (true, null);

                var body = await response.Content.ReadAsStringAsync();
                try
                {
                    using var json = JsonDocument.Parse(body);
                    return (false, json.RootElement.GetProperty("error").GetString());
                }
                catch
                {
                    return (false, $"스토리지 삭제 실패 ({(int)response.StatusCode})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 스토리지 삭제 API 호출 실패: {ex.Message}");
                return (false, "서버에 연결할 수 없습니다.");
            }
        }
    }

    public record SelectedStorage(int CloudStorageNum, string CloudType, string AccountId);
    // ServerUnreachable: true면 서버 다운/네트워크 오류 등 — 분산 저장 폴백 금지, 업로드 실패 처리.
    // false + Storage == null이면 409(진짜 용량 부족) — 분산 저장 폴백 대상.
    public record SelectStorageResult(SelectedStorage Storage, bool ServerUnreachable);
    public record FileLocation(int CloudStorageNum, string CloudType, string CloudFileId, string FileName, ulong FileSize);
    public record RedistributionCandidate(int CloudStorageNum, string CloudType, string AccountId);
    public record RedistributionFilePlan(int FileId, string FileName, string CloudFileId, ulong FileSizeKB, List<RedistributionCandidate> Candidates);
    public record RedistributionPlan(List<RedistributionFilePlan> Files);
}
