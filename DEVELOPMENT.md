# 开发与关联

## 构建

使用 PowerShell 7（含 Microsoft.CodeAnalysis C# 12 编译器）、.NET 6 runtime，以及本机合法安装的 GTFO/BepInEx interop 引用。将 [GTFO-Mod-Build-and-Verification-Tools](https://github.com/PreLuna/GTFO-Mod-Build-and-Verification-Tools) 克隆到本仓库旁边。

```powershell
./Build.ps1 -GameRoot 'D:/Games/GTFO' -RuntimeRoot 'C:/Program Files/dotnet/shared/Microsoft.NETCore.App/6.0.16'
```

路径仅为示例，请换成本机目录；可额外指定 `-BuildToolsRoot`、`-DependencyRoot`。输出在忽略的 `out/`，构建不写入游戏。重复构建前自行移走或清理 `out/`。

- WeaponAttachments 编译引用：无同仓编译引用。
- FlashlightCustom 编译引用：无同仓编译引用。

配件与手电共用武器挂载过程；改动挂点或数值时一起验证配件、手电、GameData。外部编译依赖须与 `ExternalReferences` 的 SHA256 一致，放在 DependencyRoot 中，不上传游戏或第三方框架目录。

## 维护和发布

`maintenance.lock.json` 记录本次维护输入。修改后审核差异并更新锁文件，再执行工具仓库的 `tools/Verify-Repository.ps1 -RepositoryRoot .`，重建并核验输出身份、版本、依赖。测试输出不提交。

有 `Test.ps1` 时执行 `./Test.ps1 -RuntimeRoot '<.NET6目录>'`；离线测试不等于游戏内实测。冻结 DLL 的改造层在工具仓库中，原始主体没有可重建源码，不能当作全源码项目。

Releases 只打包 `out/package/BepInEx` 和安装说明；不打包源码、测试、个人配置或游戏框架。跨仓版本由 [整合仓库](https://github.com/PreLuna/GTFO-Modpack-Manifest-and-Verification) 的清单登记，发布后填写真实提交号和安装包校验值。

## 提交前检查

各仓目录是后续开发的唯一源码入口，安装 ZIP 与 out/ 均为构建产物。
修改后先审查差异并暂存有意提交的文件，再执行工具仓库的 tools/Update-Lock.ps1 -RepositoryRoot '<仓库>'，暂存更新后的锁文件，最后执行 tools/Check-Commit.ps1 -RepositoryRoot '<仓库>'。
新增文件、中文路径、遗漏暂存、未锁定源码及误入暂存区的输出文件都会被检查。更新锁仅表示确认输入，不等于构建或测试通过；相关模组仍需重建和回归。

首次克隆整套仓库后运行工具仓库 tools/Enable-Hooks.ps1 -CheckoutRoot '<9仓所在目录>'，安装本机提交前检查。Git 不会随克隆自动携带 .git/hooks。此检查可由 Git 的 --no-verify 绕过，不能代替人工审核与发布验收。
