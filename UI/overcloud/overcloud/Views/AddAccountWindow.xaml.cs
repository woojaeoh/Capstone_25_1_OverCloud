using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DB.overcloud.Repository;
using DB.overcloud.Models;  // CloudAccountInfo 클래스 사용을 위해 추가
using OverCloud.Services;
using SourceChord.FluentWPF; // AcrylicWindow 상속을 위해 추가

namespace overcloud.Views
{
    public partial class AddAccountWindow : AcrylicWindow
    {
        private readonly LoginController _controller;
        private readonly string _userId;
        private readonly bool _isCoopMode;

        public AddAccountWindow(LoginController controller, string userId, bool coop)
        {
            InitializeComponent();
            _controller = controller;
            _userId = userId;
            _isCoopMode = coop;

            // 창 드래그 가능
            this.MouseDown += (s, e) => { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) this.DragMove(); };
        }

        private async void AddAccountWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isCoopMode)
            {
                cooperationComboBox.Visibility = Visibility.Visible;
                var coopAccounts = await OverCloudApiClient.GetMyCoopAccountsAsync() ?? new List<string>();
                cooperationComboBox.ItemsSource = coopAccounts;
            }
            else
            {
                cooperationComboBox.Visibility = Visibility.Collapsed;
            }
        }

        private async void Confirm_Click(object sender, RoutedEventArgs e)
        {
            string cloudType = (cloudComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

            string targetId = _isCoopMode && cooperationComboBox.SelectedItem != null
                ? cooperationComboBox.SelectedItem.ToString()
                : _userId;

            bool success;
            string message = null;

            if (cloudType == "GoogleDrive" || cloudType == "OneDrive")
            {
                // 인터랙티브 OAuth(브라우저 팝업)라 서버에서 대신 못 돌림 — client_id/redirect_uri/scope는
                // 서버에서 받고, code 교환도 서버가 대신 하지만 브라우저 열기/로컬 리스너는 클라이언트 몫.
                (success, message) = await overcloud.transfer_manager.StorageAddManager.AddAsync(cloudType, targetId);
            }
            else
            {
                // Dropbox는 기존 DB 직접 접속 경로 그대로 유지(이번 재설계 범위 밖).
                var accountInfo = new CloudStorageInfo
                {
                    ID = targetId,
                    AccountId = txtID.Text,
                    AccountPassword = txtPassword.Password,
                    CloudType = cloudType,
                    TotalCapacity = 0,
                    UsedCapacity = 0
                };
                success = await _controller.AccountService.Add_Cloud_Storage(accountInfo, _userId);
            }

            System.Windows.MessageBox.Show(success ? "계정 추가 성공" : (message ?? "계정 추가 실패"));
            if (success)
                this.Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
