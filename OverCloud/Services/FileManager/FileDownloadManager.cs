using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using OverCloud.Services.FileManager.DriveManager;

namespace OverCloud.Services.FileManager
{
    public class FileDownloadManager
    {
      //  private readonly Dictionary<string, ICloudFileService> serviceMap;
         private readonly IAccountRepository acountRepository;
        private readonly List<ICloudFileService> cloudServices;
        private readonly IFileRepository fileRepo;
        private readonly IStorageRepository storageRepository;

//        new GoogleDriveService(new GoogleTokenProvider() , new StorageRepository(DbConfig.ConnectionString) )
        public FileDownloadManager(
           
            IFileRepository fileRepo,
            IAccountRepository acountRepository,
            List<ICloudFileService> cloudServices,
            IStorageRepository storageRepository
            )

        {
            this.fileRepo = fileRepo;
            this.acountRepository = acountRepository;
            this.cloudServices = cloudServices;
            this.storageRepository = storageRepository;
        }

        public async Task <bool> DownloadFile(string userId, string cloudFileId, int CloudStorageNum, string savePath)
        {
            Console.WriteLine(userId);
            Console.WriteLine("DownloadFile");
            

            var cloudInfo = storageRepository.GetCloud(CloudStorageNum, userId);
            //var cloudInfo = clouds.FirstOrDefault(c => c.CloudStorageNum == CloudStorageNum);
            if (cloudInfo == null)
            {
                Console.WriteLine("❌ 클라우드 정보 없음");
                return false;
            }

            string cloudType = cloudInfo.CloudType;
            var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloudType));
            if (service == null)
            {
                Console.WriteLine($"❌ 지원되지 않는 클라우드");
                return false;
            }


            bool result = await service.DownloadFileAsync(CloudStorageNum, cloudFileId, savePath,userId);
            return result;
        }

  

        public async Task<bool> DownloadAndMergeFile(int logicalFileId, string finalsavePath, string userId,int CloudStorageNum)
        {
                  // 1. 논리 파일 정보 불러오기
            var logicalFile = fileRepo.GetFileById(logicalFileId);
            if (logicalFile == null || !logicalFile.IsDistributed)
            {
                Console.WriteLine("❌ 유효한 분산 파일이 아닙니다.");
                return false;
            }

                  // 2. 조각 목록 불러오기
            var chunks = fileRepo.GetChunksByRootFileId(logicalFileId)
                          .OrderBy(c => c.ChunkIndex)
                          .ToList();

            if (chunks.Count == 0)
            {
                Console.WriteLine("❌ 조각이 존재하지 않습니다.");
                return false;
            }


                 // 4. 병합 스트림 열기
            using FileStream output = new FileStream(finalsavePath, FileMode.Create, FileAccess.Write);

            foreach (var chunk in chunks)
            {
                // 클라우드 계정 정보 가져오기
                var account = storageRepository.GetCloud(chunk.CloudStorageNum, userId);
               
                if (account == null)
                {
                    Console.WriteLine($"❌ 계정 없음: CloudStorageNum {chunk.CloudStorageNum}");
                    return false;
                }

                //var chunkCloud = storageRepository.GetCloud(chunk.CloudStorageNum);

                //클라우드 서비스 선택 . 여기서 같은 클라우드 2개의 경우 , 첫번째것만 선택되어서 정확히 호출해야함.
                //원드라이브 두개 연결시, 분산저장된 파일 다운로드시에 첫번째 OneDrive계정만 선택되는 문제 해결.
                var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(account.CloudType));
                if (service == null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"❌ 클라우드 서비스 없음: {account.CloudType}");
                    return false;
                }

                        // 3. 조각 다운로드 → 임시 파일 경로 반환
                string tempPath = Path.GetTempFileName();
                bool success = await service.DownloadFileAsync(chunk.CloudStorageNum, chunk.CloudFileId, tempPath,userId);

                if (!success)
                {
                    Console.WriteLine($"❌ 조각 다운로드 실패: {chunk.FileName}");
                    return false;
                }

                        // 4. 조각 읽어서 병합
                byte[] data = await File.ReadAllBytesAsync(tempPath);
                await output.WriteAsync(data, 0, data.Length);

                File.Delete(tempPath);
            }

            Console.WriteLine($"✅ 다운로드 및 병합 완료: {finalsavePath}");
            return true;
        }





    }
}