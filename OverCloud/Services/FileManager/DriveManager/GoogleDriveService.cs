// GoogleDriveService.cs (1~3단계 리팩토링: TokenProvider 기반 + 전체 호출 흐름 점검)
using DB.overcloud.Models;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.Net.Http.Headers;
using System.Text.Json;
using OverCloud.Services;
using Google.Apis.Http;
using System.IO;
using System.Net.Http;
using DB.overcloud.Repository;
using Google.Apis.Upload;
using System.Diagnostics;
using MySqlX.XDevAPI.Common;

namespace OverCloud.Services.FileManager.DriveManager
{
    public class GoogleDriveService : ICloudFileService
    {
        private readonly GoogleTokenProvider tokenProvider;
        private readonly IStorageRepository storageRepo;
        private readonly AccountService accountService;
        private readonly IAccountRepository accountRepository;

        public GoogleDriveService(GoogleTokenProvider tokenProvider, IStorageRepository storageRepo, IAccountRepository accountRepository)
        {
            this.tokenProvider = tokenProvider;
            this.storageRepo = storageRepo;
            this.accountRepository = accountRepository;
        }

        private DriveService CreateDriveService(string accessToken)
        {
            return new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = null,
                ApplicationName = "OverCloud",
                HttpClientFactory = new AccessTokenHttpClientFactory(accessToken)
            });
        }

        public async Task<string> UploadFileAsync(CloudStorageInfo bestStorage, string file_name, string userId)
        {
            var oneCloud = storageRepo.GetCloud(bestStorage.CloudStorageNum, userId);
            if (oneCloud == null) return null;

            var accessToken = await tokenProvider.GetAccessTokenAsync(oneCloud);
            var service = CreateDriveService(accessToken);

            var fileInfo = new FileInfo(file_name);
            var fileMetadata = new Google.Apis.Drive.v3.Data.File()
            {
                Name = fileInfo.Name
            };

            using var stream = new FileStream(file_name,FileMode.Open,FileAccess.Read);

            if (fileMetadata.Name.Length <= 256*1024*1024) 
            {        
                var request = service.Files.Create(fileMetadata, stream, "application/octet-stream");
                request.Fields = "id";
                var result = await request.UploadAsync();
                if (result.Status == Google.Apis.Upload.UploadStatus.Completed)
                {
                    return request.ResponseBody.Id; // Google Drive 파일 ID 반환
                }

                Console.WriteLine($"일반 업로드 실패: {result.Exception}");
                return null;
            }
            else //256MB초과 업로드 -> resumable upload
            {
                var request = service.Files.Create(fileMetadata, stream, "application/octet-stream");
                request.Fields = "id";

                request.ChunkSize = ResumableUpload.MinimumChunkSize * 640; //256kb * 640 = 163MB

                var result = await request.UploadAsync();

                if (result.Status == Google.Apis.Upload.UploadStatus.Completed)
                {
                    return request.ResponseBody.Id;
                }

                Console.WriteLine($"❌ 업로드 실패: {result.Exception}");
                return null;

            }
        }

        public async Task<bool> DownloadFileAsync(int CloudStorageNum, string cloudFileId, string savePath, string userId)
        {
            var clouds = storageRepo.GetCloud(CloudStorageNum,userId);
       //     var googleCloud = clouds.FirstOrDefault(c => c.ID == userId);
            if (clouds == null) return false;

          //  Console.WriteLine(userId);
            Console.WriteLine(" google DownloadFileAsync");

            var accessToken = await tokenProvider.GetAccessTokenAsync(clouds);
            var service = CreateDriveService(accessToken);

            var request = service.Files.Get(cloudFileId);
            using var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
            await request.DownloadAsync(stream);

            return true;
        }

        public async Task<bool> DeleteFileAsync(int cloudStorageNum, string fileId, string userId)
        {
            var googleCloud = storageRepo.GetCloud(cloudStorageNum, userId);
            //var googleCloud = clouds.FirstOrDefault(c => c.CloudStorageNum == cloudStorageNum);
            if (googleCloud == null) return false;

            var accessToken = await tokenProvider.GetAccessTokenAsync(googleCloud);
            var service = CreateDriveService(accessToken);

            try
            {
                await service.Files.Delete(fileId).ExecuteAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 파일 삭제 실패: {ex.Message}");
                return false;
            }
        }


        public async Task<(ulong, ulong)> GetDriveQuotaAsync(int cloudStorageNum, string userId) //admin
        {
            var googleCloud = storageRepo.GetCloud(cloudStorageNum, userId); //

            //var googleCloud = clouds.FirstOrDefault(c => c.CloudStorageNum == cloudStorageNum);
            if (googleCloud == null)
            {
                Console.WriteLine(googleCloud);
                return (0, 0);
            }

            var accessToken = await tokenProvider.GetAccessTokenAsync(googleCloud);
            var service = CreateDriveService(accessToken);

            var about = service.About.Get();
            about.Fields = "storageQuota";
            var result = await about.ExecuteAsync();

            ulong total = ulong.Parse(result.StorageQuota.Limit?.ToString() ?? "0");
            ulong used = ulong.Parse(result.StorageQuota.Usage?.ToString() ?? "0");
          

            return (total, used);
        }

        // 토큰 기반으로 DriveService에 인증 설정하는 HttpClientFactory
        // AccessToken 기반 HttpClientFactory
        private class AccessTokenHttpClientFactory : Google.Apis.Http.IHttpClientFactory
        {
            private readonly string accessToken;

            public AccessTokenHttpClientFactory(string accessToken)
            {
                this.accessToken = accessToken;
            }

            public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
            {
                var handler = new ConfigurableMessageHandler(new HttpClientHandler());
                var client = new ConfigurableHttpClient(handler)
                {
                    DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) }
                };
                return client;
            }
        }
    }
}