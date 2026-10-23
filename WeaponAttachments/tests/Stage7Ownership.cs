using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Execution;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;
using PreLuna.WeaponAttachments.Ownership;

internal static class Stage7Ownership
{
    private static int _passed,_failed;
    private static readonly List<object> Results=new();
    private static void Check(bool value){if(!value)throw new Exception("Assertion failed");}
    private static void Run(string name,Action action){try{action();_passed++;Results.Add(new{Name=name,Success=true});}catch(Exception e){_failed++;Results.Add(new{Name=name,Success=false,Error=e.ToString()});}}
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(args[1]);
        for(int host=0;host<4;host++)for(int bot=1;bot<=3;bot++)
        {
            int h=host,b=bot;Run($"主机槽 {h} 人机 {b} 的映射与逆映射",()=>{
                int team=OwnershipPolicy.TeamSlot(b,h);Check(team!=h && OwnershipPolicy.BotSlot(team,h)==b);
                var decision=OwnershipPolicy.Resolve(new(true,h,team,false,true,false));Check(decision.Mode==OwnershipMode.HostBot && decision.Target!.BotSlot==b);
            });
        }
        foreach(int host in Enumerable.Range(0,4))
        {int h=host;Run($"主机 {h} 本人身份",()=>Check(OwnershipPolicy.Resolve(new(true,h,h,true,false,true)).Mode==OwnershipMode.LocalHost));
         Run($"非主机 {h} 只识别本人",()=>Check(OwnershipPolicy.Resolve(new(false,h,h,true,false,true)).Mode==OwnershipMode.LocalClient));}
        Run("远端真人即使复制本地所有权标志也不识别",()=>Check(OwnershipPolicy.Resolve(new(true,0,1,false,false,true)).Target==null));
        Run("非主机不管理其他人机",()=>Check(OwnershipPolicy.Resolve(new(false,0,1,false,true,false)).Target==null));
        Run("BOT 不能占用本机槽",()=>Check(OwnershipPolicy.Resolve(new(true,0,0,false,true,true)).Target==null));
        Run("本地标志与槽位不符拒绝",()=>Check(OwnershipPolicy.Resolve(new(true,0,1,true,false,true)).Target==null));
        Run("本人没有本地所有权拒绝",()=>Check(OwnershipPolicy.Resolve(new(true,0,0,true,false,false)).Target==null));
        Run("销毁中实例拒绝",()=>Check(OwnershipPolicy.Resolve(new(true,0,1,false,true,false,false)).Target==null));
        Run("已脱离队伍的旧 BOT 拒绝",()=>Check(OwnershipPolicy.Resolve(new(true,0,1,false,true,false,true,false)).Target==null));
        Run("未知主机槽拒绝",()=>Check(OwnershipPolicy.Resolve(new(true,-1,1,false,true,false)).Target==null));
        Run("越界对象槽拒绝",()=>Check(OwnershipPolicy.Resolve(new(true,0,4,false,true,false)).Target==null));
        Run("真人接管立即变为忽略",()=>{var bot=new OwnerSnapshot(true,0,1,false,true,false);Check(OwnershipPolicy.Resolve(bot).Target!=null);Check(OwnershipPolicy.Resolve(bot with{OwnerIsBot=false}).Target==null);});
        Run("失去主机立即拒绝 BOT",()=>{var bot=new OwnerSnapshot(true,0,1,false,true,false);Check(OwnershipPolicy.Resolve(bot with{IsHost=false}).Target==null);});
        Run("非法映射不产生存档对象",()=>{Check(OwnershipPolicy.TeamSlot(4,0)==-1 && OwnershipPolicy.TeamSlot(1,4)==-1 && OwnershipPolicy.BotSlot(0,0)==-1);});
        var caps=new[]{"stat.damage","stat.recoil","stat.charge_speed","stat.wake_up_radius","stat.alert_radius","stat.alert_buildup","stat.fire_rate","stat.reload_speed","stat.magazine_capacity","stat.aim_spread","stat.reserve_capacity","stat.ammo_refill","mechanism.armor_pierce","mechanism.ammo_return_to_clip","condition.kills","condition.always","flashlight.original_profile"};
        Run("主机本人保留已实现能力",()=>Check(OwnershipPolicy.Capabilities(OwnershipMode.LocalHost,caps).Count==caps.Length));
        Run("BOT 不开放消音镜头后坐力及本地蓄力",()=>Check(!OwnershipPolicy.Capabilities(OwnershipMode.HostBot,caps).Any(v=>v is "stat.recoil" or "stat.charge_speed" or "stat.wake_up_radius" or "stat.alert_radius" or "stat.alert_buildup")));
        Run("BOT 基础属性弹药及原版手电能力保留",()=>{var v=OwnershipPolicy.Capabilities(OwnershipMode.HostBot,caps);Check(new[]{"stat.damage","stat.fire_rate","stat.reload_speed","stat.magazine_capacity","stat.reserve_capacity","mechanism.ammo_return_to_clip","flashlight.original_profile"}.All(v.Contains));});
        Run("非主机战斗消音与补给能力关闭",()=>{var v=OwnershipPolicy.Capabilities(OwnershipMode.LocalClient,caps);Check(!v.Any(x=>x.StartsWith("mechanism.") || x is "stat.damage" or "condition.kills" or "stat.ammo_refill" or "stat.reserve_capacity" or "stat.wake_up_radius"));});
        Run("非主机保留本机 getter 与显示路径",()=>{var v=OwnershipPolicy.Capabilities(OwnershipMode.LocalClient,caps);Check(new[]{"stat.fire_rate","stat.reload_speed","stat.magazine_capacity","stat.recoil","flashlight.original_profile"}.All(v.Contains));});
        Run("远端不具有执行能力",()=>Check(OwnershipPolicy.Capabilities(OwnershipMode.Ignored,caps).Count==0));
        Run("能力不提升上游未实现项且去重",()=>Check(OwnershipPolicy.Capabilities(OwnershipMode.HostBot,new[]{"stat.damage","stat.damage"}).SequenceEqual(new[]{"stat.damage"})));
        Run("同枪四对象分别计算重启后仍独立",()=>{
            var registry=CapabilityRegistry.CreateDefaults();var weapon=new WeaponIdentity("test.pack",1);
            var parts=Enumerable.Range(1,4).Select(i=>new AttachmentDefinition{Id="test.owner"+i,DisplayName="测试对象"+i,Slot=AttachmentSlot.Muzzle,
                Applicability=new(){Targets=new[]{ConfigurationTargetKind.LocalPlayer,ConfigurationTargetKind.HostBot},WeaponScope=WeaponScope.All},
                Modifiers=new[]{new StatModifierDefinition{Stat="damage",Percent=i*10,Operation=PercentOperation.IncreasePercent}}}).ToArray();
            var catalog=CatalogJson.Read(CatalogJson.Serialize(CatalogDocument.Empty() with{Attachments=parts}),registry).Catalog!;
            string path=Path.Combine(args[1],"owners.json");var storage=new JsonLoadoutStorage(path,catalog.CatalogId);var loadouts=LoadoutManager.Open(catalog,storage);
            int index=0;foreach(var target in ConfigurationTarget.All){var draft=loadouts.BeginEdit(target,weapon);Check(draft.Select(AttachmentSlot.Muzzle,"test.owner"+(++index)).Success && draft.Apply().Success);}
            loadouts=LoadoutManager.Open(catalog,storage);index=0;
            foreach(var target in ConfigurationTarget.All){var binding=new RuntimeBinding(catalog,loadouts,new(catalog,registry),target,weapon,"instance"+index,new[]{"stat.damage"});Check(Math.Abs(binding.Calculate("damage",100,0)-(110+10*index++))<.001);}
            Check(loadouts.SavedLoadoutCount==4);
        });
        File.WriteAllText(args[0],JsonSerializer.Serialize(new{Passed=_passed,Failed=_failed,Success=_failed==0,Results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Stage7 ownership: {_passed}/{_passed+_failed} passed.");return _failed==0?0:1;
    }
}
