using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using OverCloud.Services;
using OverCloud.Services.FileManager;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.Services.StorageManager;
using overcloud.Converters;
using overcloud.Windows;
using overcloud.transfer_manager;


namespace overcloud.Views
{
    public partial class SharedAccountView : System.Windows.Controls.UserControl
    {

        private LoginController _controller;

        private static TransferManagerWindow _transferWindow;

        private string _user_id;
        private string _currentAccountId; // 현재 선택된 계정 ID
        private FileSearchView _fileSearchView;

        // 탐색기 상태
        private int currentFolderId = -1;
        private bool isMoveMode = false;
        private int moveTargetFolderId = -2;
        private List<FileItemViewModel> moveCandidates = new();

        private bool _isFolderChanging = false;


        public SharedAccountView(LoginController controller,
            string user_id)

        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString(), "XAML 파싱 에러");
                throw;
            }
            Loaded += HomeView_Loaded;
            _controller = controller;
            _user_id = user_id;

            this.KeyDown += SharedAccountView_KeyDown;
            this.Focusable = true;
            this.Focus();

            _fileSearchView = new FileSearchView();
            _fileSearchView.SearchSubmitted += OnSearchKeywordSubmitted;
            SearchHost.Content = _fileSearchView;

            // 초기 서비스 설정

            // FileSearchView 생성 및 위치 지정
            _fileSearchView = new FileSearchView();
            _fileSearchView.SearchSubmitted += OnSearchKeywordSubmitted;
            SearchHost.Content = _fileSearchView;
        }


        private void HomeView_Loaded(object sender, RoutedEventArgs e)
        {
            LoadAccountTrees();
            RefreshExplorer();
        }


        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///탐색기 참조를 위한 클래스
        ///
        public class FileItemViewModel : INotifyPropertyChanged
        {
            public int FileId { get; set; }
            public string FileName { get; set; }
            public ulong FileSize { get; set; }

            public string ID { get; set; }
            public DateTime UploadedAt { get; set; }
            public int CloudStorageNum { get; set; }
            public int? ParentFolderId { get; set; }
            public bool IsFolder { get; set; }
            public int Count { get; set; }
            public string cloud_file_id { get; set; }
            public string GoogleFileId { get; set; }

            private bool _isChecked;
            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked != value)
                    {
                        _isChecked = value;
                        OnPropertyChanged(nameof(IsChecked));
                    }
                }
            }

            public string Icon => IsFolder ? "asset/folder.png" : "asset/file.png";

            public event PropertyChangedEventHandler PropertyChanged;

            private string _issueStatus;
            /// <summary>
            /// 파일에 매핑된 이슈가 있을 경우, 콤마(,)로 구분한 상태 문자열.
            /// 이슈가 없으면 null 또는 빈 문자열.
            /// </summary>
            public string IssueStatus
            {
                get => _issueStatus;
                set
                {
                    if (_issueStatus != value)
                    {
                        _issueStatus = value;
                        OnPropertyChanged(nameof(IssueStatus));
                    }
                }
            }

            protected void OnPropertyChanged(string name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }

            public bool IsDistributed { get; set; }

            public string IconText => IsFolder ? "📁" : "📄";

            private string _fullPath = string.Empty;
            public string FullPath
            {
                get => _fullPath;
                set
                {
                    if (_fullPath != value)
                    {
                        _fullPath = value;
                        OnPropertyChanged(nameof(FullPath));
                    }
                }
            }
        }

        //////변환기
        private FileItemViewModel ToViewModel(CloudFileInfo file)
        {
            return new FileItemViewModel
            {
                FileId = file.FileId,
                FileName = file.FileName,
                FileSize = file.FileSize,
                ID = file.ID,
                UploadedAt = file.UploadedAt,
                CloudStorageNum = file.CloudStorageNum,
                ParentFolderId = file.ParentFolderId,
                IsFolder = file.IsFolder,
                cloud_file_id = file.CloudFileId,
                IsChecked = false,
                IsDistributed = file.IsDistributed
            };
        }


        /// 뷰 클래스에서 받은 정보를 DB에 저장하기 위한 변환기
        private CloudFileInfo ToCloudFileInfo(FileItemViewModel vm)
        {
            return new CloudFileInfo
            {
                FileId = vm.FileId,
                FileName = vm.FileName,
                FileSize = vm.FileSize,
                ID = vm.ID,
                UploadedAt = vm.UploadedAt,
                CloudStorageNum = vm.CloudStorageNum,
                ParentFolderId = moveTargetFolderId, // 여기서만 목적지로 덮어씀
                IsFolder = vm.IsFolder,
                CloudFileId = vm.cloud_file_id,
                IsDistributed = vm.IsDistributed

            };
        }
        ////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///전송관리자 창
        private void ShowTransferWindow()
        {
            if (_transferWindow == null || !_transferWindow.IsVisible)
            {
                _transferWindow = new TransferManagerWindow();
                _transferWindow.Owner = System.Windows.Application.Current.MainWindow;
                _transferWindow.Show();
            }
            else
            {
                _transferWindow.Activate();
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////////


        private async void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            ShowTransferWindow();

            var choice = System.Windows.MessageBox.Show(
                "파일을 선택하려면 [예], 폴더를 선택하려면 [아니오]를 클릭하세요.",
                "선택 방식",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes)
            {
                // 파일 선택
                var fileDialog = new CommonOpenFileDialog
                {
                    IsFolderPicker = false,
                    Multiselect = false,
                    Title = "파일 선택"
                };

                if (fileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string filePath = fileDialog.FileName;
                    ulong fileSize = (ulong)new FileInfo(filePath).Length;

                    // 용량 체크 (Phase 4 4단계 — GET /api/quota/{userId}는 sub==userId 외에
                    // sub가 이 협업 계정(_currentAccountId)의 정당한 멤버인 경우도 허용하도록 넓혀둠)
                    ulong? totalRemainingByte = await OverCloudApiClient.GetRemainingQuotaBytesAsync(_currentAccountId);
                    if (totalRemainingByte == null)
                    {
                        System.Windows.MessageBox.Show("❌ 서버에서 용량 정보를 가져오지 못했습니다.");
                        return;
                    }
                    if (totalRemainingByte < fileSize)
                    {
                        System.Windows.MessageBox.Show("❌ 전체 클라우드 용량이 부족합니다.");
                        return;
                    }

                    // 전송 큐에 추가
                    App.TransferManager.UploadManager.EnqueueUploads(new List<(string FileName, string FilePath, int ParentFolderId)>
                    {
                        (Path.GetFileName(filePath), filePath, currentFolderId)
                    }, _currentAccountId);
                }
            }
            else if (choice == MessageBoxResult.No)
            {
                // 폴더 선택
                using var folderDialog = new FolderBrowserDialog
                {
                    Description = "폴더 선택",
                    RootFolder = Environment.SpecialFolder.MyComputer
                };

                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string rootPath = folderDialog.SelectedPath;

                    await CollectAllFilesFromFolder(rootPath, currentFolderId);  // 리스트 반환 X, 내부에서 큐 등록됨

                    LoadFolderContents(currentFolderId, _currentAccountId);
                    RefreshExplorer();
                }
            }
        }



        private async Task CollectAllFilesFromFolder(string folderPath, int parentFolderId)
        {
            // 1. DB에 폴더 등록
            var folderInfo = new CloudFileInfo
            {
                FileName = Path.GetFileName(folderPath),
                ParentFolderId = parentFolderId,
                IsFolder = true,
                UploadedAt = DateTime.Now,
                FileSize = 0,
                CloudStorageNum = -1,
                CloudFileId = string.Empty,
                ID = _currentAccountId
            };

            int newFolderId = _controller.FileRepository.add_folder(folderInfo);
            if (newFolderId == -1)
            {
                System.Windows.MessageBox.Show($"폴더 '{folderInfo.FileName}' 등록 실패");
                return;
            }

            // 2. 파일 수집 및 업로드 큐 등록
            foreach (var file in Directory.GetFiles(folderPath))
            {
                App.TransferManager.UploadManager.EnqueueUpload(new UploadTaskInfo
                {
                    LocalPath = file,
                    FolderId = newFolderId
                }, _currentAccountId);
            }

            // 3. 하위 폴더 재귀
            foreach (var dir in Directory.GetDirectories(folderPath))
            {
                await CollectAllFilesFromFolder(dir, newFolderId);
            }
        }


        /// //////////////////////////////////////////////////////////////////////////////////

        //_fileRepository._fileRepository.all_file_list

        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem parentItem &&
                parentItem.Tag is AccountFolderTag tag &&
                parentItem.Items.Count == 1 &&
                parentItem.Items[0] is string s &&
                s == "Loading...")
            {
                parentItem.Items.Clear();

                var children = _controller.FileRepository.all_file_list(tag.FolderId, tag.AccountId)
                                .Where(f => f.IsFolder)
                                .ToList();

                foreach (var child in children)
                {
                    var childItem = new TreeViewItem
                    {
                        Header = $"📁 {child.FileName}",
                        Tag = new AccountFolderTag(tag.AccountId, child.FileId)
                    };
                    childItem.Items.Add("Loading...");
                    childItem.Expanded += Folder_Expanded;
                    parentItem.Items.Add(childItem);
                }
            }
        }

        private void RefreshExplorer()
        {
            // 1) 현재 확장된 노드들의 ID를 수집
            var expandedIds = new HashSet<int>();
            CollectExpandedIds(FileExplorerTree.Items, expandedIds);

            // 2) 트리 클리어 & 루트 로드
            FileExplorerTree.Items.Clear();
            LoadAccountTrees();

            // 3) 저장된 ID에 해당하는 노드 다시 펼치기
            RestoreExpandedState(FileExplorerTree.Items, expandedIds);
        }

        // 재귀적으로 TreeViewItem에서 IsExpanded된 Tag(int) 수집
        private void CollectExpandedIds(ItemCollection items, HashSet<int> ids)
        {
            foreach (var obj in items.OfType<TreeViewItem>())
            {
                if (obj.Tag is AccountFolderTag tag)
                {
                    if (obj.IsExpanded)
                        ids.Add(tag.FolderId);

                    CollectExpandedIds(obj.Items, ids);
                }
            }
        }

        // 재귀적으로 ID가 있으면 다시 확장 및 자식 로드
        private void RestoreExpandedState(ItemCollection items, HashSet<int> ids)
        {
            foreach (var tvi in items.OfType<TreeViewItem>())
            {
                if (tvi.Tag is AccountFolderTag tag && ids.Contains(tag.FolderId))
                {
                    tvi.IsExpanded = true;

                    // “Loading…” 있으면 실제 하위 폴더로 교체
                    if (tvi.Items.Count == 1 && tvi.Items[0] is string s && s == "Loading...")
                    {
                        tvi.Items.Clear();
                        var children = _controller.FileRepository.all_file_list(tag.FolderId, tag.AccountId).Where(f => f.IsFolder);
                        foreach (var f in children)
                        {
                            var childTvi = new TreeViewItem
                            {
                                Header = f.FileName,
                                Tag = new AccountFolderTag(tag.AccountId, f.FileId)
                            };
                            childTvi.Items.Add("Loading...");
                            childTvi.Expanded += Folder_Expanded;
                            tvi.Items.Add(childTvi);
                        }
                    }

                    // 자식도 재귀 처리
                    RestoreExpandedState(tvi.Items, ids);
                }
            }
        }



        private void FileExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item && item.Tag is AccountFolderTag tag)
            {
                currentFolderId = tag.FolderId;
                _currentAccountId = tag.AccountId; // string으로 필드 선언 필요
                LoadFolderContents(currentFolderId, _currentAccountId);
            }
            Console.WriteLine("현재 폴더 위치 변경 : " + currentFolderId + ", 계정 : " + _currentAccountId);

            LoadUsageDetails(_currentAccountId);
        }

        private void LoadFolderContents(int folderId, string accountId)
        {
            var vms = _controller.FileRepository
                .all_file_list(folderId, accountId)
                .Select(f =>
                {
                    // 1) CloudFileInfo → VM 생성
                    var vm = ToViewModel(f);

                    // 2) 평소에는 빈 문자열
                    vm.FullPath = string.Empty;

                    // 3) 이슈 조회 & 상태 세팅
                    var issues = _controller.FileIssueRepository
                                   .GetIssuesByFileId(f.FileId);

                    if (issues != null && issues.Any())
                    {
                        var lowestStatus = issues
                            .Select(i => Enum.Parse<IssueStatusEnum>(i.Status))
                            .Min();
                        vm.IssueStatus = lowestStatus.ToString();
                    }
                    else
                    {
                        vm.IssueStatus = string.Empty;
                    }

                    return vm;
                })
                .ToList();

            RightFileListPanel.ItemsSource = vms;
            IssueColumnPanel.ItemsSource = vms;
            DateColumnPanel.ItemsSource = vms;
            PathColumnPanel.ItemsSource = vms;    // PathColumnPanel 이 있다면 빈 문자열만 바인딩
        }


        private record AccountFolderTag(string AccountId, int FolderId);


        private void LoadAccountTrees()
        {
            FileExplorerTree.Items.Clear();

            var accounts = _controller.CoopUserRepository.connected_cooperation_account_nums(_user_id);

            foreach (var accountId in accounts)
            {
                var accountRoot = new TreeViewItem
                {
                    Header = $"📁 {accountId}",
                    Tag = new AccountFolderTag(accountId, -1)
                };

                var rootChildren = _controller.FileRepository.all_file_list(-1, accountId)
                                     .Where(f => f.IsFolder)
                                     .ToList();

                foreach (var child in rootChildren)
                {
                    var childItem = new TreeViewItem
                    {
                        Header = $"📁 {child.FileName}",
                        Tag = new AccountFolderTag(accountId, child.FileId)
                    };
                    childItem.Items.Add("Loading...");
                    childItem.Expanded += Folder_Expanded;
                    accountRoot.Items.Add(childItem);
                }

                FileExplorerTree.Items.Add(accountRoot);
            }
        }



        private async void RightFileItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (_isFolderChanging) return;
            _isFolderChanging = true;

            try
            {
                if (sender is StackPanel panel && panel.DataContext != null)
                {
                    var fileInfo = panel.DataContext;

                    var info = panel.DataContext as FileItemViewModel;

                    // 안전한 null 확인
                    if (info == null || string.IsNullOrEmpty(info.FileName) || string.IsNullOrEmpty(info.Icon))
                        return;


                    if (info.Icon == "asset/folder.png")
                    {
                        var folder = _controller.FileRepository.all_file_list(currentFolderId, _currentAccountId)
                                     .FirstOrDefault(f => f.IsFolder && f.FileName == info.FileName);

                        if (folder != null)
                        {
                            currentFolderId = folder.FileId;
                            //LoadFolderContents(currentFolderId);
                            //SelectFolderInTree(folder.FileId);
                            await Task.Run(() =>
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    LoadFolderContents(currentFolderId, _currentAccountId);
                                    SelectFolderInTree(_currentAccountId, folder.FileId);

                                });
                            });
                        }
                    }
                }
            }
            finally
            {
                // 로딩이 완료되었든 실패했든 다시 클릭 허용
                _isFolderChanging = false;
            }
        }




        private void SelectFolderInTree(string accountId, int folderId)
        {
            foreach (var item in FileExplorerTree.Items)
            {
                if (item is TreeViewItem rootItem)
                {
                    if (SelectFolderInTreeRecursive(rootItem, accountId, folderId))
                        break;
                }
            }
        }


        private bool SelectFolderInTreeRecursive(TreeViewItem parent, string accountId, int folderId)
        {
            if (parent.Tag is AccountFolderTag tag)
            {
                if (tag.AccountId == accountId && tag.FolderId == folderId)
                {
                    parent.IsSelected = true;
                    parent.BringIntoView();
                    return true;
                }

                foreach (var childObj in parent.Items)
                {
                    if (childObj is TreeViewItem childItem)
                    {
                        if (childItem.Items.Count == 1 && childItem.Items[0] is string s && s == "Loading...")
                        {
                            // 동적 로드
                            if (childItem.Tag is AccountFolderTag childTag)
                            {
                                childItem.Items.Clear();

                                var children = _controller.FileRepository.all_file_list(childTag.FolderId, childTag.AccountId)
                                    .Where(f => f.IsFolder)
                                    .ToList();

                                foreach (var child in children)
                                {
                                    var newChild = new TreeViewItem
                                    {
                                        Header = $"📁 {child.FileName}",
                                        Tag = new AccountFolderTag(childTag.AccountId, child.FileId)
                                    };
                                    newChild.Items.Add("Loading...");
                                    newChild.Expanded += Folder_Expanded;
                                    childItem.Items.Add(newChild);
                                }
                            }
                        }

                        if (SelectFolderInTreeRecursive(childItem, accountId, folderId))
                            return true;
                    }
                }
            }
            return false;
        }

        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // 클릭이 상위 StackPanel로 가지 않게 막음
        }





        private List<FileItemViewModel> GetCheckedFiles()
        {
            if (RightFileListPanel.ItemsSource is IEnumerable<FileItemViewModel> items)
            {
                return items.Where(f => f.IsChecked).ToList();
            }
            return new List<FileItemViewModel>();
        }

        private async void Button_Down_Click(object sender, RoutedEventArgs e)
        {
            ShowTransferWindow();

            var selectedFiles = GetCheckedFiles();
            if (selectedFiles.Count == 0)
            {
                System.Windows.MessageBox.Show("선택된 항목이 없습니다.");
                return;
            }

            string localBase = "";
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "파일을 저장할 폴더를 선택하세요.";
                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    localBase = folderDialog.SelectedPath;
                else
                {
                    System.Windows.MessageBox.Show("저장할 폴더를 선택하지 않았습니다.");
                    return;
                }
            }

            var allMap = GetAllFilesFromCurrentFolder();

            try
            {
                // 1. 파일은 비동기 큐에 추가
                var enqueueList = selectedFiles
                    .Where(f => !f.IsFolder)
                    .Select(f => (
                        FileID: f.FileId,
                        FileName: f.FileName,
                        CloudFileId: f.cloud_file_id,
                        CloudStorageNum: f.CloudStorageNum,
                        LocalPath: Path.Combine(localBase, f.FileName),
                        IsDistributed: f.IsDistributed,
                        FileSize: f.FileSize
                    )).ToList();

                App.TransferManager.DownloadManager.EnqueueDownloads(enqueueList, _currentAccountId);

                // 2. 폴더는 기존 재귀 다운로드
                foreach (var item in selectedFiles.Where(f => f.IsFolder))
                {
                    await DownloadItemRecursive(item.FileId, localBase, allMap, item.IsDistributed);
                }

                System.Windows.MessageBox.Show("다운로드 요청 완료");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"다운로드 중 오류 발생: {ex.Message}");
            }
        }


        private async Task DownloadItemRecursive(int fileId, string localBase, Dictionary<int, CloudFileInfo> current_file_map, bool _IsDistributed)
        {
            if (!current_file_map.TryGetValue(fileId, out var file)) return;

            string cloudPath = GetCloudPath(file, current_file_map);
            string localPath = Path.Combine(localBase, cloudPath);

            if (file.IsFolder)
            {
                Directory.CreateDirectory(localPath);

                var children = _controller.FileRepository.all_file_list(file.FileId, file.ID); // 이 폴더의 하위 항목
                foreach (var child in children)
                {
                    DownloadItemRecursive(child.FileId, localBase, current_file_map, child.IsDistributed);
                }
            }
            else
            {
                string? dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                App.TransferManager.DownloadManager.EnqueueDownloads(new List<(int FileId, string FileName, string CloudFileId, int CloudStorageNum, string LocalPath, bool IsDistributed, ulong FileSize)>
                    {
                        (fileId, file.FileName, file.CloudFileId, file.CloudStorageNum, localPath, _IsDistributed, file.FileSize)
                    }, _currentAccountId);
            }
        }


        private string GetCloudPath(CloudFileInfo file, Dictionary<int, CloudFileInfo> allMap)
        {
            var parts = new List<string> { file.FileName };
            var current = file;
            while (current.ParentFolderId != null && allMap.TryGetValue(current.ParentFolderId, out var parent))
            {
                parts.Insert(0, parent.FileName);
                current = parent;
            }
            return Path.Combine(parts.ToArray());
        }

        private Dictionary<int, CloudFileInfo> GetAllFilesFromCurrentFolder()
        {
            var result = new Dictionary<int, CloudFileInfo>();

            void Traverse(int parentId)
            {
                var children = _controller.FileRepository.all_file_list(parentId, _currentAccountId);
                foreach (var file in children)
                {
                    result[file.FileId] = file;
                    if (file.IsFolder)
                        Traverse(file.FileId);
                }
            }

            Traverse(currentFolderId); // 현재 보고 있는 폴더부터 시작
            return result;
        }


        ////////////////////////////////////////////////////////////////////////////////////////////////////////
        ////삭제 버튼 클릭 시

        private async void Button_DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var checkedItems = GetCheckedFiles();
            if (checkedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("선택된 항목이 없습니다.");
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                $"총 {checkedItems.Count}개의 항목을 삭제하시겠습니까?",
                "삭제 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            var allFileMap = GetAllFilesFromCurrentFolder(); // 현재 폴더 기준 전체 파일 맵

            foreach (var item in checkedItems)
            {
                await DeleteItemRecursive(item.FileId, allFileMap);
            }

            // UI 갱신
            LoadFolderContents(currentFolderId, _currentAccountId);
            RefreshExplorer();
        }


        private async Task DeleteItemRecursive(int fileId, Dictionary<int, CloudFileInfo> allFileMap)
        {
            if (!allFileMap.TryGetValue(fileId, out var file)) return;

            // 1. 폴더인 경우 자식 먼저 삭제
            if (file.IsFolder)
            {
                var children = _controller.FileRepository.all_file_list(file.FileId, file.ID);
                foreach (var child in children)
                {
                    await DeleteItemRecursive(child.FileId, allFileMap);
                }
            }

            // 2. 마지막에 자기 자신 삭제 (파일이든 폴더든)
            bool deleted;
            if (file.IsDistributed)
            {
                deleted = await _controller.FileDeleteManager.Delete_DistributedFile(file.FileId, _currentAccountId);
            }
            else
            {
                deleted = await _controller.FileDeleteManager.Delete_File(file.CloudStorageNum, file.FileId, _currentAccountId);
            }

            if (!deleted)
            {
                System.Windows.MessageBox.Show($"{file.FileName} 삭제 실패");
            }
        }



        ///////////////////////////////////////////////////////////////////////////////////////////////////////
        ///


        //////////////////////////////////////////////////////////////////////////////////////////////////////
        ///이동 버튼 클릭 시
        /*
        private void Button_Move_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetCheckedFiles();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show("이동할 항목을 선택하세요.");
                return;
            }

            var dialog = new FolderSelectDialog(_fileRepository)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                int targetFolderId = dialog.SelectedFolderId.Value;

                foreach (var item in selected)
                {
                    var cloudInfo = ToCloudFileInfo(item);
                    cloudInfo.ParentFolderId = targetFolderId;
                    _fileRepository.change_dir(cloudInfo);
                }

                LoadFolderContents(currentFolderId, _currentAccountId);
                RefreshExplorer();

                System.Windows.MessageBox.Show("이동이 완료되었습니다.");
            }
        }*/



        ///////////////////////////////////////////////////////////////////////////////////////////////////////
        ///폴더 추가
        private void Button_AddFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentAccountId))
            {
                System.Windows.MessageBox.Show("협업 클라우드를 먼저 선택해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 다이얼로그 띄우기
            var dlg = new AddFolderDialog();
            // this가 아니라 이 UserControl을 포함하고 있는 Window를 Owner로 지정
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() != true)
                return;

            // 입력한 이름으로 CloudFileInfo 생성
            var info = new CloudFileInfo
            {
                FileName = dlg.FolderName,
                ParentFolderId = currentFolderId,
                IsFolder = true,
                UploadedAt = DateTime.Now,
                FileSize = 0,
                CloudStorageNum = -1,
                CloudFileId = string.Empty,
                ID = _currentAccountId
            };

            // DB에 삽입
            int result;

            try
            {
                result = _controller.FileRepository.add_folder(info);
                //result = null;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"폴더 추가 중 오류: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (result == -1)

            {
                System.Windows.MessageBox.Show("폴더 추가에 실패했습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // UI 갱신
            LoadFolderContents(currentFolderId, _currentAccountId);
            RefreshExplorer();


        }


        ////////////////////////////////////////////////////////////////////////////////////////
        ///복사 코드
        ///
        /*
        private async void Button_Copy_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetCheckedFiles();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show("복사할 항목을 선택하세요.");
                return;
            }

            var dialog = new FolderSelectDialog(_fileRepository)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                int targetFolderId = dialog.SelectedFolderId.Value;

                foreach (var item in selected)
                {
                    bool result = await _fileCopyManager.Copy_File(item.FileId, targetFolderId, _user_id);
                    if (!result)
                    {
                        System.Windows.MessageBox.Show($"파일/폴더 '{item.FileName}' 복사 실패");
                    }
                }

                LoadFolderContents(currentFolderId, _currentAccountId);
                RefreshExplorer();

                System.Windows.MessageBox.Show("복사가 완료되었습니다.");
            }
        }

        private async Task<bool> CopyFolderRecursive(int sourceFolderId, int targetParentFolderId)
        {
            var folderInfo = _fileRepository.specific_file_info(sourceFolderId);
            if (folderInfo == null || !folderInfo.IsFolder)
                return false;

            // 1. 현재 폴더를 targetParentFolderId 아래 새로 추가
            var newFolderInfo = new CloudFileInfo
            {
                FileName = folderInfo.FileName,
                ParentFolderId = targetParentFolderId,
                IsFolder = true,
                UploadedAt = DateTime.Now,
                FileSize = 0,
                CloudStorageNum = -1,
                CloudFileId = string.Empty,
                ID = _user_id
            };

            int newFolderId = _fileRepository.add_folder(newFolderInfo);
            if (newFolderId == -1)
            {
                System.Windows.MessageBox.Show($"폴더 '{newFolderInfo.FileName}' 복사 실패");
                return false;
            }

            // 2. 하위 항목 재귀 복사
            var children = _fileRepository.all_file_list(sourceFolderId, _currentAccountId);
            foreach (var child in children)
            {
                if (child.IsFolder)
                {
                    // 하위 폴더면 재귀 호출
                    await CopyFolderRecursive(child.FileId, newFolderId);
                }
                else
                {
                    // 파일이면 파일 복사 (Copy_File 호출)
                    await _fileCopyManager.Copy_File(child.FileId, newFolderId, _user_id);
                }
            }

            return true;
        }
        */

        private void CreateCooperationAccount_Click(object sender, RoutedEventArgs e)
        {
            var registerWindow = new COP_RegisterWindow(_controller, _user_id);
            registerWindow.Owner = Window.GetWindow(this); // 모달창으로 띄우기
            registerWindow.ShowDialog();

            // 협업 계정 생성 후 트리 새로고침 필요할 경우
            RefreshExplorer(); // 또는 LoadAccountTrees() 등
        }

        private void JoinCooperationAccount_Click(object sender, RoutedEventArgs e)
        {
            var registerWindow = new COP_JoinWindow(_controller, _user_id);
            registerWindow.Owner = Window.GetWindow(this); // 모달창으로 띄우기
            registerWindow.ShowDialog();

            // 협업 계정 생성 후 트리 새로고침 필요할 경우
            RefreshExplorer(); // 또는 LoadAccountTrees() 등
        }

        private void DisJoinCooperationAccount_Click(object sender, RoutedEventArgs e)
        {
            var registerWindow = new COP_Dis_JoinWindow(_controller, _user_id);
            registerWindow.Owner = Window.GetWindow(this); // 모달창으로 띄우기
            registerWindow.ShowDialog();

            // 협업 계정 생성 후 트리 새로고침 필요할 경우
            RefreshExplorer(); // 또는 LoadAccountTrees() 등
        }

        private void Button_GenerateLink_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetCheckedFiles();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show("파일 또는 폴더를 선택해주세요.");
                return;
            }

            List<string> linkParts = new();

            foreach (var item in selected)
            {
                linkParts.Add($"{_user_id},{item.cloud_file_id},{item.FileId}");
            }

            string fullLink = string.Join("|", linkParts);
            string url = $"http://capstonedesign.duckdns.org/download/?link={Uri.EscapeDataString(fullLink)}";

            System.Windows.Clipboard.SetText(url);

            var alert = new AcrylicAlertWindow($"링크가 복사되었습니다:\n{url}")
            {
                Owner = Window.GetWindow(this)   // 모달로 띄우려면 Owner 지정
            };
            alert.ShowDialog();

            //System.Windows.MessageBox.Show("링크가 복사되었습니다:\n" + url);
        }

        private void Button_DownloadLink_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new DownloadFromLinkWindow(_currentAccountId, _controller)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }

        private void CurrentCooperationAccountsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new CooperationListWindow(_controller, _user_id);
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();
        }

        private void Button_CreateIssue_Click(object sender, RoutedEventArgs e)
        {
            var selectedFiles = GetCheckedFiles();
            if (selectedFiles.Count == 0)
            {
                System.Windows.MessageBox.Show("이슈를 생성할 파일을 선택하세요.");
                return;
            }

            // 현재 협업 계정 ID 사용
            string coopId = _currentAccountId;
            List<string> userList = _controller.CoopUserRepository.GetUsersByCoopId(coopId);

            var issueDialog = new AddIssueDialog(userList)
            {
                Owner = Window.GetWindow(this)
            };

            if (issueDialog.ShowDialog() == true)
            {
                string title = issueDialog.IssueTitle;
                string description = issueDialog.IssueDescription;
                string assignedTo = string.IsNullOrWhiteSpace(issueDialog.AssignedTo) ? null : issueDialog.AssignedTo;
                DateTime? dueDate = issueDialog.DueDate;

                // 이슈 객체 1개 생성 (파일 여러 개에 대해 동일 이슈 등록)
                var newIssue = new FileIssueInfo
                {
                    ID = coopId,   // 협업 클라우드 ID
                    Title = title,
                    Description = description,
                    CreatedBy = _user_id,
                    AssignedTo = assignedTo,
                    Status = "OPEN",
                    CreatedAt = DateTime.Now,
                    DueDate = dueDate
                };

                // 이슈 등록
                int issueId = _controller.FileIssueRepository.AddIssue(newIssue);

                if (issueId == -1)
                {
                    System.Windows.MessageBox.Show("이슈 등록 실패");
                    return;
                }

                // 선택된 모든 파일에 대해 매핑 등록
                foreach (var file in selectedFiles)
                {
                    bool mappingResult = _controller.FileIssueMappingRepository.AddMapping(issueId, file.FileId);
                    if (!mappingResult)
                    {
                        System.Windows.MessageBox.Show($"파일 '{file.FileName}' 매핑 실패");
                    }
                }

                System.Windows.MessageBox.Show("이슈 등록 성공");
            }
        }


        private void SharedAccountView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                // 현재 폴더 내용 새로고침
                LoadFolderContents(currentFolderId, _currentAccountId);
            }
        }


        private async void Button_transfer_show(object sender, RoutedEventArgs e)
        {
            ShowTransferWindow();
        }

        public enum IssueStatusEnum
        {
            OPEN = 0,
            IN_PROGRESS = 1,
            RESOLVED = 2,
            CLOSED = 3
        }

        private void OnSearchKeywordSubmitted(string keyword)
        {
            var results = _controller.FileRepository.FindByFileName(keyword, _currentAccountId);

            var viewModels = results.Select(f =>
            {
                var vm = ToViewModel(f);
                vm.FullPath = _controller.FileRepository.GetFullPath(f.FileId);
                return vm;
            }).ToList();

            RightFileListPanel.ItemsSource = viewModels;
            IssueColumnPanel.ItemsSource = viewModels;
            DateColumnPanel.ItemsSource = viewModels;
            PathColumnPanel.ItemsSource = viewModels;
        }

        private void LoadUsageDetails(string coopId)
        {
            // 기존 그룹화 로직에서 총합 계산
            var clouds = _controller.AccountService.Get_Clouds_For_User(coopId);

            double totalGB = clouds.Sum(c => (double)c.TotalCapacity) / 1024 / 1024;
            double usedGB = clouds.Sum(c => (double)c.UsedCapacity) / 1024 / 1024;
            int percent = totalGB > 0 ? (int)(usedGB * 100.0 / totalGB) : 0;

            // ProgressBar & 텍스트 설정
            TotalUsageBar.Value = percent;
            TotalUsageText.Text = $"{usedGB:F2}GB / {totalGB:F2}GB ({percent}%)";
        }



    }

}
