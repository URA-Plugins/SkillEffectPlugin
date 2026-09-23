using Gallop;
using Gallop.Endpoints;
using System.Drawing;
using System.Text.Json;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;

var originalCwd = Directory.GetCurrentDirectory();
var workspace = Path.Combine(Path.GetTempPath(), "skill-effect-plugin-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    Directory.SetCurrentDirectory(workspace);
    InitializeHostConfigForSmoke();
    InitializeHostDatabaseForSmoke();
    AssertConfigDraftSaveAndCancelSemantics();
    using var ui = new WorkspaceSmokeSession();
    AssertDefaultSettingsSkipStartupUpdate(ui);
    AssertStartupUpdateUsesCancellationToken(ui);
    PreparePluginData();

    var plugin = new SkillEffectPlugin.SkillEffectPlugin();
    var context = new SmokePluginContext(ui.Application);
    Workspace? target = null;
    try
    {
        plugin.Initialize(context);
        AssertTrue(
            ReferenceEquals(Workspace.Current, ui.Bootstrap),
            "Initialize must keep the bootstrap workspace active.");

        AssertTrue(plugin.Effects.TryGetValue("skill-one", out var effect), "Lowercase effect JSON name was not loaded.");
        AssertNearly(12.3, effect, "Effect parser must keep the historical first-four-character value.");

        plugin.Analyze(CreateTerminalResponse()).GetAwaiter().GetResult();

        target = Workspace.Create("技能收益");
        var screen = ui.CaptureScreen();
        AssertTrue(
            screen.Contains("技能收益", StringComparison.Ordinal)
            && screen.Contains("技能", StringComparison.Ordinal),
            "SkillEffectPlugin framebuffer must include its panel title and column heading.");
        AssertTrue(ReferenceEquals(target, Workspace.Current), "Publishing the panel must focus its workspace.");
        AssertTrue(
            !new[] { "TRACE ", "INFO ", "OK ", "WARN ", "ERROR " }
                .Any(screen.Contains),
            "SkillEffectPlugin must not render a notification overlay.");
        plugin.Analyze(CreateTerminalResponse()).GetAwaiter().GetResult();
        AssertTrue(
            ReferenceEquals(Workspace.Create("技能收益"), target),
            "Repeated output must reuse the skill-effects workspace.");
    }
    finally
    {
        plugin.DisposeAsync().GetAwaiter().GetResult();
    }

    var retained = Workspace.Create("技能收益");
    AssertTrue(
        ReferenceEquals(retained, target),
        "Dispose must retain the original canonical Workspace generation.");
    retained.SwitchTo();
    AssertTrue(
        !ui.CaptureScreen().Contains("技能点性价比", StringComparison.Ordinal),
        "Dispose must remove the skill-effects panel key.");
}
finally
{
    Directory.SetCurrentDirectory(originalCwd);
    if (Directory.Exists(workspace))
        Directory.Delete(workspace, recursive: true);
}

static SingleModeCheckEventResponse CreateTerminalResponse() => new()
{
    data = new()
    {
        chara_info = new()
        {
            state = 2,
            skill_array = [],
            skill_tips_array = []
        },
        unchecked_event_array = []
    }
};

static void InitializeHostConfigForSmoke()
{
    File.WriteAllText(
        "config.yaml",
        """
        core:
          listen-address: 127.0.0.1
          listen-port: 4693
          show-first-run-prompt: false
        repository:
          targets: []
        plugin: {}
        updater:
          is-github-blocked: false
          trainer-is-male: true
          database-language: ja-JP
          custom-database-repository: ''
          force-use-github-to-update: false
        language:
          selected: SimplifiedChinese
        misc:
          save-response-for-debug: false
        workspace-taskbar-title-order: []
        """);
    UmamusumeResponseAnalyzer.Config.Initialize();
    AssertTrue(
        !UmamusumeResponseAnalyzer.Config.Core.ShowFirstRunPrompt,
        "Host must load the strict smoke config from the isolated working directory.");
}

static void InitializeHostDatabaseForSmoke()
{
    foreach (var (path, json) in new[]
    {
        ("events_male.br", "[]"),
        ("names.br", "[]"),
        ("skill_data.br", "[]"),
        ("skill_upgrade_speciality.br", "[]"),
        ("talent_skill_sets.br", "{}"),
        ("factor_ids.br", "{}"),
        ("wins_saddle.br", "[]"),
        ("succession_relation.br", "{}")
    })
    {
        using var output = File.Create(path);
        using var brotli = new System.IO.Compression.BrotliStream(
            output,
            System.IO.Compression.CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(brotli);
        writer.Write(json);
    }

    AssertEqual(
        UmamusumeResponseAnalyzer.DatabaseAvailability.Ready,
        UmamusumeResponseAnalyzer.Database.Initialize().GetAwaiter().GetResult(),
        "Host database fixture must load through the public initialization path.");
}

Console.WriteLine("PASS SkillEffectPlugin smoke");

static void AssertDefaultSettingsSkipStartupUpdate(WorkspaceSmokeSession ui)
{
    var plugin = new SkillEffectPlugin.SkillEffectPlugin();
    var context = new SmokePluginContext(ui.Application);
    try
    {
        plugin.Initialize(context);
        AssertTrue(
            ReferenceEquals(Workspace.Current, ui.Bootstrap),
            "Default initialization must keep the bootstrap workspace active.");
        plugin.StartAsync().GetAwaiter().GetResult();

        var settingsPath = Path.Combine("PluginData", "SkillEffectPlugin", "settings.json");
        var settings = File.ReadAllText(settingsPath);
        AssertTrue(settings.Contains("\"AutoUpdateSkillEffects\": false", StringComparison.Ordinal), "Default settings must persist AutoUpdateSkillEffects=false.");
    }
    finally
    {
        plugin.DisposeAsync().GetAwaiter().GetResult();
    }

    Directory.Delete("PluginData", recursive: true);
}

static void AssertStartupUpdateUsesCancellationToken(WorkspaceSmokeSession ui)
{
    var dataDirectory = Path.Combine("PluginData", "SkillEffectPlugin");
    Directory.CreateDirectory(dataDirectory);
    File.WriteAllText(
        Path.Combine(dataDirectory, "settings.json"),
        """
        {
          "DisplayOrder": 0,
          "MinimumExpectedEffect": 0,
          "Race": "",
          "RunningStyle": "",
          "URACloudBaseUrl": "http://127.0.0.1:4694",
          "AutoUpdateSkillEffects": true
        }
        """);

    var context = new SmokePluginContext(ui.Application);
    var plugin = new SkillEffectPlugin.SkillEffectPlugin();
    plugin.Initialize(context);
    AssertTrue(
        ReferenceEquals(Workspace.Current, ui.Bootstrap),
        "Auto-update initialization must keep the bootstrap workspace active.");
    try
    {
        plugin.StartAsync(new CancellationToken(canceled: true)).GetAwaiter().GetResult();
        throw new InvalidOperationException("The startup download must observe Host cancellation.");
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        plugin.DisposeAsync().GetAwaiter().GetResult();
    }

    Directory.Delete("PluginData", recursive: true);
}

static void AssertConfigDraftSaveAndCancelSemantics()
{
    using var application = Terminal.Gui.App.Application.Create(new VirtualTimeProvider())
        .Init(DriverRegistry.Names.ANSI);
    application.Driver!.SetScreenSize(100, 24);
    var plugin = new SkillEffectPlugin.SkillEffectPlugin();
    var settingsPath = Path.Combine("PluginData", "SkillEffectPlugin", "settings.json");

    application.AddTimeout(TimeSpan.Zero, () =>
    {
        if (application.TopRunnableView is not Dialog dialog)
            return true;

        ReplaceFocusedText(application, "2");
        application.InjectKey(Key.Tab);
        ReplaceFocusedText(application, "3.5");
        application.InjectKey(Key.Tab);
        ReplaceFocusedText(application, "saved-course");
        application.InjectKey(Key.Tab);
        ReplaceFocusedText(application, "saved-style");
        application.InjectKey(Key.Tab);
        ReplaceFocusedText(application, "http://127.0.0.1:4695");
        application.InjectSequence(InputInjectionExtensions.LeftButtonClick(
            dialog.ViewportToScreen(new Point(2, 10))));
        application.InjectKey(Key.Enter);
        return false;
    });
    plugin.ConfigPromptAsync(application).GetAwaiter().GetResult();

    using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
    {
        var root = settings.RootElement;
        AssertEqual(2, root.GetProperty("DisplayOrder").GetInt32(), "Save must persist the display order draft.");
        AssertNearly(3.5, root.GetProperty("MinimumExpectedEffect").GetDouble(), "Save must persist the minimum-effect draft.");
        AssertEqual("saved-course", root.GetProperty("Race").GetString(), "Save must persist the race draft.");
        AssertEqual("saved-style", root.GetProperty("RunningStyle").GetString(), "Save must persist the running-style draft.");
        AssertEqual("http://127.0.0.1:4695", root.GetProperty("URACloudBaseUrl").GetString(), "Save must persist the URACloud URL draft.");
        AssertEqual(true, root.GetProperty("AutoUpdateSkillEffects").GetBoolean(), "Save must persist the auto-update draft.");
    }

    var baseline = File.ReadAllBytes(settingsPath);
    AssertCanceledWithoutWrite(
        application,
        plugin,
        settingsPath,
        baseline,
        (owner, _) =>
        {
            ReplaceFocusedText(owner, "0");
            for (var i = 0; i < 5; i++)
                owner.InjectKey(Key.Tab);
            owner.InjectKey(Key.Enter);
        },
        "Cancel");
    AssertCanceledWithoutWrite(
        application,
        plugin,
        settingsPath,
        baseline,
        (owner, _) =>
        {
            ReplaceFocusedText(owner, "0");
            owner.InjectKey(Key.Esc);
        },
        "Esc");
    AssertCanceledWithoutWrite(
        application,
        plugin,
        settingsPath,
        baseline,
        (owner, dialog) =>
        {
            ReplaceFocusedText(owner, "0");
            owner.RequestStop(dialog);
        },
        "close");

    plugin.DisposeAsync().GetAwaiter().GetResult();
    Directory.Delete("PluginData", recursive: true);
}

static void AssertCanceledWithoutWrite(
    IApplication application,
    SkillEffectPlugin.SkillEffectPlugin plugin,
    string settingsPath,
    byte[] baseline,
    Action<IApplication, Dialog> interact,
    string action)
{
    application.AddTimeout(TimeSpan.Zero, () =>
    {
        if (application.TopRunnableView is not Dialog dialog)
            return true;
        interact(application, dialog);
        return false;
    });

    try
    {
        plugin.ConfigPromptAsync(application).GetAwaiter().GetResult();
        throw new InvalidOperationException($"Config {action} must cancel.");
    }
    catch (OperationCanceledException)
    {
    }

    AssertTrue(baseline.SequenceEqual(File.ReadAllBytes(settingsPath)), $"Config {action} must not write settings.");
}

static void ReplaceFocusedText(IApplication application, string text)
{
    application.InjectKey(Key.A.WithCtrl);
    foreach (var character in text)
        application.InjectKey(new Key(character));
}

static void PreparePluginData()
{
    var dataDirectory = Path.Combine("PluginData", "SkillEffectPlugin");
    var effectDirectory = Path.Combine(dataDirectory, "course");
    Directory.CreateDirectory(effectDirectory);

    File.WriteAllText(
        Path.Combine(dataDirectory, "settings.json"),
        """
        {
          "DisplayOrder": 0,
          "MinimumExpectedEffect": 0,
          "Race": "course",
          "RunningStyle": "style",
          "URACloudBaseUrl": "http://127.0.0.1:4694",
          "AutoUpdateSkillEffects": false
        }
        """);

    File.WriteAllText(
        Path.Combine(effectDirectory, "style.json"),
        """
        [
          { "name": "skill-one", "effect": "12.34" }
        ]
        """);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
}

static void AssertNearly(double expected, double actual, string message)
{
    if (Math.Abs(expected - actual) > 0.0001)
        throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
}

sealed class SmokePluginContext(IApplication application) : IPluginContext
{
    public IApplication Application { get; } = application;
    IPluginAnalyzerRegistry IPluginContext.Analyzers { get; } = new SmokeAnalyzerRegistry();
    public bool IsPluginAvailable(string internalName) => false;

    public void ReportBackgroundFailure(Exception error)
        => throw new InvalidOperationException("SkillEffectPlugin background work failed.", error);
}

sealed class SmokeAnalyzerRegistry : IPluginAnalyzerRegistry
{
    public void Register<TPayload>(
        AnalyzerKind kind,
        IReadOnlyList<EndpointPattern> patterns,
        Func<AnalyzerInvocation<TPayload>, ValueTask> handler,
        int priority = 0)
    {
    }
}
