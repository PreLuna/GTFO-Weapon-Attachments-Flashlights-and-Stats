# GTFO-Weapon-Attachments-Flashlights-and-Stats

武器配件、手电与数据。本仓包含以下MOD／共享组件，各自维护文件放在自己的目录。

[定位入口](#modules) · [依赖与联动](#relations) · [开发与排查](#development) · [来源说明](#sources)

<a id="modules"></a>
## 定位入口

| 功能／组件 | 版本 | 修改位置与输入 | 维护方式 | 测试材料 |
| --- | --- | --- | --- | --- |
| 自定义武器配件 `WeaponAttachments` | 0.8.0 | [WeaponAttachments/src](WeaponAttachments/src) | 源码 | [WeaponAttachments/tests](WeaponAttachments/tests) |
| 手电筒大修 `FlashlightCustom` | 1.0.1 | [FlashlightCustom/baseline](FlashlightCustom/baseline) | 冻结DLL＋身份迁移 | 由仓级入口／配方核对 |
| 武器数值数据 `WeaponDataCustom` | 1.0.8 | [WeaponDataCustom/GameData](WeaponDataCustom/GameData) | JSON数据 | 由仓级入口／配方核对 |

具体路径、版本、GUID、资源和依赖的机器登记见[repo.json](repo.json)。src是实现，tests是测试；IntegrationTests用于跨MOD场景或调度配方，Shared仅保存真正共享的接口。

仓级测试配方与共享场景见[IntegrationTests](IntegrationTests)。测试文件是否实际执行，以根Test.ps1及配方为准。

默认配件配置见[WeaponAttachments/config](WeaponAttachments/config)，不是个人运行CFG。

普通玩家从[Releases](https://github.com/PreLuna/GTFO-Weapon-Attachments-Flashlights-and-Stats/releases)下载已经构建的安装ZIP，按对应版本说明安装并准备前置；不需要构建工具仓。退出游戏后将ZIP中的BepInEx合并到已配置前置的游戏目录，替换同功能旧版以免重复加载；个人CFG不包含在安装包中。GitHub的Source code／Code ZIP用于开发，不能把baseline原件直接当安装DLL。

<a id="relations"></a>
## 依赖与联动

配件、手电与武器数据同属武器维护范围，仍分别输出插件和数据。修改配件数值、光照或GameData时联合核对最终效果；不能把管理分组当作相互硬依赖。

| 模块 | 插件硬前置／声明版本 | 可选联动／软依赖 |
| --- | --- | --- |
| WeaponAttachments | GTFO-API `0.5.1`；MTFO `4.6.3`；ModifierAPI `1.3.2`；AmorLib `1.2.7`；ExtraWeaponCustomization `4.13.4`；WeaponStatShower `2.0.5` | MTFO.Extension.PartialBlocks |
| FlashlightCustom | 无插件硬前置声明 | 无插件软依赖声明 |

WeaponDataCustom是数据包，运行前置为MTFO 4.6.3、PartialData 1.5.2、PierceBugFix 1.2.2、WeaponStatShower 2.0.5。配件插件对PartialData（MTFO.Extension.PartialBlocks）是软依赖；数据包的安装条件另行满足。

所有插件仍需GTFO及相应BepInEx IL2CPP环境。版本保留加载器原始声明表达式；编译引用、运行硬前置和可选桥分别判断。

<a id="development"></a>
## 开发与排查

| 要修改／排查的功能 | 先读哪里 | 联动范围 |
| --- | --- | --- |
| 配件定义与默认目录 | [WeaponAttachments/src/Definitions](WeaponAttachments/src/Definitions) | 默认配置在WeaponAttachments/config；运行时个人配置不入仓。 |
| 配装、数值与原生接线 | [WeaponAttachments/src](WeaponAttachments/src) | Loadouts、Modifiers、Native、Interface分别定位存储、计算、游戏绑定与面板。 |
| 武器JSON数据 | [WeaponDataCustom/GameData](WeaponDataCustom/GameData) | 与配件的最终数值联合核对；保持既有安装相对路径。 |

冻结baseline保留原始WangLocal文件名、内部身份和SHA256，构建后输出PreLuna版；它不是完整主体源码。行为调整须先证明补丁输入链，不能只改DLL文件名。维护者本地历史冷档不随本仓提供，未据此宣称源码已经恢复。

开发环境：支持C#12编译的PowerShell 7、.NET 6 runtime、GTFO/BepInEx的core与生成的interop；武器仓还需repo.json锁定的4个外部编译引用。将[共用工具仓](https://github.com/PreLuna/GTFO-Mod-Build-and-Verification-Tools)放在本仓旁边，可通过BuildToolsRoot覆盖位置。

先审查并登记输入／维护锁，再在本仓目录执行（路径换成本机实际值，重复构建前显式清理本仓忽略的out）：

```powershell
./Build.ps1 -GameRoot 'D:/Games/GTFO' -RuntimeRoot 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App/6.0.16'
./Test.ps1 -RuntimeRoot 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App/6.0.16'
```

根Test.ps1按IntegrationTests/recipes.json调度6组，覆盖定义、配装、计算、执行、界面与所有权；原生探针和游戏内结果不得混同于受控测试。

构建与离线测试不安装到游戏。`.cs`是功能／测试／辅助源码，`.ps1`是维护脚本，编译所得`.dll`才供游戏加载；运行CFG不加入源码仓。

维护锁与提交步骤见[共用维护流程](https://github.com/PreLuna/GTFO-Mod-Build-and-Verification-Tools#maintenance)，打包要求见[发布与打包](https://github.com/PreLuna/GTFO-Mod-Build-and-Verification-Tools#packaging)。仅使用Releases的玩家不需要这些开发步骤。

<a id="sources"></a>
## 来源说明

- FlashlightCustom：FlashlightOverhaul；作者 Niedvin；来源版本 原始输入快照manifest标注2.1.0；本分支当前1.0.1并含后续独立修复。
- WeaponDataCustom：BetterWeaponConfig／OmegaWeapons提取配置；作者 具体来源以工作区说明及历史输入为准，不臆定全包单一作者；来源版本 当前数据分支1.0.8；源包确切版本未在本轮核定。

保留源文件中的作者与版权声明。部分模组包含第三方派生实现或冻结基线，本说明不替代上游许可证，也不新增再分发授权；本仓库没有统一重授权声明。
