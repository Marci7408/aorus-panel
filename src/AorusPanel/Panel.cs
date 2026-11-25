using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AorusPanel;

internal enum PanelMode : byte
{
    Enthusiast1 = 1, Enthusiast2, Enthusiast3, Image, Text, Gif, Chibi, Carousel = 10
}

internal record PanelState(string Firmware, bool On, PanelMode Mode, byte Metrics, byte Interval);

internal sealed class Panel : IDisposable
{
    private readonly int index;
    private readonly string firmware;
    private bool disposed;

    public Panel()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GIGABYTE", "Control Center", "Lib", "GBT_VGA", "GvDll", "GvDisplayA.dll");
        if (!File.Exists(path))
            throw new IOException("Gigabyte’s VGA library is missing. Install the GCC VGA module to connect.");
        Native.Load(path);

        Check(Native.GvInitDispLib(), "Initialize Gigabyte’s VGA library");
        try
        {
            CardList cards = CardList.Empty();
            Check(Native.GvGetVgaInfo(ref cards), "Enumerate graphics cards");
            index = -1;
            for (int i = 0; i < Math.Min(cards.Count, 10); i++)
            {
                if (cards.Vendor[i] != 0x10de || cards.Device[i] != 0x2484 ||
                    cards.SubVendor[i] != 0x1458 || cards.SubDevice[i] != 0x4069)
                    continue;

                if (index >= 0)
                    throw new IOException("More than one matching AORUS card was found.");
                index = i;
            }

            if (index < 0)
                throw new IOException("The supported AORUS RTX 3070 (1458:4069) was not found.");

            byte version = Read(0xd6)[1];
            if (version == 0)
                throw new IOException("The LCD did not return a firmware version.");
            firmware = $"{version >> 4}.{version & 0x0f}";
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public PanelState Status()
    {
        byte[] state = Read(0xde);
        byte[] metrics = Read(0xdf);
        if (state[1] == 0 || state[2] is not (1 or 2))
            throw new IOException("The LCD returned an invalid power or mode value.");
        return new PanelState(firmware, state[2] == 1, (PanelMode)state[1], metrics[1], metrics[2]);
    }

    public PanelState Power(bool on)
    {
        Command(0xe7, on ? (byte)1 : (byte)2);
        return Save(value => value.On == on);
    }

    public PanelState Display(PanelMode mode, byte? metrics = null, byte interval = 1)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (metrics.HasValue)
        {
            if (mode is < PanelMode.Enthusiast1 or > PanelMode.Enthusiast3 || interval is < 1 or > 10)
                throw new ArgumentOutOfRangeException(nameof(interval));
            var values = new byte[9];
            for (int i = 0; i < 8; i++)
                values[i] = (byte)((metrics.Value >> i) & 1);
            values[8] = interval;
            Command(0xe1, values);
        }

        Command(0xe5, (byte)mode);
        Command(0xe7, 1);
        return Save(value => value.On && value.Mode == mode &&
            (!metrics.HasValue || (value.Metrics == metrics.Value && value.Interval == interval)));
    }

    public PanelState Upload(PanelMedia media, Action<double>? progress)
    {
        PanelState before = Status();
        int count = media.Data.Length / 256 + 1;
        bool large = media.Data.Length >= 20_480;
        uint address = media.Mode switch
        {
            PanelMode.Image => 0x01f25800,
            PanelMode.Text => 0x01f00000,
            PanelMode.Gif => 0,
            _ => throw new InvalidDataException("Invalid upload mode.")
        };
        var header = new byte[14];
        BinaryPrimitives.WriteUInt32BigEndian(header, address);
        header[4] = media.Mode == PanelMode.Gif ? (byte)2 : (byte)1;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(9), media.Mode == PanelMode.Gif ? media.Frames : (ushort)0);
        header[11] = media.Delay;
        header[12] = large ? (byte)2 : (byte)1;

        Command(0xf2, 1);
        try
        {
            Thread.Sleep(500);
            Command(0xf1, header);
            int eraseBlock = large ? 65_536 : 4096;
            int blocks = (media.Data.Length + eraseBlock - 1) / eraseBlock;
            for (int i = 0; i < blocks; i++)
            {
                Thread.Sleep(large ? 2000 : 400);
                progress?.Invoke(0.2 * (i + 1) / blocks);
            }
            Thread.Sleep(1000);

            var block = new byte[256];
            for (int i = 0; i < count; i++)
            {
                Array.Clear(block);
                int offset = i * block.Length;
                Array.Copy(media.Data, offset, block, 0, Math.Min(block.Length, media.Data.Length - offset));
                uint status = 0;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    status = Write(block);
                    if (status == 0)
                        break;
                    Thread.Sleep(2);
                }
                Check(status, $"Upload block {i + 1} of {count}");
                if (i % 20 == 0)
                    progress?.Invoke(0.2 + 0.75 * (i + 1) / count);
            }

            Thread.Sleep(500);
            Command(0xf2, 2);
        }
        catch
        {
            TryCommand(0xf2, 2);
            TryCommand(0xe5, (byte)before.Mode);
            TryCommand(0xe7, before.On ? (byte)1 : (byte)2);
            throw;
        }

        PanelState state = Display(media.Mode);
        progress?.Invoke(1);
        return state;
    }

    private PanelState WaitFor(Func<PanelState, bool> matches)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            Thread.Sleep(120);
            PanelState state = Status();
            if (matches(state))
                return state;
        }

        throw new IOException("The LCD did not confirm the requested change.");
    }

    private void TryCommand(byte operation, params byte[] values)
    {
        try { Command(operation, values); }
        catch (IOException) { }
    }

    private PanelState Save(Func<PanelState, bool> matches)
    {
        WaitFor(matches);
        // A settings commit can make the LCD briefly unavailable for other commands.
        Thread.Sleep(300);
        Command(0xaa);
        Thread.Sleep(1000);
        return WaitFor(matches);
    }

    private void Command(byte operation, params byte[] values)
    {
        byte[] bytes = CommandBytes(operation, values);
        uint status = Write(bytes);
        if (operation is not (0xf1 or 0xf2))
        {
            for (int retry = 0; status != 0 && retry < 5; retry++)
            {
                Thread.Sleep(200);
                status = Write(bytes);
            }
        }
        Check(status, $"LCD command 0x{operation:X2}");
    }

    private byte[] Read(byte operation)
    {
        var packet = Packet.Create(CommandBytes(operation, []));
        var reply = new byte[4];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint status = Native.GvReadI2C(index, ref packet, reply, reply.Length);
            if (status == 0 && reply[0] == operation)
                return reply;
            Thread.Sleep(60);
        }

        throw new IOException($"The LCD did not answer command 0x{operation:X2}.");
    }

    private uint Write(byte[] bytes)
    {
        var packet = Packet.Create(bytes);
        return Native.GvWriteI2C(index, ref packet);
    }

    private static byte[] CommandBytes(byte operation, byte[] values)
    {
        byte[] bytes = new byte[256];
        bytes[0] = operation;
        bytes[1] = 0xcb;
        bytes[2] = 0x55;
        bytes[3] = 0xac;
        bytes[4] = 0x38;
        values.CopyTo(bytes, 5);
        return bytes;
    }

    private static void Check(uint status, string action)
    {
        if (status != 0)
            throw new IOException($"{action} failed (0x{status:X8}).");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Native.GvFreeDispLib();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Packet
    {
        public byte Address;
        public int RegisterSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Data;
        public int DataSize;
        public int Speed;
        public byte Port;

        public static Packet Create(byte[] data) =>
            new() { Address = 0xc2, Data = data, DataSize = 256, Speed = 400, Port = 1 };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CardList
    {
        public int Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public int[] Type;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public int[] Memory;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public ushort[] Vendor;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public ushort[] Device;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public ushort[] SubDevice;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public ushort[] SubVendor;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2310)] public byte[] Details;

        public static CardList Empty() => new()
        {
            Type = new int[10], Memory = new int[10],
            Vendor = new ushort[10], Device = new ushort[10], SubDevice = new ushort[10], SubVendor = new ushort[10],
            Details = new byte[2310]
        };
    }

    private static class Native
    {
        private const string Library = "GvDisplayA.dll";
        private static IntPtr handle;

        static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
            (name, _, _) => name == Library ? handle : IntPtr.Zero);

        public static void Load(string path)
        {
            if (handle != IntPtr.Zero)
                return;

            // Search the installed VGA library's directory for its dependencies.
            handle = LoadLibraryEx(path, IntPtr.Zero, 0x00001100);
            if (handle == IntPtr.Zero)
                throw new IOException($"Could not load Gigabyte’s VGA library (Windows error {Marshal.GetLastWin32Error()}).");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint GvInitDispLib();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint GvFreeDispLib();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint GvGetVgaInfo(ref CardList cards);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint GvReadI2C(int index, ref Packet packet, [Out] byte[] reply, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint GvWriteI2C(int index, ref Packet packet);
    }
}
