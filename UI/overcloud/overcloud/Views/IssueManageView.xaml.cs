using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using OverCloud.Services;

namespace overcloud.Views
{
    public partial class IssueManageView : System.Windows.Controls.UserControl
    {
        private readonly LoginController _controller;
        private readonly string _userId;

        private string currentCoopId = null;
        private List<FileIssueInfo> currentIssueList = new();

        public IssueManageView(LoginController controller, string userId)
        {
            InitializeComponent();
            _controller = controller;
            _userId = userId;
            Loaded += IssueManageView_Loaded;

            this.KeyDown += HomeView_KeyDown;
            this.Focusable = true;
            this.Focus();
        }

        private async void IssueManageView_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadCooperationList();
        }

        private async Task LoadCooperationList()
        {
            var coopAccounts = await OverCloudApiClient.GetMyCoopAccountsAsync() ?? new List<string>();
            var coopList = coopAccounts
                            .Select(id => new CoopAccountViewModel {
                                AccountId = id,
                                ImagePath = "pack://application:,,,/overcloud;component/asset/box.png"
                            })

                            .ToList();

            CoopListBox.ItemsSource = coopList;

            if (coopList.Count > 0)
            {
                CoopListBox.SelectedIndex = -1;
            }
        }

        // 핵심 변경: Mouse 이벤트로 항상 선택 이벤트를 강제 발생
        private async void CoopListBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(CoopListBox, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item != null)
            {
                CoopListBox.SelectedItem = item.DataContext;
                if (item.DataContext is CoopAccountViewModel selectedCoop)
                {
                    currentCoopId = selectedCoop.AccountId;
                    await LoadIssues();
                }
                e.Handled = true;
            }
        }

        private async Task LoadIssues()
        {
            if (currentCoopId == null) return;

            currentIssueList = await OverCloudApiClient.GetIssuesAsync(currentCoopId) ?? new List<FileIssueInfo>();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (IssueItemsControl == null) return;

            string selectedStatus = (StatusFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ALL";

            if (selectedStatus == "ALL")
            {
                IssueItemsControl.ItemsSource = currentIssueList;
            }
            else
            {
                var filtered = currentIssueList.Where(issue => issue.Status == selectedStatus).ToList();
                IssueItemsControl.ItemsSource = filtered;
            }
        }

        private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadIssues();
        }

        private void IssueCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is StackPanel panel && panel.DataContext is FileIssueInfo selectedIssue)
            {
                var detailWindow = new IssueDetailWindow(_controller, selectedIssue)
                {
                    Owner = Window.GetWindow(this)
                };

                // 위치 조정 로직
                var ownerWindow = detailWindow.Owner;
                if (ownerWindow != null)
                {
                    // Owner 중앙에 배치
                    detailWindow.WindowStartupLocation = WindowStartupLocation.Manual;

                    double targetLeft = ownerWindow.Left + (ownerWindow.Width - detailWindow.Width) / 2;
                    double targetTop = ownerWindow.Top + (ownerWindow.Height - detailWindow.Height) / 2;

                    // 화면 위로 넘어가지 않도록 최소값 보정
                    if (targetTop < 0) targetTop = 0;

                    detailWindow.Left = targetLeft;
                    detailWindow.Top = targetTop;
                }

                detailWindow.ShowDialog();
            }
        }


        public class CoopAccountViewModel
        {
            public string AccountId { get; set; }
            public string ImagePath { get; set; }
        }

        private async void HomeView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                // 현재 폴더 내용 새로고침
                await LoadIssues();
            }
        }
    }
}
