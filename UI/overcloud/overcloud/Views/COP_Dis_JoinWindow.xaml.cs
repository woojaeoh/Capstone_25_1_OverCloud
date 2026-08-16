using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OverCloud.Services;
using SourceChord.FluentWPF;

namespace overcloud.Views
{
    public partial class COP_Dis_JoinWindow : AcrylicWindow
    {
        private string _user_id_mine;
        private LoginController _controller;

        public COP_Dis_JoinWindow(LoginController controller, string user_id_mine)
        {
            InitializeComponent();
            _controller = controller;
            _user_id_mine = user_id_mine;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            List<string> cooperationAccounts = await OverCloudApiClient.GetMyCoopAccountsAsync() ?? new List<string>();
            cooperationListComboBox.ItemsSource = cooperationAccounts;

            if (cooperationAccounts.Count > 0)
                cooperationListComboBox.SelectedIndex = 0;
        }

        private async void WithdrawButton_Click(object sender, RoutedEventArgs e)
        {
            string selectedUserId = cooperationListComboBox.SelectedItem as string;

            if (string.IsNullOrEmpty(selectedUserId))
            {
                System.Windows.MessageBox.Show("탈퇴할 협업 계정을 선택하세요.");
                return;
            }

            bool result = await OverCloudApiClient.LeaveCoopAsync(selectedUserId);

            System.Windows.MessageBox.Show(result ? "탈퇴 성공" : "탈퇴 실패");
            if (result) this.Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

    }
}
