using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Messaging;

namespace RemoteDesktop.Service.Services;

/// <summary>
/// Handles basic mouse/keyboard injection requests coming from the operator. The current implementation targets
/// Windows hosts and uses SendInput; non-Windows platforms log and drop the events to keep the prototype portable.
/// </summary>
public sealed class InputService
{
    private readonly MonitorService _monitorService;
    private readonly InputDesktopSwitcher _desktopSwitcher;
    private readonly ILogger<InputService> _logger;

    public InputService(MonitorService monitorService, InputDesktopSwitcher desktopSwitcher, ILogger<InputService> logger)
    {
        _monitorService = monitorService;
        _desktopSwitcher = desktopSwitcher;
        _logger = logger;
    }

    public Task HandleAsync(InputMessage message, string activeMonitorId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogDebug("Input injection skipped: non-Windows platform");
            return Task.CompletedTask;
        }

        using var desktopScope = _desktopSwitcher.TryEnterInputDesktop();

        var bounds = _monitorService.GetBounds(activeMonitorId);
        if (bounds is null)
        {
            _logger.LogWarning("Cannot handle input: monitor {MonitorId} bounds unavailable", activeMonitorId);
            return Task.CompletedTask;
        }

        var descriptor = _monitorService.GetDescriptor(activeMonitorId);
        var scale = descriptor?.DpiScale ?? 1.0;

        if (message.Mouse is MousePayload mouse)
        {
            HandleMouse(mouse, bounds.Value, scale);
        }

        if (message.Keyboard is KeyboardPayload keyboard)
        {
            HandleKeyboard(keyboard);
        }

        if (message.Special is SpecialPayload special)
        {
            HandleSpecial(special);
        }

        return Task.CompletedTask;
    }

    private void HandleMouse(MousePayload mouse, System.Drawing.Rectangle bounds, double scale)
    {
        if (mouse.X.HasValue && mouse.Y.HasValue)
        {
            var scaledWidth = bounds.Width * scale;
            var scaledHeight = bounds.Height * scale;
            var scaledLeft = bounds.Left * scale;
            var scaledTop = bounds.Top * scale;

            var x = ClampToRange(mouse.X.Value) * scaledWidth + scaledLeft;
            var y = ClampToRange(mouse.Y.Value) * scaledHeight + scaledTop;
            SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
        }

        if (mouse.Buttons is MouseButtons buttons)
        {
            SendButton(MouseButton.Left, buttons.Left);
            SendButton(MouseButton.Right, buttons.Right);
            SendButton(MouseButton.Middle, buttons.Middle);
            if (buttons.X1.HasValue)
            {
                SendXButton(1, buttons.X1.Value);
            }
            if (buttons.X2.HasValue)
            {
                SendXButton(2, buttons.X2.Value);
            }
        }

        if (mouse.Wheel.HasValue)
        {
            var delta = (int)Math.Round(mouse.Wheel.Value * 120); // WHEEL_DELTA multiplier
            SendMouseEvent(MouseEventFlags.WHEEL, mouseData: delta);
        }

        if (mouse.HWheel.HasValue)
        {
            var hDelta = (int)Math.Round(mouse.HWheel.Value * 120);
            SendMouseEvent(MouseEventFlags.HWHEEL, mouseData: hDelta);
        }
    }

    private static void HandleKeyboard(KeyboardPayload keyboard)
    {
        uint flags = KEYEVENTF_SCANCODE;
        if (keyboard.IsExtended)
            flags |= KEYEVENTF_EXTENDEDKEY;
        if (!keyboard.IsKeyDown)
            flags |= KEYEVENTF_KEYUP;

        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki = new KEYBDINPUT
        {
            wScan = (ushort)keyboard.ScanCode,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

private void HandleSpecial(SpecialPayload special)
{
    if (!string.Equals(special.Action, "ctrl_alt_del", StringComparison.OrdinalIgnoreCase))
    {
        _logger.LogDebug("Unknown special input action {Action}", special.Action);
        return;
    }

    try
    {
        // Пытаемся отправить Ctrl+Alt+Del
        if (!SendSAS(asUser: false))
        {
            _logger.LogWarning("Failed to send secure attention sequence (Ctrl+Alt+Del). LastError={LastError}", Marshal.GetLastWin32Error());
            return;
        }
        _logger.LogInformation("Sent secure attention sequence (Ctrl+Alt+Del)");
    }
    catch (DllNotFoundException)
    {
        // Если файла sas.dll нет, ловим ошибку здесь и не даем программе упасть
        _logger.LogWarning("sas.dll not found. Ctrl+Alt+Del is not supported on this system.");
    }
    catch (Exception ex)
    {
        // Ловим любые другие неожиданные ошибки
        _logger.LogWarning(ex, "Failed to send SAS due to an unexpected error.");
    }
}

    private static void SendButton(MouseButton button, bool? state)
    {
        if (state is null)
        {
            return;
        }

        var flags = button switch
        {
            MouseButton.Left => state.Value ? MouseEventFlags.LEFTDOWN : MouseEventFlags.LEFTUP,
            MouseButton.Right => state.Value ? MouseEventFlags.RIGHTDOWN : MouseEventFlags.RIGHTUP,
            MouseButton.Middle => state.Value ? MouseEventFlags.MIDDLEDOWN : MouseEventFlags.MIDDLEUP,
            _ => MouseEventFlags.ABSOLUTE
        };

        SendMouseEvent(flags);
    }

    private static void SendXButton(int buttonNumber, bool isDown)
    {
        int mouseData = buttonNumber == 1 ? XBUTTON1 : XBUTTON2;
        var flags = isDown ? MouseEventFlags.XDOWN : MouseEventFlags.XUP;
        SendMouseEvent(flags, mouseData: mouseData);
    }


    private static void SendMouseEvent(MouseEventFlags flags, int mouseData = 0)
    {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].U.mi = new MOUSEINPUT
        {
            dx = 0,
            dy = 0,
            mouseData = mouseData,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static double ClampToRange(double value) => Math.Min(1.0, Math.Max(0.0, value));

    private enum MouseButton
    {
        Left,
        Right,
        Middle
    }

    #region Win32

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int XBUTTON1 = 0x0001;
    private const int XBUTTON2 = 0x0002;

    [Flags]
    private enum MouseEventFlags : uint
    {
        MOVE = 0x0001,
        LEFTDOWN = 0x0002,
        LEFTUP = 0x0004,
        RIGHTDOWN = 0x0008,
        RIGHTUP = 0x0010,
        MIDDLEDOWN = 0x0020,
        MIDDLEUP = 0x0040,
        XDOWN = 0x0080,
        XUP = 0x0100,
        ABSOLUTE = 0x8000,
        WHEEL = 0x0800,
        HWHEEL = 0x1000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public MouseEventFlags dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("sas.dll", SetLastError = true)]
    private static extern bool SendSAS(bool asUser);

    #endregion
}
