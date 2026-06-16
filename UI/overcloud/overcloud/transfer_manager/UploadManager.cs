using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OverCloud.Services;
using overcloud.transfer_manager;
using OverCloud.Services.FileManager;
using overcloud;

namespace OverCloud.transfer_manager
{
    public class UploadManager
    {
        private readonly ObservableCollection<TransferItemViewModel> _uploads = new();
        private readonly BlockingCollection<(TransferItemViewModel Item, UploadTaskInfo Task, string UserId)> _queue = new();
        private readonly SemaphoreSlim _semaphore = new(2);
        private readonly FileUploadManager _fileUploadManager;
        private readonly CloudTierManager _cloudTierManager;

        public ObservableCollection<TransferItemViewModel> Uploads => _uploads;

        public UploadManager(FileUploadManager fileUploadManager, CloudTierManager cloudTierManager)
        {
            _fileUploadManager = fileUploadManager;
            _cloudTierManager = cloudTierManager;

            Task.Run(ProcessQueue);
        }

        // ✅ 시그니처 유지, 클래스 수정 안함
        public void EnqueueUploads(List<(string FileName, string FilePath, int ParentFolderId)> files, string user_id)
        {
            foreach (var file in files)
            {
                var item = new TransferItemViewModel
                {
                    FileName = file.FileName,
                    Status = "대기 중",
                    Progress = 0,
                    LocalPath = file.FilePath
                };

                System.Windows.Application.Current.Dispatcher.Invoke(() => _uploads.Add(item));

                var taskInfo = new UploadTaskInfo
                {
                    FileName = file.FileName,
                    LocalPath = file.FilePath,
                    FolderId = file.ParentFolderId
                };

                _queue.Add((item, taskInfo, user_id));
            }
        }

        private async Task ProcessQueue()
        {
            foreach (var (item, task, userId) in _queue.GetConsumingEnumerable())
            {
                await _semaphore.WaitAsync();
                _ = ProcessUpload(item, task, userId);
            }
        }

        private static long ManagedHeapMB() =>
            GC.GetTotalMemory(false) / 1024 / 1024;

        private static long WorkingSetMB() =>
            System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;

        private async Task ProcessUpload(TransferItemViewModel item, UploadTaskInfo file, string userId)
        {
            try
            {
                long heapBefore = ManagedHeapMB();
                long wsBefore   = WorkingSetMB();
                Console.WriteLine($"[MEM] [{file.FileName}] 시작 — 힙:{heapBefore}MB  WorkingSet:{wsBefore}MB");

                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    item.Status = "업로드 중";
                    ulong fileSizeBytes = (ulong)new FileInfo(file.LocalPath).Length;
                    double fileSizeMB = fileSizeBytes / (1024.0 * 1024.0);
                    double expectedSeconds = Math.Max(3, fileSizeMB / 10.0);
                    item.StartFakeProgress(expectedSeconds);
                });

                ulong fileSize = (ulong)new FileInfo(file.LocalPath).Length;
                var bestStorage = _cloudTierManager.SelectBestStorage(fileSize / 1024, userId);

                bool result = bestStorage != null
                    ? await _fileUploadManager.file_upload(file.LocalPath, file.FolderId, userId)
                    : await _fileUploadManager.Upload_Distributed(file.LocalPath, file.FolderId, userId);

                Console.WriteLine($"[MEM] [{file.FileName}] 완료 — 힙:{ManagedHeapMB()}MB  WorkingSet:{WorkingSetMB()}MB  (업로드 전 대비 힙 증가: {ManagedHeapMB() - heapBefore}MB)");

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    item.Status = result ? "완료" : "실패";
                    item.Progress = result ? 100 : 0;
                    if (result)
                    {
                        item.CompleteUpload();
                        App.TransferManager.Completed.Add(item);
                    }
                    else
                    {
                        item.Status = "실패";
                        item.Progress = 0;
                    }
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

        public void EnqueueUpload(UploadTaskInfo task, string user_id)
        {
            EnqueueUploads(new List<(string FileName, string FilePath, int ParentFolderId)>
            {
                (Path.GetFileName(task.LocalPath), task.LocalPath, task.FolderId)
            }, user_id);
        }


    }
}
