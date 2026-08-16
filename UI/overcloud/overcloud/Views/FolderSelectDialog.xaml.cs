using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DB.overcloud.Repository;
using System.Windows.Controls;
using System.Windows;
using OverCloud.Services;
using SourceChord.FluentWPF;

namespace overcloud.Views
{
    public partial class FolderSelectDialog : AcrylicWindow
    {
        private LoginController _controller;
        public int? SelectedFolderId { get; private set; } = null;
        private string user_id = null;

        public FolderSelectDialog(LoginController _controller, string user_id)
        {
            InitializeComponent();
            this._controller = _controller;
            this.user_id = user_id;
            LoadFolders();
        }

        // 생성자에서 fire-and-forget으로 호출(생성자는 async일 수 없음) — 다른 async void UI 핸들러와 동일한 패턴.
        private async void LoadFolders()
        {
            // 내 클라우드
            var myRootItem = new TreeViewItem { Header = "📁 내 클라우드", Tag = -1 };
            LoadChildFolders(myRootItem, -1, user_id);
            FolderTreeView.Items.Add(myRootItem);

            // 협업 클라우드
            var cooperationIds = await OverCloudApiClient.GetMyCoopAccountsAsync() ?? new List<string>();
            foreach (var coopUserId in cooperationIds)
            {
                var coopRootItem = new TreeViewItem
                {
                    Header = $"🤝 협업 클라우드 ({coopUserId})",
                    Tag = (-1, coopUserId) // 튜플로 구분
                };
                LoadChildFolders(coopRootItem, -1, coopUserId);
                FolderTreeView.Items.Add(coopRootItem);
            }
        }


        private void LoadChildFolders(TreeViewItem parentItem, int parentId, string targetUserId)
        {
            var folders = _controller.FileRepository.all_file_list(parentId, targetUserId)
                .Where(f => f.IsFolder)
                .ToList();

            foreach (var folder in folders)
            {
                var item = new TreeViewItem
                {
                    Header = folder.FileName,
                    Tag = folder.FileId
                };

                item.Expanded += (s, e) =>
                {
                    if (item.Items.Count == 0)
                        LoadChildFolders(item, folder.FileId, targetUserId);
                };

                parentItem.Items.Add(item);
            }
        }


        private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item && item.Tag is int id)
            {
                SelectedFolderId = id;
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFolderId == null)
            {
                System.Windows.MessageBox.Show("폴더를 선택하세요.");
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

}
