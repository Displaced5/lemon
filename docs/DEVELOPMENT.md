# 开发说明

## 结构

`RestoreController` 是不访问游戏内存的纯状态机；分别控制时尚配饰与面部配饰的记忆、中断归因、暂停、稳定等待和有限重试。`SettingsData` 管理配置及旧版本归一化。`EquipmentChangeTracker` 识别职业和套装编号变化。`SubmissionArbiter` 轮转选择可提交项并控制请求间隔。

`GameAdapter` 负责在框架线程读取游戏状态、检查配饰目录及解锁、提交前复查。先核对角色标识与本地玩家 EntityId，不跨帧保存原生指针。界面和命令经队列进入框架更新；某类接口异常仅停止对应控制器。

时尚配饰读取 `Character.OrnamentData.OrnamentId`，与 `UsingFashionAccessory` 标记相互核对，通过 `ActionManager.GetActionStatus/UseAction(ActionType.Ornament, id)` 恢复。

面部配饰只读取 `DrawData.GlassesIds[0]`，保存完整 `Glasses.RowId`，包含具体颜色变体；按 `GlassesStyle.GlassesSlot` 验证实际位置，通过 `IUnlockState.IsGlassesUnlocked` 验证解锁。佩戴走正常游戏命令 `GameMain.ExecuteCommand(455, 0, glassesId)`，不直接写入 DrawData，不调用 SetGlasses 强制显示。面部配饰没有 Ornament 动作状态，不能用时尚配饰的状态查询冒充面部可用性确认，必须继续核对命令结果和实际 ID。

底层数组长度不代表玩家可用位置数量。当前仅支持一件面部配饰，第二位置不能传入佩戴命令；配置保留旧 Slots 字段名，但归一化后只有一个元素。

职业从有效本地玩家的 `ClassJob.RowId` 读取；套装从 `RaptureGearsetModule.CurrentGearsetIndex` 读取。首次采样只建立基线，变化后等待稳定再进入原恢复流程。没有原生动作 Hook，采样可能无法识别极短或同编号重复切换。

## 配置与测试

配置版本 1 的 `Characters` 保留为时尚配饰记忆，版本 2 增加独立的 `FacewearCharacters`。配置归一化不丢弃实际位置的 ID 和暂停状态；拒绝高于支持范围的配置版本，避免降级覆盖。

离线检查覆盖状态机、合成角色隔离、款式与颜色切换、配置迁移、单件面部约束、职业与套装变化、延迟及失败处理、请求调度和编译身份。测试不加载原生游戏函数，不能替代游戏内验证。

## 参考接口

以下链接是接口研究来源，仓库不包含其源码快照或宿主程序集：

- [Dalamud 开发手册](https://dalamud.dev/plugin-development/getting-started/)
- [FFXIVClientStructs DrawDataContainer](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/c306aa6c980c38db9ad5155a9f219e2931f4f1c2/FFXIVClientStructs/FFXIV/Client/Game/Character/DrawDataContainer.cs)
- [FFXIVClientStructs GameMain](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/c306aa6c980c38db9ad5155a9f219e2931f4f1c2/FFXIVClientStructs/FFXIV/Client/Game/GameMain.cs)
- [OmenTools GlassesCommand](https://github.com/AtmoOmen/OmenTools/blob/53762480b8c032f60acbcb33fafd04c07b06db1d/Interop/Game/ExecuteCommand/Implementations/GlassesCommand.cs)
- [OmenTools 命令枚举](https://github.com/AtmoOmen/OmenTools/blob/53762480b8c032f60acbcb33fafd04c07b06db1d/Info/Game/Enums/ExecuteCommandFlag.cs)

目标为国服 API 15；这些接口引用不保证未来版本的区域或布局兼容。
