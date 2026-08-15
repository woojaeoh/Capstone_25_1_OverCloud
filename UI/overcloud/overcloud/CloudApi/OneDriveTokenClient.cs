using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace overcloud.CloudApi
{
    // Phase 4 3단계(docs/3TIER_ARCHITECTURE.md 5.1) — GoogleDriveTokenClient와 동일한 이유로,
    // OverCloud.Services.FileManager.DriveManager.OneDriveService(OneDriveTokenRefresher/storageRepo 의존)를
    // 그대로 쓰지 않고 토큰-only 버전을 새로 둔다.
    public static class OneDriveTokenClient
    {
        public static async Task<string> UploadAsync(string accessToken, string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var fileName = fileInfo.Name;

            if (fileInfo.Length <= 100 * 1024 * 1024) // 100MB 이하 -> 단일 업로드
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var uploadUrl = $"https://graph.microsoft.com/v1.0/me/drive/root:/{fileName}:/content";
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var response = await client.PutAsync(uploadUrl, new StreamContent(fileStream));
                if (!response.IsSuccessStatusCode) return null;

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return json.RootElement.GetProperty("id").GetString();
            }

            return await UploadLargeFileViaSessionAsync(accessToken, filePath, fileName);
        }

        private static async Task<string> UploadLargeFileViaSessionAsync(string accessToken, string filePath, string fileName)
        {
            // 1. 업로드 세션 생성(여기에만 토큰이 필요)
            using var sessionClient = new HttpClient();
            sessionClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var sessionPayload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["item"] = new Dictionary<string, object>
                {
                    ["@microsoft.graph.conflictBehavior"] = "rename",
                    ["name"] = fileName
                }
            });

            var sessionResponse = await sessionClient.PostAsync(
                $"https://graph.microsoft.com/v1.0/me/drive/root:/{fileName}:/createUploadSession",
                new StringContent(sessionPayload, Encoding.UTF8, "application/json"));

            if (!sessionResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ OneDrive 업로드 세션 생성 실패: {sessionResponse.StatusCode}");
                return null;
            }

            using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
            string uploadUrl = sessionJson.RootElement.GetProperty("uploadUrl").GetString();

            // 2. 조각 업로드 (Authorization 헤더 절대 붙이지 않음 — 업로드 세션 URL 자체가 인증을 대신함)
            const int chunkSize = 100 * 1024 * 1024;
            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;
            long uploaded = 0;

            using var uploadClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromMinutes(10)
            };

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            HttpResponseMessage finalResponse = null;

            while (uploaded < fileSize)
            {
                int thisChunkSize = (int)Math.Min(chunkSize, fileSize - uploaded);
                byte[] buffer = new byte[thisChunkSize];
                int read = await stream.ReadAsync(buffer, 0, thisChunkSize);
                if (read == 0) break;

                var content = new ByteArrayContent(buffer, 0, read);
                content.Headers.Add("Content-Range", $"bytes {uploaded}-{uploaded + read - 1}/{fileSize}");

                var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
                var chunkResponse = await uploadClient.SendAsync(request);

                if (!chunkResponse.IsSuccessStatusCode &&
                    chunkResponse.StatusCode != HttpStatusCode.Accepted &&
                    chunkResponse.StatusCode != HttpStatusCode.Created &&
                    chunkResponse.StatusCode != HttpStatusCode.OK)
                {
                    var error = await chunkResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ OneDrive 조각 업로드 실패 at byte {uploaded}: {error}");
                    return null;
                }

                finalResponse = chunkResponse;
                uploaded += read;
            }

            // 3. 완료 후 응답 처리
            if (finalResponse != null && finalResponse.IsSuccessStatusCode)
            {
                using var resultJson = JsonDocument.Parse(await finalResponse.Content.ReadAsStringAsync());
                if (resultJson.RootElement.TryGetProperty("id", out var idProperty))
                    return idProperty.GetString();
            }

            Console.WriteLine("❌ OneDrive 조각 업로드는 완료했지만 파일 ID를 찾지 못함");
            return null;
        }

        public static async Task<bool> DownloadAsync(string accessToken, string cloudFileId, string savePath)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await client.GetAsync($"https://graph.microsoft.com/v1.0/me/drive/items/{cloudFileId}/content");
            if (!response.IsSuccessStatusCode) return false;

            using var httpStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await httpStream.CopyToAsync(fileStream);
            return true;
        }

        public static async Task<bool> DeleteAsync(string accessToken, string cloudFileId)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await client.DeleteAsync($"https://graph.microsoft.com/v1.0/me/drive/items/{cloudFileId}");
            return response.IsSuccessStatusCode;
        }
    }
}
