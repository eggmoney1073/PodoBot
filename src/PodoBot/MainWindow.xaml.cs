using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
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
    private readonly TimerService _timers;

    private readonly ObservableCollection<BotCommand> _commands;
    private readonly ObservableCollection<RouletteItem> _roulette;
    private readonly ObservableCollection<TimerConfig> _timerConfigs;
    private readonly ObservableCollection<CounterConfig> _counters;

    private bool _loginInProgress;

    public MainWindow()
    {
        InitializeComponent();

        _store = new LocalDataStore();
        _secureTokens = new SecureTokenStore(_store.DirectoryPath);
        _api = new ChzzkApiClient();
        _auth = new AuthManager(_api, _secureTokens);
        _session = new ChzzkSessionClient(_api);
        _overlay = new OverlayServer(_store);

        _commands = new ObservableCollection<BotCommand>(_store.Data.Commands);
        _roulette = new ObservableCollection<RouletteItem>(_store.Data.RouletteItems);
        _timerConfigs = new ObservableCollection<TimerConfig>(_store.Data.Timers);
        _counters = new ObservableCollection<CounterConfig>(_store.Data.Counters);

        CommandsGrid.ItemsSource = _commands;
        RouletteGrid.ItemsSource = _roulette;
        TimersGrid.ItemsSource = _timerConfigs;
        CountersGrid.ItemsSource = _counters;

        RouletteTriggerTextBox.Text = _store.Data.Roulette.Trigger;

        _bot = new BotEngine(_store, SendChatAsync, _overlay.PublishRouletteAsync);
        _timers = new TimerService(_store, SendChatAsync);

        _overlay.AuthorizationHandler = OnAuthorizationAsync;
        _overlay.Log += AppendLog;
        _session.Log += AppendLog;
        _session.ConnectionChanged += OnConnectionChanged;
        _session.ChatReceived += _bot.ProcessAsync;
        _bot.Log += AppendLog;
        _timers.Log += AppendLog;

        Loaded += Window_Loaded;
        Closing += Window_Closing;

        RefreshHome();
        RefreshRouletteTotal();
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
            await _timers.DisposeAsync();
            await _session.DisposeAsync();
            await _overlay.DisposeAsync();
        }
        catch
        {
        }
    }

    private async void MainConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsConnected)
            return;

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
            MessageBox.Show(
                ex.Message,
                "PodoBot",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            MainConnectButton.IsEnabled = true;
            MainConnectButton.Content = "방송봇 켜기";
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _session.DisconnectAsync();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        StartLogin();
    }

    private void StartLogin()
    {
        try
        {
            var url = _auth.CreateLoginUrl();
            _loginInProgress = true;
            OpenUrl(url);
            AppendLog("브라우저에서 치지직 연결을 승인해 주세요.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "치지직 연결",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async Task OnAuthorizationAsync(string code, string state)
    {
        await _auth.HandleCodeAsync(code, state);

        await Dispatcher.InvokeAsync(async () =>
        {
            _loginInProgress = false;
            RefreshHome();
            AppendLog($"치지직 로그인 완료: {_auth.Tokens.ChannelName}");

            try
            {
                var token = await _auth.GetAccessTokenAsync();
                await _session.ConnectAsync(token);
            }
            catch (Exception ex)
            {
                AppendLog($"자동 연결 실패: {ex.Message}");
            }
        });
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
            DisconnectButton.Visibility = connected
                ? Visibility.Visible
                : Visibility.Collapsed;

            MainConnectButton.Visibility = connected
                ? Visibility.Collapsed
                : Visibility.Visible;

            MainConnectButton.IsEnabled = true;
            MainConnectButton.Content = string.IsNullOrWhiteSpace(_auth.Tokens.RefreshToken)
                ? "치지직 연결하기"
                : "방송봇 켜기";

            if (connected)
            {
                _timers.Start();
                WelcomeTitle.Text = "방송 준비 완료";
                WelcomeBody.Text = "PodoBot이 채팅을 보고 있어요. 방송만 시작하면 됩니다.";
            }
            else
            {
                _ = _timers.StopAsync();
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
            WelcomeTitle.Text = loggedIn
                ? "방송봇을 켜 주세요"
                : "치지직을 연결해 주세요";

            WelcomeBody.Text = loggedIn
                ? "버튼 한 번이면 명령어, 룰렛, 타이머가 모두 시작됩니다."
                : "처음 한 번만 로그인하면 다음부터는 바로 방송봇을 켤 수 있어요.";

            MainConnectButton.Content = loggedIn
                ? "방송봇 켜기"
                : "치지직 연결하기";
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
        CommandsGrid.CommitEdit();
        RouletteGrid.CommitEdit();
        TimersGrid.CommitEdit();
        CountersGrid.CommitEdit();

        _store.Data.Commands = _commands.ToList();
        _store.Data.RouletteItems = _roulette.ToList();
        _store.Data.Timers = _timerConfigs.ToList();
        _store.Data.Counters = _counters.ToList();
        _store.Data.Roulette.Trigger = RouletteTriggerTextBox.Text.Trim();

        var total = _roulette.Sum(x => x.ChancePercent);

        if (validateRoulette
            && _roulette.Count > 0
            && Math.Abs(total - 100) > 0.001)
        {
            throw new InvalidOperationException(
                $"룰렛 확률 합계가 {total:0.###}%입니다. 100%로 맞춰 주세요.");
        }

        await _store.SaveAsync();
        _timers.Reset();
        RefreshRouletteTotal();
    }

    private void RefreshRouletteTotal()
    {
        var total = _roulette.Sum(x => x.ChancePercent);
        RouletteTotalText.Text = $"현재 확률 합계 {total:0.###}%";
        RouletteTotalText.Foreground =
            Math.Abs(total - 100) <= 0.001
                ? Brushes.SeaGreen
                : Brushes.IndianRed;
    }

    private void AddCommandButton_Click(object sender, RoutedEventArgs e)
    {
        _commands.Add(new BotCommand
        {
            Trigger = "!명령어",
            Response = "답변 내용을 입력하세요.",
            Permission = "전체"
        });
    }

    private void DeleteCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsGrid.SelectedItem is BotCommand item)
            _commands.Remove(item);
    }

    private void AddRouletteButton_Click(object sender, RoutedEventArgs e)
    {
        _roulette.Add(new RouletteItem { Text = "새 항목", ChancePercent = 0 });
        RefreshRouletteTotal();
    }

    private void DeleteRouletteButton_Click(object sender, RoutedEventArgs e)
    {
        if (RouletteGrid.SelectedItem is RouletteItem item)
            _roulette.Remove(item);

        RefreshRouletteTotal();
    }

    private void AddTimerButton_Click(object sender, RoutedEventArgs e)
    {
        _timerConfigs.Add(new TimerConfig
        {
            Message = "방송 안내 메시지를 입력하세요.",
            IntervalMinutes = 30
        });
    }

    private void DeleteTimerButton_Click(object sender, RoutedEventArgs e)
    {
        if (TimersGrid.SelectedItem is TimerConfig item)
            _timerConfigs.Remove(item);
    }

    private void AddCounterButton_Click(object sender, RoutedEventArgs e)
    {
        _counters.Add(new CounterConfig
        {
            Name = "새 카운터",
            Trigger = "!카운트",
            Permission = "매니저"
        });
    }

    private void DeleteCounterButton_Click(object sender, RoutedEventArgs e)
    {
        if (CountersGrid.SelectedItem is CounterConfig item)
            _counters.Remove(item);
    }

    private void PreviewOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(OverlayServer.OverlayUrl);
    }

    private void CopyOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(OverlayServer.OverlayUrl);
        AppendLog("OBS 룰렛 주소를 복사했습니다.");
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _store.DirectoryPath,
            UseShellExecute = true
        });
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
            LogTextBox.AppendText(
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        });
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
