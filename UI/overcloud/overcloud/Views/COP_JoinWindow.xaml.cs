using System.Windows;
using DB.overcloud.Repository;
using OverCloud.Services;
using SourceChord.FluentWPF;

namespace overcloud.Views
{
    public partial class COP_JoinWindow : AcrylicWindow
    {
        private LoginController _controller;
        private string user_id = null;

        public COP_JoinWindow(LoginController controller, string user_id)
        {
            _controller = controller;
            this.user_id = user_id;
            InitializeComponent();
        }

        private async void JoinButton_Click(object sender, RoutedEventArgs e)
        {
            string userId = IdBox.Text.Trim();
            string password = PasswordBox.Password.Trim();

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password))
            {
                System.Windows.MessageBox.Show("협업 클라우드 이름과 접근 코드를 모두 입력해주세요.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool success = await OverCloudApiClient.JoinCoopAsync(userId, password);

            if (success)
            {
                System.Windows.MessageBox.Show("클라우드 참가를 완료했습니다.", "성공", MessageBoxButton.OK, MessageBoxImage.Information);
                this.Close();
            }
            else
            {
                System.Windows.MessageBox.Show("참가에 문제가 발생했습니다.", "실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
