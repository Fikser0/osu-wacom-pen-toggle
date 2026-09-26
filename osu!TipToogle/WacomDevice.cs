using System;
using System.Runtime.InteropServices;

namespace osu_TipToogle
{
    public static class WacomDevice
    {
        private const int VID_WACOM = 0x056A;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        public static string LastDetectedModel { get; private set; } = "Searching...";

        private static string? _cachedDevicePath = null;
        private static byte _cachedReportId = 0;
        private static int _cachedReportLength = 0;

        public static void InvalidateCache()
        {
            _cachedDevicePath = null;
            _cachedReportId = 0;
            _cachedReportLength = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid interfaceClassGuid;
            public int flags;
            public IntPtr reserved;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid HidGuid);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, out int RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(string? lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

        public static string SetPressureAndButtons(bool enable)
        {
            string? cachedPath = _cachedDevicePath;
            if (!string.IsNullOrEmpty(cachedPath))
            {
                string? fastResult = TrySendReport(cachedPath, _cachedReportId, _cachedReportLength, enable);
                if (fastResult != null)
                {
                    return fastResult;
                }

                InvalidateCache();
            }

            return EnumerateAndSet(enable);
        }

        private static string? TrySendReport(string? devicePath, byte reportId, int reportLength, bool enable)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            IntPtr handle = CreateFile(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero
            );

            if (handle == (IntPtr)(-1) || handle == IntPtr.Zero)
                return null;

            try
            {
                byte[] readBuf = new byte[reportLength];
                readBuf[0] = reportId;

                if (!HidD_GetFeature(handle, readBuf, readBuf.Length))
                    return null;

                if (readBuf[1] != 84 || readBuf[2] != 86 || readBuf[3] != 1)
                    return null;

                bool currentExchange = (readBuf[6] != 0);
                if (currentExchange == enable)
                {
                    return enable ? "Active (ON)" : "Disabled (OFF)";
                }

                byte[] writeBuf = new byte[readBuf.Length];
                writeBuf[0] = reportId;
                writeBuf[1] = 84;
                writeBuf[2] = 86;
                writeBuf[3] = 1;
                writeBuf[4] = readBuf[4];
                writeBuf[5] = readBuf[5];
                writeBuf[6] = (byte)(enable ? 1 : 0);

                bool motionSyncSupported = (readBuf[7] & 4) != 0;
                if (motionSyncSupported)
                {
                    writeBuf[7] = readBuf[8];
                }
                writeBuf[8] = 0;

                bool ok = HidD_SetFeature(handle, writeBuf, writeBuf.Length);
                return ok ? (enable ? "Active (ON)" : "Disabled (OFF)") : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string EnumerateAndSet(bool enable)
        {
            HidD_GetHidGuid(out Guid hidGuid);

            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
            if (hDevInfo == (IntPtr)(-1)) return "ERR: SetupDiGetClassDevs failed";

            SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = Marshal.SizeOf(ifData);

            try
            {
                for (uint i = 0; SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, out int reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    IntPtr pDetail = Marshal.AllocHGlobal(reqSize);
                    try
                    {
                        Marshal.WriteInt32(pDetail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, pDetail, reqSize, out reqSize, IntPtr.Zero))
                            continue;

                        string? devicePath = Marshal.PtrToStringUni(new IntPtr(pDetail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(devicePath)) continue;

                        IntPtr handle = CreateFile(
                            devicePath,
                            GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero,
                            OPEN_EXISTING,
                            0,
                            IntPtr.Zero
                        );

                        if (handle == (IntPtr)(-1) || handle == IntPtr.Zero) continue;

                        try
                        {
                            HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                            attr.Size = Marshal.SizeOf(attr);
                            if (!HidD_GetAttributes(handle, ref attr) || attr.VendorID != VID_WACOM)
                                continue;

                            byte[]? readBuf = null;
                            byte reportId = 0;
                            string detectedType = "";

                            byte[] testCtl = new byte[32];
                            testCtl[0] = 36;
                            if (HidD_GetFeature(handle, testCtl, testCtl.Length) && testCtl[1] == 84 && testCtl[2] == 86 && testCtl[3] == 1)
                            {
                                readBuf = testCtl;
                                reportId = 36;
                                detectedType = $"Wacom CTL (PID 0x{attr.ProductID:X4}, Report 0x24)";
                            }
                            else
                            {
                                byte[] testPtk = new byte[64];
                                testPtk[0] = 96;
                                if (HidD_GetFeature(handle, testPtk, testPtk.Length) && testPtk[1] == 84 && testPtk[2] == 86 && testPtk[3] == 1)
                                {
                                    readBuf = testPtk;
                                    reportId = 96;
                                    detectedType = $"Wacom PTK (PID 0x{attr.ProductID:X4}, Report 0x60)";
                                }
                            }

                            if (readBuf != null)
                            {
                                LastDetectedModel = detectedType;

                                _cachedDevicePath = devicePath;
                                _cachedReportId = reportId;
                                _cachedReportLength = readBuf.Length;

                                bool currentExchange = (readBuf[6] != 0);
                                if (currentExchange == enable)
                                {
                                    return enable ? "Active (ON)" : "Disabled (OFF)";
                                }

                                byte[] writeBuf = new byte[readBuf.Length];
                                writeBuf[0] = reportId;
                                writeBuf[1] = 84;
                                writeBuf[2] = 86;
                                writeBuf[3] = 1;
                                writeBuf[4] = readBuf[4];
                                writeBuf[5] = readBuf[5];
                                writeBuf[6] = (byte)(enable ? 1 : 0);

                                bool motionSyncSupported = (readBuf[7] & 4) != 0;
                                if (motionSyncSupported)
                                {
                                    writeBuf[7] = readBuf[8];
                                }
                                writeBuf[8] = 0;

                                bool ok = HidD_SetFeature(handle, writeBuf, writeBuf.Length);
                                return ok ? (enable ? "Active (ON)" : "Disabled (OFF)") : "ERR: SetFeature failed";
                            }
                        }
                        finally
                        {
                            CloseHandle(handle);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pDetail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            LastDetectedModel = "Not Found";
            return "Tablet not found";
        }
    }
}