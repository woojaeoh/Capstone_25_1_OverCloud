using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;

namespace overcloud.CloudApi
{
    // Phase 4 3단계(docs/3TIER_ARCHITECTURE.md 5.1) — 클라이언트가 Google Drive에 직접 업/다운로드하기 위한
    // 토큰-only 클라이언트. OverCloud.Services.FileManager.DriveManager.GoogleDriveService와 달리
    // storageRepo.GetCloud(DB 직접 조회)나 GoogleTokenProvider(client_secret으로 로컬 refresh_token 교환)를
    // 전혀 쓰지 않는다 — access token은 호출부가 OverCloudApiClient.GetOAuthAccessTokenAsync로 미리 받아 넘긴다.
    public static class GoogleDriveTokenClient
    {
        private static DriveService CreateDriveService(string accessToken)
        {
            return new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = null,
                ApplicationName = "OverCloud",
                HttpClientFactory = new AccessTokenHttpClientFactory(accessToken)
            });
        }

        public static async Task<string> UploadAsync(string accessToken, string filePath)
        {
            var service = CreateDriveService(accessToken);
            var fileInfo = new FileInfo(filePath);
            var fileMetadata = new Google.Apis.Drive.v3.Data.File { Name = fileInfo.Name };

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var request = service.Files.Create(fileMetadata, stream, "application/octet-stream");
            request.Fields = "id";

            if (fileInfo.Length > 256 * 1024 * 1024) // 256MB 초과 -> resumable upload
                request.ChunkSize = ResumableUpload.MinimumChunkSize * 640;

            var result = await request.UploadAsync();
            if (result.Status != Google.Apis.Upload.UploadStatus.Completed)
            {
                Console.WriteLine($"❌ Google Drive 업로드 실패: {result.Exception}");
                return null;
            }

            return request.ResponseBody.Id;
        }

        public static async Task<bool> DownloadAsync(string accessToken, string cloudFileId, string savePath)
        {
            var service = CreateDriveService(accessToken);
            var request = service.Files.Get(cloudFileId);
            using var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
            await request.DownloadAsync(stream);
            return true;
        }

        public static async Task<bool> DeleteAsync(string accessToken, string cloudFileId)
        {
            var service = CreateDriveService(accessToken);
            try
            {
                await service.Files.Delete(cloudFileId).ExecuteAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Google Drive 삭제 실패: {ex.Message}");
                return false;
            }
        }

        private class AccessTokenHttpClientFactory : IHttpClientFactory
        {
            private readonly string _accessToken;
            public AccessTokenHttpClientFactory(string accessToken) => _accessToken = accessToken;

            public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
            {
                var handler = new ConfigurableMessageHandler(new HttpClientHandler());
                return new ConfigurableHttpClient(handler)
                {
                    DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", _accessToken) }
                };
            }
        }
    }
}
