using System.Buffers.Binary;

namespace MiRemoteControl.Plugin.XiaomiRemote;

/// <summary>
/// Decodes the keyboard-usage array emitted by Xiaomi RC001/RC003 remotes.
/// Windows' HID keyboard mapper drops a few valid usages (notably Back and
/// Volume), so the same decoder is shared by Raw Input and the HID tap.
/// </summary>
public static class XiaomiRemoteHidReportParser
{
    public const byte ReportId = 1;

    private static readonly IReadOnlyDictionary<ushort, string> Buttons =
        new Dictionary<ushort, string>
        {
            [0x66] = "Power",
            [0x52] = "Up",
            [0x50] = "Left",
            [0x28] = "Ok",
            [0x4F] = "Right",
            [0x51] = "Down",
            [0xF1] = "Back",
            [0x80] = "VolumeUp",
            [0x4A] = "Home",
            [0x81] = "VolumeDown",
            [0x65] = "Menu",
            [0x35] = "Tv"
        };

    public static bool TryParse(ReadOnlySpan<byte> report, out IReadOnlyList<ushort> usages)
    {
        usages = [];
        // A native HID callback normally returns 6 payload bytes. A Windows
        // filter can return the complete input buffer (report ID + zero-padded
        // maximum report length), so accept both without trimming useful data.
        if (report.Length > 1 && report[0] == ReportId && (report.Length - 1) % 2 == 0)
            report = report[1..];
        if (report.IsEmpty || report.Length % 2 != 0) return false;

        var parsed = new List<ushort>(report.Length / 2);
        for (var offset = 0; offset < report.Length; offset += 2)
        {
            var usage = BinaryPrimitives.ReadUInt16LittleEndian(report[offset..]);
            if (usage != 0 && !parsed.Contains(usage)) parsed.Add(usage);
        }

        usages = parsed;
        return true;
    }

    public static bool TryGetButton(ushort usage, out string button) =>
        Buttons.TryGetValue(usage, out button!);
}
