using System;
using System.IO;
using System.Windows;
using OverCloud.Services;
using SourceChord.FluentWPF;

namespace overcloud.Views
{
    public partial class LanSendWindow : AcrylicWindow
    {
        private readonly LanTransferService _lanService;
        private readonly string _currentUserId;

        public LanSendWindow(LanTransferService lanService, string currentUserId)
        {
            InitializeComponent();
            _lanService = lanService;
            _currentUserId = currentUserId;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "전송할 파일 선택" };
            if (dialog.ShowDialog() == true)
                FilePathBox.Text = dialog.FileName;
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            string targetUserId = TargetUserIdBox.Text.Trim();
            string filePath = FilePathBox.Text.Trim();

            if (string.IsNullOrEmpty(targetUserId))
            {
                System.Windows.MessageBox.Show("받는 사람 ID를 입력하세요.");
                return;
            }
            if (!File.Exists(filePath))
            {
                System.Windows.MessageBox.Show("전송할 파일을 선택하세요.");
                return;
            }
            if (targetUserId == _currentUserId)
            {
                System.Windows.MessageBox.Show("자기 자신에게는 전송할 수 없습니다.");
                return;
            }

            SendButton.IsEnabled = false;
            StatusText.Text = "전송 중...";
            SendProgress.Value = 0;

            // LanTransferService.SendFileAsync에 진행률 콜백 연결
            bool result = await _lanService.SendFileAsync(
                targetUserId,
                filePath,
                progress => Dispatcher.Invoke(() =>
                {
                    SendProgress.Value = progress;
                    StatusText.Text = $"{progress}%";
                })
            );

            if (result)
            {
                StatusText.Text = "전송 완료";
                System.Windows.MessageBox.Show($"{Path.GetFileName(filePath)} 전송 완료");
                Close();
            }
            else
            {
                StatusText.Text = "전송 실패 — 상대방이 오프라인이거나 같은 네트워크가 아닙니다.";
                SendButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
