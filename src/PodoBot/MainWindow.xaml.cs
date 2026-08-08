using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PodoBot;

public partial class MainWindow : Window
{
    private readonly LocalDataStore _store;
    private readonly SecureTokenStore _secureTokens;
    private readonly ChzzkApiClient _api;
    private readonly AuthManager _auth;
    private readonly ChzzkSessionClient _session;
    private readonly OverlayServer _overlay;
    private readonly BotEngine _bot;
    private readonly RepeatingMessageService _repeatingMessages;
    private readonly CountdownTimerService _countdown;

    public ObservableCollection<RouletteDefinition> Roulettes => _store.Data.Roulettes;

    public MainWindow()
    {
        InitializeComponent();

        _store = new LocalDataStore();
        _secureTokens = new SecureTokenStore(_store.DirectoryPath);
        _api = new ChzzkApiClient();
        _auth = new AuthManager(_api, _secureTokens);
        _session = new ChzzkSessionClient(_api);
        _overlay = new OverlayServer(_store);

        DataContext = this;

        CommandsGrid.ItemsSource = _store.Data.Commands;
        RoulettesGrid.ItemsSource = _store.Data.Roulettes;
        DonationRulesGrid.ItemsSource = _store.Data.DonationRules;
        RepeatingMessagesGrid.ItemsSource = _store.Data.RepeatingMessages;
        CountdownTimersGrid.ItemsSource = _store.Data.CountdownTimers;
        CountersGrid.ItemsSource = _store.Data.Counters;
        SongBookGrid.ItemsSource = _store.Data.Songs;

        if (_store.Data.Roulettes.Count > 0)
            RoulettesGrid.SelectedIndex = 0;

        _bot = new BotEngine(_store, SendChatAsync, _overlay.PublishRouletteAsync);
        _repeatingMessages = new RepeatingMessageService(_store, SendChatAsync);
        _countdown = new CountdownTimerService(SendChatAsync, _overlay);

        _overlay.AuthorizationHandler = OnAuthorizationAsync;
        _overlay.Log += AppendLog;
        _session.Log += AppendLog;
        _session.ConnectionChanged += OnConnectionChanged;
        _session.ChatReceived += _bot.ProcessAsync;
        _session.DonationReceived += _bot.ProcessDonationAsync;
        _bot.Log += AppendLog;
        _repeatingMessages.Log += AppendLog;
        _countdown.Log += AppendLog;
        _countdown.StatusChanged += OnCountdownStatusChanged;

        Loaded += Window_Loaded;
        Closing += Window_Closing;

        RefreshHome();
        RefreshRouletteEditor();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _overlay.StartAsync();
            AppendLog("PodoBot 준비 완료");
        }
        catch (Exception ex)
        {
            AppendLog($"로컬 기능 시작 실패: {ex.Message}");
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            await SaveAsync(validateRoulette: false);
            await _countdown.DisposeAsync();
            await _repeatingMessages.DisposeAsync();
            await _session.DisposeAsync();
            await _overlay.DisposeAsync();
        }
        catch { }
    }

    private async void MainConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsConnected) return;

        try
        {
            if (string.IsNullOrWhiteSpace(_auth.Tokens.RefreshToken))
            {
                StartLogin();
                return;
            }

            MainConnectButton.IsEnabled = false;
            MainConnectButton.Content = "연결 중...";
            var token = await _auth.GetAccessTokenAsync();
            await _session.ConnectAsync(token);
        }
        catch (Exception ex)
        {
            AppendLog($"연결 실패: {ex.Message}");
            MessageBox.Show(ex.Message, "PodoBot", MessageBoxButton.OK, MessageBoxImage.Information);
            MainConnectButton.IsEnabled = true;
            MainConnectButton.Content = "방송봇 켜기";
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        => await _session.DisconnectAsync();

    private void LoginButton_Click(object sender, RoutedEventArgs e) => StartLogin();

    private void StartLogin()
    {
        try
        {
            var url = _auth.CreateLoginUrl();
            OpenUrl(url);
            AppendLog("브라우저에서 치지직 연결을 승인해 주세요.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "치지직 연결", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task OnAuthorizationAsync(string code, string state)
    {
        await _auth.HandleCodeAsync(code, state);

        await Dispatcher.InvokeAsync(() =>
        {
            RefreshHome();
            AppendLog($"치지직 로그인 완료: {_auth.Tokens.ChannelName}");
        });

        try
        {
            var token = await _auth.GetAccessTokenAsync();
            await _session.ConnectAsync(token);
        }
        catch (Exception ex)
        {
            AppendLog($"자동 연결 실패: {ex.Message}");
        }
    }

    private async Task SendChatAsync(string text)
    {
        var token = await _auth.GetAccessTokenAsync();
        _bot.NotifyOutgoing(text);
        await _api.SendChatAsync(token, text);
        AppendLog($"PodoBot: {text}");
    }

    private void OnConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            StatusDot.Fill = connected
                ? new SolidColorBrush(Color.FromRgb(54, 191, 123))
                : new SolidColorBrush(Color.FromRgb(167, 171, 180));

            StatusText.Text = connected ? "방송봇 켜짐" : "연결 안 됨";
            DisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            MainConnectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            MainConnectButton.IsEnabled = true;
            MainConnectButton.Content = string.IsNullOrWhiteSpace(_auth.Tokens.RefreshToken)
                ? "치지직 연결하기"
                : "방송봇 켜기";

            if (connected)
            {
                _repeatingMessages.Start();
                WelcomeTitle.Text = "방송 준비 완료";
                WelcomeBody.Text = "명령어, 룰렛, 후원, 반복 안내를 감시하고 있습니다.";
            }
            else
            {
                _ = _repeatingMessages.StopAsync();
                RefreshHome();
            }
        });
    }

    private void RefreshHome()
    {
        var loggedIn = !string.IsNullOrWhiteSpace(_auth.Tokens.RefreshToken);
        ChannelText.Text = string.IsNullOrWhiteSpace(_auth.Tokens.ChannelName)
            ? ""
            : $"연결 계정: {_auth.Tokens.ChannelName}";
        AccountText.Text = loggedIn
            ? $"{_auth.Tokens.ChannelName} 계정이 연결되어 있습니다."
            : "치지직에 연결되지 않았습니다.";

        if (!_session.IsConnected)
        {
            WelcomeTitle.Text = loggedIn ? "방송봇을 켜 주세요" : "치지직을 연결해 주세요";
            WelcomeBody.Text = loggedIn ? "버튼 한 번이면 방송 기능이 시작됩니다." : "처음 한 번만 로그인하면 됩니다.";
            MainConnectButton.Content = loggedIn ? "방송봇 켜기" : "치지직 연결하기";
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync(validateRoulette: true);
            AppendLog("설정 저장 완료");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "저장할 수 없음");
        }
    }

    private async Task SaveAsync(bool validateRoulette)
    {
        CommitGrid(CommandsGrid);
        CommitGrid(RoulettesGrid);
        CommitGrid(RouletteItemsGrid);
        CommitGrid(DonationRulesGrid);
        CommitGrid(RepeatingMessagesGrid);
        CommitGrid(CountdownTimersGrid);
        CommitGrid(CountersGrid);
        CommitGrid(SongBookGrid);

        if (validateRoulette)
        {
            foreach (var roulette in _store.Data.Roulettes)
            {
                var activeItems = roulette.Items
                    .Where(x => !string.IsNullOrWhiteSpace(x.Text) && x.ChancePercent > 0)
                    .ToArray();

                if (activeItems.Length == 0) continue;

                var total = activeItems.Sum(x => x.ChancePercent);
                if (Math.Abs(total - 100) > 0.001)
                    throw new InvalidOperationException($"{roulette.Name} 확률 합계가 {total:0.###}%입니다. 100%로 맞춰 주세요.");
            }

            foreach (var rule in _store.Data.DonationRules.Where(x => x.Enabled))
            {
                if (!_store.Data.Roulettes.Any(x => x.Id == rule.RouletteId))
                    throw new InvalidOperationException("후원 룰렛 규칙에 실행할 룰렛을 선택하세요.");
            }
        }

        await _store.SaveAsync();
        _repeatingMessages.Reset();
        RefreshRouletteEditor();
    }

    private static void CommitGrid(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void RoulettesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshRouletteEditor();

    private void RefreshRouletteEditor()
    {
        if (RoulettesGrid.SelectedItem is not RouletteDefinition roulette)
        {
            RouletteItemsGrid.ItemsSource = null;
            SelectedRouletteText.Text = "룰렛 항목";
            RouletteTotalText.Text = "";
            return;
        }

        RouletteItemsGrid.ItemsSource = roulette.Items;
        SelectedRouletteText.Text = $"{roulette.Name} 항목";
        var total = roulette.Items.Sum(x => x.ChancePercent);
        RouletteTotalText.Text = $"현재 합계 {total:0.###}%";
        RouletteTotalText.Foreground = Math.Abs(total - 100) <= 0.001 ? Brushes.SeaGreen : Brushes.IndianRed;
    }

    private void RouletteItemsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(new Action(RefreshRouletteEditor));

    private void AddCommandButton_Click(object sender, RoutedEventArgs e)
        => _store.Data.Commands.Add(new BotCommand { Trigger = "!새명령어", Response = "답변 내용을 입력하세요.", Permission = "전체" });

    private void DeleteCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsGrid.SelectedItem is BotCommand item) _store.Data.Commands.Remove(item);
    }

    private void AddRouletteButton_Click(object sender, RoutedEventArgs e)
    {
        var number = _store.Data.Roulettes.Count + 1;
        var roulette = new RouletteDefinition { Name = $"룰렛 {number}", Trigger = $"!룰렛{number}" };
        roulette.Items.Add(new RouletteItem { Text = "결과 1", ChancePercent = 50 });
        roulette.Items.Add(new RouletteItem { Text = "결과 2", ChancePercent = 50 });
        _store.Data.Roulettes.Add(roulette);
        RoulettesGrid.SelectedItem = roulette;
    }

    private void DeleteRouletteButton_Click(object sender, RoutedEventArgs e)
    {
        if (RoulettesGrid.SelectedItem is not RouletteDefinition item) return;

        foreach (var rule in _store.Data.DonationRules.Where(x => x.RouletteId == item.Id))
        {
            rule.Enabled = false;
            rule.RouletteId = Guid.Empty;
        }

        _store.Data.Roulettes.Remove(item);
        if (_store.Data.Roulettes.Count > 0) RoulettesGrid.SelectedIndex = 0;
        RefreshRouletteEditor();
    }

    private void AddRouletteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (RoulettesGrid.SelectedItem is not RouletteDefinition roulette) return;
        roulette.Items.Add(new RouletteItem { Text = "새 결과", ChancePercent = 0 });
        RefreshRouletteEditor();
    }

    private void DeleteRouletteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (RoulettesGrid.SelectedItem is not RouletteDefinition roulette
            || RouletteItemsGrid.SelectedItem is not RouletteItem item) return;
        roulette.Items.Remove(item);
        RefreshRouletteEditor();
    }

    private void AddDonationRuleButton_Click(object sender, RoutedEventArgs e)
    {
        _store.Data.DonationRules.Add(new DonationRouletteRule
        {
            MinAmount = 2000,
            Keyword = "애교",
            RouletteId = _store.Data.Roulettes.FirstOrDefault()?.Id ?? Guid.Empty
        });
    }

    private void DeleteDonationRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DonationRulesGrid.SelectedItem is DonationRouletteRule item) _store.Data.DonationRules.Remove(item);
    }

    private void AddRepeatingMessageButton_Click(object sender, RoutedEventArgs e)
        => _store.Data.RepeatingMessages.Add(new RepeatingMessageConfig { Message = "방송 안내 메시지를 입력하세요.", IntervalMinutes = 30 });

    private void DeleteRepeatingMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (RepeatingMessagesGrid.SelectedItem is RepeatingMessageConfig item) _store.Data.RepeatingMessages.Remove(item);
    }

    private void AddCountdownTimerButton_Click(object sender, RoutedEventArgs e)
        => _store.Data.CountdownTimers.Add(new CountdownTimerConfig { Name = "새 타이머", Minutes = 5, FinishMessage = "타이머가 종료되었습니다." });

    private void DeleteCountdownTimerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CountdownTimersGrid.SelectedItem is CountdownTimerConfig item) _store.Data.CountdownTimers.Remove(item);
    }

    private async void StartCountdownTimerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CountdownTimersGrid.SelectedItem is not CountdownTimerConfig item)
        {
            MessageBox.Show("시작할 타이머를 선택하세요.", "PodoBot");
            return;
        }

        try
        {
            CommitGrid(CountdownTimersGrid);
            await _countdown.StartAsync(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "타이머");
        }
    }

    private async void StopCountdownTimerButton_Click(object sender, RoutedEventArgs e)
        => await _countdown.StopAsync();

    private void OnCountdownStatusChanged(string status)
        => Dispatcher.Invoke(() => CountdownStatusText.Text = status);

    private void AddCounterButton_Click(object sender, RoutedEventArgs e)
        => _store.Data.Counters.Add(new CounterConfig { Name = "새 카운터", Trigger = "!카운트", Permission = "매니저" });

    private void DeleteCounterButton_Click(object sender, RoutedEventArgs e)
    {
        if (CountersGrid.SelectedItem is CounterConfig item) _store.Data.Counters.Remove(item);
    }

    private void AddSongButton_Click(object sender, RoutedEventArgs e)
        => _store.Data.Songs.Add(new SongBookEntry { Provider = "TJ", Title = "새 곡" });

    private void DeleteSongButton_Click(object sender, RoutedEventArgs e)
    {
        if (SongBookGrid.SelectedItem is SongBookEntry item) _store.Data.Songs.Remove(item);
    }

    private void PreviewRouletteButton_Click(object sender, RoutedEventArgs e) => OpenUrl(OverlayServer.RouletteUrl);
    private void CopyRouletteButton_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(OverlayServer.RouletteUrl); AppendLog("OBS 룰렛 주소를 복사했습니다."); }
    private void PreviewTimerButton_Click(object sender, RoutedEventArgs e) => OpenUrl(OverlayServer.TimerUrl);
    private void CopyTimerButton_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(OverlayServer.TimerUrl); AppendLog("OBS 타이머 주소를 복사했습니다."); }
    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = _store.DirectoryPath, UseShellExecute = true });
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _session.DisconnectAsync();
        _auth.Logout();
        RefreshHome();
        AppendLog("치지직 연결 정보를 삭제했습니다.");
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        });
    }

    private static void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
}
