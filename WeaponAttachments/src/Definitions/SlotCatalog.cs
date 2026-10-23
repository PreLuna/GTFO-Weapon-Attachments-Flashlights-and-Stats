using System;
using System.Collections.Generic;
using System.Linq;

namespace PreLuna.WeaponAttachments.Definitions;

public sealed record SlotDescriptor(AttachmentSlot Slot, string Key, string DisplayName, bool Reserved);

public static class SlotCatalog
{
    public static IReadOnlyList<SlotDescriptor> All { get; } = Array.AsReadOnly(new[]
    {
        new SlotDescriptor(AttachmentSlot.Muzzle, "muzzle", "枪口", false),
        new SlotDescriptor(AttachmentSlot.Barrel, "barrel", "枪管", false),
        new SlotDescriptor(AttachmentSlot.Handguard, "handguard", "护木", false),
        new SlotDescriptor(AttachmentSlot.FrontGrip, "front_grip", "前握把", false),
        new SlotDescriptor(AttachmentSlot.RearGrip, "rear_grip", "后握把", false),
        new SlotDescriptor(AttachmentSlot.Stock, "stock", "枪托", false),
        new SlotDescriptor(AttachmentSlot.Magazine, "magazine", "弹匣", false),
        new SlotDescriptor(AttachmentSlot.Ammunition, "ammunition", "子弹种类", false),
        new SlotDescriptor(AttachmentSlot.Perk, "perk", "武器特长", false),
        new SlotDescriptor(AttachmentSlot.Optic, "optic", "瞄准镜", true),
        new SlotDescriptor(AttachmentSlot.Flashlight, "flashlight", "手电", false)
    });

    public static SlotDescriptor? Find(AttachmentSlot slot) => All.FirstOrDefault(item => item.Slot == slot);
    public static string OriginalId(AttachmentSlot slot) => "original." +
        (Find(slot) ?? throw new ArgumentOutOfRangeException(nameof(slot))).Key;
}
