using System;
using System.Drawing;
using System.IO;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TablePet.App.Services;

public sealed record TrayActions(
    Action ShowHide,
    Action Settings,
    Action Pause,
    Action Topmost,
    Action ClickThrough,
    Action Restore,
    Action Exit,
    Action? MenuOpened = null,
    Action? MenuClosed = null,
    Action? DailyMode = null,
    Action? WorkMode = null);

/// <summary>Uses the standard Windows notification area and its native accessible menu.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _ownedIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _visibility;
    private readonly Forms.ToolStripMenuItem _pause;
    private readonly Forms.ToolStripMenuItem _topmost;
    private readonly Forms.ToolStripMenuItem _clickThrough;
    private readonly Forms.ToolStripMenuItem? _dailyMode;
    private readonly Forms.ToolStripMenuItem? _workMode;
    private readonly Dispatcher _dispatcher;
    private readonly TrayActions _actions;
    private bool _disposed;

    public TrayService(TrayActions actions, string? iconPath = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _actions = actions;
        _ownedIcon = LoadIcon(iconPath);
        _menu = new Forms.ContextMenuStrip();
        _visibility = AddItem("隐藏桌宠", actions.ShowHide);
        AddItem("设置…", actions.Settings);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        if (actions.DailyMode is { } daily) _dailyMode = AddItem("日常陪伴", daily);
        if (actions.WorkMode is { } work) _workMode = AddItem("工作模式", work);
        if (_dailyMode is not null || _workMode is not null) _menu.Items.Add(new Forms.ToolStripSeparator());
        _pause = AddItem("暂停自动动作", actions.Pause);
        _topmost = AddItem("始终置顶", actions.Topmost);
        _clickThrough = AddItem("整只猫鼠标穿透", actions.ClickThrough);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem("恢复位置", actions.Restore);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem("退出", actions.Exit);
        _menu.Opened += (_, _) => Invoke(actions.MenuOpened);
        _menu.Closed += (_, _) => Invoke(actions.MenuClosed);
        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "草莓猫 · TablePet",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Invoke(actions.ShowHide);
    }

    public void Update(bool visible, bool paused, bool topmost, bool clickThrough, bool working = false, bool canWork = true)
    {
        if (_disposed) return;
        _visibility.Text = visible ? "隐藏桌宠" : "显示桌宠";
        _pause.Checked = paused;
        _pause.Text = working ? (paused ? "继续工作／休息计时" : "暂停工作／休息计时") : "暂停自动动作";
        if (_dailyMode is not null) _dailyMode.Checked = !working;
        if (_workMode is not null) { _workMode.Checked = working; _workMode.Enabled = canWork; }
        _topmost.Checked = topmost;
        _clickThrough.Checked = clickThrough;
        _icon.Text = "草莓猫 · " + (working ? "工作模式" : "日常陪伴") + (paused ? "（已暂停）" : "") + (!visible ? "（已隐藏）" : "");
    }

    public void ShowMessage(string message)
    {
        if (_disposed) return;
        _icon.ShowBalloonTip(4000, "草莓猫 · TablePet", message, Forms.ToolTipIcon.Info);
    }

    private Forms.ToolStripMenuItem AddItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => Invoke(action);
        _menu.Items.Add(item);
        return item;
    }

    private void Invoke(Action? action)
    {
        if (_disposed || action is null) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => Invoke(action)));
            return;
        }
        try { action(); }
        catch (Exception ex)
        {
            ErrorLogger.Write("托盘操作失败。", ex);
            ShowMessage("操作未能完成，请查看设置或应用日志。");
        }
    }

    private static Icon LoadIcon(string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            try { return new Icon(iconPath); }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                ErrorLogger.Write("托盘图标加载失败，使用系统图标。", ex);
            }
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_menu.Visible) _actions.MenuClosed?.Invoke();
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon.Dispose();
    }
}
