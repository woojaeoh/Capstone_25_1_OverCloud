using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        private static readonly HttpClient _http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:6513")
        };

        public static string AccessToken { get; private set; }
        public static string RefreshToken { get; private set; }
        public static bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken);

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
    }

    public record SelectedStorage(int CloudStorageNum, string CloudType, string AccountId);
    // ServerUnreachable: true면 서버 다운/네트워크 오류 등 — 분산 저장 폴백 금지, 업로드 실패 처리.
    // false + Storage == null이면 409(진짜 용량 부족) — 분산 저장 폴백 대상.
    public record SelectStorageResult(SelectedStorage Storage, bool ServerUnreachable);
    public record FileLocation(int CloudStorageNum, string CloudType, string CloudFileId, string FileName, ulong FileSize);
}
