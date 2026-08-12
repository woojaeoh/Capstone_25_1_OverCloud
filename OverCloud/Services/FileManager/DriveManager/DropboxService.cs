using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;

namespace OverCloud.Services.FileManager.DriveManager
{
    public class DropboxService : ICloudFileService
    {
        private readonly DropboxTokenRefresher dropboxTokenRefresher;
        private readonly IStorageRepository storageRepository;
        private readonly IAccountRepository accountRepository;
        private string accessToken;

        public DropboxService(DropboxTokenRefresher dropboxTokenRefresher, IStorageRepository storageRepository, IAccountRepository accountRepository)
        {
            this.dropboxTokenRefresher = dropboxTokenRefresher;
            this.storageRepository = storageRepository;
            this.accountRepository = accountRepository;
        }

        private HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client;
        }

        private async Task<bool> EnsureAccessTokenAsync(CloudStorageInfo cloud)
        {
            accessToken = await dropboxTokenRefresher.RefreshAccessTokenAsync(cloud);
            return !string.IsNullOrEmpty(accessToken);
        }

        public async Task<string> UploadFileAsync(CloudStorageInfo bestStorage, string filePath, string userId)
        {
            var oneCloud = storageRepository.GetCloud(bestStorage.CloudStorageNum, userId);
            if (oneCloud == null)
            {
                Console.WriteLine("❌ Storage 정보 없음.");
                return null;
            }

            if (!await EnsureAccessTokenAsync(oneCloud))
            {
                Console.WriteLine("❌ Dropbox AccessToken 갱신 실패.");
                return null;
            }

            var client = CreateClient();
            var fileName = Path.GetFileName(filePath);
            var dropboxPath = $"/{fileName}";

            Console.WriteLine($"📤 업로드 시작: {filePath} → {dropboxPath}");

            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var content = new StreamContent(fileStream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload")
                {
                    Content = content
                };
                request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
                {
                    path = dropboxPath,
                    mode = "overwrite",
                    autorename = true,
                    mute = false
                }));

                Console.WriteLine("📤 Dropbox 요청 준비 완료.");

                var response = await client.SendAsync(request);
                Console.WriteLine($"📤 Dropbox 응답 코드: {response.StatusCode}");

                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"📤 Dropbox 응답 내용: {responseContent}");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("❌ Dropbox 업로드 실패.");
                    return null;
                }

                var json = JsonDocument.Parse(responseContent);
                var fileId = json.RootElement.GetProperty("id").GetString();
                Console.WriteLine($"✅ Dropbox 업로드 성공, 파일 ID: {fileId}");
                return fileId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 업로드 중 예외 발생: {ex.Message}");
                return null;
            }
        }


        public async Task<bool> DownloadFileAsync(int cloudStorageNum, string cloudFileId, string savePath, string userId)
        {
            var oneCloud = storageRepository.GetCloud(cloudStorageNum, userId);
            if (oneCloud == null) return false;
            if (!await EnsureAccessTokenAsync(oneCloud)) return false;

            var client = CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
            request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = cloudFileId }));

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;

            using var httpStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
            await httpStream.CopyToAsync(fileStream);
            return true;
        }

        public async Task<bool> DeleteFileAsync(int cloudStorageNum, string cloudFileId, string userId)
        {
            var oneCloud = storageRepository.GetCloud(cloudStorageNum, userId);
            if (oneCloud == null) return false;
            if (!await EnsureAccessTokenAsync(oneCloud)) return false;

            var client = CreateClient();
            var content = new StringContent(JsonSerializer.Serialize(new { path = cloudFileId }), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.dropboxapi.com/2/files/delete_v2", content);

            return response.IsSuccessStatusCode;
        }

        public async Task<(ulong, ulong)> GetDriveQuotaAsync(int cloudStorageNum, string userId)
        {
            var oneCloud = storageRepository.GetCloud(cloudStorageNum, userId);
            if (oneCloud == null) return (0, 0);
            if (!await EnsureAccessTokenAsync(oneCloud)) return (0, 0);

            var client = CreateClient();
            var response = await client.PostAsync("https://api.dropboxapi.com/2/users/get_space_usage", null);
            if (!response.IsSuccessStatusCode) return (0, 0);

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            ulong total = (ulong)json.RootElement.GetProperty("allocation").GetProperty("allocated").GetInt64();
            ulong used = (ulong)json.RootElement.GetProperty("used").GetInt64();

            return (total, used);
        }
    }
}
