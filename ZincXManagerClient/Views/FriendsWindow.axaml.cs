using System.IO;  // MemoryStream:头像字节转图片
using Avalonia.Controls;  // 窗口与控件
using Avalonia.Input;  // 鼠标键盘输入
using Avalonia.Media.Imaging;  // Bitmap:账号头像
using ZincXManagerShared.Account;  // AvatarStore:头像缓存

namespace ZincXManagerClient.Views;

/// <summary>好友条:单独的小窗体(类似老版 QQ / Steam 的好友条),选好友后再开聊天窗体。</summary>
public partial class FriendsWindow : Window
{
    public FriendsWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowMaterial(this);

        //无边框窗口自己处理边缘拖拽:好友条只允许上下拉长,宽度锁死
        WindowResizeHelper.Attach(this, horizontal: false);

        //登录态在 ClientSession 里,这里只负责把名字与头像显示出来
        SelfNameText.Text = string.IsNullOrWhiteSpace(ClientSession.User) ? "未登录" : ClientSession.User;
        ShowSelfAvatar();
    }

    /// <summary>有账号头像就换成图片,没有就保留首字占位。</summary>
    private void ShowSelfAvatar()
    {
        if (string.IsNullOrWhiteSpace(ClientSession.User))
        {
            return;
        }

        try
        {
            var bytes = AvatarStore.Load(ClientSession.User);
            if (bytes.Length == 0)
            {
                return;
            }

            SelfAvatarImage.Source = new Bitmap(new MemoryStream(bytes));
            SelfAvatarImage.IsVisible = true;
            SelfAvatarInitial.IsVisible = false;
        }
        catch
        {
            //头像读不出来就继续用首字
        }
    }

    /// <summary>双击好友 → 单独打开聊天窗体。</summary>
    private void OnFriendDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Control { Tag: string name } && name.Length > 0)
        {
            var chat = new ChatWindow(name);
            WindowPlacement.PlaceAt(chat, this);
            chat.Show();
        }
    }

    /// <summary>按住标题栏拖动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>点最小化按钮:窗口最小化。</summary>
    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>点右上角关闭按钮:关掉这个窗口。</summary>
    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>点底部「好友通知」打开申请列表。</summary>
    private void OnNotificationsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new FriendRequestsWindow();
        WindowPlacement.PlaceAt(window, this);
        window.Show();
    }

    /// <summary>点标题栏的「+」打开添加好友窗口。</summary>
    private void OnAddFriendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new AddFriendWindow();
        WindowPlacement.PlaceAt(window, this);
        window.Show();
    }

}
