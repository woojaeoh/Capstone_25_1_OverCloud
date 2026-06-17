using DB.overcloud.Repository;
using OverCloud.Services.FileManager;
using OverCloud.Services.StorageManager;
using OverCloud.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows;

namespace overcloud.Views
{
    public partial class SharedAccountListView : System.Windows.Controls.UserControl
    {
        private LoginController _controller;
        private string _user_id;

        private ICollectionView _view;
        private ObservableCollection<AccountItemViewModel> _items;

        private List<string> _cooperationGroups;
        private string _selectedCoopId;

        public SharedAccountListView(LoginController controller, string user_id)
        {
            InitializeComponent();

            _controller = controller;
            _user_id = user_id;
        }

        private void SharedAccountListView_Loaded(object sender, RoutedEventArgs e)
        {
            _cooperationGroups = _controller.CoopUserRepository.connected_cooperation_account_nums(_user_id);

            // "전체 보기" 항목 추가
            _cooperationGroups.Insert(0, "전체");

            CoopSelector.ItemsSource = _cooperationGroups;
            CoopSelector.SelectedIndex = 0; // 기본은 전체
        }

        private void CoopSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CoopSelector.SelectedItem is string selected)
            {
                _selectedCoopId = selected;
                RefreshList();
            }
        }

        private void RefreshList()
        {
            _items = new ObservableCollection<AccountItemViewModel>();

            if (_selectedCoopId == "전체")
            {
                // 전체 협업 클라우드 기준
                var joinedCoops = _controller.CoopUserRepository.connected_cooperation_account_nums(_user_id);

                foreach (var coopId in joinedCoops)
                {
                    var accounts = _controller.AccountService.Get_Clouds_For_User(coopId);
                    foreach (var acc in accounts)
                    {
                        _items.Add(new AccountItemViewModel
                        {
                            CloudName = acc.CloudType,
                            IsActive = true,
                            AccountId = acc.AccountId,
                            Owner = coopId,
                            UsagePercent = acc.TotalCapacity > 0 ? (int)(acc.UsedCapacity * 100.0 / acc.TotalCapacity) : 0,
                            UsageDisplay = $"{((double)acc.UsedCapacity / 1024 / 1024):F2}/{((double)acc.TotalCapacity / 1024 / 1024):F2} GB",
                            LastLoginDate = DateTime.Now,
                            IsSelected = false
                        });
                    }
                }
            }
            else
            {
                // 특정 협업 클라우드만
                var accounts = _controller.AccountService.Get_Clouds_For_User(_selectedCoopId);
                foreach (var acc in accounts)
                {
                    _items.Add(new AccountItemViewModel
                    {
                        CloudName = acc.CloudType,
                        IsActive = true,
                        AccountId = acc.AccountId,
                        Owner = _selectedCoopId,
                        UsagePercent = acc.TotalCapacity > 0 ? (int)(acc.UsedCapacity * 100.0 / acc.TotalCapacity) : 0,
                        UsageDisplay = $"{((double)acc.UsedCapacity / 1024 / 1024):F2}/{((double)acc.TotalCapacity / 1024 / 1024):F2} GB",
                        LastLoginDate = DateTime.Now,
                        IsSelected = false
                    });
                }
            }

            _view = CollectionViewSource.GetDefaultView(_items);
            AccountsGrid.ItemsSource = _view;
        }




        public class AccountItemViewModel : INotifyPropertyChanged
        {
            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set { _isSelected = value; OnPropertyChanged(); }
            }

            public string CloudName { get; set; }
            public bool IsActive { get; set; }
            public string AccountId { get; set; }
            public int UsagePercent { get; set; }
            public string UsageDisplay { get; set; }
            public DateTime LastLoginDate { get; set; }

            public string Owner { get; set; }  // ✅ 추가

            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string prop = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }


        private void Button_Add_Click(object sender, RoutedEventArgs e)
        {
            var window = new AddAccountWindow(_controller, _user_id, true);
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();

            RefreshList();
        }

        private void Button_Delete_Click(object sender, RoutedEventArgs e)
        {
            var window = new DeleteAccountWindow(_controller, _user_id, true);
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();

            RefreshList();
        }


    }
}
