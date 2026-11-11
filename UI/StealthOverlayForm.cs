using System.Drawing.Drawing2D;
using ZeroTouchLock.Native;

namespace ZeroTouchLock.UI;

/// <summary>
/// A click-through, transparent layered full-screen overlay that smoothly dims the screen
/// during the Warning state, giving immediate visual feedback before an auto-lock occurs.
/// </summary>
public sealed class StealthOverlayForm : Form
{
    private double _targetOpacity = 0.75;
    private double _currentOpacity = 0.0;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private bool _isFadingIn;

    public StealthOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(10, 14, 23); // Deep night tint
        Opacity = 0.0;
        DoubleBuffered = true;

        // Cover all screens in multi-monitor setups
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Bounds = virtualScreen;

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 25 };
        _fadeTimer.Tick += OnFadeTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // WS_EX_LAYERED | WS_EX_TRANSPARENT makes all clicks pass through to background apps
            // WS_EX_TOOLWINDOW prevents appearing in Alt-Tab
            // WS_EX_NOACTIVATE prevents stealing focus from active windows
            cp.ExStyle |= Win32Api.WS_EX_LAYERED |
                          Win32Api.WS_EX_TRANSPARENT |
                          Win32Api.WS_EX_TOOLWINDOW |
                          Win32Api.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Smoothly fades in the stealth shield overlay.
    /// </summary>
    public void ShowShield(double targetOpacity = 0.75)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowShield(targetOpacity)));
            return;
        }

        _targetOpacity = Math.Clamp(targetOpacity, 0.1, 0.95);
        _isFadingIn = true;

        // Reposition over virtual screen in case monitor topology changed
        Bounds = SystemInformation.VirtualScreen;

        if (!Visible)
        {
            Opacity = 0.0;
            _currentOpacity = 0.0;
            Show();
        }

        _fadeTimer.Start();
    }

    /// <summary>
    /// Smoothly fades out and hides the stealth shield overlay.
    /// </summary>
    public void HideShield()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(HideShield));
            return;
        }

        _isFadingIn = false;
        _fadeTimer.Start();
    }

    private void OnFadeTimerTick(object? sender, EventArgs e)
    {
        const double step = 0.08;

        if (_isFadingIn)
        {
            _currentOpacity = Math.Min(_targetOpacity, _currentOpacity + step);
            Opacity = _currentOpacity;
            if (_currentOpacity >= _targetOpacity)
            {
                _fadeTimer.Stop();
            }
        }
        else
        {
            _currentOpacity = Math.Max(0.0, _currentOpacity - step);
            Opacity = _currentOpacity;
            if (_currentOpacity <= 0.0)
            {
                _fadeTimer.Stop();
                Hide();
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Render subtle center proximity warning badge on primary screen
        Rectangle primary = Screen.PrimaryScreen?.Bounds ?? ClientRectangle;

        int boxWidth = 560;
        int boxHeight = 110;
        int boxX = primary.Left + (primary.Width - boxWidth) / 2;
        int boxY = primary.Top + (primary.Height - boxHeight) / 2;
        var boxRect = new Rectangle(boxX, boxY, boxWidth, boxHeight);

        using var boxBrush = new SolidBrush(Color.FromArgb(200, 20, 26, 40));
        using var borderPen = new Pen(Color.FromArgb(180, 245, 158, 11), 1.5f); // Amber warning border

        using var path = GetRoundedRectanglePath(boxRect, 16);
        g.FillPath(boxBrush, path);
        g.DrawPath(borderPen, path);

        using var titleFont = new Font("Segoe UI", 13f, FontStyle.Bold);
        using var descFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        using var titleBrush = new SolidBrush(Color.FromArgb(254, 243, 199)); // Amber-100
        using var descBrush = new SolidBrush(Color.FromArgb(209, 213, 219));  // Gray-300

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        var titleRect = new Rectangle(boxX, boxY + 18, boxWidth, 30);
        var descRect = new Rectangle(boxX, boxY + 54, boxWidth, 40);

        g.DrawString("PROXIMITY WARNING - AUTO-LOCK PENDING", titleFont, titleBrush, titleRect, sf);
        g.DrawString("BLE signal is weak. Move closer to the workstation to prevent locking.", descFont, descBrush, descRect, sf);
    }

    private static GraphicsPath GetRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
