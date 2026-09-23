# SkillEffectPlugin

`SkillEffectPlugin` 是 UmamusumeResponseAnalyzer 插件，用技能效果表计算尚未学习技能的期望收益，并显示收益及每 1000 技能点收益。

## 行为

插件处理 `single_mode/check_event` 响应。仅当没有待处理事件且育成角色状态为 `2` 或 `3` 时，才在“技能收益”工作区发布以下三列：

- 技能：存在可用进化技能时显示基础技能到进化技能的映射。
- 收益：技能效果表中的期望收益。
- 技能点性价比：`收益 * 1000 / 技能点消耗`。

已学习技能会被排除；结果可按游戏内顺序、收益或技能点性价比排序，并按最小收益过滤。

## 数据与配置

插件数据位于 `PluginData/SkillEffectPlugin/`：

- `settings.json` 保存 `DisplayOrder`、`MinimumExpectedEffect`、`Race`、`RunningStyle`、`URACloudBaseUrl` 和 `AutoUpdateSkillEffects`。
- `<Race>/<RunningStyle>.json` 是技能效果数组；每项包含字符串字段 `name` 和 `effect`。

`DisplayOrder` 为 `0` 时按游戏内顺序，为 `1` 时按收益降序，为 `2` 时按技能点性价比降序。配置界面可以保存配置，或从 `<URACloudBaseUrl>/SkillEffects/download` 下载并解压技能效果文件；启用自动更新后，插件在 `StartAsync` 中执行同一更新。

技能效果值不计算技能组合的边际效应，仅供参考。未配置赛道或跑法、或对应效果文件不存在时，不会加载技能效果数据。

## 构建

仓库通过 NuGet 包引用 Host API。克隆后在仓库根执行：

```powershell
dotnet build .\SkillEffectPlugin.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
```

Host-dependent smoke 位于 `tests/SkillEffectPluginSmoke`。

## 验证与发布

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；版本 tag 触发 GitHub Release 发布。环境要求、共用 workflow 本地映射和发布规则见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/v1/README.md)。
