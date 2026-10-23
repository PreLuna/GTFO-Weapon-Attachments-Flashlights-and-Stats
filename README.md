# GTFO-Weapon-Attachments-Flashlights-and-Stats

武器配件、手电与数据。各MOD的维护文件直接放在自己的目录内。

| MOD／目录 | 版本 | 维护方式 | 必要前置／声明版本 | 可选联动／软依赖 |
| --- | --- | --- | --- | --- |
| [自定义武器配件](WeaponAttachments/) | 0.8.0 | 源码 | GTFO API `0.5.1`；MTFO `4.6.3`；ModifierAPI `1.3.2`；AmorLib `1.2.7`；ExtraWeaponCustomization `4.13.4`；WeaponStatShower `2.0.5` | PartialData |
| [手电筒大修](FlashlightCustom/) | 1.0.1 | 冻结DLL＋身份迁移 | 无插件硬前置声明 | 无插件软依赖声明 |
| [武器数值数据](WeaponDataCustom/) | 1.0.8 | 数据JSON | MTFO 4.6.3、PartialData 1.5.2、PierceBugFix 1.2.2、WeaponStatShower 2.0.5 | 无插件软依赖声明 |

所有插件均需GTFO与相应BepInEx IL2CPP环境。无插件硬依赖声明不表示可脱离游戏运行；软依赖也可能对应某项功能的启用条件。版本列保留加载器声明表达式。

## MOD之间的关系

配件、手电、武器数据属于同一武器行为范围，改动挂点、灯光或数值时一起验证；它们没有相互全部硬依赖。配件声明PartialData为软依赖，武器数据包自身明确使用PartialData，安装条件应分别看各MOD。武器数据的14个JSON归WeaponDataCustom，默认配件目录和schema归WeaponAttachments。

## 下载与安装

普通玩家从 [Releases](https://github.com/PreLuna/GTFO-Weapon-Attachments-Flashlights-and-Stats/releases) 下载安装ZIP，退出游戏后将BepInEx合并到已配置前置的游戏目录，替换同功能旧版以免重复加载。个人配置不包含在安装包中。Source code ZIP用于开发。已发布标签固定对应原提交；本地目录整理不覆盖旧安装包。

构建、测试及修改入口见 [DEVELOPMENT.md](DEVELOPMENT.md)，来源见 [NOTICE.md](NOTICE.md)，机器可读路径和依赖见 [repo.json](repo.json)。
