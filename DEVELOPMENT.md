# 开发与关联

## 修改入口

| MOD | 源码／输入 | 测试 |
| --- | --- | --- |
| 自定义武器配件 | `WeaponAttachments/src` | `WeaponAttachments/tests` |
| 手电筒大修 | `FlashlightCustom/baseline/WangLocal.FlashlightCustom.dll` | 当前无独立源码测试入口 |
| 武器数值数据 | `WeaponDataCustom/GameData` | 当前无独立源码测试入口 |

配件、手电、武器数据属于同一武器行为范围，改动挂点、灯光或数值时一起验证；它们没有相互全部硬依赖。配件声明PartialData为软依赖，武器数据包自身明确使用PartialData，安装条件应分别看各MOD。武器数据的14个JSON归WeaponDataCustom，默认配件目录和schema归WeaponAttachments。

仓库根Build.ps1/Test.ps1负责共同调度；IntegrationTests保存跨MOD场景和测试配方，单MOD测试主体在各自tests目录。文件存在不表示已被执行。

## 构建与测试

PowerShell 7需支持C#12编译，使用.NET 6 runtime和本机GTFO/BepInEx托管引用。将GTFO-Mod-Build-and-Verification-Tools放在本仓旁边。

```powershell
./Build.ps1 -GameRoot 'D:/Games/GTFO' -RuntimeRoot 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App/6.0.16'
./Test.ps1 -RuntimeRoot 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App/6.0.16'
```

路径换成本机目录，可指定BuildToolsRoot/DependencyRoot。输入路径由repo.json显式登记，不搜索旧根src或历史工作区。输出在忽略的out，重复构建前清理或移走out。构建不安装到游戏。

冻结DLL主体没有完整源码；本轮只移动baseline并保留SHA256，没有导入历史补丁。未来改行为需先恢复并核对补丁输入链。

## 暂存与维护锁

审查Git差异并暂存有意提交的输入，再运行工具仓tools/Update-Lock.ps1 -RepositoryRoot <仓库>，暂存更新后的maintenance.lock.json，最后运行tools/Check-Commit.ps1。只检查一致性不等于构建或游戏实测通过。

新克隆用工具仓tools/Enable-Hooks.ps1 -CheckoutRoot <9仓目录>安装本机检查；Git不自动携带.git/hooks。本机钩子能被绕过，仍需人工审核与回归。不要向源码仓加入ZIP、日志、个人CFG或框架。必要的冻结DLL只允许repo.json登记路径。

## 安装包与提交状态

在干净源码提交上构建测试，用tools/Package-Release.ps1 -RepositoryRoot <仓库> -DestinationRoot <源码仓库之外> -Version <新标签>生成ZIP和包含准确源码提交的清单。

本地尚未提交时，仅可显式用-Preview做验收预览；清单中SourceCommit为空、ReleaseReady=false，BaseCommit只是修改前提交。预览不作为正式发布，不覆盖已发版本。构建输出记录当前输入锁与工具锁。

整合仓默认验证已发布精确提交；-LocalWorkspace验证未提交整理的当前锁、暂存内容及基准HEAD。未来获准提交后，再登记真实新提交并重新下载验收；保留原Releases标签和旧验收记录。
