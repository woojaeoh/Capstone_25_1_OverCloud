using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiveCharts;
using LiveCharts.Wpf;
using OverCloud.Services;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.Services.StorageManager;
using DB.overcloud.Repository;
using DB.overcloud.Models;
using Separator = LiveCharts.Wpf.Separator;
using System.Diagnostics;
using OverCloud.Services.FileManager;

namespace overcloud.Views
{
    public partial class AccountDetailView : System.Windows.Controls.UserControl
    {
        private LoginController _controller;
        private string _user_id;

        // true = 막대 차트, false = 파이 차트
        private bool _isBarMode = true;
        private string _currentFilter = "All";

        public AccountDetailView(LoginController controller, string user_id)
        {
            InitializeComponent();

            // 서비스 초기화
            _controller = controller;
            _user_id = user_id;

            // 초기 탭 선택
            FilterTab.SelectedIndex = 0;
            // 초기 토글 버튼 텍스트
            ToggleChartButton.Content = "파이 차트";
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // 첫 로드 시
            LoadUsageDetails("All");
            LoadChart("All");
        }

        private void FilterTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilterTab.SelectedItem is TabItem ti)
            {
                _currentFilter = ti.Tag.ToString();
                LoadUsageDetails(_currentFilter);
                LoadChart(_currentFilter);
            }
        }

        private void LoadUsageDetails(string filter)
        {
            // 전체 계정 정보 가져오기
            var all = _controller.AccountService.Get_Clouds_For_User(_user_id);

            // 필터링: “All” 이 아니면 해당 CloudType만
            var filtered = filter == "All"
                ? all
                : all.Where(a => a.CloudType.Equals(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            // 드라이브별 그룹화 및 합산
            var grouped = filtered
                .GroupBy(a => a.CloudType)
                .Select(g => new
                {
                    CloudType = g.Key,
                    Total = g.Sum(x => (double)x.TotalCapacity) / 1024/1024,
                    Used = g.Sum(x => (double)x.UsedCapacity)/1024/1024
                })
                .ToList();

            // 뷰모델 생성
            var items = grouped
                .Select(g => new UsageItemViewModel
                {
                    DriveName = g.CloudType,
                    TotalDisplay = $"{g.Total:F2}GB",
                    UsedDisplay = $"{g.Used:F2}GB",
                    UsedPercent = g.Total > 0
                        ? (int)(g.Used * 100.0 / g.Total)
                        : 0
                })
                .ToList();

            // “All” 필터일 때 전체 합계 행 추가
            if (filter == "All")
            {
                double totalSum = grouped.Sum(g => g.Total);
                double usedSum = grouped.Sum(g => g.Used);
                items.Insert(0, new UsageItemViewModel
                {
                    DriveName = "Total",
                    TotalDisplay = $"{totalSum:F2}GB",
                    UsedDisplay = $"{usedSum:F2}GB",
                    UsedPercent = totalSum > 0
                        ? (int)(usedSum * 100.0 / totalSum)
                        : 0
                });
            }

            UsageList.ItemsSource = items;
        }

        private void LoadChart(string filter)
        {
            var all = _controller.AccountService.Get_Clouds_For_User(_user_id);

            // 필터링
            var filtered = filter == "All"
                ? all
                : all.Where(a => a.CloudType.Equals(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            // 그룹화 & 합산
            var grouped = filtered
                .GroupBy(a => a.CloudType)
                .Select(g => new
                {
                    CloudType = g.Key,
                    Total = g.Sum(x => (double)x.TotalCapacity) /1024 /1024,
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
            // 모드 토글
            _isBarMode = !_isBarMode;

            // 버튼 텍스트 변경
            ToggleChartButton.Content = _isBarMode
                ? "파이 차트"
                : "막대 차트";

            // 현재 필터 기준으로 차트만 다시 렌더
            LoadChart(_currentFilter);
        }

        private void Button_Add_Click(object sender, RoutedEventArgs e)
        {
            AddAccountWindow window = new AddAccountWindow(_controller, _user_id, false);
            window.ShowDialog();
        }

        private void Button_Delete_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("삭제 버튼 누름");
            var window = new DeleteAccountWindow(_controller, _user_id, false);
            // this(UserControl)가 아니라 이 컨트롤을 호스트하는 Window를 Owner로 지정
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();
            // 필요하다면 HomeView 쪽 RefreshExplorer() 호출
        }
    }

    public class UsageItemViewModel
    {
        public string DriveName { get; set; }
        public string TotalDisplay { get; set; }
        public string UsedDisplay { get; set; }
        public int UsedPercent { get; set; }
    }
}
