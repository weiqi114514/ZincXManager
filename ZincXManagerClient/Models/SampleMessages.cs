using System.Collections.Generic;

namespace ZincXManagerClient.Models;

/// <summary>
/// 设计稿里的示例消息,仅用于界面预览(Debug 下由 MainViewModel.LoadSampleMessages 加载)。
/// 真实消息由服务端下发,界面不写死内容。
/// </summary>
public static class SampleMessages
{
    public static IReadOnlyList<MessageItem> All { get; } = new List<MessageItem>
    {
        new()
        {
            Kind = MessageKind.Update,
            Title = "ZincXManager更新 | 1.0.0 > 1.0.1",
            Body = "加入XXX功能\n修复了已知BUG",
            Source = "源:ZXMOS",
            PrimaryAction = "立刻下载并更新"
        },
        new()
        {
            Kind = MessageKind.Choice,
            Title = "是否添加猫娘",
            Body = "猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘猫娘",
            Source = "源:ZXMS-锌能源",
            PrimaryAction = "是",
            SecondaryAction = "否"
        },
        new()
        {
            Kind = MessageKind.Input,
            Title = "Where are you from",
            Body = "请填写你所在的省份",
            Source = "源:ZXMS-锌能源",
            PrimaryAction = "确认"
        },
        new()
        {
            Kind = MessageKind.Select,
            Title = "版本选择",
            Body = "请选择你游玩的版本",
            Source = "源:ZXMS-锌能源",
            Options = new[] { "1.21.1", "1.21", "1.20.6", "1.20.1", "1.19.4" },
            SelectedOption = "1.21.1"
        }
    };
}
