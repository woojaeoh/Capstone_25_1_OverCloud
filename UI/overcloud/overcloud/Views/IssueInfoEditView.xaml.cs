using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DB.overcloud.Models;
using OverCloud.Services;

namespace overcloud.Views
{
    public partial class IssueInfoEditView : System.Windows.Controls.UserControl
    {
        private readonly LoginController _controller;
        private readonly FileIssueInfo _issueInfo;
        private readonly IssueDetailView _parentDetailView;
        private readonly string _coopId;

        public IssueInfoEditView(LoginController controller, FileIssueInfo issueInfo, IssueDetailView parentDetailView)
        {
            InitializeComponent();
            _controller = controller;
            _issueInfo = issueInfo;
            _parentDetailView = parentDetailView;
            _coopId = issueInfo.ID; // FileIssueInfo 안에 협업ID가 들어있다고 가정

            LoadData();
        }

        private void LoadData()
        {
            TitleTextBox.Text = _issueInfo.Title;
            DescriptionTextBox.Text = _issueInfo.Description;

            LoadAssignedUserList();

            // Status 콤보박스 초기화
            StatusComboBox.Items.Clear();
            StatusComboBox.Items.Add("OPEN");
            StatusComboBox.Items.Add("IN_PROGRESS");
            StatusComboBox.Items.Add("RESOLVED");
            StatusComboBox.Items.Add("CLOSED");

            StatusComboBox.SelectedItem = _issueInfo.Status;
            DueDatePicker.SelectedDate = _issueInfo.DueDate;
        }

        // 생성자(→LoadData)에서 fire-and-forget으로 호출(생성자는 async일 수 없음).
        private async void LoadAssignedUserList()
        {
            var users = await OverCloudApiClient.GetCoopMembersAsync(_coopId) ?? new List<string>();
            AssignedToComboBox.Items.Clear();

            // null 가능하도록 맨 앞에 (없음) 추가
            ComboBoxItem emptyItem = new ComboBoxItem { Content = "(없음)", Tag = null };
            AssignedToComboBox.Items.Add(emptyItem);

            foreach (var userId in users)
            {
                ComboBoxItem item = new ComboBoxItem { Content = userId, Tag = userId };
                AssignedToComboBox.Items.Add(item);
            }

            // 현재 담당자가 있다면 해당 유저를 선택
            if (_issueInfo.AssignedTo == null)
            {
                AssignedToComboBox.SelectedIndex = 0;  // "(없음)"
            }
            else
            {
                foreach (ComboBoxItem item in AssignedToComboBox.Items)
                {
                    if (item.Tag as string == _issueInfo.AssignedTo)
                    {
                        AssignedToComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _issueInfo.Title = TitleTextBox.Text;
            _issueInfo.Description = DescriptionTextBox.Text;

            // 담당자 업데이트
            if (AssignedToComboBox.SelectedItem is ComboBoxItem selectedUser)
            {
                _issueInfo.AssignedTo = selectedUser.Tag as string;  // null 허용
            }

            // 상태 업데이트 (선택 안했으면 이전 상태 유지)
            if (StatusComboBox.SelectedItem != null)
            {
                _issueInfo.Status = StatusComboBox.SelectedItem.ToString();
            }

            _issueInfo.DueDate = DueDatePicker.SelectedDate;

            bool updated = await OverCloudApiClient.UpdateIssueAsync(
                _issueInfo.IssueId, _issueInfo.Title, _issueInfo.Description,
                _issueInfo.AssignedTo, _issueInfo.Status, _issueInfo.DueDate);
            if (updated)
            {
                System.Windows.MessageBox.Show("이슈가 수정되었습니다.");
                _parentDetailView.ReloadRightDetail();  // 다시 조회 뷰로 복귀
            }
            else
            {
                System.Windows.MessageBox.Show("이슈 수정 실패.");
            }
        }
    }
}
