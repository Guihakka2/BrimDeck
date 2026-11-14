namespace BrimDeck.Core;

// What GamingBatteries takes from XInput for the controllers on the Xbox 360 wireless receiver (045E:0719), which
// Windows.Gaming.Input gives no battery report for. XInputGetCapabilitiesEx (xinput1_4, ordinal 108) says product 0 with the
// wireless flag for them (SDL_xinputjoystick.c, zlib, names them so); XInputGetBatteryInformation gives a battery type and one
// of four levels (Microsoft's XInput documentation), taken as SDL takes them: 10, 40, 70 and 100 %, coarse.
public static class XInputBattery
{
    public const int Microsoft = 0x045E, Receiver360 = 0x0719;
    public const ushort Wireless = 0x0002;
    public const byte Disconnected = 0x00, Wired = 0x01, Unknown = 0xFF;

    // A slot holding a controller on the Xbox 360 wireless receiver.
    public static bool Receiver360Slot(ushort productId, ushort flags) => productId == 0 && (flags & Wireless) != 0;

    // The reading for a battery type and level: null when the slot holds nothing (no entry), a reading without a level when
    // the type is unknown, wired (the level is only meant for batteries) or the level out of range.
    public static BatteryRead? Battery(byte type, byte level)
    {
        if (type == Disconnected) return null;
        if (type is Wired or Unknown || level > 3) return new(null);
        return new(level switch { 0 => 10, 1 => 40, 2 => 70, _ => 100 }, Coarse: true);
    }
}
