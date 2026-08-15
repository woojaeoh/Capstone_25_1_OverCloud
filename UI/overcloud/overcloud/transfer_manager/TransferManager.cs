using System.Collections.ObjectModel;
using overcloud.transfer_manager;
using overcloud.Views;
using OverCloud.Services;
using OverCloud.Services.FileManager;

namespace OverCloud.transfer_manager
{
    public class TransferManager
    {
        public DownloadManager DownloadManager { get; }
        public UploadManager UploadManager { get; }
        public ObservableCollection<TransferItemViewModel> Completed { get; }

        public TransferManager(FileUploadManager fileUploadManager, FileDownloadManager fileDownloadManager)
        {
            DownloadManager = new DownloadManager(fileDownloadManager);
            UploadManager = new UploadManager(fileUploadManager);
            Completed = new ObservableCollection<TransferItemViewModel>();
        }
    }
}
