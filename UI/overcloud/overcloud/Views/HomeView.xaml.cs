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
using System.Windows.Media.Imaging;


namespace overcloud.Views
{
    public partial class HomeView : System.Windows.Controls.UserControl
    {

        private LoginController _controller;

        private static TransferManagerWindow _transferWindow;

        private string _user_id;
        private FileSearchView _fileSearchView;


        // 탐색기 상태
        private int currentFolderId = -1;
        private bool isMoveMode = false;
        private int moveTargetFolderId = -2;
        private List<FileItemViewModel> moveCandidates = new();

        private bool _isFolderChanging = false;

        public HomeView(LoginController controller,
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

            this.KeyDown += HomeView_KeyDown;
            this.Focusable = true;
            this.Focus();

            // 초기 서비스 설정

            // FileSearchView 생성 및 위치 지정
            _fileSearchView = new FileSearchView();
            _fileSearchView.SearchSubmitted += OnSearchKeywordSubmitted;
            SearchHost.Content = _fileSearchView;

            LoadUsageDetails(_user_id);
        }


        private void HomeView_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRootFolders();
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
                UploadedAt = vm.UploadedAt,
                CloudStorageNum = vm.CloudStorageNum,
                ParentFolderId = moveTargetFolderId, // 여기서만 목적지로 덮어씀
                IsFolder = vm.IsFolder,
                CloudFileId = vm.cloud_file_id

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

                    // 용량 체크
                    ulong totalRemainingByte = _controller.CloudTierManager.GetTotalRemainingQuotaInBytes(_user_id);
                    if (totalRemainingByte < fileSize)
                    {
                        System.Windows.MessageBox.Show("❌ 전체 클라우드 용량이 부족합니다.");
                        return;
                    }

                    // 전송 큐에 추가
                    App.TransferManager.UploadManager.EnqueueUploads(new List<(string FileName, string FilePath, int ParentFolderId)>
                    {
                        (Path.GetFileName(filePath), filePath, currentFolderId)
                    }, _user_id);
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

                    LoadFolderContents(currentFolderId);
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
                ID = _user_id
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
                }, _user_id);
            }

            // 3. 하위 폴더 재귀
            foreach (var dir in Directory.GetDirectories(folderPath))
            {
                await CollectAllFilesFromFolder(dir, newFolderId);
            }
        }


        /// //////////////////////////////////////////////////////////////////////////////////

        //_fileRepository._fileRepository.all_file_list

        private void LoadRootFolders()
        {
            // "모든 파일" 루트 노드
            var rootItem = new TreeViewItem
            {
                Header = "📁 Home",
                Tag = -1
            };

            // 바로 하위 폴더만 조회해서 추가
            var rootChildren = _controller.FileRepository.all_file_list_full(-1, _user_id)
                                 .Where(f => f.IsFolder)
                                 .ToList();

            foreach (var child in rootChildren)
            {
                var childItem = new TreeViewItem
                {
                    Header = $"📁 {child.FileName}",
                    Tag = child.FileId
                };
                childItem.Items.Add("Loading..."); // 하위 폴더 열 때만 로드
                childItem.Expanded += Folder_Expanded;
                rootItem.Items.Add(childItem);
            }

            FileExplorerTree.Items.Add(rootItem);
        }

        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem parentItem)
            {
                if (parentItem.Items.Count == 1 && parentItem.Items[0] is string && (string)parentItem.Items[0] == "Loading...")
                {
                    parentItem.Items.Clear();

                    int parentId = (int)parentItem.Tag;

                    var children = _controller.FileRepository.all_file_list_full(parentId, _user_id)
                                    .Where(f => f.IsFolder)
                                    .ToList();

                    foreach (var child in children)
                    {
                        var childItem = new TreeViewItem
                        {
                            Header = $"📁 {child.FileName}",
                            Tag = child.FileId
                        };
                        // StackPanel로 아이콘과 텍스트를 구성
                        //var headerPanel = new StackPanel
                        //{
                        //    Orientation = System.Windows.Controls.Orientation.Horizontal
                        //};

                        //// 📂 이미지 아이콘 (예: Images/folder.png)
                        //var image = new System.Windows.Controls.Image
                        //{
                        //    Source = new BitmapImage(new Uri("pack://application:,,,/asset/folder.png")),
                        //    Width = 16,
                        //    Height = 16,
                        //    Margin = new Thickness(0, 0, 5, 0),
                        //    VerticalAlignment = VerticalAlignment.Center
                        //};

                        //var icon = new TextBlock
                        //{
                        //    Text = "📁 ",
                        //    VerticalAlignment = VerticalAlignment.Center
                        //};

                        //// 파일 이름
                        //var text = new TextBlock
                        //{
                        //    Text = child.FileName,
                        //    VerticalAlignment = VerticalAlignment.Center
                        //};

                        //headerPanel.Children.Add(icon);
                        //headerPanel.Children.Add(text);

                        //childItem.Header = headerPanel;

                        childItem.Items.Add("Loading..."); // 또 하위가 있을 수 있으니
                        childItem.Expanded += Folder_Expanded;
                        parentItem.Items.Add(childItem);
                    }
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
            LoadRootFolders();

            // 3) 저장된 ID에 해당하는 노드 다시 펼치기
            RestoreExpandedState(FileExplorerTree.Items, expandedIds);
        }

        // 재귀적으로 TreeViewItem에서 IsExpanded된 Tag(int) 수집
        private void CollectExpandedIds(ItemCollection items, HashSet<int> ids)
        {
            foreach (var obj in items.OfType<TreeViewItem>())
            {
                int id = (int)obj.Tag;
                if (obj.IsExpanded) ids.Add(id);
                CollectExpandedIds(obj.Items, ids);
            }
        }

        // 재귀적으로 ID가 있으면 다시 확장 및 자식 로드
        private void RestoreExpandedState(ItemCollection items, HashSet<int> ids)
        {
            foreach (var tvi in items.OfType<TreeViewItem>())
            {
                int id = (int)tvi.Tag;
                if (ids.Contains(id))
                {
                    tvi.IsExpanded = true;

                    // “Loading…” 있으면 실제 하위 폴더로 교체
                    if (tvi.Items.Count == 1 && tvi.Items[0] is string s && s == "Loading...")
                    {
                        tvi.Items.Clear();
                        var children = _controller.FileRepository.all_file_list_full(id, _user_id).Where(f => f.IsFolder);
                        foreach (var f in children)
                        {
                            var childTvi = new TreeViewItem
                            {
                                Header = f.FileName,
                                Tag = f.FileId
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
            if (e.NewValue is TreeViewItem item && item.Tag is int folderId)
            {
                currentFolderId = folderId;
                LoadFolderContents(currentFolderId);
                if (isMoveMode)
                {
                    moveTargetFolderId = folderId;
                }
            }
            Console.WriteLine("현제 폴더 위치 변경 : " + currentFolderId);
        }

        private void LoadFolderContents(int folderId)
        {
            var contents = _controller.FileRepository
                .all_file_list_full(folderId, _user_id)
                .Select(f =>
                {
                    var vm = ToViewModel(f);
                    vm.FullPath = string.Empty;                    // 평소에는 빈 문자열
                    return vm;
                })
                .ToList();

            RightFileListPanel.ItemsSource = contents;
            DateColumnPanel.ItemsSource = contents;
            PathColumnPanel.ItemsSource = contents;          // 3열 패널에도 바인딩
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
                        var folder = _controller.FileRepository.all_file_list_full(currentFolderId, _user_id)
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
                                    LoadFolderContents(currentFolderId);
                                    SelectFolderInTree(folder.FileId);
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




        private void SelectFolderInTree(int folderId)
        {
            foreach (var item in FileExplorerTree.Items)
            {
                if (item is TreeViewItem rootItem)
                {
                    if (SelectFolderInTreeRecursive(rootItem, folderId))
                        break;
                }
            }
        }

        private bool SelectFolderInTreeRecursive(TreeViewItem parent, int folderId)
        {
            if (parent.Tag is int id && id == folderId)
            {
                parent.IsSelected = true;
                parent.BringIntoView();
                return true;
            }

            foreach (var childObj in parent.Items)
            {
                if (childObj is TreeViewItem childItem)
                {
                    // 하위 항목이 "Loading..."이고 아직 로드되지 않은 경우만 처리
                    if (childItem.Items.Count == 1 && childItem.Items[0] is string s && s == "Loading...")
                    {
                        // 여기서 childItem.Tag 기준으로 로드해야 함!
                        if (childItem.Tag is int childId)
                        {
                            childItem.Items.Clear();

                            // ⚠️ 하위 항목을 중복해서 추가하지 않도록 체크
                            var children = _controller.FileRepository.all_file_list_full(childId, _user_id)
                                           .Where(f => f.IsFolder && f.FileId != childId) // 자기 자신은 제외
                                           .ToList();

                            foreach (var child in children)
                            {
                                // 중복 방지: 같은 FileId의 항목이 이미 존재하면 추가하지 않음
                                bool alreadyExists = childItem.Items.OfType<TreeViewItem>()
                                                      .Any(x => x.Tag is int tag && tag == child.FileId);
                                if (alreadyExists) continue;

                                var newChild = new TreeViewItem
                                {
                                    Header = child.FileName,
                                    Tag = child.FileId
                                };
                                newChild.Items.Add("Loading...");
                                newChild.Expanded += Folder_Expanded;
                                childItem.Items.Add(newChild);
                            }
                        }
                    }

                    if (SelectFolderInTreeRecursive(childItem, folderId))
                        return true;
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

                App.TransferManager.DownloadManager.EnqueueDownloads(enqueueList, _user_id);

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

                var children = _controller.FileRepository.all_file_list_full(file.FileId, file.ID); // 이 폴더의 하위 항목
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

                App.TransferManager.DownloadManager.EnqueueDownloads(new List<(int FileId, string FileName, string CloudFileId, int CloudStorageNum, string LocalPath, bool IsDistributed,ulong FileSize)>
                    {
                        (fileId ,file.FileName, file.CloudFileId, file.CloudStorageNum, localPath, _IsDistributed, file.FileSize)
                    }, _user_id);
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
                var children = _controller.FileRepository.all_file_list_full(parentId, _user_id);
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
            LoadFolderContents(currentFolderId);
            RefreshExplorer();
        }


        private async Task DeleteItemRecursive(int fileId, Dictionary<int, CloudFileInfo> allFileMap)
        {
            if (!allFileMap.TryGetValue(fileId, out var file)) return;

            // 1. 폴더인 경우 자식 먼저 삭제
            if (file.IsFolder)
            {
                var children = _controller.FileRepository.all_file_list_full(file.FileId, file.ID);
                foreach (var child in children)
                {
                    await DeleteItemRecursive(child.FileId, allFileMap);
                }
            }

            // 2. 마지막에 자기 자신 삭제 (파일이든 폴더든)
            bool deleted;
            if (file.IsDistributed)
            {
                deleted = await _controller.FileDeleteManager.Delete_DistributedFile(file.FileId, _user_id);
            }
            else
            {
                deleted = await _controller.FileDeleteManager.Delete_File(file.CloudStorageNum, file.FileId, _user_id);
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
        private void Button_Move_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetCheckedFiles();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show("이동할 항목을 선택하세요.");
                return;
            }

            var dialog = new FolderSelectDialog(_controller, _user_id)
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
                    _controller.FileRepository.change_dir(cloudInfo);
                }

                LoadFolderContents(currentFolderId);
                RefreshExplorer();

                System.Windows.MessageBox.Show("이동이 완료되었습니다.");
            }
        }

        /*
        private void Button_ConfirmMove_Click(object sender, RoutedEventArgs e)
        {
            if (!isMoveMode || moveTargetFolderId == -2 || moveCandidates.Count == 0)
            {
                System.Windows.MessageBox.Show("이동할 항목 또는 대상 폴더가 지정되지 않았습니다.");
                return;
            }

            foreach (var item in moveCandidates)
            {
                var cloudInfo = ToCloudFileInfo(item);
                var result = _fileRepository.change_dir(cloudInfo);
            }

            isMoveMode = false;
            moveTargetFolderId = -2;
            moveCandidates.Clear();


            UploadButton.Visibility = Visibility.Visible;
            DownloadButton.Visibility = Visibility.Visible;
            DeleteButton.Visibility = Visibility.Visible;
            MoveButton.Visibility = Visibility.Visible;
            CopyButton.Visibility = Visibility.Visible;
            AddFolderButton.Visibility = Visibility.Visible;

            MoveModePanel.Visibility = Visibility.Collapsed;
            PageTitleTextBlock.Text = "홈";

            LoadFolderContents(currentFolderId);
            RefreshExplorer();

            System.Windows.MessageBox.Show("이동이 완료되었습니다.");
        }

        private void Button_CancelMove_Click(object sender, RoutedEventArgs e)
        {
            isMoveMode = false;
            moveTargetFolderId = -2;
            moveCandidates.Clear();

            UploadButton.Visibility = Visibility.Visible;
            DownloadButton.Visibility = Visibility.Visible;
            DeleteButton.Visibility = Visibility.Visible;
            MoveButton.Visibility = Visibility.Visible;
            CopyButton.Visibility = Visibility.Visible;
            AddFolderButton.Visibility = Visibility.Visible;

            MoveModePanel.Visibility = Visibility.Collapsed;
            PageTitleTextBlock.Text = "홈";

        }*/


        ///////////////////////////////////////////////////////////////////////////////////////////////////////
        ///폴더 추가
        private void Button_AddFolder_Click(object sender, RoutedEventArgs e)
        {
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
                ID = _user_id
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
            LoadFolderContents(currentFolderId);
            RefreshExplorer();


        }


        ////////////////////////////////////////////////////////////////////////////////////////
        ///복사 코드
        ///

        private async void Button_Copy_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetCheckedFiles();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show("복사할 항목을 선택하세요.");
                return;
            }

            var dialog = new FolderSelectDialog(_controller, _user_id)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                int targetFolderId = dialog.SelectedFolderId.Value;

                foreach (var item in selected)
                {
                    bool result = await _controller.FileCopyManager.Copy_File(item.FileId, targetFolderId, _user_id);
                    if (!result)
                    {
                        System.Windows.MessageBox.Show($"파일/폴더 '{item.FileName}' 복사 실패");
                    }
                }

                LoadFolderContents(currentFolderId);
                RefreshExplorer();

                System.Windows.MessageBox.Show("복사가 완료되었습니다.");
            }
        }

        private async Task<bool> CopyFolderRecursive(int sourceFolderId, int targetParentFolderId)
        {
            var folderInfo = _controller.FileRepository.specific_file_info(sourceFolderId);
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

            int newFolderId = _controller.FileRepository.add_folder(newFolderInfo);
            if (newFolderId == -1)
            {
                System.Windows.MessageBox.Show($"폴더 '{newFolderInfo.FileName}' 복사 실패");
                return false;
            }

            // 2. 하위 항목 재귀 복사
            var children = _controller.FileRepository.all_file_list_full(sourceFolderId, _user_id);
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
                    await _controller.FileCopyManager.Copy_File(child.FileId, newFolderId, _user_id);
                }
            }

            return true;
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
            var dialog = new DownloadFromLinkWindow(_user_id, _controller)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }

        private void Button_LanSend_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new LanSendWindow(_controller.LanTransferService, _user_id)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }


        private void HomeView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                // 현재 폴더 내용 새로고침
                LoadFolderContents(currentFolderId);
            }
        }

        private async void Button_transfer_show(object sender, RoutedEventArgs e)
        {
            ShowTransferWindow();
        }


        private void OnSearchKeywordSubmitted(string keyword)
        {
            var results = _controller.FileRepository.FindByFileName(keyword, _user_id);

            var viewModels = results.Select(f =>
            {
                var vm = ToViewModel(f);
                vm.FullPath = _controller.FileRepository.GetFullPath(f.FileId);
                return vm;
            }).ToList();

            RightFileListPanel.ItemsSource = viewModels;
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
