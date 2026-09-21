using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PotatoAgent.Core.Brain;

namespace GUI.ViewModels;

/// <summary>
/// 设置页的 ViewModel：多档案（profile）的读取 / 编辑 / 保存，以及"测试连接"。
/// </summary>
/// <remarks>
/// <para>
/// <b>界面怎么接</b>：把本对象设成设置页的 <c>DataContext</c>，然后
/// <c>TextBox.Text="{Binding BaseUrl, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"</c>、
/// <c>Button.Command="{Binding SaveCommand}"</c> 这样绑即可，XAML 里不需要任何逻辑。
/// </para>
/// <para>
/// <b>密钥规则（重要）</b>：
/// <list type="number">
/// <item><see cref="ApiKeyInput"/> 载入时<b>永远是空串</b> —— 绝不把已存的明文密钥回显到输入框。</item>
/// <item><see cref="ApiKeyMasked"/> 只给"看状态"用（例如 <c>sk-1****cdef</c>），可以随便绑。</item>
/// <item>保存时：输入框有字 → 用新密钥；输入框空且没点"清除密钥" → <b>保持原密钥不变</b>；
/// 点了 <see cref="ClearApiKeyCommand"/> → 写空，等于删掉密钥。</item>
/// <item>明文只在内存里，落盘由 <see cref="ConfigStore"/> 做 DPAPI 加密。</item>
/// </list>
/// </para>
/// <para><b>不抛异常</b>：所有网络/磁盘失败都会被翻成 <see cref="StatusMessage"/> 上的一句人话。</para>
/// </remarks>
public sealed class SettingsViewModel : ObservableObject
{
    private const double DefaultTemperature = 0.7;
    private const int DefaultMaxTokens = 2048;
    private const int ProbeTimeoutSeconds = 20;

    private readonly ConfigStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds) };

    private string _profileName = "default";
    private string? _selectedProfileName;
    private string _baseUrl = string.Empty;
    private string _apiKeyInput = string.Empty;
    private bool _pendingApiKeyClear;
    private string _apiKeyMasked = "(no key stored)";
    private bool _hasStoredApiKey;
    private string _model = string.Empty;
    private string? _selectedModelSuggestion;
    private string _temperatureText = DefaultTemperature.ToString(CultureInfo.InvariantCulture);
    private string _maxTokensText = DefaultMaxTokens.ToString(CultureInfo.InvariantCulture);
    private string _endpointPreview = "(enter a base URL to see the request URLs)";
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private bool? _lastTestSucceeded;
    private bool _suppressProfileReload;

    /// <summary>建一个设置页 ViewModel。构造时会把盘上的配置读进表单。</summary>
    /// <param name="store">配置仓库（<c>%APPDATA%\PotatoAgent\config.json</c>）。</param>
    public SettingsViewModel(ConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        LoadCommand = new RelayCommand(Load);
        SaveCommand = new RelayCommand(Save);
        NewProfileCommand = new RelayCommand(NewProfile);
        DeleteProfileCommand = new RelayCommand(DeleteSelectedProfile, () => SelectedProfileName is not null && !IsBusy);
        ClearApiKeyCommand = new RelayCommand(ClearApiKey, () => !IsBusy);
        TestConnectionCommand = new AsyncRelayCommand(
            TestConnectionAsync,
            () => !IsBusy,
            ex => StatusMessage = "Test failed unexpectedly: " + Describe(ex));

        Load();
    }

    /// <summary>保存成功后触发。界面可以挂它做三件事：清空密码框、提示用户、让 Chat 页重建 Provider。</summary>
    public event EventHandler? Saved;

    // ==================== 可绑定属性：档案（profile） ====================

    /// <summary>全部档案名。类型 <see cref="ObservableCollection{T}"/>（<see cref="string"/>），只读，加载/保存后自动刷新。</summary>
    public ObservableCollection<string> Profiles { get; } = new();

    /// <summary>
    /// 当前选中的档案名。类型 <see cref="string?"/>，双向绑定（<c>ListBox.SelectedItem</c> / <c>ComboBox.SelectedItem</c>）。
    /// 赋值后会立刻把该档案的内容读进下面那些编辑框。
    /// </summary>
    public string? SelectedProfileName
    {
        get => _selectedProfileName;
        set
        {
            if (!SetProperty(ref _selectedProfileName, value))
            {
                return;
            }

            RefreshCommandStates();

            if (!_suppressProfileReload)
            {
                LoadProfileIntoForm(value);
            }
        }
    }

    /// <summary>
    /// 编辑框里的档案名。类型 <see cref="string"/>，双向绑定（<c>TextBox</c>）。
    /// 保存时按这个名字新增/覆盖；改了名字就是"另存为新档案"（旧的那份不会被删）。
    /// </summary>
    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    // ==================== 可绑定属性：连接参数 ====================

    /// <summary>
    /// 接口根地址，例如 <c>https://api.deepseek.com</c>。类型 <see cref="string"/>，双向绑定（<c>TextBox</c>）。
    /// 末尾带不带 <c>/</c>、带不带 <c>/v1</c> 都行，Core 会自己补全。
    /// </summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
            {
                RecomputeEndpointPreview();
            }
        }
    }

    /// <summary>
    /// 用户新输入的密钥明文。类型 <see cref="string"/>，双向绑定。
    /// <b>读出来是空串</b>（除非用户真的刚敲了字），所以可以安全地绑到一个 <c>PasswordBox</c>
    /// （靠 <c>PasswordChanged</c> 事件把值推进来，WPF 的 <c>PasswordBox.Password</c> 本身不可绑定）。
    /// </summary>
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set => SetProperty(ref _apiKeyInput, value);
    }

    /// <summary>
    /// 密钥状态的掩码文本（例如 <c>stored (encrypted): sk-1****cdef</c> / <c>(no key stored)</c>）。
    /// 类型 <see cref="string"/>，只读。可以安全地绑到 <c>TextBlock</c>。
    /// </summary>
    public string ApiKeyMasked
    {
        get => _apiKeyMasked;
        private set => SetProperty(ref _apiKeyMasked, value);
    }

    /// <summary>盘上/内存里是否已经有一把能用的密钥。类型 <see cref="bool"/>，只读。</summary>
    public bool HasStoredApiKey
    {
        get => _hasStoredApiKey;
        private set => SetProperty(ref _hasStoredApiKey, value);
    }

    /// <summary>是否已点了"清除密钥"（保存后生效）。类型 <see cref="bool"/>，只读。</summary>
    public bool PendingApiKeyClear
    {
        get => _pendingApiKeyClear;
        private set => SetProperty(ref _pendingApiKeyClear, value);
    }

    /// <summary>
    /// 模型名，例如 <c>deepseek-chat</c>。类型 <see cref="string"/>，双向绑定（<c>TextBox</c>）。
    /// </summary>
    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    /// <summary>
    /// 采样温度文本（<c>0</c>~<c>2</c>；<b>留空 = 不发给服务端</b>，用服务端默认值）。
    /// 类型 <see cref="string"/>，双向绑定。字符串存是为了让你输错时能报错而不是静默变成 0。
    /// </summary>
    public string TemperatureText
    {
        get => _temperatureText;
        set => SetProperty(ref _temperatureText, value);
    }

    /// <summary>
    /// 单次回复 token 上限文本（正整数；<b>留空 = 不发给服务端</b>）。类型 <see cref="string"/>，双向绑定。
    /// </summary>
    public string MaxTokensText
    {
        get => _maxTokensText;
        set => SetProperty(ref _maxTokensText, value);
    }

    /// <summary>温度解析结果（只在界面上显示"最终会发什么"时用）。类型 <see cref="double?"/>，只读。</summary>
    public double? Temperature => ParseTemperature(TemperatureText, out _);

    /// <summary>token 上限解析结果。类型 <see cref="int?"/>，只读。</summary>
    public int? MaxTokens => ParseMaxTokens(MaxTokensText, out _);

    // ==================== 可绑定属性：测试与提示 ====================

    /// <summary>
    /// "测试连接"拉回来的模型名列表。类型 <see cref="ObservableCollection{T}"/>（<see cref="string"/>），只读。
    /// 服务端不支持 <c>GET /v1/models</c> 时保持为空，不算失败。
    /// </summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>
    /// 用户在模型列表里点了哪一项。类型 <see cref="string?"/>，双向绑定（<c>ListBox.SelectedItem</c>）。
    /// 赋值会把 <see cref="Model"/> 一起改掉，省得手抄。
    /// </summary>
    public string? SelectedModelSuggestion
    {
        get => _selectedModelSuggestion;
        set
        {
            if (SetProperty(ref _selectedModelSuggestion, value) && !string.IsNullOrWhiteSpace(value))
            {
                Model = value!;
            }
        }
    }

    /// <summary>
    /// 两个真实请求地址的预览（POST chat / GET models）。类型 <see cref="string"/>，只读，改 <see cref="BaseUrl"/> 时自动更新。
    /// </summary>
    public string EndpointPreview
    {
        get => _endpointPreview;
        private set => SetProperty(ref _endpointPreview, value);
    }

    /// <summary>
    /// 一句话状态 / 错误提示（英文，永远可读）。类型 <see cref="string"/>，只读。
    /// 加载、保存、测试连接的结果都写在这里，界面绑一个 <c>TextBlock</c> 就行。
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>正在跑（测试连接期间为 true）。类型 <see cref="bool"/>，只读，可用来显示进度。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommandStates();
            }
        }
    }

    /// <summary>
    /// 上次测试连接的结果：true 成功 / false 失败 / null 还没测过。
    /// 类型 <see cref="bool?"/>，只读。绑 <c>TextBlock</c> 的 Foreground 触发器正好。
    /// </summary>
    public bool? LastTestSucceeded
    {
        get => _lastTestSucceeded;
        private set => SetProperty(ref _lastTestSucceeded, value);
    }

    /// <summary>配置文件完整路径（给用户核对"到底存哪了"）。类型 <see cref="string"/>，只读。</summary>
    public string ConfigFilePath => _store.FilePath;

    // ==================== 命令 ====================

    /// <summary>命令：重新从磁盘读配置，刷新档案列表与全部编辑框。类型 <see cref="System.Windows.Input.ICommand"/>。</summary>
    public RelayCommand LoadCommand { get; }

    /// <summary>
    /// 命令：把编辑框里的内容写进 <see cref="ConfigStore"/> 并落盘（密钥 DPAPI 加密）。
    /// 成功后会触发 <see cref="Saved"/> 事件，并把 <see cref="ApiKeyInput"/> 清空。
    /// </summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>异步命令：测一次连接。先发 <c>GET /v1/models</c>（不花 token），不行就发一条最短的对话请求兜底。</summary>
    public AsyncRelayCommand TestConnectionCommand { get; }

    /// <summary>命令：清空表单，准备填一套新档案（不写盘，按"保存"才落盘）。</summary>
    public RelayCommand NewProfileCommand { get; }

    /// <summary>命令：删掉当前选中的档案（立即写盘）。没有任何档案时不能点。</summary>
    public RelayCommand DeleteProfileCommand { get; }

    /// <summary>命令：删掉已存的密钥（下次"保存"生效，<see cref="PendingApiKeyClear"/> 会变 true）。</summary>
    public RelayCommand ClearApiKeyCommand { get; }

    // ==================== 加载 / 保存 ====================

    /// <summary>读盘 → 刷档案列表 → 把当前档案填进表单。</summary>
    private void Load()
    {
        try
        {
            var config = _store.Load();

            _suppressProfileReload = true;
            Profiles.Clear();
            foreach (var profile in config.Profiles)
            {
                Profiles.Add(profile.Name);
            }

            var active = config.ActiveProfile;
            _selectedProfileName = active?.Name;
            OnPropertyChanged(nameof(SelectedProfileName));
            _suppressProfileReload = false;

            LoadProfileIntoForm(active?.Name);

            StatusMessage = _store.LastLoadStatus switch
            {
                ConfigLoadStatus.Ok => $"Loaded {Profiles.Count} profile(s) from {_store.FilePath}.",
                ConfigLoadStatus.Missing => $"No config file yet ({_store.FilePath}). Fill the form and press Save.",
                ConfigLoadStatus.Corrupt => $"Config file is not valid JSON - starting from an empty form (the file was NOT overwritten). {_store.LastLoadError}",
                ConfigLoadStatus.KeyUndecryptable => $"Loaded, but the stored API key cannot be decrypted on this machine/user - please type it again. {_store.LastLoadError}",
                ConfigLoadStatus.Unreadable => $"Config file could not be read (permissions?). {_store.LastLoadError}",
                _ => string.Empty,
            };
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not load settings: " + Describe(ex);
        }
    }

    /// <summary>把某套档案读进表单；名字为 null / 找不到就清空表单。</summary>
    private void LoadProfileIntoForm(string? name)
    {
        var profile = _store.Current.Find(name) ?? (string.IsNullOrEmpty(name) ? _store.Current.Profiles.FirstOrDefault() : null);

        if (profile is null)
        {
            ProfileName = string.IsNullOrWhiteSpace(name) ? "default" : name!;
            BaseUrl = string.Empty;
            Model = string.Empty;
            TemperatureText = DefaultTemperature.ToString(CultureInfo.InvariantCulture);
            MaxTokensText = DefaultMaxTokens.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            ProfileName = profile.Name;
            BaseUrl = profile.BaseUrl;
            Model = profile.Model;
            TemperatureText = profile.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            MaxTokensText = profile.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        // 关键：表单里的密钥输入框永远从空开始，不回显已存密钥。
        ApiKeyInput = string.Empty;
        PendingApiKeyClear = false;
        RefreshKeyState(profile);
        AvailableModels.Clear();
        SelectedModelSuggestion = null;
    }

    private void RefreshKeyState(ProviderProfile? profile)
    {
        HasStoredApiKey = profile?.HasApiKey ?? false;
        ApiKeyMasked = profile is null
            ? "(no profile)"
            : PendingApiKeyClear
                ? "will be cleared on Save"
                : profile.HasApiKey
                    ? "stored (DPAPI encrypted): " + ProviderProfile.Mask(profile.ApiKey)
                    : profile.HasUndecryptableKey
                        ? "stored but NOT decryptable on this machine/user - type it again"
                        : "(no key stored)";
    }

    private void Save()
    {
        try
        {
            var draft = BuildProfile(includeStoredKey: false, out var error);
            if (draft is null)
            {
                LastTestSucceeded = null;
                StatusMessage = "Not saved: " + error;
                return;
            }

            var stored = _store.UpsertProfile(draft);
            _store.Save();

            // 保存成功后：列表刷新、密钥状态刷新、输入框清空（明文不留在界面上）。
            _suppressProfileReload = true;
            if (!Profiles.Contains(stored.Name))
            {
                Profiles.Add(stored.Name);
            }

            _selectedProfileName = stored.Name;
            OnPropertyChanged(nameof(SelectedProfileName));
            _suppressProfileReload = false;

            ApiKeyInput = string.Empty;
            PendingApiKeyClear = false;
            RefreshKeyState(stored);

            if (string.IsNullOrWhiteSpace(stored.Model))
            {
                StatusMessage = $"Saved to {_store.FilePath}. Warning: model name is empty, so chatting will fail until you fill it.";
            }
            else if (!stored.HasApiKey)
            {
                StatusMessage = $"Saved to {_store.FilePath} (key: encrypted). Warning: no API key stored yet.";
            }
            else
            {
                StatusMessage = $"Saved to {_store.FilePath}. API key stored DPAPI-encrypted (never in plaintext).";
            }

            LastTestSucceeded = null;
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 磁盘满了、文件被占用、或者 Core 的"明文密钥自检"拒绝写入，都在这里变成一句提示。
            StatusMessage = "Save failed: " + Describe(ex);
        }
    }

    private void NewProfile()
    {
        var index = 1;
        string name;
        do
        {
            name = $"profile{++index}";
        }
        while (Profiles.Contains(name));

        _suppressProfileReload = true;
        _selectedProfileName = null;
        OnPropertyChanged(nameof(SelectedProfileName));
        _suppressProfileReload = false;

        ProfileName = name;
        BaseUrl = string.Empty;
        Model = string.Empty;
        TemperatureText = DefaultTemperature.ToString(CultureInfo.InvariantCulture);
        MaxTokensText = DefaultMaxTokens.ToString(CultureInfo.InvariantCulture);
        ApiKeyInput = string.Empty;
        PendingApiKeyClear = false;
        RefreshKeyState(null);
        AvailableModels.Clear();
        SelectedModelSuggestion = null;
        LastTestSucceeded = null;
        StatusMessage = $"New profile '{name}' - fill it in and press Save.";
        RefreshCommandStates();
    }

    private void DeleteSelectedProfile()
    {
        var name = SelectedProfileName;
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        try
        {
            if (!_store.RemoveProfile(name))
            {
                StatusMessage = $"Profile '{name}' no longer exists.";
                return;
            }

            _store.Save();

            _suppressProfileReload = true;
            Profiles.Remove(name);
            _selectedProfileName = Profiles.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedProfileName));
            _suppressProfileReload = false;

            LoadProfileIntoForm(_selectedProfileName);
            LastTestSucceeded = null;
            StatusMessage = $"Deleted profile '{name}' and saved.";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = "Delete failed: " + Describe(ex);
        }
    }

    private void ClearApiKey()
    {
        PendingApiKeyClear = true;
        ApiKeyInput = string.Empty;
        RefreshKeyState(_store.Current.Find(SelectedProfileName));
        StatusMessage = "The stored API key will be removed when you press Save.";
    }

    // ==================== 测试连接 ====================

    /// <summary>
    /// 测一次连接：先 <c>GET {base}/v1/models</c>（不消耗 token），不认这个端点的服务端再用
    /// 一条 <c>max_tokens=1</c> 的最短对话请求兜底。整个过程不抛异常，结果写进 <see cref="StatusMessage"/>。
    /// </summary>
    private async Task TestConnectionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var draft = BuildProfile(includeStoredKey: true, out var buildError);
        if (draft is null)
        {
            LastTestSucceeded = false;
            StatusMessage = "Test not started: " + buildError;
            return;
        }

        IsBusy = true;
        LastTestSucceeded = null;
        AvailableModels.Clear();
        StatusMessage = "Testing...";
        using var cts = new CancellationTokenSource();

        try
        {
            var modelsUri = OpenAiProvider.BuildModelsUri(draft.BaseUrl);
            var modelsFailure = await TryListModelsAsync(modelsUri, draft.ApiKey, cts.Token).ConfigureAwait(true);

            if (modelsFailure is null)
            {
                LastTestSucceeded = true;
                return;
            }

            // 服务端可能根本没有 /v1/models（野路子兼容实现很常见），发一条最短对话请求兜底。
            StatusMessage = $"GET /v1/models failed ({modelsFailure}); trying one minimal chat request...";

            var endpoint = OpenAiProvider.BuildChatCompletionsUri(draft.BaseUrl);
            var probe = draft.Clone();
            probe.MaxTokens = 1;
            probe.Temperature = 0;

            using var provider = new OpenAiProvider(probe);
            var started = DateTime.UtcNow;
            var reply = await provider
                .CompleteTextAsync(new[] { ChatMessage.User("ping") }, null, cts.Token)
                .ConfigureAwait(true);
            var elapsed = DateTime.UtcNow - started;

            LastTestSucceeded = true;
            StatusMessage =
                $"OK - POST {endpoint} answered in {elapsed.TotalMilliseconds:0} ms ({reply.Length} chars). " +
                $"Note: GET /v1/models was not usable: {modelsFailure}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LastTestSucceeded = false;
            StatusMessage = "Test cancelled.";
        }
        catch (TaskCanceledException)
        {
            LastTestSucceeded = false;
            StatusMessage = $"FAILED - the request timed out after {ProbeTimeoutSeconds} s. " +
                            "Check that the base URL is reachable from this machine.";
        }
        catch (ProviderException ex)
        {
            LastTestSucceeded = false;
            StatusMessage = "FAILED - " + Describe(ex);
        }
        catch (Exception ex)
        {
            LastTestSucceeded = false;
            StatusMessage = "FAILED - " + Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 探 <c>GET /v1/models</c>。成功返回 null（并填好 <see cref="AvailableModels"/>），
    /// 失败返回一句人话。<b>不抛异常</b>。
    /// </summary>
    /// <remarks>
    /// 这里故意自己发 <see cref="HttpClient"/> 请求、不走 <see cref="OpenAiProvider.ListModelsAsync"/> ——
    /// 那个方法是 Core 里的预留桩，调了会抛 <see cref="NotImplementedException"/>；
    /// 而且这个端点不消耗 token，最适合做"连通性 + 鉴权"体检。
    /// </remarks>
    private async Task<string?> TryListModelsAsync(Uri modelsUri, string? apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
            {
                return DescribeHttpFailure(response.StatusCode, body, modelsUri);
            }

            var ids = ParseModelIds(body);
            foreach (var id in ids)
            {
                AvailableModels.Add(id);
            }

            if (ids.Count > 0 && string.IsNullOrWhiteSpace(Model))
            {
                Model = ids[0];
            }

            var preview = ids.Count == 0
                ? "(the response had no model list)"
                : string.Join(", ", ids.Take(5)) + (ids.Count > 5 ? ", ..." : string.Empty);

            StatusMessage = $"OK - GET {modelsUri} returned 200 with {ids.Count} model(s): {preview}";
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return $"no answer within {ProbeTimeoutSeconds} s (timeout). Is {modelsUri.Host} reachable?";
        }
        catch (HttpRequestException ex)
        {
            return $"cannot connect to {modelsUri.Host}:{modelsUri.Port} - {Innermost(ex)}";
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    // ==================== 内部工具 ====================

    /// <summary>
    /// 用表单里的值拼一套档案。
    /// <paramref name="includeStoredKey"/> = true（测试用）时带上已存的密钥；false（保存用）时，
    /// 输入框为空且没点清除就传 null，表示"这次不动密钥"（<see cref="AppConfig.AddOrUpdate"/> 的约定）。
    /// </summary>
    private ProviderProfile? BuildProfile(bool includeStoredKey, out string? error)
    {
        error = null;

        var name = string.IsNullOrWhiteSpace(ProfileName) ? "default" : ProfileName.Trim();
        var baseUrl = (BaseUrl ?? string.Empty).Trim();

        if (baseUrl.Length == 0)
        {
            error = "Base URL is empty. Example: https://api.deepseek.com";
            return null;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"Base URL '{baseUrl}' is not a valid absolute http(s) URL.";
            return null;
        }

        var temperature = ParseTemperature(TemperatureText, out var temperatureError);
        if (temperatureError is not null)
        {
            error = temperatureError;
            return null;
        }

        var maxTokens = ParseMaxTokens(MaxTokensText, out var maxTokensError);
        if (maxTokensError is not null)
        {
            error = maxTokensError;
            return null;
        }

        string? apiKey;
        var typed = (ApiKeyInput ?? string.Empty).Trim();
        if (typed.Length > 0)
        {
            apiKey = typed;
        }
        else if (PendingApiKeyClear)
        {
            apiKey = string.Empty;   // 显式清空：Core 会把密文也删掉
        }
        else
        {
            apiKey = includeStoredKey
                ? _store.Current.Find(SelectedProfileName ?? name)?.ApiKey ?? _store.Current.Find(name)?.ApiKey
                : null;              // null = 保持盘上原有的密钥不动
        }

        return new ProviderProfile
        {
            Name = name,
            BaseUrl = baseUrl,
            Model = (Model ?? string.Empty).Trim(),
            Temperature = temperature,
            MaxTokens = maxTokens,

            // 推理强度不在设置页里编辑（它在聊天页顶上的工具条上切），保存时原样带回去，
            // 否则 AddOrUpdate 会把这个字段清成 null，用户刚切好的档位会莫名其妙丢回"服务端默认"。
            ReasoningEffort = _store.Current.Find(name)?.ReasoningEffort,

            ApiKey = apiKey,
        };
    }

    private static double? ParseTemperature(string? text, out string? error)
    {
        error = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;   // 留空 = 不发给服务端
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            error = $"Temperature '{trimmed}' is not a number (leave it empty to use the server default).";
            return null;
        }

        if (value < 0 || value > 2)
        {
            error = $"Temperature {value.ToString(CultureInfo.InvariantCulture)} is out of range (0 ~ 2).";
            return null;
        }

        return value;
    }

    private static int? ParseMaxTokens(string? text, out string? error)
    {
        error = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            error = $"Max tokens '{trimmed}' is not an integer (leave it empty to use the server default).";
            return null;
        }

        if (value <= 0)
        {
            error = $"Max tokens {value} must be greater than 0 (leave it empty to use the server default).";
            return null;
        }

        return value;
    }

    private void RecomputeEndpointPreview()
    {
        try
        {
            var chat = OpenAiProvider.BuildChatCompletionsUri(BaseUrl);
            var models = OpenAiProvider.BuildModelsUri(BaseUrl);
            EndpointPreview = $"POST {chat}{Environment.NewLine}GET  {models}";
        }
        catch (Exception)
        {
            EndpointPreview = "(enter a base URL to see the request URLs)";
        }
    }

    private void RefreshCommandStates()
    {
        DeleteProfileCommand.RaiseCanExecuteChanged();
        ClearApiKeyCommand.RaiseCanExecuteChanged();
        TestConnectionCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        NewProfileCommand.RaiseCanExecuteChanged();
        LoadCommand.RaiseCanExecuteChanged();
    }

    private static List<string> ParseModelIds(string body)
    {
        var ids = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        ids.Add(value!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 服务端吐了非 JSON 的东西：不算失败，只是拿不到模型名。
        }

        return ids;
    }

    /// <summary>HTTP 非 2xx → 一句能照着修的提示（不带密钥）。</summary>
    private static string DescribeHttpFailure(HttpStatusCode status, string body, Uri uri)
    {
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => "the server rejected the API key (check the key).",
            HttpStatusCode.Forbidden => "the key is valid but has no access to this endpoint.",
            HttpStatusCode.NotFound => "404 - wrong path, or this server has no /v1/models endpoint.",
            HttpStatusCode.MethodNotAllowed => "405 - this server does not allow GET /v1/models.",
            HttpStatusCode.TooManyRequests => "429 - rate limited or out of quota.",
            _ => status >= HttpStatusCode.InternalServerError
                ? "server-side error - try again later."
                : "unexpected status.",
        };

        var detail = Truncate(body, 200);
        return $"HTTP {(int)status} ({status}) from {uri} - {hint}" + (detail.Length > 0 ? $" Body: {detail}" : string.Empty);
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException is not null)
        {
            text += $" ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})";
        }

        return text;
    }

    private static string Innermost(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    private static string Truncate(string? text, int max)
    {
        var value = (text ?? string.Empty).Replace("\r", " ").Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }
}
