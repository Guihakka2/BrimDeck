using System.Runtime.InteropServices;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Gaming.Input;

namespace BrimDeck.Native;

// Game controllers through Windows.Gaming.Input: Xbox controllers on the Xbox Wireless adapter or a cable, and others Windows
// gives a battery report for (RawGameController.TryGetBatteryReport: remaining and full capacity, and whether it charges).
// Only while the page or the collapsed reading shows (DeviceDemand.Watch): the reports carry no event, so they are read when
// a controller arrives and every 15 seconds (DevicePolling.Ask). A controller that is also a connected Bluetooth device merges with that entry by
// its vendor and product (DeviceRoster). A Flydigi pad's placeholder report is left out; the Xbox 360 wireless receiver's
// controllers, which give no report there, are read through XInput.
internal sealed class GamingBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private DispatcherTimer? _poll;
    private bool _running;
    // Read at once when it starts; listening only, it reads nothing.
    public bool Listed => true;

    public void Start(DeviceDemand demand)
    {
        if (demand != DeviceDemand.Watch) { Stop(); return; }
        if (_running) return;
        _running = true;
        try
        {
            RawGameController.RawGameControllerAdded += Changed;
            RawGameController.RawGameControllerRemoved += Changed;
        }
        catch { }
        _poll ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = DevicePolling.Ask };
        _poll.Tick -= Tick; _poll.Tick += Tick; _poll.Start();
        Read();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try
        {
            RawGameController.RawGameControllerAdded -= Changed;
            RawGameController.RawGameControllerRemoved -= Changed;
        }
        catch { }
        _poll?.Stop();
        report(DeviceSource.Gaming, []);
    }

    public void Refresh() { if (_running) Read(); }
    public void ReadNow() => Refresh();
    private void Tick(object? sender, EventArgs e) => Read();
    private void Changed(object? sender, RawGameController controller) => dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (_running) Read(); });

    private void Read()
    {
        var devices = new List<DeviceBattery>();
        bool any = false;
        try
        {
            foreach (var controller in RawGameController.RawGameControllers)
            {
                any = true;
                if (!controller.IsWireless) continue;
                var battery = controller.TryGetBatteryReport();
                if (battery is null || battery.Status == Windows.System.Power.BatteryStatus.NotPresent) continue;
                // A Flydigi pad's report is a constant placeholder (Flydigi.WindowsPlaceholder); its own read gives its level.
                if (Flydigi.WindowsPlaceholder(controller.HardwareVendorId, controller.HardwareProductId, controller.DisplayName,
                    battery.RemainingCapacityInMilliwattHours, battery.FullChargeCapacityInMilliwattHours)) continue;
                int? percent = battery.RemainingCapacityInMilliwattHours is { } remaining && battery.FullChargeCapacityInMilliwattHours is { } full && full > 0
                    ? Devices.Clamp((int)Math.Round(remaining * 100.0 / full)) : null;
                string hardware = Devices.Hardware(controller.HardwareVendorId, controller.HardwareProductId);
                string name = controller.DisplayName is { Length: > 0 } display ? display : Loc.T("游戏手柄", "Game controller");
                devices.Add(new DeviceBattery("gip:" + (controller.NonRoamableId ?? hardware), name, DeviceKind.Controller, DeviceSource.Gaming, percent)
                {
                    Charging = battery.Status == Windows.System.Power.BatteryStatus.Charging, Hardware = hardware
                });
            }
        }
        catch { }
        // The Xbox 360 wireless receiver's controllers, through XInput (XInputBattery): asked only while some controller is
        // there, and not when Windows.Gaming.Input already gave the receiver's battery.
        string receiver = Devices.Hardware(XInputBattery.Microsoft, XInputBattery.Receiver360);
        if (any && !devices.Any(device => device.Hardware == receiver))
            try { devices.AddRange(XInput.Receiver360(receiver)); } catch { }
        report(DeviceSource.Gaming, devices);
    }

    // xinput1_4 (Windows 8 and later), loaded once: XInputGetCapabilitiesEx by its ordinal (108), as SDL loads it, and
    // XInputGetBatteryInformation by name. Each call answers at once for an empty slot.
    private static class XInput
    {
        private const uint Success = 0;
        private const byte GamepadBattery = 0x00;

        [StructLayout(LayoutKind.Sequential)]
        private struct CapabilitiesEx
        {
            public byte Type, SubType;
            public ushort Flags;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
            public ushort LeftMotor, RightMotor;
            public ushort VendorId, ProductId, ProductVersion, Unknown1;
            public uint Unknown2;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BatteryInformation { public byte Type, Level; }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetCapabilitiesEx(uint reserved, uint user, uint flags, out CapabilitiesEx capabilities);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetBatteryInformation(uint user, byte type, out BatteryInformation information);
        [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

        private static readonly Lazy<(GetCapabilitiesEx Capabilities, GetBatteryInformation Battery)?> Api = new(() =>
        {
            if (!NativeLibrary.TryLoad("xinput1_4.dll", typeof(XInput).Assembly, DllImportSearchPath.System32, out var library)) return null;
            IntPtr capabilities = GetProcAddress(library, 108);
            if (capabilities == IntPtr.Zero || !NativeLibrary.TryGetExport(library, "XInputGetBatteryInformation", out var battery)) return null;
            return (Marshal.GetDelegateForFunctionPointer<GetCapabilitiesEx>(capabilities), Marshal.GetDelegateForFunctionPointer<GetBatteryInformation>(battery));
        });

        public static IEnumerable<DeviceBattery> Receiver360(string hardware)
        {
            if (Api.Value is not { } api) yield break;
            for (uint slot = 0; slot < 4; slot++)
            {
                if (api.Capabilities(1, slot, 0, out var capabilities) != Success || !XInputBattery.Receiver360Slot(capabilities.ProductId, capabilities.Flags)) continue;
                if (api.Battery(slot, GamepadBattery, out var information) != Success || XInputBattery.Battery(information.Type, information.Level) is not { } read) continue;
                yield return new DeviceBattery($"xinput:{hardware}:{slot}", Loc.T("Xbox 360 无线手柄", "Xbox 360 Wireless Controller"), DeviceKind.Controller,
                    DeviceSource.Gaming, read.Percent) { Coarse = read.Coarse, Hardware = hardware };
            }
        }
    }

    public void Dispose() => Stop();
}
