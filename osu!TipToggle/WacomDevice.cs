using System;
using System.Runtime.InteropServices;

namespace osu_TipToggle
{
    /// <summary>
    /// Communicates with Wacom tablets running Shavit's custom firmware via HID Feature Reports.
    /// Controls the firmware-level "Pressure & Buttons" state via HID feature reports.
    /// </summary>
    public static class WacomDevice
    {
        private const int VID_WACOM = 0x056A;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        public static string LastDetectedModel { get; private set; } = "Searching...";

        private static readonly object _syncLock = new();
        private static string? _cachedDevicePath = null;
        private static byte _cachedReportId = 0;
        private static int _cachedReportLength = 0;
        private static int _cachedWriteOffset = 1;

        public static void InvalidateCache()
        {
            lock (_syncLock)
            {
                InvalidateCacheInternal();
            }
        }

        private static void InvalidateCacheInternal()
        {
            _cachedDevicePath = null;
            _cachedReportId = 0;
            _cachedReportLength = 0;
            _cachedWriteOffset = 1;
        }

        #region SetupAPI & HID Native Imports

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

        #endregion

        public static string SetPressureAndButtons(bool enable)
        {
            lock (_syncLock)
            {
                string? cachedPath = _cachedDevicePath;
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    string? fastResult = TrySendReport(cachedPath, _cachedReportId, _cachedReportLength, _cachedWriteOffset, enable);
                    if (fastResult != null)
                    {
                        return fastResult;
                    }

                    InvalidateCacheInternal();
                }

                return EnumerateAndSet(enable);
            }
        }

        private static byte[] BuildFeatureReport(byte reportId, int reportLength, int writeOffset, byte[] readBuf, bool enable)
        {
            byte[] writeBuf = new byte[reportLength];
            writeBuf[0] = reportId;

            writeBuf[writeOffset] = 84;
            writeBuf[writeOffset + 1] = 86;
            writeBuf[writeOffset + 2] = 1;
            writeBuf[writeOffset + 3] = readBuf[4];
            writeBuf[writeOffset + 4] = readBuf[5];
            writeBuf[writeOffset + 5] = (byte)(enable ? 1 : 0);

            bool motionSyncSupported = (readBuf[7] & 4) != 0;
            writeBuf[writeOffset + 6] = motionSyncSupported ? readBuf[8] : (byte)0;
            writeBuf[writeOffset + 7] = 0;

            return writeBuf;
        }

        private static string? TrySendReport(string? devicePath, byte reportId, int reportLength, int writeOffset, bool enable)
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

                byte[] writeBuf = BuildFeatureReport(reportId, reportLength, writeOffset, readBuf, enable);
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
            string? stockDetectedModel = null;

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

                            bool isSupported = TryGetSupportedModel(attr.ProductID, out string knownName, out int defaultOffset);
                            if (isSupported && stockDetectedModel == null)
                            {
                                stockDetectedModel = knownName;
                            }

                            byte[]? readBuf = null;
                            byte reportId = 0;

                            // Probe report 0x24 (CTL-472/672, CTL/CTH-480/680)
                            byte[] testCtl = new byte[32];
                            testCtl[0] = 36;
                            if (HidD_GetFeature(handle, testCtl, testCtl.Length) && testCtl[1] == 84 && testCtl[2] == 86 && testCtl[3] == 1)
                            {
                                readBuf = testCtl;
                                reportId = 36;
                            }
                            else
                            {
                                // Probe report 0x60 (PTK-x70, CTL-4100/6100)
                                byte[] testPtk = new byte[64];
                                testPtk[0] = 96;
                                if (HidD_GetFeature(handle, testPtk, testPtk.Length) && testPtk[1] == 84 && testPtk[2] == 86 && testPtk[3] == 1)
                                {
                                    readBuf = testPtk;
                                    reportId = 96;
                                }
                            }

                            if (readBuf != null)
                            {
                                string modelName = isSupported ? knownName : $"Wacom PID 0x{attr.ProductID:X4}";
                                int writeOffset = isSupported ? defaultOffset : (reportId == 96 ? 1 : 1);

                                LastDetectedModel = $"{modelName} (Report 0x{reportId:X2})";

                                _cachedDevicePath = devicePath;
                                _cachedReportId = reportId;
                                _cachedReportLength = readBuf.Length;
                                _cachedWriteOffset = writeOffset;

                                bool currentExchange = (readBuf[6] != 0);
                                if (currentExchange == enable)
                                {
                                    return enable ? "Active (ON)" : "Disabled (OFF)";
                                }

                                byte[] writeBuf = BuildFeatureReport(reportId, readBuf.Length, writeOffset, readBuf, enable);
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

            if (stockDetectedModel != null)
            {
                LastDetectedModel = $"{stockDetectedModel} (Custom firmware not installed)";
                return "Not available";
            }

            LastDetectedModel = "Not Found";
            return "Tablet not found";
        }

        private static bool TryGetSupportedModel(ushort pid, out string modelName, out int writeOffset)
        {
            switch (pid)
            {
                // CTL-480 / CTH-480 / CTL-680 / CTH-680
                case 0x030E: modelName = "Wacom CTH-480"; writeOffset = 1; return true;
                case 0x0302: modelName = "Wacom CTL-480"; writeOffset = 1; return true;
                case 0x0323: modelName = "Wacom CTH-680"; writeOffset = 1; return true;
                case 0x0303: modelName = "Wacom CTL-680"; writeOffset = 1; return true;

                // CTL-472 / CTL-672
                case 0x037A: modelName = "Wacom CTL-472"; writeOffset = 1; return true;
                case 0x037B: modelName = "Wacom CTL-672"; writeOffset = 1; return true;

                // CTL-490 / CTH-490 / CTL-690 / CTH-690
                case 0x033B: modelName = "Wacom CTL-490"; writeOffset = 1; return true;
                case 0x033C: modelName = "Wacom CTH-490"; writeOffset = 1; return true;
                case 0x033D: modelName = "Wacom CTL-690"; writeOffset = 1; return true;
                case 0x033E: modelName = "Wacom CTH-690"; writeOffset = 1; return true;

                // CTL-4100 / CTL-4100WL / CTL-6100 / CTL-6100WL
                case 0x0374: modelName = "Wacom CTL-4100"; writeOffset = 3; return true;
                case 0x0375: modelName = "Wacom CTL-6100"; writeOffset = 3; return true;
                case 0x0376: modelName = "Wacom CTL-4100WL"; writeOffset = 3; return true;
                case 0x0377: modelName = "Wacom CTL-6100WL"; writeOffset = 3; return true;
                case 0x03C5: modelName = "Wacom CTL-4100WL"; writeOffset = 3; return true;

                // PTK-470 / PTK-670 / PTK-870
                case 0x03F5: modelName = "Wacom PTK-470"; writeOffset = 1; return true;
                case 0x03F7: modelName = "Wacom PTK-670"; writeOffset = 1; return true;
                case 0x03F9: modelName = "Wacom PTK-870"; writeOffset = 1; return true;

                default:
                    modelName = string.Empty;
                    writeOffset = 1;
                    return false;
            }
        }
    }
}