using System.IO.Compression;
using System.Text;
using Gallop;
using Gallop.Endpoints;
using MessagePack;
using Newtonsoft.Json;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;
using HostSkillData = UmamusumeResponseAnalyzer.Entities.SkillData;
using UiText = UmamusumeResponseAnalyzer.Localization.TerminalGui;

static class SkillPanelSmoke
{
    public static async Task RunAsync(WorkspaceSmokeSession ui)
    {
        using var directory = new TempCurrentDirectory("SkillEffectPlugin-runtime-panel");
        IPlugin plugin = CreateSkillEffectPlugin();
        var response = CreateSkillEffectResponse();
        using var context = new RuntimePluginContext(ui.Application);
        Workspace? published = null;
        try
        {
            plugin.Initialize(context);
            var endpoint = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleMode.CheckEvent)];
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                Console.SetError(output);
                await PluginManager.CreateAttributeRegistrations(plugin).Single(candidate => candidate.EndpointType == endpoint.EndpointType).Handler(new(endpoint, MessagePackSerializer.Serialize(response), new(null, null, null, null, null, null)));
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
            if (output.ToString().Length != 0)
                throw new InvalidOperationException("Analyzer wrote directly to the process console.");
            published = Workspace.Create("技能收益");
            var screen = ui.CaptureScreen();
            if (!ReferenceEquals(Workspace.Current, published) || !screen.Contains("runtime-effect-skill", StringComparison.Ordinal))
                throw new InvalidOperationException("Skill framebuffer omitted its recommendation row.");
            if (!screen.Contains("技能收益", StringComparison.Ordinal))
                throw new InvalidOperationException("Skill effects framebuffer omitted its panel title.");
        }
        finally
        {
            await plugin.DisposeAsync();
        }
        var retained = Workspace.Create("技能收益");
        if (!ReferenceEquals(retained, published))
            throw new InvalidOperationException("Dispose violated the plugin workspace ownership contract.");
        retained.SwitchTo();
        if (ui.CaptureScreen().Contains("runtime-effect-skill", StringComparison.Ordinal))
            throw new InvalidOperationException("Dispose left the skill panel visible.");
        retained.Remove();
        ui.Bootstrap.SwitchTo();
        Console.WriteLine("PASS SkillEffectPlugin registered dispatch, framebuffer and panel disposal");
    }

    static SkillEffectPlugin.SkillEffectPlugin CreateSkillEffectPlugin()
    {
        InitializeDatabase(
            "SkillEffect",
            [
                new()
                {
                    Id = 900002,
                    GroupId = 90002,
                    Rarity = 1,
                    Rate = 1,
                    Name = "runtime-effect-skill",
                    DisplayName = "runtime-effect-skill",
                    Cost = 100,
                    Grade = 100,
                    Propers = []
                }
            ],
            [],
            new Dictionary<int, string>(),
            []);

        var dataDirectory = Path.Combine("PluginData", "SkillEffectPlugin");
        var effectDirectory = Path.Combine(dataDirectory, "runtime-course");
        Directory.CreateDirectory(effectDirectory);
        File.WriteAllText(
            Path.Combine(dataDirectory, "settings.json"),
            """
            {
              "DisplayOrder": 0,
              "MinimumExpectedEffect": 0,
              "Race": "runtime-course",
              "RunningStyle": "runtime-style",
              "URACloudBaseUrl": "http://127.0.0.1:4694",
              "AutoUpdateSkillEffects": false
            }
            """);
        File.WriteAllText(
            Path.Combine(effectDirectory, "runtime-style.json"),
            """
            [
              { "name": "runtime-effect-skill", "effect": "12.34" }
            ]
            """);

        return new();
    }

    static SingleModeCheckEventResponse CreateSkillEffectResponse()
    {
        var response = CreateTerminalResponse(cardId: 0);
        response.data.chara_info.skill_point = 100;
        response.data.chara_info.skill_tips_array =
        [
            new()
            {
                group_id = 90002,
                rarity = 1,
                level = 0
            }
        ];
        return response;
    }

    static SingleModeCheckEventResponse CreateTerminalResponse(int cardId) => new()
    {
        data = new()
        {
            chara_info = new()
            {
                state = 2,
                card_id = cardId,
                speed = 0,
                stamina = 0,
                power = 0,
                guts = 0,
                wiz = 0,
                skill_point = 0,
                skill_array = [],
                skill_tips_array = []
            },
            unchecked_event_array = []
        }
    };

    static void InitializeDatabase(
        string fixtureName,
        IReadOnlyList<HostSkillData> skills,
        IReadOnlyList<BaseName> names,
        IReadOnlyDictionary<int, string> factorIds,
        IReadOnlyList<int> saddleIds)
    {
        WriteBrotliJson(Database.EVENT_NAME_FILEPATH, new List<Story>());
        WriteBrotliJson(Database.NAMES_FILEPATH, names.ToList(), new() { TypeNameHandling = TypeNameHandling.All });
        WriteBrotliJson(Database.SKILLS_FILEPATH, skills);
        WriteBrotliJson(Database.SKILL_UPGRADE_SPECIALITY_FILEPATH, new List<SkillUpgradeSpeciality>());
        WriteBrotliJson(Database.TALENT_SKILLS_FILEPATH, new Dictionary<int, TalentSkillData[]>());
        WriteBrotliJson(Database.FACTOR_IDS_FILEPATH, factorIds);
        WriteBrotliJson(Database.SADDLE_IDS_FILEPATH, saddleIds);
        WriteBrotliJson(Database.SUCCESSION_RELATION_FILEPATH, new SuccessionRelationTable());

        if (Database.Initialize().GetAwaiter().GetResult() != DatabaseAvailability.Ready)
            throw new InvalidOperationException($"{fixtureName} database fixture did not load atomically.");
    }

    static void WriteBrotliJson<T>(string path, T value, JsonSerializerSettings? settings = null)
    {
        using var file = File.Create(path);
        using var brotli = new BrotliStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(brotli, Encoding.UTF8);
        using var json = new JsonTextWriter(writer);
        JsonSerializer.CreateDefault(settings).Serialize(json, value);
    }

}
