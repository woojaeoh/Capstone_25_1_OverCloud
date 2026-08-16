using DB.overcloud.Repository;
using OverCloud.Services.FileManager;
using OverCloud.Services.StorageManager;
using OverCloud.Services;
using System.Windows.Controls;
using System.Windows;
using LiveCharts.Wpf;
using LiveCharts;
using System.Diagnostics;
using Separator = LiveCharts.Wpf.Separator;

namespace overcloud.Views
{
    public partial class SharedAccountDetailView : System.Windows.Controls.UserControl  //협업 계정에 연결된 모든 클라우드 계정을 가져오도록 수정해야함
    {
        private LoginController _controller;
        private string _user_id;

        private bool _isBarMode = true;
        private string _currentFilter = "All";

        private List<string> _cooperationGroups;
        private string _selectedCoopId;

        public SharedAccountDetailView(LoginController controller, string user_id)
        {
            InitializeComponent();
            _controller = controller;
            _user_id = user_id;

            // 초기화
            _currentFilter = "All";
        }
        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _cooperationGroups = await OverCloudApiClient.GetMyCoopAccountsAsync() ?? new List<string>();
            CoopSelector.ItemsSource = _cooperationGroups;

            if (_cooperationGroups.Any())
            {
                CoopSelector.SelectedIndex = 0;
            }
        }


        private void CoopSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CoopSelector.SelectedItem is string selected)
            {
                _selectedCoopId = selected;
                LoadUsageDetails(_selectedCoopId);
                LoadChart(_selectedCoopId);
            }
        }

        private void LoadUsageDetails(string coopId)
        {
            var clouds = _controller.AccountService.Get_Clouds_For_User(coopId);

            var grouped = clouds
                .GroupBy(c => c.CloudType)
                .Select(g => new
                {
                    CloudType = g.Key,
                    Total = g.Sum(x => (double)x.TotalCapacity) / 1024 / 1024,
                    Used = g.Sum(x => (double)x.UsedCapacity) / 1024 / 1024
                })
                .ToList();

            var items = grouped.Select(g => new UsageItemViewModel
            {
                DriveName = g.CloudType,
                TotalDisplay = $"{g.Total:F2}GB",
                UsedDisplay = $"{g.Used:F2}GB",
                UsedPercent = g.Total > 0 ? (int)(g.Used * 100.0 / g.Total) : 0
            }).ToList();

            UsageList.ItemsSource = items;
        }


        private void LoadChart(string coopId)   
        {
            var clouds = _controller.AccountService.Get_Clouds_For_User(coopId);

            var grouped = clouds
                .GroupBy(c => c.CloudType)
                .Select(g => new
                {
                    CloudType = g.Key,
                    Total = g.Sum(x => (double)x.TotalCapacity) / 1024 / 1024,
                    Used = g.Sum(x => (double)x.UsedCapacity) / 1024 / 1024
                })
                .ToList();


            if (_isBarMode)
            {
                // 막대 차트
                var cart = new CartesianChart
                {
                    Series = new SeriesCollection
            {
                new ColumnSeries
                {
                    Title  = "Used",
                    Values = new ChartValues<double>(grouped.Select(g => (double)g.Used))
                },
                new ColumnSeries
                {
                    Title  = "Free",
                    Values = new ChartValues<double>(
                        grouped.Select(g => (double)(g.Total - g.Used)))
                }
            }
                };
                cart.AxisX.Add(new Axis
                {
                    Labels = grouped.Select(g => g.CloudType).ToArray(),
                    Separator = new Separator { Step = 1 }
                });
                cart.AxisY.Add(new Axis { Title = "Storage (GB)" });

                ChartContainer.Content = cart;
            }
            else
            {
                // 파이 차트
                if (!grouped.Any())
                {
                    ChartContainer.Content = null;
                    return;
                }

                var pie = new PieChart
                {
                    LegendLocation = LegendLocation.Right,
                    Series = new SeriesCollection()
                };

                foreach (var g in grouped)
                {
                    pie.Series.Add(new PieSeries
                    {
                        Title = $"{g.CloudType} Used ({g.Used:F2}GB)",
                        Values = new ChartValues<double> { g.Used },
                        DataLabels = true
                    });
                    pie.Series.Add(new PieSeries
                    {
                        Title = $"{g.CloudType} Free ({g.Total - g.Used:F2}GB)",
                        Values = new ChartValues<double> { g.Total - g.Used },
                        DataLabels = true
                    });
                }

                ChartContainer.Content = pie;
            }
        }

        private void ToggleChart_Click(object sender, RoutedEventArgs e)
        {
            _isBarMode = !_isBarMode;
            ToggleChartButton.Content = _isBarMode
                ? "파이 차트"
                : "막대 차트";

            // 토글 시에도 실제 선택된 계정 ID를 넘깁니다.
            if (!string.IsNullOrEmpty(_selectedCoopId))
            {
                LoadChart(_selectedCoopId);
            }
        }


        private void Button_Add_Click(object sender, RoutedEventArgs e)
        {
            AddAccountWindow window = new AddAccountWindow(_controller, _user_id, true);
            window.ShowDialog();
        }

        private void Button_Delete_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("삭제 버튼 누름");
            var window = new DeleteAccountWindow(_controller, _user_id, true);
            // this(UserControl)가 아니라 이 컨트롤을 호스트하는 Window를 Owner로 지정
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();
            // 필요하다면 HomeView 쪽 RefreshExplorer() 호출
        }
    }

}
