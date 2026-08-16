using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using OverCloud.Services;

namespace overcloud.Views
{
    public partial class IssueDetailView : System.Windows.Controls.UserControl
    {
        private readonly LoginController _controller;
        private readonly FileIssueInfo _issueInfo;
        private readonly IssueDetailWindow _parentWindow;

        public IssueDetailView(LoginController controller, FileIssueInfo issueInfo, IssueDetailWindow parentWindow)
        {
            InitializeComponent();

            _controller = controller;
            _issueInfo = issueInfo;
            _parentWindow = parentWindow;

            LoadRelatedFiles();
            LoadComments();
            LoadIssueDisplayView();
        }

        // 오른쪽 정보 디스플레이 로드
        private void LoadIssueDisplayView()
        {
            RightDetailArea.Content = new IssueInfoDisplayView(_issueInfo);
        }

        // 외부에서 호출하는 오른쪽 새로고침 (수정 후 복귀시 사용)
        public void ReloadRightDetail()
        {
            LoadIssueDisplayView();
        }

        // 생성자에서 fire-and-forget으로 호출(생성자는 async일 수 없음) — 다른 async void UI 핸들러와 동일한 패턴.
        // fileId → 전체 경로 해석(GetFullPath)은 이번 이슈 API 이관 범위 밖이라 기존 DB 직접 접속 그대로 둔다.
        private async void LoadRelatedFiles()
        {
            var fileIds = await OverCloudApiClient.GetIssueFilesAsync(_issueInfo.IssueId) ?? new List<int>();

            List<string> fullPaths = new();

            foreach (int fileId in fileIds)
            {
                string fullPath = GetFullPath(fileId);
                fullPaths.Add(fullPath);
            }

            FileListBox.ItemsSource = fullPaths;
        }

        private string GetFullPath(int fileId)
        {
            List<string> pathParts = new();

            while (fileId != -1)
            {
                var fileInfo = _controller.FileRepository.specific_file_info(fileId);
                if (fileInfo == null)
                    break;

                pathParts.Insert(0, fileInfo.FileName);
                fileId = fileInfo.ParentFolderId;
            }

            return "/" + string.Join("/", pathParts);
        }

        // 생성자에서 fire-and-forget으로 호출(생성자는 async일 수 없음).
        private async void LoadComments()
        {
            var commentList = await OverCloudApiClient.GetIssueCommentsAsync(_issueInfo.IssueId) ?? new List<FileIssueComment>();
            CommentListBox.ItemsSource = commentList;
        }

        private async void AddCommentButton_Click(object sender, RoutedEventArgs e)
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox("댓글을 입력하세요:", "코멘트 추가", "");
            if (!string.IsNullOrWhiteSpace(input))
            {
                // commenterId는 더 이상 클라이언트가 안 보낸다 — 서버가 토큰의 sub로 강제한다(위조 방지).
                bool added = await OverCloudApiClient.AddIssueCommentAsync(_issueInfo.IssueId, input);
                if (!added)
                {
                    System.Windows.MessageBox.Show("댓글 등록 실패");
                    return;
                }
                LoadComments();
            }
        }

        private void EditIssueButton_Click(object sender, RoutedEventArgs e)
        {
            RightDetailArea.Content = new IssueInfoEditView(_controller, _issueInfo, this);
        }

        private async void DeleteIssueButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = System.Windows.MessageBox.Show("정말 이 이슈를 삭제하시겠습니까?", "이슈 삭제", MessageBoxButton.YesNo);
            if (confirm == MessageBoxResult.Yes)
            {
                // 서버가 매핑 정리(DeleteMappingsByIssueId)까지 한 번의 호출로 같이 처리한다.
                bool deleted = await OverCloudApiClient.DeleteIssueAsync(_issueInfo.IssueId);
                if (!deleted)
                {
                    System.Windows.MessageBox.Show("이슈 삭제 실패");
                    return;
                }
                System.Windows.MessageBox.Show("이슈 삭제 완료");

                // 부모 창 닫기
                _parentWindow.Close();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _parentWindow.Close();
        }

        public void SwitchRight(System.Windows.Controls.UserControl view)
        {
            RightDetailArea.Content = view;
        }
    }
}
