using Gallop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using System.IO.Compression;
using System.Text;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Plugin;
using SkillData = UmamusumeResponseAnalyzer.Entities.SkillData;

namespace SkillEffectPlugin
{
    public class SkillEffectPlugin : IPlugin
    {
        const string SKILL_DATA_FILEPATH = "./PluginData/SkillEffectPlugin/skill_data.br";
        public Version Version => new(1, 0, 0);
        [PluginDescription("显示技能期望收益")]
        public string Name => "SkillEffectPlugin";
        public string Author => "离披&ウマ娘.攻略.tools";
        public string[] Targets => [];
        public async Task UpdatePlugin(ProgressContext ctx)
        {
            var progress = ctx.AddTask($"[[{Name}]] 更新");

            using var client = new HttpClient();

            var assetsHost = string.IsNullOrEmpty(Config.Updater.CustomDatabaseRepository) ? "https://github.com/UmamusumeResponseAnalyzer/Assets/raw/refs/heads/main/".AllowMirror() : Config.Updater.CustomDatabaseRepository;
            var brUrl = $"{assetsHost}/GameData/ja-JP/skill_data.br";
            var br = await client.GetByteArrayAsync(brUrl);
            File.WriteAllBytes(SKILL_DATA_FILEPATH, br);

            using var resp = await client.GetAsync($"https://api.github.com/repos/URA-Plugins/{Name}/releases/latest");
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JObject.Parse(json);

            var isLatest = ("v" + Version.ToString()).Equals("v" + jo["tag_name"]?.ToString());
            if (isLatest)
            {
                progress.Increment(progress.MaxValue);
                progress.StopTask();
                return;
            }
            progress.Increment(25);

            var downloadUrl = jo["assets"][0]["browser_download_url"].ToString().AllowMirror();
            using var msg = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            using var stream = await msg.Content.ReadAsStreamAsync();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                    break;
                progress.Increment(read / msg.Content.Headers.ContentLength ?? 1 * 0.5);
            }
            using var archive = new ZipArchive(stream);
            archive.ExtractToDirectory(Path.Combine("Plugins", Name), true);
            progress.Increment(25);

            progress.StopTask();
        }

        [PluginSetting, PluginDescription("设置显示顺序：0是按游戏内显示，1是按期望收益从大到小，2是按每点Pt收益从大到小排序")]
        public int DisplayOrder { get; set; } = 0;
        [PluginSetting, PluginDescription("最小收益：小于这个值的不会显示")]
        public double MinimumExpectedEffect { get; set; } = 0;
        [PluginSetting, PluginDescription("赛道：PluginData/SkillEffectPlugin/里的文件夹名")]
        public string Race { get; set; } = string.Empty;
        [PluginSetting, PluginDescription("跑法：逃/先/差/追")]
        public string RunningStyle { get; set; } = string.Empty;

        /// <summary>
        /// JSON.stringify(Array.from(document.querySelectorAll('[class^="courseSkillEffectTableRow_component_skillCard__body__"]')).map(x=>{
        /// var name = x.querySelector('[class^="courseSkillEffectTableRow_component_skillCard__name__"]').innerHTML;
        /// var effect = x.querySelector('[class^="courseSkillEffectTableRow_component_skillCard__effect__"]').innerHTML;
        /// return {name,effect};
        /// }))
        /// </summary>
        private List<SkillData> SkillData { get; set; } = [];
        public Dictionary<string, double> Effects { get; set; } = [];

        public void Initialize()
        {
            Directory.CreateDirectory("./PluginData/SkillEffectPlugin/");
            if (File.Exists(SKILL_DATA_FILEPATH))
            {
                SkillData = JsonConvert.DeserializeObject<List<SkillData>>(Encoding.UTF8.GetString(Brotli.Decompress(File.ReadAllBytes(SKILL_DATA_FILEPATH))));
            }
            if (!string.IsNullOrEmpty(Race) && !string.IsNullOrEmpty(RunningStyle))
            {
                var json = JArray.Parse(File.ReadAllText(@$".\PluginData\{Name}\{Race}\{RunningStyle}.json"));
                foreach (var item in json.OrderByDescending(x => double.Parse(x["effect"].ToString()[..4])))
                {
                    var name = item["name"].ToString();
                    var effect = double.Parse(item["effect"].ToString()[..4]);
                    Effects.TryAdd(name, effect);
                }
            }
        }
#warning TODO: 有多个剧本可进化技能时，显示最好的两个？同时显示进化前和进化后的？
        [Analyzer]
        public void Analyze(JObject jo)
        {
            if (!jo.HasCharaInfo()) return;
            if (jo["data"] is null || jo["data"] is not JObject data) return;
            if (data["chara_info"] is null || data["chara_info"] is not JObject chara_info) return;
            var state = chara_info["state"].ToInt();
            if (state is 2 or 3 && data["unchecked_event_array"]?.Count() == 0)
            {
                var ev = jo.ToObject<Gallop.SingleModeCheckEventResponse>();
                if (ev == default) return;
                var skills = Database.Skills.Apply(ev.data.chara_info);
                skills.Evolve(ev.data.chara_info, skills.GetSkills());
                skills.RemoveLearned(ev.data.chara_info);
                var list = new List<(string baseName, string bestName, double Effect, int cost, int order)>();
                foreach (var skill in skills)
                {
                    if (skill.Upgrades.Count != 0 && skill.Upgrades.Any(x => Effects.ContainsKey(x.Name)))
                    {
                        var bestUpgrade = skill.Upgrades.OrderByDescending(x => Effects[x.Name]).First();
                        if (Effects.TryGetValue(bestUpgrade.Name, out var bestEff))
                        {
                            list.Add((skill.DisplayName, bestUpgrade.DisplayName, bestEff, bestUpgrade.Cost, skill.DisplayOrder));
                        }
                        else
                        {
                            AnsiConsole.WriteLine($"{skill.DisplayName}进化不行？");
                        }
                    }
                    else
                    {
                        if (Effects.TryGetValue(skill.Name, out var bestEff))
                        {
                            list.Add((skill.Name, skill.DisplayName, bestEff, skill.Cost, skill.DisplayOrder));
                        }
                    }
                }
                switch (DisplayOrder)
                {
                    case 0:
                        list = [.. list.OrderBy(x => x.order)];
                        break;
                    case 1:
                        list = [.. list.OrderByDescending(x => x.Effect)];
                        break;
                    case 2:
                        list = [.. list.OrderByDescending(x => x.Effect / x.cost)];
                        break;
                }
                foreach (var (baseName, bestName, Effect, cost, order) in list.Where(x => x.Effect >= MinimumExpectedEffect))
                {
                    if (baseName != bestName)
                        AnsiConsole.MarkupLine($"[green]Skill:[/][#FF75BD]{baseName}->{bestName}[/] [green]Effect:[/][yellow]{Effect}[/] Cost: {Effect * 1000 / cost:0.00}");
                    else if ((SkillManagerGenerator.Default.GetSkillByName(baseName)?.Rarity ?? 0) == 2)
                        AnsiConsole.MarkupLine($"[green]Skill:[/][yellow]{baseName}[/] [green]Effect:[/][yellow]{Effect}[/] Cost: {Effect * 1000 / cost:0.00}");
                    else
                        AnsiConsole.MarkupLine($"[green]Skill:[/]{baseName} [green]Effect:[/][yellow]{Effect}[/] Cost: {Effect * 1000 / cost:0.00}");
                }
            }
        }
    }
}
