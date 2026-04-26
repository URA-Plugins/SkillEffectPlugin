using Gallop;
using Gallop.Endpoints;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;

namespace SkillEffectPlugin
{
    public class SkillEffectPlugin : IPlugin
    {
        const string SettingsFileName = "settings.json";
        const string SkillEffectsPanelKey = "skill-effects";
        const string WorkspaceTitle = "技能收益";

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        static readonly SkillEffectSettings DefaultSettings = new(
            0,
            0,
            string.Empty,
            string.Empty,
            "http://127.0.0.1:4694",
            false);

        PluginSnapshot _snapshot = new(DefaultSettings, FrozenDictionary<string, double>.Empty);
        Workspace? _workspace;
        bool _hasPublishedSkillEffectsPanel;

        /// <summary>
        /// 期望收益表，源自 ウマ娘.攻略.tools。浏览器抓取脚本：
        /// JSON.stringify(Array.from(document.querySelectorAll('[class^="courseSkillEffectTableRow_component_skillCard__body__"]')).map(x=>{
        ///     var name = x.querySelector('[class^="courseSkillEffectTableRow_component_skillCard__name__"]').innerHTML;
        ///     var effect = x.querySelector('[class^="courseSkillEffectTableRow_component_skillCard__effect__"]').innerHTML;
        ///     return {name,effect};
        /// }))
        /// </summary>
        public IReadOnlyDictionary<string, double> Effects => Snapshot.Effects;

        PluginSnapshot Snapshot => Volatile.Read(ref _snapshot);
        string DataDirectory => Path.Combine("PluginData", "SkillEffectPlugin");
        string SettingsPath => Path.Combine(DataDirectory, SettingsFileName);

        public void Initialize(IPluginContext context)
        {
            _hasPublishedSkillEffectsPanel = false;
            Directory.CreateDirectory(DataDirectory);
            var settings = LoadSettings();
            Volatile.Write(ref _snapshot, new(settings, ReadEffects(settings)));

            if (settings.AutoUpdateSkillEffects && !string.IsNullOrWhiteSpace(settings.URACloudBaseUrl))
                context.Events.OnStarted(
                    cancellationToken => new(UpdateSkillEffectsAsync(
                        Snapshot.Settings.URACloudBaseUrl,
                        cancellationToken)));
        }

        public void Dispose()
        {
            if (_hasPublishedSkillEffectsPanel)
            {
                _workspace!.RemovePanel(SkillEffectsPanelKey);
                _hasPublishedSkillEffectsPanel = false;
            }
            _workspace = null;
        }

        private SkillEffectSettings LoadSettings()
        {
            if (!File.Exists(SettingsPath))
            {
                SaveSettings(DefaultSettings);
                return DefaultSettings;
            }

            return JsonSerializer.Deserialize<SkillEffectSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? throw new InvalidOperationException($"{SettingsPath} 反序列化结果为空。");
        }

        private void SaveSettings(SkillEffectSettings settings)
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }

        private FrozenDictionary<string, double> ReadEffects(SkillEffectSettings settings)
        {
            if (string.IsNullOrEmpty(settings.Race) || string.IsNullOrEmpty(settings.RunningStyle))
                return FrozenDictionary<string, double>.Empty;

            var path = Path.Combine(DataDirectory, settings.Race, $"{settings.RunningStyle}.json");
            if (!File.Exists(path))
                return FrozenDictionary<string, double>.Empty;

            var items = JsonSerializer.Deserialize<SkillEffectFileItem[]>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"{path} 反序列化结果为空。");

            var effects = new Dictionary<string, double>();
            foreach (var item in items)
            {
                var name = item.Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (item.Effect.Length >= 4 && double.TryParse(item.Effect[..4], out var effect))
                    effects.TryAdd(name, effect);
            }
            return effects.ToFrozenDictionary();
        }

        private void ReloadEffects()
        {
            while (true)
            {
                var current = Snapshot;
                var updated = current with { Effects = ReadEffects(current.Settings) };
                if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current))
                    return;
            }
        }

        async Task UpdateSkillEffectsAsync(string baseUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("URACloud 服务地址未配置");

            Directory.CreateDirectory(DataDirectory);

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };

            var downloadUrl = $"{baseUrl.TrimEnd('/')}/SkillEffects/download";
            var bytes = await client.GetByteArrayAsync(downloadUrl, cancellationToken).ConfigureAwait(false);

            using var ms = new MemoryStream(bytes);
            using var archive = new ZipArchive(ms);
            archive.ExtractToDirectory(DataDirectory, true);

            ReloadEffects();
        }

        public async Task ConfigPromptAsync(
            IApplication application,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            if (application.TopRunnable is null &&
                Environment.CurrentManagedThreadId != application.MainThreadId)
                throw new InvalidOperationException(
                    "SkillEffectPlugin 无法从非 UI thread 启动配置：Terminal.Gui 当前没有正在运行的 session。");

            var draft = Snapshot.Settings;

            while (true)
            {
                ConfigDialogResult result;
                if (Environment.CurrentManagedThreadId == application.MainThreadId)
                {
                    result = RunConfigDialog(application, draft, cancellationToken);
                }
                else
                {
                    var completion = new TaskCompletionSource<ConfigDialogResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    application.Invoke(() =>
                    {
                        try
                        {
                            completion.SetResult(RunConfigDialog(application, draft, cancellationToken));
                        }
                        catch (Exception ex)
                        {
                            completion.SetException(ex);
                        }
                    });
                    result = await completion.Task;
                }

                cancellationToken.ThrowIfCancellationRequested();
                draft = result.Settings;
                if (result.Action == ConfigAction.Cancel)
                    throw new OperationCanceledException("SkillEffectPlugin 配置已取消。", cancellationToken);

                if (result.Action == ConfigAction.Update)
                {
                    await UpdateSkillEffectsAsync(draft.URACloudBaseUrl, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var workspace = _workspace ??= Workspace.Create(WorkspaceTitle);
                    workspace.Notify("技能效果文件更新成功。", UiSeverity.Success);
                    continue;
                }

                Directory.CreateDirectory(DataDirectory);
                SaveSettings(draft);
                Volatile.Write(ref _snapshot, new(draft, ReadEffects(draft)));
                return;
            }
        }

        static ConfigDialogResult RunConfigDialog(
            IApplication application,
            SkillEffectSettings draft,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var dialog = new Dialog
            {
                Title = "SkillEffectPlugin 配置",
                Width = 88,
                Height = 20,
            };

            var displayOrder = AddTextField(dialog, "显示顺序 (0 游戏内 / 1 收益 / 2 性价比)", 0, draft.DisplayOrder.ToString());
            var minimumExpectedEffect = AddTextField(dialog, "最小收益", 2, draft.MinimumExpectedEffect.ToString());
            var race = AddTextField(dialog, "赛道文件夹", 4, draft.Race);
            var runningStyle = AddTextField(dialog, "跑法 (逃/先/差/追)", 6, draft.RunningStyle);
            var baseUrl = AddTextField(dialog, "URACloud 地址", 8, draft.URACloudBaseUrl);
            var autoUpdate = new CheckBox
            {
                X = 1,
                Y = 10,
                Text = "启动时自动更新技能效果文件",
                Value = draft.AutoUpdateSkillEffects ? CheckState.Checked : CheckState.UnChecked,
                CanFocus = false,
            };
            var validation = new Label
            {
                X = 1,
                Y = 12,
                Width = Dim.Fill(1),
                Height = 2,
                Text = string.Empty,
            };
            dialog.Add(autoUpdate, validation);

            var action = ConfigAction.Cancel;
            var result = draft;
            bool TryReadDraft()
            {
                if (!int.TryParse(displayOrder.Text, out var order) || order is < 0 or > 2)
                {
                    validation.Text = "显示顺序必须是 0、1 或 2。";
                    return false;
                }
                if (!double.TryParse(minimumExpectedEffect.Text, out var minimum))
                {
                    validation.Text = "最小收益必须是数字。";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(baseUrl.Text))
                {
                    validation.Text = "URACloud 地址不能为空。";
                    return false;
                }

                result = new(
                    order,
                    minimum,
                    race.Text,
                    runningStyle.Text,
                    baseUrl.Text,
                    autoUpdate.Value == CheckState.Checked);
                return true;
            }

            var save = new Button { Text = "保存", IsDefault = true };
            save.Accepting += (_, e) =>
            {
                if (!TryReadDraft())
                {
                    e.Handled = true;
                    return;
                }
                action = ConfigAction.Save;
                application.RequestStop(dialog);
                e.Handled = true;
            };
            var update = new Button { Text = "立即更新" };
            update.Accepting += (_, e) =>
            {
                if (!TryReadDraft())
                {
                    e.Handled = true;
                    return;
                }
                action = ConfigAction.Update;
                application.RequestStop(dialog);
                e.Handled = true;
            };
            var cancel = new Button { Text = "取消" };
            cancel.Accepting += (_, e) =>
            {
                application.RequestStop(dialog);
                e.Handled = true;
            };
            dialog.AddButton(cancel);
            dialog.AddButton(update);
            dialog.AddButton(save);
            displayOrder.SetFocus();

            using (cancellationToken.Register(
                       () => application.Invoke(() => application.RequestStop(dialog))))
                application.Run(dialog);
            cancellationToken.ThrowIfCancellationRequested();
            return new(action, result);
        }

        static TextField AddTextField(Dialog dialog, string label, int y, string value)
        {
            dialog.Add(new Label
            {
                X = 1,
                Y = y,
                Text = label,
            });
            var field = new TextField
            {
                X = 1,
                Y = y + 1,
                Width = Dim.Fill(1),
                Text = value,
            };
            dialog.Add(field);
            return field;
        }

        [ResponseAnalyzer<GameApi.SingleMode.CheckEvent>]
        public ValueTask Analyze(SingleModeCheckEventResponse response)
        {
            if (response.data.unchecked_event_array.Length != 0) return ValueTask.CompletedTask;
            if (response.data.chara_info.state is not (2 or 3)) return ValueTask.CompletedTask;

            var workspace = _workspace ??= Workspace.Create(WorkspaceTitle);
            workspace.SetPanel(
                SkillEffectsPanelKey,
                "技能收益",
                BuildSkillContent(CalculateSkillRows(response)));
            _hasPublishedSkillEffectsPanel = true;

            return ValueTask.CompletedTask;
        }

        private List<(string baseName, string bestName, double effect, int cost, int order)> CalculateSkillRows(SingleModeCheckEventResponse ev)
        {
            var skills = Database.Skills.Apply(ev.data.chara_info);
            skills.Evolve(ev.data.chara_info, skills.GetSkills());
            skills.RemoveLearned(ev.data.chara_info);

            var rows = new List<(string baseName, string bestName, double effect, int cost, int order)>();
            foreach (var skill in skills)
            {
                var bestUpgrade = skill.Upgrades
                    .Where(x => Effects.ContainsKey(x.Name))
                    .OrderByDescending(x => Effects[x.Name])
                    .FirstOrDefault();

                if (bestUpgrade != null)
                    rows.Add((skill.DisplayName, bestUpgrade.DisplayName, Effects[bestUpgrade.Name], bestUpgrade.Cost, skill.DisplayOrder));
                else if (Effects.TryGetValue(skill.Name, out var effect))
                    rows.Add((skill.Name, skill.DisplayName, effect, skill.Cost, skill.DisplayOrder));
            }

            return rows;
        }

        private WorkspaceContent BuildSkillContent(List<(string baseName, string bestName, double effect, int cost, int order)> rows)
        {
            var settings = Snapshot.Settings;
            var ordered = settings.DisplayOrder switch
            {
                1 => rows.OrderByDescending(x => x.effect),
                2 => rows.OrderByDescending(x => x.effect / x.cost),
                _ => rows.OrderBy(x => x.order),
            };

            var text = new StringBuilder("技能\t收益\t技能点性价比");
            foreach (var (baseName, bestName, effect, cost, _) in ordered.Where(x => x.effect >= settings.MinimumExpectedEffect))
            {
                var name = baseName != bestName
                    ? $"{baseName}->{bestName}"
                    : baseName;
                text.AppendLine()
                    .Append(name)
                    .Append('\t')
                    .Append(effect)
                    .Append('\t')
                    .Append((effect * 1000 / cost).ToString("0.00"));
            }

            return WorkspaceContent.Text(text.ToString());
        }

        sealed record SkillEffectSettings(
            int DisplayOrder,
            double MinimumExpectedEffect,
            string Race,
            string RunningStyle,
            string URACloudBaseUrl,
            bool AutoUpdateSkillEffects);

        sealed record PluginSnapshot(
            SkillEffectSettings Settings,
            FrozenDictionary<string, double> Effects);

        sealed record SkillEffectFileItem(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("effect")] string Effect);

        sealed record ConfigDialogResult(
            ConfigAction Action,
            SkillEffectSettings Settings);

        enum ConfigAction
        {
            Cancel,
            Save,
            Update,
        }
    }
}
