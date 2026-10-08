// Tray.cs - notification-area icon and menu.
using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace DesktopGrass.Composition;

internal sealed class Tray : IDisposable
{
    private readonly GrassApp _app;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Icon? _image;

    public Tray(GrassApp app)
    {
        _app = app;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DesktopGrass.Composition.GrassIcon.ico"))
        {
            if (stream != null) _image = new Icon(stream, SystemInformation.SmallIconSize);
        }
        Rebuild();
        _menu.Opening += (_, _) => Rebuild();
        _icon = new NotifyIcon
        {
            Icon = _image ?? SystemIcons.Application,
            Text = "Desktop Grass (Composition POC)",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _app.SetPaused(!_app.Paused);
    }

    private void Rebuild()
    {
        _menu.Items.Clear();

        var scene = new ToolStripMenuItem("Scene");
        foreach (var p in Palette.All)
        {
            var kind = p.Kind;
            scene.DropDownItems.Add(new ToolStripMenuItem(p.Name, null, (_, _) => _app.SetScene(kind)) { Checked = _app.Palette.Kind == kind });
        }
        _menu.Items.Add(scene);

        var motion = new ToolStripMenuItem("Motion");
        motion.DropDownItems.Add(new ToolStripMenuItem("Full", null, (_, _) => _app.SetMotion(MotionMode.Full)) { Checked = _app.Motion == MotionMode.Full });
        motion.DropDownItems.Add(new ToolStripMenuItem("Calm (only reacts to you)", null, (_, _) => _app.SetMotion(MotionMode.Calm)) { Checked = _app.Motion == MotionMode.Calm });
        _menu.Items.Add(motion);

        _menu.Items.Add(new ToolStripMenuItem("Send a gust", null, (_, _) => _app.GustNow()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Pause", null, (_, _) => _app.SetPaused(!_app.Paused)) { Checked = _app.Paused });
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => _app.Quit()));
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image?.Dispose();
    }
}
