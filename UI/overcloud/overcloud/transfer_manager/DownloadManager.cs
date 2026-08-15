using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OverCloud.Services;
using OverCloud.Services.FileManager;
using OverCloud.transfer_manager;
using overcloud.CloudApi;

namespace overcloud.transfer_manager
{
    public class DownloadManager
    {
        private readonly ObservableCollection<TransferItemViewModel> _downloads = new();
        private readonly BlockingCollection<(TransferItemViewModel Item, DownloadTaskInfo Task)> _queue = new();
        private readonly SemaphoreSlim _semaphore = new(2);
        private readonly FileDownloadManager _fileDownloadManager;

        public ObservableCollection<TransferItemViewModel> Downloads => _downloads;

        public DownloadManager(FileDownloadManager fileDownloadManager)
        {
            _fileDownloadManager = fileDownloadManager;

            Task.Run(ProcessQueue);
        }

        // ✅ 기존 EnqueueDownloads 시그니처 유지
        public void EnqueueDownloads(List<(int FileID, string FileName, string CloudFileId, int CloudStorageNum, string LocalPath, bool IsDistributed, ulong FileSize)> files, string user_id)
        {
            foreach (var file in files)
            {
                var item = new TransferItemViewModel
                {
                    FileName = file.FileName,
                    Status = "대기 중",
                    Progress = 0,
                    LocalPath = file.LocalPath
                };

                System.Windows.Application.Current.Dispatcher.Invoke(() => _downloads.Add(item));

                // ✅ 내부에서 DownloadTaskInfo로 변환해서 큐에 삽입
                var taskInfo = new DownloadTaskInfo(
                    file.FileID, file.FileName, file.CloudFileId, file.CloudStorageNum, file.LocalPath, file.IsDistributed, user_id, file.FileSize
                );

                _queue.Add((item, taskInfo));
            }
        }

        private async Task ProcessQueue()
        {
            foreach (var (item, task) in _queue.GetConsumingEnumerable())
            {
                await _semaphore.WaitAsync();
                _ = ProcessDownload(item, task);
            }
        }

        private async Task ProcessDownload(TransferItemViewModel item, DownloadTaskInfo file)
        {
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => { 
                    item.Status = "다운로드 중";
                    // 파일 크기로 예상 업로드 시간 계산 (20MB/sec 기준)

                    ulong fileSizeBytes = file.FileSize;
                    double fileSizeMB = fileSizeBytes / (1024.0 * 1024.0);
                    double expectedSeconds = Math.Max(3, fileSizeMB / 10.0); // 최소 3초 보장
                    item.StartFakeProgress(expectedSeconds);
                });

                // Phase 4 3단계(5.1) — 분산 저장 다운로드는 아직 미이관이라 기존 DB 직접 접속 경로 그대로.
                // 단일 파일은 /api/files/{fileId}/location으로 위치를 받아 클라이언트가 클라우드 API를 직접 호출한다.
                if (file.IsDistributed)
                    await _fileDownloadManager.DownloadAndMergeFile(file.FileID, file.LocalPath, file.UserId, file.CloudStorageNum);
                else
                    await DownloadViaCloudApiAsync(file);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {  
                    item.CompleteDownload();
                    App.TransferManager.Completed.Add(item);
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => { item.Status = "오류: " + ex.Message; });
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // location으로 소유권이 재확인된 최신 cloudStorageNum/cloudFileId/cloudType을 받아
        // 클라우드 API에서 직접 바이트를 받는다(5.1 — 서버는 바이트를 중계하지 않는다).
        private static async Task DownloadViaCloudApiAsync(DownloadTaskInfo file)
        {
            var location = await OverCloudApiClient.GetFileLocationAsync(file.FileID);
            if (location == null)
            {
                Console.WriteLine($"❌ 파일 위치 조회 실패: fileId {file.FileID}");
                return;
            }

            string provider = location.CloudType switch
            {
                "GoogleDrive" => "google",
                "OneDrive" => "onedrive",
                _ => null
            };
            if (provider == null)
            {
                Console.WriteLine($"❌ 지원되지 않는 클라우드: {location.CloudType}");
                return;
            }

            var accessToken = await OverCloudApiClient.GetOAuthAccessTokenAsync(provider, location.CloudStorageNum);
            if (string.IsNullOrEmpty(accessToken))
                return;

            bool ok = provider == "google"
                ? await GoogleDriveTokenClient.DownloadAsync(accessToken, location.CloudFileId, file.LocalPath)
                : await OneDriveTokenClient.DownloadAsync(accessToken, location.CloudFileId, file.LocalPath);

            if (!ok)
                Console.WriteLine($"❌ 다운로드 실패: fileId {file.FileID}");
        }
    }

    // ✅ 내부 전용 TaskInfo 정의
    public record DownloadTaskInfo(
        int FileID, string FileName, string CloudFileId, int CloudStorageNum,
        string LocalPath, bool IsDistributed, string UserId, ulong FileSize);
}
